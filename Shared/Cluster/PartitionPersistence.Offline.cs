using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// T-0193 OFFLINE PARTITIONS, the owner's half (Plan/Partitioning.md §3.6). A partition this node owns is
/// taken offline - hosted by no node at all - once it has been quiet for <c>offline.graceSeconds</c>:
///
///   * every member is a grid, floating object or inventory bag whose physics is static or SLEEPING and
///     not moving (requirement 2); a character, missile or meteor member keeps it online;
///   * no online player's body or controlled grid is in it, no client is routed to it, and no seat in it
///     holds the body of a client attached to this node, joined or still joining (requirement 1, T-0233);
///   * no player or actively viewed camera, anywhere in the cluster, is within <c>offline.quietMeters</c> of
///     its sphere (the cluster position registry), so nobody can currently see it;
///   * it has been on this node for <c>offline.minOnlineSeconds</c>, and nothing about it is in flight
///     (pending members, a pending save, a freeze, an import, a drain).
///
/// The transition is the drain's, which is proven: freeze the members, write a Final and wait until the
/// registry has catalogued it, THEN offer it to the gateway, and close the entities only after the gateway
/// has committed the offline row. At no point does the content exist nowhere: before the commit the node
/// still owns it (a crash recovers it from the Final or an older save), after the commit the Final is the
/// catalogued newest save of an unowned partition, which retention keeps and replication copies. Any
/// refusal or failure thaws the members and the partition simply stays online.
///
/// Energy and production do not progress while offline (requirement 3, a deliberate v1 concession). The
/// antennas and respawn points captured at the freeze ride the offline row (requirement 6 and the spawn
/// trigger); the World Authority republishes them for as long as the partition is offline.
/// </summary>
internal sealed partial class PartitionPersistence
{
	private enum OfflinePhase { Saving, Posting, Accepted }
	private enum OfflineAnswer { Accepted, Refused, Unavailable }

	private sealed class OfflineTransition
	{
		public ulong Partition;
		public uint Generation;
		public Guid FreezeId;
		public HashSet<long> Members;
		public Dictionary<long, DrainFrozenState> Frozen;
		public PendingSave Save;
		public AntennaGraphEntry[] Antennas;
		public ClusterRespawnPoint[] RespawnPoints;
		/// <summary>Member ids in the same order as <see cref="MemberSpheres"/>, so a reader can place any member.</summary>
		public long[] MemberIds;
		public double[][] MemberSpheres;
		/// <summary>#1354: the NPC grids among the members, for the registry's NPC census.</summary>
		public object[] Npc;
		public object[] Grids;
		public VRageMath.BoundingSphereD Sphere;
		public long EstimatedSnapshotBytes;
		public OfflinePhase Phase;
		public DateTime StartedUtc;
		public Task<(OfflineAnswer Answer, string Detail)> Post;
		public int PostAttempts;
		public DateTime NextPostUtc;
	}

	private OfflineTransition m_offlining;
	private readonly Dictionary<ulong, DateTime> m_offlineQuietSince = new Dictionary<ulong, DateTime>();
	private readonly Dictionary<ulong, DateTime> m_offlineRetryAfter = new Dictionary<ulong, DateTime>();
	private readonly Dictionary<ulong, DateTime> m_onlineSince = new Dictionary<ulong, DateTime>();
	private DateTime m_nextOfflineSweep;
	private DateTime m_nextGridBackfill;
	private Task m_gridBackfill;
	private ulong m_gridBackfillAfter;
	private DateTime m_nextOfflineSummary;
	private Dictionary<string, int> m_offlineBlockers = new Dictionary<string, int>();
	private int m_offlineTaken, m_offlineRefused, m_offlineFailed;
	private string m_offlineLastRefusal;

	private static readonly TimeSpan OfflineSweepInterval = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan OfflineSaveTimeout = TimeSpan.FromSeconds(120);

	/// <summary>Game thread, every Update: advances an in-flight transition and, every few seconds, looks
	/// for the next partition to take offline. One transition at a time per node - it is a background
	/// economy, not a latency path.</summary>
	private void UpdateOffline()
	{
		if (m_offlining != null) ProgressOffline();
		if (!RegistryEnabled || !m_registryReady || m_closed || m_fenced || m_concealment || m_warmRecovery
			|| ClusterRuntime.IsWorldAuthority || MySession.Static?.Ready != true) return;
		DateTime now = DateTime.UtcNow;
		if (now >= m_nextGridBackfill && m_gridBackfill?.IsCompleted != false)
		{
			m_nextGridBackfill = now.AddSeconds(ClusterConfig.GetInt("offline.gridBackfillSeconds"));
			m_gridBackfill = Task.Run(BackfillOfflineGrids);
		}
		if (now < m_nextOfflineSweep) return;
		m_nextOfflineSweep = now + OfflineSweepInterval;
		if (!ClusterConfig.GetBool("offline.enabled"))
		{
			m_offlineQuietSince.Clear();
			return;
		}
		SweepOffline(now);
	}

