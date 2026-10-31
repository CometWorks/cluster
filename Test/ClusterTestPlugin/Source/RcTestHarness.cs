using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.Common.ObjectBuilders;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.Game.Entity.UseObject;
using VRage.ObjectBuilders.Private;
using VRageMath;

using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// WA-6 test seam for remote control arbitration. The lane needs a powered, antenna-carrying grid with a
/// remote control block in its own clientless partition on this node, a relay antenna within suit radio range
/// of the player (vanilla validates the take with a MUTUAL antenna connection, and a 200 m suit broadcast
/// alone never reaches the distant station), plus a way to issue and observe use requests. The use request
/// goes through the same server entry point a client's RPC lands in (<see cref="MyRemoteControl.RequestUse"/>
/// raises RequestUseMessage), so nothing bypasses the gate under test.
/// </summary>
public static class RcTestHarness
{
	private const string HostGridName = "cluster-rc-host";
	private const string RelayGridName = "cluster-rc-relay";
	public static object Sibling(long gridId, long owner)
	{
		var parent = (MyCubeGrid)MyEntities.GetEntityById(gridId);
		var grid = SpawnStation("offline-conflict-sibling", parent.PositionComp.GetPosition() + Vector3D.Right * 30, owner, true);
		return new { grid = grid.EntityId };
	}

	public static object RateFixtures(long ownerId, int count = 20)
	{
		if (count is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(count));
		var player = PositionRegistryService.Players.FirstOrDefault(p => p.ClientId != 0 && p.IdentityId == ownerId)
			?? PositionRegistryService.Players.First(p => p.ClientId != 0);
		Vector3D origin = new(player.X, player.Y, player.Z);
		return Enumerable.Range(0, count).Select(i =>
		{
			Vector3D position = origin + Vector3D.Right * ((i % 5 - 2) * 8000) + Vector3D.Up * (18000 + i / 5 * 8000);
			var grid = SpawnStation("offline-rate-" + i, position, ownerId, true);
			return new { grid = grid.EntityId, block = grid.GetFatBlocks<MyRemoteControl>().Single().EntityId };
		}).ToArray();
	}

	/// <summary>
	/// Ensures this node hosts a dedicated remote-control station 20 km out plus a relay antenna 150 m from
	/// the anchoring player, and returns their ids. The station placement follows the IGC host grid scheme -
	/// radially out from the nearest planet, outside terrain and inside the 50 km antenna radius - but on the
	/// opposite lateral ray so the two stations never collide. The isolated position forms a fresh clientless
	/// partition owned by this node, which is exactly what the handover freeze under test needs; the relay
	/// joins the player's own partition and completes the suit -> relay -> station chain in both directions.
	/// </summary>
	public static object EnsureRemoteControlGrid(long ownerId, bool dynamic = false)
	{
		if (ownerId == 0) throw new ArgumentOutOfRangeException(nameof(ownerId),
			"A remote control owned by identity 0 fails the ownership validation of every use request");
		ClusterPlayerPosition anyPlayer = PositionRegistryService.Players.FirstOrDefault(player => player.ClientId != 0 && player.IdentityId == ownerId)
			?? PositionRegistryService.Players.FirstOrDefault(player => player.ClientId != 0)
			?? throw new InvalidOperationException("No cluster player position to anchor the remote control grid");
		Vector3D reference = new(anyPlayer.X, anyPlayer.Y, anyPlayer.Z);
		MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(reference);
		Vector3D up = planet == null ? Vector3D.Up
			: Vector3D.Normalize(reference - planet.PositionComp.GetPosition());
		string nodeId = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
		string digits = new(nodeId.Where(char.IsDigit).ToArray());
		int lane = digits.Length > 0 ? int.Parse(digits) % 7 : Math.Abs(StringComparer.Ordinal.GetHashCode(nodeId)) % 7;
		// The opposite lateral direction from the IGC host grid, so both stations can coexist in one world.
		Vector3D lateral = -Vector3D.CalculatePerpendicularVector(up) * (2000.0 + lane * 1000.0);
		MyCubeGrid relay = MyEntities.GetEntities().OfType<MyCubeGrid>()
			.FirstOrDefault(grid => grid.DisplayName == RelayGridName)
			?? SpawnStation(RelayGridName, reference + up * 100.0 - lateral / lateral.Length() * 120.0, ownerId,
				withRemoteControl: false);
		MyCubeGrid host = MyEntities.GetEntities().OfType<MyCubeGrid>()
			.FirstOrDefault(grid => grid.DisplayName == HostGridName)
			?? SpawnStation(HostGridName, reference + up * 20000.0 + lateral, ownerId, withRemoteControl: true, dynamic: dynamic);
		return Describe(host, relay);
	}

	/// <summary>Issues a use request exactly where a client RPC lands: the server-side RequestUseMessage event.</summary>
	public static object Use(long blockId, long usedById, ulong client = 0)
	{
		if (usedById == 0 && client != 0)
		{
			MyPlayer player = MySession.Static.Players.GetOnlinePlayers()
				.SingleOrDefault(candidate => candidate.Id.SteamId == client)
				?? throw new KeyNotFoundException("Online player " + client);
			usedById = player.Character?.EntityId
				?? throw new InvalidOperationException("Player " + client + " has no character");
		}
		MyRemoteControl remoteControl = FindBlock(blockId);
		if (!MyEntities.TryGetEntityById(usedById, out MyEntity user) || user is not IMyControllableEntity controllable)
			throw new KeyNotFoundException("No controllable entity " + usedById);
		remoteControl.RequestUse(UseActionEnum.Manipulate, controllable);
		return new { block = blockId, usedBy = usedById, requested = true };
	}

