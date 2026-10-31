using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using VRage.Scripting;
using VRage.Library.Utils;
using VRage.Network;
using VRage.Utils;

namespace ClusterPlugin;

public static class ClusterRuntime
{
    private static readonly DiagnosticsFeed ClientDiagnostics = new DiagnosticsFeed();
	private const string ControlPortEnvironmentVariable = "CLUSTER_CONTROL_PORT";
	private const string NodeRoleEnvironmentVariable = "CLUSTER_NODE_ROLE";
	private const string ProjectileDistanceCapEnvironmentVariable = "CLUSTER_PROJECTILE_DISTANCE_CAP";
	private static ClusterControlServer m_control;
	private static PartitionPersistence m_persistence;
	private static PartitionManager m_partitions;
	private static readonly CoalescedRequest m_gatewayPass = new CoalescedRequest(PostGatewayPass, GatewayPassFailed);
	/// <summary>SE1-0084: report-now requests go out at most this often; a member that keeps flipping would otherwise
	/// keep the Gateway pulling every node's report back to back.</summary>
	private const int GatewayPassGapMilliseconds = 250;
	private static long m_lastGatewayPass = -GatewayPassGapMilliseconds;
	private static long m_nextTickError, m_nextGatewayPassError;
	private static WorldAuthorityPersistence m_worldAuthorityPersistence;
	private static Timer m_updateTimer;
	private static bool m_storePaymentReconcileStarted;
	private static int m_updatePending;
	/// <summary>
	/// Frozen top-level entities by the transfer that froze them. Concurrent rather than a locked
	/// <see cref="Dictionary{TKey,TValue}"/> because <see cref="IsFrozen"/> is a read on hot paths and the
	/// lock made it a blocking one: all 27 sampled appearances of IsFrozen in a 30 s node-3 trace were inside
	/// Monitor.Enter_Slowpath, contending with a handover's MarkFrozen/ClearFrozen on another thread.
	/// Each mutator below has an exactly equivalent atomic form, so this loses no check that the lock made.
	/// </summary>
	private static readonly ConcurrentDictionary<long, Guid> m_frozenEntities = new ConcurrentDictionary<long, Guid>();
	private static readonly ClusterClock m_clock = new ClusterClock();
	private static readonly HttpClient m_registryClient = ClusterHttp.Client(TimeSpan.FromSeconds(10));
	internal static readonly Guid ProcessInstance = Guid.NewGuid();
	internal static long RegistryNodeEpoch;

	/// <summary>
	/// T-0340: this node keeps its saves in its host's store, <c>&lt;runRoot&gt;/saves/&lt;host&gt;/</c>. The REGISTRY decides
	/// it and says so in the registration answer; the node only offers (it has a data root, whose parent is the run
	/// root). Set once, by the registration that precedes every save path being computed.
	/// </summary>
	internal static bool HostStore { get; private set; }

	/// <summary>What the registration answer says about the host store. A registry before T-0340 does not name the
	/// field: saves stay in the node's data dir.</summary>
	internal static bool HostStoreFrom(JsonElement registration, string dataRoot) =>
		registration.TryGetProperty("HostStore", out JsonElement hostStore) && hostStore.ValueKind == JsonValueKind.True
		&& !string.IsNullOrWhiteSpace(dataRoot);

	/// <summary>Where this node's save files are: its host's store or <c>&lt;dataRoot&gt;/cluster/saves</c> (HostSaveStore.NodeSaves).</summary>
	internal static string SaveRoot() => SaveRoot(HostStore);

	/// <summary>The saves directory in this node's own data dir: where a store-mode node still finds and serves the
	/// files that were not moved into the store (HostSaveStore.Held).</summary>
	internal static string LegacySaveRoot() => SaveRoot(false);

	private static string SaveRoot(bool store)
	{
		string data = Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT");
		return ClusterProtocol.HostSaveStore.NodeSaves(string.IsNullOrWhiteSpace(data) ? AppContext.BaseDirectory : data, ClusterHttp.NodeHost, store);
	}
	private static bool m_isWorldAuthority;
	private static int m_authorityFenced;
	private static long m_pendingAuthorityGeneration = -1;
	public static bool IsClustered { get; private set; }

	/// <summary>
	/// T-0350: the registry does not answer. Set and cleared ONLY by PartitionPersistence.NoteRegistryReachable, together
	/// with PartitionManager.RegistryHold: any thread that gets no answer sets it, the first answered heartbeat clears
	/// it. While set, nothing on the game thread may make a registry request - each site takes its failure path at once.
	/// Never set on the World Authority (it runs no persistence heartbeat) nor with CLUSTER_REGISTRY_HOLD=0.
	/// </summary>
	public static volatile bool RegistryUnreachable;

	/// <summary>
	/// T-0350: a registry request failed on some thread. Sets the hold when the registry gave no answer
	/// (SaveRetryPolicy.RegistryNoAnswer). Always false, so it can stand in an exception filter:
	/// <c>catch (Exception e) when (ClusterRuntime.NoteRegistryFailure(e)) { throw; }</c>.
	/// </summary>
	internal static bool NoteRegistryFailure(Exception exception)
	{
		m_persistence?.NoteRegistryFailure(exception);
		return false;
	}

	private static T NotingRegistryFailure<T>(Func<T> request)
	{
		try { return request(); }
		catch (Exception exception) when (NoteRegistryFailure(exception)) { throw; }
	}

	// Orchestration.md §4.2: the node's externally visible lifecycle, served by the control server's
	// /cluster/node-state route without touching the game thread. "Failed" is terminal and precedes exit.
	private static volatile string m_nodeState = "Starting";
	private static volatile string m_nodeStateDetail = "";
	public static string NodeState => m_nodeState;
	/// <summary>Reported on every heartbeat so the gateway only targets nodes whose world is loaded (T-0059).</summary>
	public static bool IsServing => m_nodeState == "Serving";
	public static string NodeStateDetail => m_nodeStateDetail;

	/// <summary>This incarnation's replication id generator, once registered (T-0268 prefetch trigger).</summary>
	internal static NetworkIdRangeGenerator NetworkIds { get; private set; }

	// HotSpareNodes.md dormant spares: while this registered node has never owned content the
	// whole main loop runs at dormant.hz instead of 60 Hz (WaitForTargetFrameRate seam).
	// m_awakeUntilUtcTicks lets listener threads pre-wake ahead of the first import so no
	// handover phase ever waits on a throttled frame.
	private static volatile bool m_dormant;
	private static long m_awakeUntilUtcTicks;
	public static bool IsDormant => m_dormant;

	/// <summary>Restore the full frame rate now and hold it for at least this long, callable
	/// from any thread. Dormancy re-engages only after the window if the node is still empty.</summary>
	internal static void Wake(double seconds = 60.0)
	{
		long until = DateTime.UtcNow.AddSeconds(seconds).Ticks;
		long previous;
		while ((previous = Interlocked.Read(ref m_awakeUntilUtcTicks)) < until
				&& Interlocked.CompareExchange(ref m_awakeUntilUtcTicks, until, previous) != previous) { }
		if (m_dormant) SetDormant(false, "woken by the control plane");
	}