	/// <summary>SE-810, off the game thread: one offline row that went offline before the offers carried its grids, read from
	/// its Final without loading it. One row per pass (offline.gridBackfillSeconds), and only from a copy on this host's disk,
	/// so a sleeping world costs no transfers: a row with no copy here comes round on a node of a host that has one. Two
	/// nodes reading the same row is harmless, the registry keeps the first answer.</summary>
	private void BackfillOfflineGrids()
	{
		try
		{
			using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(30));
			ClusterRuntime.AuthorizeRegistryClient(client);
			using JsonDocument missing = Get(client, "/registry/partitions/offline/grids/missing?limit=1&after="
				+ m_gridBackfillAfter.ToString(CultureInfo.InvariantCulture));
			foreach (JsonElement id in missing.RootElement.EnumerateArray())
			{
				ulong partition = id.GetProperty("Partition").GetUInt64();
				long generation = id.GetProperty("Generation").GetInt64(), sequence = id.GetProperty("Sequence").GetInt64();
				m_gridBackfillAfter = partition;
				using JsonDocument saves = Get(client, "/registry/saves?partition=" + partition.ToString(CultureInfo.InvariantCulture));
				string path = saves.RootElement.EnumerateArray()
					.Where(row => row.GetProperty("SaveId").GetProperty("Generation").GetInt64() == generation
						&& row.GetProperty("SaveId").GetProperty("Sequence").GetInt64() == sequence)
					.SelectMany(row => row.GetProperty("Locations").EnumerateArray()).Select(location => location.GetString())
					.FirstOrDefault(location => Path.IsPathFullyQualified(location) && File.Exists(location));
				if (path == null) continue;
				PartitionSaveFile.Parsed save = PartitionSaveFile.ReadFile(path);
				PartitionSaveFile.ValidateEnvelope(save, partition, (uint)generation, (uint)sequence);
				object[] grids = OfflineGridRows.Rows(PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount));
				PostJson(client, "/registry/partitions/offline/grids", new { Partition = partition, Generation = generation, Sequence = sequence, Grids = grids }).Dispose();
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline grids of partition " + partition + " read from its Final: " + grids.Length);
			}
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline grids not read: " + SecretRedactor.Redact(exception.Message));
		}
	}

	private void SweepOffline(DateTime now)
	{
		PartitionManager.Partition[] partitions = m_partitions.Evaluate().ToArray();
		HashSet<ulong> present = partitions.Select(x => x.Id).ToHashSet();
		foreach (ulong gone in m_onlineSince.Keys.Where(id => !present.Contains(id)).ToArray()) m_onlineSince.Remove(gone);
		foreach (ulong gone in m_offlineQuietSince.Keys.Where(id => !present.Contains(id)).ToArray()) m_offlineQuietSince.Remove(gone);
		foreach (ulong gone in m_offlineRetryAfter.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) m_offlineRetryAfter.Remove(gone);

		HashSet<ulong> owned;
		lock (m_owned) owned = m_owned.Keys.ToHashSet();
		HashSet<ulong> routed = m_partitions.ClientPartitions(partitions).Select(x => x.Partition).ToHashSet();
		HashSet<long> anchors = OnlinePlayerAnchors(out List<Vector3D> localPlayers);
		HashSet<long> attached = MyPlayerCollectionPatch.AttachedIdentities();
		IReadOnlyList<ClusterPlayerPosition> players = PositionRegistryService.Players;
		double quiet = ClusterConfig.GetInt("offline.quietMeters");
		double grace = ClusterConfig.GetInt("offline.graceSeconds");
		double minOnline = ClusterConfig.GetInt("offline.minOnlineSeconds");
		var blockers = new Dictionary<string, int>();
		PartitionManager.Partition best = null;
		DateTime bestSince = DateTime.MaxValue;
		foreach (PartitionManager.Partition partition in partitions)
		{
			if (!m_onlineSince.ContainsKey(partition.Id)) m_onlineSince[partition.Id] = now;
			string blocker = OfflineBlocker(partition, now, owned, routed, anchors, attached, players, localPlayers, quiet, minOnline);
			if (blocker != null)
			{
				m_offlineQuietSince.Remove(partition.Id);
				string key = blocker.Split(':')[0];
				blockers[key] = blockers.GetValueOrDefault(key) + 1;
				continue;
			}
			if (!m_offlineQuietSince.TryGetValue(partition.Id, out DateTime since))
			{
				m_offlineQuietSince[partition.Id] = now;
				blockers["grace"] = blockers.GetValueOrDefault("grace") + 1;
				continue;
			}
			if ((now - since).TotalSeconds < grace)
			{
				blockers["grace"] = blockers.GetValueOrDefault("grace") + 1;
				continue;
			}
			blockers["eligible"] = blockers.GetValueOrDefault("eligible") + 1;
			if (since < bestSince)
			{
				best = partition;
				bestSince = since;
			}
		}
		m_offlineBlockers = blockers;
		if (now >= m_nextOfflineSummary)
		{
			m_nextOfflineSummary = now.AddMinutes(10);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline sweep: partitions=" + partitions.Length
				+ " taken=" + m_offlineTaken + " refused=" + m_offlineRefused + " failed=" + m_offlineFailed
				+ " blockers=" + string.Join(",", blockers.OrderByDescending(x => x.Value).Select(x => x.Key + ":" + x.Value)));
		}
		// #1390: its wreckage goes first, so the Final never holds it; the next sweep takes the partition as it then stands.
		if (best != null && m_offlining == null && WreckageCleaner.BeforeOffline(best, this, "before offline", mayEmpty: true) == 0)
			BeginOffline(best, now);
	}

	/// <summary>Null when the partition may go offline now (grace aside); otherwise the first reason it may not.</summary>
	private string OfflineBlocker(PartitionManager.Partition partition, DateTime now, HashSet<ulong> owned, HashSet<ulong> routed,
		HashSet<long> anchors, HashSet<long> attached, IReadOnlyList<ClusterPlayerPosition> players, List<Vector3D> localPlayers,
		double quiet, double minOnline)
	{
		ulong id = partition.Id;
		if (m_offlining?.Partition == id) return "offlining";
		if (!owned.Contains(id)) return "unowned";
		if (m_apply.IsPending(id)) return "registry"; // T-0354: its topology apply is unanswered
		if (partition.Members.Count == 0) return "empty";
		if (partition.HasPending) return "initializing";
		if (m_startupFenced.ContainsKey(id) || m_claimedPending.ContainsKey(id)) return "claim";
		if (!m_state.TryGetValue(id, out State state) || state.Pending) return "saving";
		if (m_offlineRetryAfter.TryGetValue(id, out DateTime retry) && retry > now) return "backoff";
		if ((now - m_onlineSince[id]).TotalSeconds < minOnline) return "recent";
		if (routed.Contains(id)) return "client";
		lock (m_drainSaves) if (m_drainSaves.ContainsKey(id)) return "draining";
		if (ClusterControlServer.HasLiveInboundTransfer(id)) return "importing";
		if (partition.Fast) return "fast"; // T-0292: moving, whatever the bodies report
		foreach (long member in partition.Members)
		{
			if (ClusterRuntime.IsFrozen(member)) return "frozen";
			if (anchors.Contains(member)) return "player";
			if (!MyEntities.TryGetEntityById(member, out MyEntity entity)) return "missing";
			if (entity is MyCubeGrid grid && SeatsAttachedPlayer(grid, attached)) return "joining";
			if (entity is MyCharacter body && attached.Contains(body.GetPlayerIdentityId())) return "joining";
			string memberBlocker = OfflineMemberBlocker(entity);
			if (memberBlocker != null) return memberBlocker;
		}
		VRageMath.BoundingSphereD sphere = partition.Sphere;
		double reach = sphere.Radius + quiet;
		double reachSquared = reach * reach;
		foreach (ClusterPlayerPosition player in players)
			if (player.IsWithinDistance(sphere.Center.X, sphere.Center.Y, sphere.Center.Z, reachSquared))
				return player.ClientId == 0 ? "camera-near" : "player-near";
		// This node's own online players, exact and with no World Authority round trip: a player who joined
		// seconds ago has no position in the cluster registry yet (w32 2026-09-23 21:44:39).
		foreach (Vector3D position in localPlayers)
			if (Vector3D.DistanceSquared(sphere.Center, position) <= reachSquared)
				return "player-near";
		return null;
	}

	/// <summary>Requirement 2, per member: only content that can sit on disk unchanged qualifies - a grid, a
	/// floating object or a bag whose body is static or asleep and not drifting. Two numbers, not one:
	/// Havok's activation flag, and the velocities themselves (a keyframed or welded oddity can misreport
	/// the flag, and a body that is technically asleep while carrying velocity is not at rest).</summary>
	internal static string OfflineMemberBlocker(MyEntity entity) =>
		OfflineMemberRestBlocker(entity) ?? PluginOfflineService.Blocker(entity.EntityId); // SE-903

	private static string OfflineMemberRestBlocker(MyEntity entity)
	{
		if (entity is MyCharacter character) return OfflineCharacterBlocker(character);
		if (entity is not (MyCubeGrid or MyFloatingObject or MyInventoryBagEntity)) return "type:" + entity.GetType().Name;
		if (entity.MarkedForClose || !entity.InScene) return "closing";
		MyPhysicsComponentBase physics = entity.Physics;
		if (physics == null || physics.IsStatic) return null;
		if (physics.IsActive) return "awake";
		if (physics.LinearVelocity.LengthSquared() > 0.01f || physics.AngularVelocity.LengthSquared() > 0.01f) return "moving";
		return null;
	}

	/// <summary>T-0237: a standing body of a player who is not online goes offline with its partition, as a
	/// cryo-seated one already did. Every one pinned its partition as "type:MyCharacter": w32 2026-09-24,
	/// 68 of 81 hosted partitions held nothing online but the bodies of logged-out pilots. The rejoin is
	/// unchanged in kind - the registry's character home names the partition, PendingHomeRecovery loads
	/// it and holds the join, and the player resumes the body as vanilla would; a join that outlives the
	/// hold gets the respawn screen. A body that loads back after its player has moved on to another one
	/// is reaped by <see cref="CharacterHomeService"/> against that home.
	///
	/// Havok's activation flag is not asked: a character controller on a planet reports active while it
	/// stands still, so only the velocities decide at-rest. The anchors and attached checks in
	/// <see cref="OfflineBlocker"/> already cover this node's players; the online lookup here adds a
	/// player whose body is here while its session is being torn down or set up.</summary>
	private static string OfflineCharacterBlocker(MyCharacter character)
	{
		if (character.MarkedForClose || !character.InScene) return "closing";
		if ((character.ControllerInfo?.ControllingIdentityId ?? 0) != 0) return "character-controlled";
		long identity = character.GetPlayerIdentityId();
		MyPlayerCollection players = MySession.Static.Players;
		if (identity != 0 && players.TryGetPlayerId(identity, out MyPlayer.PlayerId id) && players.IsPlayerOnline(ref id))
			return "character-online";
		MyPhysicsComponentBase physics = character.Physics;
		if (physics != null && (physics.LinearVelocity.LengthSquared() > 0.01f || physics.AngularVelocity.LengthSquared() > 0.01f))
			return "moving";
		return null;
	}

	/// <summary>T-0233: a seat holding the body of a client attached to this node pins its grid even before that
	/// client has a MyPlayer. A carry-over joins INTO its seated body, and until its player exists and takes
	/// control neither the "client" route nor <see cref="OnlinePlayerAnchors"/> can name it - both read the
	/// vanilla Controller. w32 2026-09-24 06:57: pilot-58/60/65's ships went offline 1-8 s after the gateway
	/// routed each client to them (boot + minOnline + grace landed inside the join), the clients respawned a
	/// second body, and the woken seat's copy was reaped as a duplicate, leaving each client with no local
	/// character for good. The seat's owner is the body's own owner module (MyCharacter.GetPlayerIdentityId),
	/// which a grid save carries with the pilot.</summary>
	private static bool SeatsAttachedPlayer(MyCubeGrid grid, HashSet<long> attached)
	{
		if (attached.Count == 0) return false;
		foreach (MyCockpit cockpit in grid.GetFatBlocks<MyCockpit>())
			if (cockpit.Pilot is { } pilot && attached.Contains(pilot.GetPlayerIdentityId())) return true;
		return false;
	}

	/// <summary>Every entity an online player's presence pins: the body and what it controls, and the
	/// top-most entity each hangs from (the grid a seated or remote-controlling player is in). A cryo-seated
	/// body of a player who is OFFLINE is not here, and that partition may go offline with it.</summary>
	private static HashSet<long> OnlinePlayerAnchors(out List<Vector3D> positions)
	{
		var anchors = new HashSet<long>();
		positions = new List<Vector3D>();
		foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
		{
			try
			{
				if (player.Character is MyEntity character)
				{
					anchors.Add(character.EntityId);
					anchors.Add(character.GetTopMostParent().EntityId);
					positions.Add(character.PositionComp.GetPosition());
				}
				if (player.Controller?.ControlledEntity?.Entity is MyEntity controlled)
				{
					anchors.Add(controlled.EntityId);
					anchors.Add(controlled.GetTopMostParent().EntityId);
					positions.Add(controlled.PositionComp.GetPosition());
				}
			}
			catch (Exception)
			{
				// A player mid-teardown: skip it, the next sweep sees it settled.
			}
		}
		return anchors;
	}

	/// <summary>
	/// T-0237: a drained partition goes OFFLINE instead of being released unowned, so nothing reloads it until
	/// something asks for it. A released partition is recovered at once - by another node on a rotation, by
	/// every node at the next start - and then sits online for minutes before the offline sweep may take it
	/// again (w32 2026-09-24 23:17: 120 partitions reloaded by a restart with no player anywhere near).
	///
	/// A CLUSTER STOP takes every partition: nothing simulates between a stop and the next start anyway, so
	/// an offline partition loses nothing a drained one keeps; a ship in flight is saved in flight either way.
	/// Except one a plugin keeps online (SE-904): a job with no player near would wait for a visit after the
	/// start, so that partition is released and the start loads it. This asks once and waits for nothing.
	/// A ROTATION takes only what the offline sweep itself would take (no online player's body or controlled
	/// grid, no client, every member at rest); the gateway still judges the geometry, and a refusal falls
	/// back to the release - the partition is then recovered elsewhere, as before.
	///
	/// Captured here, before the drain freezes the members: the freeze disables physics, and every body would
	/// read as at rest. The save's generation and sequence are only known once the Final is queued, so this
	/// returns the offer as a function of it.
	/// </summary>
	private Func<PendingSave, object> DrainOfflineOffer(PartitionManager.Partition partition, PartitionManager.ClientPartition[] clients)
	{
		if (ClusterConfig.Get("offline.enabled") != "true") return null;
		bool stop = m_clusterShutdown;
		HashSet<long> members = partition.Members.ToHashSet();
		MyEntity[] entities = MyEntities.GetEntities().Where(entity => members.Contains(entity.EntityId) && !entity.MarkedForClose).ToArray();
		if (entities.Length == 0 || entities.Length != members.Count) return null;
		if (stop && entities.Any(entity => PluginOfflineService.Blocker(entity.EntityId) != null)) return null;
		if (!stop)
		{
			if (clients.Any(client => client.Partition == partition.Id)) return null;
			HashSet<long> anchors = OnlinePlayerAnchors(out _);
			HashSet<long> attached = MyPlayerCollectionPatch.AttachedIdentities();
			// T-0292: a fast partition is moving whatever Havok says (a warp teleports it with zero velocity):
			// never on disk, it goes through the Final and a recovery load like any partition in motion.
			if (partition.Fast) return null;
			foreach (MyEntity entity in entities)
			{
				if (anchors.Contains(entity.EntityId)) return null;
				if (entity is MyCubeGrid grid && SeatsAttachedPlayer(grid, attached)) return null;
				if (entity is MyCharacter body && attached.Contains(body.GetPlayerIdentityId())) return null;
				if (OfflineMemberBlocker(entity) != null) return null;
			}
		}
		AntennaGraphEntry[] antennas = AntennaGraphService.CaptureFor(members);
		ClusterRespawnPoint[] respawns = RespawnRegistryService.CaptureFor(partition.Id);
		double[][] spheres = entities.Select(entity =>
		{
			VRageMath.BoundingSphereD volume = entity.PositionComp.WorldVolume;
			return new[] { volume.Center.X, volume.Center.Y, volume.Center.Z, volume.Radius };
		}).ToArray();
		long[] ids = entities.Select(entity => entity.EntityId).ToArray();
		object[] npc = NpcCensusService.Rows(entities);
		object[] grids = ClusterConfig.GetBool("offline.offerGrids") ? OfflineGridRows.Rows(entities) : null;
		VRageMath.BoundingSphereD sphere = partition.Sphere;
		long bytes = PartitionManager.EstimatedSnapshotBytes(partition);
		ulong id = partition.Id;
		return save => new
		{
			Partition = id,
			Node = m_node,
			Epoch = m_nodeEpoch,
			Generation = (long)save.Generation,
			Sequence = (long)save.Sequence,
			X = sphere.Center.X,
			Y = sphere.Center.Y,
			Z = sphere.Center.Z,
			sphere.Radius,
			MemberSpheres = spheres,
			Members = ids,
			EstimatedSnapshotBytes = bytes,
			Antennas = JsonSerializer.Serialize(antennas),
			RespawnPoints = JsonSerializer.Serialize(respawns),
			Npc = npc,
			Grids = grids,
			Drain = true,
		};
	}

	/// <summary>T-0237, registry thread: post a drained partition's offline offer. True = offline (the caller forgets
	/// the partition as a release would); false = refused, release it as before; null = no verdict (a lost reply
	/// or a gateway restart) - the offer is idempotent, so the next heartbeat asks again rather than release a
	/// partition the gateway may already have taken offline.</summary>
	private bool? TryDrainOffline(HttpClient client, DrainSave drain)
	{
		try
		{
			using JsonDocument row = PostJson(client, "/registry/partitions/offline", drain.OfflineBody);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain took partition " + drain.Partition + " OFFLINE"
				+ (m_clusterShutdown ? " (cluster stop)" : " (rotation)") + " instead of releasing it: generation "
				+ drain.Save.Generation + " sequence " + drain.Save.Sequence);
			return true;
		}
		catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
		{
			drain.OfflineBody = null;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain offline of partition " + drain.Partition
				+ " refused, releasing it instead: " + SecretRedactor.Redact(exception.Message));
			return false;
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain offline of partition " + drain.Partition
				+ " got no verdict, asking again: " + SecretRedactor.Redact(exception.Message));
			return null;
		}
	}

	private void BeginOffline(PartitionManager.Partition partition, DateTime now)
	{
		Guid freezeId = Guid.NewGuid();
		HashSet<long> members = partition.Members.ToHashSet();
		MyEntity[] entities = MyEntities.GetEntities().Where(entity => members.Contains(entity.EntityId) && !entity.MarkedForClose).ToArray();
		if (entities.Length != members.Count)
		{
			m_offlineRetryAfter[partition.Id] = now.AddSeconds(30);
			return;
		}
		object[] grids = ClusterConfig.GetBool("offline.offerGrids") ? OfflineGridRows.Rows(entities) : null;
		var frozen = new Dictionary<long, DrainFrozenState>();
		try
		{
			foreach (MyEntity entity in entities)
			{
				ClusterRuntime.MarkFrozen(entity.EntityId, freezeId);
				frozen[entity.EntityId] = DrainFrozenState.Of(entity);
				entity.NeedsUpdate = MyEntityUpdateEnum.NONE;
				if (entity.Physics != null) entity.Physics.Enabled = false;
			}
			AntennaGraphEntry[] antennas = AntennaGraphService.CaptureFor(members);
			ClusterRespawnPoint[] respawns = RespawnRegistryService.CaptureFor(partition.Id);
			double[][] spheres = entities.Select(entity =>
			{
				VRageMath.BoundingSphereD volume = entity.PositionComp.WorldVolume;
				return new[] { volume.Center.X, volume.Center.Y, volume.Center.Z, volume.Radius };
			}).ToArray();
			long bytes = PartitionManager.EstimatedSnapshotBytes(partition);
			PendingSave save = QueueCapture(partition, "Final", waitForSlot: false, frozen: frozen)
				?? throw new InvalidOperationException("the save queue is full");
			m_offlining = new OfflineTransition
			{
				Partition = partition.Id,
				Generation = save.Generation,
				FreezeId = freezeId,
				Members = members,
				Frozen = frozen,
				Save = save,
				Antennas = antennas,
				RespawnPoints = respawns,
				MemberIds = entities.Select(entity => entity.EntityId).ToArray(),
				MemberSpheres = spheres,
				Npc = NpcCensusService.Rows(entities),
				Grids = grids,
				Sphere = partition.Sphere,
				EstimatedSnapshotBytes = bytes,
				Phase = OfflinePhase.Saving,
				StartedUtc = now,
			};
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offlining partition " + partition.Id + " generation " + save.Generation
				+ " sequence " + save.Sequence + " members " + members.Count + " antennas " + antennas.Length
				+ " respawnPoints " + respawns.Length + " radius " + partition.Sphere.Radius.ToString("F0", CultureInfo.InvariantCulture)
				+ " (frozen; Final queued)");
		}
		catch (Exception exception)
		{
			Thaw(frozen, freezeId);
			m_offlineRetryAfter[partition.Id] = now.AddSeconds(ClusterConfig.GetInt("offline.retrySeconds"));
			m_offlineFailed++;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline of partition " + partition.Id + " not started: "
				+ SecretRedactor.Redact(exception.Message));
		}
	}

	private void ProgressOffline()
	{
		OfflineTransition transition = m_offlining;
		DateTime now = DateTime.UtcNow;
		if (transition.Phase == OfflinePhase.Saving)
		{
			if (!transition.Save.Completed.IsSet)
			{
				if (now - transition.StartedUtc > OfflineSaveTimeout)
					AbortOffline(transition, "the Final was not catalogued within " + OfflineSaveTimeout.TotalSeconds + "s", failed: true);
				return;
			}
			if (!transition.Save.Succeeded)
			{
				AbortOffline(transition, "the Final failed: " + (transition.Save.LastError ?? "unknown"), failed: true);
				return;
			}
			transition.Phase = OfflinePhase.Posting;
			if (AbortOfflineIfClosing(transition)) return;
			StartOfflinePost(transition);
			return;
		}
		if (transition.Phase == OfflinePhase.Posting)
		{
			if (transition.Post == null)
			{
				if (now >= transition.NextPostUtc && !AbortOfflineIfClosing(transition)) StartOfflinePost(transition);
				return;
			}
			if (!transition.Post.IsCompleted) return;
			(OfflineAnswer answer, string detail) = transition.Post.IsCompletedSuccessfully
				? transition.Post.Result : (OfflineAnswer.Unavailable, transition.Post.Exception?.GetBaseException().Message);
			transition.Post = null;
			switch (answer)
			{
				case OfflineAnswer.Accepted:
					transition.Phase = OfflinePhase.Accepted;
					break;
				case OfflineAnswer.Refused:
					m_offlineRefused++;
					m_offlineLastRefusal = detail;
					AbortOffline(transition, "the gateway refused: " + detail, failed: false);
					return;
				default:
					// Not a verdict: the registry may or may not have committed (a lost reply). The offer is
					// idempotent, so ask again with the members still frozen - thawing here could leave a
					// committed offline row beside a live copy. A dead gateway stops the node's heartbeats
					// too; nothing else about the node works either until it is back.
					transition.NextPostUtc = now.AddSeconds(Math.Min(30, 2 * transition.PostAttempts));
					if (transition.PostAttempts == 1 || transition.PostAttempts % 10 == 0)
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline of partition " + transition.Partition
							+ " is waiting for the gateway (attempt " + transition.PostAttempts + "): " + SecretRedactor.Redact(detail ?? "unknown"));
					return;
			}
		}
		if (transition.Phase == OfflinePhase.Accepted) FinishOffline(transition);
	}

	private void StartOfflinePost(OfflineTransition transition)
	{
		transition.PostAttempts++;
		var body = new
		{
			transition.Partition,
			Node = m_node,
			Epoch = m_nodeEpoch,
			Generation = (long)transition.Save.Generation,
			Sequence = (long)transition.Save.Sequence,
			X = transition.Sphere.Center.X,
			Y = transition.Sphere.Center.Y,
			Z = transition.Sphere.Center.Z,
			transition.Sphere.Radius,
			transition.MemberSpheres,
			Members = transition.MemberIds,
			transition.EstimatedSnapshotBytes,
			Antennas = JsonSerializer.Serialize(transition.Antennas),
			RespawnPoints = JsonSerializer.Serialize(transition.RespawnPoints),
			transition.Npc,
			transition.Grids,
		};
		ulong partition = transition.Partition;
		transition.Post = Task.Run(() =>
		{
			try
			{
				// The gateway answers only once the row is durable, and shares that write between offers
				// (concurrent offers, T-0237: no spacing since the SQLite registry): wait for it rather than retry.
				using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(60));
				ClusterRuntime.AuthorizeRegistryClient(client);
				using JsonDocument row = PostJson(client, "/registry/partitions/offline", body);
				// Out of m_owned at once, off the game thread: the next heartbeat must not report a partition
				// the registry has just unowned, or the stale-ownership purge races the close below.
				lock (m_owned) m_owned.Remove(partition);
				return (OfflineAnswer.Accepted, (string)null);
			}
			catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
			{
				return (OfflineAnswer.Refused, exception.Message);
			}
			catch (Exception exception)
			{
				return (OfflineAnswer.Unavailable, exception.Message);
			}
		});
	}

	private void FinishOffline(OfflineTransition transition)
	{
		m_offlining = null;
		ulong partition = transition.Partition;
		// Hierarchy, not membership: a seated (offline, cryo) body must leave with its grid, never be
		// ejected into a live top-level stray (see CloseWithHierarchy, T-0213).
		CloseWithHierarchy(MyEntities.GetEntities().Where(entity => transition.Members.Contains(entity.EntityId) && !entity.MarkedForClose));
		foreach (long member in transition.Members) ClusterRuntime.ClearFrozen(member, transition.FreezeId);
		m_partitions.Remove(partition);
		ForgetOwnership(partition);
		m_state.TryRemove(partition, out _);
		m_offlineQuietSince.Remove(partition);
		m_onlineSince.Remove(partition);
		AntennaGraphService.RetainOffline(partition, transition.Antennas);
		Shared.Patches.MyProxyAntennaScanPatch.KeepThrottleWhenEmpty = true;
		m_offlineTaken++;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline partition " + partition + " generation " + transition.Save.Generation
			+ " sequence " + transition.Save.Sequence + " members " + transition.Members.Count + " antennas " + transition.Antennas.Length
			+ " respawnPoints " + transition.RespawnPoints.Length + " took "
			+ (DateTime.UtcNow - transition.StartedUtc).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)
			+ "ms: Final catalogued, gateway committed, entities closed; no node hosts it now");
	}

	/// <summary>
	/// A node that started closing (drain or stop) while a transition was saving does not post it: the gateway
	/// refuses offers from a Closed node (409, CM 2026-09-23 23:52:46 node-2 partition 195754), and the
	/// partition's content is saved by the close anyway. Stays online, not counted as a failure.
	/// </summary>
	private bool AbortOfflineIfClosing(OfflineTransition transition)
	{
		if (!m_closed && !m_fenced) return false;
		AbortOffline(transition, "the node is closing", failed: false);
		return true;
	}

	private void AbortOffline(OfflineTransition transition, string why, bool failed)
	{
		m_offlining = null;
		Thaw(transition.Frozen, transition.FreezeId);
		m_offlineQuietSince.Remove(transition.Partition);
		m_offlineRetryAfter[transition.Partition] = DateTime.UtcNow.AddSeconds(ClusterConfig.GetInt("offline.retrySeconds"));
		if (failed) m_offlineFailed++;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline of partition " + transition.Partition + " abandoned, partition stays online: "
			+ SecretRedactor.Redact(why));
	}

	private static void Thaw(Dictionary<long, DrainFrozenState> frozen, Guid freezeId)
	{
		foreach ((long id, DrainFrozenState state) in frozen)
		{
			if (MyEntities.TryGetEntityById(id, out MyEntity entity) && !entity.MarkedForClose)
			{
				entity.NeedsUpdate = state.NeedsUpdate;
				if (state.HadPhysics && entity.Physics != null) entity.Physics.Enabled = state.PhysicsEnabled;
			}
			ClusterRuntime.ClearFrozen(id, freezeId);
		}
	}

	public object OfflineStatus() => new
	{
		enabled = ClusterConfig.GetBool("offline.enabled"),
		graceSeconds = ClusterConfig.GetInt("offline.graceSeconds"),
		minOnlineSeconds = ClusterConfig.GetInt("offline.minOnlineSeconds"),
		quietMeters = ClusterConfig.GetInt("offline.quietMeters"),
		taken = m_offlineTaken,
		refused = m_offlineRefused,
		failed = m_offlineFailed,
		lastRefusal = m_offlineLastRefusal,
		inFlight = m_offlining == null ? null : new
		{
			partition = m_offlining.Partition,
			phase = m_offlining.Phase.ToString(),
			members = m_offlining.Members.Count,
			postAttempts = m_offlining.PostAttempts,
			ageMs = (long)(DateTime.UtcNow - m_offlining.StartedUtc).TotalMilliseconds,
		},
		blockers = m_offlineBlockers,
		quiet = m_offlineQuietSince.Select(x => new { partition = x.Key, seconds = (long)(DateTime.UtcNow - x.Value).TotalSeconds }).ToArray(),
	};
}
