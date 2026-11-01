using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.World;

namespace ClusterPlugin;

public static class PositionRegistryService
{
	private const string Subsystem = "positions";
	private static readonly object Sync = new object();
	private static readonly Dictionary<string, Report> Reports = new Dictionary<string, Report>(StringComparer.Ordinal);
	private static ClusterPlayerPosition[] m_players = Array.Empty<ClusterPlayerPosition>();
	private static string m_snapshot = PositionRegistryCodec.Write(Array.Empty<ClusterPlayerPosition>());
	private static double m_nextReport;
	private static double m_nextCaptureError;

	/// <summary>
	/// The cluster-wide player positions as of the last report. The array is never mutated in place - both
	/// writers (<see cref="PublishMerged"/> and <see cref="ApplySnapshot"/>) REPLACE it - so handing the
	/// reference out is safe and skips a copy. It used to copy: the trash sweep asks once per entity
	/// (MySessionComponentTrashPatch.IsPositionCloseToClusterPlayer), which made a full array clone plus a
	/// lock acquisition per grid on the game thread.
	/// </summary>
	public static IReadOnlyList<ClusterPlayerPosition> Players
	{
		get { lock (Sync) return m_players; }
	}

	public static void Install()
	{
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ApplySnapshot);
		MySessionComponentTrashPatch.Install();
	}

	public static void Update()
	{
		if (MySession.Static?.Ready != true || ClusterRuntime.LocalMilliseconds < m_nextReport) return;
		m_nextReport = ClusterRuntime.LocalMilliseconds + 1000;
		if (ClusterRuntime.IsWorldAuthority)
		{
			PublishMerged();
			return;
		}
		// Capture per player: one throwing player (a dead character mid cross-node respawn) must skip
		// that player, not silently kill the node's whole report - that starved the WA position
		// registry of every player this node hosts.
		var players = new List<ClusterPlayerPosition>();
		foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
		{
			try { players.Add(Capture(player)); }
			catch (Exception exception)
			{
				if (ClusterRuntime.LocalMilliseconds >= m_nextCaptureError)
				{
					m_nextCaptureError = ClusterRuntime.LocalMilliseconds + 10000;
					Console.WriteLine("[ClusterPlugin] position capture failed player=" + player?.Id.SteamId
						+ " error=" + SecretRedactor.Redact(exception.Message));
				}
			}
		}
		CaptureViewedCameras(players);
		GlobalStateChannel.SendRequest(Subsystem, PositionRegistryCodec.Write(players));
	}

	/// <summary>
	/// Actively viewed camera blocks are demand positions too: a client watching a remote camera must create
	/// wake demand (and trash protection - vanilla's own sweep checks IsCloseToPlayerOrCamera) around the
	/// CAMERA, not just around its character. The viewer count rides the camera's synced m_activeViewers, so
	/// replicas report identically and the merge dedupes on the camera-derived identity id. Demand-only
	/// entries carry ClientId 0, which every player-targeting consumer (encounters, drops, meteors, weather)
	/// filters out.
	/// </summary>
	private static void CaptureViewedCameras(List<ClusterPlayerPosition> players)
	{
		try
		{
			foreach (VRage.Game.Entity.MyEntity entity in Sandbox.Game.Entities.MyEntities.GetEntities())
				if (entity is Sandbox.Game.Entities.MyCubeGrid grid && !grid.MarkedForClose)
					// GetFatBlocks<T>() is NOT a per-type index: MyFatBlockReader<T> enumerates the grid's
					// whole m_cubeBlocks slim-block set and casts each FatBlock, so it costs O(all blocks)
					// including armour. The untyped GetFatBlocks() returns the maintained m_fatBlocks list,
					// so this is O(fat blocks). Same blocks visited in the same order-independent way; on
					// today's world the saving is microseconds, but this runs once a second over every grid
					// on the node and wrecks are never deleted, so it grows with the world.
					foreach (Sandbox.Game.Entities.MyCubeBlock block in grid.GetFatBlocks())
						if (block is Sandbox.Game.Entities.MyCameraBlock camera && ((Sandbox.ModAPI.Ingame.IMyCameraBlock)camera).IsActive)
						{
							// The entity id doubles as the merge key: it is positive (the codec rejects
							// IdentityId <= 0) and its allocator type bits keep it disjoint from every
							// real player identity id.
							VRageMath.Vector3D position = camera.WorldMatrix.Translation;
							players.Add(new ClusterPlayerPosition(camera.EntityId, 0, camera.EntityId,
								position.X, position.Y, position.Z));
						}
		}
		catch (Exception exception)
		{
			if (ClusterRuntime.LocalMilliseconds >= m_nextCaptureError)
			{
				m_nextCaptureError = ClusterRuntime.LocalMilliseconds + 10000;
				Console.WriteLine("[ClusterPlugin] camera capture failed error=" + SecretRedactor.Redact(exception.Message));
			}
		}
	}

	/// <summary>World Authority only: every reported player and viewed camera WITH the node that reported it -
	/// the node a T-0193 offline partition should load onto when that player approaches it.</summary>
	public static (string Origin, ClusterPlayerPosition Player)[] PlayersWithOrigin()
	{
		lock (Sync)
			return Reports.SelectMany(x => x.Value.Players.Select(player => (x.Key, player))).ToArray();
	}

	/// <summary>Per-origin report ages for diagnostics: which nodes are actually reporting.</summary>
	public static string DescribeReports()
	{
		lock (Sync)
			return string.Join(", ", Reports.Select(x =>
				$"{x.Key}:{x.Value.Players.Length}players@{(long)(ClusterRuntime.LocalMilliseconds - x.Value.Received)}ms"));
	}

	private static ClusterPlayerPosition Capture(MyPlayer player)
	{
		var controlled = player.Controller?.ControlledEntity?.Entity;
		var character = player.Character;
		if (controlled == null && character == null && player.Identity != null)
		{
			// After a partition import moves a character to this node, MyIdentity.Character stays null
			// until the client controller re-attaches (nothing calls ChangeCharacter), which starved
			// encounter targeting of eligible players for minutes. The identity's SavedCharacters set
			// still names the character's entity id, and the import keeps entity ids stable.
			foreach (long saved in player.Identity.SavedCharacters)
				if (Sandbox.Game.Entities.MyEntities.TryGetEntityById(saved, out VRage.Game.Entity.MyEntity entity)
					&& entity is Sandbox.Game.Entities.Character.MyCharacter alive && !alive.IsDead)
				{
					character = alive;
					break;
				}
		}
		var position = controlled?.WorldMatrix.Translation ?? character?.WorldMatrix.Translation ?? player.GetPosition();
		return new ClusterPlayerPosition(player.Identity.IdentityId, player.Id.SteamId,
			controlled?.EntityId ?? character?.EntityId ?? 0, position.X, position.Y, position.Z);
	}

	private static void ReceiveReport(string origin, string payload)
	{
		ClusterPositionSnapshot snapshot = PositionRegistryCodec.Read(payload);
		lock (Sync) Reports[origin] = new Report(ClusterRuntime.LocalMilliseconds, snapshot.Players);
		PublishMerged();
	}

	private static void PublishMerged()
	{
		string snapshot;
		lock (Sync)
		{
			double cutoff = ClusterRuntime.LocalMilliseconds - 5000;
			foreach (string origin in Reports.Where(x => x.Value.Received < cutoff).Select(x => x.Key).ToArray()) Reports.Remove(origin);
			m_players = Reports.OrderBy(x => x.Value.Received).ThenBy(x => x.Key, StringComparer.Ordinal)
				.SelectMany(x => x.Value.Players).GroupBy(x => x.IdentityId).Select(x => x.Last()).OrderBy(x => x.IdentityId).ToArray();
			snapshot = PositionRegistryCodec.Write(m_players);
			if (snapshot == m_snapshot) return;
			m_snapshot = snapshot;
		}
		try { GlobalStateChannel.Publish(Subsystem, snapshot); }
		catch (Exception exception) { Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=positions error=" + SecretRedactor.Redact(exception.Message)); }
	}

	private static void ApplySnapshot(string payload)
	{
		ClusterPositionSnapshot snapshot = PositionRegistryCodec.Read(payload);
		lock (Sync)
		{
			m_players = snapshot.Players;
			m_snapshot = payload;
		}
	}

	private static string Snapshot() { lock (Sync) return m_snapshot; }
	private sealed record Report(double Received, ClusterPlayerPosition[] Players);
}
