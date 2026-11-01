using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.IntergridCommunication;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;
using Sandbox.Common.ObjectBuilders;
using VRageMath;
using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// WA-4 test seam. The provided world carries no programmable blocks, and the integration lane cannot type a
/// script into one, so the lane needs to place an IGC endpoint on a grid and drive the vanilla
/// <c>IMyIntergridCommunicationSystem</c> API the way a running script would. Everything here goes through the
/// same public API a script uses - nothing bypasses the send path under test.
/// </summary>
public static class IgcTestHarness
{
	private const string HostGridName = "cluster-igc-host";

	private static readonly object Sync = new object();
	private static readonly Dictionary<long, List<IMyBroadcastListener>> Listeners = new();

	/// <summary>
	/// Ensures this node hosts its own dedicated antenna station and returns it. The lane's IGC leg needs an
	/// antenna-carrying grid on <em>each</em> regular node; grids that used to satisfy that were incidental -
	/// respawn pods and dispatched spawns that not every run leaves behind on both nodes - which made the leg
	/// fail whenever one node came up bare. Always provisioning the same station makes the leg's endpoints
	/// identical run to run and keeps this spawn path exercised instead of only firing on bare nodes.
	/// </summary>
	public static object EnsureAntennaGrid(long ownerId)
	{
		string nodeId = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
		string digits = new(nodeId.Where(char.IsDigit).ToArray());
		int lane = digits.Length > 0 ? int.Parse(digits) % 7 : Math.Abs(StringComparer.Ordinal.GetHashCode(nodeId)) % 7;
		return EnsureNamedAntennaGrid(ownerId, HostGridName, 2000.0 + lane * 1000.0, 50000f);
	}

