using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;

namespace ClusterPlugin;

internal sealed partial class PartitionPersistence
{
	private const string WorldEnvironmentVariable = "CLUSTER_WORLD";
	private const string SavesEnvironmentVariable = "CLUSTER_MPS_PATHS";
	private const string DataEnvironmentVariable = "CLUSTER_DATA_ROOT";
	private const string GatewayEnvironmentVariable = "CLUSTER_GATEWAY_REGISTRY";
	private const string NodeEnvironmentVariable = "CLUSTER_NODE_ID";
	private const string EndpointEnvironmentVariable = "CLUSTER_NODE_ENDPOINT";
	private const string CatalogSlotEnvironmentVariable = "CLUSTER_NODE_EPOCH";
	private const string ControlPortEnvironmentVariable = "CLUSTER_CONTROL_PORT";
	private const string WarmRecoveryEnvironmentVariable = "CLUSTER_WARM_RECOVERY";
	private const string MaxPartitionsEnvironmentVariable = "CLUSTER_MAX_PARTITIONS";
	private readonly PartitionManager m_partitions;
	// Partitions whose periodic capture has already failed once; the first failure logs the
	// full stack, repeats log only the message (the loop can run for minutes on a broken member).
	private readonly HashSet<ulong> s_capturesFailed = new HashSet<ulong>();
	// Startup-fenced partitions still waiting for their claim; probed against the registry once
	// a minute so a stale local incarnation is purged in minutes, not whenever a save happens.
	private readonly ConcurrentDictionary<ulong, byte> m_startupFenced = new ConcurrentDictionary<ulong, byte>();
	private double m_nextFencedProbe;
	private DateTime m_discardHeldUntil;   // bench seam DiscardDelayMs (#1286)
	private readonly BlockingCollection<PendingSave> m_saves = new BlockingCollection<PendingSave>(2);
	private readonly ConcurrentQueue<RecoveryClaim> m_recoveries = new ConcurrentQueue<RecoveryClaim>();
	private readonly ConcurrentQueue<RecoveryClaim> m_failedRecoveries = new ConcurrentQueue<RecoveryClaim>();
	// #1286 (F41): recoveries the game thread handed back for a census check on the replication thread, due at Due.
	private readonly ConcurrentQueue<(RecoveryClaim Recovery, DateTime Due, bool Fresh)> m_censusChecks
		= new ConcurrentQueue<(RecoveryClaim Recovery, DateTime Due, bool Fresh)>();
	// A claim is registry ownership from the moment it is granted, but it only reaches m_owned once
	// the game thread finishes the load - and only m_owned rode the heartbeat. With a 3s lease TTL,
	// any load slower than that let the fresh claim expire mid-flight; the registry re-offered the
	// partition and the same node claimed and loaded it AGAIN (node-3 2026-08-11: partition 25
	// claimed at generations 18 and 19 one second apart, world content loaded twice, save state
	// left divergent). Claims pending load renew their leases here.
	private readonly ConcurrentDictionary<ulong, uint> m_claimedPending = new ConcurrentDictionary<ulong, uint>();
	// 1 while the game thread is inside LoadRecovery. The heartbeat reports queued + in-flight loads
	// so the registry knows frozen simulation frames mean "busy importing", not "hung" - the stall
	// fence killed the recovering node on every boot before this (node-3, 2026-08-11).
	private int m_recoveryLoadInFlight;
	private readonly Dictionary<ulong, int> m_recoveryFailureCounts = new Dictionary<ulong, int>();
	private const int RecoveryFailureQuarantineThreshold = 3;
	// T-0388: recoveries that loaded 0 members out of saves that held content (all of it screened as live elsewhere or
	// stale), at their recovered generation. The game thread adds; RegistryLoop releases them once their empty Final
	// is catalogued (ReleaseEmptyRecoveries).
	private readonly ConcurrentDictionary<ulong, (uint Generation, DateTime Since)> m_emptyRecoveries
		= new ConcurrentDictionary<ulong, (uint, DateTime)>();
	/// <summary>Registry thread only (RegistryLoop): when each empty recovery's "release waits" line was last written.</summary>
	private readonly Dictionary<ulong, DateTime> m_emptyRecoveryLogged = new Dictionary<ulong, DateTime>();
	private readonly ConcurrentQueue<(ulong Partition, uint Generation)> m_lostOwnership = new ConcurrentQueue<(ulong, uint)>();
	private readonly HashSet<ulong> m_pendingLostOwnership = new HashSet<ulong>();
	private readonly ConcurrentDictionary<ulong, int> m_fencedRegistrations = new ConcurrentDictionary<ulong, int>();
	// T-0155: a fenced registration leaves the partition uncatalogued, so the periodic scheduler picked
	// it again the next second - ~45 identical 409s per seed partition on every --reuse start while the
	// Gateway recovered it elsewhere. The partition waits this long before its next capture instead.
	private readonly ConcurrentDictionary<ulong, DateTime> m_fencedRegistrationRetry = new ConcurrentDictionary<ulong, DateTime>();
	private const double FencedRegistrationRetrySeconds = 5;
	private readonly ConcurrentDictionary<ulong, byte> m_fencedKeptLogged = new ConcurrentDictionary<ulong, byte>();
	private readonly ConcurrentQueue<ulong> m_fencedOrphans = new ConcurrentQueue<ulong>();
	// The member ids each assigned save was loaded with, kept until the partition's claim verdict
	// arrives: the census misses entities still in the parallel init pipeline, so any purge that
	// enumerates members through Evaluate() silently leaves a big grid alive (the 11k-block seed
	// copy of 'Pirate Base Large mk.2' took 110 s to initialize and became partition 671 opposite
	// its real incarnation in 672, 2026-08-16 - the second duplicate-incarnation wedge in one day).
	private readonly ConcurrentDictionary<ulong, long[]> m_assignedMembers = new ConcurrentDictionary<ulong, long[]>();
	/// <summary>Local-clock second each fenced orphan first reached the purge, for the init-wait deadline.</summary>
	private readonly ConcurrentDictionary<ulong, double> m_fencedOrphanSince = new ConcurrentDictionary<ulong, double>();
	// Entities instantiated from an assigned save whose partition this node has NOT (yet) been
	// confirmed to own. PartitionManager.Evaluate must not let the unassigned-member pass mint a
	// fresh REGISTERED partition around them - that is how a fence-escaped seed grid becomes a
	// durable duplicate. Static because the manager has no reference to this instance.
	private static readonly ConcurrentDictionary<long, byte> s_fenceTainted = new ConcurrentDictionary<long, byte>();

	internal static bool IsFenceTainted(long entityId)
		=> !s_fenceTainted.IsEmpty && s_fenceTainted.ContainsKey(entityId);

	// An entity acquired through a legitimate path - a handover import or a recovery load - is
	// confirmed cluster content even if an assigned save also carried (and tainted) it: without
	// this, the taint would outlive the stale copy's dedup and hide the REAL entity from the
	// topology sweep the moment its layout dissolves.
	internal static void UntaintEntities(IEnumerable<long> entityIds)
	{
		if (s_fenceTainted.IsEmpty) return;
		foreach (long id in entityIds)
			s_fenceTainted.TryRemove(id, out _);
	}

	private void UntaintAssigned(ulong partition)
	{
		if (m_assignedMembers.TryGetValue(partition, out long[] members))
			foreach (long id in members)
				s_fenceTainted.TryRemove(id, out _);
	}
	private readonly ConcurrentDictionary<ulong, State> m_state = new ConcurrentDictionary<ulong, State>();
	private readonly SaveSchedulePolicy m_schedule = new SaveSchedulePolicy();
	private readonly Dictionary<ulong, uint> m_owned = new Dictionary<ulong, uint>();
	private readonly ConcurrentDictionary<ulong, byte> m_recoveryCandidates = new ConcurrentDictionary<ulong, byte>();
	private readonly Dictionary<ulong, (uint Generation, uint Sequence, string Path)> m_assignedRecoveries = new Dictionary<ulong, (uint, uint, string)>();
	private readonly string m_root;
	/// <summary>T-0340: where this node's save files lie - its host's shared store (&lt;runRoot&gt;/saves/&lt;host&gt;) when
	/// the registry said so at registration (ClusterRuntime.HostStore), else m_root/saves as always. With the store every file goes in without replacing another
	/// node's (ClusterProtocol.HostSaveStore) and the registry records the host, not this node, as its holder.</summary>
	private readonly string m_saveRoot;
	private readonly bool m_hostStore;
	private readonly string m_gateway;
	private readonly string m_node;
	private readonly string m_endpoint;
	private readonly int m_catalogSlot;
	private readonly string m_controlEndpoint;
	private readonly bool m_warmRecovery;
	private readonly int m_maxPartitions;
	private readonly long m_maxMembers;
	/// <summary>Sum of loaded member counts, refreshed on the game thread for the heartbeat.</summary>
	private volatile int m_projectedLoad;
	/// <summary>Per-partition member counts for heartbeat Ownerships; swapped whole on the game thread.</summary>
	private volatile Dictionary<ulong, int> m_partitionMembers = new Dictionary<ulong, int>();
	private long m_nodeEpoch;
	private volatile bool m_registryReady;
	private volatile bool m_fenced;
	private volatile bool m_everOwned;
	private volatile bool m_disconnectForFence;
	private volatile bool m_closed;
	/// <summary>T-0237: the Closed state comes from a CLUSTER STOP (the gateway's shutdown is draining every
	/// node), not a rotation: the drain then takes every partition offline instead of releasing it, but for one a
	/// plugin keeps online (SE-904).</summary>
	private volatile bool m_clusterShutdown;
	private volatile bool m_exitRequested;

	/// <summary>
	/// T-0347: a cluster stop with the registry unreachable asked this node for a LOCAL stop (RequestLocalStop). The
	/// graceful path cannot run - it is the registry that closes the nodes, takes their releases and hears them Empty -
	/// and the stop used to SIGKILL every node at once, so the world fell back to the last periodic saves. In a local
	/// stop the node writes a Final save of every partition it owns to its own save store, releases nothing, and exits.
	/// A Final the registry cannot be told about is journaled (UnregisteredFinals); the next start reports the journal
	/// before any recovery claim (ReportUnregisteredFinals) and the registry catalogues what is still the newest state.
	/// </summary>
	private volatile bool m_localStop;
	private long m_localStopDeadlineTicks;
	private bool m_localStopStarted, m_localStopFinished;
	/// <summary>Partitions the local stop has queued a Final for (or given up on). Game thread only.</summary>
	private readonly HashSet<ulong> m_localStopQueued = new HashSet<ulong>();
	private readonly List<PendingSave> m_localStopSaves = new List<PendingSave>();
	private int m_localStopCaptureFailures;
	/// <summary>Partitions whose Final the save worker completed during the local stop, whoever queued it (a drain or an
	/// offline transition that was in flight when the registry went away has frozen its partition and queued its own;
	/// the worker was retrying it, and the local stop journals it instead).</summary>
	private readonly ConcurrentDictionary<ulong, byte> m_localFinals = new ConcurrentDictionary<ulong, byte>();

	/// <summary>Set on the registry thread when this node first learns it is Closed; consumed on the
	/// GAME thread by Update(), which is the only place the transfer tables may be read. See
	/// WriteStopRecord.</summary>
	private volatile bool m_stopRecordPending;

	/// <summary>Game-time seconds at which the next last-state snapshot is due.</summary>
	private double m_nextSnapshot;

