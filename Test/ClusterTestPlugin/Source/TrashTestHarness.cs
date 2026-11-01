using System;
using System.Linq;
using ClusterPlugin;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// WA-3 trash-removal test seam. Spawns a probe grid built to be genuine trash under the world's Default trash
/// flags (0x1E1A): dynamic (Fixed removal is NOT enabled, so a static grid would be protected), motionless
/// (Stationary removal IS enabled), unpowered, unowned, single block - the only thing that can protect it is
/// player distance. The state probe reports the real component's verdicts so a lane can prove removal and
/// protection decisions on every node, including proxies on the World Authority.
/// </summary>
public static class TrashTestHarness
{
	private static int m_probeCounter;
	private static readonly Guid MesNpcDataKey = new Guid("29FFD684-13D7-4045-BF76-CD48BF80E36A");

	/// <summary>
	/// Spawns a probe grid. <paramref name="blocks"/> above one makes it a fixture for the partitioning and
	/// balancing scenarios rather than a trash probe: P1-BAL-01 needs measurable PCU on one node, and a
	/// single armor block carries about one. <paramref name="spin"/> (rad/s) keeps a dynamic grid's physics
	/// body awake, so it carries measured sim load (T-0168: the balancer plans by load, and a still grid's
	/// sleeping body costs nothing); an unpowered grid in space keeps spinning.
	/// </summary>
	public static object Spawn(double x, double y, double z, long builtBy = 0, bool isStatic = false, int blocks = 1, double spin = 0, bool npc = false,
		long npcOwner = 0, bool mesKey = false, int batteries = 0, double charge = -1, bool batteriesOff = false)
	{
		if (blocks < 1 || blocks > 100_000) throw new ArgumentOutOfRangeException(nameof(blocks));
		var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
		builder.GridSizeEnum = MyCubeSize.Large;
		builder.IsStatic = isStatic;
		builder.CreatePhysics = true;
		builder.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
		builder.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(x, y, z), Vector3D.Forward, Vector3D.Up);
		builder.DisplayName = "cluster-trash-probe-" + ++m_probeCounter;
		// A compact contiguous cube: blocks have to touch or the grid refuses to build them, and a
		// run along X is 2.5 m per block - a station-sized probe (P3-CST-01) laid out as a line
		// would be kilometers long and distort every distance-based partition decision.
		int edge = (int)Math.Ceiling(Math.Pow(blocks, 1.0 / 3.0));
		for (int index = 0; index < blocks; index++)
		{
			var armor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock");
			armor.Min = new SerializableVector3I(index % edge, index / edge % edge, index / (edge * edge));
			if (builtBy != 0)
			{
				armor.BuiltBy = builtBy;
				armor.Owner = builtBy;
			}
			builder.CubeBlocks.Add(armor);
		}
		// #1354: an NPC grid (big owner an NPC identity, as MES spawns them) needs a block that HAS an owner; armor has
		// none. One battery beside the cube, owned by the world's first NPC identity (or a new one through the WA).
		// #1365: or by the given identity, known or not (an unknown one is the unknown-owner guard's positive).
		long owner = 0;
		if (npc)
		{
			owner = npcOwner != 0 ? npcOwner : MySession.Static.Players.GetNPCIdentities().FirstOrDefault();
			if (owner == 0) owner = MySession.Static.Players.CreateNewNpcIdentity("cluster-npc-probe").IdentityId;
			var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
			battery.Min = new SerializableVector3I(edge, 0, 0);
			battery.Owner = owner;
			battery.BuiltBy = owner;
			battery.ShareMode = MyOwnershipShareModeEnum.None;
			builder.CubeBlocks.Add(battery);
			builder.DisplayName = "cluster-npc-probe-" + m_probeCounter;
		}
		// #1390: ownerless batteries beside it (blocks that are not cheap), and every battery's stored power in MWh
		// (charge 0 = an unpowered grid; unset = the definition's initial charge). batteriesOff switches them off: in a
		// creative world (the bench's) an enabled battery charges itself, so an unpowered probe has to be off as well.
		for (int index = 0; index < batteries; index++)
		{
			var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
			battery.Min = new SerializableVector3I(edge + (npc ? 1 : 0) + index, 0, 0);
			builder.CubeBlocks.Add(battery);
		}
		foreach (MyObjectBuilder_BatteryBlock battery in builder.CubeBlocks.OfType<MyObjectBuilder_BatteryBlock>())
		{
			if (charge >= 0) battery.CurrentStoredPower = (float)charge;
			if (batteriesOff) battery.Enabled = false;
		}
		MyEntity spawned = MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false)
			?? throw new InvalidOperationException("Could not spawn the trash probe grid");
		if (spin != 0 && !isStatic && spawned.Physics != null)
			spawned.Physics.AngularVelocity = new Vector3(0, (float)spin, 0);
		// #1379: the key MES writes into the main grid of everything it spawns (StorageTools.NpcDataKey).
		if (mesKey)
		{
			spawned.Storage ??= new MyModStorageComponent();
			spawned.Storage[MesNpcDataKey] = "cluster-test";
		}
		return new { grid = spawned.EntityId, name = builder.DisplayName, blocks, spin, owner, batteries,
			working = WreckageCleaner.IsWorking((MyCubeGrid)spawned),
			bigOwners = ((MyCubeGrid)spawned).BigOwners.ToArray(), isStatic = ((MyCubeGrid)spawned).IsStatic,
			mesKey = spawned.Storage?.ContainsKey(MesNpcDataKey) == true };
	}

	/// <summary>
	/// Closes an entity outright. P1-MEM-05's whole point is the split that becomes possible with no member
	/// moving - the bridging entity in the middle of a chain is destroyed while both halves stay still - and
	/// there is no other way to produce that from a test.
	/// </summary>
	public static object CloseEntity(long entityId)
	{
		if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity))
			return new { closed = false, entityId, reason = "not on this node" };
		string name = entity.DisplayName;
		entity.Close();
		return new { closed = true, entityId, name };
	}

	/// <summary>Removes one block from its grid, as grinding it down does; the grid stays unless it was its last block.</summary>
	public static object RemoveBlock(long blockId)
	{
		if (!MyEntities.TryGetEntityById(blockId, out MyEntity entity))
			return new { removed = false, blockId, reason = "not on this node" };
		if (entity is not MyCubeBlock block)
			return new { removed = false, blockId, reason = "not a block" };
		long grid = block.CubeGrid.EntityId;
		block.CubeGrid.RazeBlock(block.Position);
		return new { removed = true, blockId, grid };
	}

	/// <summary>
	/// The cluster protection verdict for a raw position, on any node role. This is the cross-node proof: a
	/// node hosting ZERO local players (or the World Authority, which never hosts any) can only report a
	/// position as protected through the replicated position registry.
	/// </summary>
	public static object Close(double x, double y, double z)
	{
		return new
		{
			close = MySessionComponentTrashPatch.IsPositionCloseToClusterPlayer(x, y, z,
				MySession.Static.Settings.PlayerDistanceThreshold),
			registryPlayers = PositionRegistryService.Players.Count,
			localPlayers = MySession.Static.Players.GetOnlinePlayers().Count
		};
	}

	public static object State(long entityId)
	{
		if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyCubeGrid grid
			|| grid.MarkedForClose)
			return new { exists = false };
		MySessionComponentTrash trash = MySession.Static?.GetComponent<MySessionComponentTrash>();
		return new
		{
			exists = true,
			markedAsTrash = grid.MarkedAsTrash,
			trashState = trash?.GetTrashState(grid).ToString(),
			closeToPlayer = MySessionComponentTrash.IsCloseToPlayerOrCamera(grid,
				MySession.Static.Settings.PlayerDistanceThreshold),
			registryPlayers = PositionRegistryService.Players.Count
		};
	}
}
