using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Replication;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

public static class AntennaGraphService
{
	private const string Subsystem = "antennas";
	private static readonly object Sync = new object();
	private static readonly Dictionary<long, MyDataBroadcaster> Local = new Dictionary<long, MyDataBroadcaster>();
	private static readonly Dictionary<string, Report> Reports = new Dictionary<string, Report>(StringComparer.Ordinal);
	private static readonly Dictionary<long, Installed> InstalledProxies = new Dictionary<long, Installed>();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	/// <summary>The graph as last applied here: the merge on the authority, the last snapshot or delta on a replica.</summary>
	private static AntennaGraphEntry[] m_entries = Array.Empty<AntennaGraphEntry>();
	/// <summary>Revision of <see cref="m_entries"/>: the authority's publish counter, or on a replica the last one it applied.</summary>
	private static long m_revision;
	/// <summary>The FULL message for <see cref="m_entries"/>, built on demand for a resync and dropped whenever they change.</summary>
	private static string m_snapshotCache;
	private static readonly HashSet<long> m_unwritable = new HashSet<long>();
	/// <summary>The HUD params, per remote antenna and marker entity, the attached clients already know.</summary>
	private static readonly Dictionary<long, Dictionary<long, string>> PushedHud = new Dictionary<long, Dictionary<long, string>>();
	private static double m_nextReport;
	// T-0154: what a node last told the authority, so the next report can be the difference. Null
	// until the first (full) report went out; a full one goes again every FullReportMilliseconds
	// and after every link flap, since a request lost on the link leaves the authority on an older
	// base than the node's next delta assumes.
	private static Dictionary<long, AntennaGraphEntry> m_reported;
	private static long m_reportRevision;
	private static double m_nextForcedReport;
	private static double m_nextFullReport;
	private static long m_reportLinkEpoch;
	// T-0154 (2026-09-15 05:58): a report at most every ReportMilliseconds even while antennas move,
	// an unchanged set acknowledged with an empty delta every ForcedReportMilliseconds so the
	// authority keeps the node's report alive; the authority forgets a node's report after
	// ReportCutoffMilliseconds and publishes a changed merge at most every PublishMilliseconds.
	// Measured before the limits, 46 pilots on the 64-cluster lattice: 0.55 merged publishes/s x
	// 5.8 MB x 5 replicas = 16 MB/s out of the WA through the gateway, and wa-1 + gateway at
	// 14-15 GB RSS within 50 minutes. A relay set up to 5 s stale is the same budget the T-0138
	// proxy scan bands already accept. Since the delta protocol a report or publish carries only
	// what changed, so these budgets bound latency, not bytes.
	private const double ReportMilliseconds = 5000;
	private const double ForcedReportMilliseconds = 12000;
	// 60 s on the first deployment (2026-09-15 06:26): the five nodes' full reports were then ~50 of
	// the 52 MB the WA received in 6.5 min while the deltas averaged ~32 KB. The full report is a
	// fallback for a base the digest already guards, so it only has to bound how long a node's
	// report can stay stale after a lost request.
	private const double FullReportMilliseconds = 300000;
	private const double ReportCutoffMilliseconds = 30000;
	private const double PublishMilliseconds = 5000;
	private static double m_nextPublish;
	// Observability for /cluster/antenna-graph: how the protocol is doing, as counters, so a
	// mismatch storm or a delta that never shrinks is a reading and not a guess.
	private static long m_reportsFull, m_reportsDelta, m_reportsRejected, m_reportBytesFull, m_reportBytesDelta;
	private static long m_publishesFull, m_publishesDelta, m_publishBytesFull, m_publishBytesDelta, m_appliedDeltas, m_appliedFull, m_appliedSnapshots, m_staleDeltas;
	private static readonly Dictionary<string, double> m_mismatchLogged = new Dictionary<string, double>(StringComparer.Ordinal);