	/// <summary>
	/// Spawns a named antenna station at a chosen lateral offset and broadcast radius. The relay-chain leg builds
	/// its geometry from these: endpoints whose radii cannot span the distance directly, bridged only by an
	/// intermediate relay grid. Every node computes the same lateral basis from the same merged player anchor, so
	/// offsets chosen on different nodes are distances on one shared line.
	/// </summary>
	public static object EnsureNamedAntennaGrid(long ownerId, string name, double lateralMeters, float radiusMeters)
	{
		if (ownerId == 0) throw new ArgumentOutOfRangeException(nameof(ownerId),
			"An antenna owned by identity 0 reaches no remote broadcaster, which defeats the grid's purpose");
		if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new ArgumentException("Invalid grid name");
		if (radiusMeters < 100f || radiusMeters > 50000f) throw new ArgumentOutOfRangeException(nameof(radiusMeters));
		MyCubeGrid ready = MyEntities.GetEntities().OfType<MyCubeGrid>()
			.FirstOrDefault(grid => grid.DisplayName == name);
		if (ready != null) return new { grid = ready.EntityId, created = false };
		// Position: 20 km radially out from the nearest planet above the cluster's players - outside terrain and
		// gravity - plus the lateral offset so grids never occupy the same spot. An isolated position forms a
		// fresh partition owned by this node.
		ClusterPlayerPosition anyPlayer = PositionRegistryService.Players.FirstOrDefault(player => player.ClientId != 0)
			?? throw new InvalidOperationException("No cluster player position to anchor the antenna grid");
		Vector3D reference = new(anyPlayer.X, anyPlayer.Y, anyPlayer.Z);
		MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(reference);
		Vector3D up = planet == null ? Vector3D.Up
			: Vector3D.Normalize(reference - planet.PositionComp.GetPosition());
		Vector3D lateral = Vector3D.CalculatePerpendicularVector(up) * lateralMeters;
		Vector3D position = reference + up * 20000.0 + lateral;
		var antenna = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_RadioAntenna>("LargeBlockRadioAntenna");
		antenna.Min = new SerializableVector3I(0, 0, 0);
		antenna.Owner = ownerId;
		antenna.ShareMode = MyOwnershipShareModeEnum.Faction;
		antenna.Enabled = true;
		antenna.BroadcastRadius = radiusMeters;
		antenna.EnableBroadcasting = true;
		antenna.CustomName = name + "-antenna";
		var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
		battery.Min = new SerializableVector3I(1, 0, 0);
		battery.Owner = ownerId;
		battery.ShareMode = MyOwnershipShareModeEnum.Faction;
		battery.Enabled = true;
		battery.CurrentStoredPower = 3f;
		var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
		builder.GridSizeEnum = MyCubeSize.Large;
		builder.IsStatic = true;
		builder.CreatePhysics = true;
		// Without InScene the entity is only registered, never added to the scene: no update scheduling, no
		// resource distributor ticks, and the antenna sits functional-but-unpowered forever.
		builder.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
		builder.PositionAndOrientation = new MyPositionAndOrientation(position, Vector3D.Forward, Vector3D.Up);
		builder.DisplayName = name;
		builder.CubeBlocks.Add(antenna);
		builder.CubeBlocks.Add(battery);
		MyEntity spawned = MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false)
			?? throw new InvalidOperationException("Could not spawn the antenna host grid");
		return new { grid = spawned.EntityId, created = true };
	}

	/// <summary>
	/// Spawns a named station carrying a LASER antenna (plus battery) for the cross-node laser link leg.
	/// Same anchoring as <see cref="EnsureNamedAntennaGrid"/>; returns the grid and the laser antenna block id.
	/// </summary>
	public static object EnsureLaserGrid(long ownerId, string name, double lateralMeters)
	{
		if (ownerId == 0) throw new ArgumentOutOfRangeException(nameof(ownerId));
		if (FindLaserGrid(name) is object ready) return ready;
		ClusterPlayerPosition anyPlayer = PositionRegistryService.Players.FirstOrDefault(player => player.ClientId != 0)
			?? throw new InvalidOperationException("No cluster player position to anchor the laser grid");
		Vector3D reference = new(anyPlayer.X, anyPlayer.Y, anyPlayer.Z);
		MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(reference);
		Vector3D up = planet == null ? Vector3D.Up
			: Vector3D.Normalize(reference - planet.PositionComp.GetPosition());
		Vector3D position = reference + up * 20000.0 + Vector3D.CalculatePerpendicularVector(up) * lateralMeters;
		return SpawnLaserGrid(ownerId, name, position);
	}

	/// <summary>
	/// SE1-0226: the same laser station at a fixed position, for clientless scenarios. Owner 0 leaves the
	/// blocks unowned, which every player (and every other unowned laser) may use.
	/// </summary>
	public static object EnsureLaserGridAt(long ownerId, string name, Vector3D position)
		=> FindLaserGrid(name) ?? SpawnLaserGrid(ownerId, name, position);

	private static object FindLaserGrid(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new ArgumentException("Invalid grid name");
		MyCubeGrid ready = MyEntities.GetEntities().OfType<MyCubeGrid>()
			.FirstOrDefault(grid => grid.DisplayName == name);
		return ready == null ? null : new { grid = ready.EntityId, created = false,
			laser = ready.GetFatBlocks().OfType<MyLaserAntenna>().FirstOrDefault()?.EntityId ?? 0 };
	}

	private static object SpawnLaserGrid(long ownerId, string name, Vector3D position)
	{
		var laser = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_LaserAntenna>("LargeBlockLaserAntenna");
		laser.Min = new SerializableVector3I(0, 0, 0);
		laser.Owner = ownerId;
		laser.ShareMode = MyOwnershipShareModeEnum.Faction;
		laser.Enabled = true;
		laser.CustomName = name + "-laser";
		var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
		// The laser antenna occupies (0,0,0)-(0,1,0); the battery must touch it to share the grid and its power.
		battery.Min = new SerializableVector3I(1, 0, 0);
		battery.Owner = ownerId;
		battery.ShareMode = MyOwnershipShareModeEnum.Faction;
		battery.Enabled = true;
		battery.CurrentStoredPower = 3f;
		var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
		builder.GridSizeEnum = MyCubeSize.Large;
		builder.IsStatic = true;
		builder.CreatePhysics = true;
		builder.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
		builder.PositionAndOrientation = new MyPositionAndOrientation(position, Vector3D.Forward, Vector3D.Up);
		builder.DisplayName = name;
		builder.CubeBlocks.Add(laser);
		builder.CubeBlocks.Add(battery);
		MyEntity spawned = MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false)
			?? throw new InvalidOperationException("Could not spawn the laser host grid");
		long laserId = (spawned as MyCubeGrid)?.GetFatBlocks().OfType<MyLaserAntenna>().FirstOrDefault()?.EntityId ?? 0;
		return new { grid = spawned.EntityId, created = true, laser = laserId };
	}

	/// <summary>Pairs this grid's laser antenna with a target antenna id (cross-node: a proxy).</summary>
	public static object LaserConnect(long gridEntityId, long targetAntennaId)
	{
		MyLaserAntenna laser = LaserOf(gridEntityId);
		bool connected = laser.ClusterConnectTo(targetAntennaId);
		return new { grid = gridEntityId, laser = laser.EntityId, target = targetAntennaId, requested = connected };
	}

	/// <summary>SE1-0226: points this grid's laser antenna at GPS coordinates, the terminal's "Connect" to a pasted GPS.</summary>
	public static object LaserGps(long gridEntityId, Vector3D coords)
	{
		MyLaserAntenna laser = LaserOf(gridEntityId);
		laser.ConnectToGps(coords);
		return new { grid = gridEntityId, laser = laser.EntityId, state = laser.State.ToString() };
	}

	/// <summary>SE1-0235: the terminal's "Idle" button on this grid's laser antenna.</summary>
	public static object LaserIdle(long gridEntityId)
	{
		MyLaserAntenna laser = LaserOf(gridEntityId);
		laser.SetIdle();
		return new { grid = gridEntityId, laser = laser.EntityId, state = laser.State.ToString() };
	}

	/// <summary>Reports the laser antenna's live connection state for lane polling.</summary>
	public static object LaserStatus(long gridEntityId)
	{
		MyLaserAntenna laser = LaserOf(gridEntityId);
		Vector3D head = laser.HeadPos;
		return new
		{
			grid = gridEntityId,
			laser = laser.EntityId,
			state = laser.State.ToString(),
			targetId = laser.TargetId ?? 0,
			canLase = laser.CanLaseTargetCoords,
			working = laser.IsWorking,
			enabled = laser.Enabled,
			connectionError = laser.m_connectionError.Value.ToString(),
			head = new[] { head.X, head.Y, head.Z },
			// The lasers this node holds only as proxies: what a GPS search here can trip over (SE1-0226).
			proxyLasers = MyAntennaSystem.Static.LaserAntennas.Where(pair => pair.Value.RealAntenna == null)
				.Select(pair => pair.Key).ToArray()
		};
	}

	private static MyLaserAntenna LaserOf(long gridEntityId)
	{
		if (!MyEntities.TryGetEntityById(gridEntityId, out MyEntity entity) || entity is not MyCubeGrid grid)
			throw new KeyNotFoundException("No grid " + gridEntityId);
		return grid.GetFatBlocks().OfType<MyLaserAntenna>().FirstOrDefault()
			?? throw new InvalidOperationException("Grid " + gridEntityId + " has no laser antenna");
	}

	// The relay-chain leg turns the middle relay off through POST /cluster/igc-test-antenna, which
	// stays in the product (AntennaGraphService.SetAntennaEnabled): the regression cluster's
	// bin/antenna-ab drives the same route operationally.

	/// <summary>Places a programmable block on an existing grid and returns its entity id, the IGC address.</summary>
	public static object AddProgrammableBlock(long gridEntityId)
	{
		if (!MyEntities.TryGetEntityById(gridEntityId, out MyEntity entity) || entity is not MyCubeGrid grid)
			throw new KeyNotFoundException("No grid " + gridEntityId);
		MyCubeBlockDefinition definition = MyDefinitionManager.Static.GetDefinitionsOfType<MyCubeBlockDefinition>()
			.FirstOrDefault(candidate => candidate.Id.TypeId == typeof(MyObjectBuilder_MyProgrammableBlock)
				&& candidate.CubeSize == grid.GridSizeEnum && candidate.Public)
			?? throw new InvalidOperationException("No programmable block definition for this grid size");
		var orientation = new MyBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
		Vector3I position = FindFreeCell(grid, definition, orientation)
			?? throw new InvalidOperationException("No free cell adjacent to grid " + gridEntityId);
		// Take the owner from an antenna rather than from BigOwners: an unowned block reaches no remote broadcaster,
		// because MyAntennaSystem filters the relay set by the caller's relation to each antenna's owner and an
		// identity of 0 is friendly with nobody. That failure looks exactly like "no relay path exists".
		long owner = grid.GetFatBlocks<MyRadioAntenna>().Select(antenna => antenna.OwnerId).FirstOrDefault(id => id != 0);
		if (owner == 0) owner = grid.BigOwners.FirstOrDefault();
		MySlimBlock placed = grid.BuildGeneratedBlock(
			new MyCubeGrid.MyBlockLocation(definition.Id, position, position, position, Quaternion.Identity, 0, owner),
			Vector3.Zero, MyStringHash.NullOrEmpty)
			?? throw new InvalidOperationException("Could not place a programmable block on grid " + gridEntityId);
		if (placed.FatBlock is not MyProgrammableBlock block)
			throw new InvalidOperationException("Placed block is not a programmable block");
		if (owner != 0 && block.OwnerId != owner) block.ChangeOwner(owner, MyOwnershipShareModeEnum.Faction);
		// A script's IGC context is created lazily on first use; make it now so the endpoint registry sees it.
		MyIGCSystemSessionComponent.Static.GetOrMakeContextFor(block);
		return new { grid = gridEntityId, address = block.EntityId, position = new[] { position.X, position.Y, position.Z } };
	}

	/// <summary>Sends a unicast exactly as <c>IMyIntergridCommunicationSystem.SendUnicastMessage</c> would.</summary>
	public static object SendUnicast(long sourceAddress, long addressee, string tag, string data)
	{
		IMyIntergridCommunicationSystem igc = Context(sourceAddress);
		bool sent = igc.SendUnicastMessage(addressee, tag, data);
		return new { source = sourceAddress, addressee, tag, sent };
	}

	/// <summary>Sends a broadcast exactly as <c>IMyIntergridCommunicationSystem.SendBroadcastMessage</c> would.</summary>
	public static object SendBroadcast(long sourceAddress, string tag, string data)
	{
		IMyIntergridCommunicationSystem igc = Context(sourceAddress);
		igc.SendBroadcastMessage(tag, data, TransmissionDistance.AntennaRelay);
		return new { source = sourceAddress, tag, sent = true };
	}

	/// <summary>Registers a broadcast listener, which is what puts the tag into the cluster listener registry.</summary>
	public static object Listen(long address, string tag)
	{
		IMyIntergridCommunicationSystem igc = Context(address);
		IMyBroadcastListener listener = igc.RegisterBroadcastListener(tag);
		lock (Sync)
		{
			if (!Listeners.TryGetValue(address, out List<IMyBroadcastListener> registered))
				Listeners[address] = registered = new List<IMyBroadcastListener>();
			if (!registered.Contains(listener)) registered.Add(listener);
		}
		return new { address, tag, listening = true };
	}

	/// <summary>
	/// Drains everything queued for an address. Messages are queued by the IGC dispatch tick, so a lane polls this
	/// rather than reading it once.
	/// </summary>
	public static object Received(long address)
	{
		IMyIntergridCommunicationSystem igc = Context(address);
		var unicast = new List<object>();
		while (igc.UnicastListener.HasPendingMessage)
			unicast.Add(Describe(igc.UnicastListener.AcceptMessage()));
		var broadcast = new List<object>();
		List<IMyBroadcastListener> registered;
		lock (Sync) registered = Listeners.TryGetValue(address, out var found) ? found.ToList() : new List<IMyBroadcastListener>();
		foreach (IMyBroadcastListener listener in registered)
			while (listener.HasPendingMessage)
				broadcast.Add(Describe(listener.AcceptMessage()));
		return new { address, unicast = unicast.ToArray(), broadcast = broadcast.ToArray() };
	}

	/// <summary>
	/// Names the addresses this node hosts and the grids that could carry one. Only grids with a working radio
	/// antenna are offered: IGC across a node boundary is an antenna relay, so a grid without one is not a
	/// candidate for the test even though a programmable block would happily sit on it.
	/// </summary>
	public static object Endpoints()
	{
		MyCubeGrid[] grids = MyEntities.GetEntities().OfType<MyCubeGrid>().ToArray();
		return new
		{
			local = grids.SelectMany(grid => grid.GetFatBlocks<MyProgrammableBlock>()).Select(block => block.EntityId).ToArray(),
			grids = grids.Where(grid => grid.GetFatBlocks<MyRadioAntenna>().Any(antenna => antenna.IsWorking))
				.Select(grid => new
				{
					id = grid.EntityId,
					name = grid.DisplayName,
					blocks = grid.BlocksCount,
					programmableBlocks = grid.GetFatBlocks<MyProgrammableBlock>().Select(block => block.EntityId).ToArray()
				}).ToArray(),
			// Every antenna regardless of state: when the working-grid list above comes up empty, this is what
			// says whether the grid is missing entirely or present with an unpowered/disabled/damaged antenna.
			antennas = grids.SelectMany(grid => grid.GetFatBlocks<MyRadioAntenna>()).Select(antenna => new
			{
				grid = antenna.CubeGrid.EntityId,
				gridName = antenna.CubeGrid.DisplayName,
				isStatic = antenna.CubeGrid.IsStatic,
				blocks = antenna.CubeGrid.BlocksCount,
				power = antenna.CubeGrid.GridSystems?.ResourceDistributor
					?.ResourceStateByType(MyResourceDistributorComponent.ElectricityId).ToString(),
				id = antenna.EntityId,
				working = antenna.IsWorking,
				functional = antenna.IsFunctional,
				enabled = antenna.Enabled,
				owner = antenna.OwnerId
			}).ToArray(),
			cluster = IgcEndpointRegistryService.Endpoints
		};
	}

	/// <summary>Explains why a cross-node send would be accepted or refused, gate by gate.</summary>
	public static object Reachability(long sourceAddress, long addressee)
	{
		if (!MyEntities.TryGetEntityById(sourceAddress, out MyEntity entity) || entity is not MyProgrammableBlock source)
			throw new KeyNotFoundException("No programmable block " + sourceAddress);
		bool known = IgcEndpointRegistryService.TryGetEndpoint(addressee, out ClusterIgcEndpoint endpoint);
		return new
		{
			source = sourceAddress,
			addressee,
			known,
			endpoint,
			localContext = MyIGCSystemSessionComponent.Static.GetContextForPB(addressee) != null,
			relation = known ? source.GetUserRelationToOwner(endpoint.OwnerIdentityId).ToString() : null,
			friendly = known && source.GetUserRelationToOwner(endpoint.OwnerIdentityId).IsFriendly(),
			reach = known ? AntennaGraphService.DescribeReach(source, endpoint.GridEntityId) : null
		};
	}

	private static IMyIntergridCommunicationSystem Context(long address)
	{
		if (!MyEntities.TryGetEntityById(address, out MyEntity entity) || entity is not MyProgrammableBlock block)
			throw new KeyNotFoundException("No programmable block " + address);
		return MyIGCSystemSessionComponent.Static.GetOrMakeContextFor(block);
	}

	private static object Describe(MyIGCMessage message)
		=> new { tag = message.Tag, source = message.Source, data = message.Data?.ToString() };

	private static Vector3I? FindFreeCell(MyCubeGrid grid, MyCubeBlockDefinition definition, MyBlockOrientation orientation)
	{
		// Search outward from the existing blocks: a free cell touching the grid is what keeps the new block part
		// of the same construct, which is what the IGC connection evaluation cares about.
		Vector3I min = grid.Min - Vector3I.One;
		Vector3I max = grid.Max + Vector3I.One;
		for (int x = min.X; x <= max.X; x++)
			for (int y = min.Y; y <= max.Y; y++)
				for (int z = min.Z; z <= max.Z; z++)
				{
					var position = new Vector3I(x, y, z);
					if (grid.CubeExists(position)) continue;
					if (!grid.CanAddCube(position, orientation, definition)) continue;
					if (!Base6Directions.EnumDirections.Any(direction => grid.CubeExists(position + Base6Directions.GetIntVector(direction))))
						continue;
					return position;
				}
		return null;
	}
}
