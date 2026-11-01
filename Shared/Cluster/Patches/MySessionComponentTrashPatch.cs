using System;
using System.Collections.Generic;
using PluginSdk.Clustering;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClusterPlugin;

public static class MySessionComponentTrashPatch
{
	/// <summary>
	/// Set once the session starts unloading. The trash sweep must stop BEFORE the session tears
	/// its state down, because removing a grid dismounts its cockpits, and dismounting raises a
	/// replicated ControlledEntity_Used event through machinery the shutdown has already closed.
	///
	/// node-4, 2026-09-05 00:07:11 (T-0042). The shutdown had already printed "Shutting down
	/// server... Done" and the fixed-step loop then ran another frame:
	///     MySessionComponentTrash.UpdateAfterSimulation -> UpdateTrash -> RemoveGrid
	///     -> MyCubeGrid.DismountAllCockpits -> MyShipController.Use()
	///     -> RaiseControlledEntityUsed -> MyReplicationLayer.DispatchEvent
	///     -> MyCockpit.RemovePilot  ->  NullReferenceException
	/// The node exited 1 instead of the clean 143, which risks an incomplete final flush on a node
	/// that may still hold partitions.
	/// </summary>
	private static volatile bool s_unloading;
	private static IDisposable s_handoverRegistration;

	public static void Install()
	{
		// Idempotent: Install runs once per session start, and -= on an unsubscribed handler is a no-op.
		MySession.OnUnloading -= OnSessionUnloading;
		MySession.OnUnloading += OnSessionUnloading;
		s_unloading = false;
		lock (s_graceLock) s_graceUntil.Clear();
		s_handoverRegistration?.Dispose();
		s_handoverRegistration = Environment.GetEnvironmentVariable("CLUSTER_TRASH_GRACE_HANDOVER") == "0"
			? null : PluginHandoverService.Register("cluster.trash-grace", GraceHandover);
		MySessionComponentTrashSeams.ClusterIsCloseToPlayer = SeamIsCloseToPlayer;
		// Grid, voxel, and forageable trash act on partition-owned state: regular nodes only. The World
		// Authority loads no partition entities, and what it does host (proxy antennas, previews) it does
		// not own - its sweep must never close anything. Identity cleanup is the opposite: identities are
		// WA-owned global state, and a regular node's partial player view would remove identities that are
		// merely on another node.
		MySessionComponentTrashSeams.ClusterRunsGridTrash = ()
			=> !s_unloading
			   && ClusterNodeRole.RunsNodeLocalSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
		MySessionComponentTrashSeams.ClusterRunsIdentityTrash = ()
			=> ClusterNodeRole.RunsAuthoritativeSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
	}

	private static void OnSessionUnloading() => s_unloading = true;

	// T-0254: the trash sweep ran INSIDE handover imports and removed arriving grids: 83 of 485 trash removals on
	// 2026-09-26 fell between an import's prepare_import and its commit, against ~2 by chance (import windows are
	// 0.4% of node time). An arriving grid is frozen and its rotor/piston/connector links are not re-formed yet, so a
	// turret head or a small subgrid reads as a lone sub-20-block grid; vanilla's physical-group protection only
	// applies once the link exists. Victims include Earth-base P517 members ('PDC Custom Turret' 14:27:07.843,
	// 'Small Grid 6178' and 'Large Grid 997' 12:33:26.301, the latter at the import before 'Constructor mk.1' broke
	// apart). An arriving grid counts as close to a player - the trash keeps it - from its creation until
	// ImportGraceSeconds after the partition's commit or recovery load. A single server never trashes a grid mid-load.
	private const double ImportGraceSeconds = 60;
	// A recovery load follows a node death, and the death is what took the partition's players away: the
	// gateway ends their sessions, and they come back through the menu and a rejoin. 60 s was not enough.
	// On 2026-09-27 node-1 recovery-loaded the Earth base (258 grids) at 02:56:09 after node-5 was killed;
	// the grace ended at 02:57:09.77, and 67 ms later the sweep removed 32 grids under 20 blocks with no
	// player within 500 m. Viktor, the only player there, was back at 02:57:52, 103 s after the load. 300 s
	// covers that with margin. A player who does not come back leaves the grids to the world's own trash
	// settings, as a single server would.
	public const double RecoveryGraceSeconds = 300;
	private static readonly object s_graceLock = new object();
	private static readonly Dictionary<long, DateTime> s_graceUntil = new Dictionary<long, DateTime>();
	internal static readonly IPluginGridHandover GraceHandover = new TrashGraceHandover();