	/// <summary>How often the last-state snapshot is refreshed. The trade is staleness after a
	/// SIGKILL against write volume; 15 s is far finer than the minutes-long drains it exists to
	/// explain.</summary>
	private static readonly double SnapshotSeconds = double.TryParse(
		Environment.GetEnvironmentVariable("CLUSTER_LASTSTATE_SECONDS"),
		NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 15.0;
	private readonly Dictionary<ulong, DrainSave> m_drainSaves = new Dictionary<ulong, DrainSave>();
	/// <summary>#1390: the partitions whose wreckage this drain has cleaned already.</summary>
	private readonly HashSet<ulong> m_wreckCleaned = new HashSet<ulong>();
	// Partition -> when its "release deferred" line was last written. A route that
	// stays stale keeps a node parked for its whole 20-minute grace, and the drain
	// retries every second, so an unthrottled line there is ~1200 identical entries.
	private readonly Dictionary<ulong, DateTime> m_deferralLogged = new Dictionary<ulong, DateTime>();
	// Only RegistryLoop appends and reads; the node exits right after the linger consumes it.
	private readonly List<(ulong Partition, uint Generation, uint Sequence)> m_registeredFinals = new List<(ulong, uint, uint)>();
	private long m_forceDrainTicks = DateTimeOffset.MaxValue.UtcTicks;
	private bool m_forceDrainLogged;

	/// <summary>Gates the T-0028 "drain blocked" line to once per force-drain window.</summary>
	private bool m_blockedOwnedLogged;
	private DateTime m_nextCapture;
	private DateTime m_recoveryAfter;
	private DateTime m_genericRecoveryAfter;
	//: T-0099. Consecutive recovery-claim materialization failures, and the escalating
	//: backoff they impose. The claim loop runs at 1 Hz and NOTHING slowed it on failure, so
	//: a partition whose newest save had not replicated yet was re-claimed every second and
	//: every claim burned a GENERATION: 19 claims in 10 s driving partitions 30021/30564 from
	//: generation 10 to 20/19 while the newest save on disk was generation 9 (w29 18:16), and
	//: 9 claims burning 8 generations on partition 32266 during the 07:17 rotation wave on the
	//: current build. Both self-resolved, so the cost is generation space and log noise rather
	//: than lost content - but the registry range is finite and this is pure waste.
	private int m_recoveryFailures;
	private bool m_loaded;
	private volatile PartitionManager.ClientPartition[] m_clientPartitions = Array.Empty<PartitionManager.ClientPartition>();
	private volatile bool m_writing;
	private double? m_simulationRatioOverride;
	private int m_writeDelayMilliseconds;
	private double m_lastSimulationRatio = 1;
	private SaveFailure m_lastSaveFailure;
	private readonly bool m_concealment;
	private readonly double m_concealGraceSeconds;
	private readonly double m_preWakeMeters;
	private readonly Dictionary<ulong, ConcealedPartition> m_concealed = new Dictionary<ulong, ConcealedPartition>();
	private readonly HashSet<ulong> m_concealable = new HashSet<ulong>();

	/// <summary>
	/// Entity ids of concealable CONTENT this node has seen (boot-catalog concealed saves and every
	/// reconcealed Final). Reported to the World Authority, which merges every node's contribution
	/// into the cluster-wide set - wake-merge churn can hand an absorbed beacon to ANOTHER node, and
	/// only the shared set lets the new owner recognize it as concealable. The sweep gates on the
	/// MERGED set: a partition whose members are all concealable content is eligible regardless of
	/// its current id or owner; a passing player or foreign grid poisons it until it leaves.
	/// </summary>
	private readonly HashSet<long> m_concealableMembers = new HashSet<long>();

	public long[] ConcealableMembers() { lock (m_concealed) return m_concealableMembers.ToArray(); }
	private readonly Dictionary<ulong, DateTime> m_lastPlayerNearUtc = new Dictionary<ulong, DateTime>();
	private readonly List<(ulong Partition, PendingSave Save)> m_reconcealing = new List<(ulong, PendingSave)>();
	private readonly List<QuarantinedPartition> m_quarantined = new List<QuarantinedPartition>();

	private sealed class QuarantinedPartition
	{
		public string Path;
		public ulong Partition;
		public string Error;
	}

	private sealed class ConcealedPartition
	{
		public ulong Id;
		public string Path;
		public uint Generation;
		public uint Sequence;
		public VRageMath.BoundingSphereD Sphere;
		public int Members;
	}

	public PartitionPersistence(PartitionManager partitions)
	{
		m_partitions = partitions;
		m_partitions.IsStartupFenced = id => m_startupFenced.ContainsKey(id);
		string world = Environment.GetEnvironmentVariable(WorldEnvironmentVariable);
		string assigned = Environment.GetEnvironmentVariable(SavesEnvironmentVariable);
		string data = Environment.GetEnvironmentVariable(DataEnvironmentVariable);
		m_root = !string.IsNullOrWhiteSpace(data) ? Path.Combine(Path.GetFullPath(data), "cluster")
			: !string.IsNullOrWhiteSpace(world) ? Path.Combine(Path.GetFullPath(world), "cluster")
			: !string.IsNullOrWhiteSpace(assigned) ? Path.Combine(AppContext.BaseDirectory, "cluster") : null;
		// T-0340: the registry's registration answer (ClusterRuntime.ConfigureNetworkIds, before this is constructed)
		// said whether this node keeps its saves in its host's store.
		m_hostStore = ClusterRuntime.HostStore;
		m_saveRoot = m_hostStore ? ClusterRuntime.SaveRoot() : m_root == null ? null : Path.Combine(m_root, "saves");
		m_gateway = Environment.GetEnvironmentVariable(GatewayEnvironmentVariable)?.TrimEnd('/');
		m_node = Environment.GetEnvironmentVariable(NodeEnvironmentVariable);
		m_endpoint = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
		int.TryParse(Environment.GetEnvironmentVariable(CatalogSlotEnvironmentVariable), out m_catalogSlot);
		// T-0321: the same advertised endpoint the node registers (ClusterRuntime.AdvertisedControlEndpoint).
		m_controlEndpoint = int.TryParse(Environment.GetEnvironmentVariable(ControlPortEnvironmentVariable), out int controlPort)
			? ClusterProtocol.ClusterNet.AdvertisedControlEndpoint(Environment.GetEnvironmentVariable("CLUSTER_NODE_CONTROL_ENDPOINT"),
				Environment.GetEnvironmentVariable("CLUSTER_CONTROL_BIND"), controlPort)
			: Environment.GetEnvironmentVariable("CLUSTER_NODE_CONTROL_ENDPOINT");
		m_warmRecovery = Environment.GetEnvironmentVariable(WarmRecoveryEnvironmentVariable) == "1";
		if (m_warmRecovery && !RegistryEnabled) throw new InvalidOperationException("Warm recovery requires the cluster registry");
		// P2-REC host-loss triage: an operator-set ceiling on how many partitions this node
		// will hold; generic recovery stops claiming at the ceiling so the registry's
		// priority order decides which parts of a lost host's world come back first.
		m_maxPartitions = int.TryParse(Environment.GetEnvironmentVariable(MaxPartitionsEnvironmentVariable), out int maxPartitions) && maxPartitions > 0
			? maxPartitions : 0;
		// Projected-load ceiling: the registry's recovery auto-pick skips partitions whose
		// member weight would push this node past it (0 = unlimited).
		m_maxMembers = long.TryParse(Environment.GetEnvironmentVariable("CLUSTER_MAX_MEMBERS"), out long maxMembers) && maxMembers > 0
			? maxMembers : 0;
		m_concealment = Environment.GetEnvironmentVariable("CLUSTER_CONCEALMENT") == "1";
		m_concealGraceSeconds = double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_CONCEAL_GRACE_SECONDS"),
			NumberStyles.Float, CultureInfo.InvariantCulture, out double grace) && grace > 0 ? grace : 60;
		m_preWakeMeters = double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_CONCEAL_PREWAKE_METERS"),
			NumberStyles.Float, CultureInfo.InvariantCulture, out double preWake) && preWake > 0 ? preWake : 2000;
		if (RegistryEnabled)
		{
			m_partitions.SetAllocator(AllocatePartitionIds);
			m_partitions.Owned = id => { lock (m_owned) return m_owned.ContainsKey(id); };
			m_partitions.RegistryPending = m_apply.IsPending;
			m_partitions.ClaimOnly = m_apply.IsClaimOnly;
			m_partitions.LayoutsCreated += ClaimCreatedLayouts;
			m_partitions.TopologyChanged += ApplyTopology;
			if (RegistryApplyAsync) new Thread(RegistryApplyLoop) { IsBackground = true, Name = "ClusterRegistryApply" }.Start();
		}
		if (m_root != null)
		{
			new Thread(WriteLoop) { IsBackground = true, Name = "ClusterPartitionSave" }.Start();
		}
	}

	public void Update()
	{
		if (m_stopRecordPending) WriteStopRecord();
		WriteLastStateSnapshot();
		// T-0354: before the first sweep of this update, so it sees the layouts the registry has answered for unlocked.
		CompleteRegistryApplies();
		// Until registration completes, no entity can be legitimate cluster content (recovery claims
		// start 5 s after m_registryReady, imports need registration): everything that appears is the
		// node's own world file streaming in, and one sweep at first Update proved too early - the
		// entities materialize over the following frames. Sweep every frame of that window.
		if (RegistryEnabled && !m_registryReady) DropWorldFileEntities();
		if (!m_loaded)
		{
			LoadAssigned();
			m_loaded = true;
			if (RegistryEnabled)
			{
				new Thread(RegisterNodeAndClaims) { IsBackground = true, Name = "ClusterRegistry" }.Start();
			}
		}
		UpdateConcealment();
		IReadOnlyCollection<PartitionManager.Partition> loadSnapshot = m_partitions.Evaluate();
		m_projectedLoad = loadSnapshot.Sum(x => x.Members.Count);
		m_partitionMembers = loadSnapshot.ToDictionary(x => x.Id, x => x.Members.Count);
		if (m_disconnectForFence)
		{
			m_disconnectForFence = false;
			if (MyMultiplayer.Static != null)
				foreach (ulong client in m_partitions.ClientPartitions(m_partitions.Evaluate()).Select(x => x.Client).Distinct())
					MyMultiplayer.Static.DisconnectClient(client);
		}
		while (m_recoveries.TryDequeue(out RecoveryClaim recovery))
		{
			// T-0297: the heartbeat dropped this claim (the registry no longer holds it for this node at this
			// generation) while it waited in the queue. Loading it would duplicate the new claimant's load.
			if (DropLostPending
				&& (!m_claimedPending.TryGetValue(recovery.Partition, out uint pendingGeneration) || pendingGeneration != recovery.Generation))
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery skipped: claim " + recovery.Partition + " generation "
					+ recovery.Generation + " was lost before its load (T-0297)");
				continue;
			}
			Volatile.Write(ref m_recoveryLoadInFlight, 1);
			try
			{
				int saved = LoadRecovery(recovery);
				if (saved == DeferredLoad) continue;   // #1286: back on the replication thread for a census check, still claimed
				lock (m_owned) m_owned[recovery.Partition] = recovery.Generation;
				m_claimedPending.TryRemove(recovery.Partition, out _);
				lock (m_recoveryFailureCounts) m_recoveryFailureCounts.Remove(recovery.Partition);
				// Peek, twice over: Get() runs the topology sweep (the P1-SAV-07 hazard, see
				// PartitionManager.Peek), and its post-sweep count includes co-located neighbors the
				// sweep just merged in - this line claims what recovery LOADED, so it must report the
				// adopted member set, not the census a merge already rewrote (P1-REC-05b read a 1-entity
				// save and logged "2 members" off the beacon next door).
				PartitionManager.Partition loadedPartition = m_partitions.Peek(recovery.Partition);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery loaded partition " + recovery.Partition
					+ " generation " + recovery.Generation + " with " + loadedPartition.Members.Count + " members ["
					+ string.Join(",", loadedPartition.Members) + "]");
				// T-0388: an empty layout is dropped by the next sweep, so this partition is never reported again; the
				// registry kept its row on this node until the drain's memberless release (321894 on node-282, 01:11 to
				// 03:16 on 2026-10-03). Release it as soon as its empty Final is catalogued instead.
				if (MarkEmptyRecovery(ClusterConfig.GetBool("recovery.releaseEmpty"), saved, loadedPartition.Members.Count))
				{
					m_emptyRecoveries[recovery.Partition] = (recovery.Generation, DateTime.UtcNow);
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery of partition " + recovery.Partition + " generation "
						+ recovery.Generation + " is empty: all " + saved + " saved entities are live elsewhere or stale; it is released"
						+ " unowned once its empty Final is catalogued (T-0388)");
				}
				// T-0254: a recovery load recreates the entities like a handover import, and breaks them like one
				// too (P517 on node-5 05:39:27 2026-09-26: 60 DoDisconnects in the next 180 s).
				ClientLayerRefresh.NoteImport(loadedPartition.Members);   // T-0300: offer the new grids to clients now, not at the next 16 s layer refresh
				MySessionComponentTrashPatch.ProtectArrivals(loadedPartition.Members,
					MySessionComponentTrashPatch.RecoveryGraceSeconds); // T-0254: the displaced players' way back
			}
			catch (Exception exception)
			{
				int failures;
				lock (m_recoveryFailureCounts)
					failures = m_recoveryFailureCounts[recovery.Partition]
						= m_recoveryFailureCounts.GetValueOrDefault(recovery.Partition) + 1;
				if (m_warmRecovery && failures >= RecoveryFailureQuarantineThreshold)
				{
					// P2-REC-03 warm branch: content that keeps failing to load is poison, not a
					// transient - a warm candidate re-added forever would reclaim and fail in a loop
					// with the partition never becoming repairable. Quarantine it like the cold path:
					// visible on /cluster/quarantine, repairable in place; the claim is still released
					// so another holder can serve the partition if one exists.
					string staged = m_assignedRecoveries.TryGetValue(recovery.Partition, out var assigned)
						? assigned.Path : "<recovery claim " + recovery.Partition + ">";
					Quarantine(staged, exception, recovery.Partition);
					m_failedRecoveries.Enqueue(recovery);
					continue;
				}
				if (m_warmRecovery) m_recoveryCandidates.TryAdd(recovery.Partition, 0);
				m_failedRecoveries.Enqueue(recovery);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: recovery load failed for partition " + recovery.Partition
					+ " generation " + recovery.Generation + "; releasing claim for retry: " + SecretRedactor.Redact(exception.ToString()));
			}
		}
		Volatile.Write(ref m_recoveryLoadInFlight, 0);
		// Bench seam only (#1286): the first lost ownership waits DiscardDelayMs here (and a fenced node's exit with it),
		// so the doomed copies stay in this node's census while a peer's recovery census asks. Production never sets it.
		if (DiscardDelayMs > 0 && !m_lostOwnership.IsEmpty && m_discardHeldUntil == default)
		{
			m_discardHeldUntil = DateTime.UtcNow.AddMilliseconds(DiscardDelayMs);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] lost ownership discard test delay " + DiscardDelayMs + " ms (#1286)");
		}
		while (DateTime.UtcNow >= m_discardHeldUntil && m_lostOwnership.TryDequeue(out (ulong Partition, uint Generation) lost))
		{
			// The loss was detected against ownership as it stood at the heartbeat; by the time the
			// game thread drains it, the partition may already be on its way back in (fast ping-pong
			// handover: seen in P3-HND-02, the return import landed 2 ms before this purge and lost
			// its freshly imported entities to it, wedging the commit). A re-owned partition
			// (generation moved on) or one mid-import keeps its entities; only the stale
			// incarnation's bookkeeping is dropped.
			bool stale;
			lock (m_owned) stale = m_owned.TryGetValue(lost.Partition, out uint generation) && generation == lost.Generation;
			// T-0354: an input of a topology change whose answer is still out is absent from the heartbeat reply exactly
			// when the registry applied the change (the reply to the apply was lost). Its members live on in the
			// changed layouts - for a merge under this very id. The answer settles the ownership, not this purge.
			bool importing = ClusterControlServer.HasLiveInboundTransfer(lost.Partition) || m_apply.IsPending(lost.Partition);
			if (stale && !importing)
			{
				PartitionManager.Partition local = m_partitions.Evaluate().FirstOrDefault(x => x.Id == lost.Partition);
				if (local != null)
				{
					CloseWithHierarchy(MyEntities.GetEntities().Where(x => local.Members.Contains(x.EntityId)));
					foreach (long entity in local.Members) ClusterRuntime.ClearFrozen(entity);
				}
			}
			if (stale && !m_apply.IsPending(lost.Partition))
			{
				ForgetOwnership(lost.Partition);
				lock (m_drainSaves) m_drainSaves.Remove(lost.Partition);
			}
			lock (m_owned) m_pendingLostOwnership.Remove(lost.Partition);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] " + (stale && !importing
				? "discarded stale local ownership: " + lost.Partition
				: "skipped stale-ownership discard (partition re-owned or re-importing): " + lost.Partition));
		}
		// A startup-fenced partition whose registry row is retired or owned elsewhere is a live
		// duplicate incarnation, but the save-driven three-strike detection only advances when a
		// capture attempts to register - and a quiet anchored partition can go tens of minutes
		// between captures (partition 24, world-8 2026-08-17: seven seed grids lived duplicated on
		// two nodes while its single fenced registration waited for company). Probe the registry
		// once a minute off the game thread instead; three consecutive positives hand the
		// partition to the fenced-orphan purge below, same as the save path.
		if (!m_startupFenced.IsEmpty && m_registryReady
				&& ClusterRuntime.LocalMilliseconds / 1000.0 >= m_nextFencedProbe)
		{
			m_nextFencedProbe = ClusterRuntime.LocalMilliseconds / 1000.0 + 60;
			ulong[] probe = m_startupFenced.Keys.Where(id =>
			{
				lock (m_owned) return !m_owned.ContainsKey(id);
			}).ToArray();
			if (probe.Length != 0) System.Threading.Tasks.Task.Run(() =>
			{
				foreach (ulong id in probe)
				{
					if (!FencedPartitionOwnedElsewhere(id))
					{
						m_fencedRegistrations.TryRemove(id, out _);
						continue;
					}
					if (m_fencedRegistrations.AddOrUpdate(id, 1, (_, strikes) => strikes + 1) >= 3
							&& m_startupFenced.TryRemove(id, out _))
					{
						m_fencedOrphans.Enqueue(id);
						MyLog.Default?.WriteLineAndConsole(
							"[ClusterPlugin] startup-fenced partition confirmed stale by registry probe: " + id);
					}
				}
			});
		}
		List<ulong> stillInitializing = null;
		while (m_fencedOrphans.TryDequeue(out ulong orphan))
		{
			// The fenced-registration counterpart of the purge above: a startup-fenced partition we
			// never re-owned this session whose saves the registry keeps refusing belongs to another
			// node now. Close the leftover incarnation so the world holds one copy of its entities.
			bool orphanOwned;
			lock (m_owned) orphanOwned = m_owned.ContainsKey(orphan);
			if (orphanOwned || ClusterControlServer.HasLiveInboundTransfer(orphan))
			{
				// Re-owned or re-importing: the loaded content is legitimate after all.
				UntaintAssigned(orphan);
				m_assignedMembers.TryRemove(orphan, out _);
				m_fencedOrphanSince.TryRemove(orphan, out _);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] skipped fenced-orphan purge (partition re-owned or re-importing): " + orphan);
				continue;
			}
			// The census (Evaluate) misses entities still inside the parallel init pipeline, and a
			// purge that walks only the census leaves a big grid alive to be minted into a fresh
			// registered partition the moment it surfaces (671/672, 2026-08-16). The member list the
			// save was LOADED with is authoritative; wait out the init window before closing so the
			// close reaches every copy, but never longer than the membership pin would have held.
			long[] recorded = m_assignedMembers.TryGetValue(orphan, out long[] fromLoad)
				? fromLoad : Array.Empty<long>();
			double nowSeconds = ClusterRuntime.LocalMilliseconds / 1000.0;
			double since = m_fencedOrphanSince.GetOrAdd(orphan, nowSeconds);
			var resolved = new List<VRage.Game.Entity.MyEntity>();
			HashSet<long> reacquired = null;
			bool initializing = false;
			foreach (long id in recorded)
			{
				// Every recorded id was fence-tainted when the assigned save loaded it (see the taint
				// write below LoadAssigned); it is untainted ONLY by UntaintEntities, i.e. when the
				// entity is re-acquired through a legitimate path - a handover import or a recovery
				// load. Handovers PRESERVE entity ids and arrive under a DIFFERENT partition id, so
				// the re-owned/re-importing guard above - keyed on THIS partition - cannot see them.
				// w23, 2026-08-24: the live base was imported onto node-2 as partition 4259 at
				// 02:36:16 and closed 39 s later by the purge of seed partitions 4 and 5, because
				// TryGetEntityById resolves at PURGE time and hands back the live grid. Close only
				// what is still tainted; an untainted id belongs to somebody else now.
				// T-0392: an id whose sliced import activation is open is being re-acquired right now
				// (it is untainted only at ImportFinish): treat it as re-acquired, never close it.
				if (!IsFenceTainted(id) || ImportActivationGuard.IsOpenFor(GuardUser.FencePurge, id))
				{
					(reacquired ??= new HashSet<long>()).Add(id);
					continue;
				}
				if (MyEntities.TryGetEntityById(id, out VRage.Game.Entity.MyEntity entity))
				{
					if (entity.InScene) resolved.Add(entity);
					else initializing = true;
				}
			}
			if (initializing && nowSeconds - since < PartitionMembership.PinSeconds)
			{
				(stillInitializing ??= new List<ulong>()).Add(orphan);
				continue;
			}
			PartitionManager.Partition leftover = m_partitions.Evaluate().FirstOrDefault(x => x.Id == orphan);
			if (leftover != null)
			{
				// Same exclusion as above, and for the same reason - a re-acquired entity must not be
				// closed just because this stale partition's layout still lists it. Deliberately NOT a
				// blanket taint check: an entity reaching the census sweep may never have been tainted
				// at all, and skipping those would undo the 671/672 fix this sweep exists for.
				foreach (VRage.Game.Entity.MyEntity entity in MyEntities.GetEntities()
						.Where(x => leftover.Members.Contains(x.EntityId)))
					if (!resolved.Contains(entity) && reacquired?.Contains(entity.EntityId) != true)
						resolved.Add(entity);
				foreach (long entity in leftover.Members)
					if (reacquired?.Contains(entity) != true) ClusterRuntime.ClearFrozen(entity);
			}
			CloseWithHierarchy(resolved);
			// Only for what was actually closed: a re-acquired entity is live and its real partition
			// owns its frozen state.
			foreach (long entity in recorded)
				if (reacquired?.Contains(entity) != true) ClusterRuntime.ClearFrozen(entity);
			// Drop the layout too: a purged partition that keeps a pinned ghost layout re-adopts
			// the entity into the topology as soon as the pin resolves.
			m_partitions.Remove(orphan);
			ForgetOwnership(orphan);
			m_state.TryRemove(orphan, out _);
			lock (m_drainSaves) m_drainSaves.Remove(orphan);
			UntaintAssigned(orphan);
			m_assignedMembers.TryRemove(orphan, out _);
			m_fencedOrphanSince.TryRemove(orphan, out _);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] purged stale fenced partition: " + orphan
				+ " (" + resolved.Count + " leftover entities closed, " + recorded.Length
				+ " recorded at load, " + (reacquired?.Count ?? 0)
				+ " skipped as re-acquired; registry ownership is elsewhere)");
			// Deliberately its own line: this is the w23 base-loss counterfactual. A non-zero count
			// here is content the old code would have destroyed, so it must be greppable on its own
			// rather than buried in the summary above.
			if (reacquired != null)
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] fenced-orphan purge spared "
					+ reacquired.Count + " re-acquired entities of partition " + orphan
					+ ": " + string.Join(",", reacquired));
		}
		if (stillInitializing != null)
			foreach (ulong orphan in stillInitializing)
				m_fencedOrphans.Enqueue(orphan);
		if (m_exitRequested && DateTime.UtcNow >= m_discardHeldUntil)   // bench seam DiscardDelayMs holds the fenced exit too
		{
			m_exitRequested = false;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] empty node termination requested");
			// ExitThreadSafe only ends the game loop; foreground threads (control listener, transport
			// pollers) have kept a "cleanly" exited process alive before. The watchdog is the backstop.
			ClusterRuntime.ArmExitWatchdog("empty node termination");
			ClusterRuntime.ExitProcess(0, "node Empty");
			return;
		}
		// T-0347: nothing below can make progress without the registry, and the Finals are all that is left to do.
		if (m_localStop)
		{
			UpdateLocalStop();
			return;
		}
		// T-0193: before the drain gate, so a transition already in flight when the node is closed still
		// reaches its verdict (a committed offline row closes, anything else thaws and the drain takes it).
		UpdateOffline();
		if (m_closed)
		{
			UpdateDrain();
			return;
		}
		if (m_root == null || m_closed || m_fenced || RegistryEnabled && !m_registryReady || DateTime.UtcNow < m_nextCapture)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		m_nextCapture = now.AddSeconds(1);
		m_lastSimulationRatio = m_simulationRatioOverride ?? Sync.ServerSimulationRatio;
		m_schedule.Sample(m_lastSimulationRatio, 1);

		IReadOnlyCollection<PartitionManager.Partition> partitions = m_partitions.Evaluate();
		lock (m_owned)
			m_clientPartitions = m_partitions.ClientPartitions(partitions)
				.Select(x => new PartitionManager.ClientPartition(x.Client, x.Partition, x.Entity,
					m_owned.TryGetValue(x.Partition, out uint generation) ? generation : 0)).ToArray();
		HashSet<ulong> playerPartitions = m_clientPartitions.Select(client => client.Partition).ToHashSet();
		PartitionManager.Partition partition = partitions
			.Where(candidate => !candidate.Members.Any(ClusterRuntime.IsFrozen))
			// A partition with members still in the parallel entity-init pipeline would capture an
			// incomplete save (the serializer silently skips absent entities); wait for the pin to
			// drain or expire (PartitionMembership.PinSeconds bounds it).
			.Where(candidate => !candidate.HasPending)
			// T-0354: not while its topology apply or claim is unanswered - a save of a merged layout under the kept
			// id, registered before the registry has the merge, would hold the other input's members a second time.
			.Where(candidate => !m_apply.IsPending(candidate.Id))
			.Where(candidate => !m_fencedRegistrationRetry.TryGetValue(candidate.Id, out DateTime retry) || retry <= now)
			.Where(candidate => !m_state.TryGetValue(candidate.Id, out State state) || !state.Pending
				&& (state.LastCataloguedUtc == default || state.LastCataloguedUtc.AddSeconds(m_schedule.EffectiveIntervalSeconds(playerPartitions.Contains(candidate.Id)) - 2) <= now))
			.OrderByDescending(candidate => playerPartitions.Contains(candidate.Id))
			.ThenBy(candidate => m_state.TryGetValue(candidate.Id, out State state) ? state.LastCataloguedUtc : DateTime.MinValue)
			.FirstOrDefault();
		if (partition == null || m_saves.Count == m_saves.BoundedCapacity)
		{
			return;
		}

		State current = GetState(partition.Id);
		double lagSeconds = current.LastCataloguedUtc == default ? 0 : (now - current.LastCataloguedUtc).TotalSeconds;
		double estimate = current.CaptureMilliseconds <= 0 ? 1 : current.CaptureMilliseconds;
		if (MyFpsManager.FrameTimeAvg + estimate > 1000d / 60d * 1.5 && !m_schedule.IsLagAlarm(lagSeconds, playerPartitions.Contains(partition.Id))) return;
		try
		{
			QueueCapture(partition, "Periodic", waitForSlot: false);
		}
		catch (Exception e)
		{
			// A member mid-teardown can fail its object-builder capture (2026-08-16: a departing
			// client's character lost its broadcaster component and GetObjectBuilder NRE'd,
			// which took the whole node down through the update loop). The capture mutates no
			// state before QueueCaptured, so skipping this cycle is safe; the schedule retries.
			// The first failure per partition logs the stack - a persistent loop here means a
			// durably broken member, and the message alone does not say which serializer it is.
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: periodic capture failed for partition "
				+ partition.Id + ": " + (s_capturesFailed.Add(partition.Id) ? e.ToString() : e.Message));
		}
	}

	public string SaveStatusJson()
	{
		DateTime now = DateTime.UtcNow;
		HashSet<ulong> players = m_clientPartitions.Select(client => client.Partition).ToHashSet();
		return JsonSerializer.Serialize(new
		{
			adaptiveLevel = m_schedule.Level,
			simulationRatio = m_lastSimulationRatio,
			queueDepth = m_saves.Count,
			writing = m_writing,
			lastSaveFailure = m_lastSaveFailure,
			partitions = m_state.OrderBy(pair => pair.Key).Select(pair => new
			{
				partition = pair.Key,
				generation = pair.Value.Generation,
				sequence = pair.Value.Sequence,
				pending = pair.Value.Pending,
				effectiveIntervalMs = m_schedule.EffectiveIntervalSeconds(players.Contains(pair.Key)) * 1000,
				lastCataloguedClusterTime = pair.Value.LastCataloguedClusterTime,
				saveLagMs = pair.Value.LastCataloguedUtc == default ? 0 : (long)(now - pair.Value.LastCataloguedUtc).TotalMilliseconds,
				captureMs = pair.Value.CaptureMilliseconds,
				serializeMs = pair.Value.SerializeMilliseconds,
				writeMs = pair.Value.WriteMilliseconds,
				lagAlarm = pair.Value.LastCataloguedUtc != default && m_schedule.IsLagAlarm((now - pair.Value.LastCataloguedUtc).TotalSeconds, players.Contains(pair.Key))
			}).ToArray()
		});
	}

    internal Dictionary<ulong, long> PluginOwnershipSnapshot()
    {
        lock (m_owned) return m_owned.ToDictionary(p => p.Key, p => (long)p.Value);
    }

	public uint OwnedGeneration(ulong partition)
	{
		lock (m_owned) return m_owned.TryGetValue(partition, out uint generation) ? generation : 0;
	}

	/// <summary>T-0307 self-test: the live topology lock must read exactly this ownership.</summary>
	internal (bool Registry, ulong[] Owned) OwnershipSnapshot()
	{
		lock (m_owned) return (RegistryEnabled, m_owned.Keys.ToArray());
	}

	/// <summary>The local partition holding <paramref name="entityId"/> (0 when none); game thread (T-0256 plugin views).</summary>
	internal ulong Holder(long entityId) => m_partitions.Holder(entityId);

	internal bool ClaimPending(ulong partition) => m_apply.IsPending(partition);

	/// <summary>Game thread, as <see cref="m_offlining"/>.</summary>
	internal bool Offlining(ulong partition) => m_offlining?.Partition == partition;

	/// <summary>#1390: the node's simulation ratio of the last 1 s sample (the save-test override when set).</summary>
	internal double SimulationRatio => m_lastSimulationRatio;

	public bool OwnsPartition(ulong partition)
	{
		lock (m_owned) return m_owned.ContainsKey(partition);
	}

	public void ConfigureSaveTest(double? simulationRatio, int writeDelayMilliseconds, double sampleSeconds = 0)
	{
		if (simulationRatio.HasValue && (simulationRatio.Value < 0 || simulationRatio.Value > 2)) throw new ArgumentOutOfRangeException(nameof(simulationRatio));
		if (writeDelayMilliseconds < 0 || writeDelayMilliseconds > 60000) throw new ArgumentOutOfRangeException(nameof(writeDelayMilliseconds));
		if (sampleSeconds < 0 || sampleSeconds > 3600 || sampleSeconds != 0 && !simulationRatio.HasValue) throw new ArgumentOutOfRangeException(nameof(sampleSeconds));
		m_simulationRatioOverride = simulationRatio;
		m_writeDelayMilliseconds = writeDelayMilliseconds;
		if (sampleSeconds != 0) m_schedule.Sample(simulationRatio.Value, sampleSeconds);
	}

	public bool QueueTestCapture(ulong partitionId)
	{
		// The scheduler skips a partition whose save is still pending; the test trigger must be
		// the same benign no-op, not a 500 - the INF-02 `save` trigger races the scheduler's own
		// periodic capture under load. Peek, not Get: the caller enumerated ids from a fresh
		// Evaluate, and re-sweeping per id can retire one mid-iteration.
		if (GetState(partitionId).Pending || m_apply.IsPending(partitionId)) return false;
		PartitionManager.Partition peeked = m_partitions.Peek(partitionId);
		if (peeked.HasPending) return false;   // members still initializing: same benign no-op
		return QueueCapture(peeked, "Periodic", waitForSlot: false) != null;
	}

	public uint GetGeneration(ulong partitionId) => GetState(partitionId).Generation;
	public void SetGeneration(ulong partitionId, uint generation)
	{
		State state = GetState(partitionId);
		state.Generation = generation;
		state.Sequence = 0;
		lock (m_owned) m_owned[partitionId] = generation;
	}

	public void ForgetOwnership(ulong partitionId)
	{
		lock (m_owned) m_owned.Remove(partitionId);
		m_partitions.Remove(partitionId);
	}

	/// <summary>
	/// Close a set of entities together with their hierarchy children. Closing a grid alone ejects
	/// a seated pilot (grid close -> MyCockpit.Closing -> RemovePilot) into a live top-level entity
	/// that outlives the sweep and gets adopted as a stray duplicate of the copy embedded in the
	/// partition's saves (partitions 28/30/32, 2026-08-10/11).
	/// </summary>
	internal static void CloseWithHierarchy(IEnumerable<MyEntity> entities)
	{
		MyEntity[] targets = entities.ToArray();
		HashSet<VRage.ModAPI.IMyEntity> children = new HashSet<VRage.ModAPI.IMyEntity>();
		foreach (MyEntity target in targets) target.Hierarchy?.GetChildrenRecursive(children);
		ClusterCloseScope.Run(() =>
		{
			foreach (MyEntity target in targets) MyEntities.Close(target);
			CloseAttachedChildren(children);
			MyEntities.DeleteRememberedEntities();
		});
	}

	/// <summary>
	/// The hierarchy children a grid close would otherwise leave behind (a seated pilot, T-0213) go down
	/// with it. A grid's own blocks and their subparts are NOT closed one by one: the grid closes them
	/// itself. Closing a turret's subpart while its block was not yet marked for close made WeaponCore
	/// treat it as a model change (CoreComponent.SubpartClosed -> ResetParts -> CheckSubparts) and throw
	/// on the subpart's null Parent, killing the node: T-0309, node-2/3/1 2026-09-29 (drain) and node-1
	/// 2026-09-30 17:46:46 (offline of the Earth base).
	/// </summary>
	internal static void CloseAttachedChildren(IEnumerable<VRage.ModAPI.IMyEntity> children)
	{
		foreach (VRage.ModAPI.IMyEntity child in children)
			if (child is MyEntity orphan && !ClosesWithItsGrid(orphan) && !orphan.MarkedForClose)
				MyEntities.Close(orphan);
	}

	internal static bool ClosesWithItsGrid(MyEntity child) => child is MyCubeBlock || child is MyEntitySubpart;

	/// <summary>
	/// A registry-driven node owns no content at boot: partitions arrive through recovery claims and
	/// handover imports, always from catalogued saves. Whatever dynamic entities the node's own world
	/// file accumulated in earlier sessions are stale shadows of content whose truth is the catalog -
	/// left alive they resurface as fenced leftovers (partition 13), phantom duplicates (partitions
	/// 30/31) or stray-adopted brand-new partitions (35/36/37; all 2026-08-10), each a duplicate
	/// incarnation the handover round-trip gate then rightly refuses forever. Drop everything except
	/// the environment before the registry ever hears from this node: the voxels, and the admin safe zones
	/// (no block). SE1-0251: an admin zone is no partition member, so no save or handover carries it. Every
	/// node keeps its copy from the world file, so every node enforces it, and SE1-0252 brings the copies in
	/// line with the World Authority's set (<see cref="AdminSafeZones"/>). A block's zone goes; it lives with
	/// its block (<see cref="BlockSafeZones"/>).
	/// </summary>
	private static void DropWorldFileEntities()
	{
		MyEntity[] dynamicEntities = MyEntities.GetEntities()
			.Where(entity => entity is not MyVoxelBase && entity is not MySafeZone { SafeZoneBlockId: 0 } && !entity.MarkedForClose)
			.ToArray();
		ClusterCloseScope.Run(() =>
		{
			foreach (MyEntity entity in dynamicEntities) MyEntities.Close(entity);
			MyEntities.DeleteRememberedEntities();
		});
		if (dynamicEntities.Length > 0)
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] dropped " + dynamicEntities.Length
				+ " dynamic entities loaded from the node world file; cluster content loads from catalogued saves");
	}

	private void LoadAssigned()
	{
		// An assigned save's membership can be stale: an entity it lists may have moved to another
		// partition - and another node - since the save was catalogued, and loading it here mints a
		// second incarnation whose partition row is nonetheless legitimately ours, so no generation
		// check ever catches it (world-9, 2026-08-17 01:14: node-2's relaunch re-loaded the pirate
		// base out of partition 258's save while the live base sat in partition 244 on node-5;
		// both partitions went HandoverBlocked against each other within minutes). Screen the boot
		// load against the peers' live censuses exactly like recovery does; fail-open, one snapshot
		// for the whole catalog. #1286: a copy the registry assigns to another live node does not screen; an
		// uncertain one still does (peers booting beside this node hold copies their startup claims confirm later,
		// and there is no deferral on the game thread at boot).
		LiveCensus liveElsewhere = null;
		bool fromCatalogSeed = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SavesEnvironmentVariable));
		foreach (string path in AssignedPaths())
		{
			PartitionSaveFile.Parsed save;
			try
			{
				save = PartitionSaveFile.ReadFile(path);
			}
			catch (Exception error)
			{
				// P2-REC-03: a poison assigned save quarantines its partition instead of killing the
				// node. The rest of the catalog loads and serves; the quarantined entry is visible on
				// /cluster/quarantine and repairable in place from a replica or an edited export.
				Quarantine(path, error);
				continue;
			}
			if (m_warmRecovery)
			{
				// A bad staged save quarantines like any other poison instead of killing the node at
				// boot - the rest of the warm candidates still recover.
				if (save.Kind != 2)
				{
					Quarantine(path, new InvalidDataException("Warm recovery requires Final partition saves"), save.PartitionId);
					continue;
				}
				if (!m_recoveryCandidates.TryAdd(save.PartitionId, 0))
				{
					Quarantine(path, new InvalidDataException("Duplicate warm recovery partition " + save.PartitionId), save.PartitionId);
					continue;
				}
				m_assignedRecoveries[save.PartitionId] = (save.Generation, save.Sequence, path);
				continue;
			}
			if (fromCatalogSeed && SeedSuperseded(save) is { } superseded)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] catalog seed skipped: partition " + save.PartitionId
					+ " seed generation " + save.Generation + ": " + superseded + " (T-0231)");
				continue;
			}
			// P2-CON-01 cold start: register a catalog proxy - sphere, size, generation - and load nothing. A
			// partition carrying standing demand never conceals: a resident player character (a joining client
			// must find it without a wake round trip) or a respawn-capable block (the respawn screen enumerates
			// spawn points from loaded entities only, so a concealed medbay would silently vanish as a spawn
			// option).
			MyObjectBuilder_EntityBase[] parsed;
			try
			{
				parsed = Deserialize(save);
			}
			catch (Exception error)
			{
				Quarantine(path, error, save.PartitionId);
				continue;
			}
			if (m_concealment && !parsed.Any(entity => entity is MyObjectBuilder_Character
				|| (entity is MyObjectBuilder_CubeGrid grid && grid.CubeBlocks.Any(block =>
					block is MyObjectBuilder_SurvivalKit || block is MyObjectBuilder_MedicalRoom))))
			{
				lock (m_concealed)
				{
					m_concealed[save.PartitionId] = new ConcealedPartition
					{
						Id = save.PartitionId,
						Path = path,
						Generation = save.Generation,
						Sequence = save.Sequence,
						Sphere = save.Sphere,
						Members = save.EntityCount
					};
					m_concealable.Add(save.PartitionId);
					foreach (MyObjectBuilder_EntityBase entity in parsed) m_concealableMembers.Add(entity.EntityId);
				}
				m_state[save.PartitionId] = AssignedState(save, path);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] concealed partition " + save.PartitionId
					+ " generation " + save.Generation + " members " + save.EntityCount
					+ " radius " + save.Sphere.Radius.ToString("F0", CultureInfo.InvariantCulture));
				continue;
			}
			MyObjectBuilder_EntityBase[] entities = parsed;
			HashSet<long> screened = null;
			VRage.MyEntityIdentifier.AllocationSuspended = true;
			try
			{
				foreach (MyObjectBuilder_EntityBase entity in entities)
				{
					if (!MyEntities.TryGetEntityById(entity.EntityId, out _))
					{
						if (screened == null)
						{
							using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
							ClusterRuntime.AuthorizeRegistryClient(client);
							liveElsewhere ??= SnapshotLiveEntitiesElsewhere(client);
							// Not the T-0295 lineage cut: at boot a peer's copy of this very partition may be the registry's
							// current one (this node's copy is then fenced), and the row check tells the two apart.
							screened = StaleCopyCheck ? VerifiedScreen(client, liveElsewhere, save.PartitionId, excludeLineage: false,
								uncertainCounts: true, entities.Select(x => x.EntityId).Concat(CharactersIn(entities).Select(x => x.EntityId)).ToHashSet(),
								out _) : liveElsewhere;
						}
						if (screened.Contains(entity.EntityId))
						{
							MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] assigned save skipped entity "
								+ entity.EntityId + " for partition " + save.PartitionId
								+ ": already live on another node (cross-partition duplicate prevented)");
							continue;
						}
						StripPilotsLiveElsewhere(save.PartitionId, new[] { entity }, screened.Contains);
						StripOrphanSeatCharacters(save.PartitionId, new[] { entity });
						MyEntities.CreateFromObjectBuilderAndAdd(entity, fadeIn: false);
					}
				}
			}
			finally
			{
				VRage.MyEntityIdentifier.AllocationSuspended = false;
			}
			m_partitions.Adopt(save.PartitionId, entities.Select(entity => entity.EntityId));
			m_state[save.PartitionId] = AssignedState(save, path);
			// Tainted until the startup claim confirms this node owns the partition: if the claim
			// 409s, these entities belong to another node's newer generation and must neither be
			// reshaped into new layouts nor survive the fenced-orphan purge.
			m_assignedMembers[save.PartitionId] = entities.Select(entity => entity.EntityId).ToArray();
			foreach (MyObjectBuilder_EntityBase entity in entities)
				s_fenceTainted[entity.EntityId] = 0;
			// SAY SO. This path had no success line at all - its only possible output was "concealed
			// partition" or "assigned save skipped entity" - so a startup that loaded a partition's
			// content and one that silently loaded nothing produced identical logs. On T-0207 that
			// cost a wrong conclusion: `woke partition` is the CONCEALED path and reads 0 on a
			// perfectly healthy startup, and with no line here there was nothing to read instead, so
			// an absence got reported as "the content is never loaded" (2026-09-22). A load is the
			// thing every recovery question starts from; it must be visible, with the member count,
			// because "loaded, 0 members" and "did not load" are different faults.
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] assigned save loaded: partition " + save.PartitionId
				+ " generation " + save.Generation + " sequence " + save.Sequence
				+ " members " + entities.Length + " (fence-tainted until the startup claim confirms ownership)");
		}
	}

	private static State AssignedState(PartitionSaveFile.Parsed save, string path) => new State
	{
		Generation = save.Generation,
		Sequence = save.Sequence,
		AssignedPath = path,
		AssignedSequence = save.Sequence,
		AssignedKind = save.Kind,
		AssignedSize = new FileInfo(path).Length,
		AssignedChecksum = save.BodyChecksum,
		AssignedClusterTime = save.ClusterTime,
	};

	/// <summary>
	/// P2-CON-01 demand wake and grace-period reconcealment. Wake demand is a cluster player inside a concealed
	/// partition's pre-wake sphere (positions come from the cluster-wide registry, so an approach from any node
	/// wakes the partition before arrival) or an explicit call to <see cref="WakePartition"/> (join, respawn,
	/// remote control). Reconcealment is the reverse: an owned concealable partition with no player near, no
	/// routed client and no player member for the grace period takes a Final save and unloads once the save is
	/// durably catalogued - a mid-reconceal crash therefore recovers from either the old or the new generation,
	/// never from nothing.
	/// </summary>
	private void UpdateConcealment()
	{
		if (!m_concealment || MySession.Static?.Ready != true) return;
		IReadOnlyList<ClusterPlayerPosition> players = PositionRegistryService.Players;
		List<(ConcealedPartition Partition, string Cause)> wake = new List<(ConcealedPartition, string)>();
		lock (m_concealed)
			foreach (ConcealedPartition concealed in m_concealed.Values)
			{
				// ClientId 0 marks a demand-only registry entry (an actively viewed camera), not a character.
				ClusterPlayerPosition near = players.FirstOrDefault(player => PlayerNear(player, concealed.Sphere));
				if (near != null) wake.Add((concealed, near.ClientId == 0 ? "view" : "approach"));
			}
		foreach ((ConcealedPartition concealed, string cause) in wake) Wake(concealed, cause);
		// Settle pending reconceal saves: unload only once the Final save is durably catalogued.
		for (int i = m_reconcealing.Count - 1; i >= 0; i--)
		{
			(ulong partitionId, PendingSave pending) = m_reconcealing[i];
			if (!pending.Completed.IsSet) continue;
			m_reconcealing.RemoveAt(i);
			if (!pending.Succeeded)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] reconceal save failed for partition " + partitionId
					+ "; partition stays awake: " + (pending.LastError ?? "unknown"));
				continue;
			}
			FinishReconceal(partitionId, pending);
		}
		// Start new reconcealments for quiet, clientless, playerless partitions past the grace period.
		DateTime now = DateTime.UtcNow;
		// One sweep, one route scan and one reconcealing set for the whole loop. Each of these used to be
		// rebuilt per partition: ClientPartitions(new[] { partition }) walked every online player AND
		// allocated a dictionary for a single partition, and m_reconcealing was scanned linearly per
		// partition. This runs on the game thread every 250 ms tick whenever concealment is on.
		PartitionManager.Partition[] concealCandidates = m_partitions.Evaluate().ToArray();
		// NOTE the semantics being preserved here: ClientPartitions returns ONE ROW PER ONLINE REAL PLAYER
		// (partition 0 when the player is not in any of the partitions passed), so the previous
		// `ClientPartitions(new[] { partition }).Any()` was true whenever ANY real player was online anywhere
		// in the cluster - not when a client was in THAT partition. That looks like a bug (it keeps every
		// partition awake while anyone is online) but it is not this change's business to alter behaviour, so
		// the same question is asked once instead of once per partition. See the findings note.
		bool anyRealPlayerOnline = m_partitions.ClientPartitions(concealCandidates).Length != 0;
		HashSet<ulong> reconcealing = m_reconcealing.Select(entry => entry.Partition).ToHashSet();
		foreach (PartitionManager.Partition partition in concealCandidates)
		{
			bool memberEligible;
			lock (m_concealed) memberEligible = partition.Members.Count > 0
				&& partition.Members.All(member => m_concealableMembers.Contains(member)
					|| ConcealableContentService.Contains(member));
			if (!m_concealable.Contains(partition.Id) && !memberEligible) continue;
			lock (m_concealed) if (m_concealed.ContainsKey(partition.Id)) continue;
			if (reconcealing.Contains(partition.Id)) continue;
			bool near = players.Any(player => PlayerNear(player, partition.Sphere))
				|| players.Any(player => partition.Members.Contains(player.EntityId))
				|| anyRealPlayerOnline;
			if (near)
			{
				m_lastPlayerNearUtc[partition.Id] = now;
				continue;
			}
			if (!m_lastPlayerNearUtc.TryGetValue(partition.Id, out DateTime lastNear))
			{
				m_lastPlayerNearUtc[partition.Id] = now;
				continue;
			}
			if ((now - lastNear).TotalSeconds < m_concealGraceSeconds) continue;
			State state = GetState(partition.Id);
			if (state.Pending) continue;
			if (partition.HasPending) continue;   // members still initializing: retry next pass
			if (m_apply.IsPending(partition.Id)) continue;   // T-0354: its registry request is unanswered
			PendingSave pending = QueueCapture(partition, "Final", waitForSlot: false);
			if (pending == null) continue;
			m_reconcealing.Add((partition.Id, pending));
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] reconcealing partition " + partition.Id
				+ " generation " + pending.Generation + " sequence " + pending.Sequence);
		}
	}

	/// <summary>
	/// Orchestration.md 11.2 <c>trigger conceal-eval</c>: run the eligibility check now instead of waiting
	/// out the cadence. Deliberately the scheduler's own routine, so a triggered run and a scheduled one
	/// are the same code path.
	/// </summary>
	public object TriggerConcealEval()
	{
		UpdateConcealment();
		return ConcealmentStatus();
	}

	/// <summary>Explicit wake demand: join, respawn or remote-control targets inside the partition.</summary>
	public bool WakePartition(ulong partitionId, string cause)
	{
		ConcealedPartition concealed;
		lock (m_concealed) m_concealed.TryGetValue(partitionId, out concealed);
		if (concealed == null) return false;
		Wake(concealed, cause);
		return true;
	}

	public object ConcealmentStatus()
	{
		lock (m_concealed)
			return new
			{
				enabled = m_concealment,
				graceSeconds = m_concealGraceSeconds,
				preWakeMeters = m_preWakeMeters,
				concealed = m_concealed.Values.Select(entry => new
				{
					id = entry.Id,
					generation = entry.Generation,
					sequence = entry.Sequence,
					members = entry.Members,
					radius = entry.Sphere.Radius,
					center = new[] { entry.Sphere.Center.X, entry.Sphere.Center.Y, entry.Sphere.Center.Z }
				}).ToArray(),
				reconcealing = m_reconcealing.Select(entry => entry.Partition).ToArray(),
				awake = m_concealable.Where(id =>
				{
					lock (m_concealed) return !m_concealed.ContainsKey(id);
				}).ToArray()
			};
	}

	private bool PlayerNear(ClusterPlayerPosition player, VRageMath.BoundingSphereD sphere)
		=> VRageMath.Vector3D.Distance(new VRageMath.Vector3D(player.X, player.Y, player.Z), sphere.Center)
			<= sphere.Radius + m_preWakeMeters;

	private void Wake(ConcealedPartition concealed, string cause)
	{
		lock (m_concealed) if (!m_concealed.Remove(concealed.Id)) return;
		PartitionSaveFile.Parsed save = PartitionSaveFile.ReadFile(concealed.Path);
		MyObjectBuilder_EntityBase[] entities = Deserialize(save);
		VRage.MyEntityIdentifier.AllocationSuspended = true;
		try
		{
			foreach (MyObjectBuilder_EntityBase entity in entities)
				if (!MyEntities.TryGetEntityById(entity.EntityId, out _))
					MyEntities.CreateFromObjectBuilderAndAdd(entity, fadeIn: false);
		}
		finally
		{
			VRage.MyEntityIdentifier.AllocationSuspended = false;
		}
		m_partitions.Adopt(save.PartitionId, entities.Select(entity => entity.EntityId));
		m_lastPlayerNearUtc[save.PartitionId] = DateTime.UtcNow;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] woke partition " + save.PartitionId
			+ " generation " + save.Generation + " cause " + cause + " members " + entities.Length);
	}

	private void FinishReconceal(ulong partitionId, PendingSave pending)
	{
		PartitionManager.Partition partition = m_partitions.Get(partitionId);
		// T-0213: close through the hierarchy, not by membership. A seated pilot is a live TOP-LEVEL
		// entity whose id is normally NOT in Members, so a membership filter closes the grid and
		// leaves the character behind - MyCockpit.Closing then ejects it (see CloseWithHierarchy's
		// note and partitions 28/30/32, 2026-08-10/11). The ejected body is adopted into a partition,
		// its id enters Members, and every capture from then on writes it TWICE: once at top level
		// and once as the grid's cockpit Pilot. Import collapses the two, the round-trip gate sees
		// one character fewer than the frozen source and aborts, and the partition blocks for good
		// (130846/130847/131260/148443, 2026-09-21/22).
		CloseWithHierarchy(MyEntities.GetEntities()
			.Where(entity => partition.Members.Contains(entity.EntityId) && !entity.MarkedForClose));
		m_partitions.Remove(partitionId);
		string path = SavePath(partitionId, pending.Generation, pending.Sequence);
		lock (m_concealed)
			m_concealed[partitionId] = new ConcealedPartition
			{
				Id = partitionId,
				Path = path,
				Generation = pending.Generation,
				Sequence = pending.Sequence,
				Sphere = pending.Sphere,
				Members = pending.Entities.Length
			};
		lock (m_concealed) foreach (long member in pending.Entities.Select(entity => entity.EntityId)) m_concealableMembers.Add(member);
		m_lastPlayerNearUtc.Remove(partitionId);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] reconcealed partition " + partitionId
			+ " generation " + pending.Generation + " sequence " + pending.Sequence
			+ " members " + pending.Entities.Length);
	}

	private void Quarantine(string path, Exception error, ulong partition = 0)
	{
		if (partition == 0)
		{
			// Best-effort id peek so the operator knows WHICH partition is poisoned even when the
			// envelope is unreadable: the id sits at a fixed offset behind the intact magic.
			try
			{
				byte[] head = new byte[16];
				using FileStream stream = File.OpenRead(path);
				if (stream.Read(head, 0, head.Length) == head.Length
					&& head[0] == 'M' && head[1] == 'P' && head[2] == 'S' && head[3] == '1')
					partition = BitConverter.ToUInt64(head, 8);
			}
			catch
			{
				// The id stays 0 - the path still identifies the entry.
			}
		}
		lock (m_quarantined)
			m_quarantined.Add(new QuarantinedPartition { Path = path, Partition = partition, Error = error.Message });
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] QUARANTINED poison partition " + partition
			+ " path " + path + ": " + error.Message);
	}

	/// <summary>Quarantined partition ids for the registry heartbeat - operators watch the GATEWAY, not
	/// every node, so quarantine state must be registry-visible to be found.</summary>
	private ulong[] QuarantinedPartitionIds()
	{
		lock (m_quarantined)
			return m_quarantined.Where(entry => entry.Partition != 0).Select(entry => entry.Partition).Distinct().ToArray();
	}

	/// <summary>Cross-host repair: adopts an UPLOADED replacement save. The repair path variant requires a
	/// node-local file, which an operator on another host does not have.</summary>
	public object RepairQuarantinedFromUpload(ulong partitionId, byte[] bytes)
	{
		if (bytes == null || bytes.Length == 0) throw new ArgumentException("An uploaded save is required", nameof(bytes));
		string staging = Path.Combine(m_root ?? Path.GetTempPath(), "quarantine-repair-" + partitionId + ".mps");
		File.WriteAllBytes(staging, bytes);
		try { return RepairQuarantined(partitionId, staging); }
		finally { try { File.Delete(staging); } catch { /* staging copy only */ } }
	}

	/// <summary>
	/// Phase 1 of the in-place operator restore: close the partition's current live incarnations
	/// (with their hierarchy) so the next-frame import of the replacement save does not collide with
	/// their entity ids. Restores exist because a poisoned catalog can recover a partition from stale
	/// bytes while the only current copy sits in an operator's rescue file (partition 25, 2026-08-11).
	/// </summary>
	public object RestoreCloseMembers(ulong partitionId)
	{
		lock (m_owned) if (!m_owned.ContainsKey(partitionId)) throw new InvalidOperationException("Partition " + partitionId + " is not owned by this node");
		PartitionManager.Partition current = m_partitions.Peek(partitionId);
		MyEntity[] members = MyEntities.GetEntities().Where(entity => current.Members.Contains(entity.EntityId)).ToArray();
		CloseWithHierarchy(members);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] restore closed " + members.Length + " member(s) of partition " + partitionId);
		return new { partition = partitionId, closed = members.Length };
	}

	/// <summary>
	/// Sub-entity ids inside a restored grid that are ALREADY live: block ids and a cockpit's seated
	/// <c>Pilot</c>. Phase 1 and the top-level check below only ever compared the grid's OWN id, but a
	/// block keeps its EntityId when it breaks off into its own 1-block grid, and a seated pilot's id
	/// is a live player's character - so a grid whose own id is free still collides internally. When it
	/// does, <c>MyCubeGrid.AddBlock</c> catches the <c>DuplicateIdException</c>, writes to the GAME log
	/// and returns null while <c>InitInternal</c> carries on, so the blocks and the pilot vanish and the
	/// restore still reports success. w23 2026-08-24: a restored Visitor came back at 51 blocks instead
	/// of 53 with its pilot gone, reported as "0 skipped as still live".
	/// Each hit carries the strip action for the <c>collisions=strip</c> path so the caller never has to
	/// know the object-builder shape.
	/// </summary>
	private static List<(string Kind, long Id, Action Strip)> CollidingSubEntities(MyObjectBuilder_EntityBase entity)
	{
		var found = new List<(string, long, Action)>();
		if (entity is not MyObjectBuilder_CubeGrid grid) return found;
		foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks.ToArray())
		{
			// A zero id is "assign me one"; only a real id can collide.
			if (block.EntityId != 0 && MyEntities.TryGetEntityById(block.EntityId, out _))
			{
				MyObjectBuilder_CubeBlock colliding = block;
				found.Add(("block", block.EntityId, () => grid.CubeBlocks.Remove(colliding)));
			}
			if (block is MyObjectBuilder_Cockpit cockpit && cockpit.Pilot != null
					&& cockpit.Pilot.EntityId != 0 && MyEntities.TryGetEntityById(cockpit.Pilot.EntityId, out _))
			{
				MyObjectBuilder_Cockpit seated = cockpit;
				found.Add(("pilot", cockpit.Pilot.EntityId, () => seated.Pilot = null));
			}
		}
		return found;
	}

	/// <summary>
	/// Phase 2: import the uploaded replacement save into the still-owned partition and catalogue it
	/// immediately. Entity ids still present (a member phase 1 missed, or content that moved to
	/// another partition since the save was taken) are skipped and reported, not overwritten.
	/// <para>
	/// Sub-entity collisions (see <see cref="CollidingSubEntities"/>) REFUSE the restore by default and
	/// nothing is created: a restore that silently returns a damaged grid is worse than one that will
	/// not run, because the damage is discovered hours later and is unattributable by then. Pass
	/// <paramref name="stripCollisions"/> (<c>?collisions=strip</c>) to import anyway with the
	/// colliding blocks and seated pilots removed - the same outcome the engine produced silently,
	/// except deterministic and itemised in the log and the response.
	/// </para>
	/// </summary>
	public object RestoreOwnedPartition(ulong partitionId, byte[] bytes, bool stripCollisions = false)
	{
		if (bytes == null || bytes.Length == 0) throw new ArgumentException("An uploaded save is required", nameof(bytes));
		uint generation;
		lock (m_owned) if (!m_owned.TryGetValue(partitionId, out generation)) throw new InvalidOperationException("Partition " + partitionId + " is not owned by this node");
		string staging = Path.Combine(m_root ?? Path.GetTempPath(), "restore-" + partitionId + ".mps");
		File.WriteAllBytes(staging, bytes);
		try
		{
			PartitionSaveFile.Parsed save = PartitionSaveFile.ReadFile(staging);
			if (save.PartitionId != partitionId)
				throw new InvalidDataException("Replacement save belongs to partition " + save.PartitionId);
			MyObjectBuilder_EntityBase[] entities = Deserialize(save);
			List<long> skipped = new List<long>();

			// Collect BEFORE creating anything, so the refusal path leaves the world untouched.
			var collisions = new List<(string Owner, string Kind, long Id, Action Strip)>();
			foreach (MyObjectBuilder_EntityBase entity in entities)
			{
				if (MyEntities.TryGetEntityById(entity.EntityId, out _)) continue;   // whole entity skipped below
				foreach ((string kind, long id, Action strip) in CollidingSubEntities(entity))
					collisions.Add((entity.EntityId.ToString(CultureInfo.InvariantCulture), kind, id, strip));
			}
			string collisionList = string.Join(", ", collisions.Select(c => c.Kind + " " + c.Id + " in " + c.Owner));
			if (collisions.Count != 0 && !stripCollisions)
				throw new InvalidOperationException("Restore of partition " + partitionId + " refused: "
					+ collisions.Count + " sub-entity id(s) are already live and the import would drop them silently ["
					+ collisionList + "]. Resolve them (the live holder is usually break-off debris or a seated"
					+ " player's character) or repeat with collisions=strip to import without them.");
			if (collisions.Count != 0)
			{
				foreach ((_, _, _, Action strip) in collisions) strip();
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] restore of partition " + partitionId
					+ " STRIPPED " + collisions.Count + " colliding sub-entities before import [" + collisionList + "]");
			}

			VRage.MyEntityIdentifier.AllocationSuspended = true;
			try
			{
				foreach (MyObjectBuilder_EntityBase entity in entities)
					if (MyEntities.TryGetEntityById(entity.EntityId, out _)) skipped.Add(entity.EntityId);
					else MyEntities.CreateFromObjectBuilderAndAdd(entity, fadeIn: false);
			}
			finally
			{
				VRage.MyEntityIdentifier.AllocationSuspended = false;
			}
			m_partitions.Adopt(partitionId, entities.Select(entity => entity.EntityId).Except(skipped), mature: true);
			QueueCapture(m_partitions.Get(partitionId), "Final", waitForSlot: false);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] restored partition " + partitionId
				+ " generation " + generation + " from uploaded save (source " + save.Generation + "/" + save.Sequence
				+ "): " + entities.Length + " member(s), " + skipped.Count + " skipped as still live"
				+ (skipped.Count == 0 ? "" : " [" + string.Join(",", skipped) + "]")
				+ ", " + collisions.Count + " sub-entities stripped as colliding"
				+ (collisions.Count == 0 ? "" : " [" + collisionList + "]"));
			return new
			{
				partition = partitionId, generation, members = entities.Length, skipped = skipped.ToArray(),
				stripped = collisions.Select(c => new { owner = c.Owner, kind = c.Kind, id = c.Id }).ToArray()
			};
		}
		finally { try { File.Delete(staging); } catch { /* staging copy only */ } }
	}

	public object QuarantineStatus()
	{
		lock (m_quarantined)
			return new
			{
				quarantined = m_quarantined
					.Select(entry => new { partition = entry.Partition, path = entry.Path, error = entry.Error }).ToArray()
			};
	}

	/// <summary>
	/// P2-REC-03 repair: load a replacement save (a catalogued replica or an edited re-import) for a
	/// quarantined partition and bring it into service in place. The replacement must be a valid save
	/// for the same partition; its generation/sequence become the partition's current state, so the
	/// next capture continues the lineage from the repaired save.
	/// </summary>
	public object RepairQuarantined(ulong partitionId, string replacementPath)
	{
		QuarantinedPartition entry;
		lock (m_quarantined) entry = m_quarantined.FirstOrDefault(candidate => candidate.Partition == partitionId)
			?? throw new KeyNotFoundException("Partition " + partitionId + " is not quarantined");
		PartitionSaveFile.Parsed save = PartitionSaveFile.ReadFile(replacementPath);
		if (save.PartitionId != partitionId)
			throw new InvalidDataException("Replacement save belongs to partition " + save.PartitionId);
		MyObjectBuilder_EntityBase[] entities = Deserialize(save);
		VRage.MyEntityIdentifier.AllocationSuspended = true;
		try
		{
			foreach (MyObjectBuilder_EntityBase entity in entities)
				if (!MyEntities.TryGetEntityById(entity.EntityId, out _))
					MyEntities.CreateFromObjectBuilderAndAdd(entity, fadeIn: false);
		}
		finally
		{
			VRage.MyEntityIdentifier.AllocationSuspended = false;
		}
		m_partitions.Adopt(save.PartitionId, entities.Select(entity => entity.EntityId));
		uint generation = save.Generation;
		if (RegistryEnabled)
		{
			// The partition was never claimed this boot (quarantine skipped the startup claim), so
			// without this the registry fences every save registration for it. Same claim protocol
			// as startup, including the sequence reset: registry catalog sequences restart at 1 per
			// claim, independent of the sequence stamped in the repaired file. The claim is a CAS on
			// the registry's CURRENT generation - released warm-recovery claims advance it past the
			// save's own generation, so the expectation is read live rather than derived from the file.
			using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
			ClusterRuntime.AuthorizeRegistryClient(client);
			long expectedGeneration = save.Generation - 1L;
			using (JsonDocument partitions = Get(client, "/registry/partitions"))
				foreach (JsonElement element in partitions.RootElement.EnumerateArray())
					if (element.GetProperty("Partition").GetUInt64() == partitionId)
					{
						expectedGeneration = element.GetProperty("Generation").GetInt64();
						break;
					}
			using JsonDocument owner = Post(client, "/registry/partitions?partition=" + partitionId.ToString(CultureInfo.InvariantCulture)
				+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
				+ "&expectedGeneration=" + expectedGeneration.ToString(CultureInfo.InvariantCulture));
			generation = checked((uint)owner.RootElement.GetProperty("Generation").GetInt64());
			lock (m_owned) m_owned[partitionId] = generation;
		}
		m_state[save.PartitionId] = new State { Generation = generation, Sequence = 0 };
		lock (m_quarantined) m_quarantined.Remove(entry);
		// An immediate Final capture closes the durability window: the repaired state is
		// catalogued without waiting for the next periodic save.
		QueueCapture(m_partitions.Get(partitionId), "Final", waitForSlot: false);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] repaired quarantined partition " + partitionId
			+ " from " + replacementPath + " generation " + generation + " source sequence " + save.Sequence
			+ " members " + entities.Length);
		return new { partition = partitionId, generation, members = entities.Length };
	}

	private bool RegistryEnabled => !string.IsNullOrWhiteSpace(m_gateway) && !string.IsNullOrWhiteSpace(m_node)
		&& !string.IsNullOrWhiteSpace(m_endpoint) && m_root != null;

	// Local placeholder ids handed out after a fence: unique, far above any registry-allocated range,
	// never registered - the in-flight membership evaluation completes in memory and the fence exit
	// discards everything on the next Update tick. T-0374: from a random start per process, as a Closed
	// node's placeholder can be claimed for its drain handover (every node used to mint the same first id).
	private static long m_fencedAllocationId = ClusterProtocol.PlaceholderPartitionIds.RandomStart();

	/// <summary>
	/// T-0354: partition ids for the membership pass, from a reserve the ClusterRegistryApply worker keeps filled - no
	/// registry request here. Null when the reserve is short: the pass leaves that change for later (the worker is
	/// woken and asks for more). The ids are allocated, not claimed: a split's outputs are claimed by its topology
	/// apply, a new layout's id by <see cref="ClaimCreatedLayouts"/>. Rollback: CLUSTER_REGISTRY_APPLY_ASYNC=0.
	/// </summary>
	private ulong[] AllocatePartitionIds(int count)
	{
		// A fresh registry restarts its id space at 1, and the split-world catalog this node boots
		// with already holds partitions of those very numbers (fenced, awaiting recovery). Handing
		// such a twin id to the membership put two layouts with one id in the sweep, and the
		// Dictionary rebuild in PartitionManager.Evaluate then threw on the GAME THREAD - the loop
		// unwound out of Main and the node hung half-dead until the stall-kill fenced it
		// (P3-HND-01 Survival leg). Skip every locally-known id; a skipped registry id stays
		// reserved on the gateway and is simply never issued again, which is harmless.
		while (true)
		{
			// A Closed node gets the same local placeholder ids a fenced one does: the registry
			// refuses its claims, and with HoldTopology set (flipped alongside m_closed) the only
			// allocations still reaching this point are adoptions of genuinely new strays, which
			// were never durable in the first place. Without this branch, the 409 on the claim counted as
			// a definitive rejection and FENCED the node mid-drain - discarding the very content
			// the drain was moving out.
			if (m_fenced || m_closed)
			{
				List<ulong> result = new List<ulong>(count);
				while (result.Count < count)
				{
					ulong id = (ulong)Interlocked.Increment(ref m_fencedAllocationId);
					if (!IsLocallyKnownPartitionId(id)) result.Add(id);
				}
				return result.ToArray();
			}
			ulong[] ids = m_apply.Take(count, id =>
			{
				if (!IsLocallyKnownPartitionId(id)) return false;
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] allocated partition id " + id
					+ " collides with a locally known partition; skipping");
				return true;
			});
			m_applyWake.Set();
			if (ids != null || RegistryApplyAsync) return ids;
			// Rollback: ask on the game thread and wait, as before T-0354.
			if (!RefillPartitionIds()) Thread.Sleep(1000);
		}
	}

	/// <summary>Worker (game thread only under the rollback): tops the id reserve up. True when the registry gave ids.</summary>
	private bool RefillPartitionIds()
	{
		int wanted = Math.Min(1024, m_apply.Wanted(PartitionIdReserve));
		long epoch = m_nodeEpoch != 0 ? m_nodeEpoch : ClusterRuntime.RegistryNodeEpoch;
		if (wanted == 0 || epoch == 0 || m_fenced || m_closed) return false;
		try
		{
			using HttpClient client = ClusterHttp.Client(RetryLoopTimeout);
			ClusterRuntime.AuthorizeRegistryClient(client);
			using JsonDocument ids = Post(client, "/registry/partition-ids?node=" + Uri.EscapeDataString(m_node) + "&epoch=" + epoch
				+ "&count=" + wanted.ToString(CultureInfo.InvariantCulture));
			m_apply.Refill(ids.RootElement.EnumerateArray().Select(x => x.GetUInt64()).ToArray());
			return true;
		}
		catch (Exception exception)
		{
			// Orchestration §4.2: a definitive rejection is terminal. A 409 is not: the registry refuses ids to a node
			// it is phasing out, and the heartbeat that reports this node Closed (it sets m_closed, and the branch
			// above then hands out local placeholder ids) may simply not have landed yet.
			if (ClusterRuntime.IsDefinitiveRejection(exception) && exception is not HttpRequestException { StatusCode: HttpStatusCode.Conflict })
			{
				FenceForRejection("partition id allocation rejected: " + exception.Message);
				return false;
			}
			if (SaveRetryPolicy.RegistryUnreachable(exception)) NoteRegistryReachable(false, exception);
			// T-0350: one line per 10 s, not per attempt.
			if (RegistryHoldPolicy.LogDue(ref m_allocationRetryLoggedTicks, DateTime.UtcNow.Ticks, RetryLogTicks))
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] partition ID allocation unavailable"
					+ (RegistryApplyAsync ? " (asked again off the game thread): " : ", retrying: ") + SecretRedactor.Redact(exception.Message));
			return false;
		}
	}

	/// <summary>T-0354: how many allocated partition ids the worker keeps at hand (plus whatever one request asked beyond it).</summary>
	private const int PartitionIdReserve = 16;
	private static readonly bool RegistryApplyAsync = Environment.GetEnvironmentVariable("CLUSTER_REGISTRY_APPLY_ASYNC") != "0";
	private readonly RegistryApplyQueue m_apply = new RegistryApplyQueue();
	private readonly AutoResetEvent m_applyWake = new AutoResetEvent(false);

	// T-0350: the registry requests of the membership pass (partition ids, claims, topology apply) ask with a short
	// timeout, so the first unanswered attempt returns in 3 s and sets the registry hold, and log their retry line once
	// per 10 s. T-0354: sent by the ClusterRegistryApply worker; on the game thread only with CLUSTER_REGISTRY_APPLY_ASYNC=0.
	private static readonly TimeSpan RetryLoopTimeout = TimeSpan.FromSeconds(3);
	private static readonly long RetryLogTicks = TimeSpan.FromSeconds(10).Ticks;
	private long m_allocationRetryLoggedTicks;
	private long m_topologyRetryLoggedTicks;

	private bool IsLocallyKnownPartitionId(ulong id) => m_state.ContainsKey(id) || m_partitions.IsKnownId(id);

	/// <summary>T-0252: entity ids of the characters seated in cockpits of the given member grids.</summary>
	private static long[] SeatedPilots(IEnumerable<long> members)
	{
		var pilots = new List<long>();
		foreach (long id in members)
			if (MyEntities.GetEntityById(id) is MyCubeGrid grid && !grid.MarkedForClose)
				foreach (MyCockpit cockpit in grid.GetFatBlocks<MyCockpit>())
					if (cockpit.Pilot is { } pilot) pilots.Add(pilot.EntityId);
		return pilots.ToArray();
	}

	private long m_topologyClusterTime = long.MinValue;

	/// <summary>T-0339 (design C): the registry refused a transfer once; this process proposes none again.</summary>
	private volatile bool m_transferRefused;

	/// <summary>
	/// T-0354: the layouts the pass just made around unassigned members carry ids from the reserve that nobody has
	/// claimed. Queue the claim; until it is answered the layout is locked and not saved. Game thread.
	/// </summary>
	private void ClaimCreatedLayouts(ulong[] created)
	{
		// Local placeholder ids (fenced, Closed) were never locked and are never claimed.
		ulong[] reserved = created.Where(m_apply.IsPending).ToArray();
		if (reserved.Length == 0) return;
		if (m_fenced || m_closed)
		{
			// Closed between the allocation and here: unclaimed ids are placeholders like the local ones.
			m_apply.Unlock(reserved);
			return;
		}
		long epoch = m_nodeEpoch != 0 ? m_nodeEpoch : ClusterRuntime.RegistryNodeEpoch;
		foreach (ulong id in reserved)
			m_apply.Enqueue(new RegistryApplyQueue.Operation { Claim = id, Epoch = epoch, Expect = new[] { (id, 1u) } });
		m_applyWake.Set();
		if (!RegistryApplyAsync) DrainRegistryAppliesInline();
	}

	/// <summary>
	/// The membership pass changed the topology in memory; the registry has to apply the same change. T-0354: queued
	/// for the ClusterRegistryApply worker, not sent here - this runs inside PartitionManager.Evaluate on the game
	/// thread, and it used to retry until the registry answered (node-90: one frame of 266 s, 2026-10-01). The
	/// change stays applied in memory and its layouts stay locked until <see cref="CompleteRegistryApplies"/> takes the answer.
	/// </summary>
	private void ApplyTopology(PartitionMembership.Change[] changes)
	{
		foreach (PartitionMembership.Change change in changes)
		{
			// A partition whose startup claim was fenced stays loaded locally while it waits for a
			// recovery claim, and the membership evaluation still sees its members. Proposing a
			// split/merge over it draws a definitive 409 from the registry, which is terminal for the
			// node - node-1 crash-looped on exactly this after the 2026-08-10 upgrade restart. Only
			// owned partitions may be topology inputs; the evaluation re-proposes the change once a
			// recovery claim restores ownership. T-0307: since PartitionManager.Owned locks unowned
			// layouts, the membership pass no longer proposes such a change; this stays as a guard.
			bool owned;
			lock (m_owned) owned = change.Inputs.All(m_owned.ContainsKey);
			if (!owned)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] topology change deferred, inputs not owned: "
					+ string.Join(",", change.Inputs));
				// The outputs that came from the reserve; an output that is also an input (a merge, T-0339's split) did not.
				m_apply.Unlock(change.Outputs.Select(x => x.Id).Except(change.Inputs));
				continue;
			}
			string operationId = Guid.NewGuid().ToString("N");
			// T-0354 x T-0339: an input an earlier queued change keeps as its output (the same pass may split X -> X, Y
			// and then merge X) is named at the generation that change gives it, not the one the node knows now.
			var inputs = change.Inputs.Select(id => new { Partition = id, Generation = m_apply.InputGeneration(id, GetState(id).Generation) }).ToArray();
			Dictionary<ulong, long[]> members = change.Split ? change.Outputs.ToDictionary(x => x.Id, x => x.Members) : null;
			// T-0252: the pilots seated in each output's grids. A seated body is not a member (its ship is), so without
			// this the registry sent a disconnected pilot's placement and home to the most populated output.
			// T-0339 (design C): a transfer moves only its Moved grids, and a client in neither member list stays on its
			// partition (GatewayRegistry.RepointClientState), so only the pilots seated in the moved grids are named - the
			// target can be a large partition, and its pilots do not move.
			Dictionary<ulong, long[]> seated = change.Transfer ? new Dictionary<ulong, long[]> { [change.Inputs[1]] = SeatedPilots(change.Moved) }
				: change.Split ? change.Outputs.ToDictionary(x => x.Id, x => SeatedPilots(x.Members)) : null;
			// T-0339: strictly after this node's previous change (PartitionMembership.NextChangeTime).
			long clusterTime = PartitionMembership.NextChangeTime(ClusterRuntime.CatalogClusterMilliseconds(), ref m_topologyClusterTime);
			// The request is built once, here: every attempt sends the same OpId, inputs and cluster time, so the
			// registry answers a repeat of an apply whose answer was lost with the recorded outputs (T-0308).
			// T-0339 (design C): a transfer is a Split on the wire (two inputs, the same two ids out) - see
			// GatewayRegistry.IsTransfer for why; a registry without transfers answers 400 (Outcome.TransferRefused).
			// Both inputs are outputs, so both stay locked until the answer is taken, and each moves on by one.
			m_apply.Enqueue(new RegistryApplyQueue.Operation
			{
				Change = change,
				Epoch = m_nodeEpoch,
				// As the registry numbers them: a new output starts at 1, the kept input of a merge moves on by one.
				Expect = change.Outputs.Select(x => (x.Id, (inputs.FirstOrDefault(input => input.Partition == x.Id)?.Generation ?? 0) + 1)).ToArray(),
				Request = new { OpId = operationId, Kind = change.Split ? "Split" : "Merge",
					Node = m_node, Epoch = m_nodeEpoch, Inputs = inputs, OutputIds = change.Outputs.Select(x => x.Id).ToArray(), Members = members,
					ClusterTime = clusterTime, Seated = seated },
			});
		}
		m_applyWake.Set();
		if (!RegistryApplyAsync) DrainRegistryAppliesInline();
	}

	/// <summary>T-0354: sends the queued claims and topology applies in order and keeps the partition-id reserve filled.</summary>
	private void RegistryApplyLoop()
	{
		// Fenced: the process is exiting and discarding ownership - what is queued dies with it.
		while (!m_fenced)
		{
			bool progressed = false;
			try
			{
				progressed = RefillPartitionIds();
				progressed |= m_apply.Step(SendRegistryApply) == RegistryApplyQueue.Progress.Answered;
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: registry apply worker: " + SecretRedactor.Redact(exception.ToString()));
			}
			// An unanswered request is sent again after 1 s (sooner when the game thread queues another).
			if (!progressed) m_applyWake.WaitOne(1000);
		}
	}

	/// <summary>One attempt at one queued request. The worker's thread; the game thread only under the rollback.</summary>
	private RegistryApplyQueue.Outcome SendRegistryApply(RegistryApplyQueue.Operation operation)
	{
		try
		{
			using HttpClient client = ClusterHttp.Client(RetryLoopTimeout);
			ClusterRuntime.AuthorizeRegistryClient(client);
			if (operation.Change == null)
			{
				// expectedGeneration=0 on the first attempt only: when its answer was lost the row is this node's at
				// generation 1 and the same request would be refused as a generation conflict. Without the expectation
				// the registry answers the incarnation that owns the row with the row, and creates it otherwise.
				using JsonDocument owner = Post(client, "/registry/partitions?partition=" + operation.Claim.ToString(CultureInfo.InvariantCulture)
					+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + operation.Epoch.ToString(CultureInfo.InvariantCulture)
					+ (operation.Attempts == 1 ? "&expectedGeneration=0" : ""));
				operation.Answer = new[] { (operation.Claim, checked((uint)owner.RootElement.GetProperty("Generation").GetInt64())) };
			}
			else
			{
				using JsonDocument applied = PostJson(client, "/registry/topology", operation.Request);
				operation.Answer = applied.RootElement.GetProperty("Outputs").EnumerateArray()
					.Select(output => (output.GetProperty("Partition").GetUInt64(), checked((uint)output.GetProperty("Generation").GetInt64()))).ToArray();
			}
			return RegistryApplyQueue.Outcome.Applied;
		}
		catch (Exception exception)
		{
			operation.Error = exception.Message;
			RegistryApplyQueue.Outcome outcome = RegistryApplyQueue.Classify(operation.Change == null, exception, operation.Change?.Transfer == true);
			if (outcome != RegistryApplyQueue.Outcome.Unavailable) return outcome;
			if (SaveRetryPolicy.RegistryUnreachable(exception)) NoteRegistryReachable(false, exception);
			// T-0350: one line per 10 s, not per attempt.
			if (RegistryHoldPolicy.LogDue(ref m_topologyRetryLoggedTicks, DateTime.UtcNow.Ticks, RetryLogTicks))
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] " + (operation.Change == null ? "partition claim" : "topology WAL apply") + " unavailable"
					+ (RegistryApplyAsync ? " (attempt " + operation.Attempts + ", asked again off the game thread, layouts stay locked): " : ", retrying before save: ")
					+ (operation.Change == null ? "partition=" + operation.Claim
						: "inputs=" + string.Join(",", operation.Change.Inputs) + " outputs=" + string.Join(",", operation.Change.Outputs.Select(x => x.Id)))
					+ " " + SecretRedactor.Redact(exception.Message));
			return RegistryApplyQueue.Outcome.Unavailable;
		}
	}

	/// <summary>
	/// T-0354: the registry's answers to the queued claims and topology applies, taken on the game thread in the order
	/// the changes were made. What the retry loops did in place: the ownership and generations of an applied change, the
	/// revert of a change the registry refused because it is closing this node, the fence on a definitive refusal.
	/// </summary>
	private void CompleteRegistryApplies()
	{
		while (!m_fenced && m_apply.TryTakeAnswered(out RegistryApplyQueue.Operation operation))
		{
			PartitionMembership.Change change = operation.Change;
			if (operation.Outcome == RegistryApplyQueue.Outcome.Rejected)
			{
				// Orchestration §4.2: "a topology WAL apply refused for a stale instance" is the named terminal case -
				// fence and stop. The layouts stay locked: the process is exiting and discarding ownership.
				FenceForRejection((change == null ? "partition claim rejected: " : "topology WAL apply rejected: ") + operation.Error);
				return;
			}
			if (operation.Outcome == RegistryApplyQueue.Outcome.TransferRefused)
			{
				// T-0339 (design C): a registry that does not apply transfers refuses one as a malformed split (400) and
				// changes nothing - a mixed rollout, or a registry rolled back under a node that saw TopologyTransfer.
				// That is no reason to fence: revert the transfer and every change queued behind it (none of them was
				// sent - the worker halts at a refusal - and a later one may be built on it: the split of the rest, a
				// merge of the grown target), dissolve the layouts of unsent claims as the Closed refusal does, stop
				// proposing transfers for the life of this process, and the next pass makes the split and the merge.
				// No hold: the registry is not closing this node.
				m_transferRefused = true;
				m_partitions.RegistryTransfer = false;
				RegistryApplyQueue.Operation[] unsent = m_apply.Abandon();
				PartitionMembership.Change[] refused = unsent.Prepend(operation).Where(x => x.Change != null).Select(x => x.Change).ToArray();
				ulong[] unclaimed = unsent.Where(x => x.Change == null).Select(x => x.Claim).ToArray();
				m_partitions.RevertTopology(refused, unclaimed);
				m_apply.Finish(operation);
				foreach (RegistryApplyQueue.Operation other in unsent) m_apply.Finish(other);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] topology Transfer refused by the registry; transfers off for this process, reverted "
					+ refused.Length + " change(s) in memory, dissolved unclaimed layout(s) [" + string.Join(",", unclaimed)
					+ "], the next pass splits and merges instead: inputs=" + string.Join(",", change.Inputs)
					+ " moved=" + string.Join(",", change.Moved) + " " + SecretRedactor.Redact(operation.Error ?? ""));
				continue;
			}
			if (operation.Outcome == RegistryApplyQueue.Outcome.Closed)
			{
				// T-0048: "Node 'x' epoch N is Closed" is the registry phasing this node out (a graceful cluster stop
				// closes every node at once), not a stale-instance refusal; a 409 on a claim likewise (the heartbeat that
				// reports us Closed may not have landed yet). Fencing here discarded ownership WITHOUT the drain saves
				// on 4 of 5 nodes at the 08:13 stop. Hold the topology and let the drain proceed.
				// Record the stop ONCE, on the transition. Deliberately at stop-BEGIN rather than at exit: a node that
				// will not drain gets force-killed, and a record written on the way out would be exactly the one that
				// never appears. T-0006 and T-0028 both needed this and neither could get it.
				if (!m_closed) m_stopRecordPending = true;
				m_closed = true;
				m_partitions.HoldTopology = true;
				// T-0150: the membership already applied this change - and every later one, none of which was sent.
				// Left applied, a split parked the members in unregistered layouts that no drain can move: 31 grids of
				// 8432 died with node-2 at 03:48. Put them all back into their registered inputs (last first).
				// T-0354b: a new layout whose claim was refused, or never sent, is dissolved. It may have adopted members
				// while its claim was pending; kept as an unregistered placeholder it would hold them where no drain can
				// move them. Unassigned, the next pass places them under the hold: the nearest owned layout up to the
				// split distance (T-0353), else a local placeholder - what a stray on a Closed node got before.
				RegistryApplyQueue.Operation[] unsent = m_apply.Abandon();
				PartitionMembership.Change[] refused = unsent.Prepend(operation).Where(x => x.Change != null).Select(x => x.Change).ToArray();
				ulong[] unclaimed = unsent.Prepend(operation).Where(x => x.Change == null).Select(x => x.Claim).ToArray();
				if (refused.Length != 0 || unclaimed.Length != 0) m_partitions.RevertTopology(refused, unclaimed);
				m_apply.Finish(operation);
				foreach (RegistryApplyQueue.Operation other in unsent) m_apply.Finish(other);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] " + (change == null
						? "partition mint refused by the registry (node phasing out): partition " + operation.Claim
						: "topology WAL apply refused (node Closed, draining): inputs=" + string.Join(",", change.Inputs)
							+ " outputs=" + string.Join(",", change.Outputs.Select(x => x.Id)))
					+ "; holding topology and reverted " + refused.Length + " change(s) in memory, dissolved unclaimed layout(s) ["
					+ string.Join(",", unclaimed) + "]");
				continue;
			}
			if (change == null)
			{
				(ulong id, uint generation) = operation.Answer[0];
				m_state[id] = new State { Generation = generation };
				lock (m_owned)
				{
					m_owned[id] = generation;
					// T-0354b: a pilot of a just-spawned ship is in the 1 Hz client cache at generation 0 when the cache was
					// rebuilt before this answer, and the registry ignores a route at a generation it does not hold - the
					// route would lag up to a second behind df, where the claim was answered inside the pass.
					m_clientPartitions = m_clientPartitions.Select(x => x.Partition == id && x.Generation == 0
						? new PartitionManager.ClientPartition(x.Client, x.Partition, x.Entity, generation) : x).ToArray();
				}
				m_apply.Finish(operation);
				continue;
			}
			foreach (ulong input in change.Inputs) { m_state.TryRemove(input, out _); lock (m_owned) m_owned.Remove(input); }
			foreach ((ulong id, uint generation) in operation.Answer)
			{
				m_state[id] = new State { Generation = generation };
				lock (m_owned) m_owned[id] = generation;
			}
			m_apply.Finish(operation);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] topology " + (change.Transfer ? "Transfer" : change.Split ? "Split" : "Merge")
				+ " committed inputs=" + string.Join(",", change.Inputs) + " outputs=" + string.Join(",", change.Outputs.Select(x => x.Id))
				+ (change.Transfer ? " moved=" + string.Join(",", change.Moved) : "")
				+ (operation.Attempts > 1 ? " after " + operation.Attempts + " attempts" : "")
				+ (change.Why.Length != 0 ? "; why: " + change.Why : ""));
			// The change just re-homed every client near the affected partitions; the 1 Hz
			// cache would keep reporting bindings against the retired input ids for up to a
			// second (KI-2 window). Rebuild it with the commit so the next registry
			// heartbeat carries the post-change mapping immediately.
			IReadOnlyCollection<PartitionManager.Partition> refreshed = m_partitions.Evaluate();
			lock (m_owned)
				m_clientPartitions = m_partitions.ClientPartitions(refreshed)
					.Select(x => new PartitionManager.ClientPartition(x.Client, x.Partition, x.Entity,
						m_owned.TryGetValue(x.Partition, out uint generation) ? generation : 0)).ToArray();
		}
	}

	/// <summary>Rollback (CLUSTER_REGISTRY_APPLY_ASYNC=0): send what is queued and take the answers here, on the game thread, as before T-0354.</summary>
	private void DrainRegistryAppliesInline()
	{
		while (!m_fenced)
		{
			RegistryApplyQueue.Progress progress = m_apply.Step(SendRegistryApply);
			if (progress == RegistryApplyQueue.Progress.Unavailable) Thread.Sleep(1000);
			else if (progress == RegistryApplyQueue.Progress.Idle) break;
		}
		CompleteRegistryApplies();
	}

	private void RegisterNodeAndClaims()
	{
		using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
		ClusterRuntime.AuthorizeRegistryClient(client);
		m_nodeEpoch = ClusterRuntime.RegistryNodeEpoch;
		while (m_nodeEpoch == 0)
		{
			try
			{
				using JsonDocument node = ClusterRuntime.RegisterNode(client, m_gateway, m_node, m_endpoint);
				m_nodeEpoch = node.RootElement.GetProperty("Epoch").GetInt64();
			}
			catch (Exception exception)
			{
				// Orchestration §4.2: rejection is terminal at every step, not only at Hello.
				if (ClusterRuntime.IsDefinitiveRejection(exception))
				{
					FenceForRejection("node registration rejected: " + exception.Message);
					return;
				}
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] registry unavailable, retrying: " + SecretRedactor.Redact(exception.Message));
				Thread.Sleep(1000);
			}
		}
		while (true)
		{
			try
			{
				Directory.CreateDirectory(m_root);
				string epochPath = Path.Combine(m_root, "node-epoch");
				using (FileStream stream = new FileStream(epochPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
				{
					using StreamWriter writer = new StreamWriter(stream);
					writer.Write(m_nodeEpoch.ToString(CultureInfo.InvariantCulture));
					writer.Flush();
					stream.Flush(true);
				}
				File.Move(epochPath + ".tmp", epochPath, true);
				break;
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] node epoch persistence unavailable, retrying: " + SecretRedactor.Redact(exception.Message));
				Thread.Sleep(1000);
			}
		}
		foreach ((ulong id, State state) in m_state.ToArray())
		{
			long expectedGeneration = state.Generation - 1L;
			bool retriedAtRegistryGeneration = false;
			while (true)
			{
				try
				{
					using JsonDocument owner = Post(client, "/registry/partitions?partition=" + id.ToString(CultureInfo.InvariantCulture)
						+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
						+ "&expectedGeneration=" + expectedGeneration.ToString(CultureInfo.InvariantCulture));
					// A successful claim means this node's assigned save IS the newest generation - the
					// expectedGeneration check refuses stale assigned saves (they 409 into ordinary
					// catalog recovery below). Keep the claim: the assigned content is already loaded
					// and the next capture registers it into the catalog. Releasing here instead
					// orphaned every partition of a fresh split (whose seed saves the catalog has never
					// seen, so "recovery" had nothing to claim) and silently dropped the whole Earth
					// world on 2026-08-10 - see LostEarthStarterBase.md.
					state.Generation = checked((uint)owner.RootElement.GetProperty("Generation").GetInt64());
					// Captures continue after the assigned save's sequence: the assigned save itself is
					// catalogued below under that sequence, so restarting from 0 would collide with it.
					state.Sequence = state.AssignedSequence;
					lock (m_owned) m_owned[id] = state.Generation;
					m_startupFenced.TryRemove(id, out _);
					// Ownership confirmed: the loaded content is legitimate, release the fence taint.
					UntaintAssigned(id);
					m_assignedMembers.TryRemove(id, out _);
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] startup partition claimed: " + id
						+ " generation " + state.Generation);
					RegisterAssignedSave(client, id, state);
					break;
				}
				catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
				{
					// T-0139. Say WHICH conflict this is. A relaunching node re-posts claims for the
					// partitions its TEMPLATE world carries before it has learned the live generations,
					// and those arrive with Generation 0 - so expectedGeneration is -1 and the registry
					// refuses them, which is correct. On w31 that was exactly partitions 1-5, five per
					// rotation, never any of the ~64 created in-session (no template carries those).
					// Benign for content and pure noise in the log - but a GENUINE generation conflict
					// arrives here too and used to be indistinguishable, so it got dismissed with them.
					// The registry's refusal says which: its generation ahead of ours is a stale local
					// copy (T-0155: the seeds arrive at generation 1 from the node data dirs, not 0).
					// T-0207: ONE SHAPE HERE IS NOT A STALE COPY, AND FENCING IT DEADLOCKS THE NODE.
					// A node that restarts INSIDE ITS OWN LEASE still owns its partitions in the
					// registry - nothing expired them, and its heartbeat keeps them alive. Its
					// content is at exactly the generation the registry holds, so the claim above
					// (expectedGeneration = generation - 1) is one behind and 409s. Fencing then
					// waits for a RECOVERY THAT CANNOT COME: recovery is only offered for UNOWNED
					// partitions, and this one is owned by the node doing the waiting. Measured
					// 2026-09-21: 1 run in 12 of P3-REC-01, the node Serving with a partition it
					// refuses to use and a save re-offered every 6 s for as long as the run lasts.
					// The registry's own refusal states its generation, so ask it for the claim it
					// will accept - ONCE, and only when it confirms WE are the owner and our
					// content is AT that generation rather than behind it. A registry ahead of us
					// is the genuinely stale case the comment above describes and still fences.
					if (!retriedAtRegistryGeneration
						&& state.Generation != 0
						&& TryReadRegistryGeneration(exception.Message, out long registryGeneration)
						&& registryGeneration == state.Generation
						&& PartitionOwnerIsThisNode(client, id))
					{
						retriedAtRegistryGeneration = true;
						expectedGeneration = registryGeneration;
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] startup partition still owned by this node across the restart: "
							+ id + " (local generation " + state.Generation + ", registry at " + registryGeneration
							+ ") - reclaiming at the registry's generation instead of fencing (T-0207)");
						continue;
					}
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] startup partition fenced, waiting for recovery: " + id
						+ " (" + DescribeStartupConflict(state.Generation, exception.Message) + ")");
					m_startupFenced[id] = 0;
					break;
				}
				catch (Exception exception)
				{
					// A non-409 4xx answer (rejected node, bad request) is definitive - §4.2.
					if (ClusterRuntime.IsDefinitiveRejection(exception))
					{
						FenceForRejection("startup partition claim rejected: " + exception.Message);
						return;
					}
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] partition claim unavailable, retrying: " + SecretRedactor.Redact(exception.Message));
					Thread.Sleep(1000);
				}
			}
		}
		// ponytail: suppress recovery briefly while peers register, but keep heartbeats running so short leases stay live.
		m_recoveryAfter = DateTime.UtcNow.AddSeconds(5);
		// A crashed claimant leaves partitions unowned at a generation no Final save reaches (node-3's
		// zombie bumped 16 partitions that way, 2026-08-11): no node stages them as warm candidates, so
		// candidate-only claiming would leave them unowned forever. Once the warm boot has settled -
		// every peer's startup and staged claims are long done - fall back to the registry's triage
		// auto-pick so leftovers converge like a cold recovery would.
		m_genericRecoveryAfter = DateTime.UtcNow.AddSeconds(60);
		// The heartbeat starts BEFORE the voxel wait below: the partitions claimed above hold leases
		// from this moment, and the wait can take up to its full timeout. With the heartbeat after the
		// wait, every lease expired during it (bench baseline 2026-09-17: a planetless world waited
		// 120 s against a 15 s lease and all three partitions sat unowned). The heartbeat reports
		// Serving=false until SetNodeState below, so placement still waits for the node.
		Thread registry = new Thread(() => RegistryLoop(client)) { IsBackground = true, Name = "ClusterRegistryHeartbeat" };
		registry.Start();
		// T-0347: with the heartbeat running (the report may wait for the registry) and before the replication worker
		// below makes this node's first recovery claim.
		ReportUnregisteredFinals(client);
		// T-0053: do not announce Serving while the voxel replica is still replaying the journal.
		// Partitions imported onto un-edited terrain fall through it. Bounded, fails open.
		VoxelJournalService.Instance?.WaitForCatchUp(TimeSpan.FromSeconds(
			double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_VOXEL_CATCHUP_SECONDS"),
				NumberStyles.Float, CultureInfo.InvariantCulture, out double catchUpSeconds) && catchUpSeconds >= 0 ? catchUpSeconds : 120));
		m_registryReady = true;
		ClusterRuntime.SetNodeState("Serving");
		ClusterControlServer.WarmUpImportPath();
		new Thread(() => ReplicationLoop(client)) { IsBackground = true, Name = "ClusterReplicationWorker" }.Start();
		registry.Join();
	}

	/// <summary>
	/// Bulk data-plane work - the deletion sweep, save replication and recovery claims - on its own
	/// thread. Every download here can block for as long as a peer's control server is busy (a
	/// station-sized import holds it for seconds), and partition leases renew only through the
	/// heartbeat: when this work shared RegistryLoop, one slow peer stopped the heartbeats and a
	/// healthy source lost a mid-handover partition to lease expiry and a recovery claim.
	/// </summary>
	/// <summary>
	/// Every entity id the OTHER nodes currently hold, read from their live censuses. Runs on the
	/// replication thread only (each peer request can block for seconds). Fail-open per peer: a
	/// peer that does not answer contributes nothing, so a genuinely dead node never stalls
	/// recovery - the cost of missing it is the pre-existing duplicate risk, not a new one.
	/// Pending (still-initializing) members appear in peer censuses as type "missing" and COUNT:
	/// the census blind spot around parallel init is what let every previous duplicate through.
	/// </summary>
	/// <summary>
	/// One peer partition's contribution to the recovery census: its top-level members and, since T-0226,
	/// the pilots seated in its grids' cockpits (absent on an older peer, which then screens members only).
	/// </summary>
	internal static void AddCensusIds(JsonElement partition, HashSet<long> live, string node = null)
	{
		CensusHolder? holder = live is LiveCensus && partition.TryGetProperty("id", out JsonElement id) && id.TryGetUInt64(out ulong value)
			? new CensusHolder(node, value) : null;
		foreach (JsonElement member in partition.GetProperty("members").EnumerateArray())
			AddCensusId(live, member.GetProperty("id").GetInt64(), holder);
		if (partition.TryGetProperty("seated", out JsonElement seated))
			foreach (JsonElement pilot in seated.EnumerateArray())
				AddCensusId(live, pilot.GetInt64(), holder);
	}

	private static void AddCensusId(HashSet<long> live, long entity, CensusHolder? holder)
	{
		live.Add(entity);
		if (holder is CensusHolder copy && live is LiveCensus census)
		{
			if (!census.HoldersOf.TryGetValue(entity, out List<CensusHolder> holders)) census.HoldersOf[entity] = holders = new List<CensusHolder>();
			holders.Add(copy);
		}
	}

	/// <summary>One peer copy in the recovery census: the node that reported it (null when unknown) and its partition id.</summary>
	internal readonly record struct CensusHolder(string Node, ulong Partition);

	/// <summary>
	/// T-0295: the recovery census, with the peer partition that holds each live entity. A recovery of
	/// partition P must not be vetoed by a peer's copy of P itself: the registry has just granted THIS node
	/// the newer generation, so another holder of the same id is a stale claimant - a claim whose lease ran
	/// out before it loaded, and whose copy is discarded ("discarded stale local ownership") a moment later.
	/// On 2026-09-29 16:53 node-1 loaded 227734 under such a claim, node-5's recovery census saw its copy,
	/// skipped BOTH members as "already live on another node", registered an EMPTY Final, and node-1 then
	/// dropped its copy: a ship and its pilot were gone. Same class as the startup-fenced copies (the
	/// 'fenced' flag), which a lease-expired claim is not.
	/// </summary>
	/// <remarks>
	/// #1286 (F41) is the cross-partition variant: a peer's copy of ANOTHER partition Q that the registry no longer
	/// assigns to that peer (its node is Dead while the process still answers, or Q's row names another owner or
	/// none) is about to be discarded, and an entity adopted into Q after Q's last save lives nowhere else. So a
	/// copy screens only while the registry still assigns it to its node: <see cref="Verdict"/> on the node rows
	/// of the census and the partition rows <see cref="Rows"/> fetched for the copies that hold recovered content.
	/// </remarks>
	internal sealed class LiveCensus : HashSet<long>
	{
		public Dictionary<long, List<CensusHolder>> HoldersOf { get; } = new Dictionary<long, List<CensusHolder>>();
		/// <summary>The state (NodeState) of every registered node but the drained-out (Empty) ones, this node included.</summary>
		public Dictionary<string, int> Nodes { get; } = new Dictionary<string, int>();
		/// <summary>Registry partition rows fetched so far: whether the row exists and its owner node (null = none).</summary>
		public Dictionary<ulong, (bool Found, string Owner)> Rows { get; } = new Dictionary<ulong, (bool Found, string Owner)>();

		/// <summary>The census for recovering PARTITION: without the members peers hold only in that same partition id.</summary>
		public HashSet<long> ExcludingLineage(ulong partition, out int excluded)
		{
			HashSet<long> screened = Screened(holder => holder.Partition != partition, out HashSet<long> unscreened);
			excluded = unscreened.Count;
			return screened;
		}

		/// <summary>The ids at least one copy that COUNTS holds; UNSCREENED gets the ids whose every copy does not count.</summary>
		public HashSet<long> Screened(Func<CensusHolder, bool> counts, out HashSet<long> unscreened)
		{
			HashSet<long> screened = new HashSet<long>();
			unscreened = new HashSet<long>();
			foreach (long entity in this)
				if (!HoldersOf.TryGetValue(entity, out List<CensusHolder> holders) || holders.Any(counts)) screened.Add(entity);
				else unscreened.Add(entity);
			return screened;
		}

		/// <summary>
		/// Whether the registry still assigns a copy to its node. Stale: the node's row is Dead (its process may still
		/// answer), or the partition's row names another live registration - the copy is discarded at the node's next
		/// heartbeat. Uncertain: the row names no owner, or one that is Dead or not registered - a lapsed claim being
		/// discarded, or a booting node's copy its startup claim is about to confirm; only a later census tells which.
		/// Current otherwise, and whenever a row or the node's registration is unknown (as before #1286, duplicate-safe).
		/// The epoch is not compared: a restarted node's boot copy names its old epoch until the startup claim.
		/// </summary>
		public CopyVerdict Verdict(CensusHolder holder)
		{
			if (holder.Node == null || !Nodes.TryGetValue(holder.Node, out int state)) return CopyVerdict.Current;
			if (state == NodeStateDead) return CopyVerdict.Stale;
			if (!Rows.TryGetValue(holder.Partition, out (bool Found, string Owner) row) || !row.Found || row.Owner == holder.Node)
				return CopyVerdict.Current;
			return row.Owner != null && Nodes.TryGetValue(row.Owner, out int owner) && owner != NodeStateDead
				? CopyVerdict.Stale : CopyVerdict.Uncertain;
		}

		/// <summary>Why a copy is not current, for the log.</summary>
		public string StaleReason(CensusHolder holder) =>
			Nodes.TryGetValue(holder.Node, out int state) && state == NodeStateDead ? "node Dead"
			: Rows.TryGetValue(holder.Partition, out (bool Found, string Owner) row) && row.Found ? "registry owner " + (row.Owner ?? "none") : "unknown";

		/// <summary>
		/// The census for a load of PARTITION whose content holds COVER: the ids a copy that counts holds. A copy counts
		/// unless it is the same lineage (when EXCLUDELINEAGE; T-0295), stale, or uncertain (when UNCERTAINCOUNTS is
		/// false). STALEONLY gets the cover ids that only such copies hold (other than copies of the same lineage).
		/// </summary>
		public HashSet<long> ScreenedForLoad(ulong partition, bool excludeLineage, bool uncertainCounts, IEnumerable<long> cover,
			out HashSet<long> staleOnly)
		{
			bool Lineage(CensusHolder holder) => excludeLineage && holder.Partition == partition;
			HashSet<long> screened = Screened(holder => !Lineage(holder) && Verdict(holder) switch
			{
				CopyVerdict.Current => true,
				CopyVerdict.Uncertain => uncertainCounts,
				_ => false,
			}, out HashSet<long> unscreened);
			staleOnly = cover.Where(entity => unscreened.Contains(entity) && HoldersOf[entity].Any(holder => !Lineage(holder))).ToHashSet();
			return screened;
		}
	}

	internal enum CopyVerdict { Current, Uncertain, Stale }

	private const int NodeStateDead = 2;   // ClusterRegistry NodeState.Dead
	private const int NodeStateEmpty = 3;  // ClusterRegistry NodeState.Empty

	/// <summary>
	/// #1286: fetches the registry rows of the partitions whose copies hold any of COVER (and that the census has
	/// not read yet), one small GET each - only the copies a load would be screened against, not the 200k-row report.
	/// A row that cannot be read stays unknown, so its copy screens.
	/// </summary>
	private void FetchCensusRows(HttpClient client, LiveCensus census, IEnumerable<long> cover)
	{
		foreach (ulong partition in cover.Where(census.HoldersOf.ContainsKey).SelectMany(entity => census.HoldersOf[entity])
			.Where(holder => holder.Node != null && census.Nodes.TryGetValue(holder.Node, out int state) && state != NodeStateDead)
			.Select(holder => holder.Partition).Distinct().Where(partition => !census.Rows.ContainsKey(partition)).ToArray())
		{
			try
			{
				using JsonDocument rows = Get(client, "/registry/partitions?partition=" + partition.ToString(CultureInfo.InvariantCulture));
				JsonElement row = rows.RootElement.EnumerateArray().FirstOrDefault();
				census.Rows[partition] = (row.ValueKind == JsonValueKind.Object,
					row.ValueKind == JsonValueKind.Object && row.TryGetProperty("Node", out JsonElement node) && node.ValueKind == JsonValueKind.String
						? node.GetString() : null);
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery census: registry row of partition " + partition
					+ " unavailable (" + SecretRedactor.Redact(exception.Message) + "); its copies screen as live");
			}
		}
	}

	/// <summary>#1286: the census screen for a load of PARTITION with content COVER, logging the copies it set aside.</summary>
	private HashSet<long> VerifiedScreen(HttpClient client, LiveCensus census, ulong partition, bool excludeLineage, bool uncertainCounts,
		HashSet<long> cover, out HashSet<long> staleOnly)
	{
		FetchCensusRows(client, census, cover);
		HashSet<long> screened = census.ScreenedForLoad(partition, excludeLineage, uncertainCounts, cover, out staleOnly);
		if (staleOnly.Count > 0)
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery census: " + staleOnly.Count + " entit(ies) of partition " + partition
				+ " are held only by peer copies the registry does not assign to their node (#1286): " + string.Join("; ", staleOnly.Take(8).Select(entity =>
					entity + " in " + string.Join(",", census.HoldersOf[entity].Where(holder => census.Verdict(holder) != CopyVerdict.Current).Select(holder =>
						holder.Node + "/" + holder.Partition + " (" + census.StaleReason(holder) + ")")))));
		return screened;
	}

	/// <summary>
	/// #1286 (F41), game thread: whether a recovery load goes back to the replication thread instead of loading now.
	/// The first time peers hold any of the content, so that check reads the registry rows of those copies; then
	/// while content is held only by stale copies, for a fresh census at least StaleCopyRecheckSeconds later, StaleCopyRechecks
	/// times at most (section 4: the bounded re-check). The claim stays pending meanwhile and its lease rides the
	/// heartbeat. After the last re-check the load takes the content that only stale copies hold.
	/// </summary>
	private bool DeferForCensusCheck(RecoveryClaim recovery, Dictionary<long, MyObjectBuilder_EntityBase> entities)
	{
		HashSet<long> cover = entities.Keys.Concat(CharactersIn(entities.Values).Select(character => character.EntityId)).ToHashSet();
		if (!recovery.CensusVerified)
		{
			if (recovery.LiveElsewhere is not { Count: > 0 } || !cover.Overlaps(recovery.LiveElsewhere)) return false;
			m_censusChecks.Enqueue((recovery with { Cover = cover }, DateTime.UtcNow, false));
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery of partition " + recovery.Partition + " generation " + recovery.Generation
				+ ": peers hold some of its content; checking those copies against the registry before the load (#1286)");
			return true;
		}
		long[] staleOnly = recovery.StaleOnly == null ? Array.Empty<long>() : cover.Where(recovery.StaleOnly.Contains).ToArray();
		if (staleOnly.Length == 0) return false;
		if (recovery.CensusRechecks < StaleCopyRechecks)
		{
			m_censusChecks.Enqueue((recovery with { Cover = cover }, DateTime.UtcNow.AddSeconds(StaleCopyRecheckSeconds), true));
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery of partition " + recovery.Partition + " generation " + recovery.Generation
				+ " deferred: " + staleOnly.Length + " entit(ies) are held only by stale peer copies [" + string.Join(",", staleOnly.Take(16))
				+ "]; re-check " + (recovery.CensusRechecks + 1) + "/" + StaleCopyRechecks + " in " + StaleCopyRecheckSeconds + " s (#1286)");
			return true;
		}
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery of partition " + recovery.Partition + " generation " + recovery.Generation
			+ " loads " + staleOnly.Length + " entit(ies) held only by stale peer copies after " + recovery.CensusRechecks
			+ " re-check(s) [" + string.Join(",", staleOnly.Take(16)) + "] (#1286)");
		return false;
	}

	/// <summary>#1286, replication thread: the census checks DeferForCensusCheck handed back, each re-queued for its load.</summary>
	private void RunCensusChecks(HttpClient client)
	{
		for (int pending = m_censusChecks.Count; pending > 0 && m_censusChecks.TryDequeue(out var check); pending--)
		{
			if (check.Due > DateTime.UtcNow)
			{
				m_censusChecks.Enqueue(check);
				continue;
			}
			RecoveryClaim recovery = check.Recovery;
			try
			{
				LiveCensus census = check.Fresh ? SnapshotLiveEntitiesElsewhere(client) : recovery.Census;
				HashSet<long> screened = VerifiedScreen(client, census, recovery.Partition, excludeLineage: true, uncertainCounts: false,
					recovery.Cover, out HashSet<long> staleOnly);
				recovery = recovery with
				{
					Census = census, LiveElsewhere = screened, StaleOnly = staleOnly, CensusVerified = true,
					CensusRechecks = recovery.CensusRechecks + (check.Fresh ? 1 : 0)
				};
			}
			catch (Exception exception)
			{
				// Unchecked, the load screens against every copy as before #1286 - never a lost claim over a failed check.
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery census check of partition " + recovery.Partition
					+ " failed, loading with the unchecked census: " + SecretRedactor.Redact(exception.Message));
				recovery = recovery with { CensusVerified = true, StaleOnly = null };
			}
			m_recoveries.Enqueue(recovery);
			ClusterRuntime.Wake();
		}
	}

	private LiveCensus SnapshotLiveEntitiesElsewhere(HttpClient client)
	{
		LiveCensus live = new LiveCensus();
		List<string> controls = new List<string>();
		// #1286: the registration that answers for each control endpoint - the newest one, when a reused port slot
		// has several - so each copy in the census can be checked against the registry's ownership of it.
		Dictionary<string, (string Node, DateTimeOffset Registered)> answering = new Dictionary<string, (string Node, DateTimeOffset Registered)>();
		try
		{
			using JsonDocument nodes = Get(client, "/registry/nodes");
			foreach (JsonElement node in nodes.RootElement.EnumerateArray())
			{
				string name = node.GetProperty("Node").GetString();
				// T-0338: not the rows of nodes that drained to Empty (3) and stopped. They are kept for their saves
				// (T-0323), 31 of 40 on r85, and their port slots are reused: each was asked too, so one slow dormant
				// node was asked up to four times at 5 s each. A Dead row stays: its process may still be alive.
				int state = node.TryGetProperty("State", out JsonElement stateElement) && stateElement.ValueKind == JsonValueKind.Number
					? stateElement.GetInt32() : -1;
				// This node's own row too, never asked: a peer's copy of a partition the registry assigns HERE is stale.
				if (name != null && state != NodeStateEmpty) live.Nodes[name] = state;
				if (name == m_node || state == NodeStateEmpty) continue;
				if (!node.TryGetProperty("ControlEndpoint", out JsonElement control)) continue;
				string endpoint = control.GetString();
				if (string.IsNullOrWhiteSpace(endpoint)) continue;
				endpoint = endpoint.TrimEnd('/');
				if (!controls.Contains(endpoint)) controls.Add(endpoint);
				if (name == null) continue;
				DateTimeOffset registered = node.TryGetProperty("Registered", out JsonElement at) && at.ValueKind == JsonValueKind.String
					&& at.TryGetDateTimeOffset(out DateTimeOffset when) ? when : DateTimeOffset.MinValue;
				if (!answering.TryGetValue(endpoint, out var known) || registered >= known.Registered) answering[endpoint] = (name, registered);
			}
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery census: node list unavailable, "
				+ "proceeding without duplicate screening: " + SecretRedactor.Redact(exception.Message));
			return live;
		}
		using HttpClient peer = ClusterHttp.Client(TimeSpan.FromSeconds(5));
		// T-0321: N7 is a peer route; over a LAN prefix it needs the join token, which this census never sent.
		ClusterRuntime.AuthorizeRegistryClient(peer);
		// All peers at once: the census is as slow as the slowest peer, not the sum (a wake has a 16 s lead).
		Task<string>[] asked = controls.Select(control => peer.GetStringAsync(control + "/cluster/partitions")).ToArray();
		for (int index = 0; index < controls.Count; index++)
		{
			string control = controls[index];
			try
			{
				string body = asked[index].GetAwaiter().GetResult();
				using JsonDocument census = JsonDocument.Parse(body);
				foreach (JsonElement partition in census.RootElement.GetProperty("partitions").EnumerateArray())
				{
					// A startup-fenced partition on the peer is a provisional copy of content the registry
					// owns elsewhere (possibly the very lineage being recovered here); it is purged or
					// claimed later and must not veto this recovery - see PartitionManager.IsStartupFenced.
					if (partition.TryGetProperty("fenced", out JsonElement fenced) && fenced.ValueKind == JsonValueKind.True)
					{
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery census: peer " + control + " partition "
							+ partition.GetProperty("id").GetRawText() + " is startup-fenced; its members are not screened");
						continue;
					}
					AddCensusIds(partition, live, answering.TryGetValue(control, out var holder) ? holder.Node : null);
				}
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery census: peer " + control
					+ " did not answer (" + SecretRedactor.Redact(exception.Message) + "); its content is not screened");
			}
		}
		return live;
	}

	private void ReplicationLoop(HttpClient client)
	{
		while (!m_fenced && !m_exitRequested)
		{
			try
			{
				// #1286: before the closed check - a deferred load is a claim this node holds, and a drain waits for it.
				RunCensusChecks(client);
				if (!m_closed)
				{
					// T-0163: retention is housekeeping. A directive this node cannot carry out must
					// not skip replication and the recovery claim below, or a node whose data root
					// moved never recovers anything.
					try { ApplyDeletes(client); }
					catch (Exception exception) when (exception is not HttpRequestException { StatusCode: HttpStatusCode.Conflict })
					{
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] retention unavailable: " + SecretRedactor.Redact(exception.Message));
					}
					// T-0323: no per-node pace. The registry hands out copies against one cluster-wide byte budget
					// (ReplicaPacing.cs), so this node takes them for as long as it is handed one - but for a second
					// at most, so the recovery claim below still runs every loop.
					Stopwatch slice = Stopwatch.StartNew();
					bool copied;
					while ((copied = ReplicateOne(client)) && !m_closed && !m_exitRequested && slice.ElapsedMilliseconds < 1000) { }
					// T-0399: no copy handed out - the worker is idle, so it re-reads one held global copy (PartitionPersistence.Scrub.cs).
					if (!copied && !m_closed && !m_exitRequested) ScrubGlobals(client);
					if (DateTime.UtcNow >= m_recoveryAfter
						&& (!m_warmRecovery || !m_recoveryCandidates.IsEmpty || DateTime.UtcNow >= m_genericRecoveryAfter)
						&& BelowRecoveryCapacity() && !m_closed)
					{
						try
						{
							ulong[] requested = m_warmRecovery ? m_recoveryCandidates.Keys.ToArray() : Array.Empty<ulong>();
							using JsonDocument claims = PostJson(client, "/registry/recoveries/claim", new { Node = m_node, Epoch = m_nodeEpoch, Partitions = requested });
							// T-0295: every granted claim rides the heartbeat from the moment of the grant. It used
							// to join m_claimedPending only after the peer census (5 s per silent peer) and the
							// materialization (a save fetched from a slow holder): node-1 took 36 s over both
							// against the 15 s lease, the registry re-offered 227734 to node-5, and the two
							// recoveries of one partition ended with neither keeping it (2026-09-29 16:52-16:53).
							(JsonElement Claim, ulong Partition, uint Generation)[] granted = claims.RootElement.EnumerateArray()
								.Select(claim => { JsonElement ownership = claim.GetProperty("Ownership");
									return (claim, ownership.GetProperty("Partition").GetUInt64(), checked((uint)ownership.GetProperty("Generation").GetInt64())); })
								.ToArray();
							if (LeaseAtGrant) foreach (var grant in granted) m_claimedPending[grant.Partition] = grant.Generation;
							int enqueued = 0;
							try
							{
								if (granted.Length != 0 && RecoveryClaimDelayMs > 0)
								{
									// Bench seam (CLUSTER_TEST_RECOVERY_CLAIM_DELAY_MS): hold the granted claims past
									// the lease here, where the census and a slow save fetch spend the time.
									MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery claim test delay " + RecoveryClaimDelayMs + " ms (T-0295)");
									Thread.Sleep(RecoveryClaimDelayMs);
								}
								LiveCensus liveElsewhere = null;
								foreach (var grant in granted)
								{
									JsonElement claim = grant.Claim;
									// One census snapshot per claim batch, taken on this thread so the
									// game thread never blocks on peers; see SnapshotLiveEntitiesElsewhere.
									liveElsewhere ??= SnapshotLiveEntitiesElsewhere(client);
									RecoveryClaim materialized = MaterializeRecovery(ParseRecovery(claim), client);
									HashSet<long> screened = liveElsewhere.ExcludingLineage(materialized.Partition, out int stale);
									if (stale > 0)
										MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery census: " + stale
											+ " member(s) of partition " + materialized.Partition + " live on a peer under an older claim of the"
											+ " same partition (stale claimant, T-0295); not screened");
									RecoveryClaim parsed = materialized with { LiveElsewhere = screened, Census = liveElsewhere };
									if (!LeaseAtGrant) m_claimedPending[parsed.Partition] = parsed.Generation;
									m_recoveryCandidates.TryRemove(parsed.Partition, out _);
									m_recoveries.Enqueue(parsed);
									enqueued++;
									// A dormant spare must be at full rate BEFORE the recovery import
									// lands on the game thread, not 250 ms into it.
									ClusterRuntime.Wake();
									// A materialized claim clears the T-0099 backoff: the condition it guards
									// against is "this node cannot yet build this partition", and it evidently can.
									m_recoveryFailures = 0;
									MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery claimed partition " + parsed.Partition
										+ " generation " + parsed.Generation + " from " + parsed.Files.Length + " save(s)");
								}
							}
							finally
							{
								// A claim that never reached the load queue (a materialization failure below) is
								// not renewed forever: it lapses on the lease as before and the registry re-offers it.
								foreach (var grant in granted.Skip(enqueued))
									m_claimedPending.TryRemove(new KeyValuePair<ulong, uint>(grant.Partition, grant.Generation));
							}
						}
						catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict) { }
					}
				}
			}
			catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
			{
				// Never fence from here: a conflict on this thread is a state transition seen
				// mid-race (the registry refuses replica/deletion polls the moment the node turns
				// Closed, before the next heartbeat tells us). The heartbeat loop sees the same
				// registry within a second and owns the epoch-fence verdict; fencing from this
				// thread killed every drain by discarding ownership before the Final saves.
			}
			catch (FileNotFoundException exception)
			{
				// T-0099: the claim SUCCEEDED at the registry - the generation is already spent -
				// and materialization then failed because the newest save has not replicated to
				// this node yet. Retrying at 1 Hz spends another generation per attempt and cannot
				// help: the save arrives on the replication schedule, not on demand. So back off
				// 2, 4, 8 ... 60 s. The window is real and self-closing (both recorded cases
				// resolved once the save landed), and a peer that DOES hold the save is not blocked
				// by this node waiting.
				int backoff = Math.Min(60, 1 << Math.Min(6, ++m_recoveryFailures));
				m_recoveryAfter = DateTime.UtcNow.AddSeconds(backoff);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery materialization failed ("
					+ m_recoveryFailures + " in a row), backing off " + backoff + "s: "
					+ SecretRedactor.Redact(exception.Message));
			}
			catch (Exception exception)
			{
				// T-0350: no NoteRegistryFailure here - this catch also sees a PEER that does not answer (a save fetch, the
				// census). The registry requests of this loop report themselves (Get/Post/PostJson).
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] replication worker unavailable: " + SecretRedactor.Redact(exception.Message));
			}
			Thread.Sleep(1000);
		}
	}

	// T-0295: a granted recovery claim renews its lease from the grant. CLUSTER_RECOVERY_LEASE_AT_GRANT=0
	// restores the old order (renewal only once materialized) for the bench red of P1-REC-14.
	private static readonly bool LeaseAtGrant = Environment.GetEnvironmentVariable("CLUSTER_RECOVERY_LEASE_AT_GRANT") != "0";
	// T-0297: a pending claim the registry no longer holds at its generation is dropped, not loaded.
	// CLUSTER_RECOVERY_DROP_LOST_PENDING=0 restores loading it (the bench red of P1-REC-15).
	private static readonly bool DropLostPending = Environment.GetEnvironmentVariable("CLUSTER_RECOVERY_DROP_LOST_PENDING") != "0";
	// Bench seam only (T-0297): the heartbeat leaves pending claims out of its request, so their leases lapse
	// while the node keeps heartbeating - a claim the registry takes back while it is still queued.
	private static readonly bool HeartbeatOmitsPending = Environment.GetEnvironmentVariable("CLUSTER_TEST_HEARTBEAT_OMIT_PENDING") == "1";
	// Bench seam only (T-0295): see the recovery claim in ReplicationLoop. 0 = off.
	private static readonly int RecoveryClaimDelayMs =
		int.TryParse(Environment.GetEnvironmentVariable("CLUSTER_TEST_RECOVERY_CLAIM_DELAY_MS"), out int delay) && delay > 0 ? delay : 0;
	// #1286 (F41): a peer copy screens a recovered entity only if the registry still assigns that copy to its node.
	// CLUSTER_RECOVERY_STALE_COPY_CHECK=0 restores screening against every copy a peer reports (the red).
	private static readonly bool StaleCopyCheck = Environment.GetEnvironmentVariable("CLUSTER_RECOVERY_STALE_COPY_CHECK") != "0";
	// #1286: how many fresh censuses, StaleCopyRecheckSeconds apart, a recovery waits for an entity held only by stale
	// copies to show up in a current one (or vanish) before it loads the entity itself (section 4: the bounded re-check).
	// 8 x 2-3 s outlasts a booting peer's startup-claim window (~12 s), which turns its uncertain copies current (a
	// replication loop busy elsewhere spaces the re-checks further).
	private static readonly int StaleCopyRechecks =
		int.TryParse(Environment.GetEnvironmentVariable("CLUSTER_RECOVERY_STALE_COPY_RECHECKS"), out int rechecks) && rechecks >= 0 ? rechecks : 8;
	private const int StaleCopyRecheckSeconds = 2;
	// Bench seam only (#1286): CLUSTER_TEST_DISCARD_DELAY_MS holds this node's discard of lost ownership and its fenced exit
	// (see Update). 0 = off.
	private static readonly int DiscardDelayMs =
		int.TryParse(Environment.GetEnvironmentVariable("CLUSTER_TEST_DISCARD_DELAY_MS"), out int discardDelay) && discardDelay > 0 ? discardDelay : 0;
	// LoadRecovery's answer for a load handed back to the replication thread (m_censusChecks) instead of loaded.
	private const int DeferredLoad = -1;

	private bool BelowRecoveryCapacity()
	{
		if (m_maxPartitions == 0) return true;
		lock (m_owned) return m_owned.Count < m_maxPartitions;
	}

	private DateTime m_lastHitchUtc = DateTime.MinValue;
	/// <summary>UtcNow ticks when the registry first reported this node Closed (T-0048 memberless release gate).</summary>
	private long m_closedAtTicks;
	private long m_appliedConfigRevision = -1;

	/// <summary>
	/// Hands failed recovery claims back to the registry. T-0317: the registry refuses the release (409
	/// "refusing release") while a connected client is routed to the partition - its character home, say, when
	/// the player is reconnecting. That 409 is the gateway's guard, not an epoch fence: treating it as one killed
	/// the node, and the next claimant the same way on the player's next attempt (r32 2026-09-30 20:27-20:30,
	/// partition 279043, three node deaths; T-0316 removed that load failure, this removes the fence). The same
	/// lesson as the drain release (w14 node-4). The release is retried every RetryFailedReleaseSeconds until the
	/// route moves or the client leaves. Any other 409 on this release (the claim moved on: a stale generation or
	/// owner) drops the retry; a real epoch fence still reaches the heartbeat below and fences the node there.
	/// </summary>
	private void ReleaseFailedRecoveries(HttpClient client)
	{
		DateTime nowUtc = DateTime.UtcNow;
		foreach (KeyValuePair<ulong, (RecoveryClaim Claim, DateTime Due)> deferred in m_deferredFailedReleases.ToArray())
			if (deferred.Value.Due <= nowUtc)
			{
				m_deferredFailedReleases.Remove(deferred.Key);
				m_failedRecoveries.Enqueue(deferred.Value.Claim);
			}
		while (m_failedRecoveries.TryDequeue(out RecoveryClaim failed))
		{
			m_claimedPending.TryRemove(failed.Partition, out _);
			try
			{
				Post(client, "/registry/partitions/release?partition=" + failed.Partition.ToString(CultureInfo.InvariantCulture)
					+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
					+ "&generation=" + failed.Generation.ToString(CultureInfo.InvariantCulture)).Dispose();
			}
			catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
			{
				if (!exception.Message.Contains("refusing release", StringComparison.Ordinal))
				{
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] failed recovery claim " + failed.Partition + " generation " + failed.Generation
						+ " not released, the claim moved on (T-0317): " + SecretRedactor.Redact(exception.Message));
					continue;
				}
				bool first = !m_deferredFailedReleaseLogged.TryGetValue(failed.Partition, out DateTime last);
				if (first || nowUtc - last >= TimeSpan.FromMinutes(1))
				{
					m_deferredFailedReleaseLogged[failed.Partition] = nowUtc;
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] failed recovery release deferred (client still routed; T-0317): "
						+ SecretRedactor.Redact(exception.Message));
				}
				m_deferredFailedReleases[failed.Partition] = (failed, nowUtc.AddSeconds(RetryFailedReleaseSeconds));
				continue;
			}
			m_deferredFailedReleaseLogged.Remove(failed.Partition);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery claim released for retry: " + failed.Partition);
		}
	}

	/// <summary>
	/// T-0388: a recovery whose saves held content but which loaded none of it - every entity screened out as live on
	/// another node (the cross-partition duplicate check) or as a stale body - recovered an empty partition. Off with
	/// recovery.releaseEmpty=false (the node then holds it until its drain, as before). A save that was already empty is
	/// not marked: if the registry keeps the released row (a client route or a character home still names it, see
	/// GatewayRegistry.EmptyAndUnclaimed), the next node to recover it loads that empty save and holds it, so a pinned
	/// row costs one extra lap and never a release/recover loop.
	/// </summary>
	internal static bool MarkEmptyRecovery(bool enabled, int savedEntities, int loadedMembers)
		=> enabled && savedEntities > 0 && loadedMembers == 0;

	/// <summary>
	/// #1334: why an admin delete of the only member of <paramref name="partition"/> could not be made durable right
	/// now, or null. Game thread, before the close: <see cref="ReleaseEmptied"/> needs the partition owned here, no
	/// registry request in flight for it and a free save slot for its empty Final.
	/// </summary>
	internal string EmptyReleaseBlocker(ulong partition)
	{
		lock (m_owned)
			if (!m_owned.ContainsKey(partition)) return "its partition is not owned here";
		if (m_claimedPending.ContainsKey(partition) || m_apply.IsPending(partition)) return "its partition has a registry request pending";
		if (GetState(partition).Pending || m_saves.Count >= m_saves.BoundedCapacity) return "its partition is saving";
		return null;
	}

	/// <summary>
	/// #1334: an admin delete (<see cref="GridDeleteService"/>) closed the only member of <paramref name="partition"/>.
	/// As for an empty recovery (T-0388) the empty layout is dropped by the next sweep and never saved again, and the
	/// drain's memberless release leaves the save that still holds the grid as the newest, so the next recovery of the
	/// row would load the grid back. Adopted empty, its empty Final queued, it is released unowned once that Final is
	/// catalogued (<see cref="ReleaseEmptyRecoveries"/>), and the registry reaps the row. Game thread, right after the
	/// close (the closing grid is out of the census already). Null when queued, else why not.
	/// </summary>
	internal string ReleaseEmptied(ulong partition)
	{
		string blocker = EmptyReleaseBlocker(partition);
		if (blocker != null) return blocker;
		uint generation;
		lock (m_owned) generation = m_owned[partition];
		m_partitions.Adopt(partition, Array.Empty<long>(), mature: true);
		// SE-897: the count the registry thread reads is from the top of this frame and still has the grid. A slow frame lets
		// the empty Final be catalogued and a heartbeat run first, and that reads "it has members again" and forgets the release.
		m_partitionMembers = new Dictionary<ulong, int>(m_partitionMembers) { [partition] = 0 };
		if (QueueCapture(m_partitions.Peek(partition), "Final", waitForSlot: false) == null) return "the save queue filled up";
		m_emptyRecoveries[partition] = (generation, DateTime.UtcNow);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] partition " + partition + " generation " + generation
			+ " is empty after an admin grid delete; it is released unowned once its empty Final is catalogued (#1334)");
		return null;
	}

	internal enum EmptyRecoveryStep { Wait, Release, Forget }

	/// <summary>
	/// T-0388: what to do with an empty recovery this heartbeat. Released only once its empty Final is catalogued at the
	/// recovered generation: the registry's empty-row reap at release (GatewayRegistry.ReapIfEmpty) needs the newest
	/// save to be that empty one, and without it the release would hand the row back to recovery with the OLDER save
	/// that still holds the content as its newest - a duplicate the moment the census misses the live copy (the w33
	/// 73626 shape). Also not while the claim is still pending or the heartbeat just sent did not renew it at this
	/// generation. Forgotten when the partition is no longer this node's at that generation or has members again.
	/// </summary>
	internal static EmptyRecoveryStep EmptyRecoveryNext(uint generation, uint? ownedGeneration, int members, bool claimPending,
		bool savePending, uint? cataloguedGeneration, bool renewed)
	{
		if (ownedGeneration != generation || members > 0) return EmptyRecoveryStep.Forget;
		if (claimPending || savePending || cataloguedGeneration != generation || !renewed) return EmptyRecoveryStep.Wait;
		return EmptyRecoveryStep.Release;
	}

	/// <summary>T-0388: registry thread only (RegistryLoop). Releases each empty recovery unowned once its empty Final is
	/// catalogued; the registry then reaps the row as empty (nothing to recover), as it does for a drain's memberless release.</summary>
	private void ReleaseEmptyRecoveries(HttpClient client, HashSet<(ulong, uint)> renewed)
	{
		DateTime nowUtc = DateTime.UtcNow;
		Dictionary<ulong, int> members = m_partitionMembers;
		foreach (KeyValuePair<ulong, (uint Generation, DateTime Since)> empty in m_emptyRecoveries.ToArray())
		{
			uint generation = empty.Value.Generation;
			uint? owned;
			lock (m_owned) owned = m_owned.TryGetValue(empty.Key, out uint current) ? (uint?)current : null;
			State state = m_state.TryGetValue(empty.Key, out State found) ? found : null;
			bool catalogued = state != null && state.LastCataloguedUtc != default;
			EmptyRecoveryStep step = EmptyRecoveryNext(generation, owned, members.GetValueOrDefault(empty.Key),
				// T-0354: a queued claim or topology apply naming it counts as a pending claim (as for the drain's memberless release).
				m_claimedPending.ContainsKey(empty.Key) || m_apply.IsPending(empty.Key), state?.Pending ?? false,
				catalogued ? (uint?)state.Generation : null, renewed.Contains((empty.Key, generation)));
			if (step == EmptyRecoveryStep.Wait)
			{
				// A Final that failed in the load frame (T-0316) never comes: a memberless partition gets no periodic
				// capture. It waits for the drain's memberless release, as before T-0388; say so once a minute.
				if (!catalogued && nowUtc - empty.Value.Since >= TimeSpan.FromSeconds(30)
					&& (!m_emptyRecoveryLogged.TryGetValue(empty.Key, out DateTime last) || nowUtc - last >= TimeSpan.FromMinutes(1)))
				{
					m_emptyRecoveryLogged[empty.Key] = nowUtc;
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] empty recovery " + empty.Key + " generation " + generation
						+ " not released: its empty Final is not catalogued; the drain releases it (T-0388)");
				}
				continue;
			}
			if (step == EmptyRecoveryStep.Forget)
			{
				ForgetEmptyRecovery(empty);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] empty recovery " + empty.Key + " generation " + generation + " left alone: "
					+ (owned != generation ? "no longer this node's at that generation" : "it has members again") + " (T-0388)");
				continue;
			}
			try
			{
				Post(client, "/registry/partitions/release?partition=" + empty.Key.ToString(CultureInfo.InvariantCulture)
					+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
					+ "&generation=" + generation.ToString(CultureInfo.InvariantCulture)).Dispose();
			}
			catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
			{
				// Never let a 409 out of here: RegistryLoop reads one as the epoch fence.
				if (exception.Message.Contains("refusing release", StringComparison.Ordinal))
				{
					// A connected client is routed to it: keep it and ask again (the drain path's deferral, T-0048).
					if (!m_emptyRecoveryLogged.TryGetValue(empty.Key, out DateTime last) || nowUtc - last >= TimeSpan.FromMinutes(1))
					{
						m_emptyRecoveryLogged[empty.Key] = nowUtc;
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] empty recovery release deferred (client still routed; T-0388): "
							+ SecretRedactor.Redact(exception.Message));
					}
					continue;
				}
				ForgetEmptyRecovery(empty);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] empty recovery " + empty.Key + " generation " + generation
					+ " not released, the registry refused it (T-0388): " + SecretRedactor.Redact(exception.Message));
				continue;
			}
			catch (Exception exception)
			{
				// Unanswered: the heartbeat reports the registry; this one is asked again next second.
				if (!m_emptyRecoveryLogged.TryGetValue(empty.Key, out DateTime last) || nowUtc - last >= TimeSpan.FromMinutes(1))
				{
					m_emptyRecoveryLogged[empty.Key] = nowUtc;
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] empty recovery release of " + empty.Key + " failed, retrying (T-0388): "
						+ SecretRedactor.Redact(exception.Message));
				}
				continue;
			}
			lock (m_owned)
				if (m_owned.TryGetValue(empty.Key, out uint still) && still == generation) m_owned.Remove(empty.Key);
			ForgetEmptyRecovery(empty);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] released empty recovered partition " + empty.Key + " generation " + generation
				+ " unowned (its newest save, the empty Final, holds no entities; T-0388)");
		}

		void ForgetEmptyRecovery(KeyValuePair<ulong, (uint Generation, DateTime Since)> empty)
		{
			m_emptyRecoveries.TryRemove(empty);
			m_emptyRecoveryLogged.Remove(empty.Key);
		}
	}

	private const double RetryFailedReleaseSeconds = 5;
	/// <summary>Registry thread only (RegistryLoop).</summary>
	private readonly Dictionary<ulong, (RecoveryClaim Claim, DateTime Due)> m_deferredFailedReleases = new Dictionary<ulong, (RecoveryClaim, DateTime)>();
	private readonly Dictionary<ulong, DateTime> m_deferredFailedReleaseLogged = new Dictionary<ulong, DateTime>();

	/// <summary>
	/// T-0347: reports the Finals a local stop journaled on this host - this node's own and those of the nodes that ran
	/// here before it (under node control the writer's id never comes back) - so the registry catalogues what is still a
	/// partition's newest state before recovery hands out the older catalogued save. Sent even when there is nothing to
	/// report: the registry holds this host's partitions back until one of its nodes has reported (LateSaves.cs). Every
	/// verdict is logged; a refused save stays on disk and its partition recovers from the catalogue.
	/// </summary>
	private void ReportUnregisteredFinals(HttpClient client)
	{
		string[] journals;
		var reports = new List<object>();
		try
		{
			journals = ClusterProtocol.UnregisteredFinals.JournalsUnder(Environment.GetEnvironmentVariable(DataEnvironmentVariable),
				!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLUSTER_SLOT_ID")));
			foreach (string journal in journals)
			{
				List<ClusterProtocol.UnregisteredFinal> entries = ClusterProtocol.UnregisteredFinals.Read(journal, out int unreadable);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] unregistered Finals journal " + journal + ": " + entries.Count + " save(s)"
					+ (unreadable == 0 ? "" : ", " + unreadable + " unreadable line(s) skipped"));
				foreach (ClusterProtocol.UnregisteredFinal entry in entries)
				{
					try
					{
						// The journal line was written after the file; a file that is not what the line says is not reported.
						PartitionSaveFile.Parsed parsed = PartitionSaveFile.ReadHeader(File.ReadAllBytes(entry.Location));
						PartitionSaveFile.ValidateEnvelope(parsed, entry.Partition, checked((uint)entry.Generation), checked((uint)entry.Sequence));
						if (parsed.Kind != 2 || !parsed.BodyChecksum.ToString("X16").Equals(entry.Checksum, StringComparison.OrdinalIgnoreCase))
							throw new InvalidDataException("not the journaled Final (kind " + parsed.Kind + ", checksum " + parsed.BodyChecksum.ToString("X16") + ")");
					}
					catch (Exception exception)
					{
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: journaled Final of partition " + entry.Partition + " generation " + entry.Generation
							+ " sequence " + entry.Sequence + " at " + entry.Location + " is not reported: " + SecretRedactor.Redact(exception.Message));
						continue;
					}
					reports.Add(new { entry.Partition, entry.Generation, entry.Sequence, entry.SizeBytes, entry.Checksum, entry.ClusterTime,
						entry.Location, entry.Writer, entry.WriterEpoch, entry.Entities, entry.Homes });
				}
			}
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: unregistered Finals journals could not be read; nothing is reported: "
				+ SecretRedactor.Redact(exception.ToString()));
			journals = Array.Empty<string>();
			reports.Clear();
		}
		long logged = 0;
		while (!m_fenced)
		{
			try
			{
				using JsonDocument verdicts = PostJson(client, "/registry/saves/late?node=" + Uri.EscapeDataString(m_node)
					+ "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture), new { Saves = reports });
				foreach (JsonElement verdict in verdicts.RootElement.EnumerateArray())
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] unregistered Final of partition " + verdict.GetProperty("Partition").GetRawText()
						+ " generation " + verdict.GetProperty("Generation").GetRawText() + " sequence " + verdict.GetProperty("Sequence").GetRawText()
						+ (verdict.GetProperty("Accepted").GetBoolean() ? " catalogued: " : " REFUSED by the registry, the partition recovers from the catalogue: ")
						+ verdict.GetProperty("Reason").GetString());
				foreach (string journal in journals) ClusterProtocol.UnregisteredFinals.MarkReported(journal);
				return;
			}
			catch (Exception exception) when (SaveRetryPolicy.RegistryUnreachable(exception))
			{
				if (RegistryHoldPolicy.LogDue(ref logged, DateTime.UtcNow.Ticks, RetryLogTicks))
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] unregistered Finals report unavailable, retrying: " + SecretRedactor.Redact(exception.Message));
				Thread.Sleep(1000);
			}
			catch (Exception exception)
			{
				// An answer, and a no: the journals stay for the next start and recovery proceeds from the catalogue.
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: unregistered Finals report rejected (" + reports.Count + " save(s), journals kept): "
					+ SecretRedactor.Redact(exception.Message));
				return;
			}
		}
	}

	private void RegistryLoop(HttpClient client)
	{
		// T-0350: from here on a registry hold has its release (NoteRegistryReachable). Nothing in this loop reads the hold.
		m_heartbeatRunning = true;
		while (!m_fenced)
		{
			try
			{
				ReleaseFailedRecoveries(client);
				KeyValuePair<ulong, uint>[] owned;
				lock (m_owned) owned = m_owned.ToArray();
				KeyValuePair<ulong, uint>[] pendingClaims = m_claimedPending.ToArray()
					.Where(pending => owned.All(o => o.Key != pending.Key)).ToArray();
				// T-0354: what a queued claim or topology apply makes this node's once the registry has applied it.
				KeyValuePair<ulong, uint>[] expected = m_apply.Expected().Select(x => new KeyValuePair<ulong, uint>(x.Partition, x.Generation)).ToArray();
				Dictionary<ulong, int> memberCounts = m_partitionMembers;
				double clockSend = ClusterRuntime.LocalMilliseconds;
				// Game-thread hitches since the last heartbeat, with the handover phase that held the
				// thread - the gateway files them on the transfer beside its own stamps (§3).
				HitchRecorder.Hitch[] hitches = HitchRecorder.Snapshot(m_lastHitchUtc);
				if (hitches.Length != 0) m_lastHitchUtc = hitches[^1].Utc;
				using JsonDocument heartbeat = PostJson(client, "/registry/nodes/heartbeat", new
				{
					Node = m_node, Epoch = m_nodeEpoch,
					Hitches = hitches.Select(h => new { h.Utc, h.Ms, h.Phase, h.TransferId, h.Partition, h.PhaseMs }).ToArray(),
					ConfigRevision = ClusterConfig.Revision,
					Ownerships = owned.Concat(HeartbeatOmitsPending ? Array.Empty<KeyValuePair<ulong, uint>>() : pendingClaims).Select(x => new { Partition = x.Key, Generation = x.Value,
						Members = memberCounts.TryGetValue(x.Key, out int members) ? members : 0 })
						// At zero members, as a pending claim: it renews a lease and reports no content.
						.Concat(expected.Select(x => new { Partition = x.Key, Generation = x.Value, Members = 0 })).ToArray(),
					ClientPartitions = m_clientPartitions.Select(x => new { x.Client, x.Partition, x.Entity, x.Generation }).ToArray(),
					RecoveryCapacity = m_maxPartitions,
					ProjectedLoad = m_projectedLoad,
					LoadCapacity = m_maxMembers,
					LocalSendMilliseconds = clockSend,
					AuthorityMilliseconds = ClusterRuntime.IsWorldAuthority ? ClusterRuntime.AuthorityClockMilliseconds : (double?)null,
					SimulationFrame = MySession.Static?.GameplayFrameCounter ?? -1,
					SaveFailure = m_lastSaveFailure,
					Quarantined = QuarantinedPartitionIds(),
					RecoveryLoadsPending = m_recoveries.Count + m_censusChecks.Count + Volatile.Read(ref m_recoveryLoadInFlight),
					// T-0059: registry Active is set at registration; placement must wait for Serving.
					Serving = ClusterRuntime.IsServing, DeploymentRevision = NodeProcessIdentity.Revision,
					ConfigurationVerified = NodeProcessIdentity.Verified, ConfigurationFailure = NodeProcessIdentity.Failure
				});
				double clockReceive = ClusterRuntime.LocalMilliseconds;
				// §2: the gateway's handover-config document wins; a node that missed the PUT fan-out
				// (or was relaunched) converges within one heartbeat by pulling it.
				if (heartbeat.RootElement.TryGetProperty("HandoverConfigRevision", out JsonElement configRevision)
					&& configRevision.ValueKind == JsonValueKind.Number && configRevision.GetInt64() != m_appliedConfigRevision)
				{
					using JsonDocument config = Get(client, "/registry/handover-config");
					// Only latch the revision the node actually applied; latching it regardless turned
					// an unreadable document into a permanent, silent divergence (w22, 2026-08-21).
					if (ClusterRuntime.ApplyGatewayConfig(config.RootElement))
						m_appliedConfigRevision = configRevision.GetInt64();
				}
				// T-0339 (design C): transfers only where the registry says it applies them; absent on an older registry.
				m_partitions.RegistryTransfer = !m_transferRefused && heartbeat.RootElement.TryGetProperty("TopologyTransfer", out JsonElement transfer)
					&& transfer.ValueKind == JsonValueKind.True;
				double echoedSend = heartbeat.RootElement.GetProperty("LocalSendMilliseconds").GetDouble();
				long authorityGeneration = heartbeat.RootElement.GetProperty("WorldAuthorityGeneration").GetInt64();
				ClusterRuntime.SetAuthorityGeneration(authorityGeneration);
				bool hasAuthorityClock = heartbeat.RootElement.TryGetProperty("AuthorityMilliseconds", out JsonElement authorityClock)
					&& authorityClock.ValueKind == JsonValueKind.Number;
				HashSet<ulong> accepted = heartbeat.RootElement.GetProperty("Ownerships").EnumerateArray()
					.Select(x => x.GetProperty("Partition").GetUInt64()).ToHashSet();
				// T-0297: a PENDING claim the registry no longer holds for this node at its generation (the lease
				// lapsed, or it was taken back and granted on) must not be loaded: the new claimant loads the same
				// save, and both nodes would simulate the same entities until the next heartbeat discarded this
				// copy - node-1's 16:53:15 load-then-discard of 227734 (T-0295). Only what this heartbeat carried.
				HashSet<(ulong, uint)> acceptedAt = heartbeat.RootElement.GetProperty("Ownerships").EnumerateArray()
					.Select(x => (x.GetProperty("Partition").GetUInt64(), checked((uint)x.GetProperty("Generation").GetInt64()))).ToHashSet();
				foreach (KeyValuePair<ulong, uint> pending in DropLostPending ? pendingClaims : Array.Empty<KeyValuePair<ulong, uint>>())
					if (!acceptedAt.Contains((pending.Key, pending.Value)) && m_claimedPending.TryRemove(pending))
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery claim " + pending.Key + " generation " + pending.Value
							+ " is no longer this node's at the registry; its queued load is dropped (T-0297)");
				// Judge only what this heartbeat ASKED about. The reply covers the snapshot the request
				// carried; a partition that entered m_owned between the snapshot and the reply - a
				// topology split's fresh output, a just-committed import - was never presented, so its
				// absence from the reply says nothing. Comparing the live map instead closed the
				// output's members 65 ms after the split committed (node-2 2026-09-08 04:45:55, T-0080:
				// Pilot-002's character gone, the lease lapsed, the body recovered elsewhere
				// uncontrolled), and the same after a split on node-5 22:37:55. A partition re-owned
				// at a newer generation since the snapshot is not stale either.
				lock (m_owned)
					foreach (KeyValuePair<ulong, uint> lost in owned.Where(x => !accepted.Contains(x.Key)
							&& m_owned.TryGetValue(x.Key, out uint current) && current == x.Value
							&& !m_apply.IsPending(x.Key)   // T-0354: the answer to its topology apply decides, see Update
							&& m_pendingLostOwnership.Add(x.Key)).ToArray())
						m_lostOwnership.Enqueue((lost.Key, lost.Value));
				if (ClusterRuntime.IsWorldAuthority && hasAuthorityClock && ClusterRuntime.AcceptAuthorityMessage(authorityGeneration))
					ClusterRuntime.AdvanceAuthorityClock(authorityClock.GetDouble());
				else if (!ClusterRuntime.IsWorldAuthority && echoedSend == clockSend
					&& hasAuthorityClock
					&& ClusterRuntime.AcceptAuthorityMessage(authorityGeneration)
					&& ClusterRuntime.AcceptClockSample(clockSend, clockReceive, authorityClock.GetDouble()))
					NoteClockSample(clockReceive - clockSend);
				else if (!ClusterRuntime.IsWorldAuthority && hasAuthorityClock)
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] rejected malformed World Authority clock sample");
				JsonElement state = heartbeat.RootElement.GetProperty("State");
				Interlocked.Exchange(ref m_heartbeatAnsweredMilliseconds, Environment.TickCount64);
				NoteRegistryReachable(true, null);
				bool closed = state.ValueKind == JsonValueKind.String ? state.GetString() == "Closed" : state.GetInt32() == 1;
				// T-0388: after the heartbeat, so a release follows one that renewed the claim at its generation. A Closed
				// node leaves them to the drain's memberless release below.
				if (!closed && !m_closed && !m_emptyRecoveries.IsEmpty) ReleaseEmptyRecoveries(client, acceptedAt);
				if (closed)
				{
					if (heartbeat.RootElement.TryGetProperty("ForceDrainAt", out JsonElement deadline) && deadline.ValueKind == JsonValueKind.String)
						Interlocked.Exchange(ref m_forceDrainTicks, deadline.GetDateTimeOffset().UtcTicks);
					if (heartbeat.RootElement.TryGetProperty("ClusterDraining", out JsonElement draining) && draining.ValueKind == JsonValueKind.True)
						m_clusterShutdown = true;
					m_closed = true;
					// The registry refuses claims from a Closed node, so a partition minted from here
					// on could never be registered - and a member a split moved into one would die
					// with this node. Freeze the topology; the drain moves members out with the
					// partitions they are already in.
					m_partitions.HoldTopology = true;
					DrainSave[] ready;
					lock (m_drainSaves) ready = m_drainSaves.Values.Where(x => x.Unloaded).ToArray();
					foreach (DrainSave drain in ready)
					{
						// T-0237: offline in place of the release when the drain captured an offer for it.
						if (drain.OfflineBody != null)
						{
							bool? offline = TryDrainOffline(client, drain);
							if (offline == null) continue;   // no verdict (lost reply): the offer is idempotent, ask again
							if (offline == true)
							{
								lock (m_owned) m_owned.Remove(drain.Partition);
								lock (m_drainSaves) m_drainSaves.Remove(drain.Partition);
								lock (m_deferralLogged) m_deferralLogged.Remove(drain.Partition);
								m_registeredFinals.Add((drain.Partition, drain.Save.Generation, drain.Save.Sequence));
								continue;
							}
						}
						JsonDocument released;
						try
						{
							released = Post(client, "/registry/partitions/release?partition=" + drain.Partition.ToString(CultureInfo.InvariantCulture)
								+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
								+ "&generation=" + drain.Generation.ToString(CultureInfo.InvariantCulture));
						}
						catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict
							&& exception.Message.Contains("refusing release", StringComparison.Ordinal))
						{
							// The gateway's guard, not a fence: a connected client is still ROUTED to this
							// partition (its route goes stale when its ship moves partitions faster than
							// the resolve catches up - the ROUTES-BROKEN condition). The route re-resolves
							// within seconds, or the client disconnects; either way the release succeeds
							// on a later drain pass. Treating this 409 as an epoch fence killed the whole
							// node over one stale route - node-4, w14, 2026-08-18 09:33: fenced mid-drain,
							// then sat Failed for 20 minutes with every partition it still held degrading
							// before the unload finally died. Defer the partition, keep draining the rest.
							// First deferral for this partition, then at most once a minute:
							// w16's partition 475 (unowned, yet still routed to two clients
							// that were themselves flying perfectly well elsewhere) parked
							// node-4 for its full grace and wrote this line every second.
							bool sayIt;
							DateTime nowUtc = DateTime.UtcNow;
							lock (m_deferralLogged)
							{
								sayIt = !m_deferralLogged.TryGetValue(drain.Partition, out DateTime last)
									|| nowUtc - last >= TimeSpan.FromMinutes(1);
								if (sayIt) m_deferralLogged[drain.Partition] = nowUtc;
							}
							if (sayIt)
								MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain release deferred (client still routed): "
									+ SecretRedactor.Redact(exception.Message));
							continue;
						}
						using JsonDocument releasedScope = released;
						lock (m_owned) m_owned.Remove(drain.Partition);
						lock (m_drainSaves) m_drainSaves.Remove(drain.Partition);
						lock (m_deferralLogged) m_deferralLogged.Remove(drain.Partition);
						m_registeredFinals.Add((drain.Partition, drain.Save.Generation, drain.Save.Sequence));
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain released partition " + drain.Partition + " generation "
							+ released.RootElement.GetProperty("Generation").GetInt64() + " unowned");
					}
					// T-0048: an owned partition with NO local members - the character partition of a
					// client the gateway disconnected at stop, its content gone with the player - is never
					// selected by UpdateDrain (it iterates evaluated partitions only), so each node stayed
					// owner of 5-9 such ids for the whole 600 s force window and never reached Empty
					// (08:50 stop: "drain blocked: owned=5 selectable=0 blocked=[]"). Once the drain
					// queue is idle and the close is a few seconds old, release them unowned: their
					// last registered save stands, there is nothing left here to capture.
					if (m_closedAtTicks == 0) m_closedAtTicks = DateTime.UtcNow.Ticks;
					bool drainIdle;
					lock (m_drainSaves) drainIdle = m_drainSaves.Count == 0;
					if (drainIdle && DateTime.UtcNow.Ticks - m_closedAtTicks >= TimeSpan.FromSeconds(5).Ticks)
					{
						Dictionary<ulong, int> members = m_partitionMembers;
						KeyValuePair<ulong, uint>[] memberless;
						lock (m_owned) memberless = m_owned
							.Where(x => (!members.TryGetValue(x.Key, out int count) || count == 0)
								// T-0354: the input of an unanswered split has no layout, and is not memberless.
								&& !GetState(x.Key).Pending && !m_claimedPending.ContainsKey(x.Key) && !m_apply.IsPending(x.Key)).ToArray();
						foreach (KeyValuePair<ulong, uint> stale in memberless)
						{
							try
							{
								Post(client, "/registry/partitions/release?partition=" + stale.Key.ToString(CultureInfo.InvariantCulture)
									+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
									+ "&generation=" + stale.Value.ToString(CultureInfo.InvariantCulture)
									// #1292: a member-less row that never had a save of its own is reaped, not left as unsaved state.
									+ "&memberless=true").Dispose();
							}
							catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
							{
								MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain release of memberless partition " + stale.Key
									+ " refused: " + SecretRedactor.Redact(exception.Message));
								continue;
							}
							lock (m_owned) m_owned.Remove(stale.Key);
							MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain released memberless partition " + stale.Key
								+ " generation " + stale.Value + " unowned (no local members; last registered save stands, or none and the registry reaps it)");
						}
					}
					bool empty;
					lock (m_owned) empty = m_owned.Count == 0;
					lock (m_drainSaves) empty &= m_drainSaves.Count == 0;
					if (empty)
					{
						Post(client, "/registry/nodes/empty?node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)).Dispose();
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain finals registered; node Empty");
						LingerUntilFinalsReplicated(client);
						m_exitRequested = true;
						ClusterRuntime.ArmExitWatchdog("drain complete; node Empty");
						return;
					}
				}
			}
			catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
			{
				ClusterRuntime.SetNodeState("Failed", "registry epoch fenced: " + exception.Message);
				m_fenced = true;
				m_disconnectForFence = true;
				lock (m_owned)
					foreach (KeyValuePair<ulong, uint> partition in m_owned.Where(x => m_pendingLostOwnership.Add(x.Key)).ToArray())
						m_lostOwnership.Enqueue((partition.Key, partition.Value));
				m_exitRequested = true;
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] registry epoch fenced; discarding local ownership and terminating");
				ClusterRuntime.ArmExitWatchdog("registry epoch fenced");
				break;
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] registry unavailable: " + SecretRedactor.Redact(exception.Message));
				if (SaveRetryPolicy.RegistryUnreachable(exception)) NoteRegistryReachable(false, exception);
			}
			Thread.Sleep(1000);
		}
	}

	private long m_registryHoldSinceTicks;   // under m_registryHoldLock

	/// <summary>
	/// T-0350: the heartbeat is the node's view of the registry. An unanswered one holds the topology
	/// (PartitionManager.RegistryHold), so the game thread starts no split, merge or partition-id request that it
	/// would then retry until the registry is back; the first answered one releases it. Rollback:
	/// CLUSTER_REGISTRY_HOLD=0.
	/// </summary>
	/// <remarks>
	/// Any thread may report the registry unreachable (the save worker, the replication worker, a game-thread request
	/// that got no answer) - whichever sees it first sets the hold. ONLY the heartbeat loop reports it reachable, and
	/// the hold is only ever set while that loop runs, so a set hold always has the thread that clears it. The same
	/// transitions drive ClusterRuntime.RegistryUnreachable, the flag the game-thread sites outside this class read.
	/// </remarks>
	private void NoteRegistryReachable(bool reachable, Exception exception)
	{
		if (!RegistryHoldEnabled) return;
		lock (m_registryHoldLock)
		{
			if (!reachable)
			{
				if (m_partitions.RegistryHold || !m_heartbeatRunning) return;
				m_registryHoldSinceTicks = DateTime.UtcNow.Ticks;
				m_partitions.RegistryHold = true;
				ClusterRuntime.RegistryUnreachable = true;
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] registry unreachable: topology held (no split, merge or new partition) until it answers again: "
					+ SecretRedactor.Redact(exception?.Message ?? ""));
				return;
			}
			if (!m_partitions.RegistryHold) return;
			m_partitions.RegistryHold = false;
			ClusterRuntime.RegistryUnreachable = false;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] registry reachable again: topology released after "
				+ ((DateTime.UtcNow.Ticks - m_registryHoldSinceTicks) / (double)TimeSpan.TicksPerSecond).ToString("F0", CultureInfo.InvariantCulture) + " s");
		}
	}

	private readonly object m_registryHoldLock = new object();
	/// <summary>T-0350: the heartbeat loop (RegistryLoop) is running - the only thing that clears the registry hold.</summary>
	private volatile bool m_heartbeatRunning;

	/// <summary>
	/// T-0350: a registry request got no answer on this thread; sets the hold without waiting for the next heartbeat to
	/// fail. Always false, so it can stand in an exception filter (the exception still reaches its handler).
	/// </summary>
	/// <remarks>
	/// Not while the heartbeat is being answered: a request that times out on one slow route, or one that was started
	/// inside an outage and times out after it, says nothing about a registry that answered the heartbeat a second ago,
	/// and would hold the topology (and refuse handovers) until the next heartbeat for nothing.
	/// </remarks>
	internal bool NoteRegistryFailure(Exception exception)
	{
		if (SaveRetryPolicy.RegistryNoAnswer(exception)
			&& !RegistryHoldPolicy.HeartbeatAnswering(Environment.TickCount64, Interlocked.Read(ref m_heartbeatAnsweredMilliseconds)))
			NoteRegistryReachable(false, exception);
		return false;
	}

	/// <summary>Environment.TickCount64 of the last answered heartbeat.</summary>
	private long m_heartbeatAnsweredMilliseconds;

	private static readonly bool RegistryHoldEnabled = Environment.GetEnvironmentVariable("CLUSTER_REGISTRY_HOLD") != "0";

	/// <summary>
	/// A drain Final exists only on this node the moment it registers; exiting right away makes it
	/// unpullable and recovery silently rides a stale Periodic instead (P1-DRAIN-01 lost a character
	/// this way). Keep the process serving until every registered Final has a second holder, bounded
	/// so a peerless cluster still shuts down. Heartbeats pausing during the linger is fine: the node
	/// already posted Empty, and Empty holders stay usable without a live lease.
	/// </summary>
	private void LingerUntilFinalsReplicated(HttpClient client)
	{
		if (m_registeredFinals.Count == 0) return;
		try
		{
			using (JsonDocument nodes = Get(client, "/registry/nodes"))
			{
				bool peer = nodes.RootElement.EnumerateArray().Any(x =>
					x.GetProperty("Node").GetString() != m_node
					&& (x.GetProperty("State").ValueKind == JsonValueKind.String ? x.GetProperty("State").GetString() == "Active" : x.GetProperty("State").GetInt32() == 0)
					&& (x.GetProperty("Role").ValueKind == JsonValueKind.String ? x.GetProperty("Role").GetString() == "Regular" : x.GetProperty("Role").GetInt32() == 0));
				if (!peer)
				{
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] no active peer to replicate drain finals; exiting");
					return;
				}
			}
			DateTime deadline = DateTime.UtcNow.AddSeconds(10);
			while (DateTime.UtcNow < deadline)
			{
				bool pending = false;
				foreach (IGrouping<ulong, (ulong Partition, uint Generation, uint Sequence)> partition in m_registeredFinals.GroupBy(x => x.Partition))
				{
					using JsonDocument saves = Get(client, "/registry/saves?partition=" + partition.Key.ToString(CultureInfo.InvariantCulture));
					foreach ((ulong Partition, uint Generation, uint Sequence) final in partition)
						pending |= !saves.RootElement.EnumerateArray().Any(row =>
							row.GetProperty("SaveId").GetProperty("Partition").GetUInt64() == final.Partition
							&& row.GetProperty("SaveId").GetProperty("Generation").GetInt64() == final.Generation
							&& row.GetProperty("SaveId").GetProperty("Sequence").GetInt64() == final.Sequence
							&& row.GetProperty("Locations").GetArrayLength() >= 2);
				}
				if (!pending)
				{
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain finals replicated to a peer; exiting");
					return;
				}
				Thread.Sleep(500);
			}
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] WARNING: drain final replication wait timed out; finals may be unpullable");
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain final replication wait failed: " + SecretRedactor.Redact(exception.Message));
		}
	}

	/// <summary>
	/// Orchestration §4.2/§4.5 step 3: a definitive registry rejection means this incarnation is never
	/// admitted again - discard local ownership WITHOUT saving and terminate, exactly like the
	/// heartbeat-fence branch of RegistryLoop. Callable from any thread; the game thread's Update
	/// drains the flags and exits. Idempotent.
	/// </summary>
	internal void FenceForRejection(string reason)
	{
		if (m_fenced) return;
		ClusterRuntime.SetNodeState("Failed", reason);
		m_fenced = true;
		m_disconnectForFence = true;
		lock (m_owned)
			foreach (KeyValuePair<ulong, uint> partition in m_owned.Where(x => m_pendingLostOwnership.Add(x.Key)).ToArray())
				m_lostOwnership.Enqueue((partition.Key, partition.Value));
		m_exitRequested = true;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] registry rejection is terminal; discarding local ownership and terminating: "
			+ SecretRedactor.Redact(reason));
		ClusterRuntime.ArmExitWatchdog("terminal registry rejection");
	}

	/// <summary>
	/// HotSpareNodes.md dormant spares: true while this node has never owned, claimed or been
	/// granted any content and may run its main loop at the dormant rate. Sticky on first
	/// ownership - a node that ever held content stays at full rate for its incarnation
	/// (spare-ness migrates by relaunch). Called at the plugin update cadence, never per frame.
	/// </summary>
	internal bool DormantEligible()
	{
		if (m_everOwned || m_closed || m_fenced || !m_registryReady || !RegistryEnabled) return false;
		int owned;
		lock (m_owned) owned = m_owned.Count;
		if (owned != 0 || !m_claimedPending.IsEmpty || !m_recoveries.IsEmpty)
		{
			m_everOwned = true;
			return false;
		}
		return true;
	}

	// T-0350: every registry request of this class goes through the three helpers below (peer requests do not), so a
	// request that gets no answer sets the registry hold from whichever thread made it - the save worker, the
	// replication worker, a retry loop - without waiting for the next heartbeat. The filter never handles anything.
	private JsonDocument Post(HttpClient client, string path)
	{
		try
		{
			using HttpResponseMessage response = client.PostAsync(m_gateway + path, null).GetAwaiter().GetResult();
			return ClusterRuntime.ReadRegistryResponse(response);
		}
		catch (Exception exception) when (NoteRegistryFailure(exception)) { throw; }
	}

	private JsonDocument Get(HttpClient client, string path)
	{
		try
		{
			using HttpResponseMessage response = client.GetAsync(m_gateway + path).GetAwaiter().GetResult();
			return ClusterRuntime.ReadRegistryResponse(response);
		}
		catch (Exception exception) when (NoteRegistryFailure(exception)) { throw; }
	}

	private JsonDocument PostJson(HttpClient client, string path, object body)
	{
		try
		{
			using StringContent content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
			using HttpResponseMessage response = client.PostAsync(m_gateway + path, content).GetAwaiter().GetResult();
			return ClusterRuntime.ReadRegistryResponse(response);
		}
		catch (Exception exception) when (NoteRegistryFailure(exception)) { throw; }
	}

	private readonly HashSet<string> m_foreignDeleteDirectives = new(StringComparer.Ordinal);

	private void ApplyDeletes(HttpClient client)
	{
		using JsonDocument document = Get(client, "/registry/deletions?node=" + Uri.EscapeDataString(m_node)
			+ "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture));
		// T-0340: with the host store this is the host's store root - the registry hands this node its host's deletes -
		// and the node's own data dir stays a root: an unmoved save is still catalogued there, held by this node.
		string saveRoot = Path.GetFullPath(m_saveRoot) + Path.DirectorySeparatorChar;
		string legacyRoot = Path.GetFullPath(Path.Combine(m_root, "saves")) + Path.DirectorySeparatorChar;
		// One line per sweep, not per file: a line per deleted save was the node logs' largest line
		// kind (18,775 of 91,079 lines in two hours of w32, 2026-09-23) and nothing reads it. The
		// manifest.jsonl tombstone below is the per-file record.
		int deleted = 0;
		string last = null;
		foreach (JsonElement directive in document.RootElement.EnumerateArray())
		{
			JsonElement id = directive.GetProperty("SaveId");
			string location = Path.GetFullPath(directive.GetProperty("Location").GetString());
			if (!location.StartsWith(saveRoot, StringComparison.Ordinal) && !location.StartsWith(legacyRoot, StringComparison.Ordinal))
			{
				// T-0163: a copy the registry still attributes to this node name but outside this
				// incarnation's save root (a warm restart onto a fresh data root). It is not ours to
				// delete, and acknowledging it would claim a deletion that did not happen: skip it
				// and keep going with the rest.
				if (m_foreignDeleteDirectives.Add(location))
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] retention skipped a copy outside the node save root: " + location);
				continue;
			}
			File.Delete(location);
			string tombstone = JsonSerializer.Serialize(new { op = "delete", key = id.GetProperty("Partition").GetUInt64(),
				gen = id.GetProperty("Generation").GetInt64(), seq = id.GetProperty("Sequence").GetInt64(), location, wallTime = DateTimeOffset.UtcNow }) + Environment.NewLine;
			using (FileStream stream = new FileStream(Path.Combine(m_root, "manifest.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
			{
				using StreamWriter writer = new StreamWriter(stream);
				writer.Write(tombstone);
				writer.Flush();
				stream.Flush(true);
			}
			Post(client, "/registry/deletions/ack?partition=" + id.GetProperty("Partition").GetUInt64().ToString(CultureInfo.InvariantCulture)
				+ "&generation=" + id.GetProperty("Generation").GetInt64().ToString(CultureInfo.InvariantCulture)
				+ "&sequence=" + id.GetProperty("Sequence").GetInt64().ToString(CultureInfo.InvariantCulture)
				+ "&location=" + Uri.EscapeDataString(location)).Dispose();
			deleted++;
			last = location;
		}
		if (deleted > 0)
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] retention deleted " + deleted
				+ " save file(s); last " + last);
	}

	private DateTime m_clockLineUtc = DateTime.MinValue;
	private int m_clockSamples;
	private double m_clockMaxRtt;

	/// <summary>
	/// One clock line a minute, not one per 1 Hz heartbeat: 15,545 of 91,079 node log lines in two hours of
	/// w32 (2026-09-23), and no tool reads them. The line keeps its "clock sample rtt=" prefix and adds how
	/// many samples it stands for and the worst round trip among them.
	/// </summary>
	private void NoteClockSample(double rtt)
	{
		m_clockSamples++;
		m_clockMaxRtt = Math.Max(m_clockMaxRtt, rtt);
		DateTime now = DateTime.UtcNow;
		if (now - m_clockLineUtc < TimeSpan.FromMinutes(1))
			return;
		MyLog.Default?.WriteLineAndConsole(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] clock sample rtt={0:F3}ms cluster={1:F3}ms ({2} sample(s) since the last line, max rtt {3:F3}ms)",
			rtt, ClusterRuntime.ClusterMilliseconds(), m_clockSamples, m_clockMaxRtt));
		m_clockLineUtc = now;
		m_clockSamples = 0;
		m_clockMaxRtt = 0;
	}

	/// <summary>
	/// T-0323: serves a stopped node's catalogued save file on this host (this node is its proxy). The registry grant has
	/// already bound exactly this location to this save; the path must still resolve - every symlink followed - to a
	/// partition save file (.mps) under this host's run root (the parent of this node's data dir; #1210: a managed slot's
	/// <c>runtime/nodes</c>), or nothing is read.
	/// T-0340 step 5: opened for streaming from <paramref name="offset"/>, not read whole (ClusterProtocol.SaveFileStream).
	/// </summary>
	public FileStream OpenProxiedSave(string location, long offset)
	{
		string resolved = ClusterProtocol.ProxiedSavePath.Resolve(location, Environment.GetEnvironmentVariable(DataEnvironmentVariable),
				!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLUSTER_SLOT_ID")))
			?? throw new UnauthorizedAccessException("Proxied save location is not a save file under this host's run root");
		return ClusterProtocol.SaveFileStream.Open(resolved, offset);
	}

	/// <summary>T-0340: whether a requested location is this host's store path of that save - then the file is read by
	/// its save id (<see cref="OpenSave"/>), the path computed here, and the location is not used as a path at all.</summary>
	public bool IsStoreLocation(string location, ulong partition, uint generation, uint sequence) =>
		m_hostStore && string.Equals(location, SavePath(partition, generation, sequence), StringComparison.Ordinal);

	public FileStream OpenSave(ulong partition, uint generation, uint sequence, long offset)
	{
		string path = SavePath(partition, generation, sequence);
		// T-0340: a save this node wrote before the store, not moved into it, is catalogued (and asked for) at its old path.
		if (m_hostStore)
			path = ClusterProtocol.HostSaveStore.Held(path, ClusterProtocol.HostSaveStore.PartitionSave(Path.Combine(m_root, "saves"), partition, generation, sequence));
		return ClusterProtocol.SaveFileStream.Open(path, offset);
	}


	/// <summary>Takes one replica directive; true when a save was copied (the caller may take another).</summary>
	private bool ReplicateOne(HttpClient client)
	{
		using JsonDocument work = Get(client, "/registry/replicas?node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture));
		JsonElement directive = work.RootElement.EnumerateArray().FirstOrDefault();
		if (directive.ValueKind == JsonValueKind.Undefined) return false;
		JsonElement id = directive.GetProperty("SaveId");
		ulong partition = id.GetProperty("Partition").GetUInt64();
		uint generation = checked((uint)id.GetProperty("Generation").GetInt64()), sequence = checked((uint)id.GetProperty("Sequence").GetInt64());
		long size = directive.GetProperty("SizeBytes").GetInt64();
		string checksum = directive.GetProperty("Checksum").GetString();
		if (directive.TryGetProperty("Global", out JsonElement globalFlag) && globalFlag.GetBoolean())
		{
			foreach (JsonElement source in directive.GetProperty("Sources").EnumerateArray())
			{
				string holder = source.GetProperty("Holder").GetString();
				try
				{
					string path = DownloadGlobal(source.GetProperty("Endpoint").GetString(), generation, sequence, size, checksum);
					// T-0377: the node closed during the download; the registry refuses a holder on an Empty node.
					if (m_closed || m_exitRequested) return false;
					Post(client, "/registry/global-saves/holders?generation=" + generation.ToString(CultureInfo.InvariantCulture)
						+ "&sequence=" + sequence.ToString(CultureInfo.InvariantCulture)
						+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
						+ "&size=" + size.ToString(CultureInfo.InvariantCulture) + "&checksum=" + Uri.EscapeDataString(checksum)
						+ "&location=" + Uri.EscapeDataString(path) + "&control=" + Uri.EscapeDataString(m_controlEndpoint)).Dispose();
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] replicated global save " + generation + "/" + sequence + " from " + holder);
					return true;
				}
				catch (Exception exception)
				{
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] global replica source " + holder + " failed: " + SecretRedactor.Redact(exception.Message));
				}
			}
			return false;
		}
		foreach (JsonElement source in directive.GetProperty("Sources").EnumerateArray())
		{
			string holder = source.GetProperty("Holder").GetString();
			try
			{
				// T-0323: a gone holder's copy comes through a proxy on its host, which serves exactly the catalogued location.
				string proxy = source.TryGetProperty("Proxy", out JsonElement proxyElement) && proxyElement.ValueKind == JsonValueKind.String ? proxyElement.GetString() : null;
				string sourceLocation = proxy != null && source.TryGetProperty("Location", out JsonElement locationElement) && locationElement.ValueKind == JsonValueKind.String
					? locationElement.GetString() : null;
				string path = Download(source.GetProperty("Endpoint").GetString(), holder, partition, generation, sequence, size, checksum,
					proxy ?? holder, sourceLocation);
				Post(client, "/registry/saves/holders?partition=" + partition.ToString(CultureInfo.InvariantCulture)
					+ "&generation=" + generation.ToString(CultureInfo.InvariantCulture) + "&sequence=" + sequence.ToString(CultureInfo.InvariantCulture)
					+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
					+ "&size=" + size.ToString(CultureInfo.InvariantCulture) + "&checksum=" + Uri.EscapeDataString(checksum)
					+ "&location=" + Uri.EscapeDataString(path) + "&control=" + Uri.EscapeDataString(m_controlEndpoint)).Dispose();
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] replicated save " + partition + "/" + generation + "/" + sequence + " from " + holder);
				return true;
			}
			catch (InvalidDataException exception)
			{
				Post(client, "/registry/saves/corruption?partition=" + partition + "&generation=" + generation + "&sequence=" + sequence
					+ "&holder=" + Uri.EscapeDataString(holder) + "&reporter=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch).Dispose();
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: corrupt replica source " + holder + ": " + SecretRedactor.Redact(exception.Message));
			}
			catch (Exception exception) { MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] replica source failed: " + SecretRedactor.Redact(exception.Message)); }
		}
		return false;
	}

	/// <summary>T-0340: a recovery download landed in the host store, so the host holds the save now: tell the registry, or
	/// the file would lie there uncatalogued (and be fetched again by the next recovery on this host). Best effort.</summary>
	private void RegisterStoreCopy(HttpClient client, RecoveryFile file, string path)
	{
		try
		{
			Post(client, "/registry/saves/holders?partition=" + file.Partition.ToString(CultureInfo.InvariantCulture)
				+ "&generation=" + file.Generation.ToString(CultureInfo.InvariantCulture) + "&sequence=" + file.Sequence.ToString(CultureInfo.InvariantCulture)
				+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
				+ "&size=" + file.SizeBytes.ToString(CultureInfo.InvariantCulture) + "&checksum=" + Uri.EscapeDataString(file.Checksum)
				+ "&location=" + Uri.EscapeDataString(path) + "&control=" + Uri.EscapeDataString(m_controlEndpoint ?? string.Empty)).Dispose();
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery copy of " + file.Partition + "/" + file.Generation + "/" + file.Sequence
				+ " not registered: " + SecretRedactor.Redact(exception.Message));
		}
	}

	private RecoveryClaim MaterializeRecovery(RecoveryClaim claim, HttpClient client) => claim with
	{
		Files = claim.Files.Select(file => Materialize(file, client)).ToArray(),
		Fallbacks = m_warmRecovery ? Array.Empty<RecoveryFile>() : claim.Fallbacks.Select(file => Materialize(file, client)).ToArray()
	};

	private RecoveryFile Materialize(RecoveryFile file, HttpClient client)
	{
		if (m_assignedRecoveries.TryGetValue(file.Partition, out var assigned)
			&& assigned.Generation == file.Generation && assigned.Sequence == file.Sequence && File.Exists(assigned.Path))
		{
			try
			{
				PartitionSaveFile.Parsed staged = PartitionSaveFile.ReadFile(assigned.Path);
				PartitionSaveFile.ValidateEnvelope(staged, file.Partition, file.Generation, file.Sequence);
				if (!staged.BodyChecksum.ToString("X16").Equals(file.Checksum, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException("Catalog checksum mismatch");
				return file with { Locations = new[] { assigned.Path } };
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: invalid staged recovery save " + assigned.Path + ": " + SecretRedactor.Redact(exception.Message));
			}
		}
		string local = file.Locations.FirstOrDefault(path => Path.IsPathFullyQualified(path) && File.Exists(path) && file.LocationHolders.GetValueOrDefault(path) == m_node);
		if (local != null)
		{
			try
			{
				PartitionSaveFile.Parsed parsed = PartitionSaveFile.ReadFile(local);
				PartitionSaveFile.ValidateEnvelope(parsed, file.Partition, file.Generation, file.Sequence);
				string actualChecksum = parsed.BodyChecksum.ToString("X16");
				if (!actualChecksum.Equals(file.Checksum, StringComparison.OrdinalIgnoreCase))
				{
					// A valid envelope with the wrong checksum on a save only THIS node holds is not
					// corruption - it is the catalog trailing a file the owner legitimately rewrote
					// (the 2026-08-11 double-claim left node-3 re-capturing into an already-catalogued
					// slot for hours). Amend the catalog to the bytes that actually exist; the
					// alternative "corrupt holder" verdict falls back to older saves and rolls the
					// partition back.
					if (!file.LocationHolders.Values.All(holderNode => holderNode == m_node))
						throw new InvalidDataException("Catalog checksum mismatch");
					long actualSize = new FileInfo(local).Length;
					Post(client, "/registry/saves/amend?partition=" + file.Partition.ToString(CultureInfo.InvariantCulture)
						+ "&generation=" + file.Generation.ToString(CultureInfo.InvariantCulture)
						+ "&sequence=" + file.Sequence.ToString(CultureInfo.InvariantCulture)
						+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
						+ "&size=" + actualSize.ToString(CultureInfo.InvariantCulture) + "&checksum=" + actualChecksum
						+ "&clusterTime=" + parsed.ClusterTime.ToString(CultureInfo.InvariantCulture)
						+ "&location=" + Uri.EscapeDataString(local)).Dispose();
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] catalog amended for rewritten sole-copy save "
						+ file.Partition + "/" + file.Generation + "/" + file.Sequence);
					return file with { Locations = new[] { local }, Checksum = actualChecksum, SizeBytes = actualSize };
				}
				return file with { Locations = new[] { local } };
			}
			catch (Exception exception)
			{
				Post(client, "/registry/saves/corruption?partition=" + file.Partition + "&generation=" + file.Generation + "&sequence=" + file.Sequence
					+ "&holder=" + Uri.EscapeDataString(m_node) + "&reporter=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch).Dispose();
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: corrupt local recovery holder " + local + ": " + SecretRedactor.Redact(exception.Message));
			}
		}
		// T-0323: a catalogued location on THIS host's disk that another node wrote - a stopped node's data dir after a
		// fresh-id start or a recycle, whose own process can no longer serve it. Checksum-validated like any fetch.
		// T-0340: this host's own store is one of these. A damaged copy there is reported as a corrupt host holder, like
		// a corrupt local file above; the fetch below then replaces it and registers the good copy, which clears the mark.
		string ownStore = m_hostStore ? ClusterProtocol.HostSaveStore.Holder(ClusterHttp.NodeHost) : null;
		foreach (string path in file.Locations.Where(path => Path.IsPathFullyQualified(path) && file.LocationHolders.GetValueOrDefault(path) != m_node
			&& !file.CorruptHolders.Contains(file.LocationHolders.GetValueOrDefault(path) ?? string.Empty) && File.Exists(path)))
		{
			try
			{
				PartitionSaveFile.Parsed parsed = PartitionSaveFile.ReadFile(path);
				PartitionSaveFile.ValidateEnvelope(parsed, file.Partition, file.Generation, file.Sequence);
				if (!parsed.BodyChecksum.ToString("X16").Equals(file.Checksum, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException("Catalog checksum mismatch");
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery reads " + file.Partition + "/" + file.Generation + "/" + file.Sequence
					+ " from this host's disk: " + path + " (holder " + file.LocationHolders.GetValueOrDefault(path) + ")");
				return file with { Locations = new[] { path } };
			}
			catch (InvalidDataException exception) when (file.LocationHolders.GetValueOrDefault(path) is { Length: > 0 } corruptHolder)
			{
				// T-0376: a same-host copy that fails its checksum is reported like a fetched one. It was only logged, so
				// the registry kept counting the holder as a usable copy (redundancy overstated, no re-replication).
				Post(client, "/registry/saves/corruption?partition=" + file.Partition + "&generation=" + file.Generation + "&sequence=" + file.Sequence
					+ "&holder=" + Uri.EscapeDataString(corruptHolder) + "&reporter=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch).Dispose();
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: corrupt same-host save holder " + corruptHolder + ": " + path + ": "
					+ SecretRedactor.Redact(exception.Message));
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] same-host save " + path + " unusable: " + SecretRedactor.Redact(exception.Message));
				if (ownStore != null && file.LocationHolders.GetValueOrDefault(path) == ownStore)
					Post(client, "/registry/saves/corruption?partition=" + file.Partition + "&generation=" + file.Generation + "&sequence=" + file.Sequence
						+ "&holder=" + Uri.EscapeDataString(ownStore) + "&reporter=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch).Dispose();
			}
		}
		// T-0340: this host's own store was read above (a catalogued path on this disk); fetching "from" it would only
		// write the file onto itself.
		foreach (string holder in file.HolderEndpoints.Keys.Concat(file.HolderProxies?.Keys ?? Enumerable.Empty<string>()).Distinct()
			.Where(holder => holder != m_node && holder != ownStore && !file.CorruptHolders.Contains(holder)))
		{
			try
			{
				// T-0323: a gone holder is fetched through its proxy, which serves exactly the location that holder holds.
				string path = file.HolderProxies != null && file.HolderProxies.TryGetValue(holder, out (string Node, string Endpoint) proxy)
					? Download(proxy.Endpoint, holder, file.Partition, file.Generation, file.Sequence, file.SizeBytes, file.Checksum, proxy.Node,
						file.Locations.FirstOrDefault(location => file.LocationHolders.GetValueOrDefault(location) == holder))
					: Download(file.HolderEndpoints[holder], holder, file.Partition, file.Generation, file.Sequence, file.SizeBytes, file.Checksum);
				if (m_hostStore) RegisterStoreCopy(client, file, path);
				return file with { Locations = new[] { path } };
			}
			catch (InvalidDataException exception)
			{
				Post(client, "/registry/saves/corruption?partition=" + file.Partition + "&generation=" + file.Generation + "&sequence=" + file.Sequence
					+ "&holder=" + Uri.EscapeDataString(holder) + "&reporter=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch).Dispose();
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: corrupt remote save holder " + holder + ": " + SecretRedactor.Redact(exception.Message));
			}
			catch (Exception exception) { MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] remote save holder unavailable " + holder + ": " + SecretRedactor.Redact(exception.Message)); }
		}
		return file with { Locations = Array.Empty<string>() };
	}

	/// <summary>
	/// Pulls a global save replica from a holder's control endpoint into this node's own GLOBAL
	/// store, verified against the catalog size and body checksum. Whole-file (global saves are
	/// small); authorization rides the registry client credentials like the voxel base fetch.
	/// </summary>
	private string DownloadGlobal(string endpoint, uint generation, uint sequence, long size, string checksum)
	{
		string final = ClusterProtocol.HostSaveStore.GlobalSave(m_saveRoot, generation, sequence);
		if (File.Exists(final))
		{
			try
			{
				byte[] existing = File.ReadAllBytes(final);
				if (existing.LongLength == size && PartitionSaveFile.ReadHeader(existing).BodyChecksum.ToString("X16", CultureInfo.InvariantCulture)
					.Equals(checksum, StringComparison.OrdinalIgnoreCase)) return final;
			}
			catch (InvalidDataException) { }
			if (!m_hostStore) File.Delete(final);
		}
		using HttpClient direct = ClusterHttp.Client(TimeSpan.FromSeconds(30));
		ClusterRuntime.AuthorizeRegistryClient(direct);
		byte[] bytes = FetchExact(direct, endpoint.TrimEnd('/') + "/cluster/global-save-file?generation="
			+ generation.ToString(CultureInfo.InvariantCulture) + "&sequence=" + sequence.ToString(CultureInfo.InvariantCulture), size);
		// T-0397: replica.globalFsync=false leaves the write to the kernel's writeback instead of an fsync per copy. A copy
		// torn by a power loss fails the header/body checksum wherever it is read (StoredIs, ReadHeader) and the reader
		// takes the next holder; every global has at least two.
		WriteVerifiedGlobalSave(bytes, size, checksum, final, m_hostStore ? m_node : null, ClusterConfig.GetBool("replica.globalFsync"));
		return final;
	}

	/// <summary>
	/// T-0397: the body read straight into one array of the catalogued size. GetByteArrayAsync grew a buffer in steps
	/// and copied it out at the end: several large-object-heap arrays per ~290 KB global, ~1,000-2,800 copies per
	/// recycle. A body of another length is refused (the verify would refuse it anyway).
	/// </summary>
	private static byte[] FetchExact(HttpClient client, string url, long size)
	{
		if (size <= 0 || size > int.MaxValue) throw new InvalidDataException("Global save size " + size + " is out of range");
		using HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength is long announced && announced != size)
			throw new InvalidDataException("Fetched global save is " + announced + " bytes, the catalog says " + size);
		using Stream stream = response.Content.ReadAsStream();
		byte[] bytes = GC.AllocateUninitializedArray<byte>((int)size);
		stream.ReadExactly(bytes);
		if (stream.ReadByte() != -1) throw new InvalidDataException("Fetched global save is longer than the catalog size " + size);
		return bytes;
	}

	/// <summary>
	/// Verifies fetched global-save bytes against the catalog size and body checksum, then writes them
	/// atomically (tmp + fsync + rename). Shared with the replacement-WA restore path in
	/// WorldAuthorityCheckpointPatch so the verification rule cannot drift between the two fetchers.
	/// </summary>
	/// <param name="storeOwner">T-0340: the fetching node's id when <paramref name="final"/> is in the per-host save store
	/// (installed without replacing a good copy another node put there); null for the node's own directory.</param>
	/// <param name="durable">fsync before the rename (default). False (T-0397, replica.globalFsync): kernel writeback.</param>
	internal static void WriteVerifiedGlobalSave(byte[] bytes, long size, string checksum, string final, string storeOwner = null, bool durable = true)
	{
		if (bytes.LongLength != size || !PartitionSaveFile.ReadHeader(bytes).BodyChecksum.ToString("X16", CultureInfo.InvariantCulture)
			.Equals(checksum, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("Fetched global save does not match the catalog");
		Directory.CreateDirectory(Path.GetDirectoryName(final));
		string temporary = storeOwner != null ? ClusterProtocol.HostSaveStore.Temporary(final, storeOwner) : final + ".tmp";
		using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			stream.Write(bytes);
			stream.Flush(durable);
		}
		if (storeOwner != null) ClusterProtocol.HostSaveStore.Install(temporary, final, existing => StoredIs(existing, checksum), replaceOther: true);
		else File.Move(temporary, final, true);
	}

	/// <param name="receiver">The node the grant names and the request goes to: the holder, or its proxy (T-0323).</param>
	/// <param name="location">Proxied only: the catalogued location the proxy serves.</param>
	private string Download(string endpoint, string holder, ulong partition, uint generation, uint sequence, long size, string checksum,
		string receiver = null, string location = null)
	{
		receiver ??= holder;
		string final = SavePath(partition, generation, sequence);
		if (File.Exists(final))
		{
			try
			{
				PartitionSaveFile.Parsed existing = PartitionSaveFile.ReadFile(final);
				PartitionSaveFile.ValidateEnvelope(existing, partition, generation, sequence);
				if (existing.BodyChecksum.ToString("X16").Equals(checksum, StringComparison.OrdinalIgnoreCase)) return final;
			}
			catch (InvalidDataException) { }
			// T-0340: in the host store another node may be installing this very save; the install below replaces a bad file.
			if (!m_hostStore) File.Delete(final);
		}
		Directory.CreateDirectory(Path.GetDirectoryName(final));
		string partial = m_hostStore ? ClusterProtocol.HostSaveStore.Partial(final, m_node) : final + ".part";
		long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
		if (offset > size) { File.Delete(partial); offset = 0; }
		if (offset != 0) MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] resuming save " + partition + "/" + generation + "/" + sequence + " at byte " + offset);
		using HttpClient direct = ClusterHttp.Client(TimeSpan.FromSeconds(30));
		ClusterRuntime.AuthorizeRegistryClient(direct);
		using JsonDocument grant = PostJson(direct, "/registry/grants", new { Operation = "save", TransferId = string.Empty,
			Partition = partition, Generation = generation, Sequence = sequence, Requester = m_node, RequesterEpoch = m_nodeEpoch, Receiver = receiver,
			Location = location });
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + "/cluster/save-file?partition=" + partition + "&generation=" + generation
			+ "&sequence=" + sequence + "&offset=" + offset + (location == null ? string.Empty : "&location=" + Uri.EscapeDataString(location)));
		request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", grant.RootElement.GetProperty("Token").GetString());
		using HttpResponseMessage response = direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
		// T-0348: the holder (or the proxy of a gone one) says the catalogued file does not exist. Ends like a torn file:
		// both callers report InvalidDataException as corruption of this holder's copy, so the registry stops offering it.
		// Any other failure (transport, timeout, 401, 500) says nothing about the copy and marks nothing.
		if ((int)response.StatusCode == ClusterProtocol.ProxiedSavePath.MissingStatus)
			throw new InvalidDataException("Holder " + holder + " no longer has the save file");
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength is long length && length != size - offset)
		{
			File.Delete(partial);
			throw new InvalidDataException("Remote save length does not match catalog");
		}
		using (Stream input = response.Content.ReadAsStream())
		using (FileStream stream = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.None))
		{
			// T-0345: a fetched copy is not fsynced; its source still holds the save and the checksum below guards the bytes.
			try { input.CopyTo(stream); }
			finally { stream.Flush(PartitionSaveFile.FsyncEverySave); }
		}
		long downloaded = new FileInfo(partial).Length;
		if (downloaded > size) { File.Delete(partial); throw new InvalidDataException("Remote save exceeds catalog size"); }
		if (downloaded != size) throw new IOException("Incomplete save transfer");
		try
		{
			PartitionSaveFile.Parsed parsed = PartitionSaveFile.ReadFile(partial);
			PartitionSaveFile.ValidateEnvelope(parsed, partition, generation, sequence);
			if (!parsed.BodyChecksum.ToString("X16").Equals(checksum, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Catalog checksum mismatch");
		}
		catch (InvalidDataException)
		{
			File.Delete(partial);
			throw;
		}
		if (m_hostStore) ClusterProtocol.HostSaveStore.Install(partial, final, existing => StoredIs(existing, checksum), replaceOther: true);
		else File.Move(partial, final, true);
		return final;
	}

	private string SavePath(ulong partition, uint generation, uint sequence) => ClusterProtocol.HostSaveStore.PartitionSave(m_saveRoot, partition, generation, sequence);

	/// <summary>T-0340: a save of the catalogued checksum that is already at <paramref name="path"/> in the host store.</summary>
	private static bool? StoredIs(string path, string checksum) =>
		ulong.TryParse(checksum, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong wanted) ? PartitionSaveFile.StoredIs(path, wanted) : false;

	private static RecoveryClaim ParseRecovery(JsonElement claim)
	{
		JsonElement ownership = claim.GetProperty("Ownership");
		RecoveryFile[] files = claim.GetProperty("ResolveCover").EnumerateArray().Select(ParseFile).ToArray();
		RecoveryFile[] fallbacks = claim.TryGetProperty("Fallbacks", out JsonElement fallbackElement)
			? fallbackElement.EnumerateArray().Select(ParseFile).ToArray() : Array.Empty<RecoveryFile>();
		return new RecoveryClaim(ownership.GetProperty("Partition").GetUInt64(), checked((uint)ownership.GetProperty("Generation").GetInt64()), files, fallbacks)
		{
			HomedElsewhere = claim.TryGetProperty("CharactersHomedElsewhere", out JsonElement homed) && homed.ValueKind == JsonValueKind.Array
				? homed.EnumerateArray().Select(x => x.GetInt64()).ToHashSet() : new HashSet<long>(),
			HomesByIdentity = claim.TryGetProperty("HomesElsewhere", out JsonElement homes) && homes.ValueKind == JsonValueKind.Array
				? homes.EnumerateArray().GroupBy(x => x.GetProperty("Identity").GetInt64()).ToDictionary(g => g.Key, g => g.Select(x =>
					new HomeElsewhere(x.GetProperty("Entity").GetInt64(), x.GetProperty("Partition").GetUInt64(),
						x.GetProperty("Version").GetInt64(), x.GetProperty("Updated").GetDateTimeOffset(),
						x.TryGetProperty("Captured", out JsonElement captured) && captured.ValueKind == JsonValueKind.Number ? captured.GetInt64() : 0)).First())
				: new Dictionary<long, HomeElsewhere>(),
			SaveHomeStamps = claim.TryGetProperty("SaveHomeStamps", out JsonElement stamps) && stamps.ValueKind == JsonValueKind.Object
				? stamps.EnumerateObject().ToDictionary(x => long.Parse(x.Name, CultureInfo.InvariantCulture), x => x.Value.GetInt64())
				: new Dictionary<long, long>(),
			SaveStamped = claim.TryGetProperty("SaveStamped", out JsonElement stamped) && stamped.ValueKind == JsonValueKind.True,
			SaveCreated = claim.TryGetProperty("SaveCreated", out JsonElement created) && created.ValueKind == JsonValueKind.String
				? created.GetDateTimeOffset() : null,
			SaveCaptured = claim.TryGetProperty("SaveCaptured", out JsonElement saveCaptured) && saveCaptured.ValueKind == JsonValueKind.Number
				? saveCaptured.GetInt64() : 0,
		};

		static RecoveryFile ParseFile(JsonElement cover)
		{
			JsonElement save = cover.GetProperty("Save");
			JsonElement id = save.GetProperty("SaveId");
			Dictionary<string, string> locationHolders = save.TryGetProperty("LocationHolders", out JsonElement holderMap) ? holderMap.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()) : new();
			Dictionary<string, string> holderEndpoints = save.TryGetProperty("HolderEndpoints", out JsonElement endpointMap) ? endpointMap.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()) : new();
			HashSet<string> corrupt = save.TryGetProperty("CorruptHolders", out JsonElement corruptElement) ? corruptElement.EnumerateArray().Select(x => x.GetString()).ToHashSet() : new();
			Dictionary<string, (string Node, string Endpoint)> proxies = save.TryGetProperty("HolderProxies", out JsonElement proxyMap) && proxyMap.ValueKind == JsonValueKind.Object
				? proxyMap.EnumerateObject().ToDictionary(x => x.Name, x => (x.Value.GetProperty("Node").GetString(), x.Value.GetProperty("Endpoint").GetString()))
				: null;
			return new RecoveryFile(id.GetProperty("Partition").GetUInt64(), checked((uint)id.GetProperty("Generation").GetInt64()),
				checked((uint)id.GetProperty("Sequence").GetInt64()), save.GetProperty("Checksum").GetString(),
				save.GetProperty("SizeBytes").GetInt64(), locationHolders, holderEndpoints, corrupt,
				save.GetProperty("Locations").EnumerateArray().Select(x => x.GetString()).ToArray(),
				cover.GetProperty("EntityIds").ValueKind == JsonValueKind.Null ? null : cover.GetProperty("EntityIds").EnumerateArray().Select(x => x.GetInt64()).ToHashSet(),
				proxies);
		}
	}

	/// <summary>Loads a recovery claim; returns how many entities its saves held before the duplicate and staleness
	/// screens removed any (T-0388 tells "the saves were empty" from "everything in them lives elsewhere").</summary>
	private int LoadRecovery(RecoveryClaim recovery)
	{
		Dictionary<long, MyObjectBuilder_EntityBase> entities = new Dictionary<long, MyObjectBuilder_EntityBase>();
		HashSet<long> created = new HashSet<long>();
		// T-0207: every location this claim offered and what happened to it. The failure below used
		// to name only the partition, and ReadRecoveryFile logs a line only for a location it
		// actually OPENED - so a claim whose locations are all absent produced no explanation at
		// all. Measured on the live w32 run root: 47 "No valid recovery location" against 0
		// "corrupt recovery holder" and 0 "recovery fell back from", over four partitions failing
		// 11-12 times EACH as the gateway re-offered them around all five nodes. Not one of those
		// 47 said which path it wanted or why it could not have it.
		List<string> attempts = new List<string>();
		foreach (RecoveryFile file in recovery.Files.OrderByDescending(file => file.Generation).ThenByDescending(file => file.Sequence))
		{
			RecoveryFile loaded = file;
			PartitionSaveFile.Parsed save = ReadRecoveryFile(file, attempts);
			if (save == null)
			{
				if (m_warmRecovery) throw new FileNotFoundException("Exact warm recovery save is unavailable for partition "
					+ file.Partition + "; " + DescribeAttempts(attempts));
				RecoveryFile fallback = recovery.Fallbacks.Where(candidate => candidate.Partition == file.Partition)
					.OrderByDescending(candidate => candidate.Generation).ThenByDescending(candidate => candidate.Sequence)
					.FirstOrDefault(candidate => (save = ReadRecoveryFile(candidate, attempts)) != null);
				if (fallback == null) throw new FileNotFoundException("No valid recovery location for partition "
					+ file.Partition + "; " + DescribeAttempts(attempts));
				loaded = fallback;
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery fell back from " + file.Generation + "/" + file.Sequence
					+ " to " + fallback.Generation + "/" + fallback.Sequence + " for partition " + file.Partition);
			}
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery read save " + loaded.Partition + "/" + loaded.Generation + "/" + loaded.Sequence);
			PartitionSaveFile.MergeEntities(entities, Deserialize(save), file.EntityIds);
		}
		int saved = entities.Count;
		if (StaleCopyCheck && recovery.Census != null && DeferForCensusCheck(recovery, entities)) return DeferredLoad;
		if (recovery.LiveElsewhere is { Count: > 0 })
		{
			foreach (long id in entities.Keys.Where(recovery.LiveElsewhere.Contains).ToArray())
			{
				entities.Remove(id);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery skipped entity " + id
					+ " for partition " + recovery.Partition
					+ ": already live on another node (cross-partition duplicate prevented)");
			}
			StripPilotsLiveElsewhere(recovery.Partition, entities.Values, recovery.LiveElsewhere.Contains);
		}
		StripOrphanSeatCharacters(recovery.Partition, entities.Values);
		// T-0237: by IDENTITY too - a stale copy can be an older, different body of the same player (Pilot-010's
		// ...870 in partition 227687, w32 2026-09-25 04:16:08, while the home named ...929).
		HashSet<long> staleCharacters = new HashSet<long>(recovery.HomedElsewhere ?? new HashSet<long>());
		foreach (MyObjectBuilder_Character character in CharactersIn(entities.Values))
			if (StaleByIdentity(recovery, character) is { } why && staleCharacters.Add(character.EntityId))
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery of partition " + recovery.Partition + ": body " + character.EntityId
					+ " of identity " + character.OwningPlayerIdentityId + " is stale - " + why + " (T-0237)");
		if (staleCharacters.Count > 0)
		{
			foreach (long id in entities.Where(x => x.Value is MyObjectBuilder_Character && staleCharacters.Contains(x.Key))
				.Select(x => x.Key).ToArray())
			{
				entities.Remove(id);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery skipped standing body " + id + " for partition "
					+ recovery.Partition + ": its player's character home is newer and in another partition (stale copy, T-0237)");
			}
			// Seated copies the same way: a seat is where a body is, not what it is (Viktor 2026-09-25).
			StripPilots(recovery.Partition, entities.Values, staleCharacters.Contains,
				"its player's character home is newer and in another partition (stale seated copy, T-0237)");
		}
		// T-0237: a FENCE-TAINTED local copy of a recovered entity is stale by construction - an assigned seed
		// save loaded at boot whose claim never confirmed - and the create-if-absent loop below used to keep it
		// and adopt it into the recovered partition, discarding the save's newer state. w32 2026-09-25 00:07:48:
		// node-2 recovered the Earth base (offline 199400, generation 25) while its fresh-world seed copy
		// (partition 5, generation 1) was still loaded fenced, and the purge then "spared 6 re-acquired
		// entities" - the base ran on the seed's state. The handover import already replaces a fenced
		// duplicate with the arriving copy (ImportKeepExisting); recovery now does the same.
		MyEntity[] staleCopies = entities.Keys.Where(IsFenceTainted)
			.Select(id => MyEntities.TryGetEntityById(id, out MyEntity local) ? local : null)
			.Where(local => local != null && !local.MarkedForClose && !ImportActivationGuard.IsOpenFor(GuardUser.RecoveryStaleCopy, local.EntityId)).ToArray();   // T-0392
		if (staleCopies.Length != 0)
		{
			CloseWithHierarchy(staleCopies);
			foreach (MyEntity stale in staleCopies) ClusterRuntime.ClearFrozen(stale.EntityId);
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery of partition " + recovery.Partition + " replaced "
				+ staleCopies.Length + " fence-tainted local copies with the recovered save: "
				+ string.Join(",", staleCopies.Select(stale => stale.EntityId)));
		}
		bool adopted = false;
		try
		{
			bool suspended = VRage.MyEntityIdentifier.AllocationSuspended;
			VRage.MyEntityIdentifier.AllocationSuspended = true;
			try
			{
				foreach (MyObjectBuilder_EntityBase entity in entities.Values)
					if (!MyEntities.TryGetEntityById(entity.EntityId, out _))
					{
						created.Add(entity.EntityId);
						MyEntities.CreateFromObjectBuilderAndAdd(entity, fadeIn: false);
					}
			}
			finally { VRage.MyEntityIdentifier.AllocationSuspended = suspended; }
			adopted = true;
			m_partitions.Adopt(recovery.Partition, entities.Keys);
			// Recovered content is confirmed ownership: lift any fence taint an assigned save left.
			UntaintEntities(entities.Keys);
			m_state[recovery.Partition] = new State { Generation = recovery.Generation };
			// Peek, never Get: Get runs the topology sweep, which is free to split the partition
			// adopted two lines up - mid-load - and did (see PartitionManager.Peek).
			PartitionManager.Partition recovered = m_partitions.Peek(recovery.Partition);
			if (recovered.HasPending)
				// Big grids from the save are still in the parallel init pipeline; a Final taken
				// now would silently omit them. The recovery cover stays claimable until the first
				// periodic capture lands after the pin drains, so nothing is stranded by waiting.
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery Final deferred for partition "
					+ recovery.Partition + ": members still initializing");
			else
			{
				// T-0316: the entities are created and adopted; a Final that cannot be captured in the load frame
				// (a vanilla component that serializes only after its first update) must not fail the recovery.
				// Failing it releases the claim, and with a client routed to the partition the registry refuses
				// that release and fences the node (r32 20:27-20:30, three node deaths). The recovery cover stays
				// claimable until the first periodic capture lands, as for the deferred case above.
				try { QueueCapture(recovered, "Final"); }
				catch (Exception exception)
				{
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery Final deferred for partition " + recovery.Partition
						+ ": capture failed in the load frame (T-0316): " + SecretRedactor.Redact(exception.ToString()));
				}
			}
		}
		catch
		{
			if (adopted) m_partitions.Remove(recovery.Partition);
			m_state.TryRemove(recovery.Partition, out _);
			// T-0213: hierarchy, not membership - see FinishReconceal. A half-created recovery is
			// exactly where a grid gets closed with a pilot still in it.
			CloseWithHierarchy(MyEntities.GetEntities().Where(entity => created.Contains(entity.EntityId)));
			throw;
		}
		return saved;

		// T-0207: `attempts` records one line per LOCATION, including the ones this loop used to
		// skip in the Where clause without a word. "Not present on this node" and "present and
		// corrupt" are different defects with different owners, and the old shape could not tell
		// them apart from the outside.
		PartitionSaveFile.Parsed ReadRecoveryFile(RecoveryFile file, List<string> attempts)
		{
			if (file.Locations.Length == 0)
				attempts.Add(file.Generation + "/" + file.Sequence + " offered no location at all");
			foreach (string path in file.Locations)
			{
				// WHOSE path this is, always. Locations are absolute paths and LocationHolders maps
				// each one to the node that wrote it, so a path belonging to another node looks
				// exactly like a local path that has been deleted - and the answers are opposite:
				// "the save lives on node-3 and this node cannot read node-3's disk" is a routing
				// problem, "the save was on this node and is gone" is data loss.
				string holder = file.LocationHolders.GetValueOrDefault(path);
				string whose = holder == null ? " (holder unknown)" : holder == m_node ? " (this node)" : " (holder " + holder + ")";
				string where = file.Generation + "/" + file.Sequence + " " + path + whose;
				if (!Path.IsPathFullyQualified(path))
				{
					attempts.Add(where + ": not an absolute path");
					continue;
				}
				if (!File.Exists(path))
				{
					attempts.Add(where + ": absent");
					continue;
				}
				try
				{
					PartitionSaveFile.Parsed parsed = PartitionSaveFile.ReadFile(path);
					PartitionSaveFile.ValidateEnvelope(parsed, file.Partition, file.Generation, file.Sequence);
					if (!parsed.BodyChecksum.ToString("X16").Equals(file.Checksum, StringComparison.OrdinalIgnoreCase))
						throw new InvalidDataException("Catalog checksum mismatch");
					return parsed;
				}
				catch (Exception exception)
				{
					attempts.Add(where + ": " + exception.GetType().Name + " " + exception.Message);
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: corrupt recovery holder " + path + ": " + SecretRedactor.Redact(exception.Message));
				}
			}
			return null;
		}
	}

	/// <summary>One readable clause naming every recovery location that was tried and why each was
	/// rejected, for the exception that reports a partition as unrecoverable (T-0207).</summary>
	internal static string DescribeAttempts(List<string> attempts)
	{
		if (attempts == null || attempts.Count == 0)
			return "no locations were examined (the claim carried no files)";
		return "tried " + attempts.Count + ": " + string.Join("; ", attempts);
	}

	/// <summary>One line, on the game thread, saying what this node was holding and what it was in
	/// the middle of at the moment it was told to stop.
	///
	/// WHY IT EXISTS: two separate tickets converged on the same missing fact. T-0028 needs to know
	/// which partitions a node still OWNED when its drain could select nothing and it lingered to
	/// the force-drain deadline; T-0006 needs to know what was MID-HANDOVER when the process died,
	/// because a rejected import leaves a partition that then fails to complete 86% of the time
	/// (14% against a 99% base rate). Both questions were unanswerable for the same reason: the
	/// stop is where the evidence stops.
	///
	/// WHY AT STOP-BEGIN AND NOT AT EXIT: the interesting stops are the ones that do not finish. A
	/// node that will not drain is force-killed, so a record written on the way out is precisely the
	/// record that never gets written. This fires on the Closed transition, minutes before any kill.
	///
	/// WHY THE THREAD DANCE: the Closed transition is detected on the registry thread, and both
	/// transfer tables are documented game-thread-only (see HasLiveInboundTransfer). So the flag is
	/// set there and consumed here, in Update, which runs under MySandboxGame.ProcessInvoke.
	///
	/// Once per stop, and a node stops once, so this cannot become a log storm - the failure mode
	/// T-0028's own voxel-replica defect was.</summary>
	/// <summary>Last-known state on disk, refreshed periodically, for a node that dies without
	/// warning.
	///
	/// WHY A FILE AND NOT A LOG LINE: Cli/cluster.py stop() posts /registry/shutdown/graceful,
	/// which times out on every stop we take, and then calls kill() - which sends SIGKILL.
	/// SIGKILL cannot be caught, so no exit handler, no ProcessExit hook and no shutdown path
	/// can write anything at all. The stop record on the Closed transition covers a ROTATION,
	/// where the gateway closes the epoch first, but a cluster stop never closes the epoch and
	/// so never reaches it (CM, 2026-09-04: 6c4bb79 produced no line on its first stop, and all
	/// eight stops that night ended in "graceful shutdown unavailable; terminating").
	///
	/// So the state is written AHEAD of the death instead. After any kill the file holds what
	/// the node owned and what it had in flight, at most SnapshotSeconds old. That is what
	/// T-0006 needs - "what was mid-handover when the previous process was killed" - and the
	/// killed case is precisely the one a shutdown hook cannot see.
	///
	/// Overwritten in place, so it costs one small file per node rather than growing.</summary>
	private void WriteLastStateSnapshot()
	{
		double now = MySession.Static?.ElapsedGameTime.TotalSeconds ?? 0;
		if (now < m_nextSnapshot) return;
		m_nextSnapshot = now + SnapshotSeconds;
		try
		{
			string owned;
			int ownedCount;
			lock (m_owned)
			{
				ownedCount = m_owned.Count;
				owned = string.Join(",", m_owned.Keys.OrderBy(id => id));
			}
			// m_root is this class's own cluster directory, the same place node-epoch lives -
			// derived from CLUSTER_DATA_ROOT. CLUSTER_RUN_ROOT does not exist on a node; I
			// checked a live process's environment rather than assuming it did.
			string path = Path.Combine(m_root ?? Path.GetTempPath(), m_node + "-laststate.json");
			File.WriteAllText(path,
				"{\"node\":\"" + m_node + "\",\"epoch\":" + m_nodeEpoch
				+ ",\"writtenAtGameSeconds\":" + now.ToString("F1", CultureInfo.InvariantCulture)
				+ ",\"closed\":" + (m_closed ? "true" : "false")
				+ ",\"ownedCount\":" + ownedCount
				+ ",\"owned\":\"" + owned + "\""
				+ ",\"liveTransfers\":\"" + ClusterControlServer.DescribeLiveTransfers().Replace("\"", "'") + "\"}");
		}
		catch (Exception)
		{
			// A diagnostic must never disturb the node. Silent on purpose: this runs on every
			// snapshot interval, so a logged failure would be the log storm it exists to avoid.
		}
	}

	private void WriteStopRecord()
	{
		m_stopRecordPending = false;
		string owned;
		int ownedCount;
		lock (m_owned)
		{
			ownedCount = m_owned.Count;
			owned = ownedCount == 0 ? "none" : string.Join(",", m_owned.Keys.OrderBy(id => id));
		}
		string transfers;
		try
		{
			transfers = ClusterControlServer.DescribeLiveTransfers();
		}
		catch (Exception error)  // never let a diagnostic break the stop path
		{
			transfers = "unavailable (" + error.GetType().Name + ")";
		}
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] stop record: node=" + m_node
			+ " epoch=" + m_nodeEpoch
			+ " ownedCount=" + ownedCount + " owned=[" + owned + "]"
			+ " liveTransfers=[" + transfers + "]");
	}

	/// <summary>
	/// T-0353: a connected client whose controlled entity is in NO layout (a body the held topology has not adopted)
	/// reads as partition 0, and the non-forced drain then took the ship beside it for clientless: it was unloaded
	/// under the player, the registry refused the release for 117 s, and the client came back without a character.
	/// Such a client bears the partition nearest its entity, so that partition waits for the force pass, which
	/// disconnects its clients first.
	/// </summary>
	/// <summary>T-0353: (client, partition) pairs already logged, so a drain that polls every pass logs each once.</summary>
	private static readonly HashSet<(ulong, ulong)> s_drainBorne = new();

	private static PartitionManager.ClientPartition[] DrainClients(PartitionManager.ClientPartition[] clients,
		IReadOnlyCollection<PartitionManager.Partition> partitions)
	{
		if (partitions.Count == 0 || clients.All(client => client.Partition != 0 || client.Entity == 0)) return clients;
		return clients.Select(client =>
		{
			if (client.Partition != 0 || client.Entity == 0 || !MyEntities.TryGetEntityById(client.Entity, out MyEntity entity)) return client;
			VRageMath.Vector3D position = entity.PositionComp.GetPosition();
			PartitionManager.Partition nearest = partitions.OrderBy(x => VRageMath.Vector3D.Distance(x.Sphere.Center, position) - x.Sphere.Radius).ThenBy(x => x.Id).First();
			if (s_drainBorne.Add((client.Client, nearest.Id)))
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain client_in_no_layout client=" + client.Client + " entity=" + client.Entity
					+ " bears partition=" + nearest.Id);
			return new PartitionManager.ClientPartition(client.Client, nearest.Id, client.Entity);
		}).ToArray();
	}

	/// <summary>T-0347: any thread (the control server). Idempotent: a second request keeps the first deadline.</summary>
	public object RequestLocalStop(double budgetSeconds)
	{
		if (m_root == null) throw new InvalidOperationException("This node has no save store");
		budgetSeconds = Math.Max(5, Math.Min(3600, budgetSeconds));
		int owned;
		lock (m_owned) owned = m_owned.Count;
		bool already = m_localStop;
		if (!already)
		{
			Interlocked.Exchange(ref m_localStopDeadlineTicks, DateTime.UtcNow.AddSeconds(budgetSeconds).Ticks);
			// The stop record at stop-BEGIN, as on the Closed transition: this process is killed if it overruns.
			m_stopRecordPending = true;
			m_localStop = true;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] local stop requested: Final saves of " + owned + " owned partition(s) to the local save store within "
				+ budgetSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s, nothing released (registry " + (RegistryHeld ? "unreachable" : "not held unreachable") + ", T-0347)");
			// The game thread does the captures; one that is stuck must not outlive the stop's own SIGKILL unnoticed.
			ClusterRuntime.ArmExitWatchdog("local stop", (int)budgetSeconds + 20);
		}
		return new { accepted = true, already, owned, registryHeld = RegistryHeld, budgetSeconds };
	}

	/// <summary>
	/// T-0347, game thread: one Final per owned partition, as many per tick as the save queue takes, then exit. No freeze
	/// and no unload - nothing is handed over, the process ends. The topology is held like a Closed node's, so a member
	/// does not change partitions between two captures. A partition that cannot be captured (a save in flight, members
	/// still initializing, frozen by a transfer, its registry topology apply unanswered - #1284) is waited for until the deadline and then left to its last registered
	/// save, by name.
	/// </summary>
	private void UpdateLocalStop()
	{
		if (m_localStopFinished) return;
		if (!m_localStopStarted)
		{
			m_localStopStarted = true;
			m_partitions.HoldTopology = true;
		}
		bool expired = DateTime.UtcNow.Ticks >= Interlocked.Read(ref m_localStopDeadlineTicks);
		HashSet<ulong> owned;
		lock (m_owned) owned = m_owned.Keys.ToHashSet();
		List<string> waiting = new List<string>();
		bool queueFull = false;
		foreach (PartitionManager.Partition next in m_partitions.Evaluate()
			.Where(x => owned.Contains(x.Id) && !m_localStopQueued.Contains(x.Id))
			.OrderBy(PartitionManager.EstimatedSnapshotBytes).ThenBy(x => x.Id))
		{
			bool frozen = next.Members.Any(ClusterRuntime.IsFrozen);
			string blocked = RegistryApplyQueue.LocalStopBlocked(GetState(next.Id).Pending, next.HasPending, frozen, m_apply.IsPending(next.Id));
			// A drain or an offline transition froze it and queued its own Final, which the worker has now completed:
			// that Final is the frozen partition's state. One that is not frozen has simulated since, and is captured again.
			if (frozen && blocked == "frozen" && m_localFinals.ContainsKey(next.Id))
			{
				m_localStopQueued.Add(next.Id);
				continue;
			}
			if (blocked != null)
			{
				waiting.Add(next.Id + ":" + blocked);
				continue;
			}
			if (expired)
			{
				waiting.Add(next.Id + ":deadline");
				continue;
			}
			if (queueFull) continue;
			PendingSave save;
			try
			{
				save = QueueCapture(next, "Final", waitForSlot: false);
			}
			catch (Exception exception)
			{
				// As the periodic capture: a member mid-teardown can fail its object builder. Not retried - the stop is short.
				m_localStopQueued.Add(next.Id);
				m_localStopCaptureFailures++;
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: local stop capture failed for partition " + next.Id
					+ "; its last registered save stands: " + SecretRedactor.Redact(exception.ToString()));
				continue;
			}
			if (save == null)
			{
				queueFull = true;   // the worker is writing; the next tick queues it
				continue;
			}
			m_localStopQueued.Add(next.Id);
			m_localStopSaves.Add(save);
		}
		bool written = m_localStopSaves.All(x => x.Completed.IsSet);
		bool overrun = DateTime.UtcNow.Ticks >= Interlocked.Read(ref m_localStopDeadlineTicks) + TimeSpan.FromSeconds(10).Ticks;
		// Done when every Final is written and nothing is left to capture; a partition that cannot be captured is waited
		// for until the deadline, a write in flight for 10 s more.
		if (!overrun && (!written || queueFull || waiting.Count != 0 && !expired)) return;
		m_localStopFinished = true;
		int registered = m_localStopSaves.Count(x => x.Succeeded), journaled = m_localStopSaves.Count(x => x.LocalOnly);
		int lost = m_localStopSaves.Count - registered - journaled + m_localStopCaptureFailures;
		bool complete = lost == 0 && waiting.Count == 0;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] local stop " + (complete ? "complete" : "INCOMPLETE") + ": " + m_localStopSaves.Count
			+ " Final save(s) queued, " + registered + " registered, " + journaled + " journaled for the next start, " + lost + " failed or unfinished"
			+ (waiting.Count == 0 ? "" : "; not saved (last registered save stands): [" + string.Join(", ", waiting) + "]"));
		ClusterRuntime.ExitProcess(complete ? 0 : 5, "local stop");
	}

	private void UpdateDrain()
	{
		DrainSave[] saves;
		lock (m_drainSaves) saves = m_drainSaves.Values.ToArray();
		foreach (DrainSave drain in saves.Where(x => x.Save.Completed.IsSet && x.Save.Succeeded && !x.Unloaded))
		{
			CloseWithHierarchy(MyEntities.GetEntities().Where(x => drain.Members.Contains(x.EntityId)));
			foreach (long entity in drain.Members) ClusterRuntime.ClearFrozen(entity, drain.FreezeId);
			m_partitions.Remove(drain.Partition);
			drain.Unloaded = true;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain final registered and partition unloaded: " + drain.Partition);
		}
		foreach (DrainSave drain in saves.Where(x => x.Save.Completed.IsSet && !x.Save.Succeeded && !x.Unloaded))
			RetryDrainFinal(drain);
		lock (m_drainSaves)
			if (m_drainSaves.Count != 0) return;
		// T-0350: no new drain capture while the registry is unreachable - its Final could not register, and the
		// partition would sit frozen behind a save worker that retries without limit. Before anything is frozen.
		if (RegistryHeld) return;

		IReadOnlyCollection<PartitionManager.Partition> partitions = m_partitions.Evaluate();
		PartitionManager.ClientPartition[] clients = DrainClients(m_partitions.ClientPartitions(partitions), partitions);
		bool force = DateTimeOffset.UtcNow.UtcTicks >= Interlocked.Read(ref m_forceDrainTicks);
		if (force && !m_forceDrainLogged)
		{
			m_forceDrainLogged = true;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] force-drain deadline reached");
		}
		HashSet<ulong> owned;
		lock (m_owned) owned = m_owned.Keys.ToHashSet();
		// Cheapest first (Partitioning.md 7.3/7.5): the client-less-first half is structural - a
		// non-forced drain only ever selects clientless partitions - and among those the smallest
		// estimated snapshot leaves the node soonest. Id breaks ties so the order is stable.
		PartitionManager.Partition next = partitions
			.Where(x => owned.Contains(x.Id) && !x.Members.Any(ClusterRuntime.IsFrozen))
			.Where(x => !GetState(x.Id).Pending && !m_apply.IsPending(x.Id))
			.Where(x => !m_drainFinalRetry.TryGetValue(x.Id, out DateTime retry) || retry <= DateTime.UtcNow)
			.Where(x => force || !clients.Any(client => client.Partition == x.Id))
			.OrderBy(PartitionManager.EstimatedSnapshotBytes).ThenBy(x => x.Id).FirstOrDefault();
		if (next == null)
		{
			// T-0028 INSTRUMENTATION, measurement only. A node that has drained but is still
			// owning something spins here forever: `next` stays null, the node never reaches
			// the `empty` branch that logs "drain finals registered; node Empty", and it
			// lingers - measured 645 s and 322 s on two of 80 incarnations while the median
			// is 3 s. The node log cannot currently say WHY, because nothing records the owned
			// set: "partition save registered" is not an ownership record, and I mistook it
			// for one. The gateway cannot answer it either - lease expiry does not
			// discriminate (the 645 s straggler had 1, against a median of 2 across 83
			// node-epochs). So state the blocked set directly, with the reason each member
			// was rejected by the selector above.
			//
			// Throttled to once per force-drain window (m_forceDrainLogged already gates the
			// deadline line the same way) so this cannot become its own log storm - the very
			// failure mode this ticket is about.
			if (force && !m_blockedOwnedLogged)
			{
				m_blockedOwnedLogged = true;
				string blocked = string.Join(", ", partitions
					.Where(x => owned.Contains(x.Id))
					.OrderBy(x => x.Id)
					.Select(x => x.Id + ":"
						+ (x.Members.Any(ClusterRuntime.IsFrozen) ? "frozen" : "")
						+ (GetState(x.Id).Pending ? "pending" : "")
						+ (m_apply.IsPending(x.Id) ? "registryPending" : "")
						+ (m_drainFinalRetry.ContainsKey(x.Id) ? "finalRetry" : "")
						+ (clients.Any(client => client.Partition == x.Id) ? "hasClients" : "")));
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain blocked: owned=" + owned.Count
					+ " selectable=0 blocked=[" + blocked + "]");
			}
			return;
		}
		// #1390: the drain's Final is the save the partition goes offline (or to its next node) with, so its wreckage goes
		// first: once per partition and drain, never emptying it, and the next heartbeat picks again.
		if (m_wreckCleaned.Add(next.Id) && WreckageCleaner.BeforeOffline(next, this, "before drain", mayEmpty: false) > 0) return;
		if (force && MyMultiplayer.Static != null)
			foreach (PartitionManager.ClientPartition client in clients.Where(x => x.Partition == next.Id))
				MyMultiplayer.Static.DisconnectClient(client.Client);
		Guid freezeId = Guid.NewGuid();
		HashSet<long> members = next.Members.ToHashSet();
		// T-0237: decided BEFORE the freeze below, which disables physics and would read every body as at rest.
		Func<PendingSave, object> offlineOffer = DrainOfflineOffer(next, clients);
		Dictionary<long, DrainFrozenState> frozen = new Dictionary<long, DrainFrozenState>();
		foreach (MyEntity entity in MyEntities.GetEntities().Where(x => members.Contains(x.EntityId)))
		{
			ClusterRuntime.MarkFrozen(entity.EntityId, freezeId);
			frozen[entity.EntityId] = DrainFrozenState.Of(entity);
			entity.NeedsUpdate = MyEntityUpdateEnum.NONE;
			if (entity.Physics != null) entity.Physics.Enabled = false;
		}
		try
		{
			PendingSave save = QueueCapture(next, "Final", frozen: frozen);
			var drainSave = new DrainSave(next.Id, GetState(next.Id).Generation, members, freezeId, save, frozen) { OfflineBody = offlineOffer?.Invoke(save) };
			lock (m_drainSaves) m_drainSaves[next.Id] = drainSave;
		}
		catch (Exception exception)
		{
			ThawDrainMembers(frozen, freezeId);
			// T-0350: the save queue stayed full for the bounded slot wait (the worker is retrying a save it cannot
			// register). Nothing catches between here and the game loop - ClusterRuntime.Update runs as a
			// MySandboxGame.Invoke action - so rethrown this ends the process mid-drain. The members are thawed above;
			// the next tick selects the partition again.
			if (exception is not TimeoutException) throw;
			m_drainSlotLoggedTicks.TryGetValue(next.Id, out long logged);
			if (RegistryHoldPolicy.LogDue(ref logged, DateTime.UtcNow.Ticks, TimeSpan.TicksPerMinute))
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain capture of partition " + next.Id + " deferred, retrying next tick: " + exception.Message);
			m_drainSlotLoggedTicks[next.Id] = logged;
		}
	}

	/// <summary>
	/// SE-835, SE-863: a Final the registry did not accept is the drain's only way to finish this partition. Left in
	/// m_drainSaves it blocked every later capture for good, with its members frozen: the node stayed Closed, its other
	/// partitions got no Final, and the stop ran out its grace with the rest of the cluster already gone. The refusal has
	/// already reconciled the partition's generation and sequence (RegisterSave), so the partition goes back to the
	/// selector and a fresh capture tries again. Nothing is given up: the node reports Empty only with every Final accepted.
	/// </summary>
	private void RetryDrainFinal(DrainSave drain)
	{
		ThawDrainMembers(drain.Frozen, drain.FreezeId);
		lock (m_drainSaves) m_drainSaves.Remove(drain.Partition);
		m_drainFinalRetry[drain.Partition] = DateTime.UtcNow.AddSeconds(FencedRegistrationRetrySeconds);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain final not accepted: partition " + drain.Partition + " generation " + drain.Save.Generation
			+ " sequence " + drain.Save.Sequence + (drain.Save.LastError == null ? "" : ": " + drain.Save.LastError)
			+ "; capturing it again in " + FencedRegistrationRetrySeconds + " s");
	}

	private static void ThawDrainMembers(Dictionary<long, DrainFrozenState> frozen, Guid freezeId)
	{
		foreach ((long id, DrainFrozenState state) in frozen)
		{
			if (MyEntities.TryGetEntityById(id, out MyEntity entity))
			{
				entity.NeedsUpdate = state.NeedsUpdate;
				if (state.HadPhysics && entity.Physics != null) entity.Physics.Enabled = state.PhysicsEnabled;
			}
			ClusterRuntime.ClearFrozen(id, freezeId);
		}
	}

	/// <summary>SE-835: partition -> when its drain Final may be captured again after one was not accepted. Game thread only.</summary>
	private readonly Dictionary<ulong, DateTime> m_drainFinalRetry = new Dictionary<ulong, DateTime>();

	/// <summary>T-0350: partition -> when its deferred drain capture was last logged (once a minute). Game thread only.</summary>
	private readonly Dictionary<ulong, long> m_drainSlotLoggedTicks = new Dictionary<ulong, long>();

	public byte[] QueueHandoverTee(PartitionManager.Partition partition, MyObjectBuilder_EntityBase[] entities)
	{
		// T-0350: the tee cannot be catalogued, so the wait below would only hold the game thread for 30 s.
		if (RegistryHeld) throw new InvalidOperationException("Registry unreachable: handover tee refused");
		State state = GetState(partition.Id);
		// Snapshot: the write worker nulls ActiveSave on completion from its own thread,
		// so a check-then-deref races it (NRE'd a capture mid-sweep exactly once).
		PendingSave activeSave = state.ActiveSave;
		if (activeSave != null && !activeSave.Completed.Wait(TimeSpan.FromSeconds(30)))
			throw new TimeoutException("Timed out waiting for the previous partition save");
		PendingSave pending = QueueCaptured(partition, "HandoverTee", entities, waitForSlot: true)
			?? throw new InvalidOperationException("Failed to queue handover tee");
		if (!pending.Completed.Wait(TimeSpan.FromSeconds(30)) || !pending.Succeeded) throw new InvalidOperationException("Handover tee was not durably catalogued");
		return pending.File;
	}

	/// <summary>
	/// tee.async (HandoverImprovements.md §4): serialise the tee on the calling (game) thread, queue the
	/// bytes for the durable write and return at once - no wait for the previous save nor for this one.
	/// The task completes with the save's Succeeded (registry-catalogued) outcome; the gateway polls it
	/// through /tee-status before it activates the transfer. A previous save still in flight is waited
	/// for on a worker, never here.
	/// </summary>
	public (byte[] File, Task<bool> Durable) QueueHandoverTeeAsync(PartitionManager.Partition partition, MyObjectBuilder_EntityBase[] entities)
	{
		if (RegistryHeld) throw new InvalidOperationException("Registry unreachable: handover tee refused");
		State state = GetState(partition.Id);
		// The sequence is RESERVED now so the file the gateway ships is the file the catalogue will
		// name; a periodic save queued before the worker gets to this one takes the next number and
		// may land first - same frozen content, so the catalogue order is immaterial.
		uint generation = state.Generation;
		uint sequence;
		lock (state) sequence = ++state.Sequence;
		long clusterTime = ClusterRuntime.CatalogClusterMilliseconds();
		byte[] file = PartitionSaveFile.Write(partition.Id, generation, sequence, 1, partition.Sphere, partition.Anchored, entities.Length,
			PartitionSaveFile.SerializeEntities(entities), clusterTime);
		Task<bool> durable = Task.Run(() =>
		{
			PendingSave activeSave = state.ActiveSave;
			if (activeSave != null && !activeSave.Completed.Wait(TimeSpan.FromSeconds(30)))
				throw new TimeoutException("Timed out waiting for the previous partition save");
			PendingSave pending = QueueCaptured(partition, "HandoverTee", entities, waitForSlot: true, presetFile: file, presetSequence: sequence)
				?? throw new InvalidOperationException("Failed to queue handover tee");
			if (!pending.Completed.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Handover tee write timed out");
			return pending.Succeeded;
		});
		return (file, durable);
	}

	private PendingSave QueueCapture(PartitionManager.Partition partition, string kind, bool waitForSlot = true,
		Dictionary<long, DrainFrozenState> frozen = null)
	{
		Stopwatch capture = Stopwatch.StartNew();
		MyObjectBuilder_EntityBase[] entities = MyEntities.GetEntities().Where(entity => partition.Members.Contains(entity.EntityId))
			.Select(entity =>
			{
				entity.BeforeSave();
				MyObjectBuilder_EntityBase builder = entity.GetObjectBuilder();
				// T-0361: a member frozen for this capture reads as at rest; it is saved with the velocity it had.
				if (frozen != null && frozen.TryGetValue(entity.EntityId, out DrainFrozenState state) && state.HadPhysics)
					ApplyFrozenVelocity(builder, state.LinearVelocity, state.AngularVelocity);
				return builder;
			}).ToArray();
		capture.Stop();
		WarnOnDoubleCapturedPilots(partition.Id, kind, entities);
		return QueueCaptured(partition, kind, entities, waitForSlot, capture.Elapsed.TotalMilliseconds);
	}

	/// <summary>
	/// T-0213: a character captured BOTH as a top-level entity and as a cockpit's Pilot in the same
	/// save. The two are one id written twice; on import the character becomes a hierarchy child of
	/// the cockpit and the re-export emits it once, so the handover round-trip gate sees one entity
	/// fewer than the frozen source and aborts - permanently, once the retries run out
	/// (130846/130847/131260/148443 on w32, 2026-09-21/22; the same shape as 28/30/32, 2026-08-10).
	///
	/// The state is born earlier, when a grid is closed by MEMBERSHIP instead of through
	/// <see cref="CloseWithHierarchy"/>: vanilla ejects the seated pilot, the body survives at top
	/// level, something adopts it, and its id joins Members. That close is fixed in three places, but
	/// a save already carrying the duplicate stays broken and nothing SAID so - the first sign was a
	/// blocked partition two handovers later, with a residual diff of two genuinely different poses.
	///
	/// Detect it where it is created and name the partition, the character and the grid. Logging
	/// only: which of the two copies is live is not decidable here, and dropping the wrong one would
	/// lose a player's body rather than a duplicate of it.
	/// </summary>
	/// <summary>
	/// T-0224: the cross-partition duplicate check above compares TOP-LEVEL ids only, but a grid carries
	/// its seated pilot inside the cockpit's builder. A partition recovered from a save made while the
	/// pilot sat in its ship brought the character back as the cockpit's Pilot while the same character
	/// was already live top-level on another node: "Duplicate incarnation" on the next handover, then
	/// HandoverBlocked (Pilot-003, 175847 recovered 07:03:56 / 174917 on node-2, 2026-09-23 07:09:33).
	/// Here, unlike <see cref="WarnOnDoubleCapturedPilots"/>, it IS decidable which copy is live: the one
	/// on the other node is running and this one is not yet, so the cockpit is emptied and the live body
	/// stays authoritative. Returns the number of pilots removed.
	/// </summary>
	internal sealed record HomeElsewhere(long Entity, ulong Partition, long Version, DateTimeOffset Updated, long Captured = 0);

	/// <summary>Every character in recovered content: standing top-level bodies and cockpit pilots.</summary>
	internal static IEnumerable<MyObjectBuilder_Character> CharactersIn(IEnumerable<MyObjectBuilder_EntityBase> entities)
	{
		foreach (MyObjectBuilder_EntityBase entity in entities)
		{
			if (entity is MyObjectBuilder_Character character) yield return character;
			else if (entity is MyObjectBuilder_CubeGrid grid && grid.CubeBlocks != null)
				foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
					if (block is MyObjectBuilder_Cockpit { Pilot: { } pilot }) yield return pilot;
		}
	}

	/// <summary>
	/// T-0237: why a recovered body is stale, or null. Its player's home is in another partition that holds the
	/// current body; this copy is older when the version stamped beside it in the save is below the home's; when it
	/// is a DIFFERENT body than the home's with no stamp at all; or - for the home's own entity in a save from a build
	/// without stamps only - when the home changed after the save. A stamped save without a stamp for the home's OWN
	/// entity overrides the home only when the home partition holds a stamped copy captured after every covered save.
	/// </summary>
	internal static string StaleByIdentity(RecoveryClaim recovery, MyObjectBuilder_Character character)
	{
		long identity = character.OwningPlayerIdentityId ?? 0;
		if (identity == 0 || recovery.HomesByIdentity == null || !recovery.HomesByIdentity.TryGetValue(identity, out HomeElsewhere home)) return null;
		string where = "home body " + home.Entity + " in partition " + home.Partition;
		if (recovery.SaveHomeStamps != null && recovery.SaveHomeStamps.TryGetValue(character.EntityId, out long copy))
			return home.Version > 0 && copy < home.Version ? where + ", copy version " + copy + " < home version " + home.Version : null;
		// A DIFFERENT body of the player with no stamp was never the body its node reported as the player's home,
		// so it is not the current one: the home names another body and its partition holds it. The stamped-save
		// exemption below is for the home's OWN entity only. Without this, Pilot-010's old ...870 (w32 2026-09-25)
		// sat in stamped re-saves of 227687 with no stamp, loaded on every recovery (g4..g7), was despawned by the
		// World Authority each time, and each recovery's first Final carried it on to the next.
		if (character.EntityId != home.Entity)
			return where + ", a different body of the player with no home stamp (home updated " + home.Updated.ToString("O") + ")";
		// The home's own body in a stamped save with no stamp for it: captured before the node re-reported the home
		// here (a split output's first save lands ~0.5 s after the split). The home partition's stamped copy orders
		// it on the cluster clock - a stamp is written only for a body the save carries, so the body was THERE later.
		// w33 2026-09-27: Pilot-028's standing body split into 73626 at 06:41:31 (save 1/1, no stamp), then boarded;
		// the drain released 73626 memberless at 07:33 and the start recovered the 06:41 copy beside the seated one
		// in 75485's 07:33 Final (stamped) - node-1 emptied its cockpit (T-0224) and the pilot lost the ship.
		// A newer unstamped copy (the body moved here after the home's last stamped save) is kept.
		if (recovery.SaveStamped)
			return home.Captured > 0 && recovery.SaveCaptured > 0 && home.Captured > recovery.SaveCaptured
				? where + ", unstamped copy captured at cluster time " + recovery.SaveCaptured + " < home's stamped copy " + home.Captured
				: null;
		return recovery.SaveCreated is { } saved && home.Updated > saved
			? where + ", unstamped save " + saved.ToString("O") + " < home updated " + home.Updated.ToString("O") : null;
	}

	internal static int StripPilotsLiveElsewhere(ulong partitionId, IEnumerable<MyObjectBuilder_EntityBase> entities, Func<long, bool> liveElsewhere)
		=> StripPilots(partitionId, entities, liveElsewhere, "is already live on another node (nested duplicate prevented, T-0224)");

	/// <summary>Empties every cockpit in <paramref name="entities"/> whose pilot <paramref name="strip"/> selects, from the Pilot
	/// field and the hierarchy component both (T-0226); returns the number of pilots removed.</summary>
	internal static int StripPilots(ulong partitionId, IEnumerable<MyObjectBuilder_EntityBase> entities, Func<long, bool> strip, string reason)
	{
		int stripped = 0;
		foreach (MyObjectBuilder_EntityBase entity in entities)
		{
			if (entity is not MyObjectBuilder_CubeGrid grid || grid.CubeBlocks == null) continue;
			foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
			{
				if (block is not MyObjectBuilder_Cockpit cockpit || cockpit.Pilot == null || !strip(cockpit.Pilot.EntityId)) continue;
				long pilotId = cockpit.Pilot.EntityId;
				cockpit.Pilot = null;
				// The seat is serialised twice (see ClusterControlServer's ghost-seat strip): the cockpit's
				// hierarchy component carries the same character, and MyHierarchyComponentBase.Deserialize
				// creates every child that does not exist yet. A Pilot-only strip re-created Pilot-020 in the
				// emptied cockpit of P177785 on node-1 (2026-09-23 08:41:14), so the standalone copy's
				// handover to node-1 still aborted, builders differ (T-0226).
				int children = ClusterControlServer.RemoveHierarchyChild(cockpit, pilotId);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] recovery emptied cockpit " + cockpit.EntityId + " on grid " + grid.EntityId
					+ " in partition " + partitionId + ": its pilot " + pilotId
					+ " " + reason + " hierarchyChildrenRemoved=" + children);
				stripped++;
			}
		}
		return stripped;
	}

	/// <summary>
	/// T-0226: a character in a cockpit's hierarchy component that is not the cockpit's Pilot. The game only
	/// serialises a seated pilot there, so such a child is an artefact, and MyHierarchyComponentBase.Deserialize
	/// would create it as an unseated body inside the ship: a second incarnation of a character that lives on
	/// elsewhere. The Pilot-only strip of 98fa035/2cfaad5 left exactly this in Pilot-020's cockpit
	/// (72057600832749171) and the grid persisted it, so the duplicate came back on the next start
	/// (P178012/P178159, 2026-09-23 09:06). Returns the number of characters removed.
	/// </summary>
	internal static int StripOrphanSeatCharacters(ulong partitionId, IEnumerable<MyObjectBuilder_EntityBase> entities)
	{
		int removed = 0;
		foreach (MyObjectBuilder_EntityBase entity in entities)
		{
			if (entity is not MyObjectBuilder_CubeGrid grid || grid.CubeBlocks == null) continue;
			foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
			{
				if (block is not MyObjectBuilder_Cockpit cockpit || cockpit.ComponentContainer?.Components == null) continue;
				long pilotId = cockpit.Pilot?.EntityId ?? 0;
				foreach (VRage.Game.ObjectBuilders.ComponentSystem.MyObjectBuilder_ComponentContainer.ComponentData data in cockpit.ComponentContainer.Components)
				{
					if (data.Component is not VRage.Game.ObjectBuilders.Components.MyObjectBuilder_HierarchyComponentBase hierarchy || hierarchy.Children == null) continue;
					foreach (MyObjectBuilder_EntityBase child in hierarchy.Children.ToArray())
					{
						if (child is not MyObjectBuilder_Character || child.EntityId == pilotId) continue;
						hierarchy.Children.Remove(child);
						removed++;
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] removed orphan seat character " + child.EntityId + " from cockpit "
							+ cockpit.EntityId + " on grid " + grid.EntityId + " in partition " + partitionId
							+ " (not the cockpit's pilot " + pilotId + ", T-0226)");
					}
				}
			}
		}
		return removed;
	}

	internal static int WarnOnDoubleCapturedPilots(ulong partitionId, string kind, MyObjectBuilder_EntityBase[] entities)
	{
		int found = 0;
		if (entities == null || entities.Length == 0) return found;
		HashSet<long> topLevel = new HashSet<long>();
		foreach (MyObjectBuilder_EntityBase entity in entities)
			if (entity != null) topLevel.Add(entity.EntityId);
		foreach (MyObjectBuilder_EntityBase entity in entities)
		{
			if (entity is not MyObjectBuilder_CubeGrid grid || grid.CubeBlocks == null) continue;
			foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
			{
				if (block is not MyObjectBuilder_Cockpit cockpit || cockpit.Pilot == null) continue;
				if (!topLevel.Contains(cockpit.Pilot.EntityId)) continue;
				found++;
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] double-captured pilot: character "
					+ cockpit.Pilot.EntityId + " is captured as a top-level entity AND as the pilot of cockpit "
					+ cockpit.EntityId + " on grid " + grid.EntityId + " in partition " + partitionId
					+ " (" + kind + " capture, " + entities.Length + " entities). This save will fail the"
					+ " handover round-trip gate: the import collapses the two copies into one. T-0213.");
			}
		}
		return found;
	}

	private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(2);

	/// <summary>T-0350: the heartbeat loop found the registry unreachable; nothing that needs it may start on the game thread.</summary>
	public bool RegistryHeld => m_partitions.RegistryHold;

	/// <summary>T-0354: the partition's topology apply or claim is not answered yet - no save, capture, drain or offline of it.</summary>
	public bool RegistryPending(ulong partition) => m_apply.IsPending(partition);
	/// <summary>T-0354b: pending only for the claim of its id (<see cref="RegistryApplyQueue.IsClaimOnly"/>).</summary>
	public bool RegistryClaimOnly(ulong partition) => m_apply.IsClaimOnly(partition);

	private PendingSave QueueCaptured(PartitionManager.Partition partition, string kind, MyObjectBuilder_EntityBase[] entities, bool waitForSlot,
		double captureMilliseconds = 0, byte[] presetFile = null, uint presetSequence = 0)
	{
		State state = GetState(partition.Id);
		PendingSave pending;
		// The async tee queues from a worker while the periodic saver queues from the game thread;
		// the check-and-claim must be atomic or both pass and the partition carries two saves.
		lock (state)
		{
			if (state.Pending) throw new InvalidOperationException("Partition already has a pending save");
			uint sequence = presetFile != null ? presetSequence : ++state.Sequence;
			state.Pending = true;
			state.CaptureMilliseconds = state.CaptureMilliseconds == 0 ? captureMilliseconds : state.CaptureMilliseconds * 0.8 + captureMilliseconds * 0.2;
			pending = new PendingSave(partition.Id, state.Generation, sequence, kind, partition.Sphere, partition.Anchored,
				entities, ClusterRuntime.CatalogClusterMilliseconds(), captureMilliseconds)
			{
				File = presetFile,
				HomeStamps = entities == null ? null : CharacterHomeService.HomeStamps(partition.Id, entities),
			};
			state.ActiveSave = pending;
		}
		// T-0350: never an unbounded wait. The write worker may be retrying the registry (a Final or a handover tee
		// it cannot register), the queue of two then stays full, and the capture that waited here held the game thread
		// for 247 s inside a network window (node-98, r124, 2026-10-01).
		if (!(waitForSlot ? m_saves.TryAdd(pending, SlotWait) : m_saves.TryAdd(pending)))
		{
			lock (state)
			{
				if (presetFile == null) state.Sequence--;
				state.Pending = false;
				state.ActiveSave = null;
			}
			if (waitForSlot) throw new TimeoutException("The save queue stayed full for " + SlotWait.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s");
			return null;
		}
		return pending;
	}

	private static MyObjectBuilder_EntityBase[] Deserialize(PartitionSaveFile.Parsed save)
	{
		if (save.Kind != 5)
		{
			return PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount);
		}
		using MemoryStream stream = new MemoryStream(save.Body, writable: false);
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(stream, out MyObjectBuilder_Sector sector))
		{
			throw new InvalidDataException("Failed to deserialize conversion partition " + save.PartitionId);
		}
		return (sector.SectorObjects ?? new List<MyObjectBuilder_EntityBase>()).ToArray();
	}

	private IEnumerable<string> AssignedPaths()
	{
		string assigned = Environment.GetEnvironmentVariable(SavesEnvironmentVariable);
		if (!string.IsNullOrWhiteSpace(assigned))
		{
			return assigned.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFullPath);
		}
		string world = Environment.GetEnvironmentVariable(WorldEnvironmentVariable);
		string catalog = string.IsNullOrWhiteSpace(world) ? null : Path.Combine(Path.GetFullPath(world), "catalog-seed.json");
		if (catalog == null || !File.Exists(catalog))
		{
			return Array.Empty<string>();
		}
		using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(catalog));
		int slot = m_catalogSlot;
		return document.RootElement.GetProperty("partitions").EnumerateArray()
			.Where(entry => !entry.TryGetProperty("slot", out JsonElement assignedSlot) || assignedSlot.GetInt32() == slot)
			.Select(entry => Path.GetFullPath(Path.Combine(world, entry.GetProperty("save").GetString())))
			.ToArray();
	}

	private State GetState(ulong id)
	{
		return m_state.GetOrAdd(id, _ => new State { Generation = 1 });
	}

	private void WriteLoop()
	{
		foreach (PendingSave save in m_saves.GetConsumingEnumerable())
		{
			m_writing = true;
			while (true)
			{
				save.Attempts++;
				bool terminal = false;
				// T-0340: the full path (SavePath: the per-host store or the node's root), the location the registry is told.
				string path = null;
				PartitionSaveFile.Parsed parsed = null;
				try
				{
					Stopwatch serialize = Stopwatch.StartNew();
					// tee.async pre-serialises on the capturing thread so the shipped and the catalogued
					// bytes are one file; everything else serialises here, off the game thread.
					if (save.File == null)
					{
						byte[] body = PartitionSaveFile.SerializeEntities(save.Entities);
						save.File = PartitionSaveFile.Write(save.PartitionId, save.Generation, save.Sequence, save.Kind == "Final" ? (byte)2 : save.Kind == "HandoverTee" ? (byte)1 : (byte)0,
							save.Sphere, save.Anchored, save.Entities.Length, body, save.ClusterTime);
					}
					serialize.Stop();
					save.SerializeMilliseconds = serialize.Elapsed.TotalMilliseconds;
					if (m_writeDelayMilliseconds != 0) Thread.Sleep(m_writeDelayMilliseconds);
					Stopwatch write = Stopwatch.StartNew();
					path = SavePath(save.PartitionId, save.Generation, save.Sequence);
					// T-0345: a Periodic save is not fsynced (PartitionSaveFile.FsyncEverySave); the kinds a node hands
					// ownership over with are.
					bool durable = save.Kind != "Periodic" || PartitionSaveFile.FsyncEverySave;
					PartitionSaveFile.WriteFile(path, save.File, durable, m_hostStore ? m_node : null);
					parsed = PartitionSaveFile.ReadFile(path);
					string manifest = JsonSerializer.Serialize(new { op = "write", key = save.PartitionId, gen = save.Generation,
						seq = save.Sequence, kind = parsed.Kind, size = save.File.Length, xxh3 = parsed.BodyChecksum.ToString("X16"), clusterTime = parsed.ClusterTime,
						wallTime = DateTimeOffset.UtcNow }) + Environment.NewLine;
					Directory.CreateDirectory(m_root);
					using (FileStream stream = new FileStream(Path.Combine(m_root, "manifest.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
					{
						using StreamWriter writer = new StreamWriter(stream);
						writer.Write(manifest);
						writer.Flush();
						stream.Flush(durable);
					}
					// T-0347: a local stop does not spend its budget asking a registry the heartbeat already found unreachable
					// (10 s per save when it does not even refuse the connection).
					if (m_localStop && RegistryEnabled && RegistryHeld)
						throw new RegistryUnreachableException("registry unreachable (held); not asked during a local stop", null);
					save.Succeeded = !RegistryEnabled || RegisterSave(save, path, parsed);
					write.Stop();
					save.WriteMilliseconds = write.Elapsed.TotalMilliseconds;
					if (save.Succeeded && m_state.TryGetValue(save.PartitionId, out State state))
					{
						state.LastCataloguedUtc = DateTime.UtcNow;
						state.LastCataloguedClusterTime = parsed.ClusterTime;
						state.SerializeMilliseconds = save.SerializeMilliseconds;
						state.WriteMilliseconds = save.WriteMilliseconds;
					}
					terminal = true;
				}
				catch (RegistryUnreachableException exception)
				{
					save.LastError = SecretRedactor.Redact(exception.Message);
					save.Unregistered = true;
					// T-0350: the save worker saw it first (no answer; a 5xx is left to the heartbeat).
					NoteRegistryFailure(exception);
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] partition save written but not registered: partition " + save.PartitionId
						+ " generation " + save.Generation + " sequence " + save.Sequence + " kind " + save.Kind + ": " + save.LastError);
				}
				catch (Exception exception)
				{
					save.LastError = SecretRedactor.Redact(exception.Message);
					save.Unregistered = false;
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: partition save failed: " + SecretRedactor.Redact(exception.ToString()));
				}
				if (save.Succeeded || terminal || m_fenced)
					break;
				// T-0347: in a local stop a Final the registry cannot be told about is not retried (nothing answers, and the
				// retry holds the queue the other partitions' Finals wait behind). The file is written and fsynced; the
				// journal entry is what lets the next start catalogue it.
				if (m_localStop && save.Unregistered && save.Kind == "Final" && parsed != null)
				{
					try
					{
						ClusterProtocol.UnregisteredFinals.Append(m_root, new ClusterProtocol.UnregisteredFinal
						{
							Partition = save.PartitionId, Generation = save.Generation, Sequence = save.Sequence, SizeBytes = save.File.Length,
							Checksum = parsed.BodyChecksum.ToString("X16"), ClusterTime = parsed.ClusterTime, Entities = parsed.EntityCount,
							Homes = save.HomeStamps, Location = path, Writer = m_node, WriterEpoch = m_nodeEpoch,
						});
						save.LocalOnly = true;
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] local stop: Final of partition " + save.PartitionId + " generation " + save.Generation
							+ " sequence " + save.Sequence + " written and journaled; the next start reports it to the registry");
					}
					catch (Exception exception)
					{
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: local stop could not journal the Final of partition " + save.PartitionId
							+ "; the next start will not know of it: " + SecretRedactor.Redact(exception.ToString()));
					}
					break;
				}
				if (!SaveRetryPolicy.ShouldRetry(save.Kind, save.Attempts, save.Unregistered))
				{
					// T-0349: an unreachable registry is no reason to report a save failure (the registry Closes a
					// node that reports one); the next periodic capture registers a newer save.
					if (!save.Unregistered)
						m_lastSaveFailure = new SaveFailure(save.PartitionId, save.LastError ?? "save failed", save.Attempts);
					break;
				}
				Thread.Sleep(1000);
			}
			if (m_state.TryGetValue(save.PartitionId, out State completedState))
			{
				completedState.Pending = false;
				completedState.ActiveSave = null;
				// A journaled Final keeps its slot: the file under that sequence is the one the journal names.
				if (!save.Succeeded && !save.LocalOnly && completedState.Sequence == save.Sequence) completedState.Sequence--;
			}
			if (m_localStop && save.Kind == "Final" && (save.Succeeded || save.LocalOnly)) m_localFinals[save.PartitionId] = 0;
			save.Completed.Set();
			m_writing = false;
		}
	}

	private static readonly string[] SaveKindNames = { "Periodic", "HandoverTee", "Final", "Sweep", "Conversion", "Repair" };

	/// <summary>
	/// Catalogue the assigned save the startup claim was granted on, immediately - not at the next
	/// periodic capture. The gap bit twice on 2026-08-10: a fresh split's seed saves were never
	/// catalogued at all, and a restart inside the first capture interval left 18 partitions with an
	/// empty catalog - in both cases recovery had nothing to claim and the content was stranded. For
	/// a concealed partition this registration is the only one there is: cold partitions never
	/// capture, so the gap never closes on its own.
	/// </summary>
	private void RegisterAssignedSave(HttpClient client, ulong id, State state)
	{
		if (state.AssignedPath == null) return;
		string kind = state.AssignedKind < SaveKindNames.Length ? SaveKindNames[state.AssignedKind] : "Periodic";
		// The catalogued holder must be able to SERVE the save: /cluster/save-file resolves by
		// convention inside this node's own store and never consults the catalogued location. A
		// fresh split's seed save lives in the split world, so cataloguing that path leaves the
		// peer's replica pump fetching a file the holder does not have - a 500 retried once per
		// second forever, and a seed generation that never reaches its replica target. Materialize
		// the assigned save into the store first and catalogue the store path instead.
		string location = state.AssignedPath;
		string stored = SavePath(id, state.Generation, state.AssignedSequence);
		if (!string.Equals(Path.GetFullPath(location), Path.GetFullPath(stored), StringComparison.Ordinal))
		{
			try
			{
				if (!File.Exists(stored))
				{
					Directory.CreateDirectory(Path.GetDirectoryName(stored));
					string temporary = m_hostStore ? ClusterProtocol.HostSaveStore.Temporary(stored, m_node) : stored + ".tmp";
					File.Copy(location, temporary, true);
					// T-0340: in the host store a file another node installed meanwhile is the same assigned save.
					if (m_hostStore) ClusterProtocol.HostSaveStore.Install(temporary, stored, _ => true, replaceOther: false);
					else File.Move(temporary, stored, true);
				}
				location = stored;
			}
			catch (Exception exception)
			{
				// Catalogue the assigned path rather than nothing: recovery can still claim it on
				// this host, only cross-node replication of this save stays broken.
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] startup save not materialized into the store: partition "
					+ id + ": " + SecretRedactor.Redact(exception.Message));
			}
		}
		for (int attempt = 1; attempt <= 5; attempt++)
		{
			try
			{
				Post(client, "/registry/saves?partition=" + id.ToString(CultureInfo.InvariantCulture)
					+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
					+ "&generation=" + state.Generation.ToString(CultureInfo.InvariantCulture)
					+ "&sequence=" + state.AssignedSequence.ToString(CultureInfo.InvariantCulture)
					+ "&kind=" + kind
					+ "&size=" + state.AssignedSize.ToString(CultureInfo.InvariantCulture)
					+ "&checksum=" + state.AssignedChecksum.ToString("X16")
					+ "&clusterTime=" + state.AssignedClusterTime.ToString(CultureInfo.InvariantCulture)
					+ "&location=" + Uri.EscapeDataString(location)
					+ "&control=" + Uri.EscapeDataString(m_controlEndpoint ?? string.Empty)).Dispose();
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] startup save catalogued: partition " + id
					+ " generation " + state.Generation + " sequence " + state.AssignedSequence);
				return;
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] startup save catalogue attempt failed: partition " + id
					+ ": " + SecretRedactor.Redact(exception.Message));
				Thread.Sleep(1000);
			}
		}
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: startup save not catalogued: partition " + id
			+ "; a restart before the first capture cannot recover this partition");
	}

	/// <summary>
	/// Adopts the registry's generation and newest catalogued sequence for an owned partition whose
	/// save registrations the registry keeps fencing. Quiet when nothing differs; the caller retries
	/// the capture on its own cadence.
	/// </summary>
	private void ReconcileSaveState(ulong partitionId)
	{
		try
		{
			using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
			ClusterRuntime.AuthorizeRegistryClient(client);
			long ownerGeneration = -1;
			using (JsonDocument partitions = Get(client, "/registry/partitions"))
				foreach (JsonElement partition in partitions.RootElement.EnumerateArray())
					if (partition.GetProperty("Partition").GetUInt64() == partitionId
						&& partition.GetProperty("Node").GetString() == m_node)
						ownerGeneration = partition.GetProperty("Generation").GetInt64();
			if (ownerGeneration < 0) return;   // not ours per the registry; the fenced-orphan logic owns that case
			long maxSequence = 0;
			using (JsonDocument saves = Get(client, "/registry/saves?partition=" + partitionId.ToString(CultureInfo.InvariantCulture)))
				foreach (JsonElement row in saves.RootElement.EnumerateArray())
				{
					JsonElement id = row.GetProperty("SaveId");
					if (id.GetProperty("Partition").GetUInt64() == partitionId && id.GetProperty("Generation").GetInt64() == ownerGeneration)
						maxSequence = Math.Max(maxSequence, id.GetProperty("Sequence").GetInt64());
				}
			bool changed = false;
			if (m_state.TryGetValue(partitionId, out State state)
				&& (state.Generation != (uint)ownerGeneration || state.Sequence < (uint)maxSequence))
			{
				state.Generation = checked((uint)ownerGeneration);
				state.Sequence = checked((uint)maxSequence);
				changed = true;
			}
			lock (m_owned)
				if (m_owned.TryGetValue(partitionId, out uint generation) && generation != (uint)ownerGeneration)
				{
					m_owned[partitionId] = checked((uint)ownerGeneration);
					changed = true;
				}
			if (changed)
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] save state reconciled: partition " + partitionId
					+ " now at registry generation " + ownerGeneration + " sequence " + maxSequence);
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] save state reconcile unavailable: " + SecretRedactor.Redact(exception.Message));
		}
	}

	/// <summary>
	/// T-0139/T-0155: which startup claim conflict this is. The claim sends expectedGeneration = local - 1.
	/// A registry generation AHEAD of that is a stale local copy (a template seed, or a partition that
	/// moved on while this node was down) - expected, recovery serves the newer save. Anything else -
	/// the registry BEHIND the local save, or an unrecognised refusal - is worth a look.
	/// </summary>
	/// <summary>
	/// The generation the registry says it holds, from its own 409 text. Same match as
	/// <see cref="DescribeStartupConflict"/> reads for the log line - the number is already there,
	/// and T-0207 needs it as a value rather than as prose.
	/// </summary>
	internal static bool TryReadRegistryGeneration(string message, out long generation)
	{
		generation = 0;
		System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(message ?? "",
			@"is generation (\d+), not (-?\d+)");
		return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out generation);
	}

	/// <summary>
	/// True only when the registry names THIS node as the partition's owner. The inverse of
	/// <see cref="FencedPartitionOwnedElsewhere"/> is not good enough for T-0207's retry: that one
	/// also answers false for an UNOWNED partition, and an unowned partition is the case ordinary
	/// recovery already handles correctly. Any doubt - unknown partition, retired, registry
	/// unreachable - answers false and leaves the existing fence in place.
	/// </summary>
	private bool PartitionOwnerIsThisNode(HttpClient client, ulong partitionId)
	{
		try
		{
			using JsonDocument partitions = Get(client, "/registry/partitions");
			foreach (JsonElement partition in partitions.RootElement.EnumerateArray())
			{
				if (partition.GetProperty("Partition").GetUInt64() != partitionId) continue;
				if (partition.TryGetProperty("Retired", out JsonElement retired) && retired.GetBoolean())
					return false;
				return partition.GetProperty("Node").GetString() == m_node;
			}
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] partition ownership check unavailable: "
				+ SecretRedactor.Redact(exception.Message));
		}
		return false;
	}

	internal static string DescribeStartupConflict(uint localGeneration, string message)
	{
		System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(message ?? "",
			@"is generation (\d+), not (-?\d+)");
		if (!match.Success)
			return "local generation " + localGeneration + ", registry refused: " + SecretRedactor.Redact(message)
				+ " - not a generation mismatch; investigate rather than dismiss";
		long registry = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
		if (localGeneration == 0)
			return "template seed, never claimed in this world - expected, T-0139";
		if (registry > localGeneration - 1L)
			return "stale local copy: local generation " + localGeneration + ", registry at " + registry
				+ " - expected, the partition moved on (T-0155)";
		return "local generation " + localGeneration + " is AHEAD of the registry's " + registry
			+ " - a real generation conflict; investigate rather than dismiss";
	}

	/// <summary>
	/// True only when the registry reports the partition live on ANOTHER node - the one case where
	/// this node's fenced copy is provably a stale duplicate. Unowned (Node null) means the registry
	/// cannot recover the partition from anywhere, so the fenced local content may be the only copy
	/// in existence. On any doubt (including registry unavailability) answer false: refusing to
	/// simulate is recoverable, deleting is not.
	/// </summary>
	/// <summary>
	/// T-0231: why a catalog-seed save must not load at boot, or null to load it. The seed is the world-creation
	/// copy (generation 1); once the registry has a row for its partition that is retired, offline, owned by
	/// another node or at the seed's generation or past it, the seed is stale by construction - every node
	/// relaunch in slot 1 or 2 used to instantiate it anyway (the Earth base, 3628 blocks, on node-2 for ~60 s at
	/// the w32 start 2026-09-25 03:02) and only the fence threw it away again. No row and no saves (a fresh world) or
	/// an unreachable registry loads as before; the startup claim's fence still guards those. SE-835: no row WITH saves
	/// is a partition the registry reaped as empty, and its content is gone or lives on elsewhere: the seed would bring
	/// a deleted grid back, at a generation below those saves.
	/// </summary>
	private string SeedSuperseded(PartitionSaveFile.Parsed save)
	{
		try
		{
			using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
			ClusterRuntime.AuthorizeRegistryClient(client);
			using JsonDocument partitions = Get(client, "/registry/partitions?partition=" + save.PartitionId.ToString(CultureInfo.InvariantCulture));
			foreach (JsonElement row in partitions.RootElement.EnumerateArray())
			{
				if (row.GetProperty("Partition").GetUInt64() != save.PartitionId) continue;   // an older gateway ignores the filter
				if (row.TryGetProperty("Retired", out JsonElement retired) && retired.ValueKind == JsonValueKind.True) return "the registry row is retired";
				if (row.TryGetProperty("Offline", out JsonElement offline) && offline.ValueKind == JsonValueKind.True) return "the partition is offline";
				long generation = row.GetProperty("Generation").GetInt64();
				if (generation >= save.Generation) return "the registry row is at generation " + generation;
				string owner = row.TryGetProperty("Node", out JsonElement node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
				if (owner != null && owner != m_node) return "owned by " + owner;
				return null;
			}
			using JsonDocument saves = Get(client, "/registry/saves?partition=" + save.PartitionId.ToString(CultureInfo.InvariantCulture));
			return saves.RootElement.EnumerateArray().Any(row => row.GetProperty("SaveId").GetProperty("Partition").GetUInt64() == save.PartitionId)
				? "the registry reaped its row, its saves remain" : null;
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] catalog seed check failed for partition " + save.PartitionId
				+ "; loading it as before: " + SecretRedactor.Redact(exception.Message));
			return null;
		}
	}

	private bool FencedPartitionOwnedElsewhere(ulong partitionId)
	{
		try
		{
			using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
			ClusterRuntime.AuthorizeRegistryClient(client);
			using JsonDocument partitions = Get(client, "/registry/partitions?partition=" + partitionId.ToString(CultureInfo.InvariantCulture));
			foreach (JsonElement partition in partitions.RootElement.EnumerateArray())
			{
				if (partition.GetProperty("Partition").GetUInt64() != partitionId) continue;
				// Retired means lineage migrated the content into a successor whose saves are
				// catalogued - the fenced local copy is provably stale and blocks the successor
				// from ever co-locating here (partition 24 vs 27, 2026-08-10).
				if (partition.TryGetProperty("Retired", out JsonElement retired) && retired.GetBoolean())
					return true;
				// T-0193: an offline partition's truth is its offline Final; a seed or template copy loaded
				// here at boot is stale by construction and must not survive to meet the real one.
				if (partition.TryGetProperty("Offline", out JsonElement offline) && offline.ValueKind == JsonValueKind.True)
					return true;
				string ownerNode = partition.GetProperty("Node").GetString();
				return ownerNode != null && ownerNode != m_node;
			}
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] fenced partition ownership check unavailable: "
				+ SecretRedactor.Redact(exception.Message));
		}
		return false;
	}

	private bool RegisterSave(PendingSave save, string path, PartitionSaveFile.Parsed parsed)
	{
		try
		{
			using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
			ClusterRuntime.AuthorizeRegistryClient(client);
				using JsonDocument registration = Post(client, "/registry/saves?partition=" + save.PartitionId.ToString(CultureInfo.InvariantCulture)
					+ "&node=" + Uri.EscapeDataString(m_node) + "&epoch=" + m_nodeEpoch.ToString(CultureInfo.InvariantCulture)
					+ "&generation=" + save.Generation.ToString(CultureInfo.InvariantCulture)
					+ "&sequence=" + save.Sequence.ToString(CultureInfo.InvariantCulture) + "&kind=" + save.Kind
					+ "&size=" + save.File.Length.ToString(CultureInfo.InvariantCulture) + "&checksum=" + parsed.BodyChecksum.ToString("X16")
					+ "&clusterTime=" + parsed.ClusterTime.ToString(CultureInfo.InvariantCulture)
					+ "&location=" + Uri.EscapeDataString(path)
					+ "&control=" + Uri.EscapeDataString(m_controlEndpoint ?? string.Empty)
					+ "&entities=" + parsed.EntityCount.ToString(CultureInfo.InvariantCulture)
					+ (save.HomeStamps == null ? string.Empty : "&homes=" + Uri.EscapeDataString(save.HomeStamps)));
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] partition save registered: partition " + save.PartitionId
					+ " generation " + save.Generation + " sequence " + save.Sequence + " kind " + save.Kind);
				m_fencedRegistrations.TryRemove(save.PartitionId, out _);
				m_fencedRegistrationRetry.TryRemove(save.PartitionId, out _);
				return !m_fenced;
		}
		catch (Exception exception)
		{
			if (exception is HttpRequestException request && request.StatusCode == HttpStatusCode.Conflict)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] save registration fenced: " + save.PartitionId);
				save.LastError = SecretRedactor.Redact(exception.Message);
				m_fencedRegistrationRetry[save.PartitionId] = DateTime.UtcNow.AddSeconds(FencedRegistrationRetrySeconds);
				// A partition we do not own whose registrations the registry keeps fencing is a
				// startup leftover that lost the recovery race: the node's own world file loaded its
				// entities, the startup claim 409'd ("waiting for recovery"), and another node then
				// recovered the partition - leaving a live duplicate incarnation here that the
				// balancer chases forever (partition 13, 2026-08-10). Three consecutive refusals
				// rule out a mid-claim race; the game thread then closes the stale content.
				bool owned;
				lock (m_owned) owned = m_owned.ContainsKey(save.PartitionId);
				if (owned)
				{
					// The registry refusing saves for a partition we own means our local generation or
					// sequence trails the registry's - a boot-time re-claim raced (node-3 2026-08-11:
					// double recovery claim left generation 19 sequence 0 locally, and every capture
					// rewrote the already-catalogued 19/1 slot for 7 hours, never cataloguing the live
					// content). Pull the registry's view and adopt it; the next capture then registers
					// a fresh, higher sequence cleanly.
					ReconcileSaveState(save.PartitionId);
					return false;
				}
				if (m_registryReady
					&& m_fencedRegistrations.AddOrUpdate(save.PartitionId, 1, (_, strikes) => strikes + 1) == 3)
				{
					if (FencedPartitionOwnedElsewhere(save.PartitionId))
						m_fencedOrphans.Enqueue(save.PartitionId);
					else
					{
						// Unowned in the registry means nobody else holds a live copy - deleting ours
						// would destroy the only incarnation of the content (this erased 7 seed
						// partitions after the 2026-08-10 fresh; LostEarthStarterBase.md). Stay
						// fenced, keep the entities, and step the strikes back so a later genuine owner
						// still triggers the purge. One step, not three: the refusals are already
						// FencedRegistrationRetrySeconds apart, so the next one re-checks the owner
						// (T-0155; the Gateway usually recovers the partition elsewhere within a minute).
						m_fencedRegistrations[save.PartitionId] = 2;
						if (m_fencedKeptLogged.TryAdd(save.PartitionId, 0))
							MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] fenced partition kept: " + save.PartitionId
								+ " has no other registry owner; local content preserved (re-checked every "
								+ FencedRegistrationRetrySeconds + " s, logged once)");
					}
				}
				return false;
			}
			// T-0349: the file is on disk; an unreachable registry is not a failed save.
			if (SaveRetryPolicy.RegistryUnreachable(exception))
				throw new RegistryUnreachableException("registry unreachable: " + exception.Message, exception);
			throw;
		}
	}

	private sealed class State
	{
		public uint Generation;
		public uint Sequence;
		public string AssignedPath;
		public uint AssignedSequence;
		public byte AssignedKind;
		public long AssignedSize;
		public ulong AssignedChecksum;
		public long AssignedClusterTime;
		public bool Pending;
		public PendingSave ActiveSave;
		public DateTime LastCataloguedUtc;
		public long LastCataloguedClusterTime;
		public double CaptureMilliseconds;
		public double SerializeMilliseconds;
		public double WriteMilliseconds;
	}

	private sealed record PendingSave(ulong PartitionId, uint Generation, uint Sequence, string Kind, VRageMath.BoundingSphereD Sphere,
		bool Anchored, MyObjectBuilder_EntityBase[] Entities, long ClusterTime, double CaptureMilliseconds)
	{
		public ManualResetEventSlim Completed { get; } = new ManualResetEventSlim();
		public bool Succeeded { get; set; }
		public byte[] File { get; set; }
		public double SerializeMilliseconds { get; set; }
		public double WriteMilliseconds { get; set; }
		public int Attempts { get; set; }
		public string LastError { get; set; }
		/// <summary>T-0349: the last attempt wrote the file but could not reach the registry.</summary>
		public bool Unregistered { get; set; }
		/// <summary>T-0347: a local stop's Final, written and journaled (UnregisteredFinals) but not catalogued.</summary>
		public bool LocalOnly { get; set; }
		/// <summary>T-0237: the character home stamps registered with the save (CharacterHomeService.HomeStamps); null = unknown.</summary>
		public string HomeStamps { get; init; }
	}
	private sealed record SaveFailure(ulong Partition, string Error, int Attempts);
	private sealed class DrainSave
	{
		public readonly ulong Partition;
		public readonly uint Generation;
		public readonly HashSet<long> Members;
		public readonly Guid FreezeId;
		public readonly PendingSave Save;
		public readonly Dictionary<long, DrainFrozenState> Frozen;
		public volatile bool Unloaded;
		/// <summary>T-0237: the offline offer to post in place of the release, or null to release as before.</summary>
		public volatile object OfflineBody;
		public DrainSave(ulong partition, uint generation, HashSet<long> members, Guid freezeId, PendingSave save, Dictionary<long, DrainFrozenState> frozen)
		{ Partition = partition; Generation = generation; Members = members; FreezeId = freezeId; Save = save; Frozen = frozen; }
	}
	/// <summary>
	/// T-0361: the velocities are read BEFORE the freeze. Vanilla returns zero for a body whose physics is disabled
	/// (MyPhysicsBody.GetLinearVelocity), so a Final captured after the freeze stored every moving member at rest:
	/// a ship in flight at a cluster stop came back standing still, and one falling in gravity started its fall again.
	/// </summary>
	private sealed record DrainFrozenState(MyEntityUpdateEnum NeedsUpdate, bool HadPhysics, bool PhysicsEnabled,
		VRageMath.Vector3 LinearVelocity = default, VRageMath.Vector3 AngularVelocity = default)
	{
		public static DrainFrozenState Of(MyEntity entity) => new(entity.NeedsUpdate, entity.Physics != null, entity.Physics?.Enabled == true,
			entity.Physics?.LinearVelocity ?? VRageMath.Vector3.Zero, entity.Physics?.AngularVelocity ?? VRageMath.Vector3.Zero);
	}

	/// <summary>T-0361: writes the velocity a member had before its freeze into the builder captured after it.</summary>
	internal static void ApplyFrozenVelocity(MyObjectBuilder_EntityBase builder, VRageMath.Vector3 linear, VRageMath.Vector3 angular)
	{
		if (builder is MyObjectBuilder_CubeGrid grid)
		{
			grid.LinearVelocity = linear;
			grid.AngularVelocity = angular;
		}
		else if (builder is MyObjectBuilder_Character character) character.LinearVelocity = linear;
	}
	internal sealed record RecoveryFile(ulong Partition, uint Generation, uint Sequence, string Checksum, long SizeBytes,
		Dictionary<string, string> LocationHolders, Dictionary<string, string> HolderEndpoints, HashSet<string> CorruptHolders,
		string[] Locations, HashSet<long> EntityIds, Dictionary<string, (string Node, string Endpoint)> HolderProxies = null);
	internal sealed record RecoveryClaim(ulong Partition, uint Generation, RecoveryFile[] Files, RecoveryFile[] Fallbacks)
	{
		/// <summary>
		/// Entity ids live on OTHER nodes at claim time. A recovered save may carry an entity whose
		/// newer incarnation rides a different partition id on another node - the per-partition-id
		/// generation check cannot see that, and resurrecting the stale copy wedges every future
		/// co-location handover on the duplicate-incarnation guard (world 4, 2026-08-16: node-5's
		/// lease lapsed, peers recovered its partitions, and entity 73422848341645999 came back in
		/// TWO partitions within a minute). LoadRecovery drops these ids from the recovered set.
		/// </summary>
		public HashSet<long> LiveElsewhere { get; init; }
		/// <summary>#1286: the census LiveElsewhere was cut from (replication thread only), and the state of its check.</summary>
		public LiveCensus Census { get; init; }
		/// <summary>#1286: LiveElsewhere was checked against the registry's ownership of every copy that holds this content.</summary>
		public bool CensusVerified { get; init; }
		/// <summary>#1286: the content's ids (members and seated pilots), set when the load hands the claim back for a check.</summary>
		public HashSet<long> Cover { get; init; }
		/// <summary>#1286: content ids that only stale peer copies hold (not in LiveElsewhere), from the last check.</summary>
		public HashSet<long> StaleOnly { get; init; }
		/// <summary>#1286: fresh censuses taken so far because of StaleOnly (bounded by StaleCopyRechecks).</summary>
		public int CensusRechecks { get; init; }
		/// <summary>T-0237: player bodies the registry's character homes place in another partition since this content
		/// was saved; a standing copy of one here is stale and is not loaded.</summary>
		public HashSet<long> HomedElsewhere { get; init; }
		/// <summary>T-0237: identity -> its home in another partition that holds the body (see <see cref="StaleByIdentity"/>).</summary>
		public Dictionary<long, HomeElsewhere> HomesByIdentity { get; init; }
		/// <summary>Character entity -> home version stamped in the newest covering save that carries it.</summary>
		public Dictionary<long, long> SaveHomeStamps { get; init; }
		/// <summary>Whether any covering save carries stamps (a build with home versions); false = an older save.</summary>
		public bool SaveStamped { get; init; }
		public DateTimeOffset? SaveCreated { get; init; }
		/// <summary>T-0237: cluster-clock capture time of the newest covered save (0 = older registry).</summary>
		public long SaveCaptured { get; init; }
	}
}