	public static void Install()
	{
		MyAntennaSystemPatch.Install();
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplyBroadcast);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ApplySnapshot);
		ClusterLaserLink.Peer = LaserPeer;
	}

	/// <summary>
	/// Synced handshake facts for a laser antenna hosted on another node, from the latest applied snapshot.
	/// Null for unknown ids, non-lasers, and antennas this node hosts itself (the real block answers there).
	/// </summary>
	private static ClusterLaserLink.PeerInfo? LaserPeer(long antennaEntityId)
	{
		AntennaGraphEntry[] entries;
		lock (Sync) entries = m_entries;
		foreach (AntennaGraphEntry entry in entries)
		{
			if (entry.AntennaEntityId != antennaEntityId) continue;
			if (!entry.IsLaser || entry.Origin == Node) return null;
			return new ClusterLaserLink.PeerInfo
			{
				TargetId = entry.LaserTargetId,
				State = entry.LaserState,
				Powered = entry.LaserPowered,
				RotationFinished = entry.LaserRotationFinished,
				HeadPos = new VRageMath.Vector3D(entry.LaserHeadX, entry.LaserHeadY, entry.LaserHeadZ)
			};
		}
		return null;
	}

	/// <summary>The latest applied cluster antenna snapshot (all origins).</summary>
	public static AntennaGraphEntry[] Entries() { lock (Sync) return m_entries; }

	public static void Register(MyDataBroadcaster broadcaster)
	{
		if (!IsLocal(broadcaster)) return;
		lock (Sync) Local[broadcaster.AntennaEntityId] = broadcaster;
		// T-0166: a broadcaster arriving with an imported partition is a handover the rest of the cluster
		// is waiting on (its proxies elsewhere point at the old origin); report it on the next update
		// instead of up to ReportMilliseconds later.
		m_nextReport = 0;
		// Vanilla deactivates the proxy for this antenna id here and reactivates it in UnregisterAntenna,
		// so the marker source swaps without either side closing. Logged because a client's HUD signal
		// marker rides on which of the two is live.
		if (InstalledProxies.ContainsKey(broadcaster.AntennaEntityId))
			Console.WriteLine("[ClusterPlugin] antenna_local_register antenna=" + broadcaster.AntennaEntityId + " proxyPresent=true");
	}

	public static void Unregister(MyDataBroadcaster broadcaster)
	{
		if (!IsLocal(broadcaster)) return;
		lock (Sync)
			if (Local.TryGetValue(broadcaster.AntennaEntityId, out MyDataBroadcaster current) && ReferenceEquals(current, broadcaster))
				Local.Remove(broadcaster.AntennaEntityId);
		// T-0166: the partition left (or the antenna died). The graph this node last applied may already
		// name the new origin - it was skipped only because the antenna was still local - so apply it again
		// on the next update (not inside the entity close that called us), and tell the authority now.
		m_reapply = true;
		m_nextReport = 0;
	}

	private static volatile bool m_reapply;
	// #1302: an apply that threw out of Update (a proxy whose network id the registry refused) is tried
	// again from the latest graph, at most once a second.
	private static bool m_retryApply;
	private static double m_retryApplyAt;
	private const double RetryApplyMilliseconds = 1000;
	private static long m_applyFailures;

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		if (m_reapply && !ClusterRuntime.IsWorldAuthority
			|| m_retryApply && ClusterRuntime.LocalMilliseconds >= m_retryApplyAt)
		{
			m_reapply = false;
			m_retryApply = false;
			AntennaGraphEntry[] latest;
			lock (Sync) latest = m_entries;
			TryApplyEntries(latest);
		}
		AntennaGraphEntry[] applied = null;
		lock (Sync)
			if (m_pointBackRevision != m_revision)
			{
				m_pointBackRevision = m_revision;
				applied = m_entries;
			}
		if (applied != null) PointBackLasers(applied);
		if (ClusterRuntime.LocalMilliseconds < m_nextReport) return;
		double now = ClusterRuntime.LocalMilliseconds;
		m_nextReport = now + (ClusterRuntime.IsWorldAuthority ? 1000 : ReportMilliseconds);
		AntennaGraphEntry[] local = CaptureLocal();
		if (ClusterRuntime.IsWorldAuthority)
		{
			lock (Sync) Reports[Node] = new Report(now, local.ToDictionary(x => x.AntennaEntityId), 0);
			PublishMerged();
			return;
		}
		var current = local.ToDictionary(x => x.AntennaEntityId);
		long linkEpoch = GlobalStateChannel.LinkEpoch;
		bool full = m_reported == null || now >= m_nextFullReport || linkEpoch != m_reportLinkEpoch;
		string report;
		long revision = m_reportRevision + 1;
		if (full) report = AntennaGraphCodec.WriteFull(local, revision);
		else
		{
			Diff(m_reported, current, out List<AntennaGraphEntry> upserts, out List<long> removed);
			if (upserts.Count == 0 && removed.Count == 0 && now < m_nextForcedReport) return;
			report = AntennaGraphCodec.WriteDelta(upserts, removed, m_reportRevision, revision, AntennaGraphCodec.Digest(local));
		}
		// A report the link refused is not one the authority has: keep the base until one goes out.
		if (!GlobalStateChannel.SendRequest(Subsystem, report)) return;
		m_reported = current;
		m_reportRevision = revision;
		m_reportLinkEpoch = linkEpoch;
		m_nextForcedReport = now + ForcedReportMilliseconds;
		if (full)
		{
			m_nextFullReport = now + FullReportMilliseconds;
			Interlocked.Increment(ref m_reportsFull);
			Interlocked.Add(ref m_reportBytesFull, report.Length);
		}
		else
		{
			Interlocked.Increment(ref m_reportsDelta);
			Interlocked.Add(ref m_reportBytesDelta, report.Length);
		}
	}

	// SE1-0235: the (caller, target) pairs of lasers linking (rot_Rec or later) with both ends online,
	// each acted on once. Vanilla's ConnectTo points the target back once, at the request; the graph is
	// a state repeated every publish, so a pair is acted on when first seen and forgotten once the caller
	// links elsewhere or goes idle. Remembering it keeps a player's later "idle" on the target, or a
	// second caller, from being undone every publish. A node restart forgets them.
	private static readonly HashSet<(long Caller, long Target)> m_pointBackSeen = new();
	private static long m_pointBackRevision;

	/// <summary>
	/// SE1-0235: the far half of vanilla's <c>ConnectTo</c> for links made on another node. A laser there,
	/// on a live (not offline) partition, newly linking to a laser hosted here gets this one pointed back
	/// through <see cref="MyLaserAntennaSeams.PointBack"/>, which takes only an idle one.
	/// Every node remembers every pair with both ends online, not only the target's node, so a laser
	/// handed over to another node is not pointed back again there. A pair with an offline end waits
	/// until both are online again.
	/// </summary>
	private static void PointBackLasers(AntennaGraphEntry[] entries)
	{
		var linking = new HashSet<(long, long)>();
		var online = new HashSet<long>(entries.Where(x => !OfflinePartitionService.IsOfflineOrigin(x.Origin)).Select(x => x.AntennaEntityId));
		foreach (AntennaGraphEntry caller in entries.OrderBy(x => x.AntennaEntityId))
		{
			if (!caller.IsLaser || caller.LaserTargetId == 0 || caller.LaserState < (byte)MyLaserAntenna.StateEnum.rot_Rec) continue;
			(long, long) pair = (caller.AntennaEntityId, caller.LaserTargetId);
			linking.Add(pair);
			MyDataBroadcaster target;
			lock (Sync) Local.TryGetValue(caller.LaserTargetId, out target);
			if (OfflinePartitionService.IsOfflineOrigin(caller.Origin)
				|| target == null && !online.Contains(caller.LaserTargetId) || !m_pointBackSeen.Add(pair)
				// A local caller is vanilla's, and one already connected was answered before this node saw it.
				|| caller.Origin == Node || caller.LaserState == (byte)MyLaserAntenna.StateEnum.connected
				|| target is not MyLaserBroadcaster { RealAntenna: { } laser }) continue;
			Console.WriteLine("[ClusterPlugin] laser_point_back laser=" + laser.EntityId + " caller=" + caller.AntennaEntityId
				+ " origin=" + caller.Origin + " state=" + laser.State + " pointed=" + MyLaserAntennaSeams.PointBack(laser, caller.AntennaEntityId));
		}
		// Forgotten only once the caller is seen linking elsewhere or not at all: a caller missing from the
		// graph (switched off, a node's report lapsing, an authority change) still asks the same.
		// ponytail: pairs of deleted lasers stay, a few bytes each; prune by age if that ever shows.
		var present = new HashSet<long>(entries.Select(x => x.AntennaEntityId));
		m_pointBackSeen.RemoveWhere(pair => present.Contains(pair.Caller) && !linking.Contains(pair));
	}

	/// <summary>Entries of <paramref name="next"/> that are new or differ from <paramref name="previous"/>, and ids that left.</summary>
	private static void Diff(Dictionary<long, AntennaGraphEntry> previous, Dictionary<long, AntennaGraphEntry> next,
		out List<AntennaGraphEntry> upserts, out List<long> removed)
	{
		upserts = new List<AntennaGraphEntry>();
		removed = new List<long>();
		foreach (KeyValuePair<long, AntennaGraphEntry> entry in next)
			if (!previous.TryGetValue(entry.Key, out AntennaGraphEntry was) || was != entry.Value) upserts.Add(entry.Value);
		foreach (long id in previous.Keys)
			if (!next.ContainsKey(id)) removed.Add(id);
	}

	public static void AuthorityChanged(long generation)
	{
		foreach (Installed proxy in InstalledProxies.Values) proxy.Entity.Close();
		InstalledProxies.Clear();
		lock (Sync)
		{
			Reports.Clear();
			m_entries = Array.Empty<AntennaGraphEntry>();
			m_revision = 0;
			m_pointBackRevision = 0;
			m_snapshotCache = null;
			m_reported = null;
			m_reportRevision = 0;
			m_nextForcedReport = 0;
			m_nextFullReport = 0;
			m_nextPublish = 0;
		}
	}

	private static AntennaGraphEntry[] CaptureLocal()
	{
		MyDataBroadcaster[] broadcasters;
		lock (Sync) broadcasters = Local.Values.ToArray();
		var entries = new List<AntennaGraphEntry>(broadcasters.Length);
		foreach (MyDataBroadcaster broadcaster in broadcasters)
		{
			if (!IsLocal(broadcaster) || broadcaster.Entity.MarkedForClose
				|| broadcaster is MyRadioBroadcaster radio && !radio.Enabled
				// A disabled laser antenna must drop out of the graph like a disabled radio antenna does:
				// its proxy would otherwise keep answering relay reachability on every other node long after
				// the link went dark.
				|| broadcaster is Sandbox.Game.Entities.Blocks.MyLaserBroadcaster { RealAntenna: { } realLaser }
					&& (!realLaser.Enabled || !realLaser.IsWorking)) continue;
			var builder = new MyObjectBuilder_ProxyAntenna { EntityId = 0 };
			broadcaster.InitProxyObjectBuilder(builder);
			string written;
			// This runs every frame from ClusterRuntime.Update, so an antenna the codec cannot write has to drop
			// out of the graph rather than throw out of the game loop and take the node down with it.
			try { written = AntennaGraphCodec.WriteBuilder(builder); }
			catch (Exception exception)
			{
				if (m_unwritable.Add(broadcaster.AntennaEntityId))
					Console.WriteLine($"[ClusterPlugin] antenna_graph antenna={broadcaster.AntennaEntityId} "
						+ $"skipped=true error={SecretRedactor.Redact(exception.Message)}");
				continue;
			}
			AntennaGraphEntry entry = new AntennaGraphEntry(Node, broadcaster.AntennaEntityId,
				written, GridEntityId(broadcaster));
			if (broadcaster is Sandbox.Game.Entities.Blocks.MyLaserBroadcaster { RealAntenna: { } laser })
			{
				ClusterLaserLink.PeerInfo snapshot = laser.ClusterLaserSnapshot;
				entry = entry with
				{
					IsLaser = true, LaserTargetId = snapshot.TargetId, LaserState = snapshot.State,
					LaserPowered = snapshot.Powered, LaserRotationFinished = snapshot.RotationFinished,
					LaserHeadX = snapshot.HeadPos.X, LaserHeadY = snapshot.HeadPos.Y, LaserHeadZ = snapshot.HeadPos.Z
				};
			}
			entries.Add(entry);
		}
		lock (Sync)
			if (m_retainedOffline.Count != 0)
			{
				double now = ClusterRuntime.LocalMilliseconds;
				// Once the authority publishes the offline/new origin, the bridge has done
				// its job. Continuing to report it would masquerade as a fresh live owner.
				var adopted = m_entries.Where(e => e.Origin != Node).Select(e => e.AntennaEntityId).ToHashSet();
				foreach (long expired in m_retainedOffline.Where(x => x.Value.Until <= now || adopted.Contains(x.Key)).Select(x => x.Key).ToArray())
					m_retainedOffline.Remove(expired);
				HashSet<long> live = entries.Select(x => x.AntennaEntityId).ToHashSet();
				foreach ((AntennaGraphEntry retained, double _) in m_retainedOffline.Values)
					if (!live.Contains(retained.AntennaEntityId) && !Local.ContainsKey(retained.AntennaEntityId)) entries.Add(retained);
			}
		return entries.OrderBy(x => x.AntennaEntityId).ToArray();
	}

	private static void ReceiveReport(string origin, string payload)
	{
		AntennaGraphMessage message = AntennaGraphCodec.ReadMessage(payload);
		// Validation only - nothing keeps the parse, so the shared cache turns the authority's
		// per-report re-validation of every antenna in the cluster into dictionary lookups.
		foreach (AntennaGraphEntry entry in message.Antennas ?? message.Upserts) AntennaGraphCodec.ReadBuilderShared(entry);
		double now = ClusterRuntime.LocalMilliseconds;
		lock (Sync)
		{
			Reports.TryGetValue(origin, out Report previous);
			Dictionary<long, AntennaGraphEntry> entries;
			if (message.IsFull)
			{
				entries = message.Antennas.Select(x => x with { Origin = origin }).ToDictionary(x => x.AntennaEntityId);
				long digest = AntennaGraphCodec.Digest(entries.Values);
				if (digest != message.Digest)
				{
					Reject(origin, previous, now, "digest revision=" + message.Revision + " expected=" + message.Digest + " got=" + digest);
					return;
				}
				Interlocked.Increment(ref m_reportsFull);
				Interlocked.Add(ref m_reportBytesFull, payload.Length);
			}
			else
			{
				if (previous == null || previous.Revision != message.Base)
				{
					// The node's base is not what this authority holds (a report lost on a link flap,
					// or a delta that overtook nothing). Its next full report, within
					// FullReportMilliseconds, replaces the state; until then what it last said stands
					// and stays alive, since a node that vanished from the merge for a minute would cost
					// every other node its proxies for that node's grids.
					Reject(origin, previous, now, "base=" + message.Base + " held=" + (previous?.Revision.ToString() ?? "none"));
					return;
				}
				try { entries = AntennaGraphCodec.ApplyDelta(previous.Antennas.Values, message, x => x with { Origin = origin }).ToDictionary(x => x.AntennaEntityId); }
				catch (InvalidOperationException exception)
				{
					Reject(origin, previous, now, exception.Message);
					return;
				}
				Interlocked.Increment(ref m_reportsDelta);
				Interlocked.Add(ref m_reportBytesDelta, payload.Length);
			}
			Reports[origin] = new Report(now, entries, message.Revision);
			// T-0166: an antenna this report claims that the published graph gives to another node is a
			// handover; every node showing it through a proxy needs the new origin before the publish
			// rate limit would allow it.
			foreach (AntennaGraphEntry published in m_entries)
				if (published.Origin != origin && entries.ContainsKey(published.AntennaEntityId))
				{
					m_nextPublish = 0;
					break;
				}
		}
	}

	/// <summary>A report that does not fit what the authority holds: keep the last good one alive, say so once a minute per node.</summary>
	private static void Reject(string origin, Report previous, double now, string why)
	{
		Interlocked.Increment(ref m_reportsRejected);
		if (previous != null) Reports[origin] = previous with { Received = now };
		if (m_mismatchLogged.TryGetValue(origin, out double last) && now - last < 60000) return;
		m_mismatchLogged[origin] = now;
		Console.WriteLine("[ClusterPlugin] antenna_report rejected=true origin=" + origin + " why=" + why);
	}

	private static void PublishMerged()
	{
		string payload;
		AntennaGraphEntry[] merged;
		bool full;
		lock (Sync)
		{
			double now = ClusterRuntime.LocalMilliseconds;
			if (now < m_nextPublish) return;
			double cutoff = now - ReportCutoffMilliseconds;
			foreach (string origin in Reports.Where(x => x.Value.Received < cutoff).Select(x => x.Key).ToArray()) Reports.Remove(origin);
			var next = new Dictionary<long, AntennaGraphEntry>();
			// A durable offline row is authoritative over the source's retained bridge
			// report. Only its admitted load target may replace it with a live report.
			var offline = OfflinePartitionService.AntennaEntries(out var loadTargets);
			foreach (AntennaGraphEntry entry in offline) next[entry.AntennaEntityId] = entry;
			// A later report wins an antenna id two nodes both claim (the moment of a handover): the
			// order of Reports, by receipt then by node, is what settles that, as it always has.
			foreach (AntennaGraphEntry entry in Reports.OrderBy(x => x.Value.Received).ThenBy(x => x.Key, StringComparer.Ordinal)
				.SelectMany(x => x.Value.Antennas.Values))
				if (!loadTargets.TryGetValue(entry.AntennaEntityId, out string target) || target == entry.Origin)
					next[entry.AntennaEntityId] = entry;
			Diff(m_entries.ToDictionary(x => x.AntennaEntityId), next, out List<AntennaGraphEntry> upserts, out List<long> removed);
			if (upserts.Count == 0 && removed.Count == 0) return;
			merged = next.Values.OrderBy(x => x.AntennaEntityId).ToArray();
			long revision = m_revision + 1;
			// A delta that rewrites most of the set is not smaller than the set.
			full = upserts.Count + removed.Count >= Math.Max(8, merged.Length / 2);
			payload = full ? AntennaGraphCodec.WriteFull(merged, revision)
				: AntennaGraphCodec.WriteDelta(upserts, removed, m_revision, revision, AntennaGraphCodec.Digest(merged));
			m_entries = merged;
			m_revision = revision;
			m_snapshotCache = full ? payload : null;
			m_nextPublish = now + PublishMilliseconds;
		}
		TryApplyEntries(merged);
		try
		{
			GlobalStateChannel.Publish(Subsystem, payload);
			Interlocked.Increment(ref full ? ref m_publishesFull : ref m_publishesDelta);
			Interlocked.Add(ref full ? ref m_publishBytesFull : ref m_publishBytesDelta, payload.Length);
		}
		catch (Exception exception) { Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=antennas error=" + SecretRedactor.Redact(exception.Message)); }
	}

	/// <summary>A replica's in-sequence Broadcast: the authority's delta on top of what this node applied last, or a full set.</summary>
	private static void ApplyBroadcast(string payload)
	{
		AntennaGraphMessage message = AntennaGraphCodec.ReadMessage(payload);
		if (message.IsFull)
		{
			Apply(message.Antennas, message.Revision, payload);
			Interlocked.Increment(ref m_appliedFull);
			return;
		}
		AntennaGraphEntry[] current;
		long revision;
		lock (Sync) { current = m_entries; revision = m_revision; }
		// A resync snapshot can be newer than the sequence it was labelled with (the provider runs
		// after the sequence is read), so the deltas that follow it may already be in: skip those.
		if (message.Revision <= revision) { Interlocked.Increment(ref m_staleDeltas); return; }
		if (message.Base != revision)
			throw new InvalidOperationException("Antenna graph delta base " + message.Base + " does not match revision " + revision);
		// The digest check inside ApplyDelta throws on a mismatch; GlobalStateChannel then requests
		// a resync, so a drifted replica is a full snapshot away from correct, never silently wrong.
		Apply(AntennaGraphCodec.ApplyDelta(current, message), message.Revision, null);
		Interlocked.Increment(ref m_appliedDeltas);
	}

	/// <summary>A resync snapshot (GlobalStateChannel Snapshot handler): the whole set, replacing whatever this node had.</summary>
	private static void ApplySnapshot(string payload)
	{
		AntennaGraphMessage message = AntennaGraphCodec.ReadMessage(payload);
		if (!message.IsFull) throw new InvalidOperationException("Antenna graph resync requires a full snapshot");
		Apply(message.Antennas, message.Revision, payload);
		Interlocked.Increment(ref m_appliedSnapshots);
	}

	private static void Apply(AntennaGraphEntry[] entries, long revision, string snapshot)
	{
		ApplyEntries(entries);
		lock (Sync)
		{
			m_entries = entries;
			m_revision = revision;
			m_snapshotCache = snapshot;
		}
	}

	/// <summary>
	/// ApplyEntries for the callers inside ClusterRuntime.Update, where nothing above catches: a proxy this
	/// node cannot create must not end the game loop. #1302: a draining node whose partitions left built
	/// the proxies for their antennas, the registry refused it a network-id block ("epoch N is Closed"),
	/// and the 409 escaped here and stopped the node mid-drain, so the graceful stop ended dirty.
	/// </summary>
	private static void TryApplyEntries(AntennaGraphEntry[] entries)
	{
		try { ApplyEntries(entries); }
		catch (Exception exception)
		{
			Interlocked.Increment(ref m_applyFailures);
			m_retryApply = true;
			m_retryApplyAt = ClusterRuntime.LocalMilliseconds + RetryApplyMilliseconds;
			Console.WriteLine("[ClusterPlugin] antenna_proxies apply_failed=true retryMs=" + RetryApplyMilliseconds
				+ " error=" + SecretRedactor.Redact(exception.Message));
		}
	}

	/// <summary>Installs, refreshes and closes this node's proxies so they mirror <paramref name="entries"/>; does not store them.</summary>
	private static void ApplyEntries(AntennaGraphEntry[] entries)
	{
		var desired = entries.Where(x => x.Origin != Node).ToDictionary(x => x.AntennaEntityId);
		var closed = new List<long>();
		var installed = new List<long>();
		var installedHud = new List<string>();
		foreach (long antennaId in InstalledProxies.Keys.Where(x => !desired.ContainsKey(x) || HasLocal(x)).ToArray())
		{
			InstalledProxies[antennaId].Entity.Close();
			InstalledProxies.Remove(antennaId);
			PushedHud.Remove(antennaId);
			closed.Add(antennaId);
		}
		foreach (AntennaGraphEntry entry in desired.Values)
		{
			if (HasLocal(entry.AntennaEntityId)) continue;
			if (InstalledProxies.TryGetValue(entry.AntennaEntityId, out Installed existing) && existing.Builder == entry.Builder) continue;
			// A remote antenna that MOVES reports a different builder every snapshot. Recreating the proxy
			// for that would destroy and rebuild its far-broadcaster replicable once a second, and every
			// attached client's HUD signal marker blinks with it. Moving the proxy it already has is what
			// the position state group is there for.
			// Refresh only READS the builder, so it takes the shared parse; the create path below gets a
			// fresh instance because MyEntities.CreateFromObjectBuilderAndAdd stamps an EntityId on it.
			if (existing != null && Refresh(existing.Entity, AntennaGraphCodec.ReadBuilderShared(entry)))
			{
				InstalledProxies[entry.AntennaEntityId] = new Installed(entry.Builder, existing.Entity);
				continue;
			}
			MyObjectBuilder_ProxyAntenna builder = AntennaGraphCodec.ReadBuilder(entry);
			if (existing != null) existing.Entity.Close();
			if (existing != null) InstalledProxies.Remove(entry.AntennaEntityId);
			MyEntity entity;
			try
			{
				entity = MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false)
					?? throw new InvalidOperationException("Cannot create antenna proxy");
			}
			catch (Exception)
			{
				// The throw can come from inside MyEntities.Add (OnAddedToScene drawing the network id), after
				// the proxy is already in MyEntities. Close it, or the retry adds a second proxy for this antenna.
				CloseOrphanProxies(entry.AntennaEntityId);
				throw;
			}
			// MyProxyAntenna.Init leaves Active false, which leaves its MyRadioBroadcaster disabled, which makes
			// MyAntennaSystem skip it when it collects the broadcasters a grid can reach. Vanilla only flips it in
			// RegisterAntenna, and only when the real antenna is absent locally - true here, but the flag has to be
			// set for the relay to exist at all, so set it explicitly rather than relying on that ordering.
			if (entity is MyProxyAntenna proxy) proxy.Active = true;
			// A fresh proxy carries its params into every client's far-broadcaster replicable, so that
			// is what the clients now know.
			PushedHud[entry.AntennaEntityId] = HudSignature(builder);
			InstalledProxies[entry.AntennaEntityId] = new Installed(entry.Builder, entity);
			installed.Add(entry.AntennaEntityId);
			installedHud.Add(entry.AntennaEntityId + ":" + (builder.HudParams?.Count ?? 0));
		}
		// Installing or closing a proxy replaces the broadcaster a client's HUD signal marker hangs off,
		// and the far-broadcaster replicable behind the old one dies with it. The server would otherwise
		// only notice on the outermost update layer's 960-frame timer, leaving the marker dark for ~16 s
		// after a partition arrives on this node and its proxy is dropped.
		if (closed.Count > 0 || installed.Count > 0)
		{
			// NOT part of the log line - see ClusterControlServer's copy of this note. The invalidation is
			// load-bearing HERE in a way it is not there: the object behind an antenna id really has been
			// replaced, and the in-range bookkeeping is keyed by id, so every client holding that id has to
			// be told. It stays blanket. It must not stay inside a log argument.
			int invalidatedClients = MyCharacterReplicableSeams.InvalidateBroadcasterDependencies();
			Console.WriteLine("[ClusterPlugin] antenna_proxies closed=[" + string.Join(",", closed) + "] installed=["
				+ string.Join(",", installed) + "] hud=[" + string.Join(",", installedHud) + "] invalidatedClients=" + invalidatedClients);
		}
	}

	/// <summary>Closes every proxy for <paramref name="antennaId"/> that this service does not track (#1302).</summary>
	private static void CloseOrphanProxies(long antennaId)
	{
		foreach (MyProxyAntenna orphan in MyEntities.GetEntities().OfType<MyProxyAntenna>()
			.Where(x => x.AntennaEntityId == antennaId && !x.MarkedForClose).ToArray())
		{
			if (InstalledProxies.TryGetValue(antennaId, out Installed tracked) && ReferenceEquals(tracked.Entity, orphan)) continue;
			orphan.Close();
		}
	}

	/// <summary>
	/// True when the source block can reach a grid living on another node. Reuses vanilla's own relay evaluation:
	/// the broadcasters reachable from the source include the proxy antennas installed for remote grids, so a
	/// genuine relay chain - not mere co-existence in the cluster - is what answers this.
	/// </summary>
	public static bool CanReachGrid(MyCubeBlock source, long gridEntityId)
	{
		if (source?.CubeGrid == null || gridEntityId == 0) return false;
		long[] antennas;
		lock (Sync)
			antennas = m_entries.Where(entry => entry.GridEntityId == gridEntityId)
				.Select(entry => entry.AntennaEntityId).ToArray();
		if (antennas.Length == 0 || MyAntennaSystem.Static == null) return false;
		return Relayed(source).Any(broadcaster => antennas.Contains(broadcaster.AntennaEntityId));
	}

	/// <summary>
	/// Explains a reachability answer. The refusal reasons are otherwise indistinguishable from the caller's side:
	/// a send that returns false looks the same whether the address is unknown, the owner unfriendly, or no relay
	/// path exists.
	/// </summary>
	public static object DescribeReach(MyCubeBlock source, long gridEntityId)
	{
		AntennaGraphEntry[] entries;
		lock (Sync) entries = m_entries.ToArray();
		long[] antennas = entries.Where(entry => entry.GridEntityId == gridEntityId)
			.Select(entry => entry.AntennaEntityId).ToArray();
		HashSet<MyDataBroadcaster> reachable = Relayed(source);
		return new
		{
			sourceGrid = source?.CubeGrid?.EntityId ?? 0,
			sourceOwner = source?.OwnerId ?? 0,
			targetGrid = gridEntityId,
			graphEntries = entries.Select(entry => new { entry.Origin, entry.AntennaEntityId, entry.GridEntityId }).ToArray(),
			targetAntennas = antennas,
			reachableBroadcasters = reachable.Select(broadcaster => broadcaster.AntennaEntityId).ToArray(),
			installedProxies = InstalledProxies.Select(proxy => new
			{
				antenna = proxy.Key,
				active = (proxy.Value.Entity as MyProxyAntenna)?.Active,
				enabled = ((proxy.Value.Entity as MyProxyAntenna)?.Broadcaster as MyRadioBroadcaster)?.Enabled,
				radius = ((proxy.Value.Entity as MyProxyAntenna)?.Broadcaster as MyRadioBroadcaster)?.BroadcastRadius,
				closed = proxy.Value.Entity.Closed,
				distance = source?.CubeGrid == null ? 0d
					: Vector3D.Distance(proxy.Value.Entity.PositionComp.GetPosition(), source.CubeGrid.PositionComp.GetPosition())
			}).ToArray(),
			reachable = reachable.Any(broadcaster => antennas.Contains(broadcaster.AntennaEntityId))
		};
	}

	/// <summary>
	/// Vanilla's relay walk from the source's grid. <c>MyIGCSystemSessionComponent.BroadcasterProvider</c> is
	/// deliberately not used: it is <c>GetCubeGridGroupBroadcasters</c>, which returns the antennas *on* the source
	/// construct, not the ones it can reach - vanilla then asks <c>CheckConnection</c> per pair, which needs the
	/// target block loaded and so cannot answer for a remote addressee. <c>mutual: false</c> matches the
	/// <c>ConnectionProvider</c> vanilla installs.
	/// </summary>
	private static HashSet<MyDataBroadcaster> Relayed(MyCubeBlock source)
	{
		if (source?.CubeGrid == null || MyAntennaSystem.Static == null) return new HashSet<MyDataBroadcaster>();
		// Without an output set of its own the answer is a scratch set of the antenna system, and scripts on worker threads
		// get here at the same time
		return MyAntennaSystem.Static.GetAllRelayedBroadcasters(source.CubeGrid, source.OwnerId, mutual: false,
			output: new HashSet<MyDataBroadcaster>());
	}

	/// <summary>
	/// Applies a fresh report to an installed radio proxy in place. Returns false for anything the proxy
	/// cannot absorb without being rebuilt (a laser, a character/block change, a closed entity), which
	/// leaves the caller on the recreate path.
	/// </summary>
	private static bool Refresh(MyEntity entity, MyObjectBuilder_ProxyAntenna builder)
	{
		if (entity is not MyProxyAntenna proxy || proxy.Closed || proxy.MarkedForClose) return false;
		if (builder.IsLaser || proxy.IsCharacter != builder.IsCharacter) return false;
		if (proxy.Broadcaster is not MyRadioBroadcaster radio) return false;
		proxy.PositionComp.SetPosition(builder.Position);
		radio.BroadcastRadius = builder.BroadcastRadius;
		radio.IsBeacon = builder.IsBeacon;
		proxy.ChangeOwner(builder.Owner, builder.Share);
		if (builder.HudParams != null)
		{
			// ChangeHudParams only fixes the server's copy. A client's HUD marker text comes from the
			// params its far-broadcaster replicable was created with, and vanilla pushes a later change
			// with MyDataBroadcaster.UpdateHudParams - its CustomNameChanged hook does exactly this. Without
			// the push, an antenna's "HUD Text" edited on the owning node never reaches a client watching
			// from another node: the marker keeps the name the proxy was installed with. Position is left
			// out of the comparison, since a moving antenna reports a new one every snapshot and travels
			// through the broadcaster's own position instead.
			Dictionary<long, string> next = HudSignature(builder);
			PushedHud.TryGetValue(builder.AntennaEntityId, out Dictionary<long, string> pushed);
			// A retail client MERGES what is pushed - MyProxyAntenna.ChangeHudParams never removes an entry -
			// so a marker that disappeared, a beacon on the remote grid switched off, can only be cleared by
			// giving the client a new replicable. Refuse the in-place refresh and let the caller recreate.
			if (pushed != null && pushed.Keys.Any(marker => !next.ContainsKey(marker))) return false;
			proxy.ChangeHudParams(builder.HudParams);
			if (pushed == null || next.Count != pushed.Count
				|| next.Any(entry => !pushed.TryGetValue(entry.Key, out string was) || was != entry.Value))
			{
				PushedHud[builder.AntennaEntityId] = next;
				radio.UpdateHudParams(proxy);
			}
		}
		proxy.Info = new MyAntennaSystem.BroadcasterInfo { EntityId = builder.InfoEntityId, Name = builder.InfoName };
		proxy.HasRemoteControl = builder.HasRemote;
		proxy.MainRemoteControlOwner = builder.MainRemoteOwner;
		proxy.MainRemoteControlId = builder.MainRemoteId;
		proxy.MainRemoteControlSharing = builder.MainRemoteSharing;
		return true;
	}

	/// <summary>
	/// The cluster-wide graph as this node sees it, with what a HUD marker is drawn from: whose antenna it is,
	/// whether it broadcasts, its radius and the marker text. A disabled antenna or beacon leaves the graph
	/// entirely, so an entry that should have gone dark is visible here as one that is still listed.
	/// </summary>
	public static object Describe()
	{
		AntennaGraphEntry[] entries;
		object[] reports;
		lock (Sync)
		{
			entries = m_entries;
			// Reports is written on the link's receive thread; read it under the lock.
			reports = Reports.OrderBy(x => x.Key, StringComparer.Ordinal)
				.Select(x => (object)new { origin = x.Key, revision = x.Value.Revision, antennas = x.Value.Antennas.Count,
					ageMilliseconds = ClusterRuntime.LocalMilliseconds - x.Value.Received }).ToArray();
		}
		return new
		{
			node = Node,
			entries = entries.Select(entry =>
			{
				MyObjectBuilder_ProxyAntenna builder = null;
				// Read-only, and this endpoint runs on the GAME THREAD - a fresh parse of every antenna in
				// the cluster stalled it for ~16 ms per call (measured 2026-08-22 while profiling w22).
				try { builder = AntennaGraphCodec.ReadBuilderShared(entry); } catch (Exception) { }
				return (object)new
				{
					entry.Origin,
					entry.AntennaEntityId,
					entry.GridEntityId,
					entry.IsLaser,
					beacon = builder?.IsBeacon,
					radius = builder?.BroadcastRadius,
					owner = builder?.Owner,
					infoName = builder?.InfoName,
					hudTexts = builder?.HudParams?.Select(hud => hud?.Text).ToArray(),
					proxy = InstalledProxies.ContainsKey(entry.AntennaEntityId),
					pushedHud = PushedHud.TryGetValue(entry.AntennaEntityId, out Dictionary<long, string> pushed)
						? pushed.OrderBy(marker => marker.Key).Select(marker => marker.Key + "=" + marker.Value).ToArray() : null,
				};
			}).ToArray(),
			localAntennas = Local.Keys.OrderBy(id => id).ToArray(),
			installedProxies = InstalledProxies.Keys.OrderBy(id => id).ToArray(),
			// T-0154 delta protocol: what went over the link and whether it fitted.
			protocol = new
			{
				revision = m_revision,
				reportRevision = m_reportRevision,
				reportsFull = Interlocked.Read(ref m_reportsFull), reportsDelta = Interlocked.Read(ref m_reportsDelta),
				reportsRejected = Interlocked.Read(ref m_reportsRejected),
				reportBytesFull = Interlocked.Read(ref m_reportBytesFull), reportBytesDelta = Interlocked.Read(ref m_reportBytesDelta),
				publishesFull = Interlocked.Read(ref m_publishesFull), publishesDelta = Interlocked.Read(ref m_publishesDelta),
				publishBytesFull = Interlocked.Read(ref m_publishBytesFull), publishBytesDelta = Interlocked.Read(ref m_publishBytesDelta),
				appliedDeltas = Interlocked.Read(ref m_appliedDeltas), appliedFull = Interlocked.Read(ref m_appliedFull),
				appliedSnapshots = Interlocked.Read(ref m_appliedSnapshots),
				staleDeltas = Interlocked.Read(ref m_staleDeltas),
				applyFailures = Interlocked.Read(ref m_applyFailures),
				// #1290: whether moving antennas are patched or re-parsed (ReadBuilderShared), since start.
				fastPath = AntennaGraphCodec.FastPathTotals(),
				reports,
			},
		};
	}

	/// <summary>
	/// Everything a client's HUD marker takes from a proxy's params, position excluded. Later entries win on
	/// a repeated entity id, the way <see cref="MyProxyAntenna.ChangeHudParams"/> overwrites its map.
	/// </summary>
	private static Dictionary<long, string> HudSignature(MyObjectBuilder_ProxyAntenna builder)
	{
		var texts = new Dictionary<long, string>();
		if (builder.HudParams == null) return texts;
		foreach (MyObjectBuilder_HudEntityParams hud in builder.HudParams)
			if (hud != null)
				texts[hud.EntityId] = $"{(int)hud.FlagsEnum}|{hud.Owner}|{(int)hud.Share}|{hud.Text}";
		return texts;
	}

	/// <summary>
	/// Enables or disables every radio and laser antenna on a grid, server-side.
	/// </summary>
	/// <remarks>
	/// Operational A/B tooling, not a test hook: it is what POST /cluster/igc-test-antenna does
	/// (NodeAPI.md), and ~/ws/se/se1/cluster-regression/bin/antenna-ab drives the regression cluster with it to
	/// measure what the antenna graph costs with a broadcaster off. The IGC relay-chain lane uses the
	/// same route to prove its endpoints were reachable only through the middle relay - the same
	/// vanilla relay evaluation that answers reachability must then answer "no relay path". It moved
	/// here from IgcTestHarness when that harness went to Test/ClusterTestPlugin (T-0171).
	/// </remarks>
	public static object SetAntennaEnabled(long gridEntityId, bool enabled)
	{
		if (!MyEntities.TryGetEntityById(gridEntityId, out MyEntity entity) || entity is not MyCubeGrid grid)
			throw new KeyNotFoundException("No grid " + gridEntityId);
		MyTerminalBlock[] antennas = grid.GetFatBlocks()
			.Where(block => block is MyRadioAntenna or MyLaserAntenna)
			.Cast<MyTerminalBlock>().ToArray();
		if (antennas.Length == 0) throw new InvalidOperationException("Grid " + gridEntityId + " has no antenna");
		foreach (MyTerminalBlock antenna in antennas)
			((MyFunctionalBlock)antenna).Enabled = enabled;
		return new { grid = gridEntityId, enabled, antennas = antennas.Length };
	}

	/// <summary>
	/// T-0193 requirement 6: the antenna state a partition goes offline with - exactly the entries this
	/// node's report carries for those grids at the moment of the freeze.
	/// </summary>
	internal static AntennaGraphEntry[] CaptureFor(HashSet<long> gridEntityIds)
		=> CaptureLocal().Where(entry => gridEntityIds.Contains(entry.GridEntityId)).ToArray();

	/// <summary>Entries of a partition this node just took offline, still reported until the World Authority
	/// has had time to pick the same entries up from the offline table (see OfflinePartitionService): the
	/// authority's merge then swaps the origin without the entry ever leaving the graph, so no node's proxy
	/// - and no client's HUD marker - blinks across the switch.</summary>
	private static readonly Dictionary<long, (AntennaGraphEntry Entry, double Until)> m_retainedOffline = new Dictionary<long, (AntennaGraphEntry, double)>();
	private const double RetainedOfflineMilliseconds = 20000;

	internal static void RetainOffline(ulong partition, AntennaGraphEntry[] entries)
	{
		if (entries == null || entries.Length == 0) return;
		double until = ClusterRuntime.LocalMilliseconds + RetainedOfflineMilliseconds;
		lock (Sync)
			foreach (AntennaGraphEntry entry in entries)
				m_retainedOffline[entry.AntennaEntityId] = (entry, until);
		m_nextReport = 0;
	}

	/// <summary>The authority re-merges now instead of at the publish rate limit (the offline table changed).</summary>
	internal static void RequestPublish()
	{
		lock (Sync) m_nextPublish = 0;
	}

	private static long GridEntityId(MyDataBroadcaster broadcaster)
		=> (broadcaster.Entity as MyCubeBlock)?.CubeGrid?.EntityId ?? broadcaster.Entity?.GetTopMostParent()?.EntityId ?? 0;

	private static bool IsLocal(MyDataBroadcaster broadcaster)
		=> broadcaster?.Entity != null && broadcaster.Entity.EntityId == broadcaster.AntennaEntityId;
	private static bool HasLocal(long antennaId) { lock (Sync) return Local.ContainsKey(antennaId); }

	/// <summary>The FULL message for a replica's resync, at the authority's current revision.</summary>
	private static string Snapshot()
	{
		lock (Sync) return m_snapshotCache ??= AntennaGraphCodec.WriteFull(m_entries, m_revision);
	}

	private sealed record Report(double Received, Dictionary<long, AntennaGraphEntry> Antennas, long Revision);
	private sealed record Installed(string Builder, MyEntity Entity);
}