	// T-0303: carry the absolute expiry through the existing per-grid handover hooks. Restarting the
	// 300 s interval on every move would let balancing keep debris protected indefinitely.
	private sealed class TrashGraceHandover : IPluginGridHandover
	{
		public byte[] Leaving(long gridId)
		{
			lock (s_graceLock)
				return s_graceUntil.TryGetValue(gridId, out DateTime until) && until > DateTime.UtcNow
					? BitConverter.GetBytes(until.Ticks) : null;
		}
		public void Left(long gridId) { lock (s_graceLock) s_graceUntil.Remove(gridId); }
		public void Stayed(long gridId) { }
		public void Arrived(long gridId, byte[] state)
		{
			if (state?.Length != sizeof(long)) return;
			long ticks = BitConverter.ToInt64(state, 0);
			if (ticks <= DateTime.UtcNow.Ticks || ticks > DateTime.MaxValue.Ticks) return;
			lock (s_graceLock) ExtendGrace(gridId, new DateTime(ticks, DateTimeKind.Utc));
		}
	}

	// Caller holds s_graceLock. Both local protection and an arriving expiry may only extend it.
	private static void ExtendGrace(long id, DateTime until)
	{
		if (!s_graceUntil.TryGetValue(id, out DateTime existing) || existing < until) s_graceUntil[id] = until;
	}

	/// <summary>Protect arriving entities from the trash sweep for `seconds` from now (extends, never shortens).</summary>
	public static void ProtectArrivals(IEnumerable<long> entityIds, double seconds = ImportGraceSeconds)
	{
		DateTime until = DateTime.UtcNow.AddSeconds(seconds);
		lock (s_graceLock)
		{
			if (s_graceUntil.Count > 50000)
			{
				DateTime now = DateTime.UtcNow;
				foreach (long stale in new List<long>(s_graceUntil.Keys))
					if (s_graceUntil[stale] < now) s_graceUntil.Remove(stale);
			}
			foreach (long id in entityIds)
				ExtendGrace(id, until);
		}
	}

	/// <summary>One arriving entity, while its import is still running (the commit extends it).</summary>
	public static void ProtectArrival(long entityId) => ProtectArrivals(new[] { entityId }, 120);

	internal static bool InImportGrace(long entityId)
	{
		lock (s_graceLock)
			return s_graceUntil.TryGetValue(entityId, out DateTime until) && until >= DateTime.UtcNow;
	}

	public static bool IsCloseToPlayerOrCameraPrefix(MyEntity entity, float distanceThreshold, ref bool __result)
	{
		if (entity != null && InImportGrace(entity.EntityId))
		{
			__result = true;
			return false;
		}
		var position = entity.WorldMatrix.Translation;
		if (IsPositionCloseToClusterPlayer(position.X, position.Y, position.Z, distanceThreshold))
		{
			__result = true;
			return false;
		}
		// #1307: vanilla counts a grid as close when no LOCAL online player controls anything - "nobody is
		// playing, keep everything". On one server that means nobody in the world; on a node it only means
		// nobody on this node, so a node without its own players never trashed a grid while players flew
		// elsewhere in the cluster. When the node has no controlling player of its own but the cluster has
		// one, answer as one server would with that player online and not near: not close. With a local
		// controlling player the original runs, so it still reads the live local positions.
		if (!HasLocalControllingPlayer() && HasControllingPlayer(PositionRegistryService.Players))
		{
			__result = false;
			return false;
		}
		return true;
	}

	private static bool HasLocalControllingPlayer()
	{
		foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
			if (player.Controller?.ControlledEntity != null)
				return true;
		return false;
	}

	/// <summary>
	/// Whether a registry entry stands for a player who controls something, the players vanilla counts in
	/// IsCloseToPlayerOrCamera. Camera entries (ClientId 0) are demand only, and EntityId 0 is a player with
	/// neither a controlled entity nor a character (the respawn screen).
	/// </summary>
	internal static bool HasControllingPlayer(IReadOnlyList<ClusterPlayerPosition> players)
	{
		foreach (ClusterPlayerPosition player in players)
			if (player.ClientId != 0 && player.EntityId != 0)
				return true;
		return false;
	}

	/// <summary>The seam's actual verdict, shared with the trash test probe so the lane exercises this code.</summary>
	public static bool IsPositionCloseToClusterPlayer(double x, double y, double z, float distanceThreshold)
	{
		double distanceSquared = (double)distanceThreshold * distanceThreshold;
		foreach (ClusterPlayerPosition player in PositionRegistryService.Players)
			if (player.IsWithinDistance(x, y, z, distanceSquared))
				return true;
		return false;
	}

	private static bool? SeamIsCloseToPlayer(MyEntity entity, float distanceThreshold)
	{
		bool result = false;
		return IsCloseToPlayerOrCameraPrefix(entity, distanceThreshold, ref result) ? null : result;
	}
}