	/// <summary>
	/// Releases whoever controls the block. Runs server-side because every vanilla client-side release
	/// raiser is Sync.IsServer-guarded; the server's ForceReleaseControl raises the broadcast release.
	/// </summary>
	public static object Release(long blockId)
	{
		MyRemoteControl remoteControl = FindBlock(blockId);
		bool wasControlled = remoteControl.PreviousControlledEntity != null;
		if (wasControlled) remoteControl.ForceReleaseControl();
		return new { block = blockId, released = wasControlled };
	}

	/// <summary>Reports whether anybody controls the remote control block right now.</summary>
	public static object Status(long blockId)
	{
		MyRemoteControl remoteControl = FindBlock(blockId);
		return new
		{
			block = blockId,
			grid = remoteControl.CubeGrid.EntityId,
			working = remoteControl.IsWorking,
			controlled = remoteControl.PreviousControlledEntity != null,
			controllingIdentity = remoteControl.ControllerInfo?.ControllingIdentityId ?? 0,
			position = new[] { remoteControl.CubeGrid.PositionComp.GetPosition().X, remoteControl.CubeGrid.PositionComp.GetPosition().Y, remoteControl.CubeGrid.PositionComp.GetPosition().Z },
			speed = remoteControl.CubeGrid.Physics?.LinearVelocity.Length() ?? 0,
			isStatic = remoteControl.CubeGrid.IsStatic,
		};
	}

	internal static MyCubeGrid SpawnStation(string name, Vector3D position, long ownerId, bool withRemoteControl, bool dynamic = false)
	{
		var antenna = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_RadioAntenna>("LargeBlockRadioAntenna");
		antenna.Min = new SerializableVector3I(0, 0, 0);
		antenna.Owner = ownerId;
		antenna.ShareMode = MyOwnershipShareModeEnum.All;
		antenna.Enabled = true;
		antenna.BroadcastRadius = 50000f;
		antenna.EnableBroadcasting = true;
		antenna.CustomName = name + "-antenna";
		var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
		battery.Min = new SerializableVector3I(1, 0, 0);
		battery.Owner = ownerId;
		battery.ShareMode = MyOwnershipShareModeEnum.All;
		battery.Enabled = true;
		battery.CurrentStoredPower = 3f;
		var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
		builder.GridSizeEnum = MyCubeSize.Large;
		builder.IsStatic = !dynamic;
		builder.CreatePhysics = true;
		// Without InScene the entity is only registered, never added to the scene: no update scheduling, no
		// resource distributor ticks, and the blocks sit functional-but-unpowered forever.
		builder.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
		builder.PositionAndOrientation = new MyPositionAndOrientation(position, Vector3D.Forward, Vector3D.Up);
		builder.DisplayName = name;
		builder.CubeBlocks.Add(antenna);
		builder.CubeBlocks.Add(battery);
		if (withRemoteControl)
		{
			var remoteControl = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_RemoteControl>("LargeBlockRemoteControl");
			remoteControl.Min = new SerializableVector3I(2, 0, 0);
			remoteControl.Owner = ownerId;
			remoteControl.ShareMode = MyOwnershipShareModeEnum.All;
			remoteControl.CustomName = name + "-target";
			builder.CubeBlocks.Add(remoteControl);
		}
		if (dynamic)
		{
			var thrust = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Thrust>("LargeBlockSmallThrust");
			thrust.Min = new SerializableVector3I(3, 0, 0);
			thrust.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Backward, Base6Directions.Direction.Up);
			thrust.Owner = ownerId;
			thrust.Enabled = true;
			builder.CubeBlocks.Add(thrust);
			var gyro = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Gyro>("LargeBlockGyro");
			gyro.Min = new SerializableVector3I(2, 1, 0);
			gyro.Owner = ownerId;
			builder.CubeBlocks.Add(gyro);
		}
		MyEntity spawned = MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false)
			?? throw new InvalidOperationException("Could not spawn " + name);
		return (MyCubeGrid)spawned;
	}

	private static MyRemoteControl FindBlock(long blockId)
	{
		if (!MyEntities.TryGetEntityById(blockId, out MyEntity entity) || entity is not MyRemoteControl remoteControl)
			throw new KeyNotFoundException("No remote control block " + blockId);
		return remoteControl;
	}

	private static object Describe(MyCubeGrid host, MyCubeGrid relay)
	{
		MyRemoteControl remoteControl = host.GetFatBlocks<MyRemoteControl>().FirstOrDefault()
			?? throw new InvalidOperationException("The remote control host grid carries no remote control block");
		Vector3D position = host.PositionComp.GetPosition();
		return new
		{
			grid = host.EntityId,
			block = remoteControl.EntityId,
			relay = relay.EntityId,
			position = new[] { position.X, position.Y, position.Z },
		};
	}
}