	private static void SetDormant(bool dormant, string reason)
	{
		if (m_dormant == dormant) return;
		m_dormant = dormant;
		float hz = 0f;
		if (dormant)
		{
			try { hz = Math.Max(1, ClusterConfig.GetInt("dormant.hz")); }
			catch { hz = 4f; }
		}
		WaitForTargetFrameRateSeams.ClusterTargetFrequency = hz;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] dormant=" + (dormant ? "true" : "false")
			+ (dormant ? " hz=" + hz : "") + " (" + reason + ")");
	}

	private static void UpdateDormancy()
	{
		if (!IsClustered || IsWorldAuthority) return;
		bool eligible = false;
		try
		{
			eligible = ClusterConfig.GetBool("dormant.enabled")
				&& m_persistence != null && m_persistence.DormantEligible()
				&& (Sandbox.Game.Multiplayer.Sync.Clients == null || Sandbox.Game.Multiplayer.Sync.Clients.Count <= 1)
				&& DateTime.UtcNow.Ticks >= Interlocked.Read(ref m_awakeUntilUtcTicks);
		}
		catch { } // dormancy must never take down the update path; ineligible is the safe answer
		if (eligible != m_dormant)
			SetDormant(eligible, eligible ? "registered and empty" : "content, clients or a wake");
	}

	internal static void SetNodeState(string state, string detail = "")
	{
		m_nodeState = state;
		m_nodeStateDetail = detail;
		string line = "[ClusterPlugin] node_state=" + state + (detail.Length == 0 ? "" : " detail=" + SecretRedactor.Redact(detail));
		// Console as well as MyLog: admission failures happen before MyLog exists, and the state
		// transition trail is exactly what P3-REC-01 reads from the captured stdout.
		Console.WriteLine(line);
		MyLog.Default?.WriteLineAndConsole(line);
	}

	private static int m_exitWatchdogArmed;

	/// <summary>Longer than any healthy gap between updates, and the Registry's own SimulationStallDrain.</summary>
	private const long GameThreadStuckMilliseconds = 15_000;

	private static long m_lastGameThreadUpdate;
	private static Thread m_gameThread;
	private static int m_gameThreadId;

	/// <summary>A line written before MyLog existed, for the game log on the first update that has one.</summary>
	private static string m_pendingGameLogLine;

	/// <summary>
	/// Every termination decision (fence, drain exit, authority fence) is delivered by setting a flag
	/// the game thread's Update drains into ExitThreadSafe. A game thread that is stuck - blocked in a
	/// recovery fetch against an unhealthy peer, or already gone - never drains it, and the node lives
	/// on as a zombie: fenced in the registry, squatting on its ports, poisoning cluster-status
	/// (node-3, 2026-08-11). The watchdog makes the decision unconditional: if the process is still
	/// alive after the grace period, it is taken down hard. Ownership was already discarded or drained
	/// by the caller, so there is nothing left worth a clean shutdown.
	/// </summary>
	internal static void ArmExitWatchdog(string reason, int graceSeconds = 60)
	{
		if (Interlocked.Exchange(ref m_exitWatchdogArmed, 1) != 0) return;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] exit watchdog armed (" + graceSeconds + "s): " + SecretRedactor.Redact(reason));
		new Thread(() =>
		{
			Thread.Sleep(TimeSpan.FromSeconds(graceSeconds));
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] exit watchdog fired; terminating process");
			LogStuckGameThread();
			MyLog.Default?.Flush();
			// Environment.Exit runs the process exit handlers, and the dedicated server's shutdown
			// handler performs a FULL graceful session unload - on a fenced node that took 20
			// minutes (node-4, w14, 2026-08-18 09:33-09:53), which is 20 minutes of a dead-to-the-
			// registry node still simulating its partitions while their recoveries wait. The
			// watchdog exists to bound exactly that, so it has to bound its own exit path too:
			// if Exit has not killed the process shortly, bypass the handlers outright.
			new Thread(() =>
			{
				Thread.Sleep(TimeSpan.FromSeconds(30));
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] exit watchdog: Environment.Exit is stuck in shutdown handlers; failing fast");
				MyLog.Default?.Flush();
				Environment.FailFast("cluster exit watchdog escalation");
			})
			{ IsBackground = true, Name = "ClusterExitWatchdogEscalation" }.Start();
			Environment.Exit(3);
		})
		{ IsBackground = true, Name = "ClusterExitWatchdog" }.Start();
	}

	/// <summary>SE-883: a game thread that is stuck at the exit is why the node never got to leave by itself. One that ended has nothing to show.</summary>
	private static void LogStuckGameThread()
	{
		Thread gameThread = Volatile.Read(ref m_gameThread);
		long idle = Environment.TickCount64 - Interlocked.Read(ref m_lastGameThreadUpdate);
		if (!OperatingSystem.IsLinux() || gameThread is { IsAlive: false } || idle < GameThreadStuckMilliseconds) return;
		GameThreadStacks.Log(gameThread == null ? "the game thread never reached an update" : "no game thread update for " + idle / 1000 + " s", m_gameThreadId);
	}

	[DllImport("libc", EntryPoint = "_exit")]
	private static extern void LibcExit(int status);

	[DllImport("libc", EntryPoint = "gettid")]
	private static extern int LibcGetTid();

	/// <summary>
	/// Ends the process with <paramref name="code"/> once the plugin has decided to leave (drain
	/// complete, World Authority empty or fenced). T-0162: on Linux the LinuxCompat hub plugin
	/// replaces MySandboxGame.ExitThreadSafe with Process.Kill, so every such exit was a SIGKILL
	/// (exit 137) that exit-code accounting could not tell from an OOM or crash kill. Nothing after
	/// that decision needs the game's unload - the caller has already handed its content over - so
	/// flush the logs and leave with the code. Elsewhere the vanilla path still runs.
	/// </summary>
	internal static void ExitProcess(int code, string reason)
	{
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] process exit " + code + ": " + SecretRedactor.Redact(reason));
		NodeProcessIdentity.Exiting(code);
		MySandboxGame.ConfigDedicated.RestartSave = false;
		if (OperatingSystem.IsLinux())
		{
			try
			{
				MyLog.Default?.Flush();
				Console.Out.Flush();
				Console.Error.Flush();
				LibcExit(code);
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] process exit via libc failed: " + exception.Message);
			}
		}
		MySandboxGame.ExitThreadSafe();
	}

	private static int m_gameThreadWatcherStarted;

	/// <summary>
	/// The exit watchdog only covers PLUGIN termination decisions. An unhandled exception that
	/// unwinds the game loop itself (node-3's duplicate laser-broadcaster key, 2026-08-11) arms
	/// nothing: the loop is gone, vanilla's foreground threads keep CoreCLR alive, and the node
	/// zombifies with its background threads still heartbeating and claiming recoveries into a dead
	/// game thread. Called from Update, so the captured thread is by definition the game thread; the
	/// watcher joins it and arms the watchdog the moment it ends for any reason. A clean shutdown
	/// exits well inside the grace, so the backstop only ever fires on a wedged process.
	/// </summary>
	private static void WatchGameThreadExit()
	{
		if (Interlocked.Exchange(ref m_gameThreadWatcherStarted, 1) != 0) return;
		Thread gameThread = Thread.CurrentThread;
		if (OperatingSystem.IsLinux()) m_gameThreadId = LibcGetTid();
		Volatile.Write(ref m_gameThread, gameThread);
		new Thread(() =>
		{
			gameThread.Join();
			ArmExitWatchdog("game thread exited", graceSeconds: 30);
		})
		{ IsBackground = true, Name = "ClusterGameThreadWatcher" }.Start();
	}

	/// <summary>
	/// Orchestration.md §4.2: the registry ANSWERED, with a no. Any 4xx-class answer is a definitive
	/// rejection of this incarnation and must be treated as terminal - retry-with-backoff is reserved
	/// for unreachability (no answer at all: connection refused, timeout, 5xx).
	/// </summary>
	internal static bool IsDefinitiveRejection(Exception exception)
		=> exception is System.Net.Http.HttpRequestException { StatusCode: { } status } && (int)status >= 400 && (int)status < 500;

	/// <summary>
	/// Terminal admission failure before the game runs: report Failed through the (already listening)
	/// control server, hold the state visible long enough for a status probe to see it, then exit.
	/// An unregistered node owns nothing, so there is nothing to save.
	/// </summary>
	private static void FailNode(string reason)
	{
		SetNodeState("Failed", reason);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] node admission failed terminally: " + SecretRedactor.Redact(reason));
		Thread.Sleep(TimeSpan.FromSeconds(10));
		Environment.Exit(1);
	}

	public static bool IsWorldAuthority => m_isWorldAuthority && Volatile.Read(ref m_authorityFenced) == 0;
	public static bool IsAuthorityFenced => Volatile.Read(ref m_authorityFenced) != 0;
	public static long AuthorityGeneration { get; private set; }
	public static bool HasAuthorityClock => m_clock.IsInitialized;
	/// <summary>The authority clock's last step, once - see ClusterClock.TakeStep.</summary>
	public static double? TakeClockStep() => m_clock.TakeStep();

	public static double LocalMilliseconds => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
	/// <summary>
	/// Cluster time for durable catalog records (saves, topology). Before the first gateway clock
	/// sample, ClusterMilliseconds falls back to the host uptime clock - stamping that into the
	/// catalog places the record days into the cluster's future, and lineage resolution
	/// (ClusterTime-before-split/merge cuts) then hides the partition's real anchor save. A zero
	/// stamp is always in the past, which is the safe direction for every causality comparison.
	/// </summary>
	public static long CatalogClusterMilliseconds() => m_clock.IsInitialized ? checked((long)ClusterMilliseconds()) : 0;
	public static double ClusterMilliseconds(double vanillaMilliseconds = double.NaN)
	{
		double local = LocalMilliseconds;
		return m_clock.Now(local, double.IsNaN(vanillaMilliseconds) ? local : vanillaMilliseconds);
	}
	/// <summary>
	/// The replication clock: game time minus the accumulated admin time-of-day offset
	/// (WorldAuthority.md §8.2). The offset travels on the ordered global-state channel instead,
	/// so this value stays smooth across admin sets and the node clocks that chase it never step
	/// (Partitioning.md §5's "slewed, never stepped").
	/// </summary>
	public static double AuthorityClockMilliseconds =>
		MySession.Static?.ElapsedGameTime.TotalMilliseconds - TimeOfDayService.OffsetMilliseconds ?? ClusterMilliseconds();
	public static bool AcceptClockSample(double sendMilliseconds, double receiveMilliseconds, double gatewayMilliseconds)
		=> m_clock.Sample(sendMilliseconds, receiveMilliseconds, gatewayMilliseconds);

	/// <summary>World Authority: bring the next global save forward (no-op elsewhere).</summary>
	internal static void RequestGlobalSave() => m_worldAuthorityPersistence?.SaveSoon();

	/// <summary>World Authority whose global saves are registered: a durable command waits for one (#1270).</summary>
	internal static bool GlobalSavesPersist => m_worldAuthorityPersistence?.Persists == true;

	public static bool AcceptAuthorityMessage(long generation) => !IsAuthorityFenced && generation > 0 && generation == AuthorityGeneration;
	internal static void AdvanceAuthorityClock(double milliseconds)
	{
		if (!IsWorldAuthority || !double.IsFinite(milliseconds) || milliseconds < 0 || MySandboxGame.Static == null) return;
		MySandboxGame.Static.Invoke(() =>
		{
			// The gateway relays the offset-free replication clock, so the comparison and the
			// recovered value both re-attach the sun offset.
			long offset = TimeOfDayService.OffsetMilliseconds;
			if (MySession.Static != null && MySession.Static.ElapsedGameTime.TotalMilliseconds - offset < milliseconds)
				MySession.Static.ElapsedGameTime = TimeSpan.FromMilliseconds(milliseconds + offset);
		}, "ClusterPlugin World Authority clock recovery");
	}

	public static bool IsFrozen(long topLevelEntityId) => m_frozenEntities.ContainsKey(topLevelEntityId);

	internal static void MarkFrozen(long entityId, Guid transferId)
	{
		// GetOrAdd is the locked body exactly: absent -> ours; present and ours -> the assignment was a no-op;
		// present and another transfer's -> throw. Atomic, where the read-then-write needed the lock to be.
		if (m_frozenEntities.GetOrAdd(entityId, transferId) != transferId)
		{
			throw new InvalidOperationException("Entity " + entityId + " is frozen by another transfer");
		}
	}

	internal static void ClearFrozen(long entityId, Guid transferId)
		// Remove only if the value still matches - the compare-and-remove overload, which is what the
		// TryGetValue-then-Remove pair inside the lock was.
		=> ((ICollection<KeyValuePair<long, Guid>>)m_frozenEntities)
			.Remove(new KeyValuePair<long, Guid>(entityId, transferId));

	internal static void ClearFrozen(long entityId) => m_frozenEntities.TryRemove(entityId, out _);

    /// <summary>T-0256 plugin views: the partition holding a top-level entity here, and whether this node owns it. Game thread.</summary>
    internal static (ulong Partition, bool Owned) PluginEntityPartition(long topLevelEntityId)
    {
        ulong partition = m_persistence?.Holder(topLevelEntityId) ?? 0;
        return (partition, partition != 0 && m_persistence.OwnsPartition(partition));
    }

    internal static bool PartitionClaimPending(ulong partition) => m_persistence?.ClaimPending(partition) ?? false;

    internal static bool PartitionOfflining(ulong partition) => m_persistence?.Offlining(partition) ?? false;

    internal static Dictionary<ulong, long> PluginOwnershipSnapshot() =>
        m_persistence?.PluginOwnershipSnapshot() ?? new Dictionary<ulong, long>();

    internal static bool PluginFenceCurrent(PluginSdk.Clustering.OwnerFence fence)
    {
        if (fence.Node != Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") || fence.Incarnation != RegistryNodeEpoch || !IsServing || IsAuthorityFenced) return false;
        return fence.Target.Kind switch {
            PluginSdk.Clustering.PluginTargetKind.Node => true,
            PluginSdk.Clustering.PluginTargetKind.WorldAuthority => IsWorldAuthority && fence.Generation == AuthorityGeneration,
            PluginSdk.Clustering.PluginTargetKind.Partition => m_persistence != null && m_persistence.OwnedGeneration(fence.Target.Partition) == fence.Generation,
            _ => false,
        };
    }

	public static void StartFromEnvironment()
	{
		string value = Environment.GetEnvironmentVariable(ControlPortEnvironmentVariable);
		if (!int.TryParse(value, out int port) || port <= 0) return;

		IsClustered = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY"));
		m_isWorldAuthority = ClusterNodeRole.IsWorldAuthority(Environment.GetEnvironmentVariable(NodeRoleEnvironmentVariable));
		GlobalStateChannel.Start(Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty);
		ChatAuthorityPatch.Install();
		GpsAuthorityPatch.Install();
		PositionRegistryService.Install();
		SpawnDirectorService.Install();
		GridAddTrace.Install();
		AntennaGraphService.Install();
		OfflinePartitionService.Install();
		if (IsClustered) NpcCensusService.Install();   // #1354: the census slots for mod-mes; unset on a plain server
		RemotePullService.Install();
		OfflineRateCache.Install();
		MyGlobalEventsPatch.Install();
		MyPirateAntennasPatch.Install();
		MySessionComponentContainerDropSystemPatch.Install();
		MySessionComponentEconomyPatch.Install();
		MySessionComponentContractSystemPatch.Install();
		MyGlobalEncountersGeneratorPatch.Install();
		MyPlanetaryEncountersGeneratorPatch.Install();
		MyNeutralShipSpawnerPatch.Install();
		GridTransferService.Install();
		MySpaceFaunaComponentPatch.Install();
		MyPlayerCollectionPatch.Install();
		MyControlAssignmentTracePatch.Install();
		MyCharacterPatch.Install();
		// Imported blocks routinely name a builder identity this node has never seen; vanilla would
		// zero their BuiltBy at AddAuthorship, losing the authorship and failing the handover
		// round-trip validation (the re-export omits the now-default element: "14 != 13 child
		// elements" aborts, 2026-08-14 partition 406).
		MySlimBlockSeams.ClusterPreserveUnknownAuthorship = true;
		// The same for Owner (MyCubeBlock.Init zeroes an owner it has no identity for): a 58 s old
		// node lost a projector's owner to a 7 s replication gap, w22 2026-08-21 17:41:33.
		MyCubeBlockSeams.ClusterPreserveUnknownOwner = true;
		MyBankingSystemPatch.Install();
		MyStoreBlockPatch.Install();
		StorePaymentJournal.Install();
		MyContractPatch.Install();
		MyFactionCollectionPatch.Install();
		ReplicationServerPatch.ClusterAuthorityGeneration = () => AuthorityGeneration;
		WorldAuthorityCheckpointPatch.Install();
		AuthorizeRegistryClient(m_registryClient);
		SecretRedactor.ConfigureFromEnvironment();
		if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")))
		{
			ConfigureProjectileDistanceCap();
			ProgrammableBlockMigrationPatch.Install();
			ScriptCompilerPatch.Install();
			MySession.OnUnloading += ScriptCompilerPatch.Clear;
		}
		// Coordinate-system ids are per-process in vanilla and every node loads the same base world,
		// so without this every node issues the same first id and a handover crashes the client (T-0046).
		CoordinateSystemIdPatch.IsClustered = () => IsClustered;
		ProgrammableBlockFreezePatch.IsFrozen = IsFrozen;
		FrozenRootIncomingRpcPatch.IsFrozen = IsFrozen;
		RemoteControlGatePatch.IsFrozen = IsFrozen;
		RemoteControlGatePatch.Install();
		FrozenUpdatePatch.IsFrozen = IsFrozen;
		FrozenUpdatePatch.Install();
		ReplicationTypeTablePatch.Enabled = true;
		MyMultiplayerServerBaseSeams.ClusterAdjustUpdateTime = vanilla
			=> MyTimeSpan.FromMilliseconds(ClusterMilliseconds(vanilla.Milliseconds));
		MySessionSeams.ClusterAdjustElapsedGameTime = SessionClockPatch.Postfix;
		// The control server binds BEFORE registration (Orchestration §4.2): it must stay responsive
		// through any registration failure, so a Failed node is diagnosable from one /cluster/node-state
		// probe instead of from a wedged half-dead process. Routes needing the partition services answer
		// 503 until Attach below.
		m_control = new ClusterControlServer(null, null, null);
		m_control.Start(port);
		ConfigureNetworkIds();
        if (IsClustered) PluginServices.Start();
		if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY"))) VoxelJournalService.Start();
		PartitionManager partitions = new PartitionManager(() => EntityIdAllocator.CurrentRangeIndex);
		m_persistence = new PartitionPersistence(partitions);
		m_partitions = partitions;
		ConcealableContentService.Persistence = m_persistence;
		RespawnRegistryService.Install(partitions, m_persistence);
		IgcEndpointRegistryService.Install();
		IgcDeliveryService.Install();
		ClusterModChannelService.Install();
		PcuLedgerService.Install();
		RespawnCooldownService.Install();
		ShipStorageService.Install();
		AdminSafeZones.Install();
		PromotionService.Install();
		OnlinePlayersService.Install();
		ClusterPresenceAnnouncer.Install();
		PlayerStateService.Install();
		CharacterRegistryService.Install();
		PlayerProfileService.Install();
		TimeOfDayService.Install();
		WeatherAuthorityService.Install();
		ConcealableContentService.Install();
		AsteroidSpawnService.Install();
		// The premade cluster world is the single source of the cluster-wide game rules; the per-node
		// auto-generated dedicated config must not overwrite them at load.
		MySandboxGameSeams.ClusterKeepPremadeSessionSettings = () => IsClustered;
		MySandboxGameSeams.ClusterSaveFolder = () => Environment.GetEnvironmentVariable("CLUSTER_SAVE_FOLDER");
		// T-0345: nothing reads the vanilla world save back. Every role starts with -ignorelastsession from the
		// premade world; partitions come from the cluster saves, the World Authority from its catalogued global
		// save, voxels from the journal. The autosave still rewrote Saves/<world> every 5 min on every node
		// (46-68 MB plus a Backup copy: ~330 kB/s per serving node, the top disk writer on r85).
		// CLUSTER_VANILLA_AUTOSAVE=1 keeps it.
		if (IsClustered && Environment.GetEnvironmentVariable("CLUSTER_VANILLA_AUTOSAVE") != "1")
		{
			Sandbox.Engine.Utils.MyFakes.ENABLE_AUTOSAVE = false;
			Console.WriteLine("[ClusterPlugin] vanilla world autosave disabled (T-0345)");
		}
		// Opt-in for the test bench: the offline everyone-is-Owner short-circuit hides every
		// AdminOnly rejection, so the P3-SUN-01 lane runs with real promotion levels instead.
		bool strictPromotion = Environment.GetEnvironmentVariable("CLUSTER_STRICT_PROMOTION") == "1";
		MySessionSeams.ClusterStrictPromotion = () => strictPromotion && IsClustered;
		if (IsWorldAuthority) m_worldAuthorityPersistence = new WorldAuthorityPersistence();
		m_updateTimer = new Timer(_ =>
		{
			if (MySandboxGame.Static != null && Interlocked.Exchange(ref m_updatePending, 1) == 0)
			{
				MySandboxGame.Static.Invoke(() =>
				{
					try { Update(); }
					finally { Volatile.Write(ref m_updatePending, 0); }
				}, "ClusterPlugin persistence update");
			}
		}, null, 0, 250);
		ClusterControlServer.Attach(partitions, m_persistence, m_worldAuthorityPersistence);
		// The test utility plugin's session-init hook (null in every release deployment): the first
		// point where MySession.Static and MyAPIUtilities.Static exist, which is what a mod-side
		// harness needs and plugin Init cannot offer.
		ClusterSeams.RaiseServicesInstalled();
	}

	/// <summary>
	/// Applies the gateway's handover-config document to this node. Keys this build does not know
	/// are skipped and named, not fatal: a gateway may run ahead of a node. Returns false if the
	/// document could not be read or was rejected, so the caller does not latch a revision it never
	/// applied — latching it regardless turned one unreadable document into a permanent, silent
	/// divergence (w22, 2026-08-21).
	/// </summary>
	/// <summary>Console as well as MyLog: the registration document is applied before MyLog exists.</summary>
	private static void LogNodeLine(string line)
	{
		Console.WriteLine(line);
		MyLog.Default?.WriteLineAndConsole(line);
	}

	internal static bool ApplyGatewayConfig(JsonElement document)
	{
		if (!ClusterConfig.TryParseDocument(document, out Dictionary<string, string> update, out List<string> unknown, out string error))
		{
			LogNodeLine("[ClusterPlugin] ERROR: gateway config " + error);
			return false;
		}
		if (unknown.Count != 0) LogNodeLine("[ClusterPlugin] gateway config carries unknown keys, skipped: " + string.Join(", ", unknown));
		try { ClusterConfig.Apply(update); return true; }
		catch (ArgumentException exception)
		{
			LogNodeLine("[ClusterPlugin] ERROR: gateway config rejected: " + exception.Message);
			return false;
		}
	}

	internal static void AuthorizeRegistryClient(HttpClient client)
	{
		string token = Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_CURRENT")
			?? Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN");
		if (!string.IsNullOrWhiteSpace(token))
			client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
	}

	private static void ConfigureProjectileDistanceCap()
	{
		string value = Environment.GetEnvironmentVariable(ProjectileDistanceCapEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(value))
		{
			if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")))
				throw new ArgumentException(ProjectileDistanceCapEnvironmentVariable + " is required for a clustered node");
			return;
		}
		if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float cap) || !float.IsFinite(cap) || cap <= 0f)
			throw new ArgumentException(ProjectileDistanceCapEnvironmentVariable + " must be a positive number");
		ProjectileRangePatch.Configure(cap);
	}

	private static void ConfigureNetworkIds()
	{
		string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
		string endpoint = Environment.GetEnvironmentVariable("CLUSTER_NODE_ENDPOINT");
		if (string.IsNullOrWhiteSpace(gateway) || string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(endpoint))
		{
			if (m_isWorldAuthority) throw new InvalidOperationException("World Authority registry admission environment is incomplete");
			return;
		}
		// Orchestration §4.2: retry-with-backoff is for UNREACHABILITY only, bounded by nodeReadyTimeout;
		// a definitive rejection is terminal on the first answer.
		TimeSpan readyTimeout = TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_NODE_READY_TIMEOUT"),
			NumberStyles.Float, CultureInfo.InvariantCulture, out double readySeconds) && readySeconds > 0 ? readySeconds : 300);
		DateTime registrationDeadline = DateTime.UtcNow + readyTimeout;
		SetNodeState("Registering");
		while (true)
		{
			try
			{
				using JsonDocument registration = RegisterNode(m_registryClient, gateway, node, endpoint);
				RegistryNodeEpoch = registration.RootElement.GetProperty("Epoch").GetInt64();
				HostStore = HostStoreFrom(registration.RootElement, Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT"));
				string storeLine = HostStore
					? "[ClusterPlugin] host save store ON (the registry said so): saves go to " + SaveRoot()
					: "[ClusterPlugin] host save store OFF (the registry did not grant it, or this node has no data root): saves stay in the node data dir " + SaveRoot();
				// Registration runs before MyLog exists, so MyLog alone dropped the line (T-0340 switch-on bench 2026-10-04:
				// 0 ON/OFF lines on every node and wa-1). Console now, the game log on the first update that has one.
				Console.WriteLine(storeLine);
				if (MyLog.Default != null) MyLog.Default.WriteLine(storeLine);
				else m_pendingGameLogLine = storeLine;
				int role = registration.RootElement.GetProperty("Role").ValueKind == JsonValueKind.String
					? (registration.RootElement.GetProperty("Role").GetString() == "WA" ? 1 : 0)
					: registration.RootElement.GetProperty("Role").GetInt32();
				if ((role == 1) != IsWorldAuthority) throw new InvalidOperationException("Registry returned a different node role");
				AuthorityGeneration = registration.RootElement.GetProperty("AuthorityGeneration").GetInt64();
				if (registration.RootElement.TryGetProperty("HandoverConfig", out JsonElement handoverConfig)) ApplyGatewayConfig(handoverConfig);
				if (IsWorldAuthority && AuthorityGeneration <= 0) throw new InvalidOperationException("Registry did not issue a World Authority generation");
				if (!m_isWorldAuthority) GlobalStateChannel.AuthorityChanged(AuthorityGeneration);
				// EntityIds: the first range is drawn synchronously before any entity can be created;
				// later ranges are fetched by the allocator itself (Docs/Gateway/EntityIdRanges.md).
				(long rangeStart, long rangeEnd) = AllocateEntityIdRange(m_registryClient, gateway, node, RegistryNodeEpoch, true);
				EntityIdAllocator.Configure(rangeStart, rangeEnd,
					() => NotingRegistryFailure(() => AllocateEntityIdRange(m_registryClient, gateway, node, RegistryNodeEpoch, false)));
				NetworkIdBlock initial = AllocateNetworkIdBlock(m_registryClient, gateway, node, RegistryNodeEpoch, true);
				NetworkIdRangeGenerator generator = new NetworkIdRangeGenerator(initial,
					() => NotingRegistryFailure(() => AllocateNetworkIdBlock(m_registryClient, gateway, node, RegistryNodeEpoch, false)),
					() => RegistryUnreachable);
				ReplicationNetworkIdGeneratorPatch.Factory = () => generator;
				NetworkIds = generator;
				new Thread(() => RegistrationHeartbeat(gateway, node, RegistryNodeEpoch)) { IsBackground = true, Name = "ClusterRegistrationHeartbeat" }.Start();
				SetNodeState("Registered");
				NodeProcessIdentity.Registered();
				return;
			}
			catch (Exception exception)
			{
				// Log the rejection verbatim (the Post helpers embed the registry's answer body), then
				// stop: a rejected incarnation is never admitted, and blind 409 retries are how the
				// Phase 3 stale-registry wedge presented (Orchestration §4.2).
				if (IsDefinitiveRejection(exception))
					FailNode("registry rejected this node's registration: " + exception.Message);
				// A local admission contract violation (role mismatch, missing WA generation) is just as
				// terminal: no amount of retrying changes this process's configuration.
				if (exception is InvalidOperationException)
					FailNode("cluster admission failed: " + exception.Message);
				if (DateTime.UtcNow >= registrationDeadline)
					FailNode("registry unreachable past nodeReadyTimeout (" + readyTimeout.TotalSeconds + "s): " + exception.Message);
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] network_id_registry unavailable retry=true error=" + SecretRedactor.Redact(exception.Message));
				Thread.Sleep(1000);
			}
		}
	}

	private static void RegistrationHeartbeat(string gateway, string node, long epoch)
	{
		while (true)
		{
			try
			{
				double clockSend = LocalMilliseconds;
				using StringContent content = new StringContent(JsonSerializer.Serialize(new
				{
					// Liveness: this heartbeat is no ownership report (the persistence heartbeat is); the audit keeps the last one.
					Node = node, Epoch = epoch, Ownerships = Array.Empty<object>(), Liveness = true, LocalSendMilliseconds = clockSend,
					Serving = IsServing, DeploymentRevision = NodeProcessIdentity.Revision,
					ConfigurationVerified = NodeProcessIdentity.Verified, ConfigurationFailure = NodeProcessIdentity.Failure,
					// The offset-free replication clock, NEVER raw ElapsedGameTime: the response
					// handler re-attaches the sun offset (AdvanceAuthorityClock), so sending it
					// here too compounds the offset by one heartbeat period per second - a runaway
					// the whole cluster then slews after (WorldAuthority.md §8.2).
					AuthorityMilliseconds = IsWorldAuthority && MySession.Static?.Ready == true
						? AuthorityClockMilliseconds : (double?)null
				}), System.Text.Encoding.UTF8, "application/json");
				using HttpResponseMessage response = m_registryClient.PostAsync(gateway + "/registry/nodes/heartbeat", content).GetAwaiter().GetResult();
				response.EnsureSuccessStatusCode();
				double clockReceive = LocalMilliseconds;
				using JsonDocument heartbeat = JsonDocument.Parse(response.Content.ReadAsStream());
				long worldGeneration = heartbeat.RootElement.GetProperty("WorldAuthorityGeneration").GetInt64();
				SetAuthorityGeneration(worldGeneration);
				if (m_isWorldAuthority && heartbeat.RootElement.GetProperty("AuthorityGeneration").GetInt64() != AuthorityGeneration)
				{
					FenceWorldAuthority();
					return;
				}
				JsonElement state = heartbeat.RootElement.GetProperty("State");
				bool closed = state.ValueKind == JsonValueKind.String ? state.GetString() == "Closed" : state.GetInt32() == 1;
				if (IsWorldAuthority && closed) m_worldAuthorityPersistence?.RequestShutdown();
				if (heartbeat.RootElement.TryGetProperty("AuthorityMilliseconds", out JsonElement authorityClock)
					&& authorityClock.ValueKind == JsonValueKind.Number && AcceptAuthorityMessage(worldGeneration))
				{
					if (IsWorldAuthority) AdvanceAuthorityClock(authorityClock.GetDouble());
					else AcceptClockSample(clockSend, clockReceive, authorityClock.GetDouble());
				}
			}
			catch (Exception exception)
			{
				// Orchestration §4.2/§4.5: a heartbeat ANSWERED with a definitive refusal means this
				// incarnation is fenced. A running node discards its ownership without saving and exits
				// (the persistence half owns that path); before the game runs there is nothing to
				// discard and the node fails fast.
				if (IsDefinitiveRejection(exception))
				{
					Console.WriteLine("[ClusterPlugin] registration heartbeat rejected terminally: " + SecretRedactor.Redact(exception.Message));
					if (m_persistence != null) m_persistence.FenceForRejection("registration heartbeat rejected: " + exception.Message);
					else FailNode("registration heartbeat rejected: " + exception.Message);
					return;
				}
				Console.WriteLine("[ClusterPlugin] network_id_registration heartbeat_failed=true error=" + SecretRedactor.Redact(exception.Message));
			}
			Thread.Sleep(1000);
		}
	}

	private static NetworkIdBlock AllocateNetworkIdBlock(HttpClient client, string gateway, string node, long epoch, bool initial)
	{
		using JsonDocument block = Post(client, gateway + "/registry/network-id-blocks?node=" + Uri.EscapeDataString(node)
			+ "&epoch=" + epoch.ToString(CultureInfo.InvariantCulture));
		NetworkIdBlock result = new NetworkIdBlock(block.RootElement.GetProperty("Start").GetUInt32(), block.RootElement.GetProperty("End").GetUInt32());
		Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] network_id_block allocated node={0} epoch={1} start={2} end={3} initial={4}", node, epoch, result.Start, result.End, initial.ToString().ToLowerInvariant()));
		return result;
	}

	private static (long Start, long End) AllocateEntityIdRange(HttpClient client, string gateway, string node, long epoch, bool initial)
	{
		using JsonDocument range = Post(client, gateway + "/registry/entity-id-ranges?node=" + Uri.EscapeDataString(node)
			+ "&epoch=" + epoch.ToString(CultureInfo.InvariantCulture));
		uint index = range.RootElement.GetProperty("Index").GetUInt32();
		long start = range.RootElement.GetProperty("Start").GetInt64();
		long end = range.RootElement.GetProperty("End").GetInt64();
		Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] entity_id_range allocated node={0} epoch={1} index={2} start={3} end={4} initial={5}", node, epoch, index, start, end, initial.ToString().ToLowerInvariant()));
		return (start, end);
	}

	private static JsonDocument Post(HttpClient client, string url)
	{
		using HttpResponseMessage response = client.PostAsync(url, null).GetAwaiter().GetResult();
		return ReadRegistryResponse(response);
	}

	/// <summary>
	/// Success parses; failure throws with the registry's answer body embedded, so a terminal
	/// rejection is logged VERBATIM (Orchestration §4.2) instead of as a bare status line. The
	/// status code is preserved for the Conflict filters and the definitive-rejection test.
	/// </summary>
	internal static JsonDocument ReadRegistryResponse(HttpResponseMessage response)
	{
		if (!response.IsSuccessStatusCode)
		{
			string body;
			try { body = new StreamReader(response.Content.ReadAsStream()).ReadToEnd(); }
			catch { body = ""; }
			if (body.Length > 500) body = body.Substring(0, 500);
			throw new HttpRequestException(
				"registry answered " + (int)response.StatusCode + " " + response.StatusCode + ": " + body.Trim(),
				null, response.StatusCode);
		}
		return JsonDocument.Parse(response.Content.ReadAsStream());
	}

	internal static JsonDocument RegisterNode(HttpClient client, string gateway, string node, string endpoint)
	{
		string world = Environment.GetEnvironmentVariable("CLUSTER_WORLD");
		string token = Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN");
		string clusterId = Environment.GetEnvironmentVariable("CLUSTER_ID");
		string worldId = Environment.GetEnvironmentVariable("CLUSTER_WORLD_ID");
		string configHash = Environment.GetEnvironmentVariable("CLUSTER_CONFIG_HASH");
		if (new[] { world, token, clusterId, worldId, configHash }.Any(string.IsNullOrWhiteSpace))
			throw new InvalidOperationException("Cluster admission environment is incomplete");
		string settings = Path.Combine(Path.GetFullPath(world), "Sandbox.sbc");
		double syncDistance = double.Parse(XDocument.Load(settings).Descendants().Single(x => x.Name.LocalName == "SyncDistance").Value, CultureInfo.InvariantCulture);
		if (ProjectileRangePatch.Cap > syncDistance)
			throw new InvalidOperationException(ProjectileDistanceCapEnvironmentVariable + " cannot exceed the world SyncDistance");
		var request = new
		{
			Node = node, Endpoint = endpoint, Instance = ProcessInstance, Token = token, ClusterId = clusterId, WorldId = worldId,
			ClusterConfigHash = configHash, BinaryVersion = MySandboxGame.BuildVersion.ToString(),
			ReplicationTypeTableHash = ClusterPluginHost.ReplicationTableHash(),
			WorldSettingsHash = ClusterProtocol.WorldSettingsHash.Of(settings), SyncDistance = syncDistance,
			Role = IsWorldAuthority ? 1 : 0,
			// T-0320: node control lists every node as "node-9 @host pid 12345"; the launcher sets CLUSTER_NODE_HOST to
			// the host name of its hosts file, the machine name is the fallback.
			Host = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_NODE_HOST"))
				? Environment.MachineName : Environment.GetEnvironmentVariable("CLUSTER_NODE_HOST"),
			Pid = System.Diagnostics.Process.GetCurrentProcess().Id,
			// T-0340: the node can keep its saves in its host's store (it has a data root); the registry answers whether it does.
			HostStore = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT")),
			// T-0321: the ADVERTISED endpoint (CLUSTER_NODE_CONTROL_ENDPOINT), the one peers on other hosts can dial;
			// without it the bind, as before.
			ControlEndpoint = AdvertisedControlEndpoint()
		};
		using StringContent content = new StringContent(JsonSerializer.Serialize(request), System.Text.Encoding.UTF8, "application/json");
		using HttpResponseMessage response = client.PostAsync(gateway + "/registry/nodes", content).GetAwaiter().GetResult();
		return ReadRegistryResponse(response);
	}

	/// <summary>T-0321: the control endpoint this node registers and hands out as a save holder.</summary>
	internal static string AdvertisedControlEndpoint() => ClusterProtocol.ClusterNet.AdvertisedControlEndpoint(
		Environment.GetEnvironmentVariable("CLUSTER_NODE_CONTROL_ENDPOINT"), Environment.GetEnvironmentVariable("CLUSTER_CONTROL_BIND"),
		int.Parse(Environment.GetEnvironmentVariable(ControlPortEnvironmentVariable), CultureInfo.InvariantCulture));

	private static int m_credentialFenced;

	/// <summary>
	/// T-0321 stale-node rule: the REGISTRY answered this node with an explicit credential rejection (credential_rejected,
	/// node_id_retired, stale_incarnation - see ClusterNet.RejectionCode), so this process is not part of the cluster
	/// the registry runs: a leftover of another start, or a retired incarnation. Log the incident once and leave through
	/// the epoch-fence path - ownership discarded, nothing saved or released, exit watchdog armed. Timeouts, connection
	/// errors and 5xx never get here.
	/// </summary>
	internal static void RegistryCredentialRejected(Uri uri, string code, string token)
	{
		if (Interlocked.Exchange(ref m_credentialFenced, 1) != 0) return;
		string endpoint;
		try { endpoint = AdvertisedControlEndpoint(); } catch (Exception) { endpoint = "?"; }
		string line = string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] credential_rejected by registry: node={0} epoch={1} host={2} pid={3} endpoint={4} route={5} code={6} token_fp={7}; exiting without saving",
			Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"), Interlocked.Read(ref RegistryNodeEpoch), ClusterHttp.NodeHost, Environment.ProcessId,
			endpoint, uri.AbsolutePath, code, ClusterProtocol.ClusterNet.Fingerprint(token));
		Console.WriteLine(line);
		MyLog.Default?.WriteLineAndConsole(line);
		string reason = "registry credential rejection (" + code + " on " + uri.AbsolutePath + ")";
		if (Interlocked.Read(ref RegistryNodeEpoch) <= 0)
		{
			// Still registering: the registration loop treats the same 4xx as definitive and fails the node.
			return;
		}
		if (m_isWorldAuthority)
		{
			Interlocked.Exchange(ref m_authorityFenced, 1);
			ArmExitWatchdog(reason);
			ExitProcess(3, reason);
		}
		else if (m_persistence != null) m_persistence.FenceForRejection(reason);
		else new Thread(() => FailNode(reason)) { IsBackground = true, Name = "ClusterCredentialFence" }.Start();
	}

	private static readonly ConcurrentDictionary<string, long> m_peerRejectionReported = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// T-0321: a PEER rejected this node's credential. The caller does not exit - the peer may be the stale one. The
	/// registry is told (at most once per peer endpoint per 10 s) and fences whichever incarnation is stale; if that is
	/// this node, its answer is itself a registry rejection and <see cref="RegistryCredentialRejected"/> takes over.
	/// </summary>
	internal static void PeerCredentialRejected(Uri uri, string code, string body, string token)
	{
		string authority = uri.GetLeftPart(UriPartial.Authority);
		long now = Environment.TickCount64;
		if (m_peerRejectionReported.TryGetValue(authority, out long last) && now - last < 10_000) return;
		m_peerRejectionReported[authority] = now;
		(string peer, long peerEpoch) = ClusterProtocol.ClusterNet.RejectingPeer(body);
		string fingerprint = ClusterProtocol.ClusterNet.Fingerprint(token);
		string line = string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] credential_rejected by peer: peer={0} peer_epoch={1} endpoint={2} route={3} code={4} token_fp={5}; reporting it, not exiting",
			peer ?? "?", peerEpoch, authority, uri.AbsolutePath, code, fingerprint);
		Console.WriteLine(line);
		MyLog.Default?.WriteLineAndConsole(line);
		string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		if (string.IsNullOrWhiteSpace(gateway)) return;
		ThreadPool.QueueUserWorkItem(_ =>
		{
			try
			{
				using StringContent content = new StringContent(JsonSerializer.Serialize(new
				{
					Reporter = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"), ReporterEpoch = Interlocked.Read(ref RegistryNodeEpoch),
					PeerEndpoint = authority, Route = uri.AbsolutePath, Code = code, Peer = peer, PeerEpoch = peerEpoch, TokenFingerprint = fingerprint,
				}), System.Text.Encoding.UTF8, "application/json");
				using HttpResponseMessage response = m_registryClient.PostAsync(gateway + "/registry/peer-rejections", content).GetAwaiter().GetResult();
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] peer credential rejection reported: " + (int)response.StatusCode + " "
					+ SecretRedactor.Redact(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
			}
			catch (Exception exception)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] peer credential rejection report failed: " + SecretRedactor.Redact(exception.Message));
			}
		});
	}

	internal static bool WorldAuthorityReadyToServe(string state, bool sessionReady, bool checkpointRestored, bool fenced)
		=> state == "Registered" && sessionReady && checkpointRestored && !fenced;

	/// <summary>
	/// SE1-0084: every game frame (the plugin's Update), on a regular node with a loaded session. Runs the fast tick detector
	/// (<see cref="PartitionManager.Tick"/>); when a member entered or left the fast state, asks the Gateway for a
	/// partition pass now. Never throws: an exception from the plugin's Update stops it for good.
	/// </summary>
	public static void Tick()
	{
		if (m_partitions == null || m_isWorldAuthority || IsAuthorityFenced || MySession.Static?.Ready != true) return;
		try
		{
			if (m_partitions.Tick()) RequestGatewayPass();
		}
		catch (Exception exception)
		{
			long now = Environment.TickCount64;
			if (now < m_nextTickError) return;
			m_nextTickError = now + 60_000;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] fast tick failed (logged once a minute): " + exception);
		}
	}

	/// <summary>
	/// SE1-0084: POST /registry/report-now. The Gateway's pass loop then pulls every node's partition report at once
	/// rather than within the next second, so the wake pass, co-location and the World Authority see a fast transition
	/// right away. One request in flight, at most one per <see cref="GatewayPassGapMilliseconds"/>; a transition while one
	/// is out or waiting sends one more after it (<see cref="CoalescedRequest"/>). A failed request costs the second the
	/// loop waits anyway.
	/// </summary>
	private static void RequestGatewayPass()
	{
		if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")) || RegistryUnreachable) return;
		m_gatewayPass.Request();
	}

	private static void PostGatewayPass()
	{
		string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY").TrimEnd('/');
		long wait = Interlocked.Read(ref m_lastGatewayPass) + GatewayPassGapMilliseconds - Environment.TickCount64;
		if (wait > 0) Thread.Sleep((int)wait);
		Interlocked.Exchange(ref m_lastGatewayPass, Environment.TickCount64);
		using HttpResponseMessage response = m_registryClient.PostAsync(gateway + "/registry/report-now", null).GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode) throw new HttpRequestException("answered " + (int)response.StatusCode);
	}

	private static void GatewayPassFailed(Exception exception)
	{
		long now = Environment.TickCount64;
		if (now < Interlocked.Read(ref m_nextGatewayPassError)) return;
		Interlocked.Exchange(ref m_nextGatewayPassError, now + 60_000);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] Gateway pass request failed (logged once a minute): "
			+ SecretRedactor.Redact(exception.Message));
	}

	public static void Update()
	{
		Interlocked.Exchange(ref m_lastGameThreadUpdate, Environment.TickCount64);
        if (IsClustered) ClientDiagnostics.Update();
		GridListCommand.Update();
		WatchGameThreadExit();
		UpdateDormancy();
		ApplyPendingAuthorityGeneration();
		PluginServices.Instance?.Update();
        if (IsAuthorityFenced) return;
		if (MySession.Static?.Ready == true)
		{
			// WA has no partition loader to announce Serving. Its checkpoint/ledger restore
			// completes before vanilla session readiness; registration alone is insufficient.
			// #1318: and not before the commands journaled since its restored save are replayed (GlobalStateChannel.Update).
			if (IsWorldAuthority && m_worldAuthorityPersistence != null && !GlobalCommandJournal.ReplayPending
				&& WorldAuthorityReadyToServe(NodeState, MySession.Static.Ready, WorldAuthorityCheckpointPatch.Restored, IsAuthorityFenced))
				SetNodeState("Serving");
			if (IsServing) NodeProcessIdentity.Ready();
			GlobalStateChannel.Update();
		}
		GpsAuthorityPatch.Update();
		MyPlayerCollectionPatch.Update();
		MyBankingSystemPatch.Update();
		ContractLedger.Update();
		MyFactionCollectionPatch.Update();
		PositionRegistryService.Update();
		OfflinePartitionService.Update();   // T-0193: the World Authority's offline table poll and player wake pass
		NpcCensusService.Update();          // #1354: the cluster-wide NPC census, polled every 5 s
		UnknownOwnerGuard.Update();         // #1365: grids owned by an identity this node does not know, one walk a minute
		// Measurement only, throttled to one walk per 3600 frames. See PhysicsBodyCensus.
		PhysicsBodyCensus.Update();
		ForcedSleepService.Update();     // Viktor 2026-09-26: stopped + supported grids sleep; off with CLUSTER_FORCED_SLEEP=0
		if (m_pendingGameLogLine != null && MyLog.Default != null)
		{
			MyLog.Default.WriteLine(m_pendingGameLogLine);
			m_pendingGameLogLine = null;
		}
		if (IsClustered && IsServing && !IsWorldAuthority)
		{
			UnvisitedNpcSweeper.Update(m_partitions, m_persistence);   // #1354: unvisited/gap NPC removal; npc.sweepSeconds (default 60, 0 = off)
			WreckageCleaner.Update(m_partitions, m_persistence);       // #1390: small cheap wreckage; cleanup.wreckSeconds (default 0 = off)
			DropContainerRemovalService.Update(m_partitions, m_persistence);   // #1391: drops with no removal deadline get vanilla's
		}
		GroupPoolScrubber.Update();     // T-0147, drops pooled grid groups that pin closed grids; CLUSTER_GROUP_SCRUB_SECONDS (default 60)
		ClientChildRepair.Update();     // T-0225, re-sends block child replicables a client lacks; CLUSTER_CHILD_REPAIR_SECONDS (default 5)
		ClientLayerRefresh.Update();    // T-0300, early update-layer refresh on motion (CLUSTER_LAYER_REFRESH_METERS, default 500) and partition import
		ClusterControlServer.SweepFinishedTransfers();  // T-0147, drops payloads of finished handovers; CLUSTER_TRANSFER_PAYLOAD_SECONDS (default 600)
		// Measurement only, one ratio read per tick while an import is recovering. See ImportRecoveryProbe.
		ImportRecoveryProbe.Update();
		AntennaGraphService.Update();
		LaserCallUpService.Update();
		OfflineRateCache.Update();
		OfflineCompensation.UpdateObservations();
		ShipAntennaService.Update();   // T-0186, no-op unless CLUSTER_SHIP_ANTENNA_RULE is set (default off)
		RespawnRegistryService.Update();
		IgcEndpointRegistryService.Update();
		IgcDeliveryService.Update();
		PcuLedgerService.Update();
		RespawnCooldownService.Update();
		ShipStorageService.Update();
		AdminSafeZones.Update();
		PromotionService.Update();
		OnlinePlayersService.Update();
		RemotePlayerMirrorService.Update();
		PlayerStateService.Update();
		CharacterRegistryService.Update();
		PlayerProfileService.Update();
		BroadcasterLatchProbe.Update();
		ControlReassertService.Update();
		JetpackResyncService.Update();
		CharacterHomeService.Update();
		TimeOfDayService.Update();
		WeatherAuthorityService.Update();
		ConcealableContentService.Update();
		AsteroidSpawnService.Update();
		AsteroidStorageWarmup.Update();   // once per node process: procedural asteroid storages cached off the game thread
		SpawnDirectorService.Update();
		DropContainerDirectorService.Update();
		EncounterDirectorService.Update();
		VoxelJournalService.Instance?.Update();
		ReplicationClientViewPatch.UpdateWaitingVoxels();   // T-0368: carried asteroids a target adopts once it has generated and caught them up
		HavokBodyAudit.Update();   // T-0266: 1 Hz scan for live bodies whose native object is already gone
		Shared.Patches.VoxelClipmapWaitSetPatch.Update();   // T-0274: voxel clipmap wait-set count, 5 min
		HavokOrphanBodyFix.Update();   // T-0274: bodies vanilla Deactivate orphaned, disposed; 5 min count line
		if (!IsWorldAuthority && MySession.Static?.Ready == true)
		{
			m_persistence?.Update();
			if (!m_storePaymentReconcileStarted && IsClustered && AuthorityGeneration > 0)
			{
				m_storePaymentReconcileStarted = true;
				// Reconciliation blocks on World Authority round trips - never on the update path.
				new System.Threading.Thread(() => StorePaymentJournal.Reconcile(MyBankingSystemPatch.ChangeBalanceWithOperation))
					{ IsBackground = true, Name = "ClusterStorePaymentReconcile" }.Start();
			}
		}
		else if (IsWorldAuthority && MySession.Static?.Ready == true)
		{
			m_worldAuthorityPersistence?.Update();
		}
	}

	internal static JsonDocument Get(HttpClient client, string url)
	{
		using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
		response.EnsureSuccessStatusCode();
		return JsonDocument.Parse(response.Content.ReadAsStream());
	}

	internal static void SetAuthorityGeneration(long generation)
	{
		if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
		if (m_isWorldAuthority) return;
		if (MySession.Static?.Ready == true) Interlocked.Exchange(ref m_pendingAuthorityGeneration, generation);
		else if (AuthorityGeneration != generation) ApplyAuthorityGeneration(generation);
	}

	private static void ApplyPendingAuthorityGeneration()
	{
		long generation = Interlocked.Exchange(ref m_pendingAuthorityGeneration, -1);
		if (generation >= 0) ApplyAuthorityGeneration(generation);
	}

	private static void ApplyAuthorityGeneration(long generation)
	{
		if (AuthorityGeneration == generation) return;
		AuthorityGeneration = generation;
		MyPlayerCollectionPatch.AuthorityChanged(generation);
		AntennaGraphService.AuthorityChanged(generation);
		RespawnRegistryService.AuthorityChanged(generation);
		IgcEndpointRegistryService.AuthorityChanged(generation);
		IgcDeliveryService.AuthorityChanged(generation);
		GlobalStateChannel.AuthorityChanged(generation);
	}

	private static void FenceWorldAuthority()
	{
		if (Interlocked.Exchange(ref m_authorityFenced, 1) != 0) return;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] World Authority generation fenced; exiting");
		ArmExitWatchdog("World Authority generation fenced");
		ExitProcess(3, "World Authority generation fenced");
	}
}
