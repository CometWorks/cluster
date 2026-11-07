using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ClusterRegistry;

internal sealed partial class DirectGateway : INetEventListener, IDisposable
{
    private const byte NodeLinkChannels = 16;
    private readonly GatewayOptions _options;
    private readonly NetManager _front;
    private readonly NetManager _back;
    private readonly ConcurrentDictionary<ulong, ClientSession> _clients = new();
    private readonly ConcurrentDictionary<IPEndPoint, NodeLink> _nodes = new();
    private readonly ConcurrentDictionary<long, PendingGatewayCommand> _gatewayCommands = new();
	private readonly object _identitySnapshotLock = new();
	private long _identitySnapshotGeneration;
	private long _identitySnapshotSequence;
    private readonly ConcurrentDictionary<string, HandoverPause> _handoverPauses = new();
    private readonly HandoverTelemetry _handoverTelemetry = new();
    // T-0391: replaceable at run time through POST /balance/cost-model (a live balancer A/B); every pass reads it anew.
    // #1278 (F35): written only by OverrideCostModel under _costModelSwap; a pass reads it ONCE (CostModel model = _costModel).
    private volatile CostModel _costModel;
    private readonly object _costModelSwap = new();
    /// <summary>T-0141: per-node load EMA, one step per co-location pass. See SmoothNodeLoads.</summary>
    private readonly Dictionary<string, double> _nodeLoadEma = new();
    /// <summary>
    /// EMA weight on the newest pass. 0.25 gives a ~4-pass time constant, which at the observed
    /// pass rate spans the arrival transient that makes the raw signal jump (a partition landing
    /// reports one unsmoothed sample) without lagging a real load shift by more than a few passes.
    /// </summary>
    private const double NodeLoadEmaAlpha = 0.25;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _rotation = new(1, 1);
    /// <summary>SE1-0084: set by POST /registry/report-now, a node whose member entered or left the fast state.
    /// The automatic loop then starts its next pass at once instead of after its one second wait.</summary>
    private readonly AutoResetEvent _passNow = new(false);
    /// <summary>Requested passes still start at least this long after the previous one did, so a flapping
    /// member cannot turn the loop into a busy one.</summary>
    private static readonly TimeSpan MinPassGap = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// T-0392 (CH 2026-10-06 12:13, pacing A/B term 6): every handover runs under <see cref="_rotation"/>, so a slower
    /// import (import.activateMsPerSecond) costs cluster-wide rotation time, not one node's. Logs how long a handover
    /// waited for the semaphore: always for pull demands, forced handovers and close-node (one line per handover), and
    /// for the automatic loop's pass only when it waited a second or more (it takes the semaphore every pass).
    /// </summary>
    private void WaitRotation(string source, ulong? partition, bool always)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _rotation.Wait();
        LogRotationWait(source, partition, always, started);
    }

    private async Task WaitRotationAsync(string source, bool always)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        await _rotation.WaitAsync();
        LogRotationWait(source, null, always, started);
    }

    private static void LogRotationWait(string source, ulong? partition, bool always, long started)
    {
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (always || ms >= 1000)
            Console.WriteLine("rotation_wait source=" + source + " partition=" + (partition?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")
                + " ms=" + ms.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
    }

    // A partition the target cannot import repeats every rotation and never resolves - and because the
    // loop always takes the closest pair, the one that cannot move starves every pair that can. A second
    // apart, the freeze/serialize/import cycle it runs is enough to move the simulation speed. Back a
    // failing partition off so the cluster keeps working on what it can and the failure stays visible.
    private readonly Dictionary<ulong, (int Failures, DateTime NextAttempt)> _handoverBackoff = new();
    private const double HandoverBackoffCapSeconds = 300;
    // SE-1080: a balance migration is optional, but while its crew's adopt-ready poll runs the moving partition
    // is frozen on the source and this loop does nothing else - no co-location, wake or drain. The full budget
    // (30 s + 10 s per extra client) let a trader crew whose binding never matched hold the loop 101-112 s per
    // attempt for 10 minutes on the live cluster (2026-11-05 00:14-00:24Z); a player who arrived by gate 58 m
    // from another node's Moon Station waited 82 s for the co-location that shows him its grids. Healthy crews
    // are ready in 3.3 s at p95 and 4.5 s at most (w22), so a balance migration gives up after this, and a
    // partition whose readiness timed out stays out of balancing for the backoff cap.
    private static readonly TimeSpan BalanceReadinessBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// T-0251: a partition that just ARRIVED on a node (a balance migration or an offline load) stays
    /// there for this long before the paced balancer may move it again. w33 2026-09-26: the Earth base
    /// P517 (308 entities; a 5.6 s import hitch and ~2 min at sim 0.1 on the target) was moved node-3
    /// -> node-5 at 01:44 and pinned back at 01:46; loaded onto node-1 at 06:51:46 and moved on 11 s
    /// later, then again at 06:59 for a 1.2 % peak gain against an estimated 15.6 s migration lag.
    /// The existing guards were a global 60 s cooldown and the 180 s pin. 0 disables it.
    /// </summary>
    private static readonly double BalanceResidenceSeconds =
        double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_BALANCE_RESIDENCE_SECONDS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double residence)
            && residence >= 0 ? residence : 600;

    /// <summary>Only a partition whose migration is itself a stall is held: the residence must not freeze
    /// the many small approach loads around the fleet in place. T-0393: the threshold is the cost model's
    /// <see cref="CostModel.ResidenceMinFreezeMs"/> (formerly the const 2000), so 0 holds every arrival.</summary>
    internal static bool ResidenceSizeHeld(CostModel model, PartitionInfo info) =>
        model.FreezeMs(info.EstimatedSnapshotBytes, info.MemberCount) >= model.ResidenceMinFreezeMs;

    // A partition a node already reports while its offline row still exists is mid-load: the earliest arrival
    // there is. 61fbda6 2026-09-26 09:41:18: P517's first report (import-inflated, 1.14M sim-us/s) reached the
    // planner one second before the row dropped at 09:41:19 ('back ONLINE', where the arrival is noted), and
    // the load pinned it node-1->node-3 in that second.
    private bool RecentlyArrived(ulong partition, IReadOnlyDictionary<ulong, PartitionInfo> reported, CostModel model) =>
        BalanceResidenceSeconds > 0
        && (_registry?.IsOffline(partition) == true
            || _registry?.ArrivedAt(partition) is DateTime at && (DateTime.UtcNow - at).TotalSeconds < BalanceResidenceSeconds)
        && reported.TryGetValue(partition, out PartitionInfo? info)
        && ResidenceSizeHeld(model, info);

    /// <summary>
    /// T-0251 instrument: residence is silent unless it changed the plan, so the pass is planned again
    /// without it and a resident partition the planner would have moved is logged (618b165 07:18-07:38:
    /// P517 stayed on node-1, but node-1 was never a source, so its zero did not discriminate).
    /// </summary>
    private static void LogResidenceHeld(HashSet<ulong> resident, IReadOnlyDictionary<ulong, PartitionInfo> reported,
        RebalancePlan[] chosen, RebalancePlan[] without)
    {
        foreach (RebalancePlan plan in without)
        {
            ulong[] held = plan.Members.Select(m => m.Id).Where(resident.Contains).ToArray();
            if (held.Length == 0) continue;
            Console.WriteLine($"Balance residence held partition(s) {string.Join(",", held)}: without residence the planner"
                + $" would move a component of {plan.Members.Length} {plan.Source}->{plan.Target}, peak {plan.OldPeak}->{plan.NewPeak}"
                + $" {plan.LoadUnit}, estimated lag {plan.CostMs:F0} ms; chosen instead: "
                + (chosen.Length == 0 ? "nothing" : string.Join("; ", chosen.Select(c => $"{c.Members.Length} partition(s) {c.Source}->{c.Target}"))));
        }
    }

    /// <summary>
    /// T-0296 r3: whether a rebalance may be INITIATED this pass. "No co-location move was chosen" used to be read as
    /// "every component is co-located", but ChooseMove also answers no move when the member that has to move is in
    /// handover backoff (<paramref name="moverHeld"/>). The planner builds its components per node, so it saw the two
    /// halves of that split component as separate units and could pin the half that is NOT held to a third node.
    /// </summary>
    internal static bool RebalanceMayStart(bool moveChosen, bool moverHeld) => !moveChosen && !moverHeld;

    private bool InHandoverBackoff(ulong partition)
    {
        lock (_handoverBackoff)
            return _handoverBackoff.TryGetValue(partition, out var state) && DateTime.UtcNow < state.NextAttempt;
    }

    private void RecordHandoverFailure(ulong partition, bool backOffFully = false)
    {
        lock (_handoverBackoff)
        {
            int failures = _handoverBackoff.TryGetValue(partition, out var state) ? state.Failures + 1 : 1;
            double seconds = backOffFully ? HandoverBackoffCapSeconds : Math.Min(HandoverBackoffCapSeconds, Math.Pow(2, Math.Min(failures, 9)));
            _handoverBackoff[partition] = (failures, DateTime.UtcNow.AddSeconds(seconds));
            if (failures == 1 || failures % 10 == 0)
                Console.Error.WriteLine($"Partition {partition} handover has failed {failures} time(s); next attempt in {seconds:F0}s");
        }
    }

    private void RecordHandoverSuccess(ulong partition)
    {
        lock (_handoverBackoff) _handoverBackoff.Remove(partition);
        lock (_lastHandover) _lastHandover[partition] = DateTime.UtcNow;
    }

    /// <summary>Partitioning §6.10: the durable HandoverBlocked exclusion, distinct from the transient backoff above.</summary>
    private bool ValidationBlocked(ulong partition) => _registry?.HandoverScheduleBlocked(partition) ?? false;

    // Stamped on every completed handover; read by the --unthrottled admin release only. The old
    // per-partition balance dwell that consumed it is gone with the global rebalance design.
    private readonly Dictionary<ulong, DateTime> _lastHandover = new();
    // Pacing between rebalance INITIATIONS, not a limit on what may move: the member transfers of a
    // pinned migration ride the ordinary one-transfer-per-pass loop (Partitioning 7.3's sequential
    // freezes), and reason "balance-migration" re-arms this stamp so a fresh migration is not layered
    // on top of one still in flight.
    private const double BalanceCooldownSeconds = 60;
    private const double BalanceDeclineLogSeconds = 300;
    private DateTime _lastBalanceDeclineLogged = DateTime.MinValue;
    // T-0409: load-floor held passes (paced planner) and the rate limit of their log line.
    private long _balanceFloorHeldPasses;
    private DateTime _lastBalanceFloorLogged = DateTime.MinValue;
    // T-0409 follow-up: candidates the peak-drop guard refused, and passes it left without a plan (paced planner).
    private long _balancePeakDropSkips;
    private long _balancePeakDropHeldPasses;
    private DateTime _lastBudgetExhaustedLogged = DateTime.MinValue;
    /// <summary>T-0393: hysteresis, move budget and last-hour counters of the balancer (see BalancePace).</summary>
    private readonly BalancePace _balancePace = new();
    /// <summary>
    /// Above this member speed (m/s) a partition is "in flight" and the balancer leaves it alone
    /// (T-0074). Well above a character on foot, well below any ship under way on a leg.
    /// </summary>
    internal const double InFlightSpeed = 20;
    private DateTime _lastBalanceMove = DateTime.MinValue;
    // Docs/GlobalLoadBalancing.md 5: an in-flight rebalance pins each member of the moving component
    // to its target node; the co-location chooser treats the pin as the component's home, which is what
    // lets a migration start minority-first without being dragged back. Pins renew on progress and
    // expire on stall, so a dead migration reverts to cost-based homing.
    private const double MigrationPinSeconds = 180;
    private readonly Dictionary<ulong, (string Target, DateTime Expires)> _balanceMigrations = new();
    // Edge-triggered marker for the balance pause (recovery or rotation), so the 1 Hz loop logs
    // the pause once on entry and once on exit instead of every second.
    private string? _balancePauseReason;
    private DateTime _balanceResumedAt = DateTime.MinValue;
    // How long after a spare activation the load distribution is treated as rotation-transient.
    // Measured w23 rotations (activation -> node back) run 41-70 s; 90 covers them with slack.
    // Not a balance-behavior limit - an operational filter on WHEN the balancer trusts its
    // snapshot, the same class as the recovery pause.
    private const double RotationSettlingSeconds = 90;
    /// <summary>
    /// T-0235: nodes an operator /handover moved a partition OFF, with when. The rotator drains a
    /// node partition by partition through /handover and closes it only at the end, and its paced
    /// drain outlasts the 90 s spare-activation window of GatewayRegistry.RotationInFlight: on
    /// 2026-09-24 14:42:17 (rotation 12, 92 s after node-4's activation) the balancer pinned
    /// partition 65715 INTO node-3 while node-3 was being drained; it went unowned at the close
    /// and was recovered from its Final save. Every drain move re-arms the entry, so balancing
    /// stays paused, and pins into the node are dropped, for as long as the drain keeps moving.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _drainSources = new(StringComparer.Ordinal);
    // A partition younger than this whose gap is under the invariant reads as the join/respawn
    // spawn-adjacency class (fresh character partition next to another node's grids, co-located
    // sub-second) and logs WITHOUT the ALARM prefix, keeping the alarm channel pure signal.
    // Measured w23 19:45-19:53: 27/27 such events on sub-30 s partitions, resolved in 0-17 s.
    private const double SpawnAdjacencySeconds = 30;
    // First time each live partition id appeared in a node report; ages the spawn-adjacency
    // demotion. Pruned against the live id set so retired ids do not accumulate.
    private readonly Dictionary<ulong, DateTime> _partitionFirstSeen = new();
    private DateTime _lastFirstSeenPrune = DateTime.MinValue;

    private double PartitionAgeSeconds(ulong partition)
    {
        lock (_partitionFirstSeen)
            return _partitionFirstSeen.TryGetValue(partition, out DateTime seen)
                ? (DateTime.UtcNow - seen).TotalSeconds : 0;
    }

    private void StampFirstSeen(HashSet<ulong> live)
    {
        DateTime now = DateTime.UtcNow;
        lock (_partitionFirstSeen)
        {
            foreach (ulong id in live) _partitionFirstSeen.TryAdd(id, now);
            if ((now - _lastFirstSeenPrune).TotalSeconds < 300) return;
            _lastFirstSeenPrune = now;
            foreach (ulong id in _partitionFirstSeen.Keys.Where(id => !live.Contains(id)).ToArray())
                _partitionFirstSeen.Remove(id);
        }
    }
    private DateTime _lastCatalogGc = DateTime.MinValue;

    /// <summary>
    /// How often the save catalogue is garbage-collected. Nothing ever called PlanRetention on a
    /// running cluster before 2026-08-19: the node side of retention (poll /registry/deletions,
    /// unlink, tombstone, ack) has always run every replication cycle, but no directive was ever
    /// produced for it, so the catalogue only grew. After three hours of w18 it held 8361 saves
    /// for 1451 partitions - 93.8% of a 10.3 MB registry, of which 87% belonged to partitions
    /// that lineage had already retired - and the registry is re-serialised in full on every
    /// mutation, so that dead catalogue was being written to disk about six times a second.
    /// </summary>
    private const double CatalogGcSeconds = 60;
    private readonly ConcurrentQueue<DisconnectEvent> _disconnects = new();
    private readonly GatewayRegistry? _registry;
    private readonly AdmissionBook _admission;
    private readonly SteamFrontend? _steam;
    private readonly EosFrontend? _eos;
    private readonly object _authorityClockLock = new();
    private readonly object _pauseLock = new();
    private long _authorityClockGeneration;
    private double _authorityClockMilliseconds;
    private double _authorityClockObservedMilliseconds;
    private HttpListener? _control;
    private Thread? _frontThread;
    private Thread? _backThread;
    private Thread? _controlThread;
    private Thread? _automaticThread;
    private readonly object _pullLock = new();
    private readonly Dictionary<ulong, DateTimeOffset> _pullCooldowns = new();
    private Thread? _leaseThread;
    private volatile bool _running;
    private volatile bool _paused;
    // AdmissionOpen gates new joins. Existing sessions remain routed while their
    // authority binding is current; only an explicit/persistence pause holds relay.
    private bool RelayPaused => _paused || _registry is { PersistenceFailed: true };
    /// <summary>Set by the graceful shutdown's notice phase: no new joins while players are told and
    /// sent to the menu. Never persisted, so the next start admits everyone again (no kick cooldown).</summary>
    private volatile bool _joiningClosed;
    private bool JoiningClosed => _joiningClosed || _registry is { AdmissionOpen: false };
    /// <summary>Self-test: the first step of the graceful shutdown, alone.</summary>
    internal void CloseJoiningForSelfTest() => _joiningClosed = true;
    private volatile bool _automaticSuspended;
    private long? _forceOnlyDrainEpoch;
    private long _clientPackets, _clientBytes, _nodePackets, _nodeBytes, _relayTicks, _relayAllocations, _duplicateServerData, _queueOverflows;
    private long _globalMessages, _rejectedGlobalMessages, _admissionRejections, _singleFlightRefusals;
    /// <summary>A test verdict armed for the next joining client, -1 when none. See §9 items 1-2.</summary>
    private int _armedTicketVerdict = -1;
    private long _gatewayCommandSequence;
    //: Round-robin cursor for clients the registry has no partition route for.
    private long _unroutedCursor;
    private readonly AdminTokenFile? _adminTokens;
    private readonly EventLog _events;
    private readonly BanStore? _banStore;
    private readonly ArtifactStore? _artifacts;

    public DirectGateway(GatewayOptions options)
    {
        string? expectedVersion = Environment.GetEnvironmentVariable("CLUSTER_DIRECT_TRANSPORT_VERSION");
        if (expectedVersion != null && expectedVersion != DirectTransportConstants.VanillaVersion)
            throw new InvalidOperationException($"Direct Transport codec is pinned to {DirectTransportConstants.VanillaVersion}, not {expectedVersion}");
        _options = options;
        _http = new HttpClient(new NodeCallHandler(this) { InnerHandler = new HttpClientHandler() });
        _http.Timeout = TimeSpan.FromSeconds(10);
        if (options.AdminToken != null)
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.AdminToken);
        _paused = options.Paused;
        _costModel = CostModel.Load(options.CostModelPath);
        _adminTokens = options.AdminTokensPath == null ? null : AdminTokenFile.Load(options.AdminTokensPath);
        _events = new EventLog(options.RegistryDirectory);
        _registry = options.RegistryDirectory == null ? null : new GatewayRegistry(options.RegistryDirectory, options.Admission!, leaseTtl: options.LeaseTtl, managedDeployment: GatewayRegistry.ReadManagedDeployment(),
            gatewayHost: Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_HOST") is { Length: > 0 } gatewayHost ? gatewayHost : null);
        if (_registry != null)
        {
            _registry.EventSink = (type, target, data) =>
            {
                _events.Append(type, target: target, data: data);
                // Invoked inside the registry lock: only queue the reaction (T-0244).
                if (type == "node.dead" && data != null && data.TryGetValue("endpoint", out string? endpoint))
                    ThreadPool.QueueUserWorkItem(_ => OnRegistryNodeDead(target, endpoint));
            };
            _registry.DrainingNodes = DrainingNodes;
            _registry.HeavyNodes = () => _heavyNodes;
            // T-0321 P4: lease hold for remote nodes inside the configured network windows (off unless configured).
            _registry.LeaseHold = NetworkWindows.FromEnvironment();
            if (_registry.LeaseHold is { } hold)
                Console.WriteLine($"Network windows: {string.Join(", ", hold.Windows.Select(w => w.Start.ToString(@"hh\:mm") + "-" + w.End.ToString(@"hh\:mm")))} (+{hold.Grace.TotalSeconds:0} s); "
                    + $"leases of nodes not on {hold.GatewayHost} are held inside them");
        }
        _admission = new AdmissionBook(AdmissionSettings.Load(options.AdmissionConfig));
        _banStore = BanStore.Open(options.RegistryDirectory);
        foreach (ulong banned in _banStore?.Load() ?? [])
            _admission.Ban(banned, true);
        _artifacts = options.ExportRoot == null ? null : new ArtifactStore(options.ExportRoot);
        _eos = options.EosListen == null ? null
            : new EosFrontend(options.EosListen, DirectTransportConstants.VanillaVersion);
        // T-0229: with --relay external gateway-rs is the Steam frontend; C# keeps the decisions.
        _steam = options.SteamListen == null || options.ExternalRelay ? null
            : new SteamFrontend(options.SteamListen, DirectTransportConstants.VanillaVersion, _admission,
                acceptAllTag: options.DirectFrontend)
            {
                SessionRequested = OpenSteamSession,
                Received = OnSteamPacket,
                SessionFailed = (userId, reason) => CloseSteamSession(userId, "steam-session-failed: " + reason),
                SessionClosed = userId => CloseSteamSession(userId, "steam-session-closed"),
                TicketValidated = OnSteamTicket,
                GroupStatus = OnSteamGroupStatus,
                Players = ClusterPlayers,
            };
        _front = CreateManager(this);
        // A client re-streaming its whole world view after a cross-node respawn switch can go silent
        // longer than the default 30s (observed ~50s), and a front timeout tears the session down -
        // detaching the new node, which kicks the vanilla player while the oblivious client keeps
        // playing as a zombie. Node links keep the short timeout: WA-death detection depends on it.
        _front.DisconnectTimeout = 120000;
        _back = CreateManager(this);
    }

    public void Run()
    {
        // SE-842: not Stop(), which disconnects the nodes: they stay up for the respawn.
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => ExitOnSigterm(_registry));
        if (_registry != null && _options.SourceControl != null && _options.TargetControl != null && _options.TargetBackend != null)
        {
            try { ReconcileHandovers().GetAwaiter().GetResult(); }
            catch (Exception e) { Console.Error.WriteLine("Startup handover reconciliation failed: " + e.Message); }
        }
        if (_options.ExternalRelay)
            StartExternalRelay();
        else if (_options.DirectFrontend)
        {
            // The v6 socket has to follow the v4 bind: IPv6Any next to a loopback --listen would put the
            // accept-all frontend on every v6 interface, which is exactly what Security.md §3.6 forbids and
            // what the loopback check in GatewayOptions promises against.
            IPAddress listenV6 = IPAddress.IsLoopback(_options.Listen.Address) ? IPAddress.IPv6Loopback : IPAddress.IPv6Any;
            if (!_front.Start(_options.Listen.Address, listenV6, _options.Listen.Port))
                throw new InvalidOperationException($"Failed to bind gateway on {_options.Listen}");
            Console.WriteLine("WARNING: the Direct Transport frontend is enabled on "
                + $"{_options.Listen} with accept-all admission (Security.md §3.6)");
        }
        if (!_options.ExternalRelay)
        {
            if (!_back.Start())
                throw new InvalidOperationException("Failed to start backend UDP socket");
            _steam?.Start();
            _eos?.Start();
        }

        _running = true;
        _frontThread = _options.DirectFrontend && !_options.ExternalRelay ? StartPollThread("Gateway-Client-Poll", _front) : null;
        StartSessionMaintenance();
        _backThread = _options.ExternalRelay ? null : StartPollThread("Gateway-Backend-Poll", _back);
        StartControl();
        StartLeaseExpiry();
        StartAutomaticHandover();

        Console.WriteLine($"Gateway listening on {(_options.DirectFrontend ? _options.Listen.ToString() + " (direct)" : "")}"
            + $"{(!SteamEnabled ? "" : (_options.DirectFrontend ? " " : "") + _options.SteamListen + " (steam)")}"
            + $"{(_eos == null ? "" : " " + _options.EosListen + " (eos, login only)")}"
            + $", backend {_options.Backend}");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
        _stop.Wait();
    }

    /// <summary>
    /// SIGTERM is a stop of this process, not of the cluster: commit what the Registry holds and exit
    /// with the status the signal would have given. The restarted Registry recovers as after a crash,
    /// short of nothing it acknowledged. A stuck commit costs that guarantee, never the exit.
    /// </summary>
    internal static void ExitOnSigterm(GatewayRegistry? registry)
    {
        bool committed = registry?.FlushAndHold(SigtermFlushTimeout) ?? true;
        Console.WriteLine(committed ? "SIGTERM: Registry state committed, exiting"
            : $"WARNING: SIGTERM: Registry state not committed within {SigtermFlushTimeout.TotalSeconds:0} s, exiting without it");
        Environment.Exit(143);
    }

    private static readonly TimeSpan SigtermFlushTimeout = TimeSpan.FromSeconds(5);

    public void Stop()
    {
        _running = false;
		foreach (ClientSession session in _clients.Values) session.AdmissionCancellation.Cancel();
        foreach (HandoverPause pause in _handoverPauses.Values)
            pause.Release.TrySetResult();
        _stop.Set();
        try { _front.Stop(); } catch { }
        try { _eos?.Stop(); } catch { }
        try { _back.Stop(); } catch { }
        foreach (NodeLink link in _nodes.Values)
            link.Connected.Dispose();
        _clients.Clear();
        _nodes.Clear();
        try { _control?.Stop(); } catch { }
        try { _steamPublishTimer?.Dispose(); } catch { }
        try { _relay?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _frontThread?.Join(500); } catch { }
        try { _backThread?.Join(500); } catch { }
        try { _controlThread?.Join(500); } catch { }
        try { _automaticThread?.Join(500); } catch { }
        try { _leaseThread?.Join(500); } catch { }
        _steam?.Dispose();
        _stop.Dispose();
        _http.Dispose();
        _registry?.Dispose();
        _events.Dispose();
    }

    private void StartLeaseExpiry()
    {
        if (_registry == null)
            return;
        _leaseThread = new Thread(() =>
        {
            while (_running)
            {
                try { _registry.ExpireLeases(); }
                catch (Exception e) { Console.Error.WriteLine("Lease expiry failed: " + e.Message); }
                try { _registry.CompleteStrandedShutdown(); }
                catch (Exception e) { Console.Error.WriteLine("Stranded shutdown completion failed: " + e.Message); }
                // T-0320: node-control lifecycle, drain deadlines and the per-host planner (registry-only, no I/O).
                try { _registry.NodeControlPass(); }
                catch (Exception e) { Console.Error.WriteLine("Node control pass failed: " + e.Message); }
				RefreshPlayerAdmissions();
                foreach (NodeRegistration node in _registry.Nodes().Where(node => node.State == NodeState.Active))
                {
                    try
                    {
                        NodeLink link = GetNodeLink(EndpointParser.Parse(node.Endpoint, DirectTransportConstants.DefaultPort));
                        ConnectBackend(link);
                    }
                    catch (Exception e) { Console.Error.WriteLine("Node-link connect failed: " + e.Message); }
                }
                try { RefreshExternalRouting(); }
                catch (Exception e) { Console.Error.WriteLine("Relay routing refresh failed: " + e.Message); }
                Thread.Sleep(1000);
            }
        }) { Name = "Gateway-Lease-Expiry", IsBackground = true };
        _leaseThread.Start();
    }

    private void StartAutomaticHandover()
    {
        // With a registry the serving set comes from node registrations; the fixed CLI pair is
        // only required in the registry-less debug mode.
        if (_registry == null && (_options.SourceControl == null || _options.TargetControl == null || _options.TargetBackend == null))
            return;
        _automaticThread = new Thread(AutomaticHandoverLoop) { Name = "Gateway-Automatic-Handover", IsBackground = true };
        _automaticThread.Start();
    }

    /// <summary>A node the automatic loop may move partitions between: registered active regular,
    /// or one side of the configured pair when running without a registry.</summary>
    private sealed record ServedNode(string Node, long Epoch, string Control, IPEndPoint Backend);

    private ServedNode[] ServedNodes() => _registry != null
        ? [.. _registry.Nodes()
            .Where(x => x.Role == NodeRole.Regular && x.State == NodeState.Active && x.Serving && !x.Spare && x.ControlEndpoint != null)
            .OrderBy(x => x.Node, StringComparer.Ordinal)
            .Select(x => new ServedNode(x.Node, x.Epoch, StripScheme(x.ControlEndpoint!),
                EndpointParser.Parse(x.Endpoint, DirectTransportConstants.DefaultPort)))]
        : [new ServedNode("source", 0, _options.SourceControl!, _options.Backend),
            new ServedNode("target", 0, _options.TargetControl!, _options.TargetBackend!)];

    /// <summary>
    /// Marks superseded saves for deletion on the schedule the nodes are already polling for.
    /// Retention itself is unchanged and deliberately conservative - PlanRetention keeps the three
    /// newest saves of EVERY partition (retired ones included), every save pinned by a retained
    /// snapshot, and the full recovery cover of every live partition - so this only ever removes
    /// registrations that no recovery path can reach. Failures are logged and swallowed: a
    /// catalogue that stays too big is a performance problem, but an exception here would take
    /// down the handover loop, which is a correctness one.
    /// </summary>
    private void CollectSaveCatalog()
    {
        if (_registry == null || (DateTime.UtcNow - _lastCatalogGc).TotalSeconds < CatalogGcSeconds) return;
        _lastCatalogGc = DateTime.UtcNow;
        try
        {
            DeleteDirective[] deletions = _registry.PlanRetention();
            if (deletions.Length != 0)
                Console.WriteLine($"Save catalogue GC marked {deletions.Length} superseded save copies for deletion");
            // T-0323: then the rows of stopped nodes that nothing names any more. Takes the registry lock itself, twice,
            // and scans the archive between the two holds without it.
            _registry.ReleaseGoneRows();
        }
        catch (Exception exception)
        {
            Console.WriteLine("WARNING: save catalogue GC did not run this round: " + SecretRedactor.Redact(exception.Message));
        }
    }

    private void AutomaticHandoverLoop()
    {
        while (_running)
        {
            ulong? attempting = null;
            bool attemptingBalance = false;
            long passStarted = Stopwatch.GetTimestamp();
            try
            {
                WaitRotation("auto", null, false);
                // Docs/StateDurability.md: mutations are coalesced in memory and reach the
                // disk here, at most once per GatewayRegistry.PersistInterval. FIRST in the
                // pass, before reconciliation and the catalogue GC: those talk to nodes over
                // HTTP and can throw for as long as a node is unreachable, and a durability
                // flush must not be starved by somebody else's network problem.
                if (_registry?.FlushDue == true) _registry.Flush();
                if (_registry != null)
                    ReconcileHandovers().GetAwaiter().GetResult();
                CollectSaveCatalog();
                _registry?.SweepHandoverBlocks();
                if (!_automaticSuspended)
                {
                    ServedNode[] served = ServedNodes();
                    // T-0296 r4: a node that does not answer no longer ends the pass for every other node (CollectReports).
                    // A partial pass wakes, co-locates and drains among the nodes that did report; everything that reads a
                    // partition's or a node's ABSENCE waits for a full one: the pin prune, the scale-in settle, the balance
                    // planner, the route-contradiction clock and the published snapshot (offline admission).
                    List<(ServedNode Node, string Error)> silent = [];
                    (ServedNode Node, PartitionReport Report)[] reports = CollectReports(served, node => GetPartitions(node.Control), silent);
                    // SE-1134: everything below - the balancer, scale-in, drains, /diag's balance load - reads a node's
                    // sim ratio only once a dip has lasted (CostModel.SimRatioSustainSeconds).
                    reports = SustainRatios(reports, node => node.Node, _lowRatioRuns, _costModel.SimRatioSustainSeconds, DateTime.UtcNow);
                    string? missingReports = MissingReportPause([.. silent.Select(x => x.Node.Node)]);
                    if (missingReports != _missingReports)
                    {
                        Console.Error.WriteLine(missingReports != null
                            ? $"Automatic handover pass is partial: {missingReports} ({string.Join("; ", silent.Select(x => x.Node.Node + ": " + x.Error))})"
                            : "Automatic handover pass is complete again: every serving node reported");
                        _missingReports = missingReports;
                    }
                    if (reports.Select(x => x.Report.SyncDistance).Distinct().Count() > 1)
                        throw new InvalidOperationException("Cluster nodes have different sync distances");
                    StampFirstSeen([.. reports.SelectMany(x => x.Report.Partitions).Select(p => p.Id)]);
                    if (missingReports == null) PruneMigrationPins(reports);
                    // A node's partition report re-associates a freshly switched client only after
                    // the player binding settles, so for seconds after a switch its own partition
                    // reads as clientless. The registry's client routes are the authority: a
                    // partition routed to a CONNECTED client counts as client-bearing no matter
                    // what the report says.
                    // ... but only for those seconds. The route-over-report rule was
                    // written for a settling window and applied FOREVER, which is the pin
                    // half of the keystone deadlock: a route the node report keeps
                    // contradicting defers every handover of that partition for the life
                    // of the cluster, the partition cannot leave a draining node, and the
                    // node is eventually killed with the row still assigned - which is
                    // exactly the stranded-partition state (w19 2026-08-19: 24 of 26
                    // rotator "would not move" refusals were route pins). So the COUNT
                    // expires, not the route: past the grace window the node report wins
                    // and the partition becomes movable again. The route itself is left
                    // alone - the join side throws on an unresolvable placement to guard
                    // against duplicate bodies, and that guard stays.
                    HashSet<ulong> routed = new HashSet<ulong>();
                    if (_registry != null)
                    {
                        List<(ulong Client, ulong Partition)> routes = [];
                        foreach (ulong clientId in _clients.Keys)
                            if (_registry.GetClientPartition(clientId) is { } location)
                                routes.Add((clientId, location.Partition));
                            // T-0238: a join answered from the character HOME records no placement until the node
                            // reports the client, so the partition its body is in read "clients 0" and the balancer
                            // moved it away mid-join (w32 2026-09-24 21:28:54 pilot-9; 2026-09-25 00:26:03 pilot-1,
                            // whose home had just been loaded for that very join - a start that loads nothing makes
                            // every early join this case). The home partition is its route until the report lands;
                            // RouteOverReportGrace still bounds a stale one.
                            else if (_registry.GetCharacterHome(clientId) is { } home)
                                routes.Add((clientId, home.Partition));
                        HashSet<ulong> reportsClients = ClientBearingAsReported([.. reports.SelectMany(x => x.Report.Partitions)],
                            routes, partial: missingReports != null);
                        routed = RoutedClientBearing(routes, reportsClients, _routeContradictedSince,
                            DateTimeOffset.UtcNow, RouteOverReportGrace, out var expired);
                        foreach ((ulong client, ulong partition, double seconds) in expired)
                            if (_routePinLogged.Add(partition))
                                Console.WriteLine($"Partition {partition} is routed to client {client} but no node "
                                    + $"has reported it client-bearing for {seconds:F0}s; trusting the node report "
                                    + $"so the partition can be handed over again");
                        foreach (ulong partition in _routePinLogged.Where(x => !_routeContradictedSince.ContainsKey(x)).ToArray())
                            _routePinLogged.Remove(partition);
                        ReconnectSessionsAwayFromBody(served);
                    }
                    // T-0193: the offline admission check reads these, and the wake pass runs on them
                    // BEFORE anything below can throw or pick a move - demand for an offline partition
                    // must not wait behind a failing handover.
                    // Not from a partial pass: the offline admission would read the silent node's players as nobody near.
                    if (missingReports == null)
                        _lastPartitionReports = (DateTime.UtcNow, reports.Select(x => (x.Node.Node, x.Report)).ToArray());
                    try { WakeOfflinePartitions(reports, routed); }
                    catch (Exception exception) { Console.Error.WriteLine("Offline wake pass failed: " + exception.Message); }
                    // Partitioning.md 7.1: co-locate everything inside the D + m envelope. Which is not a
                    // pairwise property, and treating it as one is why three partitions could thrash a node
                    // pair indefinitely: with A and B on this node and C on the other, and B within envelope
                    // of both A and C, moving B to join C put it out of reach of A, moving it back put it out
                    // of reach of C, and the loop traded one violation for the other about once a second -
                    // every handover succeeding, the CPU and the simulation rate paying for all of them.
                    // Partitions inside one another's envelope form a connected component that has to live on
                    // one node, so solve the component and move a partition that is on the wrong side of it.
                    // With more than two nodes the component solve stays pairwise per node pair - the most
                    // urgent (smallest-gap) move across all pairs runs first, and the next pass re-evaluates.
                    (Move Move, (ServedNode Node, PartitionReport Report) From, (ServedNode Node, PartitionReport Report) To)? decision = null;
                    // T-0141: advance each node's load EMA ONCE for this pass, before the pair loop.
                    // Doing it inside the loop would step a node's EMA once per PAIR it appears in -
                    // three times a pass on four nodes - which makes the smoothing constant depend on
                    // the fleet size. The pair loop then only READS these.
                    Dictionary<string, double> passLoad = SmoothNodeLoads(reports);
                    // #1278 (F35): one cost model for the whole pass; an override landing mid-pass applies from the next one.
                    CostModel model = _costModel;
                    CostModel gapModel = GapModel(model);
                    // #1454: the heavy units of this pass, before anything below decides a move.
                    IReadOnlySet<ulong> heavy = UpdateHeavy(reports, routed, gapModel, partial: missingReports != null);
                    // T-0312: scale-in picks and drains retiring nodes by pinning their components (NodeScaling.cs).
                    // Not on a partial pass: SettleRetiring reads a retiring node without a report as "owns nothing" and closes it.
                    HashSet<string> retiring = _registry == null ? []
                        : missingReports == null ? ScaleInPass(reports, passLoad, routed, gapModel) : [.. _registry.RetiringNodes()];
                    // T-0296 r3: ChooseMove asks this only about a member that has to move, so a "true" means a
                    // component is NOT co-located and merely waits out its backoff (see RebalanceMayStart).
                    HashSet<ulong> voidedPins = [];
                    List<ulong[]> trains = [];
                    bool moverHeld = false;
                    bool HeldMover(ulong id)
                    {
                        if (!InHandoverBackoff(id)) return false;
                        moverHeld = true;
                        return true;
                    }
                    // T-0327: a node whose lease is lapsed and held by a network window takes part in no automatic handover.
                    IReadOnlySet<string> leaseHeld = _registry?.LapsedHeldNodes() ?? new HashSet<string>();
                    LogLeaseHeldExclusion(leaseHeld);
                    for (int i = 0; i < reports.Length; i++)
                        for (int j = i + 1; j < reports.Length; j++)
                        {
                            if (leaseHeld.Contains(reports[i].Node.Node) || leaseHeld.Contains(reports[j].Node.Node)) continue;
                            double pairEnvelope = reports[i].Report.SyncDistance
                                + Math.Min(reports[i].Report.Margin, reports[j].Report.Margin);
                            string sourceNode = reports[i].Node.Node, targetNode = reports[j].Node.Node;
                            MigrationSide PairMigrationSide(ulong id) => MigrationTarget(id) switch
                            {
                                null => MigrationSide.None,
                                var pin when pin == targetNode => MigrationSide.Target,
                                var pin when pin == sourceNode => MigrationSide.Source,
                                _ => MigrationSide.Elsewhere,
                            };
                            if (ChooseMove(reports[i].Report, reports[j].Report, pairEnvelope, routed, HeldMover, gapModel, TravellingClients(), ValidationBlocked, PairMigrationSide,
                                    passLoad[sourceNode], passLoad[targetNode], voidedPins.UnionWith, trains.Add, heavy)
                                is { } candidate && (decision is not { } current || candidate.Gap < current.Move.Gap))
                                decision = candidate.ToTarget
                                    ? (candidate, reports[i], reports[j])
                                    : (candidate, reports[j], reports[i]);
                        }
                    _migrationTrains = [.. trains];
                    // Not during a scale-in drain: ScaleInPass re-pins every component of a retiring node each pass.
                    if (retiring.Count == 0) DropVoidedPins(voidedPins);
                    // 7.1 preempts load balancing, so a rebalance is only ever INITIATED once every
                    // component is already co-located (Docs/GlobalLoadBalancing.md). The chooser pins a
                    // whole component to a target; from the NEXT pass the co-location loop above carries
                    // the migration member-by-member as reason "balance-migration", which also re-arms
                    // the cooldown so migrations do not stack.
                    //
                    // Balancing on half-loaded data plans garbage (t=0+2min on 2026-08-24 the excess
                    // read 20947.00 because the other nodes had not registered their content yet), so
                    // while any partition is still unowned or recovering the balancer waits. It also
                    // waits out node rotations (see GatewayRegistry.RotationInFlight): the drain's
                    // transient loads made the balancer pin 80k PCU into the node being drained.
                    string? pauseReason = missingReports != null ? missingReports
                        : _registry?.RecoveryPending() == true
                        ? "partitions are still unowned or recovering"
                        : _registry?.RotationInFlight(TimeSpan.FromSeconds(RotationSettlingSeconds)) == true
                            || DrainingNodes().Length != 0
                            ? "node rotation in flight"
                            : retiring.Count != 0 ? "scale-in drain in flight"
                            : _registry?.LeaseHoldActive() == true ? "network window: remote node leases held"
                            : null;
                    if (pauseReason != _balancePauseReason)
                    {
                        Console.WriteLine(pauseReason != null
                            ? $"Balancing paused: {pauseReason}"
                            : _balancePauseReason == "node rotation in flight"
                                ? "Balancing resumed: rotation settled"
                                : "Balancing resumed: recovery drained");
                        // T-0312: BalancerIdle counts quiet time from the end of a rotation or scale-in pause.
                        if (_balancePauseReason is "node rotation in flight" or "scale-in drain in flight") _balanceResumedAt = DateTime.UtcNow;
                        _balancePauseReason = pauseReason;
                    }
                    if (RebalanceMayStart(decision != null, moverHeld) && reports.Length >= 2 && pauseReason == null)
                    {
                        // Viktor 2026-08-24: with ZERO clients connected - startup before the fleet
                        // joins, the nightly idle window - migration lag has no audience, so the whole
                        // optimizer plan pins at once and the cluster converges in one train instead
                        // of one paced component per minute.
                        //
                        // Residence applies here too (T-0251, 91b3b15 08:09-08:10): the idle plan re-reads
                        // measured loads every pass, and a partition fresh from an import reads inflated -
                        // P517 was pinned node-1->node-2 at 08:09:03 and, 49 s after it arrived, node-2->node-4
                        // for a projected 316k->309k peak at a 15 s migration lag. The first train is not
                        // affected: a restart's recovery claim is not an arrival.
                        Dictionary<ulong, PartitionInfo> reported = [];
                        foreach (var x in reports) foreach (PartitionInfo info in x.Report.Partitions) reported[info.Id] = info;
                        HashSet<ulong> resident = [];
                        // #1454 case 4: the residence does not keep a component on a heavy node - it leaves for good.
                        HashSet<string> heavyNodes = [.. _heavyNodes];
                        HashSet<ulong> evacuees = [.. reports.Where(x => heavyNodes.Contains(x.Node.Node))
                            .SelectMany(x => x.Report.Partitions).Select(p => p.Id).Where(id => !heavy.Contains(id))];
                        bool Held(ulong id)
                        {
                            if (InHandoverBackoff(id)) return true;
                            if (evacuees.Contains(id) || !RecentlyArrived(id, reported, model)) return false;
                            resident.Add(id);
                            return true;
                        }
                        var planNodes = reports.Select(x => (x.Report, x.Node.Node)).ToArray();
                        // T-0393: the hysteresis picks this pass's gate (the tolerance unless balancing is active
                        // and a low water is set); every pass the planner runs is reported back to the pace state.
                        DateTime paceNow = DateTime.UtcNow;
                        double gate = _balancePace.Gate(model);
                        if (_clients.IsEmpty)
                        {
                            RebalancePlan[] plans = ChooseRebalancePlans(planNodes, gapModel, routed,
                                Held, ValidationBlocked, MigrationPinned, fullPlan: true, null, out var diag, gate, null, heavy, HeavyCooling);
                            if (resident.Count != 0) LogResidenceHeld(resident, reported, plans, ChooseRebalancePlans(planNodes, gapModel, routed,
                                InHandoverBackoff, ValidationBlocked, MigrationPinned, fullPlan: true, null, gate, null, heavy, HeavyCooling));
                            _balancePace.Passed(model, gate, diag.WorstDeviation, diag.WorstClientDeviation,
                                plans.Length, diag.MinGainSkipped, diag.BudgetSkipped, resident.Count, paceNow);
                            _balanceDiagnostics = new BalanceDiagnostics(DateTime.UtcNow, diag.Excess,
                                diag.AchievableExcess, diag.WorstDeviation, diag.ClientExcess,
                                diag.WorstClientDeviation, plans.Length, null);
                            if (plans.Length != 0)
                            {
                                Console.WriteLine($"Idle-cluster rebalance plan: {plans.Length} component(s),"
                                    + $" {plans.Sum(p => p.Members.Length)} partition(s), projected peak load"
                                    + $" {plans[0].OldPeak}->{plans[^1].NewPeak} {plans[0].LoadUnit}, estimated total migration lag"
                                    + $" {plans.Sum(p => p.CostMs):F0} ms");
                                foreach (RebalancePlan step in plans)
                                {
                                    PinMigration(step.Members, step.Target);
                                    LogBalancePin(step, model);
                                }
                                _lastBalanceMove = DateTime.UtcNow;
                            }
                        }
                        else if ((DateTime.UtcNow - _lastBalanceMove).TotalSeconds >= (ScaleUnsettled() ? ScaleBalanceCooldownSeconds : BalanceCooldownSeconds))
                        {
                            IReadOnlySet<string> busy = BusyMigrationNodes(reports);
                            IReadOnlySet<string>? overBudget = _balancePace.OverBudget(model, paceNow);
                            RebalancePlan[] plans = ChooseRebalancePlans(planNodes, gapModel, routed, Held,
                                ValidationBlocked, MigrationPinned, fullPlan: false, busy, out var diag, gate, overBudget, heavy, HeavyCooling);
                            if (resident.Count != 0) LogResidenceHeld(resident, reported, plans, ChooseRebalancePlans(planNodes, gapModel, routed,
                                InHandoverBackoff, ValidationBlocked, MigrationPinned, fullPlan: false, busy, gate, overBudget, heavy, HeavyCooling));
                            _balanceDiagnostics = new BalanceDiagnostics(DateTime.UtcNow, diag.Excess,
                                diag.AchievableExcess, diag.WorstDeviation, diag.ClientExcess,
                                diag.WorstClientDeviation, plans.Length, null);
                            // The planner returns a per-node-disjoint SET (each node in at most one
                            // migration, in-flight ones counted via BusyMigrationNodes) - pin them
                            // all; no node pays two concurrent stalls.
                            foreach (RebalancePlan plan in plans)
                            {
                                PinMigration(plan.Members, plan.Target);
                                _lastBalanceMove = DateTime.UtcNow;
                                _balancePace.Pinned(plan.Target, paceNow);
                                LogBalancePin(plan, model);
                                if (plan.Kind is RebalanceKind.HeavySplit or RebalanceKind.HeavyDedicate)
                                    HeavyMoved(plan.Members.Select(m => m.Id), plan.Members.Sum(m => ClientCount(m, routed)));
                            }
                            _balancePace.Passed(model, gate, diag.WorstDeviation, diag.WorstClientDeviation,
                                plans.Length, diag.MinGainSkipped, diag.BudgetSkipped, resident.Count, paceNow);
                            if (diag.PeakDropSkipped > 0)
                            {
                                Interlocked.Add(ref _balancePeakDropSkips, diag.PeakDropSkipped);
                                if (plans.Length == 0) Interlocked.Increment(ref _balancePeakDropHeldPasses);
                            }
                            // T-0073: a refusal logs as loudly as an action - a balancer that saw the
                            // misplacement and declined is otherwise indistinguishable from one that never
                            // ran. Rate-limited: the inputs change every pass, the situation rarely does.
                            // T-0409: held by the load floor - its own line, so the arm read counts held passes apart from declines.
                            if (diag.FloorHeldPeak > 0)
                            {
                                Interlocked.Increment(ref _balanceFloorHeldPasses);
                                if ((DateTime.UtcNow - _lastBalanceFloorLogged).TotalSeconds >= BalanceDeclineLogSeconds)
                                {
                                    _lastBalanceFloorLogged = DateTime.UtcNow;
                                    Console.WriteLine($"Balancing held by the load floor: peak {diag.FloorHeldPeak} < BalanceMinPeakLoad"
                                        + $" {model.BalanceMinPeakLoad:F0} sim-us/s, worst deviation {diag.WorstDeviation:F2},"
                                        + $" client deviation {diag.WorstClientDeviation:F2} vs gate {gate:F2}; {Interlocked.Read(ref _balanceFloorHeldPasses)} held pass(es)"
                                        + $" since start; {diag.Explain}");
                                }
                            }
                            else if (plans.Length == 0 && (diag.WorstDeviation > gate
                                    || diag.WorstClientDeviation > gate)
                                && (DateTime.UtcNow - _lastBalanceDeclineLogged).TotalSeconds >= BalanceDeclineLogSeconds)
                            {
                                _lastBalanceDeclineLogged = DateTime.UtcNow;
                                Console.WriteLine($"Balancing declined: excess {diag.Excess:F2}, worst deviation {diag.WorstDeviation:F2},"
                                    + $" client deviation {diag.WorstClientDeviation:F2} vs tolerance {model.BalanceRiskTolerance:F2},"
                                    + (gate != model.BalanceRiskTolerance ? $" (balancing active, low water {gate:F2})," : "")
                                    + $" but no movable component lowers over-optimal load without raising the peak; {diag.Explain}"
                                    + (diag.MinGainSkipped + diag.BudgetSkipped + diag.PeakDropSkipped > 0
                                        ? $"; candidates skipped: min-gain {diag.MinGainSkipped}, budget {diag.BudgetSkipped},"
                                            + $" peak-drop {diag.PeakDropSkipped} (BalanceMinPeakDrop {model.BalanceMinPeakDrop:F3})" : ""));
                            }
                            // T-0393: the budget's refusals, rate-limited like the decline above.
                            if (diag.BudgetSkipped > 0
                                && (DateTime.UtcNow - _lastBudgetExhaustedLogged).TotalSeconds >= BalanceDeclineLogSeconds)
                            {
                                _lastBudgetExhaustedLogged = DateTime.UtcNow;
                                Console.WriteLine($"Balancing budget exhausted: {diag.BudgetSkipped} candidate(s) skipped, target(s) at"
                                    + $" {model.BudgetMovesPerNodeHour} paced pins in the last hour: {string.Join(" ", overBudget!.Order())};"
                                    + $" worst deviation {diag.WorstDeviation:F2}, client deviation {diag.WorstClientDeviation:F2}"
                                    + $" under the budget threshold {model.BudgetUnderDeviation:F2}; pinned {plans.Length} other move(s); {diag.Explain}");
                            }
                        }
                    }
                    else if (pauseReason != null)
                        _balanceDiagnostics = new BalanceDiagnostics(DateTime.UtcNow,
                            _balanceDiagnostics?.Excess ?? 0, _balanceDiagnostics?.AchievableExcess ?? 0,
                            _balanceDiagnostics?.WorstDeviation ?? 0, _balanceDiagnostics?.ClientExcess ?? 0,
                            _balanceDiagnostics?.WorstClientDeviation ?? 0, 0, pauseReason);
                    if (decision is { Move.Reason: "co-location-blocked" } stuck)
                    {
                        // Partitioning §6.10: both sides of this component are HandoverBlocked. Escalate as
                        // an operator-visible incident once a pass instead of retrying the failing transfer.
                        Console.WriteLine($"INCIDENT: co-location involving partition {stuck.Move.Moving.Id} is blocked on both sides"
                            + $" (HandoverBlocked, component of {stuck.Move.ComponentSize}); no further transfers will be scheduled until a block clears");
                    }
                    else if (decision is { } chosen)
                    {
                        PartitionInfo moving = chosen.Move.Moving;
                        bool toTarget = chosen.Move.ToTarget;
                        int movingClients = ClientCount(moving, routed);
                        bool handedOver = false;
                        double syncDistance = chosen.From.Report.SyncDistance;
                        if (chosen.Move.RawGap < syncDistance)
                        {
                            // Since raw gaps are entity-exact (566be53f) the alarm stream is pure
                            // signal - but the join/respawn flow legitimately materializes a fresh
                            // character partition metres from another node's grids and co-locates
                            // it sub-second (w23 19:45-19:53: 27 such alarms, 1:1 with the
                            // reconnect wave, all resolved in 0-17 s, zero on established ids).
                            // That expected class logs without the ALARM prefix, keyed on
                            // partition age; a sub-invariant gap on an ESTABLISHED partition
                            // remains the real alarm.
                            Console.WriteLine(PartitionAgeSeconds(moving.Id) < SpawnAdjacencySeconds
                                ? $"Spawn-adjacent cross-node gap {chosen.Move.RawGap:F1}m for partition {moving.Id} (below the {syncDistance:F0}m invariant); accelerating co-location"
                                : $"ALARM: cross-node sphere gap {chosen.Move.RawGap:F1}m for partition {moving.Id} is below the {syncDistance:F0}m invariant; accelerating co-location");
                        }
                        Console.WriteLine($"Automatic partition handover triggered for {moving.Id}, reason {chosen.Move.Reason}, envelope gap {chosen.Move.Gap:F1}m, raw gap {chosen.Move.RawGap:F1}m, direction {(toTarget ? "source->target" : "target->source")}, clients {movingClients}, component home {(chosen.Move.HomeIsTarget ? "target" : "source")} of {chosen.Move.ComponentSize}, route {chosen.From.Node.Node}->{chosen.To.Node.Node}; why: {chosen.Move.Why}");
                        attempting = moving.Id;
                        attemptingBalance = chosen.Move.Reason.StartsWith("balance");
                        if (movingClients == 0)
                            FencedClientlessHandover(chosen.From.Node.Control, chosen.To.Node.Control, chosen.To.Node.Backend, moving.Id, allowRemoteUse: chosen.Move.Reason == "co-location").GetAwaiter().GetResult();
                        else if (moving.Clients.Length == 0)
                        {
                            // Deferred is not free: the pin survives the pass and the same plan is
                            // chosen again next second (w29 09:49:05-09:49:40: 22 triggers of
                            // partition 29558 in 35 s, one per pass). Back off like a failure.
                            Console.WriteLine($"Partition {moving.Id} has a routed client missing from its node report; automatic handover deferred");
                            RecordHandoverFailure(moving.Id);
                        }
                        // T-0238: CLIENT_READY is not the end of a join - the node still hands the client its
                        // character and control after it. A balance move 1-2 s later left pilot-16 world-ready
                        // with no local character on the new node (w32 2026-09-25 14:24:43, 8 failed recoveries);
                        // over 154 post-ready handovers on w32, NO_CHARACTER followed 3 of 19 moved within 5 s
                        // of ready, 0 of 25 at 6-15 s. An automatic move waits out the settle.
                        else if (moving.Clients.FirstOrDefault(JoinStillSettling) is var settling && settling != 0)
                        {
                            Console.WriteLine($"Partition {moving.Id} carries client {settling}, world-ready under "
                                + $"{PostReadySettle.TotalSeconds:F0}s ago; automatic handover deferred");
                            RecordHandoverFailure(moving.Id);
                        }
                        // T-0237: the node reports only clients bound to a character in the partition, so a client
                        // still loading the world is not in moving.Clients - and moving its partition takes its body
                        // away while its session stays behind. w33 2026-09-27 12:12:22: Pilot-103 joined 91594 on
                        // node-1 at 12:11:57 (body seated in its ship there); the co-location moved 91594 to node-2
                        // with clients=1 (pilot-79 only) 7 s before 103 was world-ready; node-1 then gave 103 a new
                        // body, and node-2 despawned the seated one as a duplicate. The gateway knows the joining
                        // client's placement and home, so it defers the move until that client has settled.
                        else if (JoiningClientPlacedIn(moving.Id) is var joining && joining != 0)
                        {
                            Console.WriteLine($"Partition {moving.Id} is the placement or home of client {joining}, which is still joining "
                                + $"(not world-ready or under {PostReadySettle.TotalSeconds:F0}s ready); automatic handover deferred");
                            RecordHandoverFailure(moving.Id);
                        }
                        // Every client on the partition moves in one transfer, so a shared grid never
                        // leaves half its crew behind; a partition is deferred only when the gateway
                        // cannot see a switchable session for ALL of them.
                        else if (ResolveMovingSessions(moving.Clients, chosen.From.Node.Backend) is not { } movingSessions)
                        {
                            Console.WriteLine($"Partition {moving.Id} has a client with no connected, ready source session; automatic handover deferred");
                            RecordHandoverFailure(moving.Id);
                        }
                        else
                        {
                            Handover(movingSessions, chosen.From.Node.Control, chosen.To.Node.Control, chosen.To.Node.Backend, moving.Id,
                                readinessBudget: attemptingBalance ? BalanceReadinessBudget : null).GetAwaiter().GetResult();
                            // The directed §7.4 move landed; the next encounter for this client is priced normally.
                            foreach (ulong client in moving.Clients) ClearTeleportIntent(client);
                            handedOver = true;
                        }
                        // Only a completed handover clears the backoff; a deferral steps it up.
                        if (attempting is ulong moved && (movingClients == 0 || handedOver))
                        {
                            RecordHandoverSuccess(moved);
                            if (chosen.Move.Reason.StartsWith("balance")) _lastBalanceMove = DateTime.UtcNow;
                            OnMigrationProgress(moved, chosen.To.Node.Node);
                        }
                    }
                    else
                        DrainClosedNodes(reports);
                }
                else if (_registry?.HasOffline == true)
                {
                    // Automatic handovers are suspended, but offline partitions still have to come back
                    // when somebody approaches them: gather the reports for the wake pass alone.
                    ServedNode[] served = ServedNodes();
                    List<(ServedNode Node, string Error)> silent = [];
                    (ServedNode Node, PartitionReport Report)[] reports = CollectReports(served, node => GetPartitions(node.Control), silent);
                    HashSet<ulong> routed = [.. _clients.Keys.Select(client => _registry.GetClientPartition(client)?.Partition ?? 0).Where(x => x != 0)];
                    if (silent.Count == 0)
                        _lastPartitionReports = (DateTime.UtcNow, reports.Select(x => (x.Node.Node, x.Report)).ToArray());
                    WakeOfflinePartitions(reports, routed);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Automatic handover check failed: " + e.Message);
                // T-0373: the message alone could not name the site of "Value cannot be null. (Parameter 'key')"
                // (live 2026-10-02 07:04:35). The type and the first frame of ours, once a minute per distinct message.
                if (PassLogDue(_passFailureLogged, "pass|" + e.Message, DateTime.UtcNow))
                    Console.Error.WriteLine("Automatic handover check failure detail: " + DescribeFailure(e));
                if (attempting is ulong failed) RecordHandoverFailure(failed, backOffFully: attemptingBalance && e is TimeoutException);
            }
            finally { if (_rotation.CurrentCount == 0) _rotation.Release(); }
            // SE1-0084: one second between passes, unless a node asks for one now (its fast state changed).
            if (_passNow.WaitOne(1000) && Stopwatch.GetElapsedTime(passStarted) is { } since && since < MinPassGap)
                Thread.Sleep(MinPassGap - since);
        }
    }

    /// <summary>
    /// Drain catch-up for any closed node still inside its force-drain window: client-bearing
    /// single-client partitions hand over to the lightest active node (7.3). Runs only on passes
    /// with no co-location/balance move, keeping the one-initiated-transfer-per-pass budget.
    /// </summary>
    private void DrainClosedNodes((ServedNode Node, PartitionReport Report)[] active)
    {
        if (_registry == null || active.Length == 0)
            return;
        // #1277 (T-0327 class): a lapsed lease held by a network window takes part in no automatic handover, here as in
        // the pair loop; Begin refuses it in any case. A held node still answers /partitions and, drained, is the lightest.
        IReadOnlySet<string> leaseHeld = _registry.LapsedHeldNodes();
        foreach (NodeRegistration closedNode in _registry.Nodes().Where(x => x.Role == NodeRole.Regular
            && x.State == NodeState.Closed && x.ForceDrainAt > DateTimeOffset.UtcNow
            && _forceOnlyDrainEpoch != x.Epoch && x.ControlEndpoint != null && !leaseHeld.Contains(x.Node)))
        {
            // CLUSTER-SIM-COST: lightest by measured load when the whole set reports it, PCU otherwise.
            bool drainSimLoad = active.All(x => x.Report.SimLoadSupported);
            // T-0296 r3: never INTO a retiring node (see LightestDrainTarget) nor a lease-held one; with nothing else
            // serving, the crew stays for the force drain.
            HashSet<string> excluded = new(_registry.RetiringNodes().Concat(leaseHeld));
            (string Node, long Load)[] loads = [.. active.Select(x => (x.Node.Node, EffectiveNodeLoad(x.Report, drainSimLoad)))];
            if (LightestDrainTarget(loads, excluded) is not { } lightest)
                return;
            string closedControl = StripScheme(closedNode.ControlEndpoint!);
            IPEndPoint closedBackend = EndpointParser.Parse(closedNode.Endpoint, DirectTransportConstants.DefaultPort);
            // T-0373: one closed node or one of its partitions that cannot be drained this pass no longer ends the
            // drain catch-up for everything after it. r116b 2026-10-01 18:22:11-18:24:06: node-79 was Closed and
            // reported a client-bearing partition with no registry row; FencedHandover claims such a partition for
            // its source, ClaimPartition refuses a Closed node, and "Node 'node-79' epoch 1 is Closed" escaped
            // from here in 85 passes in a row (4 more on r124, node-98), until the node went Empty.
            PartitionReport report;
            try { report = GetPartitions(closedControl); }
            catch (Exception e)
            {
                LogDrainSkip($"closed node {closedNode.Node}", "its partition report", e);
                continue;
            }
            // #1454 case 6: a heavy partition to the emptiest node without a heavy component, the light ones off those nodes.
            IReadOnlySet<ulong> heavy = _heavy;
            HashSet<string> heavyNodes = [.. _heavyNodes];
            string heavyDest = lightest;
            if (report.Partitions.Any(p => heavy.Contains(p.Id)))
            {
                heavyDest = HeavyDrainTarget(loads.Where(x => !excluded.Contains(x.Node)).Select(x => (x.Node, (double)x.Load)), heavyNodes)
                    ?? lightest;
                heavyNodes.Add(heavyDest);
            }
            string lightDest = LightestDrainTarget(loads, new HashSet<string>(excluded.Concat(heavyNodes))) ?? lightest;
            // One transfer per partition, carrying its whole crew: iterating clients would offer a
            // shared partition once per rider and move the first one alone.
            foreach (PartitionInfo partition in report.Partitions.Where(x => x.Clients.Length != 0))
            {
                if (ResolveMovingSessions(partition.Clients, closedBackend) is not { } moving) continue;
                if (DrainDeferredForJoin(partition.Id, moving, closedNode.Node)) continue;
                bool heavyPartition = heavy.Contains(partition.Id);
                (ServedNode Node, PartitionReport Report) target = active.First(x => x.Node.Node == (heavyPartition ? heavyDest : lightDest));
                try
                {
                    Handover(moving, closedControl, target.Node.Control, target.Node.Backend, partition.Id).GetAwaiter().GetResult();
                    if (heavyPartition) HeavyMoved([partition.Id], moving.Length);
                }
                catch (Exception e) { LogDrainSkip($"partition {partition.Id}", $"its drain handover off closed node {closedNode.Node}", e); }
            }
        }
    }

    // T-0373: what the automatic pass last logged about an item it skipped, and about its own failures. Touched by
    // the automatic handover loop only.
    private readonly Dictionary<string, DateTime> _passFailureLogged = new();
    internal const double PassFailureLogSeconds = 60;

    /// <summary>T-0373: a skipped item is logged once a minute per distinct (item, message), not once a pass.</summary>
    private void LogDrainSkip(string item, string what, Exception e)
    {
        if (PassLogDue(_passFailureLogged, item + "|" + e.Message, DateTime.UtcNow))
            Console.Error.WriteLine($"Automatic handover pass skipped {item}: {what} failed, the pass goes on with the rest "
                + $"(logged once per {PassFailureLogSeconds:F0}s): {DescribeFailure(e)}");
    }

    /// <summary>T-0373: whether <paramref name="key"/> is due a log line: not logged within <see cref="PassFailureLogSeconds"/>.</summary>
    internal static bool PassLogDue(Dictionary<string, DateTime> logged, string key, DateTime now)
    {
        if (logged.TryGetValue(key, out DateTime last) && (now - last).TotalSeconds < PassFailureLogSeconds) return false;
        // Bounded: messages can carry ids, so the distinct keys of a long run are swept once they are stale.
        if (logged.Count >= 256)
            foreach (string stale in logged.Where(x => (now - x.Value).TotalSeconds >= PassFailureLogSeconds).Select(x => x.Key).ToArray())
                logged.Remove(stale);
        logged[key] = now;
        return true;
    }

    /// <summary>T-0373: an exception as type, message and the first stack frame inside this assembly.</summary>
    internal static string DescribeFailure(Exception e)
    {
        string? frame = e.StackTrace?.Split('\n').Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("at ClusterRegistry.", StringComparison.Ordinal));
        return $"{e.GetType().FullName}: {SecretRedactor.Redact(e.Message)}; first frame: {frame ?? "none of ours"}";
    }

    private string _leaseHeldExcluded = "";

    /// <summary>T-0327: one line per change of the set of lease-held nodes the handover pass leaves out.</summary>
    private void LogLeaseHeldExclusion(IReadOnlySet<string> held)
    {
        string now = string.Join(", ", held.Order(StringComparer.Ordinal));
        if (now == _leaseHeldExcluded) return;
        _leaseHeldExcluded = now;
        Console.WriteLine(now.Length != 0
            ? $"network window: no automatic handover into or out of lease-held node(s) {now}"
            : "network window: automatic handovers resume for every node (no lease is held)");
    }

    /// <summary>
    /// T-0296 r3: the drain catch-up's target, the lightest serving node that is not itself retiring. A retiring node
    /// (node control remove/recycle, scale-in) still serves and reports, and it gets LIGHTER as it drains, so the
    /// lightest-node rule picked it more readily the further its own drain had got: a crew moved S -> R, and the
    /// scale-in pass pinned it straight out again (R -> T), two blackouts for one drain. CloseNode's own target
    /// already excludes retiring nodes. #1277: the caller adds the lease-held nodes to <paramref name="excluded"/>.
    /// </summary>
    internal static string? LightestDrainTarget((string Node, long Load)[] active, IReadOnlySet<string> excluded)
    {
        (string Node, long Load)[] candidates = [.. active.Where(x => !excluded.Contains(x.Node))];
        return candidates.Length == 0 ? null : candidates.MinBy(x => x.Load).Node;
    }

    private string? _missingReports;

    /// <summary>
    /// T-0296 r4: the partition reports of one automatic pass. The loop asked every serving node in turn and the first
    /// one that did not answer (connection refused while its row is still Active, or /partitions waiting more than the
    /// 10 s timeout for a stalled game thread: the node answers it ON the game thread) threw out of the whole pass:
    /// no offline wake, no co-location, no drain catch-up for ANY node until it answered or its row left the serving
    /// set (live 2026-09-30 20:27:45-20:31:01: 44 passes in a row, "Connection refused"). A node that fails at the
    /// transport is listed in <paramref name="silent"/> and the pass goes on with the others; anything else (a
    /// malformed report) still fails the pass.
    /// </summary>
    internal static (T Node, PartitionReport Report)[] CollectReports<T>(IEnumerable<T> served, Func<T, PartitionReport> fetch,
        List<(T Node, string Error)> silent)
    {
        List<(T, PartitionReport)> reports = [];
        foreach (T node in served)
            try { reports.Add((node, fetch(node))); }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
            {
                silent.Add((node, SecretRedactor.Redact(exception.Message)));
            }
        return [.. reports];
    }

    /// <summary>Why a pass with silent nodes plans no rebalance, or null for a full pass. Loads and components of a
    /// silent node are unknown, and a plan made without them is the half-loaded-data plan the recovery pause exists for.</summary>
    internal static string? MissingReportPause(IReadOnlyCollection<string> silent)
        => silent.Count == 0 ? null : "no partition report from " + string.Join(", ", silent.Order(StringComparer.Ordinal));

    /// <summary>
    /// The partitions the reports show with a client. On a partial pass a routed partition that NO report lists also
    /// counts: it may be on the silent node, and "contradicted by every node report" (RoutedClientBearing, RoutePinStale)
    /// must not start counting from a report that never came.
    /// </summary>
    internal static HashSet<ulong> ClientBearingAsReported(IReadOnlyCollection<PartitionInfo> reported,
        IEnumerable<(ulong Client, ulong Partition)> routes, bool partial)
    {
        HashSet<ulong> bearing = [.. reported.Where(partition => partition.Clients.Length != 0).Select(partition => partition.Id)];
        if (!partial) return bearing;
        HashSet<ulong> listed = [.. reported.Select(partition => partition.Id)];
        bearing.UnionWith(routes.Select(route => route.Partition).Where(partition => !listed.Contains(partition)));
        return bearing;
    }

    private PartitionReport GetPartitions(string endpoint) =>
        JsonSerializer.Deserialize<PartitionReport>(_http.GetStringAsync(ClusterUrl(endpoint, "partitions")).GetAwaiter().GetResult())
        ?? throw new InvalidDataException("Empty partition report from " + endpoint);

    private static double SphereGap(PartitionSphere a, PartitionSphere b)
    {
        double x = a.X - b.X;
        double y = a.Y - b.Y;
        double z = a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z) - a.Radius - b.Radius;
    }

    internal readonly record struct Move(PartitionInfo Moving, bool ToTarget, double Gap, double RawGap,
        bool HomeIsTarget, int ComponentSize, string Reason, string Why = "");

    /// <summary>
    /// A target-side import refusal with its body preserved: when the Partitioning §6.10 round-trip gate
    /// aborted the import, the body carries the residual signature the registry needs to count
    /// consecutive same-signature aborts toward HandoverBlocked (Orchestration §3.3).
    /// </summary>
    internal sealed class HandoverImportException : Exception
    {
        public HandoverImportException(ulong partition, int status, string body)
            : base($"Import of partition {partition} failed with HTTP {status}: {FirstLine(body)}")
        {
            Partition = partition;
            Signature = System.Text.RegularExpressions.Regex.Match(body, "residual-signature=([0-9a-f]+)") is { Success: true } match
                ? match.Groups[1].Value : null;
            Reason = FirstLine(body);
            Entities = System.Text.RegularExpressions.Regex.Match(body, "residual-entities=([0-9,]+)") is { Success: true } entities
                ? [.. entities.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse)] : null;
        }

        public ulong Partition { get; }
        public string? Signature { get; }
        /// <summary>SE-1134: the top-level entities holding the residuals, from a node that names them.</summary>
        public long[]? Entities { get; }
        public string Reason { get; }

        internal static string FirstLine(string body)
        {
            string line = body.Split('\n', 2)[0].Trim();
            return line.Length <= 200 ? line : line[..200];
        }
    }

    /// <summary>
    /// How long a registry client route may outvote a node report that says the partition
    /// has no clients. Covers the switch-settling window it was written for; past it the
    /// report wins. Override with CLUSTER_ROUTE_OVER_REPORT_GRACE_SECONDS.
    /// </summary>
    private static readonly TimeSpan RouteOverReportGrace = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("CLUSTER_ROUTE_OVER_REPORT_GRACE_SECONDS"),
            out int graceSeconds) && graceSeconds > 0 ? graceSeconds : 120);

    /// <summary>
    /// When each partition's route first disagreed with every node report. Written by the
    /// automatic-handover loop and read by FencedClientlessHandover, which also runs on the
    /// control thread when the rotator drives a handover - hence concurrent.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _routeContradictedSince = new();
    private readonly HashSet<ulong> _routePinLogged = [];

    /// <summary>
    /// Which partitions count as client-bearing for handover scheduling. A registry client
    /// route outvotes a node report that says "no clients" only inside
    /// <paramref name="grace"/> - the switch-settling window the rule was written for.
    /// Past it the node report wins, so a route the reports keep contradicting can no
    /// longer defer every handover of that partition forever. Mutates
    /// <paramref name="contradictedSince"/>: records when each disagreement started and
    /// forgets partitions no client routes to any more.
    /// </summary>
    internal static HashSet<ulong> RoutedClientBearing(
        IReadOnlyCollection<(ulong Client, ulong Partition)> routes,
        HashSet<ulong> reportedWithClients,
        IDictionary<ulong, DateTimeOffset> contradictedSince,
        DateTimeOffset now, TimeSpan grace,
        out List<(ulong Client, ulong Partition, double Seconds)> expired)
    {
        HashSet<ulong> routed = [];
        HashSet<ulong> stillRouted = [];
        expired = [];
        foreach ((ulong client, ulong partition) in routes)
        {
            stillRouted.Add(partition);
            if (reportedWithClients.Contains(partition))
            {
                contradictedSince.Remove(partition);
                routed.Add(partition);
                continue;
            }
            if (!contradictedSince.TryGetValue(partition, out DateTimeOffset since))
                contradictedSince[partition] = since = now;
            if (now - since < grace)
                routed.Add(partition);
            else
                expired.Add((client, partition, (now - since).TotalSeconds));
        }
        foreach (ulong partition in contradictedSince.Keys.Where(x => !stillRouted.Contains(x)).ToArray())
            contradictedSince.Remove(partition);
        return routed;
    }

    /// <summary>
    /// How long a connected session may sit on one node while the registry routes its client to a
    /// partition another node owns before the gateway forces a reconnect. A fenced handover or a
    /// respawn switch moves the session and the route within one transfer (sub-second), so the
    /// only way the two stay apart is a partition that left its node WITHOUT its clients: a
    /// lease-expiry recovery (w29 2026-09-08 04:46, T-0080: node-2 discarded 29115, node-4
    /// recovered it, Pilot-002 and Pilot-010 stayed on node-2 with no character for 8 minutes
    /// until the harness recycled them), or the 120 s route-over-report override moving a
    /// partition on the node's word. The rejoin resolves through the client route / character
    /// home to the node that has the body. Override with CLUSTER_SESSION_AWAY_GRACE_SECONDS.
    /// </summary>
    private static readonly TimeSpan SessionAwayGrace = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("CLUSTER_SESSION_AWAY_GRACE_SECONDS"),
            out int awaySeconds) && awaySeconds > 0 ? awaySeconds : 30);

    /// <summary>When each connected client's session was first seen on a node other than its routed partition's owner.</summary>
    private readonly Dictionary<ulong, DateTimeOffset> _sessionAwaySince = [];

    internal readonly record struct SessionPlacement(ulong Client, string? SessionNode, ulong Partition,
        string? OwnerNode, bool OwnerRecovering, bool Switching);

    /// <summary>
    /// Which connected clients have sat, for at least <paramref name="grace"/>, on a node other
    /// than the one that owns the partition the registry routes them to. A session mid-switch, a
    /// session whose backend is not a registered node, an unowned or still-recovering partition
    /// all count as "not away" - there is nothing to reconnect to yet, or the mismatch is the
    /// switch itself. Mutates <paramref name="awaySince"/> exactly like RoutedClientBearing
    /// mutates its map: dates the first disagreement, forgets clients that agree again or are gone.
    /// </summary>
    internal static List<(ulong Client, ulong Partition, string OwnerNode, string SessionNode, double Seconds)> SessionsAwayFromBody(
        IReadOnlyCollection<SessionPlacement> placements, IDictionary<ulong, DateTimeOffset> awaySince,
        DateTimeOffset now, TimeSpan grace)
    {
        List<(ulong, ulong, string, string, double)> due = [];
        HashSet<ulong> seen = [];
        foreach (SessionPlacement each in placements)
        {
            seen.Add(each.Client);
            bool away = !each.Switching && each.SessionNode != null && each.OwnerNode != null && !each.OwnerRecovering
                && !string.Equals(each.SessionNode, each.OwnerNode, StringComparison.Ordinal);
            if (!away)
            {
                awaySince.Remove(each.Client);
                continue;
            }
            if (!awaySince.TryGetValue(each.Client, out DateTimeOffset since))
                awaySince[each.Client] = since = now;
            if (now - since >= grace)
                due.Add((each.Client, each.Partition, each.OwnerNode!, each.SessionNode!, (now - since).TotalSeconds));
        }
        foreach (ulong client in awaySince.Keys.Where(x => !seen.Contains(x)).ToArray())
            awaySince.Remove(client);
        return due;
    }

    /// <summary>
    /// Runs on the automatic-handover loop: a client whose body's partition is owned by a node its
    /// session is not on cannot be served by that session (the node has no character for it), so
    /// force the reconnect that puts the join through the registry route again. The forced
    /// reconnect clears the away record; a rejoin that lands on the wrong node again is dated afresh.
    /// </summary>
    private void ReconnectSessionsAwayFromBody(ServedNode[] served)
    {
        if (_registry == null) return;
        List<SessionPlacement> placements = [];
        foreach (ClientSession session in _clients.Values)
        {
            if (session.Client == null) continue;
            if (_registry.GetClientPartition(session.ClientId) is not { } location) continue;
            PartitionOwnership? owner;
            try { owner = _registry.GetPartitionOwner(location.Partition); }
            catch (ArgumentException) { owner = null; }
            string backend = session.BackendEndpoint.ToString();
            string? sessionNode = served.FirstOrDefault(node => node.Backend.ToString() == backend)?.Node;
            placements.Add(new SessionPlacement(session.ClientId, sessionNode, location.Partition,
                owner?.Node, owner?.Recovering ?? true, session.SwitchingBackend));
        }
        List<(ulong Client, ulong Partition, string OwnerNode, string SessionNode, double Seconds)> due;
        lock (_sessionAwaySince)
            due = SessionsAwayFromBody(placements, _sessionAwaySince, DateTimeOffset.UtcNow, SessionAwayGrace);
        foreach ((ulong client, ulong partition, string ownerNode, string sessionNode, double seconds) in due)
        {
            if (!_clients.TryGetValue(client, out ClientSession? session)) continue;
            lock (_sessionAwaySince) _sessionAwaySince.Remove(client);
            ForceReconnect(session, $"session on {sessionNode} but routed partition {partition} is owned by {ownerNode} for {seconds:F0}s");
        }
    }

    /// <summary>
    /// True once a client route to this partition has been contradicted by every node
    /// report for longer than <see cref="RouteOverReportGrace"/>: the owning node has said
    /// "no client on this partition" for the whole window, so the route is stale and must
    /// stop blocking disposal. A missing entry means no scheduler pass has observed the
    /// disagreement yet - the conservative case, so the pin stands.
    /// </summary>
    private bool RoutePinStale(ulong partition) =>
        _routeContradictedSince.TryGetValue(partition, out DateTimeOffset since)
        && DateTimeOffset.UtcNow - since >= RouteOverReportGrace;

    private static int ClientCount(PartitionInfo partition, HashSet<ulong> routed) =>
        Math.Max(partition.Clients.Length, routed.Contains(partition.Id) ? 1 : 0);

    /// <summary>
    /// Clients with a pending §7.4 directed move (teleport/admin move): their side of a co-location
    /// encounter moves regardless of §7.5 price. Registered by the node's teleport handler, consumed
    /// when the client's switch lands, expired if no encounter ever materializes.
    /// </summary>
    private readonly Dictionary<ulong, DateTimeOffset> _teleportIntents = new();

    private HashSet<ulong> TravellingClients()
    {
        lock (_teleportIntents)
        {
            foreach (ulong stale in _teleportIntents.Where(x => DateTimeOffset.UtcNow - x.Value > TimeSpan.FromSeconds(120))
                         .Select(x => x.Key).ToArray())
                _teleportIntents.Remove(stale);
            return new HashSet<ulong>(_teleportIntents.Keys);
        }
    }

    private void ClearTeleportIntent(ulong client)
    {
        lock (_teleportIntents) _teleportIntents.Remove(client);
    }

    /// <summary>
    /// The §6.4 join hold: a client whose body sits in a mid-drain (unowned-but-recoverable)
    /// partition gets a transient refusal instead of a default-backend admission - which would
    /// spawn a fresh body seconds before the drain Final imports the real one, forking the
    /// player into a duplicate. The retry lands after the auto-claim. 30 s bounds the hold, so
    /// an unrecoverable home degrades to the old default-backend behaviour, never a lockout.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _homeRecoveryWaits = new();

    private bool JoinHeldForHomeRecovery(ulong clientId)
    {
        if (_registry == null || !_registry.PendingHomeRecovery(clientId))
        {
            _homeRecoveryWaits.TryRemove(clientId, out _);
            return false;
        }
        DateTimeOffset first = _homeRecoveryWaits.GetOrAdd(clientId, _ => DateTimeOffset.UtcNow);
        // T-0193: a home in an offline partition is a directed load in flight (PendingHomeRecovery asked
        // for it); a big base can take longer than a drain's re-home to come up, so it gets the longer hold.
        TimeSpan hold = _registry.HomeOffline(clientId) ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(30);
        if (DateTimeOffset.UtcNow - first >= hold) return false;
        Console.WriteLine($"Join of client {clientId} deferred: its character home is recovering");
        return true;
    }

    /// <summary>T-0292: the fast partitions of the latest reports, for the diag registry section.</summary>
    private object[] FastPartitionsNow() =>
        _lastPartitionReports is { } snapshot
            ? snapshot.Reports.SelectMany(report => report.Report.Partitions.Where(info => info.Fast)
                .Select(info => (object)new { partition = info.Id, node = report.Node, speed = info.FastSpeed, members = info.MemberCount }))
                .ToArray()
            : [];

    /// <summary>SE1-0084: the clients in a fast partition, by the latest reports (at most 5 s old) and by the
    /// registry's routes, for the World Authority's wake check (the offline table's FastClients).</summary>
    private ulong[] FastClientsNow()
    {
        if (_lastPartitionReports is not { } snapshot || DateTime.UtcNow - snapshot.At > TimeSpan.FromSeconds(5)) return [];
        PartitionInfo[] fast = [.. snapshot.Reports.SelectMany(report => report.Report.Partitions).Where(info => info.Fast)];
        if (fast.Length == 0) return [];
        HashSet<ulong> ids = [.. fast.Select(info => info.Id)];
        HashSet<ulong> clients = [.. fast.SelectMany(info => info.Clients ?? [])];
        if (_registry != null)
            foreach (ulong client in _clients.Keys)
                if (_registry.GetClientPartition(client) is { } location && ids.Contains(location.Partition)) clients.Add(client);
        return [.. clients.Order()];
    }

    /// <summary>T-0292: the latest partition reports (at most 15 s old) say this partition is fast.</summary>
    private bool IsFastPartition(ulong partition) =>
        _lastPartitionReports is { } snapshot && DateTime.UtcNow - snapshot.At <= TimeSpan.FromSeconds(15)
        && snapshot.Reports.Any(report => report.Report.Partitions.Any(info => info.Id == partition && info.Fast));

    private static (PartitionInfo Partition, bool OnSource)[] Located(PartitionReport source, PartitionReport target) =>
        source.Partitions.Select(p => (p, true)).Concat(target.Partitions.Select(p => (p, false))).ToArray();

    /// <summary>
    /// Labels each partition with its envelope-connected component. Two partitions inside one another's
    /// envelope have to end up on the same node, so the component - not the partition - is the unit both
    /// co-location and balancing reason about.
    /// </summary>
    /// <para>#1304: <paramref name="units"/> links every pair at the engage distance, same node or not. That groups
    /// partitions into balance units: a unit moved to another node leaves no cross-node pair co-location would drag back
    /// (it engages only below the engage distance). At ColocationEngageFraction 1.0 the units are the components.</para>
    private static int[] Components((PartitionInfo Partition, bool OnSource)[] all, double envelope,
        CostModel model, bool units = false)
    {
        // ponytail: O(n^2) union-find over a node pair's partitions - a few dozen at most, once a second,
        // and the sphere exclusion inside EnvelopeGap keeps far pairs O(1). A cluster-wide uniform grid
        // (Plan/EntityGapPlacement.md) if a node ever carries thousands.
        int[] parent = Enumerable.Range(0, all.Length).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        // T-0141 third attempt: HYSTERESIS. A pair already on the same node stays linked out to the
        // full envelope; a pair on different nodes must close to `engage` before co-location drags
        // them together. Placement is the memory, so no history is kept. At the default fraction of
        // 1.0 `engage == envelope` and this loop is bit-identical to what it replaced.
        // See CostModel.ColocationEngageFraction for the measurements and the invariant bound.
        double engage = envelope * model.ColocationEngageFraction;
        for (int i = 0; i < all.Length; i++)
            for (int j = i + 1; j < all.Length; j++)
            {
                // T-0292: a fast partition links to nothing - it is its own component, so co-location
                // never merges it with, or hands it over towards, the content it flies past.
                double link = all[i].OnSource == all[j].OnSource && !units ? envelope : engage;
                if (all[i].Partition.Fast || all[j].Partition.Fast)
                {
                    if (EnvelopeGap(all[i].Partition, all[j].Partition, link, model) < link) FastPartitionCounters.ColocationLink();
                    continue;
                }
                if (EnvelopeGap(all[i].Partition, all[j].Partition, link, model) < link)
                    parent[Find(i)] = Find(j);
            }
        int[] labels = new int[all.Length];
        for (int i = 0; i < all.Length; i++) labels[i] = Find(i);
        return labels;
    }

    /// <summary>
    /// Where a component stands relative to an in-flight balance migration, from one node pair's point
    /// of view (Docs/GlobalLoadBalancing.md §5). <c>Elsewhere</c> means pinned to a node outside the
    /// pair - that component is another pass's business.
    /// </summary>
    internal enum MigrationSide { None, Source, Target, Elsewhere }

    /// <summary>
    /// Picks the next partition to move so that every envelope-connected component ends up on one node.
    /// Returns null when both nodes are already co-located, or when everything that needs to move is in
    /// handover backoff. A component pinned by a balance migration (<paramref name="migration"/>) has
    /// its home dictated by the pin - including a fully co-located component being moved off its node
    /// minority-first - so the migration is carried member-by-member instead of being dragged back.
    /// </summary>
    internal static Move? ChooseMove(PartitionReport source, PartitionReport target, double envelope,
        HashSet<ulong> routed, Func<ulong, bool> blocked, CostModel? model = null,
        HashSet<ulong>? travelling = null, Func<ulong, bool>? validationBlocked = null,
        Func<ulong, MigrationSide>? migration = null, double loadSource = 0, double loadTarget = 0,
        Action<ulong[]>? pinVoided = null, Action<ulong[]>? carried = null, IReadOnlySet<ulong>? heavy = null)
    {
        model ??= new CostModel();
        Func<ulong, bool> hardBlocked = validationBlocked ?? (_ => false);
        (PartitionInfo Partition, bool OnSource)[] all = Located(source, target);
        int[] labels = Components(all, envelope, model);

        Move? best = null;
        Move? blockedIncident = null;
        foreach (IGrouping<int, int> component in Enumerable.Range(0, all.Length).GroupBy(i => labels[i]))
        {
            (PartitionInfo Partition, bool OnSource)[] members = component.Select(i => all[i]).ToArray();
            MigrationSide pinned = MigrationSide.None;
            if (migration != null)
                foreach ((PartitionInfo partition, _) in members)
                {
                    MigrationSide side = migration(partition.Id);
                    if (side != MigrationSide.None) { pinned = side; break; }
                }
            if (pinned == MigrationSide.Elsewhere) continue;
            // #1304: the planner pins balance units (Components with units: true), so a pin carries the units holding a
            // member pinned this way and leaves the rest of the envelope component where it is. At ColocationEngageFraction
            // 1.0 the units are the component and nothing changes.
            (PartitionInfo Partition, bool OnSource)[] whole = members;
            if (pinned is MigrationSide.Source or MigrationSide.Target)
            {
                int[] units = Components(members, envelope, model, units: true);
                HashSet<int> pinnedUnits = [.. Enumerable.Range(0, members.Length)
                    .Where(i => migration!(members[i].Partition.Id) == pinned).Select(i => units[i])];
                members = [.. Enumerable.Range(0, members.Length).Where(i => pinnedUnits.Contains(units[i])).Select(i => members[i])];
            }
            // T-0249: a validation-blocked member cannot follow the pin, so the pin is not honoured for
            // this component: cost-based co-location decides instead. A component not yet split stays
            // where it is; one already split is reunited (flipping toward the blocked side if needed).
            // Before this, the pin kept carrying the other members away from the blocked one.
            // T-0296 r4: and the pins END there (pinVoided). Left in place they outlived the block (60 s first, the
            // pin 180 s and renewed by any other train): the reunited group was carried apart again by the old plan.
            if (pinned is MigrationSide.Source or MigrationSide.Target
                && members.Any(m => m.OnSource == (pinned == MigrationSide.Target) && hardBlocked(m.Partition.Id)))
            {
                pinned = MigrationSide.None;
                pinVoided?.Invoke([.. members.Select(m => m.Partition.Id)]);
                members = whole;
            }
            bool migrating = pinned != MigrationSide.None;
            if (!migrating && (members.All(m => m.OnSource) || members.All(m => !m.OnSource)))
                continue;   // already co-located
            // #1454 case 5: a heavy side stays, and a merge that makes a heavy component keeps its bigger crew in place.
            string heavyWhy = "";
            bool? heavyHome = migrating ? null
                : HeavyHome(members, routed, model, heavy, travelling, loadSource, loadTarget, out heavyWhy);
            bool homeIsTarget = migrating ? pinned == MigrationSide.Target
                : heavyHome ?? HomeIsTarget(members, routed, model, travelling, loadSource, loadTarget);
            // T-0073: the decision, with its inputs, at the decision point. The handover line used to
            // carry only the winner; why node X and not the reverse was unrecoverable from the logs.
            string why = migrating
                ? $"pinned migration to {(pinned == MigrationSide.Target ? "target" : "source")}"
                : heavyHome != null ? heavyWhy
                : ExplainHome(members, routed, model, travelling, homeIsTarget, loadSource, loadTarget);
            // Nothing left on the wrong side (a migration that has fully arrived within this pair).
            if (!members.Any(m => m.OnSource == homeIsTarget)) continue;
            // Partitioning §6.10/§7.1: a HandoverBlocked side is not a candidate, so a side holding a
            // blocked member is the home; when both sides hold one the co-location escalates as an
            // incident instead of retrying every round. Only the durable validation block decides the
            // direction - the transient failure backoff keeps its old skip-this-pass behavior, so the
            // home side stays stable across passes. A MIGRATION never flips its pin: it stalls, and the
            // pin expires back to cost-based homing.
            // #1243 (octahedron-1191, 2026-10-21): the flip used to need EVERY mover on the priced side
            // blocked. With blocked 445 alone on node-3 the flip carried 214 over to it; the next pass
            // priced node-0 as home again, the movers were now {214, 445}, 214 was not blocked, so it was
            // carried back - 172 round trips in 13 min, 4689 of the run's 10217 co-location moves.
            if (migrating)
            {
                if (members.Where(m => m.OnSource == homeIsTarget).All(m => hardBlocked(m.Partition.Id))) continue;
            }
            else
            {
                ulong[] blockedOnSource = [.. members.Where(m => m.OnSource && hardBlocked(m.Partition.Id)).Select(m => m.Partition.Id)];
                ulong[] blockedOnTarget = [.. members.Where(m => !m.OnSource && hardBlocked(m.Partition.Id)).Select(m => m.Partition.Id)];
                if (blockedOnSource.Length != 0 && blockedOnTarget.Length != 0)
                {
                    blockedIncident ??= new Move(members.First(m => m.OnSource == homeIsTarget && hardBlocked(m.Partition.Id)).Partition,
                        homeIsTarget, double.MaxValue, double.MaxValue, homeIsTarget, members.Length, "co-location-blocked");
                    continue;
                }
                if (blockedOnSource.Length + blockedOnTarget.Length != 0)
                {
                    bool blockedIsTarget = blockedOnTarget.Length != 0;
                    if (blockedIsTarget != homeIsTarget)
                        why += $"; HandoverBlocked {string.Join(",", blockedIsTarget ? blockedOnTarget : blockedOnSource)}"
                            + $" -> home {(blockedIsTarget ? "target" : "source")}";
                    homeIsTarget = blockedIsTarget;
                }
            }
            // T-0249 (w33 2026-09-26 01:46): a migration moves its component member by member, so while it
            // is in flight the component is split across two nodes and a client sees only the members on
            // its own node. Viktor's p3641 was carried to node-3 at 01:46:44 while the Earth base P517 -
            // overlapping it by 1.2 km, with his ship - was refused capture ("members still initializing")
            // every attempt until 01:47:48: his client lost every grid and flew free for 64 s. So:
            //  - STALL: while any member still to move is in handover backoff, move none of the others;
            //  - CLIENTS LAST: a member carrying a client moves only after every client-less member has
            //    arrived, so the window a client spends apart from its neighbours is one planner pass,
            //    not the whole transfer of the biggest member.
            // T-0296 r4: the members a migration still has to carry, for the loop's stalled-train rule (StalledMigrationPins).
            if (migrating) carried?.Invoke([.. members.Where(m => m.OnSource == homeIsTarget).Select(m => m.Partition.Id)]);
            if (migrating && members.Any(m => m.OnSource == homeIsTarget && blocked(m.Partition.Id)))
                continue;
            bool clientlessToMove = migrating
                && members.Any(m => m.OnSource == homeIsTarget && m.Partition.Clients.Length == 0);
            foreach ((PartitionInfo partition, bool onSource) in members)
            {
                if (onSource != homeIsTarget) continue;                      // already home
                if (blocked(partition.Id) || hardBlocked(partition.Id)) continue;
                if (clientlessToMove && partition.Clients.Length > 0) continue;
                // Urgency is distance to the nearest partition it is failing to be co-located with.
                double gap = double.MaxValue, rawGap = double.MaxValue;
                foreach ((PartitionInfo other, bool otherOnSource) in members)
                {
                    double candidate = EnvelopeGap(partition, other, envelope, model);
                    if (otherOnSource != onSource && candidate < gap)
                    {
                        gap = candidate;
                        rawGap = MinGap(partition, other);
                    }
                }
                if (gap == double.MaxValue)
                {
                    if (!migrating) continue;
                    // A migrating member with no cross-pair neighbour yet (the minority-first move off
                    // a fully co-located component): floor the urgency at the envelope, so any real
                    // cross-node violation - gap under the envelope by definition - always wins.
                    gap = envelope;
                    rawGap = envelope;
                }
                if (best is { } current && current.Gap <= gap) continue;
                best = new Move(partition, homeIsTarget, gap, rawGap, homeIsTarget, members.Length,
                    migrating ? "balance-migration" : "co-location", why);
            }
        }
        return best ?? blockedIncident;
    }

    /// <summary>One initiation of a global rebalance: pin this whole component to Target.</summary>
    /// <summary>Which of the three planners produced this move. It is the only thing that says
    /// what OldPeak == NewPeak means on a given line, which is otherwise unreadable: 173 of the 307
    /// pins on 2026-09-09 left the peak unchanged and nothing said why. NONE of the three kinds is
    /// required to lower the peak. A "load" plan is scored on OverloadReduction - the drop in total
    /// over-optimal load across all nodes - and deliberately moves secondary misplacement while the
    /// peak node sits at its atomic floor; a "client" plan is scored on client reduction; "idle" is
    /// the full-cluster optimizer's own sequence, each step measured against the running peak.
    /// (T-0106.) #1454: three kinds move around a HEAVY component (CostModel.HeavyClients) and are planned before every
    /// gate: "evacuate" moves another component off a heavy component's node, "heavy-split" moves the heavy component with
    /// fewer clients off a node holding two, "heavy-dedicate" moves a heavy component onto an empty node when
    /// evacuating its neighbours would move more clients than it carries.</summary>
    internal enum RebalanceKind { Load, Client, Idle, Evacuate, HeavySplit, HeavyDedicate }

    internal readonly record struct RebalancePlan(PartitionInfo[] Members, string Source, string Target,
        long ComponentLoad, long OldPeak, long NewPeak, double CostMs, double Excess,
        string LoadUnit = "PCU", string Explain = "", RebalanceKind Kind = RebalanceKind.Load,
        long OverloadReduction = 0, double MeanLoad = 0);

    /// <summary>One envelope-connected component as the placement optimizer sees it: an indivisible
    /// bag item with a home node, a weight and a price to move it anywhere else. Unmovable components
    /// (blocked, already migrating) still weigh on their node but are never reassigned. Load is the
    /// active metric's weight: measured sim cost (us/s) when every node reports it, PCU otherwise -
    /// see <see cref="ComponentLoad"/> in ChooseRebalancePlans.</summary>
    /// <para>#1454: <c>Heavy</c> marks a heavy component (never Movable); <c>Free</c> is "nothing but the balance rules keeps
    /// it in place" (not blocked, pinned, fast or in flight) and <c>Content</c> "carries PCU or a client", which is what
    /// the heavy moves (<see cref="PlanHeavyMoves"/>) read instead of Movable.</para>
    internal readonly record struct PlacementComponent(PartitionInfo[] Members, int Node, long Load,
        double CostMs, bool Movable, int Clients = 0, bool LoadUnit = true, bool Heavy = false, bool Free = false,
        bool Content = false);

    /// <summary>
    /// CLUSTER-SIM-COST: the balance load of one partition. The measured per-partition simulation
    /// cost (game-thread update time plus attributed physics, us per second - see the node's
    /// PartitionManager) when the whole pass supports it, else PCU. The two scales must never mix
    /// in one arithmetic: a 1,204-PCU parked Visitor outweighs a piloted 500-PCU tug in PCU and
    /// underweighs it ~50x in measured cost.
    /// </summary>
    private static long PartitionLoad(PartitionInfo partition, bool simLoad)
        => simLoad ? partition.SimLoadUs : partition.Pcu;

    /// <summary>
    /// The /partitions report samples SimulationRatio alongside its partition costs. Below 0.98,
    /// elapsed wall time per simulated second is a lower bound on the node's actual work. Charge
    /// the portion missing from the attributed partition sum to the node itself. Invalid or absent
    /// ratios leave the measured sum intact; PCU-only passes never mix in a time-based load.
    /// Cap an extreme reciprocal at 1e12 us/s to keep placement arithmetic in range.
    /// </summary>
    internal static long EffectiveNodeLoad(PartitionReport report, bool simLoad)
    {
        long measured = report.Partitions.Sum(p => PartitionLoad(p, simLoad));
        double speed = report.SimulationRatio;
        // T-0355: a node that owns nothing is dormant - it ticks at 4 Hz and reports a ratio near 0.07, which the
        // reciprocal below turned into 12.5-14 M us/s. The empty node then read as the PEAK of the cluster, nothing was
        // ever balanced or woken onto it, and a returning host's fresh nodes stayed empty while a loaded node ran at 0.52x.
        if (report.Partitions.Length == 0) return 0;
        if (!simLoad || !double.IsFinite(speed) || speed <= 0 || speed >= 0.98)
            return measured;
        long elapsed = (long)Math.Min(1_000_000.0 / speed, 1_000_000_000_000.0);
        return Math.Max(measured, elapsed);
    }

    /// <summary>SE-1134: each node's simulation ratios since its current run below 0.98 began, oldest first. Only the
    /// automatic pass reads and writes it.</summary>
    private readonly Dictionary<string, List<(DateTime At, double Ratio)>> _lowRatioRuns = new();

    /// <summary>
    /// SE-1134: the reports with each node's SimulationRatio as the planners should read it. A ratio below 0.98 that
    /// has lasted less than <paramref name="sustainSeconds"/> reads 1, so EffectiveNodeLoad charges the measured sum
    /// only; once it has lasted that long it reads as the highest (mildest) ratio of the last sustainSeconds, a lower
    /// bound the node really ran under for the whole window. One hitch is one or two low samples at a pass a second,
    /// a saturated node stays low. A healthy or unusable sample ends the run. 0 seconds returns the reports unchanged.
    /// </summary>
    internal static (T Node, PartitionReport Report)[] SustainRatios<T>((T Node, PartitionReport Report)[] reports,
        Func<T, string> name, Dictionary<string, List<(DateTime At, double Ratio)>> runs, double sustainSeconds, DateTime now)
    {
        if (sustainSeconds <= 0)
        {
            runs.Clear();
            return reports;
        }
        var sustained = new (T Node, PartitionReport Report)[reports.Length];
        for (int i = 0; i < reports.Length; i++)
        {
            (T node, PartitionReport report) = reports[i];
            double ratio = report.SimulationRatio;
            string key = name(node);
            if (!double.IsFinite(ratio) || ratio <= 0 || ratio >= 0.98)
            {
                runs.Remove(key);
                sustained[i] = reports[i];
                continue;
            }
            if (!runs.TryGetValue(key, out var run)) runs[key] = run = [];
            // A gap in the samples (a silent node, suspended passes) is no evidence the node stayed slow.
            if (run.Count != 0 && (now - run[^1].At).TotalSeconds > sustainSeconds) run.Clear();
            run.Add((now, ratio));
            double effective = 1;
            if ((now - run[0].At).TotalSeconds >= sustainSeconds)
            {
                // The window keeps the sample at or just before its start, so it always spans sustainSeconds.
                int first = run.FindLastIndex(x => (now - x.At).TotalSeconds >= sustainSeconds);
                run.RemoveRange(0, first);
                effective = run.Max(x => x.Ratio);
            }
            sustained[i] = (node, report with { SimulationRatio = effective });
        }
        return sustained;
    }

    /// <summary>
    /// Per-node total load, EMA-smoothed ACROSS PASSES, for T-0141's congestion term.
    ///
    /// WHY SMOOTHED. The raw sum is far too noisy to steer with. Per-partition simLoadUs is a 10 s
    /// EMA on the NODE with no smoothing at the gateway, and a partition that has just arrived
    /// reports a single raw sample, so every migration perturbs the very quantity that prices the
    /// next migration. Measured on the reverted first attempt: the raw pairwise ratio crossed its
    /// threshold on 53 of 258 consecutive snapshot transitions - 21%, about every fifth pass on a
    /// given pair. A price that flickers that fast steers nothing; it only adds noise to §7.5.
    ///
    /// One unit is one pass, not one second, because passes are what consume this. A node absent
    /// from a pass keeps its last value rather than decaying toward zero - absence means "no report
    /// this pass", not "idle", and decaying would quietly make a silent node look like a good home.
    /// </summary>
    private Dictionary<string, double> SmoothNodeLoads((ServedNode Node, PartitionReport Report)[] reports)
    {
        // Mixed fleets are not commensurable - PCU and measured sim-us are different units - so the
        // whole pass falls back to PCU unless every node reports measured cost, exactly as
        // ChooseBalanceMove does.
        bool simLoad = reports.All(x => x.Report.SimLoadSupported);
        Dictionary<string, double> pass = new();
        foreach ((ServedNode node, PartitionReport report) in reports)
        {
            double raw = EffectiveNodeLoad(report, simLoad);
            double smoothed = _nodeLoadEma.TryGetValue(node.Node, out double previous)
                ? previous + NodeLoadEmaAlpha * (raw - previous)
                : raw;
            _nodeLoadEma[node.Node] = smoothed;
            pass[node.Node] = smoothed;
        }
        return pass;
    }

    /// <summary>
    /// Viktor 2026-08-24: the approximate placement optimizer - the bin-packing view of the cluster
    /// (components are bag items, nodes are bags, items price to move between bags, no stacking
    /// order). Stochastic multi-start: the current assignment, one pure LPT pass, then jittered LPT
    /// restarts, each polished by relocate/swap local search. Objective is lexicographic: an
    /// assignment whose excess is inside <see cref="CostModel.BalanceRiskTolerance"/> is "good
    /// enough" and among good-enough assignments the CHEAPEST (least migration) wins; otherwise the
    /// lowest peak wins, cost breaking ties. Deterministic (fixed seed), so the same inputs plan the
    /// same moves on every pass. Exact optimality is explicitly not a goal - a near-optimal peak
    /// found cheaply beats an optimal one found never.
    /// </summary>
    internal static int[] OptimizePlacement(PlacementComponent[] components, int nodeCount, CostModel model,
        long[]? nodeClients = null, bool[]? closed = null)
    {
        // #1454: a node holding a heavy component takes no movable component (the heavy component gets the node to itself).
        bool Closed(int n) => closed?[n] == true;
        long[] fixedLoad = new long[nodeCount];
        long[] fixedClients = new long[nodeCount];
        foreach (PlacementComponent c in components)
            if (!c.Movable) { fixedLoad[c.Node] += c.Load; fixedClients[c.Node] += c.Clients; }
        int[] movable = Enumerable.Range(0, components.Length).Where(i => components[i].Movable).ToArray();

        (bool LoadGood, bool ClientGood, long[] Load, long[] Cl, double MoveCost) Score(int[] assign)
        {
            long[] load = (long[])fixedLoad.Clone();
            long[] cl = (long[])fixedClients.Clone();
            double cost = 0;
            foreach (int i in movable)
            {
                load[assign[i]] += components[i].Load;
                cl[assign[i]] += components[i].Clients;
                // The import stall lands on EVERYONE simulated by the receiving node, not just the
                // moving partition's own clients: w23 20:18, a 126k/20s migration dropped its
                // 8-client target to sim 0.02 for ~4 s. Weighting the price by the destination's
                // audience steers big movers toward client-quiet nodes when an alternative exists;
                // it never blocks a needed move (a lone candidate still wins).
                if (assign[i] != components[i].Node)
                    cost += components[i].CostMs * (1 + (nodeClients?[assign[i]] ?? 0));
            }
            long peak = load.Max();
            double meanOthers = (load.Sum() - peak) / (double)(nodeCount - 1);
            double excess = (peak - meanOthers) / Math.Max(meanOthers, 1.0);
            long clPeak = cl.Max();
            double clMeanOthers = (cl.Sum() - clPeak) / (double)(nodeCount - 1);
            double clExcess = (clPeak - clMeanOthers) / Math.Max(clMeanOthers, 1.0);
            // The load vector sorted descending IS the objective: minimizing it lexicographically
            // minimizes the peak, then the second-highest node, and so on - so SECONDARY imbalance
            // (a heavy node below an unmovable peak) is optimized too, which a bare-peak objective
            // ignores as "equal peak, why pay the move" (w23 18:18, an 82k cluster stranded on a
            // 177k node while the blob node read done). Churn protection is NOT this objective's
            // job - the per-node deviation gate in ChooseRebalancePlans decides whether the
            // improvement is worth acting on at all.
            Array.Sort(load);
            Array.Reverse(load);
            Array.Sort(cl);
            Array.Reverse(cl);
            return (excess <= model.BalanceRiskTolerance, clExcess <= model.BalanceRiskTolerance,
                load, cl, cost);
        }
        static int CompareSorted(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            return 0;
        }
        // Viktor 2026-08-24 evening: connected clients are load too - constant traffic and
        // occasional work - so they are a balance dimension of their own, SECONDARY to PCU and
        // quantized by the same single tunable: first bring PCU inside the tolerance, then bring
        // clients inside the tolerance (PCU differences within the tolerance are by definition
        // not worth caring about while doing so), then pay as little migration as possible.
        static bool Better((bool LoadGood, bool ClientGood, long[] Load, long[] Cl, double MoveCost) a,
            (bool LoadGood, bool ClientGood, long[] Load, long[] Cl, double MoveCost) b)
        {
            if (a.LoadGood != b.LoadGood) return a.LoadGood;
            if (!a.LoadGood)   // both beyond tolerance: load vector, then client vector, then cost
            {
                int order = CompareSorted(a.Load, b.Load);
                if (order != 0) return order < 0;
                order = CompareSorted(a.Cl, b.Cl);
                return order < 0 || (order == 0 && a.MoveCost < b.MoveCost);
            }
            if (a.ClientGood != b.ClientGood) return a.ClientGood;
            if (!a.ClientGood)   // PCU good, clients not: client vector, then cost, then PCU vector
            {
                int order = CompareSorted(a.Cl, b.Cl);
                if (order != 0) return order < 0;
                return a.MoveCost < b.MoveCost
                    || (a.MoveCost == b.MoveCost && CompareSorted(a.Load, b.Load) < 0);
            }
            // good enough on both: the cheapest wins, balance breaking the tie
            if (a.MoveCost != b.MoveCost) return a.MoveCost < b.MoveCost;
            int loadOrder = CompareSorted(a.Load, b.Load);
            return loadOrder < 0 || (loadOrder == 0 && CompareSorted(a.Cl, b.Cl) < 0);
        }

        int[] best = components.Select(c => c.Node).ToArray();
        var bestScore = Score(best);
        Random rng = new Random(20260824);
        const int Restarts = 64;
        for (int r = 0; r < Restarts; r++)
        {
            // Longest-processing-time greedy over a (jittered) weight order; restart 0 is pure LPT.
            int[] order = movable.OrderByDescending(i =>
                components[i].Load * (r == 0 ? 1.0 : 0.8 + 0.4 * rng.NextDouble())).ToArray();
            int[] assign = components.Select(c => c.Node).ToArray();
            long[] load = (long[])fixedLoad.Clone();
            foreach (int i in order)
            {
                // Least resulting load wins; the home node wins ties so a converged cluster plans
                // zero moves instead of shuffling equal alternatives.
                int target = Closed(components[i].Node) ? -1 : components[i].Node;
                for (int n = 0; n < nodeCount; n++)
                    if (!Closed(n) && (target < 0 || load[n] < load[target])) target = n;
                if (target < 0) target = components[i].Node;
                assign[i] = target;
                load[target] += components[i].Load;
            }
            // Local search: single relocations and pairwise swaps until a sweep improves nothing.
            for (bool improved = true; improved;)
            {
                improved = false;
                var score = Score(assign);
                foreach (int i in movable)
                {
                    int was = assign[i];
                    for (int n = 0; n < nodeCount; n++)
                    {
                        if (n == was || Closed(n)) continue;
                        assign[i] = n;
                        var moved = Score(assign);
                        if (Better(moved, score)) { score = moved; was = n; improved = true; }
                        else assign[i] = was;
                    }
                }
                for (int a = 0; a < movable.Length && !improved; a++)
                    for (int b = a + 1; b < movable.Length; b++)
                    {
                        int i = movable[a], j = movable[b];
                        if (assign[i] == assign[j] || Closed(assign[i]) || Closed(assign[j])) continue;
                        (assign[i], assign[j]) = (assign[j], assign[i]);
                        var swapped = Score(assign);
                        if (Better(swapped, score)) { score = swapped; improved = true; break; }
                        (assign[i], assign[j]) = (assign[j], assign[i]);
                    }
            }
            var candidate = Score(assign);
            if (Better(candidate, bestScore)) { bestScore = candidate; best = assign; }
        }
        return best;
    }

    /// <summary>
    /// Docs/GlobalLoadBalancing.md - Viktor's 2026-08-24 design. Packs PCU into the nodes, each around
    /// the cluster average, with NO hard limits of any kind. The most loaded node's relative excess over
    /// the mean of the others is the risk proxy for simspeed drops; while it is at or below the single
    /// tunable (<see cref="CostModel.BalanceRiskTolerance"/>) the assignment is good enough and nothing
    /// moves, because the lag of the moves would not warrant the gain. Above it, the candidate that
    /// lowers the cluster's peak PCU the most PER UNIT OF ESTIMATED MIGRATION LAG wins - so small
    /// components move first and a giant moves only when nothing cheaper reaches the balance, emergent
    /// from pricing rather than from a cap. The unit is the whole envelope component (the co-location
    /// invariant); execution is by pinning (see <see cref="MigrationSide"/>), not a one-shot transfer.
    /// Returns null when converged, or when no candidate strictly lowers the peak (the atomic-blob
    /// optimum: a world whose mass is one component cannot be balanced by any mover).
    /// </summary>
    internal static RebalancePlan? ChooseGlobalRebalance(
        (PartitionReport Report, string Node)[] nodes, CostModel model, HashSet<ulong> routed,
        Func<ulong, bool> blocked, Func<ulong, bool>? validationBlocked = null,
        Func<ulong, bool>? alreadyPinned = null)
    {
        RebalancePlan[] plans = ChooseRebalancePlans(nodes, model, routed, blocked, validationBlocked,
            alreadyPinned, fullPlan: false);
        return plans.Length == 0 ? null : plans[0];
    }

    /// <summary>
    /// The optimizer-driven planner behind both balance modes. It prices the whole cluster through
    /// <see cref="OptimizePlacement"/> and stops on either of two gates sharing the ONE tunable:
    /// current excess inside the tolerance (balanced enough), or current excess within the tolerance
    /// OF THE ACHIEVABLE OPTIMUM's excess (as good as this world's atomic components permit - which
    /// is what stops the balancer from shedding 1204-PCU pebbles forever off a node whose ~420k
    /// atomic blob keeps the excess above tolerance no matter what moves, w23 17:01). With
    /// <paramref name="fullPlan"/> the entire optimal reassignment returns at once - the idle-cluster
    /// / startup mode, where nobody is playing and pacing protects no one; otherwise the single plan
    /// move that best reduces total over-optimal load per unit of migration lag, the paced live mode.
    /// </summary>
    /// <summary>
    /// One balance evaluation's published numbers: current excess, the OPTIMAL assignment's excess,
    /// and the worst per-node deviation from optimal. High excess with near-zero deviation is
    /// GAMEPLAY CONCENTRATION (players gathered in one place - the cluster working as intended,
    /// w23 20:14: twelve pilots at the base drove excess 1.41->3.76 with nothing misplaced); high
    /// deviation is a genuine placement backlog. The value alone cannot tell them apart - this can.
    /// </summary>
    internal sealed record BalanceDiagnostics(DateTime EvaluatedUtc, double Excess,
        double AchievableExcess, double WorstDeviation, double ClientExcess,
        double WorstClientDeviation, int MovesPlanned, string? PauseReason);
    private volatile BalanceDiagnostics? _balanceDiagnostics;

    internal static RebalancePlan[] ChooseRebalancePlans(
        (PartitionReport Report, string Node)[] nodes, CostModel model, HashSet<ulong> routed,
        Func<ulong, bool> blocked, Func<ulong, bool>? validationBlocked = null,
        Func<ulong, bool>? alreadyPinned = null, bool fullPlan = false,
        IReadOnlySet<string>? busyNodes = null, double? gate = null, IReadOnlySet<string>? overBudget = null,
        IReadOnlySet<ulong>? heavy = null, Func<ulong, bool>? heavyCooling = null)
        => ChooseRebalancePlans(nodes, model, routed, blocked, validationBlocked, alreadyPinned,
            fullPlan, busyNodes, out _, gate, overBudget, heavy, heavyCooling);

    internal static RebalancePlan[] ChooseRebalancePlans(
        (PartitionReport Report, string Node)[] nodes, CostModel model, HashSet<ulong> routed,
        Func<ulong, bool> blocked, Func<ulong, bool>? validationBlocked,
        Func<ulong, bool>? alreadyPinned, bool fullPlan, IReadOnlySet<string>? busyNodes,
        out (double Excess, double AchievableExcess, double WorstDeviation,
            double ClientExcess, double WorstClientDeviation, string Explain,
            int MinGainSkipped, int BudgetSkipped, long FloorHeldPeak, int PeakDropSkipped) diagnostics,
        double? gate = null, IReadOnlySet<string>? overBudget = null,
        IReadOnlySet<ulong>? heavy = null, Func<ulong, bool>? heavyCooling = null)
    {
        // #1454: heavy (HeavyMembers: the member ids of the heavy units) and heavyCooling (a heavy unit moved recently)
        // come from the caller's latch, so the planner stays stateless; null plans exactly as before.
        // T-0393: the caller's hysteresis state picks the threshold of both tolerance gates below (BalancePace:
        // the tolerance while idle, the low water while balancing); null is the tolerance, as before. The planner
        // itself stays stateless. overBudget names the targets out of their move budget (BalancePace.OverBudget).
        double gateThreshold = gate ?? model.BalanceRiskTolerance;
        int minGainSkipped = 0, budgetSkipped = 0, peakDropSkipped = 0;
        diagnostics = (0, 0, 0, 0, 0, "", 0, 0, 0, 0);
        int inFlightCount = 0, pinnedCount = 0, blockedCount = 0, emptyCount = 0, heavyCount = 0;
        if (nodes.Length < 2) return [];
        // CLUSTER-SIM-COST: measured sim cost is the load dimension when every node reports it;
        // any pre-field node in the pass drops the whole pass back to PCU (units must not mix).
        bool simLoad = nodes.All(n => n.Report.SimLoadSupported);
        string loadUnit = simLoad ? "sim-us/s" : "PCU";
        List<PlacementComponent> components = [];
        // T-0074 (2026-09-08): a component carrying a routed client is not a placement unit for
        // the LOAD dimension either. Its load is that pilot's ship, which the next leg carries
        // away within ~100 s and co-location re-homes wherever it lands (31 of 154 client-bearing
        // balance moves were pulled straight back in 6.7 h of w29), and the route switch it costs
        // the client is not free: 7 of the 13 AI legs that stalled on 2026-09-08 began within
        // 30 s of a balance-migration handover of the pilot's own partition, against 1 of the 74
        // legs that flew - the route the client hands its AI block never reaches the new owner.
        // The in-flight rule above cannot see it: the ship is stationary at its checkpoint when
        // the pin lands. Client-bearing components move only when the CLIENT dimension is what is
        // out of balance (that is what they are the unit of); they weigh on their node either way.
        long[] nodeClientLoad = nodes
            .Select(n => (long)n.Report.Partitions.Sum(p => ClientCount(p, routed))).ToArray();
        // "Out of balance" here is THIS node holding more clients than the even split (2 on one
        // of two nodes is; 1 next to an empty node is not) - the ratio gate used for the load
        // dimension reads a single pilot next to an empty node as a 100 % excess and would leave
        // nothing client-bound at all. Per node, not cluster-wide: w29 09:08-09:18 ran 5/1/4/2/0
        // clients, and a cluster-wide gate released every pilot on every node for it.
        long evenSplit = (nodeClientLoad.Sum() + nodes.Length - 1) / nodes.Length;
        int clientBoundCount = 0;
        for (int n = 0; n < nodes.Length; n++)
        {
            PartitionReport report = nodes[n].Report;
            // Unattributed work is fixed to this node, not a cost that moves with a partition.
            // Keep it in the same sim-us/s dimension as every movable component.
            long measured = report.Partitions.Sum(p => PartitionLoad(p, simLoad));
            long overhead = EffectiveNodeLoad(report, simLoad) - measured;
            if (overhead > 0)
                components.Add(new PlacementComponent([], n, overhead, 0, false));
            (PartitionInfo Partition, bool OnSource)[] all =
                report.Partitions.Select(p => (p, true)).ToArray();
            // #1304: balance units, not envelope components. On octahedron-1191 the lane chains link at 3-4 km (over the
            // node's merge distance D, under the 4000 m envelope) into 21-24-partition components of 230-290 s estimated
            // lag, which the planner could only move whole and so never moved.
            int[] labels = Components(all, report.SyncDistance + report.Margin, model, units: true);
            foreach (IGrouping<int, int> group in Enumerable.Range(0, all.Length).GroupBy(i => labels[i]))
            {
                PartitionInfo[] members = group.Select(i => all[i].Partition).ToArray();
                // A component already migrating, in backoff, or HandoverBlocked still weighs on its
                // node but is not reassignable this pass; it stays fully moveable later.
                // T-0292: a fast partition stays with its owner until it slows down - never migrated.
                bool movable = !members.Any(m => blocked(m.Id) || (validationBlocked?.Invoke(m.Id) ?? false)
                    || (alreadyPinned?.Invoke(m.Id) ?? false) || m.Fast || m.RemoteUsers is { Length: > 0 });
                if (members.Any(m => m.Fast)) FastPartitionCounters.Migration();
                // T-0074: a partition in flight is not a placement unit. Its load is the flying
                // ship's own physics, which leaves the node with the ship inside the time a migration
                // takes (a 10 km leg is ~100 s), and co-location re-homes it wherever it lands - so
                // relocating it balances nothing and costs its client a route switch (observed: the
                // pilot's seat or its view of the ship). w29 2026-09-07 14:29-21:29: 261 pins, every
                // one a single 4-14k sim-us/s partition at an unchanged peak, 154 carrying a client,
                // 31 pulled straight back by co-location within 3 min. It still weighs on its node;
                // the optimizer plans around it exactly as around a pinned component.
                // T-0141 (2026-09-15): only a SMALL component is exempt - the ship itself, not the
                // 31-member lattice cluster it is leaving (CostModel.InFlightMaxMembers).
                bool inFlight = members.Any(m => m.MaxMemberSpeed > InFlightSpeed)
                    && (model.InFlightMaxMembers <= 0 || members.Sum(m => m.MemberCount) <= model.InFlightMaxMembers);
                if (inFlight) inFlightCount++;
                if (members.Any(m => alreadyPinned?.Invoke(m.Id) ?? false)) pinnedCount++;
                else if (!movable) blockedCount++;
                long pcu = members.Sum(m => (long)m.Pcu);
                long componentLoad = members.Sum(m => PartitionLoad(m, simLoad));
                int clients = members.Sum(m => ClientCount(m, routed));
                // T-0385: the client rule is for the pilot's ship, not for the base the pilots
                // stand on - a client-bearing component above ClientBoundMaxMembers stays a load
                // unit (its load does not leave with any one client).
                bool isLoadUnit = clients == 0 || (model.ClientBoundMaxMembers > 0
                    && members.Sum(m => m.MemberCount) > model.ClientBoundMaxMembers);
                bool clientBound = !isLoadUnit && nodeClientLoad[n] <= evenSplit;
                if (clientBound && movable && !inFlight) clientBoundCount++;
                double cost = members.Sum(m => model.FreezeMs(m.EstimatedSnapshotBytes, m.MemberCount)
                    + model.GammaPerSwitch * ClientCount(m, routed));
                // The pebble gate stays on PCU: content-bearing is what "worth moving at all"
                // means, and a parked partition's measured load is legitimately ~0.
                if (pcu == 0 && clients == 0) emptyCount++;
                // #1454: a heavy unit weighs on its node and is never the optimizer's to move. Its own clients count too: mid-handover
                // both nodes report the moving partition (1191e 18:12:57Z: hub 2332 with 36 clients on node-0, a 0-client copy
                // on node-3), and the copy must not make its node a heavy node.
                bool isHeavy = heavy != null && members.Any(m => heavy.Contains(m.Id)) && model.IsHeavy(clients, latched: true);
                if (isHeavy) heavyCount++;
                components.Add(new PlacementComponent(members, n, componentLoad, cost,
                    movable && !inFlight && !clientBound && (pcu > 0 || clients > 0) && !isHeavy, clients, isLoadUnit,
                    isHeavy, movable && !inFlight, pcu > 0 || clients > 0));
            }
        }
        long[] load = new long[nodes.Length];
        long[] clLoad = new long[nodes.Length];
        foreach (PlacementComponent c in components) { load[c.Node] += c.Load; clLoad[c.Node] += c.Clients; }
        long oldPeak = load.Max();
        double meanOthers = (load.Sum() - oldPeak) / (double)(nodes.Length - 1);
        double excess = (oldPeak - meanOthers) / Math.Max(meanOthers, 1.0);
        // Clients are a balance dimension of their own (Viktor 2026-08-24: constant traffic and
        // occasional work) - secondary to PCU but gated by the SAME single tunable, so a
        // PCU-balanced cluster with all the players piled on one node is still a backlog.
        long clPeak = clLoad.Max();
        double clMeanOthers = (clLoad.Sum() - clPeak) / (double)(nodes.Length - 1);
        double clientExcess = (clPeak - clMeanOthers) / Math.Max(clMeanOthers, 1.0);
        // T-0073: the inputs of every balance decision, printed with the pin and with a decline.
        string Explain(long[]? optimal) => "loads [" + string.Join(" ", nodes.Select((n, i) => $"{n.Node}={load[i]}"))
            + "]" + (optimal == null ? "" : " optimal [" + string.Join(" ", nodes.Select((n, i) => $"{n.Node}={optimal[i]}")) + "]")
            + $" {loadUnit}; components {components.Count}, not movable {components.Count(c => !c.Movable)}"
            + $" (in flight {inFlightCount}, with clients {clientBoundCount}, pinned {pinnedCount},"
            + $" blocked {blockedCount}, empty {emptyCount}" + (heavyCount != 0 ? $", heavy {heavyCount}" : "") + ")";
        // #1454: the heavy moves pass no gate - the tolerance, the floor and the peak-drop guard are about whether a
        // relative imbalance is worth a move, and a heavy node's neighbours leave whatever the balance reads (case 4).
        // They engage their nodes like any paced plan, and no other plan targets a heavy node (closed below).
        List<RebalancePlan> heavyPlans = [];
        bool[]? closed = null;
        if (heavyCount != 0)
        {
            closed = new bool[nodes.Length];
            foreach (PlacementComponent c in components) if (c.Heavy) closed[c.Node] = true;
            if (!fullPlan)
            {
                heavyPlans = PlanHeavyMoves(components, nodes, load, busyNodes, heavyCooling, loadUnit, excess, Explain(null), out var engaged);
                foreach (RebalancePlan plan in heavyPlans.Where(p => p.Kind != RebalanceKind.Evacuate))
                    closed[Array.FindIndex(nodes, x => x.Node == plan.Target)] = true;
                busyNodes = engaged;
            }
        }
        if (excess <= gateThreshold && clientExcess <= gateThreshold)
        {
            diagnostics = (excess, excess, 0, clientExcess, 0, Explain(null), 0, 0, 0, 0);
            return [.. heavyPlans];
        }

        int[] assign = OptimizePlacement(components.ToArray(), nodes.Length, model, nodeClientLoad, closed);
        long[] optLoad = new long[nodes.Length];
        long[] optClients = new long[nodes.Length];
        for (int i = 0; i < components.Count; i++)
        {
            optLoad[assign[i]] += components[i].Load;
            optClients[assign[i]] += components[i].Clients;
        }
        long optPeak = optLoad.Max();
        double optMeanOthers = (optLoad.Sum() - optPeak) / (double)(nodes.Length - 1);
        // The achievable-optimum gate: idle when no node is loaded meaningfully beyond what the best
        // reachable assignment would put on it - "meaningfully" being the same single tunable,
        // scaled by the optimal assignment's mean non-peak load. Comparing per NODE rather than by
        // the peak excess catches SECONDARY imbalance the peak metric is blind to (w23 18:18: the
        // blob node at its atomic floor read "done" while a rotation-stranded 82k cluster sat on a
        // 177k node next to a 2.4k one), and still stops the pebble churn at the floor: a peak node
        // whose only surplus over its optimal load is a pebble is idle by the same arithmetic.
        double deviationUnit = Math.Max(optMeanOthers, 1.0);
        double worstDeviation = Enumerable.Range(0, nodes.Length)
            .Max(n => (load[n] - optLoad[n]) / deviationUnit);
        long optClPeak = optClients.Max();
        double clDeviationUnit = Math.Max((optClients.Sum() - optClPeak) / (double)(nodes.Length - 1), 1.0);
        double worstClientDeviation = Enumerable.Range(0, nodes.Length)
            .Max(n => (clLoad[n] - optClients[n]) / clDeviationUnit);
        string explain = Explain(optLoad);
        diagnostics = (excess, (optPeak - optMeanOthers) / Math.Max(optMeanOthers, 1.0), worstDeviation,
            clientExcess, worstClientDeviation, explain, 0, 0, 0, 0);
        if (worstDeviation <= gateThreshold
            && worstClientDeviation <= gateThreshold) return [.. heavyPlans];
        // T-0409: the load floor. Below it no node is short of sim time, and a paced move costs its pilots lag for no
        // sim gain, so the paced planner idles. The diagnostics above stay complete (the deviations feed /diag and the
        // pace hysteresis); FloorHeldPeak tells the caller why nothing was planned.
        if (!fullPlan && simLoad && model.BalanceMinPeakLoad > 0 && oldPeak < model.BalanceMinPeakLoad)
        {
            diagnostics.FloorHeldPeak = oldPeak;
            return [.. heavyPlans];
        }
        // T-0393: the pin log's gain = overload reduction / mean node load, the unit MinGainFraction is in.
        double meanLoad = load.Sum() / (double)nodes.Length;

        var moves = Enumerable.Range(0, components.Count)
            .Where(i => assign[i] != components[i].Node)
            .OrderByDescending(i => components[i].Load)
            .ToArray();
        if (fullPlan)
        {
            // Report the projected peak cumulatively, big movers first, so the pin log reads as the
            // trajectory the plan expects the cluster to follow.
            List<RebalancePlan> plans = [];
            long[] running = (long[])load.Clone();
            foreach (int i in moves)
            {
                long before = running.Max();
                running[components[i].Node] -= components[i].Load;
                running[assign[i]] += components[i].Load;
                plans.Add(new RebalancePlan(components[i].Members, nodes[components[i].Node].Node,
                    nodes[assign[i]].Node, components[i].Load, before, running.Max(),
                    components[i].CostMs, excess, loadUnit, explain, RebalanceKind.Idle,
                    before - running.Max(), meanLoad));
            }
            return plans.ToArray();
        }
        // Paced live mode, per-node-disjoint (Viktor 2026-08-24: Option 1 of the crumb-pace
        // finding): each pass may initiate SEVERAL migrations, as long as every node participates
        // in at most ONE - as source or as target, counting migrations already in flight
        // (busyNodes). No node ever pays two concurrent import/freeze stalls, and the drain rate
        // scales with the cluster instead of being fixed at one component per cooldown. Within the
        // pass, candidates are ranked by total over-optimal load reduced per unit of estimated
        // migration lag. Any over-loaded SOURCE qualifies, not only the peak node: with the peak
        // node at its atomic floor the remaining misplacement is all secondary (w23 18:18), and a
        // peak-only mover leaves it stranded forever. No selected move may raise the cluster peak;
        // node-disjointness makes the guarantee compositional (a source only sheds, a target's
        // resulting load was individually checked against the pre-pass peak).
        long Overload(long[] loads) => Enumerable.Range(0, nodes.Length)
            .Sum(n => Math.Max(0, loads[n] - optLoad[n]));
        long ClientOverload(long[] cls) => Enumerable.Range(0, nodes.Length)
            .Sum(n => Math.Max(0, cls[n] - optClients[n]));
        long oldOverload = Overload(load);
        long oldClientOverload = ClientOverload(clLoad);
        // T-0393 knob 2: the minimum gains, in each tier's own unit (CostModel.MinGainFraction). At 0 both are 0,
        // and every candidate that reaches a check below already has a reduction > 0, so nothing changes.
        double minLoadGain = model.MinGainFraction * meanLoad;
        double minClientGain = model.MinGainFraction * (clLoad.Sum() / (double)nodes.Length);
        // T-0393 knob 5: the move budget applies only below its deviation threshold - a real overload is served at once.
        bool budgeted = overBudget is { Count: > 0 }
            && Math.Max(worstDeviation, worstClientDeviation) < model.BudgetUnderDeviation;
        List<(int Tier, double Score, long TargetLoad, RebalancePlan Plan)> candidates = [];
        foreach (int i in moves)
        {
            int s = components[i].Node, t = assign[i];
            if (busyNodes != null && (busyNodes.Contains(nodes[s].Node) || busyNodes.Contains(nodes[t].Node)))
                continue;
            if (closed?[t] == true) continue;
            long[] adjusted = (long[])load.Clone();
            adjusted[s] -= components[i].Load;
            adjusted[t] += components[i].Load;
            long newPeak = adjusted.Max();
            // T-0409 follow-up: the peak-drop guard (CostModel.BalanceMinPeakDrop). A paced sim-load move of either tier
            // must lower the cluster peak by the margin; at 0 nothing is skipped.
            if (simLoad && model.BalanceMinPeakDrop > 0 && newPeak > oldPeak * (1 - model.BalanceMinPeakDrop))
            {
                peakDropSkipped++;
                continue;
            }
            long reduction = oldOverload - Overload(adjusted);
            // Same audience weighting as the optimizer in both tiers: the stall is experienced by
            // every client on the receiving node.
            // A component carrying a client is only ever a CLIENT move (tier 2): its load is the
            // pilot's ship and leaves with it (T-0074), so it never pays for a route switch with
            // load; it moves when it evens the client split and the load picture tolerates it.
            // T-0393: a load move whose gain is inside the noise does not qualify for tier 1 (it may still be a client move).
            bool belowGain = components[i].LoadUnit && reduction > 0 && newPeak <= oldPeak && reduction < minLoadGain;
            if (!components[i].LoadUnit || reduction <= 0 || newPeak > oldPeak || belowGain)   // tier 1: never trade peak for spread
            {
                // Tier 2 - a move that helps only the CLIENT dimension (Viktor 2026-08-24: clients
                // are load too). Eligible while it strictly reduces client over-optimal load and
                // leaves the PCU picture good enough: the peak not raised, or the resulting excess
                // still inside the one tunable (PCU changes within the tolerance are not worth
                // caring about while players are piled up).
                long[] adjustedCl = (long[])clLoad.Clone();
                adjustedCl[s] -= components[i].Clients;
                adjustedCl[t] += components[i].Clients;
                long clientReduction = oldClientOverload - ClientOverload(adjustedCl);
                if (clientReduction <= 0) { if (belowGain) minGainSkipped++; continue; }
                if (clientReduction < minClientGain) { minGainSkipped++; continue; }
                // SE-1134: its players sit through the whole estimated lag, so the load picture has to gain in proportion.
                if (model.ClientMinGainPerLagSecond > 0
                    && reduction / Math.Max(meanLoad, 1.0) < model.ClientMinGainPerLagSecond * components[i].CostMs / 1000)
                { minGainSkipped++; continue; }
                double adjMeanOthers = (adjusted.Sum() - newPeak) / (double)(nodes.Length - 1);
                if (newPeak > oldPeak
                    && (newPeak - adjMeanOthers) / Math.Max(adjMeanOthers, 1.0)
                        > model.BalanceRiskTolerance) { if (belowGain) minGainSkipped++; continue; }
                if (budgeted && overBudget!.Contains(nodes[t].Node)) { budgetSkipped++; continue; }
                // T-0393 knob 1: the fixed hitch price joins the estimated lag in the score (0 adds exactly nothing).
                double clScore = clientReduction / (1.0 + (components[i].CostMs + model.HitchMsPerMove) * (1 + nodeClientLoad[t]));
                candidates.Add((2, clScore, load[t], new RebalancePlan(components[i].Members,
                    nodes[s].Node, nodes[t].Node, components[i].Load, oldPeak, newPeak,
                    components[i].CostMs, excess, loadUnit, explain, RebalanceKind.Client, reduction, meanLoad)));
                continue;
            }
            if (budgeted && overBudget!.Contains(nodes[t].Node)) { budgetSkipped++; continue; }
            double score = reduction / (1.0 + (components[i].CostMs + model.HitchMsPerMove) * (1 + nodeClientLoad[t]));
            candidates.Add((1, score, load[t], new RebalancePlan(components[i].Members,
                nodes[s].Node, nodes[t].Node, components[i].Load, oldPeak, newPeak,
                components[i].CostMs, excess, loadUnit, explain, RebalanceKind.Load, reduction, meanLoad)));
        }
        diagnostics.MinGainSkipped = minGainSkipped;
        diagnostics.BudgetSkipped = budgetSkipped;
        diagnostics.PeakDropSkipped = peakDropSkipped;
        // PCU-reducing moves rank above pure client-rebalancing moves, so on any node pair the
        // primary dimension always drains first; a client move fills only node pairs no PCU move
        // wants this pass. Equal score prefers the emptier target, which maximizes headroom and,
        // with hot spares in play, the youngest serving node's residence time.
        List<RebalancePlan> selected = [];
        HashSet<string> used = busyNodes != null ? [.. busyNodes] : [];
        foreach (var c in candidates
                     .OrderBy(c => c.Tier).ThenByDescending(c => c.Score).ThenBy(c => c.TargetLoad))
        {
            if (!used.Add(c.Plan.Source)) continue;
            if (!used.Add(c.Plan.Target)) { used.Remove(c.Plan.Source); continue; }
            selected.Add(c.Plan);
        }
        return [.. heavyPlans, .. selected];
    }

    /// <summary>The §7.5 price of moving one partition: its snapshot, its freeze, its client switches.</summary>
    private static double MoveCost(CostModel model, PartitionInfo partition, HashSet<ulong> routed)
        => model.Cost(partition.EstimatedSnapshotBytes, ClientCount(partition, routed));

    private void LogBalancePin(RebalancePlan plan, CostModel model)
        => Console.WriteLine($"Automatic balance migration pinned: component of {plan.Members.Length}"
            + $" partition(s), kind {plan.Kind switch { RebalanceKind.HeavySplit => "heavy-split", RebalanceKind.HeavyDedicate => "heavy-dedicate", var k => k.ToString().ToLowerInvariant() }},"
            + $" load {plan.ComponentLoad} {plan.LoadUnit}, {plan.Source}->{plan.Target},"
            + $" peak load {plan.OldPeak}->{plan.NewPeak} {plan.LoadUnit}"
            // The quantity the load tier is actually SCORED on, and it is not the peak: reduction
            // is oldOverload - Overload(adjusted), the drop in total over-optimal load summed
            // across nodes. A load plan that leaves the peak untouched while shedding secondary
            // misplacement is the DESIGNED behaviour - "any over-loaded SOURCE qualifies, not only
            // the peak node: with the peak node at its atomic floor the remaining misplacement is
            // all secondary, and a peak-only mover leaves it stranded forever". An earlier version
            // of this line flagged those as "(NO PEAK REDUCTION)", which named the deliberate case
            // as a defect; printing the real score instead makes the plan auditable without
            // asserting anything about it.
            + $", overload reduction {plan.OverloadReduction} {plan.LoadUnit}"
            + $", estimated migration lag {plan.CostMs:F0} ms,"
            + $" excess {plan.Excess:F2} vs tolerance {model.BalanceRiskTolerance:F2};"
            + $" members [{string.Join(" ", plan.Members.Take(8).Select(m => m.Id))}{(plan.Members.Length > 8 ? " ..." : "")}]; {plan.Explain}"
            // T-0393: the gain in MinGainFraction's unit (overload reduction / mean node load) and, with a budget
            // configured, how much of its target's hourly budget the pin leaves used. Appended last: the analysis
            // scripts read the fields above by position.
            + $"; gain={(plan.MeanLoad > 0 ? (plan.OverloadReduction / plan.MeanLoad).ToString("F3") : "n/a")}"
            + (model.BudgetMovesPerNodeHour > 0
                ? $", target budget {_balancePace.TargetPinsLastHour(DateTime.UtcNow).GetValueOrDefault(plan.Target)}/{model.BudgetMovesPerNodeHour} per hour"
                    + $" (applies below deviation {model.BudgetUnderDeviation:F2})"
                : ""));

    private void PinMigration(PartitionInfo[] members, string target)
    {
        lock (_balanceMigrations)
            foreach (PartitionInfo member in members)
                _balanceMigrations[member.Id] = (target, DateTime.UtcNow.AddSeconds(MigrationPinSeconds));
    }

    /// <summary>
    /// Every node currently engaged in an unexpired balance migration - the pin's target, plus
    /// the node still reporting the pinned partition (its source). The paced planner keeps these
    /// out of the next pass's per-node-disjoint move set, so no node ever runs two concurrent
    /// migrations even across passes.
    /// </summary>
    private HashSet<string> BusyMigrationNodes((ServedNode Node, PartitionReport Report)[] reports)
    {
        Dictionary<ulong, string> owner = [];
        foreach ((ServedNode node, PartitionReport report) in reports)
            foreach (PartitionInfo partition in report.Partitions)
                owner[partition.Id] = node.Node;
        HashSet<string> busy = [];
        lock (_balanceMigrations)
            foreach ((ulong partition, (string target, DateTime expires)) in _balanceMigrations)
            {
                if (expires < DateTime.UtcNow) continue;
                busy.Add(target);
                if (owner.TryGetValue(partition, out string? source)) busy.Add(source);
            }
        return busy;
    }

    private string? MigrationTarget(ulong partition)
    {
        lock (_balanceMigrations)
        {
            if (!_balanceMigrations.TryGetValue(partition, out (string Target, DateTime Expires) pin)) return null;
            if (pin.Expires < DateTime.UtcNow)
            {
                _balanceMigrations.Remove(partition);
                return null;
            }
            // T-0235: a pin made before a drain began must not keep carrying members into the node.
            if (_drainSources.TryGetValue(pin.Target, out DateTime drained)
                && DateTime.UtcNow - drained < TimeSpan.FromSeconds(RotationSettlingSeconds))
            {
                _balanceMigrations.Remove(partition);
                Console.WriteLine($"Dropped balance migration pin of partition {partition}: its target {pin.Target} is being drained (operator handover)");
                return null;
            }
            return pin.Target;
        }
    }

    private bool MigrationPinned(ulong partition) => MigrationTarget(partition) != null;

    /// <summary>
    /// T-0296 r3: why a pull demand of a partition pinned by a balance migration is refused, or null. The pull
    /// handler never asked about the pin and only the automatic loop releases one, so the pin survived the pull: on
    /// the next pass the pair (demander, pin target) saw the pulled partition as a migration still to carry and moved
    /// it off the demanding node again, one second after the pull was answered "accepted". A pull TO the pin's own
    /// target is the migration's move and goes ahead.
    /// </summary>
    internal static string? PullPinRefusal(string? pinTarget, string pullTarget)
        => pinTarget == null || pinTarget == pullTarget ? null
            : $"partition is in a balance migration to {pinTarget}; it stays with its group until that has arrived";

    /// <summary>T-0235: record the node an operator /handover drains (its control endpoint, as the
    /// rotator passes it, resolved to the registered node).</summary>
    private void NoteDrainSource(string sourceControl)
    {
        string wanted = StripScheme(sourceControl);
        string? node = _registry == null ? null : GatewayRegistry.LiveFirst(_registry.Nodes()
            .Where(x => x.ControlEndpoint != null && StripScheme(x.ControlEndpoint) == wanted))?.Node;
        if (node != null)
            _drainSources[node] = DateTime.UtcNow;
    }

    private string[] DrainingNodes()
        => RecentDrainSources(_drainSources, DateTime.UtcNow, TimeSpan.FromSeconds(RotationSettlingSeconds));

    internal static string[] RecentDrainSources(IEnumerable<KeyValuePair<string, DateTime>> sources, DateTime now, TimeSpan window)
        => [.. sources.Where(x => now - x.Value < window).Select(x => x.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A member of a pinned migration completed a handover: renew every sibling pin bound for the same
    /// target (progress-based sliding expiry) and release the member that arrived on its target.
    /// </summary>
    private void OnMigrationProgress(ulong partition, string arrivedOn)
    {
        lock (_balanceMigrations)
        {
            if (!_balanceMigrations.TryGetValue(partition, out (string Target, DateTime Expires) pin)) return;
            // Renew EVERY pin, not just the arrived member's target group: an idle-cluster full plan
            // pins trains toward several targets at once, and the one-transfer-per-pass loop can spend
            // longer than the pin lifetime on one train before touching another. The expiry guards a
            // DEAD migration system reverting to cost-based homing; any progress proves it alive.
            // T-0296 r4: except the pins of a train whose own mover keeps failing (see StalledMigrationPins).
            RenewMigrationPins(_balanceMigrations, DateTime.UtcNow, MigrationPinSeconds,
                StalledMigrationPins(_migrationTrains, RecentlyFailedHandover));
            if (pin.Target == arrivedOn) _balanceMigrations.Remove(partition);
        }
    }

    /// <summary>The members still to move of every pinned component the last pass saw (ChooseMove's <c>carried</c>).
    /// Written by the automatic loop, read under <c>_rotation</c> like every caller of OnMigrationProgress.</summary>
    private ulong[][] _migrationTrains = [];

    /// <summary>A handover of this partition failed or was deferred and has not succeeded since; a record older
    /// than a pin's lifetime past its retry time is history (records are only cleared by a success).</summary>
    private bool RecentlyFailedHandover(ulong partition)
    {
        lock (_handoverBackoff)
            return _handoverBackoff.TryGetValue(partition, out var state)
                && DateTime.UtcNow < state.NextAttempt.AddSeconds(MigrationPinSeconds);
    }

    /// <summary>
    /// T-0296 r4: the pins another train's progress must not renew. T-0249 stalls a migration while a member still
    /// to move is in handover backoff and leaves the members that already arrived apart from the rest; its written
    /// exit is "the pin expires back to cost-based homing". But progress renews EVERY live pin, so while any other
    /// train moved, a train whose mover failed again and again (backoff doubles to 300 s, the pin lives 180 s) never
    /// expired and its group stayed split. A train with a failing mover is renewed only by its own progress: the
    /// mover's success clears its failure record before the renewal is computed.
    /// </summary>
    internal static HashSet<ulong> StalledMigrationPins(IEnumerable<ulong[]> trains, Func<ulong, bool> failing)
        => [.. trains.Where(train => train.Any(failing)).SelectMany(train => train)];

    /// <summary>T-0311: progress renews the LIVE pins only. An expired pin nobody queried since (expiry is
    /// enforced lazily in <see cref="MigrationTarget"/>) used to be revived here, so a pin whose migration
    /// had died lived on for as long as any other train made progress.</summary>
    internal static void RenewMigrationPins(Dictionary<ulong, (string Target, DateTime Expires)> pins, DateTime now, double seconds,
        IReadOnlySet<ulong>? stalled = null)
    {
        foreach (ulong id in pins.Keys.ToArray())
            if (pins[id].Expires < now) pins.Remove(id);
            else if (stalled?.Contains(id) != true) pins[id] = (pins[id].Target, now.AddSeconds(seconds));
    }

    /// <summary>T-0311 (race review F6): a pin is transition state bound to a partition and a target, so it
    /// ends with either. Dropped every pass: expired pins (silently), pins of partitions no serving node
    /// reports (split or merged away under new ids, owner dead or recovering) and pins toward a node that
    /// no longer serves (closed, dead, spare). Left in place, such a pin kept its target out of the paced
    /// planner's move set (<see cref="BusyMigrationNodes"/>) and made every pair skip the component as
    /// pinned Elsewhere, so a half-moved group stayed split. Returns one log line per dropped live pin.</summary>
    internal static List<string> PruneMigrationPins(Dictionary<ulong, (string Target, DateTime Expires)> pins,
        IReadOnlySet<ulong> reported, IReadOnlySet<string> serving, DateTime now, IReadOnlySet<string>? retiring = null,
        IReadOnlySet<ulong>? fast = null)
    {
        List<string> dropped = [];
        foreach ((ulong id, (string target, DateTime expires)) in pins.ToArray())
        {
            // T-0296 r2: a retiring target (node-control remove/recycle/decrease, T-0312 scale-in) still serves and reports,
            // but it is draining: a pin made before its stop would keep carrying members INTO it, and the scale-in pass would
            // pin them straight out again (the T-0235 rule for operator drains, which node control's drain path bypasses).
            string? why = expires < now ? ""
                : !serving.Contains(target) ? $"its target {target} no longer serves"
                : retiring?.Contains(target) == true ? $"its target {target} is retiring (draining for node control)"
                : !reported.Contains(id) ? "no node reports it"
                // T-0296 r4: fast is checked when the planner pins (T-0292: "never migrated") and was not looked at again.
                // A pinned member that turned fast is its own component in ChooseMove and was carried as
                // "balance-migration" at over 1000 m/s. It stays with its owner and follows its group once slow.
                : fast?.Contains(id) == true ? "it is fast now (a fast partition is not migrated)" : null;
            if (why == null) continue;
            pins.Remove(id);
            if (why.Length != 0) dropped.Add($"Dropped balance migration pin of partition {id} (target {target}): {why}");
        }
        return dropped;
    }

    /// <summary>
    /// T-0296 r4: a migration ChooseMove did not honour because a member still to move is validation-blocked
    /// (T-0249) is over, not suspended. Its pins used to stay: the block lapses after 60 s, the pin lives 180 s and
    /// is renewed by any other train's progress, so the group co-location had just reunited on the blocked member's
    /// side was carried apart again as "balance-migration", by a plan made before the block. Dropped here, the
    /// group stays where co-location put it until the planner plans it afresh. Returns one log line per dropped pin.
    /// </summary>
    internal static List<string> DropVoidedPins(Dictionary<ulong, (string Target, DateTime Expires)> pins, IEnumerable<ulong> component)
    {
        List<string> dropped = [];
        foreach (ulong id in component)
            if (pins.Remove(id, out (string Target, DateTime Expires) pin))
                dropped.Add($"Dropped balance migration pin of partition {id} (target {pin.Target}): a member of its group still to move is HandoverBlocked");
        return dropped;
    }

    private void DropVoidedPins(HashSet<ulong> component)
    {
        if (component.Count == 0) return;
        List<string> dropped;
        lock (_balanceMigrations) dropped = DropVoidedPins(_balanceMigrations, component);
        foreach (string line in dropped) Console.WriteLine(line);
    }

    private void PruneMigrationPins((ServedNode Node, PartitionReport Report)[] reports)
    {
        HashSet<ulong> reported = [.. reports.SelectMany(x => x.Report.Partitions).Select(p => p.Id)];
        HashSet<string> serving = [.. reports.Select(x => x.Node.Node)];
        HashSet<string> retiring = _registry == null ? [] : [.. _registry.RetiringNodes()];
        // A retiring node has to drain whatever it holds: ScaleInPass pins its fast partitions too, every pass.
        HashSet<ulong> fast = [.. reports.Where(x => !retiring.Contains(x.Node.Node)).SelectMany(x => x.Report.Partitions).Where(p => p.Fast).Select(p => p.Id)];
        List<string> dropped;
        lock (_balanceMigrations) dropped = PruneMigrationPins(_balanceMigrations, reported, serving, DateTime.UtcNow, retiring, fast);
        foreach (string line in dropped) Console.WriteLine(line);
    }

    /// <summary>
    /// Which node an envelope-connected component should end up on. Stable across iterations by
    /// construction - the majority side only grows as partitions arrive - so the destination does not flip
    /// between passes, which is the whole point.
    /// </summary>
    /// <summary>
    /// One line naming the component's members per side (id, member count, clients, snapshot KB),
    /// the two §7.5 side costs and, when it decided, the travelling-client rule (T-0073).
    /// </summary>
    private static string ExplainHome((PartitionInfo Partition, bool OnSource)[] members, HashSet<ulong> routed,
        CostModel model, HashSet<ulong>? travelling, bool homeIsTarget,
        double loadSource = 0, double loadTarget = 0)
    {
        static string Side((PartitionInfo Partition, bool OnSource)[] members, bool onSource, HashSet<ulong> routed)
            => string.Join(" ", members.Where(m => m.OnSource == onSource).Take(8).Select(m =>
                $"{m.Partition.Id}(m{m.Partition.MemberCount},c{ClientCount(m.Partition, routed)},{m.Partition.EstimatedSnapshotBytes / 1024}KB)"))
               + (members.Count(m => m.OnSource == onSource) > 8 ? " ..." : "");
        double onSource = members.Where(m => m.OnSource).Sum(m => MoveCost(model, m.Partition, routed));
        double onTarget = members.Where(m => !m.OnSource).Sum(m => MoveCost(model, m.Partition, routed));
        (double priceTarget, double priceSource) = HomePrices(members, routed, model, loadSource, loadTarget);
        // T-0141: the congestion term appears on EVERY co-location line, and says when it CHANGED the
        // answer. The reverted first attempt was a bare `continue` with no observation point, and a
        // whole afternoon went on not being able to tell "never fired" from "fired and churned".
        string congestion = priceTarget != onSource || priceSource != onTarget
            ? $"; load {loadSource:F0} vs {loadTarget:F0} prices it {priceSource:F0}/{priceTarget:F0}"
              + ((priceTarget < priceSource) != (onSource < onTarget) ? " (CHANGED the home)" : "")
            : "";
        bool travellingRule = travelling is { Count: > 0 }
            && members.Any(m => m.OnSource && m.Partition.Clients.Any(travelling.Contains))
               != members.Any(m => !m.OnSource && m.Partition.Clients.Any(travelling.Contains));
        bool nearTie = !travellingRule && NearTie(model, priceTarget, priceSource, loadSource, loadTarget);
        return $"source [{Side(members, true, routed)}] cost {onSource:F0}ms vs target [{Side(members, false, routed)}] cost {onTarget:F0}ms"
            + congestion
            + (travellingRule ? "; travelling client decides"
                : nearTie ? $"; near-tie, load {loadSource:F0} vs {loadTarget:F0} -> lighter node"
                : priceTarget == priceSource ? "; tie-break" : "")
            + $" -> home {(homeIsTarget ? "target" : "source")}";
    }

    /// <summary>
    /// T-0141: the two §7.5 prices are a tie when they differ by no more than
    /// <see cref="CostModel.HomeTieFraction"/> of the higher one AND the pass knows both nodes' loads
    /// and they differ. Equal loads (or a pass without load, as in the self-tests' default) fall
    /// through to the exact price comparison and the §7.1 tie-breaks, so nothing changes there.
    /// </summary>
    private static bool NearTie(CostModel model, double priceTarget, double priceSource, double loadSource, double loadTarget)
    {
        if (model.HomeTieFraction <= 0 || loadSource <= 0 || loadTarget <= 0 || loadSource == loadTarget) return false;
        double higher = Math.Max(priceTarget, priceSource);
        return higher > 0 && Math.Abs(priceTarget - priceSource) <= model.HomeTieFraction * higher;
    }

    /// <summary>
    /// The §7.5 price of each side, with T-0141's congestion term folded in. Returns the cost of
    /// choosing TARGET as home (i.e. of moving the source side) and of choosing SOURCE as home.
    ///
    /// The congestion term makes an already-loaded node resist becoming the home, because becoming
    /// the home is what makes content land on it. It is a PRICE, not a veto: whichever way it
    /// tips, the component still co-locates, so the pair is never left straddling two nodes in
    /// violation of the sphere-gap invariant. That is the whole difference from the reverted first
    /// attempt - see CostModel.LoadHomeWeight.
    /// </summary>
    private static (double Target, double Source) HomePrices(
        (PartitionInfo Partition, bool OnSource)[] members, HashSet<ulong> routed, CostModel model,
        double loadSource, double loadTarget)
    {
        double onSource = members.Where(m => m.OnSource).Sum(m => MoveCost(model, m.Partition, routed));
        double onTarget = members.Where(m => !m.OnSource).Sum(m => MoveCost(model, m.Partition, routed));
        // Choosing target as home costs moving the source side, and vice versa.
        double priceTarget = onSource, priceSource = onTarget;
        double heavier = Math.Max(loadSource, loadTarget);
        if (model.LoadHomeWeight > 0 && heavier > 0 && loadSource > 0 && loadTarget > 0)
        {
            double imbalance = Math.Abs(loadTarget - loadSource) / heavier;
            double penalty = model.LoadHomeWeight * imbalance * (onSource + onTarget);
            if (loadTarget > loadSource) priceTarget += penalty; else priceSource += penalty;
        }
        return (priceTarget, priceSource);
    }

    private static bool HomeIsTarget((PartitionInfo Partition, bool OnSource)[] members, HashSet<ulong> routed,
        CostModel model, HashSet<ulong>? travelling = null, double loadSource = 0, double loadTarget = 0)
    {
        // Partitioning.md §7.4: a teleported (or admin-moved) character travels to the node owning its
        // destination - direction comes from the trigger, not the price, and destination partitions are
        // never pulled toward the player. The teleport intent marks the client until its switch lands;
        // every ordinary drift encounter below stays §7.5-priced.
        if (travelling is { Count: > 0 })
        {
            bool travellingSource = members.Any(m => m.OnSource && m.Partition.Clients.Any(travelling.Contains));
            bool travellingTarget = members.Any(m => !m.OnSource && m.Partition.Clients.Any(travelling.Contains));
            if (travellingSource != travellingTarget) return travellingSource;
        }
        // §7.5: home is the side that costs more to move - snapshot bytes, the freeze they imply, and
        // client switches, all through one price. That single rule carries what used to be three special
        // cases: "the big cluster stays put" (its snapshot dominates), Clustering.md 6.4's "the player
        // travels to the content" (a lone character prices far below any real base, so its side moves
        // even with the client-switch term), and the balancer's component migration (moving the
        // component's costliest partition flips this majority, and co-location pulls the remainder).
        // A client-bearing side no longer pins its node outright - that was the one-way client switch
        // talking, and the switch is symmetric now; the γ term is what a switch is actually worth.
        (double priceTarget, double priceSource) = HomePrices(members, routed, model, loadSource, loadTarget);
        // T-0141 (2026-09-15): prices within HomeTieFraction of each other are a tie, and a tie goes
        // to the LIGHTER node - see CostModel.HomeTieFraction and NearTie.
        if (NearTie(model, priceTarget, priceSource, loadSource, loadTarget)) return loadTarget < loadSource;
        if (priceTarget != priceSource) return priceTarget < priceSource;
        // §7.1 tie-breaks: the side with more attached clients stays, then the larger snapshot stays,
        // then the target - so the choice never depends on report order. (Away-from-a-closing-node needs
        // no branch here: the automatic loop only pairs Active nodes, and drains bypass the chooser.)
        int clientsSource = members.Where(m => m.OnSource).Sum(m => ClientCount(m.Partition, routed));
        int clientsTarget = members.Where(m => !m.OnSource).Sum(m => ClientCount(m.Partition, routed));
        if (clientsSource != clientsTarget) return clientsTarget > clientsSource;
        long bytesSource = members.Where(m => m.OnSource).Sum(m => m.Partition.EstimatedSnapshotBytes);
        long bytesTarget = members.Where(m => !m.OnSource).Sum(m => m.Partition.EstimatedSnapshotBytes);
        if (bytesSource != bytesTarget) return bytesTarget > bytesSource;
        return true;
    }

    /// <summary>
    /// The placement gap between two partitions (Plan/EntityGapPlacement.md): the minimum
    /// entity-pair gap, minus a velocity lookahead sized by the likely mover's relocation time.
    /// The partition bounding sphere survives only as a blanket exclusion - it lower-bounds the
    /// entity gap, so a pair whose sphere-based value already clears the envelope needs no member
    /// scan and keeps the old O(1) cost. Viktor 2026-08-24: if two partitions do not meet the merge
    /// criterion, no entity pair can see the other's node - they are safe apart; and a closing pair
    /// must be co-located preemptively when its time-to-contact is inside the mover's relocation
    /// time, which is this same inequality rearranged.
    /// </summary>
    private static double EnvelopeGap(PartitionInfo a, PartitionInfo b, double envelope, CostModel model)
    {
        double lead = RelocationLeadSeconds(model, a, b);
        // The bounding sphere + partition-level max speed lower-bound every member pair's
        // velocity-adjusted gap, so this is a sound blanket exclusion AND the fallback when a
        // report predates member geometry. Every member pair's reach below is at most both max
        // speeds summed and at most the 2 x MaxGridSpeed closing cap, so the bound takes the
        // smaller of the two. Capping each side at MaxGridSpeed instead was not a bound: one
        // member above the cap closing on a static one reached up to 2 x the cap below.
        double cap = model.MaxGridSpeed;
        double bound = SphereGap(a.Sphere, b.Sphere)
            - Math.Min(a.MaxMemberSpeed + b.MaxMemberSpeed, 2 * cap) * lead;
        if (bound >= envelope) return bound;   // exclusion: the exact value can only be larger
        if (a.MemberSpheres is not { Length: > 0 } ma || b.MemberSpheres is not { Length: > 0 } mb)
            return bound;
        // Velocity applies PER MEMBER PAIR: one flying ship inside a base partition must not
        // inflate the whole base's reach (partition-level max speed did exactly that - the old
        // false-adjacency problem in new clothes). Row format [x,y,z,radius,speed]; a 4-wide row
        // reads as static.
        // T-0141: an 8-wide row [x,y,z,radius,speed,vx,vy,vz] carries the velocity VECTOR, and when
        // BOTH rows of a pair do, the lookahead is the pair's CLOSING speed along the line between
        // them - a receding or parallel pair gets none. The direction-blind sum linked a ship flying
        // away from a cluster to it for ~1.8 km; 73% of co-location triggers on 2026-09-14 had a raw
        // gap >= 4000 m (outside the envelope), and those links chained the pilots into one 15-22-
        // partition component that no balancer change could split. Closing speed never exceeds the
        // speed sum, so the sphere exclusion above stays a sound lower bound.
        double min = double.MaxValue;
        foreach (double[] x in ma)
            foreach (double[] y in mb)
            {
                double dx = x[0] - y[0], dy = x[1] - y[1], dz = x[2] - y[2];
                double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double reach;
                if (x.Length >= 8 && y.Length >= 8)
                {
                    // d points from y to x; the pair closes when x moves toward y relative to it.
                    double wx = x[5] - y[5], wy = x[6] - y[6], wz = x[7] - y[7];
                    double closing = distance > 0 ? -(wx * dx + wy * dy + wz * dz) / distance
                        : Math.Sqrt(wx * wx + wy * wy + wz * wz);
                    reach = Math.Min(Math.Max(closing, 0), 2 * cap);
                }
                else
                {
                    double sx = x.Length > 4 ? Math.Min(x[4], cap) : 0;
                    double sy = y.Length > 4 ? Math.Min(y[4], cap) : 0;
                    reach = sx + sy;
                }
                double gap = distance - x[3] - y[3] - reach * lead;
                if (gap < min) min = gap;
            }
        return min;
    }

    /// <summary>
    /// True minimum entity-pair gap (geometry only, no velocity lookahead) - what the cross-node
    /// invariant alarm and the logged raw gap measure. Falls back to the bounding-sphere gap when
    /// either report predates member geometry. O(N*M), paid only for pairs the sphere exclusion
    /// could not settle; the worst live pair (356 x 158 members) is ~56k distance evaluations,
    /// well under a millisecond at the 1 Hz pass rate.
    /// </summary>
    private static double MinGap(PartitionInfo a, PartitionInfo b)
    {
        if (a.MemberSpheres is not { Length: > 0 } ma || b.MemberSpheres is not { Length: > 0 } mb)
            return SphereGap(a.Sphere, b.Sphere);
        double min = double.MaxValue;
        foreach (double[] x in ma)
            foreach (double[] y in mb)
            {
                double dx = x[0] - y[0], dy = x[1] - y[1], dz = x[2] - y[2];
                double gap = Math.Sqrt(dx * dx + dy * dy + dz * dz) - x[3] - y[3];
                if (gap < min) min = gap;
            }
        return min;
    }

    /// <summary>
    /// The velocity-lookahead horizon: the P99-ish relocation time of the pair's likely mover (the
    /// cheaper side), approximated as 3x its estimated freeze plus pipeline overhead, floored at
    /// the pre-entity-gap constant of 5 s. A lone approaching ship keeps today's 5 s horizon; a
    /// closing 80k cluster gets ~45 s, because that is how long moving it will actually take.
    /// The 3x is the measured median-to-tail ratio placeholder until a per-size P99 is fitted from
    /// handover telemetry (Plan/EntityGapPlacement.md open point).
    /// </summary>
    private static double RelocationLeadSeconds(CostModel model, PartitionInfo a, PartitionInfo b)
    {
        double mover = Math.Min(model.FreezeMs(a.EstimatedSnapshotBytes, a.MemberCount),
            model.FreezeMs(b.EstimatedSnapshotBytes, b.MemberCount));
        return Math.Max(5, 3 * mover / 1000 + 2) * LeadScale;
    }

    /// <summary>
    /// T-0141 experiment knob: multiplies the co-location velocity lookahead. 1 (default) is unchanged; 0 turns
    /// the lookahead off so only the raw member gap links partitions. With pilots flying and the rotator off,
    /// 43 of 54 co-location handovers on 2026-09-14 21:03-21:20 had a raw gap >= 4000 m - linked only by the
    /// lookahead - and 41 of them moved into the node that became the pile.
    /// </summary>
    private static readonly double LeadScale =
        double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_COLOCATION_LEAD_SCALE"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double scale) && scale >= 0 ? scale : 1.0;

    /// <summary>
    /// A join the registry could not route YET, kept until it can be answered properly (T-0197).
    /// The deadline travels with the request so a retry that is still transient does not start the
    /// clock again - otherwise a partition that stays recovering holds the request forever.
    /// </summary>
    private sealed record HeldJoin(IJoinRequest Request, ulong ClientId, string? AssertedName, DateTimeOffset Deadline, string Reason);

    /// <summary>Joins held for a transient registry state. Touched only from the frontend poll
    /// thread and from OnConnectionRequest, which runs on it, so the lock guards the retry drain
    /// against nothing more than a future caller; it costs an uncontended lock per second.</summary>
    private readonly List<HeldJoin> _heldJoins = [];

    /// <summary>Next tick at which the poll thread sweeps <see cref="_heldJoins"/>; frontend poll
    /// thread only, so it needs no synchronisation.</summary>
    private long _heldJoinsNextSweep;

    /// <summary>
    /// How long a join may be held before the refusal becomes terminal after all. CLUSTER_JOIN_HOLD,
    /// seconds, default 120 (T-0298, Viktor 2026-10-01: was 20). A Steam login in the ~40 s after a restart
    /// waits for the nodes AND its offline home's load; at 20 s gateway-rs refused it, the client's login went
    /// with the expired session and the player had to join again (r34 21:29:56). 120 s matches the offline-home
    /// window of JoinHeldForHomeRecovery. The earlier reasoning for 20: measured recoveries complete in 1-16 s on this cluster (T-0197's
    /// passing runs: 6 s and 39 s, the 39 s one being a client that retried by itself), and the
    /// client's own connect attempts run out on the same order, so a longer hold would answer a
    /// request nobody is listening for any more. Zero restores the old terminal behaviour.
    /// </summary>
    private static readonly TimeSpan JoinHold = ParseJoinHold();

    private static TimeSpan ParseJoinHold()
    {
        string? raw = Environment.GetEnvironmentVariable("CLUSTER_JOIN_HOLD");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(120);
    }

    public void OnConnectionRequest(ConnectionRequest request) => OnConnectionRequest(new LiteNetLibJoinRequest(request), null, null);

    /// <summary>
    /// <paramref name="decoded"/> carries the identity a HELD request already decoded, and passing it
    /// is not an optimisation - it is the difference between the hold working and the hold being a
    /// rejection with a comment on it.
    ///
    /// T-0197: ConnectionRequest.Data is a NetDataReader and TryDecodeHandshake CONSUMES it
    /// (GetString, GetULong, TryGetString each advance the position). The retry path re-enters this
    /// method with the SAME ConnectionRequest, so the second decode sees AvailableBytes == 0, returns
    /// false, and the request is refused as a bad handshake - one second after the hold was placed,
    /// by the very sweep that exists to answer it. That is what "the held join was never answered"
    /// actually was: it WAS answered, with a reject, on the first sweep. Measured 2026-09-21
    /// (bench 20260921-152113Z): "Holding join for client ..." and "Rejected 127.0.0.1:54627: bad
    /// Direct Transport handshake" in the same second, with a base rate of that line of ZERO across
    /// eleven other bench runs and the whole live-cluster gateway log.
    ///
    /// The single-flight check and the admission test ARE still the right questions on a retry. Only
    /// the decode is one the reader can answer exactly once.
    /// </summary>
    private void OnConnectionRequest(IJoinRequest request, DateTimeOffset? holdUntil,
        (ulong ClientId, string? AssertedName)? decoded)
    {
        ClientSession? session = null;
        try
        {
            if (JoiningClosed)
            {
                request.Reject();
                return;
            }
            ulong clientId;
            string? assertedName;
            if (decoded is { } already)
            {
                (clientId, assertedName) = already;
            }
            else if (request.Data == null || !DirectTransportCodec.TryDecodeHandshake(request.Data, out clientId, out assertedName))
            {
                Console.Error.WriteLine($"Rejected {request.RemoteEndPoint}: bad Direct Transport handshake");
                request.Reject();
                return;
            }

            if (clientId == DirectTransportCodec.NodeLinkId)
            {
                Console.Error.WriteLine($"Rejected reserved client {clientId} from {request.RemoteEndPoint}");
                request.Reject();
                return;
            }
            // Gateway.md §7.1: at most one live session AND at most one in-flight join per identity. The
            // second connect is refused - never both proceeding - because every node-side path that
            // "spawns if no character is found" races its own retries, and the Phase 3 flight's duplicate
            // characters were that race resolving twice. A session that exists but has not finished
            // attaching is still in the map, so the same check covers the in-flight case.
            if (_clients.TryGetValue(clientId, out ClientSession? existing))
            {
                Interlocked.Increment(ref _singleFlightRefusals);
                Console.Error.WriteLine($"Refused second join for identity {clientId} from {request.RemoteEndPoint}: "
                    + $"single-flight (existing session on {existing.BackendEndpoint}, switching={existing.SwitchingBackend})");
                request.Reject();
                return;
            }

            if (_admission.Reject(clientId, clientId, ClusterPlayers().Count, _clients.ContainsKey(clientId),
                isProfiling: false) is JoinResult refusal)
            {
                Console.Error.WriteLine($"Rejected Direct Transport client {clientId}: {refusal}");
                request.Reject();
                return;
            }
            if (JoinHeldForHomeRecovery(clientId))
            {
                // T-0197, the same correction one level up: this hold was ALSO implemented as a
                // reject, on the assumption that the client comes back. The measurement says it
                // makes one attempt and stops, so a deferral delivered as a refusal is a lockout
                // with a comment on it. Hold the request and answer it when the home is claimed.
                HoldJoin(request, clientId, assertedName, "character home partition is recovering", holdUntil);
                return;
            }
            ClientRoute? route;
            try
            {
                route = _registry?.ResolveClientRoute(clientId);
            }
            catch (RegistryBusyException busy)
            {
                // T-0197: NOT YET, rather than no. The registry now says which of the eight
                // unroutable states it found, and this one is a state the cluster is actively
                // leaving - a partition mid-recovery, a lapsed lease being re-homed, a node that
                // has registered but is not serving yet. Rejecting here is what locked a player
                // out for the rest of the run: the client makes ONE attempt about five seconds
                // after its node dies, and a reject is terminal. Hold the request instead and
                // answer it from the poll thread once the registry can route it.
                HoldJoin(request, clientId, assertedName, busy.Message, holdUntil);
                return;
            }
            IPEndPoint backend = route == null ? UnroutedBackend() : EndpointParser.Parse(route.Endpoint, DirectTransportConstants.DefaultPort);
            session = new ClientSession(clientId, backend, route)
            {
                // The Direct Transport frontend binds the identity from the handshake's asserted id
                // (Security.md §3.6 test-accept-all); only the ticket step is bypassed. The name is
                // asserted the same way - without it the player is named after the id, which is what
                // every headless client showed up as in chat and the player list.
                Credential = new ClientCredential(DirectTransportService, clientId, clientId,
                    assertedName ?? clientId.ToString(), _admission.IsAdmin(clientId)),
            };
            if (!_clients.TryAdd(clientId, session))
            {
                request.Reject();
                return;
            }
            session.Client = request.Accept(session);
            if (session.Client == null)
            {
                Console.Error.WriteLine($"Dropped the join of client {clientId} from {request.RemoteEndPoint}: no new connection to bind it to; its next attempt joins afresh");
                RemoveSession(session);
                return;
            }
            Console.WriteLine($"Accepted client {clientId} from {request.RemoteEndPoint}");
            if (route != null)
                Console.WriteLine($"Resolved client {clientId} partition {route.Partition} to {route.Node} epoch {route.Epoch} at {backend}");
            if (!RelayPaused && (_registry == null || _registry.WorldAuthority().Generation <= 0))
                Attach(session, GetNodeLink(session.BackendEndpoint));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Connection request failed: " + e);
            if (session?.Client != null)
                session.Client.Disconnect();
            else
                request.Reject();
            // #1221: also a session that threw between TryAdd and the accept, or single-flight refuses the identity for good.
            if (session != null)
                RemoveSession(session);
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        NodeLink? link = peer.Tag as NodeLink;
        if (link == null && _nodes.TryGetValue((IPEndPoint)peer, out link))
            peer.Tag = link;
        if (link != null)
        {
            lock (link.Sync)
            {
                if (link.Peer != null && !ReferenceEquals(peer, link.Peer))
                    return;
                link.Peer = peer;
                link.ConnectedAt = link.LastActivity = DateTimeOffset.UtcNow;
                link.DisconnectedAt = null;
                link.LastDisconnectReason = null;
                link.Connected.Set();
                Console.WriteLine($"Connected node link {link.Endpoint}");
            }
            OnLinkConnected(link);
        }
    }

    /// <summary>A node link came up: re-attach its clients. Shared by the in-process and the external relay.</summary>
    private void OnLinkConnected(NodeLink link)
    {
		foreach (ClientSession session in link.Clients.Values)
		{
			SendAttach(link, session);
			if (!session.SwitchingBackend)
				FlushClientBacklog(session);
		}
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (peer.Tag is ClientSession session)
        {
            Console.WriteLine($"Client {session.ClientId} disconnected: {disconnectInfo.Reason}");
            RemoveSession(session);
        }
        else
        {
            NodeLink? link = peer.Tag as NodeLink;
            if (link == null && _nodes.TryGetValue((IPEndPoint)peer, out link))
                peer.Tag = link;
            if (link == null)
                return;
            lock (link.Sync)
            {
                if (!ReferenceEquals(peer, link.Peer))
                    return;
                link.Peer = null;
                link.Connected.Reset();
            }
            OnLinkDisconnected(link, disconnectInfo.Reason.ToString());
        }
    }

    /// <summary>A node link went down: release its clients' attach/detach waits and move its settled
    /// clients off it. Shared by the in-process and the external relay.</summary>
    private void OnLinkDisconnected(NodeLink link, string reason)
    {
            Console.WriteLine($"Node link {link.Endpoint} disconnected: {reason}");
            link.DisconnectedAt = DateTimeOffset.UtcNow;
            link.LastDisconnectReason = reason;
            foreach (ClientSession attached in link.Clients.Values)
            {
                lock (attached.RelayLock)
                {
                    bool attachChanged = ReferenceEquals(attached.AttachedLink, link) || ReferenceEquals(attached.AttachingLink, link);
                    if (ReferenceEquals(attached.AttachedLink, link))
                        attached.AttachedLink = null;
                    if (ReferenceEquals(attached.AttachingLink, link))
                        attached.AttachingLink = null;
                    bool detachChanged = ReferenceEquals(attached.DetachingLink, link);
                    if (detachChanged)
                        attached.DetachingLink = null;
                    if (attachChanged)
                        attached.AttachCompleted.Set();
                    if (detachChanged)
                        attached.DetachCompleted.Set();
                }
                if (ReferenceEquals(attached.Link, link) && !attached.SwitchingBackend)
                    ForceReconnect(attached, "node-link-lost");
            }
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        long started = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            byte[] wirePayload = reader.GetRemainingBytes();

            if (peer.Tag is ClientSession session && session.Client is DirectClientChannel direct && ReferenceEquals(peer, direct.Peer))
                OnClientPacket(session, DirectTransportCodec.DecodePacket(session.ClientId, wirePayload, deliveryMethod));
            else if (peer.Tag is NodeLink link && ReferenceEquals(peer, link.Peer))
            {
                link.LastActivity = DateTimeOffset.UtcNow;
                Interlocked.Increment(ref _nodePackets);
                Interlocked.Add(ref _nodeBytes, wirePayload.Length);
                NodeLinkPacket envelope = DirectTransportCodec.DecodeNodeLink(wirePayload, deliveryMethod);
                if (envelope.Kind == NodeLinkMessage.Plugin) { RoutePluginMessage(link, envelope.Payload); return; }
                if (envelope.Kind == NodeLinkMessage.Global)
                {
                    RouteGlobalMessage(link, envelope.Payload);
                    return;
                }
                if (!_clients.TryGetValue(envelope.ClientId, out ClientSession? target))
                    return;
                lock (target.RelayLock)
                {
                    if (!ReferenceEquals(target.Link, link))
                        return;
                    if (envelope.Kind is NodeLinkMessage.AttachAck or NodeLinkMessage.DetachAck or NodeLinkMessage.Detach)
                    {
                        OnNodeControl(target, link, envelope.Kind);
                        return;
                    }
                    if (envelope.Kind != NodeLinkMessage.Relay)
                        return;
                    target.LastNodeActivity = DateTimeOffset.UtcNow;
                    GatewayPacket packet = new(envelope.ClientId, envelope.Channel, envelope.Payload, envelope.Delivery);
                    if (!ShouldRelayServerPacket(target, packet))
                        return;
                    if (ReferenceEquals(target.ValidationLink, link) && DirectTransportCodec.TryGetGameMessageId(packet, out byte targetMessage)
                        && targetMessage == 7)
                    {
                        target.TargetServerPacketSeen = true;
                        CompleteTargetReadiness(target);
                    }
                    if (target.BufferServerPackets)
                    {
                        if (!target.ServerBacklog.TryEnqueue(packet))
                            ForceReconnect(target, "server-backlog-overflow");
                    }
                    else
                        SendToClient(target, packet);
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Receive failed: " + e);
            if (e is InvalidDataException && peer.Tag is NodeLink)
                peer.Disconnect();
        }
        finally
        {
            Interlocked.Add(ref _relayTicks, Stopwatch.GetTimestamp() - started);
            Interlocked.Add(ref _relayAllocations, GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
    }

    /// <summary>
    /// A node's AttachAck, DetachAck or Detach about <paramref name="target"/>, which the caller has
    /// matched to <paramref name="link"/> (the session's current link) under the session's relay lock.
    /// Shared by the in-process relay and the external relay's NodeControl events.
    /// </summary>
    private void OnNodeControl(ClientSession target, NodeLink link, NodeLinkMessage kind)
    {
        if (kind == NodeLinkMessage.AttachAck)
        {
            if (ReferenceEquals(target.AttachingLink, link))
            {
                if (!RouteStillValid(target, link))
                {
                    target.Client?.Disconnect();
                    return;
                }
                target.AttachedLink = link;
							target.AttachedAdmissionGeneration = target.AttachingAdmissionGeneration;
							target.AttachedIdentityId = target.AttachingIdentityId;
							target.AttachedBindingRevision = target.AttachingBindingRevision;
                target.RegistryRouteValidated = true;
							if (target.AttachedBindingRevision != target.BindingRevision)
							{
								byte[] binding = AttachBinding(target);
								target.AttachingAdmissionGeneration = target.PlayerReady?.AuthorityGeneration ?? 0;
								target.AttachingIdentityId = target.PlayerReady?.Identity.IdentityId ?? 0;
								target.AttachingBindingRevision = target.BindingRevision;
								target.AttachCompleted.Reset();
								SendNodeLink(link, new(NodeLinkMessage.Attach, target.ClientId, 0, binding, DeliveryMethod.ReliableOrdered));
								return;
							}
							target.AttachingLink = null;
                target.AttachCompleted.Set();
							if (target.PlayerReady is PlayerReady ready
								&& target.AttachedBindingRevision == target.BindingRevision)
							{
								target.AdmissionState = "Ready";
								target.AdmissionError = null;
								Interlocked.Exchange(ref target.AdmissionStarted, 0);
								Console.WriteLine($"Player admission ready client {target.ClientId} identity {ready.Identity.IdentityId} generation {ready.AuthorityGeneration}");
							}
                SendCredit(link, target.ClientId, 4 * 1024 * 1024);
                if (!target.SwitchingBackend)
                    FlushClientBacklog(target);
            }
            return;
        }
        if (kind == NodeLinkMessage.DetachAck)
        {
            if (ReferenceEquals(target.DetachingLink, link))
            {
                target.DetachingLink = null;
                target.DetachCompleted.Set();
            }
            return;
        }
        if (kind == NodeLinkMessage.Detach)
        {
						if (target.SwitchingBackend || target.AdmissionState is "Attaching" or "Refreshing" or "Granted")
						{
							target.AttachedLink = null;
							target.AttachingLink = null;
							target.AttachCompleted.Reset();
							RetryPlayerAttach(target, link);
							return;
						}
            target.Client?.Disconnect();
            return;
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        Console.Error.WriteLine($"UDP socket error from {endPoint}: {socketError}");

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    private NodeLink GetNodeLink(IPEndPoint endpoint) =>
        _nodes.GetOrAdd(endpoint, value => new NodeLink(value, external: _relay != null) { Relay = _relay });

    private void RouteGlobalMessage(NodeLink sourceLink, byte[] payload)
    {
        if (_registry == null) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string kind = root.GetProperty("Kind").GetString() ?? string.Empty;
            long generation = root.GetProperty("Generation").GetInt64();
            string origin = root.GetProperty("Origin").GetString() ?? string.Empty;
            string? target = root.TryGetProperty("Target", out JsonElement targetElement) && targetElement.ValueKind == JsonValueKind.String
                ? targetElement.GetString() : null;
            NodeRegistration source = _registry.GetNodeByEndpoint(sourceLink.Endpoint.ToString());
            WorldAuthority authority = _registry.WorldAuthority();
            if (source.Role == NodeRole.WA)
            {
                if (authority.Node != source.Node || authority.Epoch != source.Epoch || generation != authority.Generation
                    || kind is not ("Broadcast" or "Snapshot" or "Response")
                    || (kind == "Response" && (target == null || root.GetProperty("Sequence").GetInt64() <= 0)))
                    throw new InvalidDataException("Global broadcast is not from the live World Authority generation");
                if (kind == "Response" && target == "$gateway")
                {
                    long sequence = root.GetProperty("Sequence").GetInt64();
					Guid operationId = root.GetProperty("OperationId").GetGuid();
					if (!root.TryGetProperty("Payload", out JsonElement responsePayloadElement)
						|| responsePayloadElement.ValueKind != JsonValueKind.String)
						throw new InvalidDataException("Global response payload is invalid");
					string responsePayload = responsePayloadElement.GetString() ?? string.Empty;
                    if (!_gatewayCommands.TryGetValue(sequence, out PendingGatewayCommand? pending)
                        || pending.Generation != generation || pending.Subsystem != root.GetProperty("Subsystem").GetString()
						|| pending.OperationId != operationId
                        || !_gatewayCommands.TryRemove(sequence, out PendingGatewayCommand? removed) || !ReferenceEquals(removed, pending))
                        throw new InvalidDataException("Global response has no pending Gateway command");
					pending.Completion.TrySetResult(responsePayload);
                }
                else
				{
					if (kind is "Broadcast" or "Snapshot" && root.GetProperty("Subsystem").GetString() == "identities"
						&& AcceptIdentitySnapshot(generation, root.GetProperty("Sequence").GetInt64()))
						UpdatePlayerBindings(root.GetProperty("Payload").GetString() ?? string.Empty, generation);
                    foreach (NodeRegistration node in _registry.Nodes().Where(node => ReceivesGlobal(node, target)))
                        SendGlobal(GetNodeLink(EndpointParser.Parse(node.Endpoint, DirectTransportConstants.DefaultPort)), payload, GlobalDelivery(kind));
				}
            }
            else
            {
                if (origin != source.Node || generation != authority.Generation || kind is not ("Request" or "Resync" or "Command")
					|| (kind == "Command" && (root.GetProperty("Sequence").GetInt64() <= 0
						|| root.GetProperty("OperationId").GetGuid() == Guid.Empty)) || authority.Node == null)
                    throw new InvalidDataException("Global request is not valid for the live World Authority generation");
                NodeRegistration wa = _registry.Nodes().Single(node => node.Node == authority.Node && node.Epoch == authority.Epoch);
                SendGlobal(GetNodeLink(EndpointParser.Parse(wa.Endpoint, DirectTransportConstants.DefaultPort)), payload, GlobalDelivery(kind));
            }
            Interlocked.Increment(ref _globalMessages);
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref _rejectedGlobalMessages);
            Console.Error.WriteLine("Rejected global-state frame: " + e.Message);
        }
    }

    /// <summary>
    /// Which regular nodes a World Authority frame goes to. A broadcast goes to the Active ones. A frame addressed to
    /// one node, like the answer to its command, also reaches it while it drains (Closed): the drain still asks the WA,
    /// and with only Active nodes served every answer was dropped, the command timed out and the drain ended the node
    /// unsaved (SE-1091).
    /// </summary>
    internal static bool ReceivesGlobal(NodeRegistration node, string? target) => node.Role == NodeRole.Regular
        && (target == null ? node.State == NodeState.Active : node.Node == target && node.State is NodeState.Active or NodeState.Closed);

    /// <summary>
    /// T-0154: bulk global frames (publishes, snapshots, node reports) go ReliableOrdered; a command
    /// or a response goes ReliableUnordered, which is a separate LiteNetLib queue on the same link,
    /// so it never waits behind the megabytes of antenna snapshots the ordered channel carries
    /// during a rebalancing wave. Commands and responses are matched by sequence and operation id.
    /// </summary>
    private static void SendGlobal(NodeLink link, byte[] payload, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
    {
        lock (link.Sync)
        {
            if (!link.IsConnected)
                throw new IOException("Global-state target node link is disconnected: " + link.Endpoint);
            var packet = new NodeLinkPacket(NodeLinkMessage.Global, 0, 0, payload, delivery);
            if (link.External) SendExternalNodeLink(link, packet);   // T-0229: gateway-rs holds the link
            else link.Peer.Send(DirectTransportCodec.EncodeNodeLink(packet), 0, packet.Delivery);
        }
    }

    private static DeliveryMethod GlobalDelivery(string kind)
        => kind is "Command" or "Response" ? DeliveryMethod.ReliableUnordered : DeliveryMethod.ReliableOrdered;

    /// <summary>
    /// Cluster-wide player list, the only one that exists (§4.2): every browser-visible count and
    /// the ServerFull cap read it. The vanilla per-node "chat bot" member is not reproduced.
    /// </summary>
    private IReadOnlyList<(ulong UserId, string Name, bool Steam)> ClusterPlayers() =>
        _clients.Values.Where(session => session.Credential != null)
            // A Direct Transport client is admitted on an asserted identity with no Steam auth
            // session behind it, so the browser row cannot name it - only count it. See
            // SteamFrontend.Publish.
            .Select(session => (session.Credential!.UserId, session.Credential!.Name,
                session.Credential!.ServiceName != DirectTransportService)).ToArray();

    private void OnClientPacket(ClientSession session, GatewayPacket packet)
    {
        session.LastClientActivity = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _clientPackets);
        Interlocked.Add(ref _clientBytes, packet.Payload.Length + 1);
        if (DirectTransportCodec.TryGetGameMessageId(packet, out byte messageId) && !OnClientMessage(session, packet, messageId))
            return;
        SendToBackend(session, packet);
    }

    /// <summary>
    /// The control plane's view of a client game message: CLIENT_CONNECTED starts admission,
    /// CLIENT_READY marks the world loaded. False means the packet must not be relayed. Shared by the
    /// in-process relay and the external relay's ClientMessage events.
    /// </summary>
    private bool OnClientMessage(ClientSession session, GatewayPacket packet, byte messageId)
    {
        {
            if (messageId == VanillaJoinCodec.ClientConnectedMessageId)
            {
                // T-0248: a login on a session that is still open while the cluster is shutting down. The
                // shutdown sends the player to the menu, and vanilla's reconnector retries 10 s later. A Steam
                // P2P session to the same server is still the old one then (the gateway saw it close only at
                // 23:10:42, the retry came at 23:10:42.5), so the retry arrived here as a login on it and went
                // to the node, which still listed the player and answered JOIN_RESULT AlreadyJoined: an error
                // box that ends the reconnector (2026-09-27 23:10, the CH's Steam client). A new session is
                // refused while joining is closed (OnRelaySteamJoin), and the reconnector retries through
                // that. Here, the same host-left as a session without a login makes it unload and reconnect again.
                if (JoiningClosed)
                {
                    ForceReconnect(session, "login-while-admission-closed");
                    return false;
                }
                session.ClientConnectedPacket = packet;
                if (session.Credential != null) BeginPlayerAdmission(session);
                else if (!BeginTicketValidation(session, packet)) return false;
            }
            // CLIENT_READY is the client's world-loaded signal: only from here on may this session's
            // backend be switched under join-message suppression - before it, the client still needs
            // the join sequence the suppression would drop (the mid-join handover starvation).
            else if (messageId == VanillaJoinCodec.ClientReadyMessageId && !session.WorldReady)
            {
                session.WorldReadyAt = DateTimeOffset.UtcNow;
                session.WorldReady = true;
                Console.WriteLine($"Client {session.ClientId} is world-ready");
            }
        }
        return true;
    }

    /// <summary>
    /// A player who rejoins within the frontend's grace period arrives inside the still-open Steam
    /// session, so no session request announces them: the first packet of the new join is what
    /// opens the session. Everything after that is the ordinary join - the ticket in this packet's
    /// CLIENT_CONNECTED still has to validate before any node sees the client.
    /// </summary>
    private void OnSteamPacket(ulong userId, GatewayPacket packet)
    {
        if (!_clients.TryGetValue(userId, out ClientSession? session))
        {
            if (!OpenSteamSession(userId) || !_clients.TryGetValue(userId, out session))
                return;
        }
        OnClientPacket(session, packet);
    }

    private bool OpenSteamSession(ulong userId)
    {
        if (_registry is { AdmissionOpen: false })
        {
            Console.Error.WriteLine($"Refused Steam session with {userId}: cluster admission is closed");
            return false;
        }
        if (_clients.ContainsKey(userId))
        {
            Console.Error.WriteLine($"Refused Steam session with {userId}: a session is already open");
            return false;
        }
        if (JoinHeldForHomeRecovery(userId))
            return false;
        ClientRoute? route = null;
        string? deferred = null;
        try { route = _registry?.ResolveClientRoute(userId); }
        catch (RegistryBusyException busy) { deferred = busy.Message; Console.Error.WriteLine($"Route lookup for {userId} deferred to admission: {busy.Message}"); }
        catch (Exception e) { Console.Error.WriteLine($"Route lookup for {userId} failed: {e.Message}"); }
        IPEndPoint backend = route == null ? UnroutedBackend()
            : EndpointParser.Parse(route.Endpoint, DirectTransportConstants.DefaultPort);
        var session = new ClientSession(userId, backend, route) { AdmissionState = "TicketPending", RouteDeferred = deferred };
        if (!_clients.TryAdd(userId, session))
            return false;
        session.Client = _steam!.Channel(userId);
        Console.WriteLine($"Accepted client {userId} from steam:{userId}");
        if (route != null)
            Console.WriteLine($"Resolved client {userId} partition {route.Partition} to {route.Node} epoch {route.Epoch} at {backend}");
        return true;
    }

    private void CloseSteamSession(ulong userId, string reason)
    {
        if (!_clients.TryGetValue(userId, out ClientSession? session))
            return;
        Console.WriteLine($"Client {userId} disconnected: {reason}");
        SteamEndAuth(userId);
        RemoveSession(session);
    }

    /// <summary>
    /// Security.md §3.2: the token rides inside CLIENT_CONNECTED, so validation starts here and the
    /// session sits in TicketPending with no node attached. Nothing this client sent is ever
    /// flushed to a node unless the ticket and the §3.4 gate admit it.
    /// </summary>
    private bool BeginTicketValidation(ClientSession session, GatewayPacket packet)
    {
        if (!SteamEnabled)
        {
            RejectClient(session, JoinResult.TicketInvalid);
            return false;
        }
        try
        {
            ClientConnectedData data = VanillaJoinCodec.DecodeClientConnected(packet.Payload);
            if (data.Token is not { Length: > 0 } token)
            {
                Console.Error.WriteLine($"Client {session.ClientId} presented no auth ticket");
                RejectClient(session, JoinResult.TicketInvalid);
                return false;
            }
            lock (session.RelayLock)
            {
                session.AssertedName = string.IsNullOrWhiteSpace(data.Name) ? session.ClientId.ToString() : data.Name!;
                session.AssertedServiceName = string.IsNullOrWhiteSpace(data.ServiceName) ? "Steam" : data.ServiceName!;
                session.IsProfiling = data.IsProfiling;
            }
            SteamBeginAuth(session.ClientId, token);
            return true;
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"Client {session.ClientId} sent an undecodable CLIENT_CONNECTED: {e.Message}");
            RejectClient(session, JoinResult.TicketInvalid);
            return false;
        }
    }

    private void OnSteamTicket(ulong userId, JoinResult result, ulong ownerId, uint[]? missingDlcs = null)
    {
        if (!_clients.TryGetValue(userId, out ClientSession? session))
            return;
        bool admitted = session.Credential != null;
        if (!admitted)
        {
            // A verdict for a join, which is what an armed test verdict replaces. Steam also sends a
            // verdict when the *old* client cancels its ticket by quitting, and that one is a
            // revocation of an admitted session - it must not consume the armed one.
            int armed = Interlocked.Exchange(ref _armedTicketVerdict, -1);
            if (armed >= 0)
            {
                result = (JoinResult)armed;
                Console.WriteLine($"Replaced the ticket verdict for joining client {userId}"
                    + $" with {result} (test)");
            }
        }
        if (result != JoinResult.OK)
        {
            // §2.6: the auth session lives for the whole client session, so a later verdict is a
            // revocation and drops exactly this client - others are untouched.
            if (admitted) ForceReconnect(session, "ticket-revoked-" + result);
            else RejectClient(session, result);
            return;
        }
        if (admitted)
            return;
        if (_admission.Reject(userId, ownerId, ClusterPlayers().Count, alreadyJoined: false, session.IsProfiling)
            is JoinResult refusal)
        {
            RejectClient(session, refusal);
            return;
        }
        session.TicketOwnerId = ownerId == 0 ? userId : ownerId;
        session.MissingDlcs = missingDlcs;
        if (_admission.Settings.GroupId != 0)
        {
            session.AdmissionState = "GroupPending";
            SteamRequestGroupStatus(userId, _admission.Settings.GroupId);
            return;
        }
        AdmitValidatedClient(session);
    }

    private void OnSteamGroupStatus(ulong userId, bool member)
    {
        if (!_clients.TryGetValue(userId, out ClientSession? session) || session.Credential != null)
            return;
        if (!member) RejectClient(session, JoinResult.NotInGroup);
        else AdmitValidatedClient(session);
    }

    /// <summary>
    /// T-0237 step 3 (2026-09-27 06:07, Viktor): a Steam join whose route the registry answered
    /// "come back shortly" (his home partition was offline and its load had just been requested)
    /// was accepted on the DEFAULT backend. The Direct Transport path holds the join on the same
    /// exception; the Steam path cannot hold the session request, but it does not attach until the
    /// ticket validates a few seconds later - and the home was ONLINE by then (load 06:07:07,
    /// online 06:07:10, admitted 06:07:12). Landing on node-1 gave him a body-less spawn screen
    /// while his placement stayed on node-3, and the away-from-body guard kicked him mid-spawn.
    /// So admission resolves a deferred route again, waiting up to SteamRouteWaitSeconds, and only
    /// then falls back to the default backend as before. Viktor 2026-09-27: a player with a living
    /// body (standing, seated, in a cryopod; its partition online or offline) must be put back into
    /// it, never offered the spawn screen - so the wait is the same 120 s bound T-0193 gives an
    /// offline home's load (JoinHeldForHomeRecovery), not a short courtesy.
    /// </summary>
    private const int SteamRouteWaitSeconds = 120;

    private async Task ResolveDeferredSteamRoute(ClientSession session)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        (ClientRoute? route, string? last, bool gaveUp) = await WaitForRoute(
            () => _registry?.ResolveClientRoute(session.ClientId), session.RouteDeferred,
            TimeSpan.FromSeconds(SteamRouteWaitSeconds), TimeSpan.FromSeconds(1), () => session.DisconnectReason != null);
        double waited = (DateTimeOffset.UtcNow - started).TotalSeconds;
        if (route != null)
        {
            session.RegistryRoute = route;
            session.BackendEndpoint = EndpointParser.Parse(route.Endpoint, DirectTransportConstants.DefaultPort);
            Console.WriteLine($"Resolved client {session.ClientId} partition {route.Partition} to {route.Node} epoch {route.Epoch}"
                + $" at {session.BackendEndpoint} after {waited:F0}s (deferred: {session.RouteDeferred})");
        }
        else if (gaveUp)
            Console.Error.WriteLine($"Route of client {session.ClientId} still unavailable after {waited:F0}s ({last}); admitting on the default backend");
        else if (last != null)
            Console.Error.WriteLine($"Route lookup for {session.ClientId} failed: {last}");
        session.RouteDeferred = null;
    }

    /// <summary>
    /// Polls <paramref name="resolve"/> while it answers "come back shortly" (RegistryBusyException), up to
    /// <paramref name="wait"/>. Returns the route (null = the registry has none: default backend), the last
    /// reason, and whether it gave up on a still-busy answer. Any other failure ends the wait at once.
    /// </summary>
    internal static async Task<(ClientRoute? Route, string? Last, bool GaveUp)> WaitForRoute(Func<ClientRoute?> resolve,
        string? first, TimeSpan wait, TimeSpan step, Func<bool> abandoned)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        string? last = first;
        while (true)
        {
            try { return (resolve(), last, false); }
            catch (RegistryBusyException busy) { last = busy.Message; }
            catch (Exception e) { return (null, e.Message, false); }
            if (elapsed.Elapsed >= wait || abandoned()) return (null, last, true);
            await Task.Delay(step);
        }
    }

    private void AdmitValidatedClient(ClientSession session)
    {
        if (session.RouteDeferred != null && session.RegistryRoute == null && _registry != null)
        {
            // One wait per session: a second verdict (group status, a repeated ticket) arriving
            // meanwhile returns here, and the wait's own call below admits exactly once.
            if (Interlocked.Exchange(ref session.RouteWaitStarted, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                await ResolveDeferredSteamRoute(session);
                if (_clients.TryGetValue(session.ClientId, out ClientSession? current) && ReferenceEquals(current, session))
                    AdmitValidatedClient(session);
            });
            return;
        }
        lock (session.RelayLock)
            session.Credential = new ClientCredential(session.AssertedServiceName ?? "Steam", session.ClientId,
                session.TicketOwnerId == 0 ? session.ClientId : session.TicketOwnerId,
                session.AssertedName ?? session.ClientId.ToString(), _admission.IsAdmin(session.ClientId));
        ClientCredential credential = session.Credential!;
        Console.WriteLine($"Admitted {credential.ServiceName} client {credential.UserId} owner {credential.OwnerId}"
            + $" name '{credential.Name}' admin {credential.IsAdmin}");
        EndSteamReconnectStreak(credential.UserId);
        SteamPublishDirty();
        if (_registry != null) BeginPlayerAdmission(session);
        else lock (_pauseLock) { if (!RelayPaused) Attach(session, GetNodeLink(session.BackendEndpoint)); }
    }

    /// <summary>
    /// A perimeter rejection: the Gateway synthesizes JOIN_RESULT itself and the node never sees
    /// the client - nothing it sent was ever flushed, because a rejected session never attaches.
    /// </summary>
    private void RejectClient(ClientSession session, JoinResult reason)
    {
        Interlocked.Increment(ref _admissionRejections);
        Console.WriteLine($"Rejected client {session.ClientId} at the Gateway: {reason}");
        ClientChannel? channel = session.Client;
        lock (session.RelayLock)
        {
            session.AdmissionState = "Rejected";
            session.AdmissionError = reason.ToString();
            session.DisconnectReason ??= "admission-" + reason;
        }
        _disconnects.Enqueue(new(session.ClientId, DateTimeOffset.UtcNow, "admission-" + reason));
        while (_disconnects.Count > 100) _disconnects.TryDequeue(out _);
        if (channel?.Connected == true)
            channel.Send(new GatewayPacket(session.ClientId, 2, VanillaJoinCodec.EncodeJoinResultFrame(reason),
                DeliveryMethod.ReliableOrdered));
        RemoveSession(session);
        if (channel != null)
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(250); channel.Disconnect(); });
    }

    private void BeginPlayerAdmission(ClientSession session)
    {
		if (_registry == null) return;
		// Security.md §3.2: a session in TicketPending has no validated credential to bind an identity
		// from. The World Authority refresh sweep sees its CLIENT_CONNECTED and would otherwise race
		// the ticket callback; the ticket path starts admission itself once the credential lands.
		if (session.Credential == null) return;
		if (!PlayerAdmissionRequired(session))
		{
			session.AdmissionState = "Disabled";
			lock (_pauseLock)
				if (!RelayPaused) Attach(session, GetNodeLink(session.BackendEndpoint));
			return;
		}
		if (Interlocked.CompareExchange(ref session.AdmissionStarted, 1, 0) != 0) return;
		bool refresh = session.PlayerReady != null;
		long existingIdentityId = session.PlayerReady?.Identity.IdentityId ?? 0;
		long existingGeneration = session.PlayerReady?.AuthorityGeneration ?? 0;
		session.AdmissionState = refresh ? "Refreshing" : "Binding";
        _ = Task.Run(async () =>
        {
            try
            {
                WorldAuthority authority = _registry.WorldAuthority();
                if (authority.Node == null || authority.Generation <= 0) throw new InvalidOperationException("World Authority is unavailable");
                // ExpectedIdentityId tells a replacement World Authority which identity this session was
                // granted under the previous generation, so it can restore the binding even when its
                // journaled save predates the player's admission.
                // Security.md §3.5: every field here comes from the credential the Gateway validated,
                // never from client-asserted data - the identity is bound to the platform userId.
                ClientCredential credential = session.Credential
                    ?? throw new InvalidOperationException("Client has no validated credential");
                var request = new PlayerBindingRequest(credential.ServiceName, credential.UserId, credential.OwnerId, 0,
                    credential.Name, credential.IsAdmin, existingIdentityId);
                string payload = await SendGatewayCommand(authority, "players", JsonSerializer.Serialize(request), session.AdmissionCancellation.Token);
                GlobalCommandResult result = JsonSerializer.Deserialize<GlobalCommandResult>(payload)
                    ?? throw new InvalidDataException("Empty player binding response");
                if (!result.Success) throw new InvalidOperationException(result.Error);
                PlayerReady ready = JsonSerializer.Deserialize<PlayerReady>(result.Payload)
                    ?? throw new InvalidDataException("Empty player readiness response");
                WorldAuthority currentAuthority = _registry.WorldAuthority();
                if (currentAuthority.Node != authority.Node || currentAuthority.Epoch != authority.Epoch
                    || currentAuthority.Generation != authority.Generation || ready.AuthorityGeneration != authority.Generation
                    || ready.ServiceName != request.ServiceName || ready.UserId != request.UserId || ready.OwnerId != request.OwnerId
                    || ready.SerialId != request.SerialId || ready.IsAdmin != request.IsAdmin || ready.Identity.IdentityId <= 0
					|| existingIdentityId != 0 && existingGeneration != authority.Generation
						&& ready.Identity.IdentityId != existingIdentityId
                    || ready.Identity.Name == null || ready.Identity.Name.Length > 256 || ready.Identity.Model?.Length > 256)
                    throw new InvalidDataException("Player readiness response does not match the admission request");
				lock (session.RelayLock)
				{
					session.PlayerReady = ready;
					session.BindingRevision++;
					session.AdmissionState = "Attaching";
					session.AdmissionError = null;
				}
				Console.WriteLine($"Player binding granted client {session.ClientId} identity {ready.Identity.IdentityId} generation {ready.AuthorityGeneration}");
				AttachGrantedPlayer(session);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
				session.AdmissionState = refresh ? "Stale" : "Rejected";
                session.AdmissionError = exception.Message;
                Console.Error.WriteLine($"Player admission failed for {session.ClientId}: {exception.Message}");
				if (refresh) Interlocked.Exchange(ref session.AdmissionStarted, 0);
				else ForceReconnect(session, "player-admission-failed");
            }
        });
    }

	private void RefreshPlayerAdmissions()
	{
		WorldAuthority authority = _registry!.WorldAuthority();
		if (authority.Node == null || authority.Generation <= 0) return;
		foreach (ClientSession session in _clients.Values)
		{
			bool refresh;
			lock (session.RelayLock)
			{
				refresh = session.ClientConnectedPacket != null && session.Credential != null
					&& (session.PlayerReady == null || session.PlayerReady.AuthorityGeneration != authority.Generation)
					&& Volatile.Read(ref session.AdmissionStarted) == 0;
				if (refresh)
					session.AdmissionState = "Refreshing";
			}
			if (refresh) BeginPlayerAdmission(session);
		}
	}

	private void UpdatePlayerBindings(string payload, long generation)
	{
		IdentityAuthoritySnapshot snapshot = JsonSerializer.Deserialize<IdentityAuthoritySnapshot>(payload)
			?? throw new InvalidDataException("Empty identity snapshot");
		Dictionary<long, IdentityAuthorityEntry> identities = snapshot.Identities.ToDictionary(identity => identity.IdentityId);
		// Bindings indexed the same way the identities already were. The lookup below ran per connected
		// client over the WHOLE binding table - which holds every player ever registered, not just the
		// connected ones - on every identity broadcast from the World Authority.
		Dictionary<(ulong UserId, int SerialId), PlayerBindingEntry> bindings = [];
		foreach (PlayerBindingEntry entry in snapshot.Bindings) bindings[(entry.UserId, entry.SerialId)] = entry;
		foreach (ClientSession session in _clients.Values)
		{
			bool attach;
			lock (session.RelayLock)
			{
				PlayerReady? ready = session.PlayerReady;
				if (ready == null || ready.AuthorityGeneration != generation) continue;
				PlayerBindingEntry? binding = bindings.GetValueOrDefault((ready.UserId, ready.SerialId));
				if (binding == null || !identities.TryGetValue(binding.IdentityId, out IdentityAuthorityEntry? identity)
					|| ready.Identity == identity) continue;
				session.PlayerReady = ready with { Identity = identity };
				session.BindingRevision++;
				session.AdmissionState = "Attaching";
				attach = !session.SwitchingBackend;
			}
			if (attach)
				lock (_pauseLock)
					if (!RelayPaused) Attach(session, GetNodeLink(session.BackendEndpoint));
		}
	}

	private bool AcceptIdentitySnapshot(long generation, long sequence)
	{
		lock (_identitySnapshotLock)
		{
			if (generation < _identitySnapshotGeneration
				|| generation == _identitySnapshotGeneration && sequence <= _identitySnapshotSequence) return false;
			_identitySnapshotGeneration = generation;
			_identitySnapshotSequence = sequence;
			return true;
		}
	}

	/// <summary>
	/// T-0357: what follows "Player binding granted". <see cref="RetryPlayerAttach"/> on EVERY outcome. Before, a
	/// binding granted while the relay was paused or the backend was switching only set "Granted" and started
	/// nothing: 2026-10-01 23:44:39 a Steam join was bound while the gateway was still --paused for the boot (the
	/// CLI posts /resume when the node set has settled, 23:44:50), no Attach was sent, nothing was logged, and the
	/// 10 s client backlog age force-reconnected it at 23:44:48.
	/// SE1-0102: the retry sends the first Attach too, so the backlog age is held before it goes out. An Attach
	/// sent here first left a gap until the retry started in which a backlogExpired ended the session at once.
	/// </summary>
	private void AttachGrantedPlayer(ClientSession session)
	{
		lock (_pauseLock)
			if (RelayPaused || session.SwitchingBackend) session.AdmissionState = "Granted";
		RetryPlayerAttach(session);
	}

	/// <summary>How long an admitted client may wait for its node's AttachAck outside a deliberate hold.</summary>
	internal static TimeSpan AttachRetryTimeout = TimeSpan.FromSeconds(90);

	/// <summary>
	/// Why an admitted session is not attached right now; null = nothing to say (attached, or the Attach
	/// was sent a moment ago). <paramref name="held"/>: a deliberate hold (the relay pause, a backend
	/// switch), which its owner ends and which does not count against <see cref="AttachRetryTimeout"/>.
	/// </summary>
	private string? AttachWaitReason(ClientSession session, NodeLink link, TimeSpan waited, bool switchHold, out bool held)
	{
		held = true;
		if (RelayPaused) return "held: the relay is paused";
		if (switchHold) return "held: its backend is being switched";
		held = false;
		if (!link.IsConnected) return "not sent: node link not connected";
		if (_registry != null && session.PlayerReady is PlayerReady ready
			&& ready.AuthorityGeneration != _registry.WorldAuthorityGeneration)
			return "not sent: its player binding is being refreshed for a new World Authority generation";
		if (ReferenceEquals(session.AttachingLink, link))
			return waited >= TimeSpan.FromSeconds(2) ? "sent, the node has not acknowledged it" : null;
		// Whatever SendAttach named (and logged) itself.
		return session.AttachSkipped is string skipped ? "not sent: " + skipped : null;
	}

	/// <summary>
	/// T-0357: an admitted client is attached, or this keeps trying every 250 ms until the node acknowledges
	/// (a duplicate Attach is re-acknowledged by the node), the session ends, or
	/// <see cref="AttachRetryTimeout"/> of waiting outside a deliberate hold has passed. Every reason it waits is
	/// logged once per session and reason, the timeout and an unexpected failure have their own lines. While it
	/// runs the client backlog's age does not end the session (<see cref="ClientBacklogHeld"/>): the bound is this
	/// deadline, not the 10 s that used to pre-empt it.
	/// </summary>
	/// <param name="detachedBy">The link whose node sent Detach: re-attached even during a backend switch, as
	/// before (the switch waits for exactly that acknowledgement), until this retry ends. A granted binding is NOT
	/// attached during a switch, also as before: the switch attaches its own target.</param>
	private void RetryPlayerAttach(ClientSession session, NodeLink? detachedBy = null)
	{
		if (detachedBy != null) session.AttachRetryDetachedBy = detachedBy;
		if (Interlocked.CompareExchange(ref session.AttachRetryStarted, 1, 0) != 0) return;
		PushRouting(session);
		_ = Task.Run(async () =>
		{
			NodeLink? link = null;
			string? last = null;
			try
			{
				Stopwatch waited = Stopwatch.StartNew();
				DateTimeOffset deadline = DateTimeOffset.UtcNow + AttachRetryTimeout;
				while (true)
				{
					if (!_clients.TryGetValue(session.ClientId, out ClientSession? current) || !ReferenceEquals(current, session)
						|| session.DisconnectReason != null)
						return;
					link = session.Link ?? GetNodeLink(session.BackendEndpoint);
					bool switchHold;
					lock (_pauseLock)
					{
						switchHold = session.SwitchingBackend && !ReferenceEquals(session.AttachRetryDetachedBy, link);
						if (!RelayPaused && !switchHold) Attach(session, link);
					}
					lock (session.RelayLock)
						if (session.AdmissionState == "Ready"
							|| ReferenceEquals(session.AttachedLink, link) && session.AttachedBindingRevision == session.BindingRevision)
						{
							if (session.AttachWaitLogged != null)
								Console.WriteLine($"Attach of client {session.ClientId} to {link.Endpoint} acknowledged after {waited.Elapsed.TotalSeconds:0.0}s");
							session.AttachWaitLogged = null;
							return;
						}
					string? reason = AttachWaitReason(session, link, waited.Elapsed, switchHold, out bool held);
					if (reason != null)
					{
						last = reason;
						// SendAttach logs its own "not sent" reasons; the others are named here, once each.
						if (session.AttachWaitLogged != reason && !(session.AttachSkipped is string skipped && reason == "not sent: " + skipped))
							Console.WriteLine($"Attach of client {session.ClientId} to {link.Endpoint} {reason}; retrying");
						session.AttachWaitLogged = reason;
					}
					if (held) deadline = DateTimeOffset.UtcNow + AttachRetryTimeout;
					else if (DateTimeOffset.UtcNow >= deadline) break;
					await Task.Delay(250, session.AdmissionCancellation.Token);
				}
				Console.WriteLine($"Attach of client {session.ClientId} to {link.Endpoint} timed out after {AttachRetryTimeout.TotalSeconds:0}s"
					+ $" outside a hold ({last ?? "no reason seen"}); ending the session");
				ForceReconnect(session, "player-admission-attach-timeout");
			}
			catch (OperationCanceledException) { }
			// Attach() says so when the session ended under it: the disconnect has its own line.
			catch (IOException) when (!_clients.TryGetValue(session.ClientId, out ClientSession? current) || !ReferenceEquals(current, session)) { }
			catch (Exception exception)
			{
				// A retry that dies leaves an admitted client nobody attaches, and the backlog age no longer ends it.
				Console.Error.WriteLine($"Attach retry of client {session.ClientId} to {link?.Endpoint} failed: {exception}");
				ForceReconnect(session, "player-admission-attach-failed");
			}
			finally
			{
				// #1279: the exemption belongs to this retry. Left set, every later switch let a granted binding
				// attach to that link mid-switch. Cleared before the retry is released, so a new one starts clean.
				session.AttachRetryDetachedBy = null;
				Interlocked.Exchange(ref session.AttachRetryStarted, 0);
				PushRouting(session);
			}
		});
	}

	/// <summary>
	/// T-0357: the client backlog's age is evidence of a stuck client only when nothing is deliberately holding
	/// it. Held = the relay pause or a backend switch (what the relay itself already exempts when a packet arrives
	/// in the hold) or an attach that is still being retried, which <see cref="RetryPlayerAttach"/> bounds. Says
	/// so once per session and reason. External relay only.
	/// </summary>
	private bool ClientBacklogHeld(ClientSession session)
	{
		string? reason = RelayPaused ? "the relay is paused"
			: session.SwitchingBackend ? "its backend is being switched"
			: Volatile.Read(ref session.AttachRetryStarted) != 0 ? "its attach is still being retried"
			: null;
		if (reason != null && session.BacklogHoldLogged != reason)
			Console.WriteLine($"Client {session.ClientId} backlog age not enforced: {reason}");
		session.BacklogHoldLogged = reason;
		return reason != null;
	}

    private async Task<string> SendGatewayCommand(WorldAuthority authority, string subsystem, string payload, CancellationToken cancellationToken,
        Guid? stableOperationId = null)
    {
        NodeRegistration wa = _registry!.Nodes().Single(node => node.Node == authority.Node && node.Epoch == authority.Epoch
            && node.Role == NodeRole.WA && node.State == NodeState.Active);
        NodeLink link = GetNodeLink(EndpointParser.Parse(wa.Endpoint, DirectTransportConstants.DefaultPort));
        ConnectBackend(link);
        if (!link.Connected.Wait(TimeSpan.FromSeconds(10), cancellationToken)) throw new TimeoutException("Timed out connecting to the World Authority");
        long sequence = Interlocked.Increment(ref _gatewayCommandSequence);
		Guid operationId = stableOperationId ?? Guid.NewGuid();
		var pending = new PendingGatewayCommand(authority.Generation, subsystem, operationId,
			new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_gatewayCommands.TryAdd(sequence, pending)) throw new InvalidOperationException("Duplicate Gateway command sequence");
        try
        {
            byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new GlobalEnvelope("Command", authority.Generation, subsystem,
				sequence, "$gateway", null, payload, operationId));
            SendGlobal(link, envelope, DeliveryMethod.ReliableUnordered);
			return await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally { _gatewayCommands.TryRemove(sequence, out _); }
    }

    private void ConnectBackend(NodeLink link)
    {
        if (link.External)
        {
            if (!link.ExternalConnected)
                try { _relay?.Send(RelayIpcType.EnsureLink, 0, new { endpoint = link.Endpoint.ToString() }); }
                catch (IOException) { }
            return;
        }
        lock (link.Sync)
        {
            if (link.Peer?.ConnectionState is ConnectionState.Connected or ConnectionState.Outgoing)
                return;
            link.Connected.Reset();
            link.Peer = _back.Connect(link.Endpoint.Address.ToString(), link.Endpoint.Port,
                DirectTransportCodec.EncodeNodeLinkHandshake(_options.JoinToken ?? string.Empty));
            link.Peer.Tag = link;
        }
    }

    private void Attach(ClientSession session, NodeLink link)
    {
        lock (session.RelayLock)
        {
            if (!_clients.TryGetValue(session.ClientId, out ClientSession? current) || !ReferenceEquals(current, session))
                throw new IOException("Client disconnected during backend attach");
            link.Clients[session.ClientId] = session;
            session.Link = link;
            session.BackendEndpoint = link.Endpoint;
        }
        ConnectBackend(link);
        if (link.IsConnected)
            SendAttach(link, session);
    }

    private void SwitchBackend(ClientSession session, IPEndPoint backend)
    {
        bool ownsSwitch = !session.SwitchingBackend;
        bool completed = false;
        try
        {
			if (ownsSwitch)
			{
				EnsurePlayerAdmissionReady(session);
				session.SwitchingBackend = true;
			}
            NodeLink target = GetNodeLink(backend);
            ConnectBackend(target);
            if (!target.Connected.Wait(10000))
                throw new TimeoutException("Timed out connecting backend " + backend);
            lock (target.Sync)
            {
                if (!target.IsConnected)
                    throw new IOException("Backend disconnected during switch " + backend);
            }
            NodeLink? source;
            lock (session.RelayLock)
                source = session.Link;
            if (source != null && !ReferenceEquals(source, target))
            {
                if (SendDetach(source, session) && !session.DetachCompleted.Wait(10000))
                    throw new TimeoutException("Timed out detaching source backend " + source.Endpoint);
                source.Clients.TryRemove(session.ClientId, out _);
            }
            lock (target.Sync)
            {
                if (!target.IsConnected)
                    throw new IOException("Backend disconnected during switch " + backend);
            }
			DateTimeOffset attachDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
			while (true)
			{
				Attach(session, target);
				TimeSpan remaining = attachDeadline - DateTimeOffset.UtcNow;
				if (remaining <= TimeSpan.Zero || !session.AttachCompleted.Wait(remaining))
					throw new IOException("Backend failed to attach client during switch " + backend);
				lock (session.RelayLock)
					if (ReferenceEquals(session.AttachedLink, target)
						&& session.AttachedBindingRevision == session.BindingRevision) break;
			}
            completed = true;
        }
        finally
        {
            if (ownsSwitch)
            {
				bool healthy = FinishSwitching(session, () =>
				{
					if (RouteHealthy(session)) FlushClientBacklog(session);
				});
                if (!healthy)
                    session.Client?.Disconnect();
                if (completed && !healthy)
                    throw new IOException("Backend disconnected while completing switch " + backend);
            }
        }
    }

	private void EnsurePlayerAdmissionReady(ClientSession session)
	{
		if (_registry == null || !PlayerAdmissionRequired(session)) return;
		if (Volatile.Read(ref session.AdmissionStarted) == 0) BeginPlayerAdmission(session);
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(90);
		while (DateTimeOffset.UtcNow < deadline)
		{
			WorldAuthority authority = _registry.WorldAuthority();
			if (authority.Node != null && session.PlayerReady is PlayerReady ready
				&& ready.AuthorityGeneration == authority.Generation && session.AdmissionState == "Ready"
				&& session.AttachedBindingRevision == session.BindingRevision
				&& session.AttachedAdmissionGeneration == ready.AuthorityGeneration
				&& session.AttachedIdentityId == ready.Identity.IdentityId) return;
			if (authority.Node != null && Volatile.Read(ref session.AdmissionStarted) == 0) BeginPlayerAdmission(session);
			if (session.Client?.Connected != true)
				throw new IOException("Client disconnected while waiting for current World Authority admission");
			Thread.Sleep(50);
		}
		throw new TimeoutException("Timed out waiting for current World Authority player admission");
	}

	private bool PlayerAdmissionRequired(ClientSession session) =>
		_registry != null && (_registry.WorldAuthorityGeneration > 0 || session.PlayerReady != null);

	private bool AttachedBindingCurrent(ClientSession session)
	{
		if (!PlayerAdmissionRequired(session)) return true;
		// Lock-free: this runs for every client-to-node packet (see WorldAuthorityGeneration).
		long generation = _registry!.WorldAuthorityGeneration;
		return session.PlayerReady is PlayerReady ready && ready.AuthorityGeneration == generation
			&& session.AttachedBindingRevision == session.BindingRevision
			&& session.AttachedAdmissionGeneration == ready.AuthorityGeneration
			&& session.AttachedIdentityId == ready.Identity.IdentityId;
	}

	// A fully attached session stays routable while an authority replacement refreshes its
	// admission: the node already acknowledged this client under this identity, the admission
	// validation rejects any identity change across generations, and the backend link is
	// unchanged — so the packets go to the same node under the same identity either way.
	// Buffering them instead would overflow ClientBacklog during the outage window and turn a
	// World Authority outage into a client disconnect.
	private bool AttachedBindingRoutable(ClientSession session)
	{
		if (AttachedBindingCurrent(session)) return true;
		return session.PlayerReady is PlayerReady ready
			&& session.AttachedIdentityId != 0
			&& session.AttachedIdentityId == ready.Identity.IdentityId;
	}

	private void EnsureAttachedBindingCurrent(ClientSession session)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (true)
		{
			NodeLink? link;
			lock (session.RelayLock)
			{
				if (AttachedBindingCurrent(session)) return;
				link = session.Link;
			}
			if (link == null) throw new IOException("Client has no backend for player admission");
			if (session.PlayerReady?.AuthorityGeneration != _registry!.WorldAuthority().Generation)
				EnsurePlayerAdmissionReady(session);
			else
				Attach(session, link);
			TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
			if (remaining <= TimeSpan.Zero || !session.AttachCompleted.Wait(remaining))
				throw new TimeoutException("Timed out attaching the current player binding");
		}
	}

	private bool FinishSwitching(ClientSession session, Action completion)
		=> FinishSwitching(session.RelayLock, () => RouteHealthy(session), () => EnsureAttachedBindingCurrent(session),
			() => AttachedBindingCurrent(session), () => session.SwitchingBackend = false, completion,
			failure => Console.WriteLine($"Client {session.ClientId} could not re-attach its player binding while completing a switch: "
				+ $"{failure.Message}; ending the switch as unhealthy"));

	/// <summary>
	/// The end of every backend switch: clears <c>SwitchingBackend</c> and runs <paramref name="completion"/> under
	/// the relay lock; false when the route is not healthy (the caller disconnects the client).
	/// </summary>
	internal static bool FinishSwitching(object relayLock, Func<bool> routeHealthy, Action ensureBindingCurrent,
		Func<bool> bindingCurrent, Action clearSwitching, Action completion, Action<Exception> report)
	{
		while (true)
		{
			bool healthy;
			lock (relayLock) healthy = routeHealthy();
			// T-0296 r3: the re-attach throws when the binding cannot be made current in time (the World Authority
			// replaced or away while the switch completes). The switch still has to END: the exception used to leave
			// SwitchingBackend set, so the relay kept buffering the client both ways with the backlog age bound off,
			// the rest of a crew was never un-frozen, and RespawnSwitch never released its SwitchLock.
			Exception? failure = null;
			if (healthy)
				try { ensureBindingCurrent(); }
				catch (Exception e) { failure = e; }
			lock (relayLock)
			{
				healthy = failure == null && routeHealthy();
				if (failure != null) report(failure);
				if (healthy && !bindingCurrent()) continue;
				clearSwitching();
				completion();
				return healthy;
			}
		}
	}

    private void StartControl()
    {
        if (_options.Control == null)
            return;

        _control = new HttpListener();
        // T-0321: a LAN --control also listens on 127.0.0.1:<port>, so every loopback tool keeps working unchanged;
        // the credential is decided per request from the prefix it arrived on (LoopbackExempt).
        string[] prefixes = ClusterProtocol.ClusterNet.ControlPrefixes(_options.Control.Address, _options.Control.Port, "/");
        foreach (string prefix in prefixes) _control.Prefixes.Add(prefix);
        _control.Start();
        // T-0324: without TCP_NODELAY a 16-65 KB Content-Length response (or a chunked tail) waited ~44 ms for the
        // client's delayed ACK on a same-host kept-alive connection.
        ClusterProtocol.HttpListenerNoDelay.Apply(_control, Console.WriteLine);
        _controlThread = new Thread(ControlLoop) { Name = "Gateway-Control", IsBackground = true };
        _controlThread.Start();
        Console.WriteLine($"Gateway control listening on {_options.Control}"
            + (prefixes.Length > 1 ? $" and 127.0.0.1:{_options.Control.Port} (loopback)" : ""));
    }

    internal CostModel CurrentCostModel => _costModel;

    /// <summary>
    /// #1278 (F35): POST /balance/cost-model runs on the thread pool, so two overrides can race. Unfenced, both read the
    /// same model and the later write drops the earlier one's field; under the lock each applies on top of the other.
    /// </summary>
    internal CostModel OverrideCostModel(IEnumerable<KeyValuePair<string, string>> overrides)
    {
        lock (_costModelSwap)
            return _costModel = CostModel.WithOverrides(_costModel, overrides);
    }

    private void ControlLoop()
    {
        while (_running && _control != null)
        {
            try
            {
                HttpListenerContext context = _control.GetContext();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { HandleControl(context); }
                    catch (Exception e) { Console.Error.WriteLine("Control request failed: " + e); }
                });
            }
            catch (HttpListenerException) { return; }
            catch (Exception e) { Console.Error.WriteLine("Control failed: " + e); }
        }
    }

    private void HandleControl(HttpListenerContext context)
    {
        // T-0411: a request the listener already refused with 411 must not run: the caller was told it failed.
        if (ClusterProtocol.HttpListenerLengthRequired.AlreadyAnswered(context.Request.HttpMethod,
                context.Request.Headers["Content-Length"], context.Request.Headers["Transfer-Encoding"]))
        {
            Console.Error.WriteLine("Control request refused: "
                + ClusterProtocol.HttpListenerLengthRequired.Describe(context.Request.HttpMethod, context.Request.Url?.AbsolutePath));
            return;
        }
        try
        {
            string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            try { _registry?.EnsurePersistenceHealthy(); }
            catch (IOException)
            {
                WriteAdminError(context, 503, "registry_persistence_failed", "Registry requires restart and durable recovery.");
                return;
            }
            // T-0321 P4 fault injection: a silenced host's nodes and executor get no answer at all (a cut, not a refusal).
            if (FaultSilenced(context, path)) return;
            if (path.Equals(CometWorks.ClusterGateway.AdminContract.V1.ExecutorProtocol.HeartbeatRoute, StringComparison.OrdinalIgnoreCase))
            {
                HandleExecutorRequest(context);
                return;
            }
            if (path == "/registry/plugins/v1") { HandlePluginOperation(context, path); return; }
            if (path.Equals(SkinCheckRoute, StringComparison.OrdinalIgnoreCase)) { HandleSkinCheck(context, path); return; }
            if (IsAdminRequestPath(path))
            {
                // The admin contract has its own credential resolution: scoped tokens from the
                // token file are valid here even though the legacy routes do not know them.
                AdminActor? actor = ResolveAdminActor(context);
                string? refusal = null;
                // SE-1197: a node acting for an admin is held to the moderator rules even where loopback would let
                // it in unauthenticated; an admin token (Quasar, the CLI) never names a moderator.
                if (context.Request.Headers[ClusterProtocol.ClusterNet.ModeratorHeader] != null && actor is null or { Name: "loopback" })
                    actor = NodeModerator(context, path, loopback: actor != null, out refusal);
                if (actor == null && refusal != null)
                {
                    WriteAdminError(context, 403, "moderator_refused", refusal);
                    return;
                }
                if (actor == null)
                {
                    context.Response.Headers["WWW-Authenticate"] = "Bearer";
                    WriteAdminError(context, 401, "bearer_token_required", "A valid admin bearer token is required.");
                    return;
                }
                HandleAdminRequest(context, path, actor);
                return;
            }
            if ((_options.AdminToken is not null || _adminTokens is not null
                    || _options.Control != null && !IPAddress.IsLoopback(_options.Control.Address))
                && !LoopbackExempt(context) && !Authorized(context) && !NodeAuthorized(context, path))
            {
                RefuseControlCredential(context, path);
                return;
            }
            if (context.Request.HttpMethod == "GET" && path.Equals("/transport/status", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, TransportStatus());
                return;
            }
            // The balancer's own view of the last evaluation, so monitors can tell gameplay
            // concentration (high excess, deviation ~0) from a placement backlog (high deviation)
            // without re-deriving the optimizer's result. See BalanceDiagnostics.
            if (context.Request.HttpMethod == "GET" && path.Equals("/balance/status", StringComparison.OrdinalIgnoreCase))
            {
                BalanceDiagnostics? d = _balanceDiagnostics;
                CostModel model = _costModel;
                DateTime statusNow = DateTime.UtcNow;
                var hour = _balancePace.LastHour(statusNow);
                WriteJson(context, 200, d == null
                    ? new { evaluated = false }
                    : (object)new
                    {
                        evaluated = true,
                        evaluatedUtc = d.EvaluatedUtc,
                        excess = d.Excess,
                        achievableExcess = d.AchievableExcess,
                        worstDeviation = d.WorstDeviation,
                        clientExcess = d.ClientExcess,
                        worstClientDeviation = d.WorstClientDeviation,
                        movesPlanned = d.MovesPlanned,
                        pauseReason = d.PauseReason,
                        tolerance = model.BalanceRiskTolerance,
                        // T-0393: the hysteresis state and the pace counters over the last 3600 s (BalancePace.LastHour).
                        lowWater = model.EffectiveLowWater(),
                        balancingActive = _balancePace.Active,
                        pinsLastHour = hour.Pins,
                        minGainSkipsLastHour = hour.MinGainSkips,
                        budgetSkipsLastHour = hour.BudgetSkips,
                        deadbandHoldsLastHour = hour.DeadbandHolds,
                        residenceHoldsLastHour = hour.ResidenceHolds,
                        budgetMovesPerNodeHour = model.BudgetMovesPerNodeHour,
                        budgetUnderDeviation = model.BudgetUnderDeviation,
                        targetPinsLastHour = _balancePace.TargetPinsLastHour(statusNow),
                        // T-0409: the load floor and the paced passes it has held since start.
                        balanceMinPeakLoad = model.BalanceMinPeakLoad,
                        floorHeldPasses = Interlocked.Read(ref _balanceFloorHeldPasses),
                        // T-0409 follow-up: the peak-drop guard, its refused candidates and the passes it left planless.
                        balanceMinPeakDrop = model.BalanceMinPeakDrop,
                        peakDropSkips = Interlocked.Read(ref _balancePeakDropSkips),
                        peakDropHeldPasses = Interlocked.Read(ref _balancePeakDropHeldPasses),
                        // #1454: the heavy components of the last pass (keyed by their smallest member id).
                        heavyClients = model.HeavyClients,
                        heavyReleaseClients = model.HeavyReleaseClients,
                        heavy = _heavyLogged.Select(x => new { partition = x.Key, node = x.Value.Node, clients = x.Value.Clients }).ToArray(),
                    });
                return;
            }
            // T-0391: read or override the balancer's cost model live, e.g. POST /balance/cost-model?ClientBoundMaxMembers=0.
            // Not persisted: a restart reads --cost-model again.
            if (path.Equals("/balance/cost-model", StringComparison.OrdinalIgnoreCase)
                && (context.Request.HttpMethod == "GET" || context.Request.HttpMethod == "POST"))
            {
                if (context.Request.HttpMethod == "POST")
                {
                    var overrides = context.Request.QueryString.AllKeys.Where(key => key != null)
                        .Select(key => new KeyValuePair<string, string>(key!, context.Request.QueryString[key] ?? "")).ToArray();
                    if (overrides.Length == 0) throw new ArgumentException("POST /balance/cost-model needs at least one Field=value");
                    CostModel after = OverrideCostModel(overrides);
                    Console.WriteLine($"Cost model overridden live ({string.Join(", ", overrides.Select(o => o.Key + "=" + o.Value))}): {JsonSerializer.Serialize(after)}");
                    WriteJson(context, 200, after);
                    return;
                }
                WriteJson(context, 200, _costModel);
                return;
            }
            if (context.Request.HttpMethod == "GET" && path.Equals("/diag", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, DiagSnapshot(context));
                return;
            }
            if (context.Request.HttpMethod == "POST" && path.StartsWith("/trigger/", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, RunTrigger(context, path["/trigger/".Length..]));
                return;
            }
            // The ban and kick books are cluster-scoped and authoritative at the perimeter
            // (Security.md §3.4), so admin actions on them land here, not on a node.
            if (context.Request.HttpMethod == "POST" && path.Equals("/admission/ban", StringComparison.OrdinalIgnoreCase))
            {
                ulong user = ulong.Parse(Required(context, "user"));
                bool banned = context.Request.QueryString["banned"] != "false";
                _admission.Ban(user, banned);
                if (banned && _clients.TryGetValue(user, out ClientSession? bannedSession))
                    ForceReconnect(bannedSession, "banned-by-admins");
                Console.WriteLine($"Admin {(banned ? "banned" : "unbanned")} user {user}");
                WriteJson(context, 200, new { user, banned });
                return;
            }
            if (context.Request.HttpMethod == "POST" && path.Equals("/admission/kick", StringComparison.OrdinalIgnoreCase))
            {
                ulong user = ulong.Parse(Required(context, "user"));
                // An admin kick is punitive and arms the anti-flap cooldown. A §6.6 control-liveness
                // forced reconnect is the opposite: the player is being rescued, and the remedy is
                // that they come straight back through the vanilla join path. Sending it down the
                // punitive path locked the stuck player out for the whole KickTimeout - the fix
                // undoing the very promise it exists to keep.
                bool reconnect = string.Equals(context.Request.QueryString["reason"], "reconnect",
                    StringComparison.OrdinalIgnoreCase);
                if (!reconnect) _admission.Kick(user);
                if (_clients.TryGetValue(user, out ClientSession? kicked))
                    ForceReconnect(kicked, reconnect ? "control-liveness-reconnect" : "kicked-by-admins");
                Console.WriteLine(reconnect
                    ? $"Forced reconnect for user {user} (control liveness); admission cooldown not armed"
                    : $"Admin kicked user {user}");
                WriteJson(context, 200, new { user, kicked = true, cooldown = !reconnect });
                return;
            }
            // Security.md §9 items 1-2: every rejection path and the mid-session revocation have to
            // be exercised, and Steam issues no VAC ban, expired licence or replayed ticket on
            // demand. With no user this arms a one-shot override of the next real verdict (the
            // rejection paths, with a real client and a real ticket); with a user it injects the
            // verdict into that live session (the revocation path). Debug frontend only - the same
            // fail-closed gate as accept-all admission (§3.6).
            if (context.Request.HttpMethod == "POST" && path.Equals("/admission/test-ticket-verdict", StringComparison.OrdinalIgnoreCase))
            {
                if (!_options.DirectFrontend || !SteamEnabled)
                    throw new ArgumentException("Test ticket verdicts require --direct and a Steam frontend");
                var result = Enum.Parse<JoinResult>(Required(context, "result"), ignoreCase: true);
                string? user = context.Request.QueryString["user"];
                if (user == null)
                {
                    Volatile.Write(ref _armedTicketVerdict, (int)result);
                    WriteJson(context, 200, new { armed = result.ToString() });
                    return;
                }
                ulong userId = ulong.Parse(user);
                if (!_clients.ContainsKey(userId))
                    throw new ArgumentException($"No client session for {userId}");
                Console.WriteLine($"Injecting ticket verdict {result} for client {userId} (test)");
                OnSteamTicket(userId, result, 0);
                WriteJson(context, 200, new { user = userId, injected = result.ToString() });
                return;
            }
            // SE1-0084: a node's member entered or left the fast state; pull every report now (AutomaticHandoverLoop).
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/report-now", StringComparison.OrdinalIgnoreCase))
            {
                _passNow.Set();
                FastPartitionCounters.PassRequest();
                WriteJson(context, 200, new { requested = true });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/status", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.Status());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/node-plan", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.NodePlan());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/node-plan", StringComparison.OrdinalIgnoreCase))
            {
                _registry.EnsureLegacyExecutorAllowed();
                NodeSlotRequest request = ReadJson<NodeSlotRequest>(context);
                WriteJson(context, 200, _registry.PutNodeSlot(request.SlotKey, request.Host, request.Role, request.Desired));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/executors/heartbeat", StringComparison.OrdinalIgnoreCase))
            {
                ExecutorHeartbeatRequest request = ReadJson<ExecutorHeartbeatRequest>(context);
                WriteJson(context, 200, _registry.ExecutorHeartbeat(request.Executor, request.Observations));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/shutdown/graceful", StringComparison.OrdinalIgnoreCase))
            {
                double timeout = PositiveSeconds(context, "timeoutSeconds", 900);
                double forceAfter = PositiveSeconds(context, "forceAfterSeconds", 900);
                WriteJson(context, 200, ShutdownCluster(false, TimeSpan.FromSeconds(timeout), TimeSpan.FromSeconds(forceAfter)));
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(100); Stop(); });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/shutdown/emergency", StringComparison.OrdinalIgnoreCase))
            {
                double timeout = PositiveSeconds(context, "timeoutSeconds", 30);
                WriteJson(context, 200, ShutdownCluster(true, TimeSpan.FromSeconds(timeout), TimeSpan.Zero));
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(100); Stop(); });
                return;
            }
            if (context.Request.HttpMethod == "GET" && path.Equals("/handover/paused", StringComparison.OrdinalIgnoreCase))
            {
                // With gateway-rs the held client packets queue there, not in this process.
                Dictionary<ulong, (long Count, long Bytes)> external = _handoverPauses.IsEmpty
                    ? new Dictionary<ulong, (long Count, long Bytes)>() : ExternalClientBacklog(ExternalRelayStatus().Status);
                WriteJson(context, 200, _handoverPauses.Values.Select(pause =>
                {
                    (long Count, long Bytes) held = default;
                    bool relayed = pause.Session != null && external.TryGetValue(pause.Session.ClientId, out held);
                    return new
                    {
                        transferId = pause.TransferId,
                        partition = pause.Partition,
                        client = pause.Session?.ClientId,
                        paused = !pause.Release.Task.IsCompleted,
                        queuedPacketCount = relayed ? held.Count : pause.Session?.ClientBacklog.Count ?? 0,
                        queuedBytes = relayed ? held.Bytes : pause.Session?.ClientBacklog.Bytes ?? 0,
                    };
                }).ToArray());
                return;
            }
            if (IsControlVerb(context) && path.Equals("/handover/release", StringComparison.OrdinalIgnoreCase))
            {
                string transferId = Required(context, "transferId");
                if (!_handoverPauses.TryGetValue(transferId, out HandoverPause? pause))
                {
                    WriteJson(context, 404, new { released = false, transferId });
                    return;
                }
                pause.Release.TrySetResult();
                WriteJson(context, 200, new { released = true, transferId });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                _registry.Checkpoint();
                WriteJson(context, 200, _registry.Status());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/nodes", StringComparison.OrdinalIgnoreCase))
            {
                NodeRegistration registered = _registry.RegisterNode(ReadJson<NodeJoinRequest>(context));
                // §2: a relaunched or rotated node starts with the current handover-config document.
                System.Text.Json.Nodes.JsonObject registration = (System.Text.Json.Nodes.JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(registered)!;
                registration["HandoverConfig"] = System.Text.Json.JsonSerializer.SerializeToNode(_registry.HandoverConfig());
                WriteJson(context, 200, registration);
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/network-id-blocks", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.AllocateNetworkIdBlock(Required(context, "node"), long.Parse(Required(context, "epoch"))));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/network-id-blocks", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.NetworkIdBlocks());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/entity-id-ranges", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.AllocateEntityIdRange(Required(context, "node"), long.Parse(Required(context, "epoch"))));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/entity-id-ranges", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.EntityIdRanges());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/client-diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                string node = Required(context, "node");
                long epoch = long.Parse(Required(context, "epoch"), System.Globalization.CultureInfo.InvariantCulture);
                WriteJson(context, 200, _registry.ClientDiagnostics(node, epoch));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/nodes", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.Nodes());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/world-authority", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.WorldAuthority());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/client-routes", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.RouteClient(ulong.Parse(Required(context, "client")), ulong.Parse(Required(context, "partition")),
                    long.TryParse(context.Request.QueryString["entity"], out long entity) ? entity : 0));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/client-routes", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.ResolveClientRoute(ulong.Parse(Required(context, "client")))
                    ?? throw new ArgumentException("Client route is unavailable"));
                return;
            }
            if (_registry != null && path.Equals("/registry/character-homes", StringComparison.OrdinalIgnoreCase))
            {
                // Where a client's character lives, so a rejoin can be answered with the node holding
                // the body instead of the default backend (Clustering §6.4). Nodes report it for the
                // players they host; the last report survives the log-off, which is the whole point.
                ulong homeClient = ulong.Parse(Required(context, "client"));
                if (context.Request.HttpMethod == "GET")
                {
                    WriteJson(context, 200, _registry.GetCharacterHome(homeClient)
                        ?? throw new ArgumentException($"No character home for client {homeClient}"));
                    return;
                }
                if (IsControlVerb(context))
                {
                    try
                    {
                        long version = _registry.SetCharacterHome(homeClient, long.Parse(Required(context, "identity")),
                            long.Parse(Required(context, "entity")), ulong.Parse(Required(context, "partition")),
                            Required(context, "node"), long.Parse(Required(context, "epoch")),
                            long.Parse(Required(context, "generation")), long.Parse(Required(context, "expectedVersion")),
                            long.Parse(Required(context, "expectedWriteRevision")));
                        WriteJson(context, 200, new { stored = true, version,
                            writeRevision = _registry.GetCharacterHome(homeClient)!.WriteRevision });
                    }
                    catch (RegistryConflictException error)
                    {
                        CharacterHome? current = _registry.GetCharacterHome(homeClient);
                        WriteJson(context, 409, new { stored = false, error = error.Message,
                            version = current?.Version ?? 0, writeRevision = current?.WriteRevision ?? 0 });
                    }
                    return;
                }
            }
            if (_registry != null && path.Equals("/registry/character-reaps", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod == "GET")
                    WriteJson(context, 200, _registry.PendingCharacterReaps(Required(context, "node"),
                        long.Parse(Required(context, "epoch"))));
                else if (context.Request.HttpMethod == "POST")
                    WriteJson(context, 200, _registry.AuthorizeCharacterReap(ulong.Parse(Required(context, "client")),
                        long.Parse(Required(context, "identity")), long.Parse(Required(context, "entity")),
                        Required(context, "node"), long.Parse(Required(context, "epoch")),
                        ulong.Parse(Required(context, "partition")), long.Parse(Required(context, "generation")),
                        long.Parse(Required(context, "homeVersion")), long.Parse(Required(context, "writeRevision"))));
                else throw new ArgumentException("Character reap expects GET or POST");
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST"
                && path.Equals("/registry/character-reaps/resolve", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var permit = JsonSerializer.Deserialize<ClusterProtocol.CharacterReapPermit>(reader.ReadToEnd())
                    ?? throw new ArgumentException("Character reap permit is required");
                _registry.ResolveCharacterReap(ulong.Parse(Required(context, "client")), permit);
                WriteJson(context, 200, new { resolved = true });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/teleport-intents", StringComparison.OrdinalIgnoreCase))
            {
                // Partitioning.md §7.4: the node that just teleported (or admin-moved) a character
                // registers the client here, so the co-location that resolves the resulting cross-node
                // gap moves the CHARACTER side to the destination's owner instead of §7.5-pricing the
                // encounter (which would pull a small destination partition to the player).
                ulong travellingClient = ulong.Parse(Required(context, "client"));
                lock (_teleportIntents) _teleportIntents[travellingClient] = DateTimeOffset.UtcNow;
                WriteJson(context, 200, new { accepted = true, client = travellingClient });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/pull-demands", StringComparison.OrdinalIgnoreCase))
            {
                // WA-4/WA-6 on-demand partition pull: a node reports that a client it hosts demanded a grid
                // another node antenna-publishes. The gateway owns the transfer: a fenced clientless handover
                // of the grid's partition to the demanding node, after which vanilla on-demand replication
                // serves the client's next attempt. Clientless refuses partitions holding clients by design.
                ulong pullPartition = ulong.Parse(Required(context, "partition"));
                string pullSource = Required(context, "source");
                string pullTarget = Required(context, "target");
                PartitionOwnership pullOwner;
                try { pullOwner = _registry.GetPartitionOwner(pullPartition); }
                catch (Exception)
                {
                    // A demand can race the owner's death and lease expiry; refuse it cleanly - the
                    // caller retries after recovery reassigns the partition.
                    WriteJson(context, 200, new { accepted = false, reason = "partition ownership is unavailable" });
                    return;
                }
                if (IsFastPartition(pullPartition))
                {
                    FastPartitionCounters.Pull();
                    WriteJson(context, 200, new { accepted = false, reason = "partition is fast (T-0292): it stays with its owner until it slows down" });
                    return;
                }
                if (pullOwner.Node != pullSource)
                {
                    WriteJson(context, 200, new { accepted = false, reason = "partition is not owned by the reported source" });
                    return;
                }
                if (PullPinRefusal(MigrationTarget(pullPartition), pullTarget) is { } pinned)
                {
                    WriteJson(context, 200, new { accepted = false, reason = pinned });
                    return;
                }
                NodeRegistration? pullSourceNode = _registry.Nodes().SingleOrDefault(node => node.Node == pullSource
                    && node.Role == NodeRole.Regular && node.State == NodeState.Active);
                NodeRegistration? pullTargetNode = _registry.Nodes().SingleOrDefault(node => node.Node == pullTarget
                    && node.Role == NodeRole.Regular && node.State == NodeState.Active);
                if (pullSourceNode?.ControlEndpoint == null || pullTargetNode?.ControlEndpoint == null)
                {
                    WriteJson(context, 200, new { accepted = false, reason = "source or target node is not active with a control endpoint" });
                    return;
                }
                // Read the source now: a pin may be newer than the scheduler's cached report.
                if (GetPartitions(StripScheme(pullSourceNode.ControlEndpoint)).Partitions
                    .Any(p => p.Id == pullPartition && p.RemoteUsers is { Length: > 0 }) || _registry.HasRemoteUse(pullPartition))
                {
                    WriteJson(context, 200, new { accepted = false, reason = "remote-use" });
                    return;
                }
                lock (_pullLock)
                {
                    if (_pullCooldowns.TryGetValue(pullPartition, out DateTimeOffset lastPull)
                        && DateTimeOffset.UtcNow - lastPull < TimeSpan.FromSeconds(60))
                    {
                        WriteJson(context, 200, new { accepted = false, reason = "cooldown" });
                        return;
                    }
                    _pullCooldowns[pullPartition] = DateTimeOffset.UtcNow;
                }
                // T-0296 r3: a retiring target is draining; the scale-in pass would pin the pulled partition straight out again.
                if (_registry.IsRetiring(pullTarget))
                {
                    WriteJson(context, 200, new { accepted = false, reason = "the target node is retiring (draining for node control)" });
                    return;
                }
                Console.WriteLine($"Pull demand partition {pullPartition} {pullSource} -> {pullTarget}"
                    + $" grid {context.Request.QueryString["grid"]} client {context.Request.QueryString["client"]}");
                WaitRotation("pull", pullPartition, true);
                try
                {
                    WhenRegistryApplied(pullPartition, () => FencedClientlessHandover(StripScheme(pullSourceNode.ControlEndpoint),
                        StripScheme(pullTargetNode.ControlEndpoint), EndpointParser.Parse(pullTargetNode.Endpoint, DirectTransportConstants.DefaultPort),
                        pullPartition)).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    // The source can die between its last heartbeat and the fenced handover (it still
                    // reads as Active here). Nothing was transferred - refuse cleanly; the caller's
                    // retry lands after recovery reassigns the partition.
                    Console.WriteLine($"Pull demand partition {pullPartition} refused: {exception.Message}");
                    WriteJson(context, 200, new { accepted = false, reason = exception.Message == "remote-use" ? "remote-use" : "the source node did not serve the handover" });
                    return;
                }
                finally { _rotation.Release(); }
                // A pull toward the pin's own target was the migration's move: release that member's pin.
                OnMigrationProgress(pullPartition, pullTarget);
                WriteJson(context, 200, new { accepted = true, partition = pullPartition, source = pullSource, target = pullTarget });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/pod-placement", StringComparison.OrdinalIgnoreCase))
            {
                // WA-6 pod placement: a respawn-ship spawn creates its own fresh partition, so unlike a
                // medbay respawn it can execute anywhere - place it on the least-loaded node.
                PartitionOwnership[] placementPartitions = _registry.Partitions();
                NodeRegistration[] placementCandidates = _registry.Nodes()
                    .Where(node => node.Role == NodeRole.Regular && node.State == NodeState.Active && node.Serving && !node.Spare
                        && !string.IsNullOrWhiteSpace(node.ControlEndpoint))
                    .ToArray();
                if (placementCandidates.Length == 0)
                {
                    WriteJson(context, 404, new { error = "no active regular node for pod placement" });
                    return;
                }
                // Partition count is the simulation-load signal; attached clients break ties (a node
                // already streaming to players is more loaded than an idle peer with the same
                // partition count), then node id for a stable total order. Count each CONNECTED
                // client through its live route with the durable character home as fallback: the
                // live map lags respawn/backend churn (the recovery auto-pick had the same
                // blindness), and a departed client's stale route must not count either.
                ulong[] placementClients = _clients.Keys
                    .Select(client => _registry.GetClientPartition(client)?.Partition
                        ?? _registry.GetCharacterHome(client)?.Partition ?? 0UL)
                    .Where(partition => partition != 0).ToArray();
                Func<NodeRegistration, int> clientsOn = node => placementClients.Count(client =>
                    placementPartitions.Any(partition => partition.Partition == client
                        && partition.Node == node.Node && partition.Epoch == node.Epoch));
                NodeRegistration placementTarget = placementCandidates
                    .OrderBy(node => placementPartitions.Count(partition => partition.Node == node.Node && partition.Epoch == node.Epoch))
                    .ThenBy(clientsOn)
                    .ThenBy(node => node.Node, StringComparer.Ordinal)
                    .First();
                int placementLoad = placementPartitions.Count(partition =>
                    partition.Node == placementTarget.Node && partition.Epoch == placementTarget.Epoch);
                Console.WriteLine($"Pod placement -> {placementTarget.Node} ({placementLoad} partitions, {clientsOn(placementTarget)} clients)");
                WriteJson(context, 200, new { node = placementTarget.Node, partitions = placementLoad });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/respawns/switch", StringComparison.OrdinalIgnoreCase))
            {
                RespawnSwitchRequest request = ReadJson<RespawnSwitchRequest>(context);
                WriteJson(context, 200, RespawnSwitch(ResolveSession(request.UserId), request,
                    context.Request.Headers["Authorization"] ?? string.Empty).GetAwaiter().GetResult());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/peer-rejections", StringComparison.OrdinalIgnoreCase))
            {
                HandlePeerRejection(context);
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/handover-config", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.HandoverConfig());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/offline-accounting-clock", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, (object?)_registry.AccountingClock("", 0, null) ?? new { unavailable = true });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/offline-accounting-sun", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.AccountingSun(Required(context, "epoch"), long.Parse(Required(context, "from")), long.Parse(Required(context, "through"))));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/offline-accounting-clock", StringComparison.OrdinalIgnoreCase))
            {
                var request = ReadJson<AccountingClockRequest>(context);
                WriteJson(context, 200, _registry.AccountingClock(request.Node, request.Epoch, request.Sample));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/nodes/heartbeat", StringComparison.OrdinalIgnoreCase))
            {
                HeartbeatRequest request = ReadJson<HeartbeatRequest>(context);
                NodeRegistration node = _registry.Heartbeat(request.Node, request.Epoch, request.Ownerships, request.ClientPartitions,
                    request.SimulationFrame, request.SaveFailure, request.RecoveryCapacity, request.Quarantined,
                    request.ProjectedLoad, request.LoadCapacity, request.RecoveryLoadsPending, request.Serving,
                    request.DeploymentRevision, request.ConfigurationVerified, request.ConfigurationFailure, request.Liveness);
                if (!request.Liveness) _registry.ReportRemoteUse(request.Node, request.Epoch, request.Ownerships, request.RemoteUsers);
                if (request.ServerData != null)
                    ReportServerData(request.Node, request.ServerData);
                if (request.Hitches != null)
                    foreach (HitchReport hitch in request.Hitches)
                        _handoverTelemetry.Hitch(hitch.TransferId, request.Node, hitch.Phase, hitch.Ms, hitch.Utc, hitch.PhaseMs);
                double gatewayMilliseconds = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
                if (node.Role == NodeRole.WA && request.AuthorityMilliseconds is double authorityMilliseconds)
                {
                    lock (_authorityClockLock)
                    {
                        if (node.AuthorityGeneration >= _authorityClockGeneration)
                        {
                            if (_authorityClockGeneration > 0)
                                authorityMilliseconds = Math.Max(authorityMilliseconds,
                                    _authorityClockMilliseconds + gatewayMilliseconds - _authorityClockObservedMilliseconds);
                            _authorityClockGeneration = node.AuthorityGeneration;
                            _authorityClockMilliseconds = authorityMilliseconds;
                            _authorityClockObservedMilliseconds = gatewayMilliseconds;
                        }
                    }
                }
                WorldAuthority authority = _registry.WorldAuthority();
                double? currentAuthorityMilliseconds = null;
                lock (_authorityClockLock)
                    if (authority.Node != null && _authorityClockGeneration == authority.Generation)
                        currentAuthorityMilliseconds = _authorityClockMilliseconds + gatewayMilliseconds - _authorityClockObservedMilliseconds;
                PartitionVersion[] accepted = _registry.PartitionsOwnedBy(node.Node, node.Epoch)
                    .Select(x => new PartitionVersion(x.Partition, x.Generation)).ToArray();
                WriteJson(context, 200, new HeartbeatResponse(node.Node, node.Epoch, node.Endpoint, node.Registered, node.State,
                    node.LeaseExpires, node.ForceDrainAt, node.Role, node.AuthorityGeneration, authority.Generation, accepted,
                    request.LocalSendMilliseconds, gatewayMilliseconds, currentAuthorityMilliseconds, _registry.HandoverConfigRevision,
                    _registry.ShuttingDown, AccountingClock: _registry.AccountingClock(node.Node, node.Epoch, request.AccountingClock),
                    ServerDataWanted: _admission.ServerData == null));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/nodes/close", StringComparison.OrdinalIgnoreCase))
            {
                double seconds = double.TryParse(context.Request.QueryString["forceAfterSeconds"], out double forceAfter) && forceAfter >= 0 ? forceAfter : 900;
                WriteJson(context, 200, _registry.CloseNode(Required(context, "node"), long.Parse(Required(context, "epoch")), TimeSpan.FromSeconds(seconds)));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/nodes/empty", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.MarkEmpty(Required(context, "node"), long.Parse(Required(context, "epoch"))));
                return;
            }
            if (_registry != null && IsControlVerb(context) && path.Equals("/registry/scale", StringComparison.OrdinalIgnoreCase))
            {
                // T-0312: GET = status (+ T-0320 per-host targets); POST ?nodes=N is kept one release as a shim for
                // "cluster nodes set <sole host> --serving N" (409 when the cluster has more than one host). force and
                // retire are gone: a decrease is never refused, and the oldest serving node goes first.
                if (context.Request.HttpMethod == "POST")
                {
                    try { _registry.ScaleShim(int.Parse(Required(context, "nodes")), "registry/scale", CapacityWarningFor); }
                    catch (NodeControlException refused)
                    {
                        WriteJson(context, refused.Status, new { error = refused.Code, message = refused.Message });
                        return;
                    }
                }
                WriteJson(context, 200, ScaleView());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/nodes/spare", StringComparison.OrdinalIgnoreCase))
            {
                string spareNode = Required(context, "node");
                bool spare = bool.Parse(Required(context, "spare"));
                WriteJson(context, 200, (object?)_registry.SetNodeSpare(spareNode, spare) ?? new { node = spareNode, spare });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/partitions", StringComparison.OrdinalIgnoreCase))
            {
                string? expected = context.Request.QueryString["expectedGeneration"];
                WriteJson(context, 200, _registry.ClaimPartition(
                    ulong.Parse(Required(context, "partition")), Required(context, "node"), long.Parse(Required(context, "epoch")),
                    expected == null ? null : long.Parse(expected)));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/partition-ids", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.AllocatePartitionIds(Required(context, "node"), long.Parse(Required(context, "epoch")), int.Parse(Required(context, "count"))));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/topology", StringComparison.OrdinalIgnoreCase))
            {
                TopologyRequest request = ReadJson<TopologyRequest>(context);
                WriteJson(context, 200, _registry.ApplyLineage(request.OpId, request.Kind, request.Node, request.Epoch, request.Inputs,
                    request.OutputIds, request.Members, request.ClusterTime, request.Seated));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/npc-census", StringComparison.OrdinalIgnoreCase))
            {
                if (NpcCensusNow() is { } census) WriteJson(context, 200, census, redact: false);
                else WriteJson(context, 503, new { error = "no complete partition report pass yet" });
                return;
            }
            if (_registry != null && path.StartsWith("/registry/partitions/offline", StringComparison.OrdinalIgnoreCase))
            {
                HandleOfflineRoutes(context, path);
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/partitions", StringComparison.OrdinalIgnoreCase))
            {
                // ?partition=N: that one row (or []). The whole report is ~40 MB / 232k rows on w32, and a node
                // asking about one partition used to fetch all of it (T-0231).
                string? one = context.Request.QueryString["partition"];
                WriteJson(context, 200, _registry.PartitionsReport(only: string.IsNullOrEmpty(one) ? null : ulong.Parse(one)));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/quarantine", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.QuarantinedPartitions());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/partitions/release", StringComparison.OrdinalIgnoreCase))
            {
                ulong releasePartition = ulong.Parse(Required(context, "partition"));
                // Same guard as /registry/lineage below: a partition routed to a connected
                // client is not disposable, however empty its node report looks right now -
                // but the report only LAGS the registry for seconds, so the pin expires with
                // the rest of the route-over-report rule. Release is non-destructive: it
                // hands the row to recovery, which resurrects its newest save on a live
                // node, so a stale pin blocking it strands the content AND parks the node
                // (w19 partition 1780: a clean drain Final, reported nowhere, held node-3
                // past its kill deadline). Retirement below keeps the unconditional guard -
                // that one destroys reachability.
                if (!RoutePinStale(releasePartition))
                    foreach (ulong releaseClient in _clients.Keys)
                    {
                        if (_registry.RoutedPartition(releaseClient) == releasePartition)
                            throw new RegistryConflictException(
                                $"Partition {releasePartition} is routed to connected client {releaseClient}; refusing release");
                    }
                WriteJson(context, 200, _registry.ReleasePartition(releasePartition, Required(context, "node"), long.Parse(Required(context, "epoch")),
                    long.Parse(Required(context, "generation")), memberless: context.Request.QueryString["memberless"] == "true",
                    failedRecovery: context.Request.QueryString["failed"] == "true"));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/recoveries/claim", StringComparison.OrdinalIgnoreCase))
            {
                RecoveryRequest request = ReadJson<RecoveryRequest>(context);
                WriteJson(context, 200, _registry.ClaimRecoveries(request.Node, request.Epoch, request.Partitions));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/leases/expire", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, new { expired = _registry.ExpireLeases() });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/handovers/begin", StringComparison.OrdinalIgnoreCase))
            {
                HandoverRequest request = ReadJson<HandoverRequest>(context);
                WriteJson(context, 200, _registry.Begin(request.Id, request.Partition, request.SourceNode, request.SourceEpoch, request.SourceGeneration, request.TargetNode, request.TargetEpoch));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/grants", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.IssueGrant(ReadJson<GrantRequest>(context)), redact: false);
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/grants/validate", StringComparison.OrdinalIgnoreCase))
            {
                _registry.ValidateGrant(ReadJson<GrantValidation>(context));
                WriteJson(context, 200, new { valid = true });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.StartsWith("/registry/handovers/", StringComparison.OrdinalIgnoreCase))
            {
                string id = Required(context, "id");
                HandoverRecord handover = path[20..].ToLowerInvariant() switch
                {
                    "activate" => _registry.Activate(id),
                    "commit" => _registry.Commit(id),
                    "abort" => _registry.Abort(id),
                    _ => throw new ArgumentException("Unknown handover action"),
                };
                WriteJson(context, 200, handover);
                return;
            }
            if (context.Request.HttpMethod == "GET" && path.Equals("/registry/handovers/history", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _handoverTelemetry.Snapshot());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/handovers", StringComparison.OrdinalIgnoreCase))
            {
                string? id = context.Request.QueryString["id"];
                if (id == null)
                    WriteJson(context, 200, _registry.Incomplete());
                else
                {
                    HandoverRecord? handover = _registry.Get(id);
                    WriteJson(context, handover == null ? 404 : 200, handover ?? (object)new { error = "handover not found" });
                }
                return;
            }
            // TestPlan §1.4 / P3-REC-01: deterministically answer one node's registration, heartbeats,
            // claims and ID allocations with definitive 409s. Debug builds only.
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/debug/reject-node", StringComparison.OrdinalIgnoreCase))
            {
                if (!_options.DirectFrontend)
                    throw new ArgumentException("reject-node requires --direct");
                string slot = Required(context, "slot");
                bool armed = (context.Request.QueryString["mode"] ?? "on") != "off";
                _registry.SetTestRejection(slot, armed);
                WriteJson(context, 200, new { slot, rejected = armed });
                return;
            }
            // Orchestration §3.3: the HandoverBlocked table and its admin clear trigger.
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/handover-blocked", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.HandoverBlocks());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/handover-blocked/clear", StringComparison.OrdinalIgnoreCase))
            {
                ulong blockedPartition = ulong.Parse(Required(context, "partition"));
                WriteJson(context, 200, new { partition = blockedPartition, cleared = _registry.ClearHandoverBlock(blockedPartition) });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/saves", StringComparison.OrdinalIgnoreCase))
            {
                string? sequence = context.Request.QueryString["sequence"];
                if (sequence != null)
                {
                    WriteJson(context, 200, _registry.RegisterSave(
                        new SaveId(ulong.Parse(Required(context, "partition")), long.Parse(Required(context, "generation")), long.Parse(sequence)),
                        Required(context, "node"), long.Parse(Required(context, "epoch")), Enum.Parse<SaveKind>(Required(context, "kind"), true),
						long.Parse(Required(context, "size")), Required(context, "checksum"), long.Parse(Required(context, "clusterTime")), Required(context, "location"), context.Request.QueryString["control"] ?? string.Empty,
						ParseHomeStamps(context.Request.QueryString["homes"]),
						int.TryParse(context.Request.QueryString["entities"], out int entities) && entities >= 0 ? entities : null));
                    return;
                }
                ulong[] partitions = Required(context, "partitions").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ulong.Parse).ToArray();
                string? created = context.Request.QueryString["created"];
                WriteJson(context, 200, _registry.RegisterSave(
                    Required(context, "id"), Required(context, "node"), long.Parse(Required(context, "epoch")),
                    long.Parse(Required(context, "generation")), partitions, Required(context, "location"),
                    created == null ? DateTimeOffset.UtcNow : DateTimeOffset.Parse(created)));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/saves/late", StringComparison.OrdinalIgnoreCase))
            {
                // T-0347 (LateSaves.cs): the Finals of a local stop, reported from the writers' journals at the next start.
                WriteJson(context, 200, _registry.RegisterLateSaves(Required(context, "node"), long.Parse(Required(context, "epoch")),
                    ReadJson<LateSaveRequest>(context).Saves ?? []));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/global-saves", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.RegisterGlobalSave(
                    new GlobalSaveId(long.Parse(Required(context, "generation")), long.Parse(Required(context, "sequence"))),
                    Required(context, "node"), long.Parse(Required(context, "epoch")), Enum.Parse<SaveKind>(Required(context, "kind"), true),
                    long.Parse(Required(context, "size")), Required(context, "checksum"), long.Parse(Required(context, "clusterTime")),
                    Required(context, "location"), context.Request.QueryString["control"] ?? string.Empty));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/host-store/adopt", StringComparison.OrdinalIgnoreCase))
            {
                // T-0340 step 7 (HostStoreMove.cs): the host agent's evidence for legacy save files it moved into its host's store.
                WriteJson(context, 200, _registry.HostStoreAdopt(ReadJson<HostStoreAdoptRequest>(context)));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/host-store/archived-partitions", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, new { partitions = _registry.HostStoreArchivedPartitions() });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/global-saves", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.GlobalSaves());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/global-saves/newest", StringComparison.OrdinalIgnoreCase))
            {
                GlobalSaveRegistration? save = _registry.NewestGlobalSave();
                WriteJson(context, save == null ? 404 : 200, save ?? (object)new { error = "global save not found" });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/global-saves/recovery", StringComparison.OrdinalIgnoreCase))
            {
                GlobalSaveRegistration? save = _registry.NewestGlobalSaveForRecovery(context.Request.QueryString["without"]);
                WriteJson(context, save == null ? 404 : 200, save ?? (object)new { error = "global save not found" });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/global-journal", StringComparison.OrdinalIgnoreCase))
            {
                // #1318 (GlobalJournal.cs): the World Authority's commands, journaled before it answers them.
                WriteJson(context, 200, new { appended = _registry.AppendGlobalJournal(Required(context, "node"), long.Parse(Required(context, "epoch")),
                    long.Parse(Required(context, "generation")), ReadJson<GlobalJournalAppend>(context).Entries ?? []) });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/global-journal", StringComparison.OrdinalIgnoreCase))
            {
                // Not redacted: the replacement replays these payloads and results as they were, like the global save holding them.
                WriteJson(context, 200, _registry.GlobalJournal(long.Parse(context.Request.QueryString["after"] ?? "0")), redact: false);
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/voxel-bases", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.RegisterVoxelBase(Required(context, "storage"), long.Parse(Required(context, "baseVersion")),
                    long.Parse(Required(context, "tailSeq")), Required(context, "node"), long.Parse(Required(context, "epoch")),
                    long.Parse(Required(context, "generation")), long.Parse(Required(context, "size")), Required(context, "checksum"), Required(context, "location")));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/voxel-bases", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.VoxelBaseCatalog());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/voxel-journals", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.ReportVoxelJournal(new VoxelRef(Required(context, "storage"),
                    long.Parse(Required(context, "baseVersion")), long.Parse(Required(context, "tailSeq"))), Required(context, "node"),
                    long.Parse(Required(context, "epoch")), long.Parse(Required(context, "generation"))));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/voxel-journals", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.VoxelJournals());
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/voxel-replicas", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.ReportVoxelReplica(Required(context, "storage"), Required(context, "node"),
                    long.Parse(Required(context, "epoch")), long.Parse(Required(context, "applied"))));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/voxel-replicas", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.VoxelReplicas());
                return;
            }
			if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/replicas", StringComparison.OrdinalIgnoreCase))
			{
				WriteJson(context, 200, _registry.ReplicaWork(Required(context, "node"), long.Parse(Required(context, "epoch"))));
				return;
			}
			if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/saves/holders", StringComparison.OrdinalIgnoreCase))
			{
				WriteJson(context, 200, _registry.RegisterHolder(new SaveId(ulong.Parse(Required(context, "partition")), long.Parse(Required(context, "generation")), long.Parse(Required(context, "sequence"))),
					Required(context, "node"), long.Parse(Required(context, "epoch")), long.Parse(Required(context, "size")), Required(context, "checksum"),
					Required(context, "location"), Required(context, "control")));
				return;
			}
			if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/global-saves/holders", StringComparison.OrdinalIgnoreCase))
			{
				// T-0399: ?withdraw=1 takes the node's copy back (its scrub found it unreadable); same parameters as the register.
				if (context.Request.QueryString["withdraw"] == "1")
				{
					WriteJson(context, 200, new { withdrawn = _registry.WithdrawGlobalHolder(
						new GlobalSaveId(long.Parse(Required(context, "generation")), long.Parse(Required(context, "sequence"))),
						Required(context, "node"), long.Parse(Required(context, "epoch")), Required(context, "location")) });
					return;
				}
				WriteJson(context, 200, _registry.RegisterGlobalHolder(new GlobalSaveId(long.Parse(Required(context, "generation")), long.Parse(Required(context, "sequence"))),
					Required(context, "node"), long.Parse(Required(context, "epoch")), long.Parse(Required(context, "size")), Required(context, "checksum"),
					Required(context, "location"), Required(context, "control")));
				return;
			}
			if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/saves/amend", StringComparison.OrdinalIgnoreCase))
			{
				WriteJson(context, 200, _registry.AmendSave(
					new SaveId(ulong.Parse(Required(context, "partition")), long.Parse(Required(context, "generation")), long.Parse(Required(context, "sequence"))),
					Required(context, "node"), long.Parse(Required(context, "epoch")),
					long.Parse(Required(context, "size")), Required(context, "checksum"), long.Parse(Required(context, "clusterTime")), Required(context, "location")));
				return;
			}
			if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/saves/corruption", StringComparison.OrdinalIgnoreCase))
			{
				_registry.ReportCorruption(new SaveId(ulong.Parse(Required(context, "partition")), long.Parse(Required(context, "generation")), long.Parse(Required(context, "sequence"))),
					Required(context, "holder"), Required(context, "reporter"), long.Parse(Required(context, "epoch")));
				WriteJson(context, 200, new { quarantined = true });
				return;
			}
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/saves", StringComparison.OrdinalIgnoreCase))
            {
                // ?partition=N: that partition's saves only. Nodes always asked with it but got the WHOLE catalogue
                // (6.1 MB on w32 2026-09-25) and filtered locally - and a draining node's final-replication wait asks
                // once per drained partition every 500 ms for up to 10 s.
                string? onePartition = context.Request.QueryString["partition"];
                WriteJson(context, 200, string.IsNullOrEmpty(onePartition) ? _registry.Saves() : _registry.SavesFor(ulong.Parse(onePartition)));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/saves/resolve", StringComparison.OrdinalIgnoreCase))
            {
                SaveCover[] cover = _registry.ResolveCover(ulong.Parse(Required(context, "partition")));
                WriteJson(context, cover.Length == 0 ? 404 : 200, cover);
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/rebuild", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.RebuildCatalog(ReadJson<RebuildRequest>(context)));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/lineage", StringComparison.OrdinalIgnoreCase))
            {
                LineageRequest request = ReadJson<LineageRequest>(context);
                // The same invariant FencedClientlessHandover enforces for transfers, applied to
                // retirement: a partition routed to a CONNECTED client is not empty, whatever the
                // node report told the caller (the report lags the registry for seconds after a
                // switch). Retiring it out from under the client is how world 12's dissolutions
                // turned the fleet into unpartitioned zombies - refuse instead.
                //
                // ...and it expires the same way, for the same reason. This guard read the raw
                // route with no expiry while the transfer path learned to age it out, so the
                // retirement half stayed pinned forever on a route no node has confirmed since.
                // w19 2026-08-20: partition 3190 held pilot-3's character, every node reported it
                // clientless, and node-3 sat Closed 48+ minutes retrying this exact merge every
                // 5 minutes while the whole fleet's rotation halted behind it (pick_slot defers
                // every slot while a Regular is Closed). The old comment here promised "the caller
                // retries a later cycle, by which time the report and the registry agree" - which
                // is only true once something expires the route, and nothing did.
                foreach (ulong lineageClient in _clients.Keys)
                {
                    if (_registry.GetClientPartition(lineageClient) is not { } lineagePlacement
                        || !request.Inputs.Any(x => x.Partition == lineagePlacement.Partition))
                        continue;
                    if (!RoutePinStale(lineagePlacement.Partition))
                        throw new RegistryConflictException(
                            $"Partition {lineagePlacement.Partition} is routed to connected client {lineageClient}; refusing lineage '{request.OpId}'");
                    Console.WriteLine($"Partition {lineagePlacement.Partition} is routed to connected client "
                        + $"{lineageClient} but its node has reported it clientless for at least "
                        + $"{RouteOverReportGrace.TotalSeconds:F0}s; retiring it on the node's report "
                        + $"(lineage '{request.OpId}')");
                }
                WriteJson(context, 200, _registry.RecordLineage(request.OpId, request.Kind, request.Inputs, request.Outputs, request.Members, request.ClusterTime));
                return;
            }
			if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/lineage", StringComparison.OrdinalIgnoreCase))
			{
				WriteJson(context, 200, _registry.Lineage());
				return;
			}
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/snapshots", StringComparison.OrdinalIgnoreCase))
            {
                SnapshotRequest request = ReadJson<SnapshotRequest>(context);
                WriteJson(context, 200, _registry.CreateSnapshot(request.SnapshotId, request.Kind, request.ClusterTime,
                    request.PartitionRefs, request.Dirty, request.GlobalRef, request.VoxelRefs));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/snapshots/online", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.CreateOnlineSnapshot(Required(context, "id"),
                    long.TryParse(context.Request.QueryString["clusterTime"], out long clusterTime) ? clusterTime : DateTimeOffset.UtcNow.UtcTicks));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/retention", StringComparison.OrdinalIgnoreCase))
            {
                int keep = int.TryParse(context.Request.QueryString["keep"], out int keepValue) ? keepValue : 3;
                int snapshots = int.TryParse(context.Request.QueryString["snapshots"], out int snapshotValue) ? snapshotValue : 2;
                WriteJson(context, 200, _registry.PlanRetention(keep, snapshots));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "POST" && path.Equals("/registry/deletions/ack", StringComparison.OrdinalIgnoreCase))
            {
                bool removed = _registry.AcknowledgeDelete(
                    new SaveId(ulong.Parse(Required(context, "partition")), long.Parse(Required(context, "generation")), long.Parse(Required(context, "sequence"))),
                    Required(context, "location"));
                WriteJson(context, removed ? 200 : 404, new { removed });
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/deletions", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, _registry.PendingDeletes(Required(context, "node"), long.Parse(Required(context, "epoch"))));
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/saves/newest", StringComparison.OrdinalIgnoreCase))
            {
                SaveRegistration? save = _registry.NewestSave(ulong.Parse(Required(context, "partition")));
                WriteJson(context, save == null ? 404 : 200, save is null ? new { error = "save not found" } : (object)save);
                return;
            }
            if (_registry != null && context.Request.HttpMethod == "GET" && path.Equals("/registry/saves/cover", StringComparison.OrdinalIgnoreCase))
            {
                ulong[] partitions = Required(context, "partitions").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ulong.Parse).ToArray();
                SaveRegistration[] saves = _registry.Cover(partitions);
                WriteJson(context, saves.Length == 0 && partitions.Length != 0 ? 404 : 200, saves);
                return;
            }
            if (IsControlVerb(context) && path.Equals("/handover", StringComparison.OrdinalIgnoreCase))
            {
                string source = Required(context, "source");
                string target = Required(context, "target");
                IPEndPoint backend = EndpointParser.Parse(Required(context, "backend"), DirectTransportConstants.DefaultPort);
                ulong partition = ulong.Parse(Required(context, "partition"));
                string? clientlessValue = context.Request.QueryString["clientless"];
                bool clientless = clientlessValue != null && bool.TryParse(clientlessValue, out bool parsedClientless) && parsedClientless;
                if (clientlessValue != null && !bool.TryParse(clientlessValue, out _))
                    throw new ArgumentException("Invalid query parameter 'clientless'");
                string? pauseValue = context.Request.QueryString["pauseAfterCapture"];
                bool pauseAfterCapture = pauseValue != null && bool.TryParse(pauseValue, out bool parsedPause) && parsedPause;
                if (pauseValue != null && !bool.TryParse(pauseValue, out _))
                    throw new ArgumentException("Invalid query parameter 'pauseAfterCapture'");
                // T-0235: before the move, so the balancer pass waiting on _rotation sees the drain.
                NoteDrainSource(source);
                WaitRotation("forced", partition, true);
                try
                {
                    // T-0354b: a partition a node made a moment ago is refused until its id claim is answered; a requested
                    // move waits that out (bounded) instead of failing, as on df where the claim was answered at once.
                    if (clientless)
                        WhenRegistryApplied(partition, () => FencedClientlessHandover(source, target, backend, partition, pauseAfterCapture)).GetAwaiter().GetResult();
                    else
                        WhenRegistryApplied(partition, () => Handover(ResolveCrew(context, source, partition), source, target, backend, partition, pauseAfterCapture)).GetAwaiter().GetResult();
                }
                finally { _rotation.Release(); }
                WriteText(context, 200, "handover ok\n");
                return;
            }

            if (IsControlVerb(context) && path.Equals("/close-node", StringComparison.OrdinalIgnoreCase))
            {
                double seconds = double.TryParse(context.Request.QueryString["forceAfterSeconds"], out double forceAfter) && forceAfter >= 0 ? forceAfter : 900;
                bool forceOnly = bool.TryParse(context.Request.QueryString["forceOnly"], out bool parsedForceOnly) && parsedForceOnly;
                NodeRegistration closed = CloseNode(TimeSpan.FromSeconds(seconds), forceOnly, context.Request.QueryString["node"]).GetAwaiter().GetResult();
                WriteJson(context, 200, closed);
                return;
            }

            if (IsControlVerb(context) && path.Equals("/switch", StringComparison.OrdinalIgnoreCase))
            {
                ControlSwitchBackend(ResolveSession(context), EndpointParser.Parse(Required(context, "backend"), DirectTransportConstants.DefaultPort));
                WriteText(context, 200, "switch ok\n");
                return;
            }

            if (IsControlVerb(context) && path.Equals("/resume", StringComparison.OrdinalIgnoreCase))
            {
                Resume();
                WriteText(context, 200, "resume ok\n");
                return;
            }
			if (IsControlVerb(context) && path.Equals("/pause", StringComparison.OrdinalIgnoreCase))
			{
				lock (_pauseLock) _paused = true;
				WriteText(context, 200, "paused\n");
				return;
			}
			// Suspends only the automatic co-location initiation - relay, scripted transfers,
			// and reconciliation keep running. For maintenance and for test choreography that
			// must hold deliberately co-located cross-node partitions in place.
			if (IsControlVerb(context) && path.Equals("/automatic/pause", StringComparison.OrdinalIgnoreCase))
			{
				_automaticSuspended = true;
				WriteText(context, 200, "automatic paused\n");
				return;
			}
			if (IsControlVerb(context) && path.Equals("/automatic/resume", StringComparison.OrdinalIgnoreCase))
			{
				_automaticSuspended = false;
				WriteText(context, 200, "automatic resumed\n");
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.Equals("/balance/migrate", StringComparison.OrdinalIgnoreCase))
			{
				HandleTestMigration(context);
				return;
			}

            WriteText(context, 404, "unknown gateway control route\n");
        }
        catch (Exception e)
        {
            // LOG FIRST, WRITE SECOND. Every successful route above has already called WriteText
            // or WriteJson and closed the response, so if anything throws AFTER a response was
            // sent, the WriteText below throws InvalidOperationException from set_ContentLength64.
            // That second exception escapes to ControlLoop's catch (line ~1861), which prints IT -
            // and the original exception, the one this handler exists to report, is never logged
            // anywhere. Twice on 2026-08-22 (07:58:12 and 09:05:05, both during heavy rotation)
            // the only trace left was the reporting machinery failing; what actually went wrong is
            // unrecoverable from the log.
            //
            // A defect that silently substitutes itself for other defects corrupts every
            // investigation that reads gateway.log, which is the primary instrument here.
            // Name the ROUTE. Three of these fired today (07:58:12, 09:05:05, 09:52:06), all
            // "This operation cannot be performed after the response has been submitted" thrown from
            // WriteText - i.e. something had already answered this request. Which route did is not
            // recoverable from the message, and the stack's line numbers refer to the DEPLOYED build,
            // not to this source, so they cannot be mapped back either (see
            // cluster-regression findings, archive-build-id-names-the-wrong-repo). Without the path
            // the next occurrence is as unattributable as these three were.
            // T-0139. A RegistryConflictException/RegistryBusyException/ArgumentException is an
            // ANSWER, not a fault: the registry refusing a stale claim is the guard working. Logging
            // it with a full stack treats an expected outcome as a crash, and the volume hides the
            // one case that matters. Measured by the CM on w31: every rotation produced 5 rejected
            // claims plus 5 stack traces (a relaunching node re-posting generation-0 claims for the
            // TEMPLATE seed partitions 1-5, which is why it is always those five and never any of
            // the ~64 created in-session), on a cluster rotating a node every 10 minutes. The
            // hazard the ticket names is precise: a GENUINE generation conflict arrives looking
            // exactly like these and gets dismissed with them.
            //
            // So: expected refusals log one line with their message, unexpected faults (500) keep
            // the full exception and stack, which is the case the stack was ever useful for.
            // Except ArgumentNullException: no request can supply a null, so it is always a gateway bug
            // (w32 2026-09-25 20:53:03, POST /handover "Value cannot be null. (Parameter 'key')" was logged
            // as a one-line refusal and its throw site was unrecoverable). It gets the stack and a 500.
            bool expected = e is RegistryBusyException or RegistryConflictException or FormatException
                || e is ArgumentException and not ArgumentNullException;
            Console.Error.WriteLine("Control request failed: verb=" + context.Request.HttpMethod
                // From the request, not from `path`: that local is declared inside the try and is
                // therefore out of scope here - and would be unset anyway if the throw beat its
                // assignment, which is exactly the case worth logging.
                + " path=" + (context.Request.Url?.AbsolutePath ?? "?") + " "
                + (expected ? e.GetType().Name + ": " + e.Message : e.ToString()));
            try
            {
                // Response body follows the same rule. A stack trace here is also of no use to the
                // node: its line numbers refer to the DEPLOYED gateway build, not to any source the
                // reader has (archive-build-id-names-the-wrong-repo).
                WriteText(context, e is RegistryBusyException ? 503 : e is RegistryConflictException ? 409 : expected ? 400 : 500,
                    (expected ? e.GetType().Name + ": " + e.Message : e.ToString()) + "\n");
            }
            catch (Exception writeFailure)
            {
                // Expected when the response is already submitted. The caller gets nothing, which
                // it was going to get anyway; the point is that the line above survived.
                Console.Error.WriteLine("  (could not send the error response: " + writeFailure.GetType().Name
                                        + ": " + writeFailure.Message + ")");
            }
        }
    }

    private ClusterLifecycle ShutdownCluster(bool emergency, TimeSpan timeout, TimeSpan forceDrainAfter)
    {
        GatewayRegistry registry = _registry!;
        if (!emergency)
            NotifyAndReturnPlayers();
		lock (_pauseLock) _paused = true;
        registry.BeginShutdown(emergency, forceDrainAfter);
        foreach (ClientSession client in _clients.Values) client.Client?.Disconnect();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!registry.ShutdownReady() && DateTimeOffset.UtcNow < deadline)
        {
            registry.ExpireLeases();
            Thread.Sleep(100);
        }
        return registry.CompleteShutdown(emergency);
    }

    /// <summary>
    /// Graceful shutdown, before the drain (Viktor 2026-09-26: a deploy stop dropped his connection
    /// with no word - gateway-rs closed the Steam session and the client sat retrying against closed
    /// admission). In order:
    ///  1. joining closes (<see cref="_joiningClosed"/>);
    ///  2. the World Authority posts a server line to global chat that the cluster shuts down now;
    ///  3. after <c>CLUSTER_SHUTDOWN_NOTICE_SECONDS</c> (10) every node sends each client attached to it
    ///     back to the main menu with vanilla's "server left" control message (Viktor 2026-09-27: no
    ///     kick box; the client shows vanilla's reconnector and rejoins by itself once the cluster is
    ///     back). This is NOT a kick: nothing is put on the node's kicked list and the admission
    ///     anti-flap cooldown is not armed;
    ///  4. up to <c>CLUSTER_SHUTDOWN_LEAVE_SECONDS</c> (15) for the clients to leave on their own;
    ///     whoever is left is disconnected at the transport as before, then the drain saves and stops.
    /// Every step is best-effort: a node or WA that does not answer never blocks the shutdown.
    /// </summary>
    private void NotifyAndReturnPlayers()
    {
        _joiningClosed = true;
        double notice = EnvSeconds("CLUSTER_SHUTDOWN_NOTICE_SECONDS", 10);
        double leave = EnvSeconds("CLUSTER_SHUTDOWN_LEAVE_SECONDS", 15);
        int clients = _clients.Count;
        Console.WriteLine($"Shutdown notice: joining closed, {clients} client(s) connected");
        if (clients == 0)
            return;
        string text = Environment.GetEnvironmentVariable("CLUSTER_SHUTDOWN_MESSAGE") is { Length: > 0 } custom
            ? custom : "The cluster is shutting down now. You can join again once it is back up.";
        bool announced = false;
        if (WorldAuthorityControl() is { } authority)
        {
            try
            {
                using HttpResponseMessage response = _http.Send(new HttpRequestMessage(HttpMethod.Post,
                    ClusterUrl(authority.Control, "shutdown-notice") + "?text=" + Uri.EscapeDataString(text)));
                announced = response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Shutdown notice: World Authority chat failed: {e.Message}");
            }
        }
        Console.WriteLine($"Shutdown notice: chat {(announced ? "sent" : "NOT sent")}; returning players to the menu in {notice:0}s");
        if (announced)
            Thread.Sleep(TimeSpan.FromSeconds(notice));
        // Repeated until the leave deadline: a client that attaches to a node after the first pass
        // (mid-handover, still joining) is sent on a later one. Nodes send each client once.
        var returned = new HashSet<ulong>();
        var answered = new HashSet<string>();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(leave);
        do
        {
            foreach (NodeRegistration node in _registry?.Nodes() ?? [])
            {
                if (node.ControlEndpoint == null || node.State != NodeState.Active) continue;
                try
                {
                    using HttpResponseMessage response = _http.Send(new HttpRequestMessage(HttpMethod.Post,
                        ClusterUrl(node.ControlEndpoint.Replace("http://", string.Empty), "return-to-menu")
                        // A Steam client knows its server as gateway-rs's GameServer, not as the node (T-0248).
                        + (Volatile.Read(ref _steamServerId) is var steamServer and not 0 ? "?serverId=" + steamServer : "")));
                    response.EnsureSuccessStatusCode();
                    foreach (string id in response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                                 .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (ulong.TryParse(id, out ulong userId)) returned.Add(userId);
                    answered.Add(node.Node);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Shutdown notice: return-to-menu on {node.Node} failed: {e.Message}");
                }
            }
            for (DateTimeOffset next = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
                 !_clients.IsEmpty && DateTimeOffset.UtcNow < next && DateTimeOffset.UtcNow < deadline;)
                Thread.Sleep(250);
        } while (!_clients.IsEmpty && DateTimeOffset.UtcNow < deadline);
        // Still connected and never listed by a node: joining, or a dead client the transport has
        // not timed out yet (the front's DisconnectTimeout is 120 s). The drain cuts both.
        ulong[] unlisted = _clients.Keys.Where(id => !returned.Contains(id)).ToArray();
        Console.WriteLine($"Shutdown notice: {returned.Count} client(s) returned to the menu by {answered.Count} node(s); "
            + $"{clients - _clients.Count} left, {_clients.Count} still connected (disconnected by the drain)"
            + (unlisted.Length == 0 ? string.Empty
                : $", {unlisted.Length} never listed by a node: {string.Join(",", unlisted.Take(20))}{(unlisted.Length > 20 ? ",..." : string.Empty)}"));
    }

    private static double EnvSeconds(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) && value >= 0 ? value : fallback;

    private static double PositiveSeconds(HttpListenerContext context, string name, double fallback) =>
        double.TryParse(context.Request.QueryString[name], out double value) && value >= 0 ? value : fallback;

    private ClientSession ResolveSession(HttpListenerContext context)
    {
        string? value = context.Request.QueryString["client"];
        if (value != null)
        {
            if (!ulong.TryParse(value, out ulong clientId) || !_clients.TryGetValue(clientId, out ClientSession? selected))
                throw new ArgumentException("Unknown client '" + value + "'");
            return selected;
        }
        if (_clients.Count == 1)
            return _clients.Values.Single();
        throw new ArgumentException(_clients.IsEmpty ? "No connected client" : "Missing query parameter 'client'");
    }

    private ClientSession ResolveSession(ulong client) => _clients.TryGetValue(client, out ClientSession? session)
        ? session : throw new ArgumentException("Unknown client '" + client + "'");

    /// <summary>
    /// Every client riding the partition an operator asked to move, not just the one named in the
    /// request. `client` selects the partition's session; the whole crew travels with it.
    ///
    /// The caller names one client because that is how the endpoint has always been addressed, but
    /// moving only that one leaves its shipmates pointed at a node that no longer owns the grid
    /// they are standing on. The regression rotator papered over this by re-pointing the others
    /// with a bare `/switch`, which moves packets without moving any replication state - the exact
    /// desync a handover exists to avoid.
    /// </summary>
    private ClientSession[] ResolveCrew(HttpListenerContext context, string source, ulong partition)
    {
        ClientSession named = ResolveSession(context);
        PartitionInfo? reported;
        try { reported = GetPartitions(source).Partitions.SingleOrDefault(x => x.Id == partition); }
        catch (Exception e)
        {
            Console.WriteLine($"Could not read {source} to find the crew of partition {partition}: {e.Message}; "
                + $"handing over client {named.ClientId} alone");
            return [named];
        }
        // A named client the source does not report on this partition is an explicit instruction,
        // not a crew move: honour it as it was asked for.
        if (reported == null || !reported.Clients.Contains(named.ClientId))
            return [named];
        if (ResolveMovingSessions(reported.Clients, named.BackendEndpoint) is not { } crew)
            throw new RegistryConflictException($"Partition {partition} has a client with no connected, ready "
                + $"session on {source}; handover refused rather than splitting the partition");
        if (crew.Length > 1)
            Console.WriteLine($"Handover of partition {partition} carries {crew.Length} clients: "
                + string.Join(", ", crew.Select(x => x.ClientId)));
        return crew;
    }

    /// <summary>
    /// Backend for a client the registry has no partition route for: a first join, or a client
    /// whose character is not in any partition yet.
    ///
    /// It is normally the configured backend, but it must not be that node when that node cannot
    /// take the client. <c>_options.Backend</c> names ONE node (the deployment's first), and under
    /// timed node rotation that node is retired like any other. A partition-less client sitting on
    /// it is then force-reconnected and routed straight back onto the same slot - which is Closed,
    /// or dead, or still loading its world - so it never becomes world-ready and answers 503 on
    /// /v1/character for the rest of the run. Measured 2026-08-15: eight clients looping on node-1
    /// across three rotations, fifteen of twenty-four pilots stuck in recovery.
    ///
    /// So: keep the configured backend while its node is Active with a live lease, and otherwise
    /// spread onto any other Active regular node. Falls back to the configured value when the
    /// registry can offer nothing, which is the pre-2026-08-15 behaviour.
    /// </summary>
    private IPEndPoint UnroutedBackend()
    {
        if (_registry == null)
            return _options.Backend;
        try
        {
            NodeRegistration[] live = _registry.Nodes()
                .Where(x => x.Role == NodeRole.Regular && x.State == NodeState.Active && x.Serving && !x.Spare
                    && x.Endpoint != null && x.LeaseExpires > DateTimeOffset.UtcNow)
                .ToArray();
            if (live.Length == 0)
                return _options.Backend;
            string configured = _options.Backend.ToString();
            if (live.Any(x => EndpointParser.Parse(x.Endpoint, DirectTransportConstants.DefaultPort)
                    .ToString() == configured))
                return _options.Backend;
            NodeRegistration pick = live[(int)((ulong)Interlocked.Increment(ref _unroutedCursor)
                                               % (ulong)live.Length)];
            IPEndPoint backend = EndpointParser.Parse(pick.Endpoint, DirectTransportConstants.DefaultPort);
            Console.WriteLine($"Unrouted client backend: {_options.Backend} is not Active; using "
                + $"{pick.Node} at {backend}");
            return backend;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Unrouted-backend pick failed: {error.Message}");
            return _options.Backend;
        }
    }

    private async Task<NodeRegistration> CloseNode(TimeSpan forceDrainAfter, bool forceOnly, string? nodeName)
    {
        if (_registry == null)
            throw new InvalidOperationException("Close-node requires the registry");
        await WaitRotationAsync("close", true);
        try
        {
            NodeRegistration source = nodeName == null
                ? _registry.GetNodeByEndpoint(_options.Backend.ToString())
                : _registry.Nodes().FirstOrDefault(x => x.Node == nodeName)
                    ?? throw new ArgumentException($"Unknown node '{nodeName}'");
            if (source.ControlEndpoint == null)
                throw new InvalidOperationException($"Node '{source.Node}' registered no control endpoint");
            string sourceControl = StripScheme(source.ControlEndpoint);
            IPEndPoint sourceBackend = EndpointParser.Parse(source.Endpoint, DirectTransportConstants.DefaultPort);
            // Drain target: the lightest ready node (7.3); fetching its report doubles as the
            // readiness probe the fixed-pair implementation did against the configured target.
            // CLUSTER-SIM-COST: lightest by measured load when every candidate reports it, PCU
            // otherwise - decided after all reports are in, so the units never mix.
            List<(NodeRegistration Registration, string Control, PartitionReport Report)> ready = [];
            foreach (NodeRegistration candidate in _registry.Nodes().Where(x => x.Role == NodeRole.Regular
                && x.State == NodeState.Active && x.Serving && !x.Spare && x.Node != source.Node && x.ControlEndpoint != null
                && x.LeaseExpires > DateTimeOffset.UtcNow && !_registry.IsRetiring(x.Node)))
            {
                string control = StripScheme(candidate.ControlEndpoint!);
                ready.Add((candidate, control, GetPartitions(control)));
            }
            bool closeSimLoad = ready.Count > 0 && ready.All(x => x.Report.SimLoadSupported);
            (NodeRegistration Registration, string Control, PartitionReport Report)? drainTarget = ready.Count == 0
                ? null : ready.MinBy(x => EffectiveNodeLoad(x.Report, closeSimLoad));
            if (drainTarget is not { } target)
                throw new RegistryConflictException($"No active replacement node to drain '{source.Node}' to");
            ClientSession[] sessions = _clients.Values.Where(x => x.BackendEndpoint.Equals(sourceBackend)).ToArray();
            PartitionReport report = GetPartitions(sourceControl);
            // #1454 case 6: a heavy partition drains once, to the emptiest node without a heavy component, and the light
            // ones keep off every node holding or taking one while another node is ready.
            IReadOnlySet<ulong> heavy = _heavy;
            HashSet<string> heavyNodes = [.. _heavyNodes];
            string? heavyNode = report.Partitions.Any(p => heavy.Contains(p.Id))
                ? HeavyDrainTarget(ready.Select(x => (x.Registration.Node, (double)EffectiveNodeLoad(x.Report, closeSimLoad))), heavyNodes) : null;
            var heavyTarget = ready.FirstOrDefault(x => x.Registration.Node == heavyNode);
            if (heavyNode != null) heavyNodes.Add(heavyNode);
            if (heavyNodes.Contains(target.Registration.Node)
                && ready.Where(x => !heavyNodes.Contains(x.Registration.Node)).ToArray() is { Length: > 0 } light)
                target = light.MinBy(x => EffectiveNodeLoad(x.Report, closeSimLoad));
            IPEndPoint targetBackend = EndpointParser.Parse(target.Registration.Endpoint, DirectTransportConstants.DefaultPort);
            // A session the source reports in no partition is NOT a reason to refuse the
            // close. A client that has just been admitted, or whose character is being
            // respawned, is connected to the node without owning anything on it, and
            // there is simply nothing to hand over for it - it leaves by forced
            // reconnect like any other client the drain cannot follow. Throwing here
            // failed the whole close-node with a 500 and stalled the rotation, which is
            // what happened on 2026-08-15 with five pilots still boarding on node-1.
            var unpartitioned = sessions.Where(session =>
                report.Partitions.SingleOrDefault(x => x.Clients.Contains(session.ClientId)) == null).ToArray();
            if (unpartitioned.Length > 0)
            {
                Console.WriteLine($"Node {source.Node} holds {unpartitioned.Length} client(s) in no partition "
                    + $"({string.Join(", ", unpartitioned.Select(x => x.ClientId))})");
                // Riding the node into its kill was a guaranteed disconnect for these clients, and
                // the vanilla client does not always survive one (native teardown crashes on
                // host-left are the single biggest source of pilot churn). They have nothing to
                // hand over, but they do have a live gateway connection, and that is switchable:
                // move each to the node that owns its registry partition if one exists elsewhere
                // (the stranded-client case), otherwise to the drain target. A client still in
                // admission has no attachable binding yet and keeps the old forced-reconnect path;
                // so does one whose switch fails - that is no worse than before.
                foreach (ClientSession session in unpartitioned)
                {
                    if (session.PlayerReady == null) continue;
                    try
                    {
                        IPEndPoint rescueBackend = targetBackend;
                        string rescueNode = target.Registration.Node;
                        if (_registry.GetClientPartition(session.ClientId) is ClientPartition home)
                        {
                            PartitionOwnership owner = _registry.GetPartitionOwner(home.Partition);
                            NodeRegistration? ownerNode = owner.Node == source.Node ? null
                                : _registry.Nodes().FirstOrDefault(x => x.Node == owner.Node && x.Epoch == owner.Epoch
                                    && x.State == NodeState.Active && x.LeaseExpires > DateTimeOffset.UtcNow);
                            if (ownerNode != null)
                            {
                                rescueBackend = EndpointParser.Parse(ownerNode.Endpoint, DirectTransportConstants.DefaultPort);
                                rescueNode = ownerNode.Node;
                            }
                        }
                        else
                        {
                            // No placement at all - the client is invisible to every partition
                            // guard until something repairs it. The character-home path (§6.4)
                            // still knows where its body lives; adopt the client there instead
                            // of parking it on the drain target with the same broken state.
                            try
                            {
                                if (_registry.ResolveClientRoute(session.ClientId) is { } derived
                                    && derived.Node != source.Node)
                                {
                                    rescueBackend = EndpointParser.Parse(derived.Endpoint, DirectTransportConstants.DefaultPort);
                                    rescueNode = derived.Node;
                                    Console.WriteLine($"Unpartitioned client {session.ClientId} adopted via its character home on {rescueNode}");
                                }
                            }
                            catch (RegistryConflictException)
                            {
                                // mid-rotation home partition: the drain target is still the
                                // right parking spot, exactly as before this repair existed.
                            }
                        }
                        Console.WriteLine($"Switching unpartitioned client {session.ClientId} off closing node {source.Node} to {rescueNode}");
                        ControlSwitchBackend(session, rescueBackend);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Unpartitioned client {session.ClientId} could not be switched off {source.Node}: {e.Message}");
                    }
                }
            }
            var plans = sessions
                .Select(session => (Session: session,
                    Partition: report.Partitions.SingleOrDefault(x => x.Clients.Contains(session.ClientId))))
                .Where(x => x.Partition != null)
                .Select(x => (x.Session, Partition: x.Partition!))
                .GroupBy(x => x.Partition.Id).ToArray();
            NodeRegistration closed = _registry.CloseNode(source.Node, source.Epoch, forceDrainAfter);
            Console.WriteLine($"Closed node {source.Node} epoch {source.Epoch}; force drain at {closed.ForceDrainAt:O}");
            if (forceOnly)
            {
                _forceOnlyDrainEpoch = source.Epoch;
                return closed;
            }
            _forceOnlyDrainEpoch = null;

            // Cheapest first (§7.3): the partitions most likely to complete inside the drain window go
            // before the expensive ones, so a deadline strands the least.
            CostModel model = _costModel;
            foreach (var plan in plans.OrderBy(p => model.Cost(p.First().Partition.EstimatedSnapshotBytes, p.Count())))
            {
                if (closed.ForceDrainAt!.Value - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(45))
                    break;
                ClientSession[] moving = plan.Select(x => x.Session).ToArray();
                PartitionInfo partition = plan.First().Partition;
                // T-0296 r3: left to the drain catch-up (DrainClosedNodes), which retries until the join has settled.
                if (DrainDeferredForJoin(partition.Id, moving, source.Node)) continue;
                var to = heavyNode != null && heavy.Contains(partition.Id) ? heavyTarget : target;
                Console.WriteLine($"Draining occupied partition {partition.Id} from {source.Node} to {to.Registration.Node}"
                    + $" with {moving.Length} client(s)" + (heavy.Contains(partition.Id) ? " (heavy)" : ""));
                await Handover(moving, sourceControl, to.Control,
                    EndpointParser.Parse(to.Registration.Endpoint, DirectTransportConstants.DefaultPort), partition.Id);
                if (heavy.Contains(partition.Id)) HeavyMoved([partition.Id], moving.Length);
            }
            return closed;
        }
        finally { _rotation.Release(); }
    }

    /// <summary>
    /// The sessions for every client a partition carries, or null when even one of them cannot be
    /// switched right now. A handover that moved only part of a partition's crew would put players
    /// who share a grid on two nodes, so "all or defer" is the only safe answer.
    /// </summary>
    /// <summary>How long after CLIENT_READY an automatic (balance/co-location) handover leaves a client alone (T-0238).</summary>
    internal static readonly TimeSpan PostReadySettle = TimeSpan.FromSeconds(10);

    private bool JoinStillSettling(ulong client) =>
        _clients.TryGetValue(client, out ClientSession? session) && session.WorldReady
        && DateTimeOffset.UtcNow - session.WorldReadyAt < PostReadySettle;

    /// <summary>
    /// T-0237: a connected client that has not settled (not world-ready, or ready under <see cref="PostReadySettle"/>)
    /// whose routed placement or character home is <paramref name="partition"/>, or 0. Such a client is not in the
    /// node's report of the partition yet, so only the gateway can see that moving it would strand the join.
    /// </summary>
    private ulong JoiningClientPlacedIn(ulong partition)
    {
        if (_registry == null) return 0;
        GatewayRegistry registry = _registry;
        // T-0237: a join the gateway is still HOLDING (its home loading or recovering) has no session yet, but the
        // partition was loaded for it: w33 2026-09-27 20:12:11 a co-location moved Pilot-038's just-loaded home
        // 111231 away one line before its held join was accepted. It counts as a client that has not settled.
        ulong[] held;
        lock (_heldJoins) held = [.. _heldJoins.Select(x => x.ClientId)];
        return JoiningClientPlacedIn(_clients.Values.Select(x => (x.ClientId, x.WorldReady, x.WorldReadyAt))
                .Concat(held.Where(id => !_clients.ContainsKey(id)).Select(id => (id, false, default(DateTimeOffset)))), partition,
            client => registry.GetClientPartition(client)?.Partition, client => registry.GetCharacterHome(client)?.Partition,
            DateTimeOffset.UtcNow);
    }

    internal static ulong JoiningClientPlacedIn(IEnumerable<(ulong Client, bool WorldReady, DateTimeOffset WorldReadyAt)> sessions,
        ulong partition, Func<ulong, ulong?> placement, Func<ulong, ulong?> home, DateTimeOffset now)
    {
        foreach ((ulong client, bool ready, DateTimeOffset readyAt) in sessions)
        {
            if (ready && now - readyAt >= PostReadySettle) continue;
            if (placement(client) == partition || home(client) == partition) return client;
        }
        return 0;
    }

    /// <summary>
    /// T-0296 r3: the client that defers a client-bearing move of <paramref name="partition"/> with the crew
    /// <paramref name="crew"/>, or 0. A client that is NOT in the crew and is still joining (held, not world-ready, or
    /// inside the post-ready settle) while placed or homed in the partition would be left behind: its body leaves
    /// with the partition, its session stays on the source and gets a fresh body there (T-0237 shape). With
    /// <paramref name="settleCrew"/> a crew member inside its own post-ready settle defers the move as well (T-0238);
    /// the drains pass it because they retry every pass, an operator /handover does not.
    /// </summary>
    internal static ulong ClientBearingMoveBlocker(IEnumerable<(ulong Client, bool WorldReady, DateTimeOffset WorldReadyAt)> sessions,
        IReadOnlyCollection<ulong> crew, ulong partition, Func<ulong, ulong?> placement, Func<ulong, ulong?> home, DateTimeOffset now,
        bool settleCrew)
    {
        (ulong Client, bool WorldReady, DateTimeOffset WorldReadyAt)[] all = [.. sessions];
        if (settleCrew)
            foreach ((ulong client, bool ready, DateTimeOffset readyAt) in all)
                if (crew.Contains(client) && ready && now - readyAt < PostReadySettle) return client;
        return JoiningClientPlacedIn(all.Where(x => !crew.Contains(x.Client)), partition, placement, home, now);
    }

    private ulong ClientBearingMoveBlocker(ulong partition, IReadOnlyList<ClientSession> crew, bool settleCrew)
    {
        if (_registry == null) return 0;
        GatewayRegistry registry = _registry;
        ulong[] held;
        lock (_heldJoins) held = [.. _heldJoins.Select(x => x.ClientId)];
        return ClientBearingMoveBlocker(_clients.Values.Select(x => (x.ClientId, x.WorldReady, x.WorldReadyAt))
                .Concat(held.Where(id => !_clients.ContainsKey(id)).Select(id => (id, false, default(DateTimeOffset)))),
            [.. crew.Select(x => x.ClientId)], partition,
            client => registry.GetClientPartition(client)?.Partition, client => registry.GetCharacterHome(client)?.Partition,
            DateTimeOffset.UtcNow, settleCrew);
    }

    /// <summary>T-0296 r3: a drain skips the partition this pass; DrainClosedNodes offers it again every second.</summary>
    private bool DrainDeferredForJoin(ulong partition, IReadOnlyList<ClientSession> crew, string node)
    {
        if (ClientBearingMoveBlocker(partition, crew, settleCrew: true) is not (var blocker and not 0)) return false;
        Console.WriteLine($"Partition {partition} on closed node {node} is carrying or receiving client {blocker}, which is still joining "
            + $"(not world-ready or under {PostReadySettle.TotalSeconds:F0}s ready); drain handover deferred");
        return true;
    }

    private ClientSession[]? ResolveMovingSessions(IReadOnlyList<ulong> clients, IPEndPoint sourceBackend)
    {
        List<ClientSession> sessions = [];
        foreach (ulong client in clients)
        {
            if (!_clients.TryGetValue(client, out ClientSession? session)
                || session.SwitchingBackend || !session.BackendEndpoint.Equals(sourceBackend) || !session.WorldReady)
                return null;
            sessions.Add(session);
        }
        return sessions.Count == 0 ? null : sessions.ToArray();
    }

    private async Task Handover(ClientSession session, string source, string target, IPEndPoint backend, ulong partition, bool pauseAfterCapture = false)
        => await Handover([session], source, target, backend, partition, pauseAfterCapture);

    private async Task Handover(IReadOnlyList<ClientSession> sessions, string source, string target, IPEndPoint backend, ulong partition, bool pauseAfterCapture = false,
        TimeSpan? readinessBudget = null)
    {
        // Switch locks in client-id order: two handovers that overlap on a client take them the same
        // way round and one waits instead of both deadlocking half-held.
        ClientSession[] ordered = sessions.OrderBy(x => x.ClientId).ToArray();
        int taken = 0;
        try
        {
            foreach (ClientSession each in ordered)
            {
                await each.SwitchLock.WaitAsync();
                taken++;
            }
            foreach (ClientSession each in sessions)
            {
                if (!_clients.TryGetValue(each.ClientId, out ClientSession? current) || !ReferenceEquals(current, each))
                    throw new InvalidOperationException("Client disconnected before handover");
                // A client that has not sent CLIENT_READY is still consuming the join sequence; switching
                // its backend suppresses exactly those messages, so the join starves and times out.
                if (!each.WorldReady)
                    throw new RegistryConflictException($"Client {each.ClientId} is still joining; handover refused until CLIENT_READY");
            }
            // T-0296 r3 (T-0237 shape): the crew is what the source REPORTS, and a client still joining into this
            // partition is not in that report. Moving the partition takes its body away while its session stays on
            // the source. The automatic path and the drains check before they call; this is the operator /handover
            // and the window between their check and the switch locks.
            if (ClientBearingMoveBlocker(partition, sessions, settleCrew: false) is var joining and not 0)
                throw new RegistryConflictException($"Partition {partition} is the placement or home of client {joining}, which is still joining; handover refused until it has settled");
            if (_registry == null)
            {
                if (sessions.Count != 1)
                    throw new InvalidOperationException("Multi-client handover requires the registry");
                await UnfencedHandover(sessions[0], source, target, backend, partition);
            }
            else
                await FencedHandover(sessions, source, target, backend, partition, pauseAfterCapture, readinessBudget);
        }
        finally
        {
            for (int i = taken - 1; i >= 0; i--)
                ordered[i].SwitchLock.Release();
        }
    }

    private void ControlSwitchBackend(ClientSession session, IPEndPoint backend)
    {
        session.SwitchLock.Wait();
        try
        {
            if (!_clients.TryGetValue(session.ClientId, out ClientSession? current) || !ReferenceEquals(current, session))
                throw new InvalidOperationException("Client disconnected before switch");
            SwitchBackend(session, backend);
        }
        catch
        {
            session.Client?.Disconnect();
            throw;
        }
        finally { session.SwitchLock.Release(); }
    }

    private async Task<RespawnExecutionResult> RespawnSwitch(ClientSession session, RespawnSwitchRequest request, string authorization)
    {
        // A pod (respawn ship) has no medbay anchors: it creates its own fresh partition on the
        // target node, so only the target's name is validated here and ownership not at all.
        bool pod = !string.IsNullOrWhiteSpace(request.RespawnShipId);
        // SE1-0122: a spawn point preview moves the dead client to the medbay's node without respawning it.
        bool preview = request.Preview && !pod;
        if (request.OperationId == Guid.Empty || request.UserId == 0 || request.SerialId < 0 || request.IdentityId <= 0
            || string.IsNullOrWhiteSpace(request.Origin) || request.ModelName?.Length > 256
            || request.RespawnShipId?.Length > 256 || request.ClientState?.Length > 8 * 1024 * 1024
            || !pod && (request.RespawnEntityId <= 0 || request.GridEntityId <= 0
                || request.PartitionId == 0 || request.Generation <= 0))
            throw new ArgumentException("Invalid respawn switch request");
        await session.SwitchLock.WaitAsync();
        GatewayRegistry registry = _registry!;
        IPEndPoint sourceBackend = session.BackendEndpoint;
        ClientPartition? previous = registry.GetClientPartition(session.ClientId);
        bool routeChanged = false, switched = false, success = false;
        try
        {
            PlayerReady ready = session.PlayerReady ?? throw new RegistryConflictException("Client has no World Authority player binding");
            if (ready.UserId != request.UserId || ready.SerialId != request.SerialId || ready.Identity.IdentityId != request.IdentityId)
                throw new RegistryConflictException("Respawn request does not match the attached player binding");
            NodeRegistration targetNode;
            if (pod)
            {
                targetNode = registry.Nodes().SingleOrDefault(node => node.Node == request.Origin
                    && node.Role == NodeRole.Regular && node.State == NodeState.Active)
                    ?? throw new RegistryConflictException("Respawn target node is not active");
            }
            else
            {
                // SE-1189: the client's list names the medbay's partition as it was when the list loaded, and a trader
                // leaving the station splits that partition every few minutes. A preview follows it to the partition
                // that holds the grid now; the retired row has no node, and the switch used to fail on it.
                if (preview)
                {
                    ulong live = registry.LivePartitionOf(request.PartitionId, request.GridEntityId);
                    if (live != request.PartitionId) request = request with { PartitionId = live };
                }
                PartitionOwnership owner = registry.GetPartitionOwner(request.PartitionId);
                // The source node detached the client's view before asking, so a failed switch costs the client a
                // reconnect. A preview just follows a medbay that moved in the meantime; the target checks it holds it.
                if (preview && (owner.Node != request.Origin || owner.Generation != request.Generation))
                    request = request with { Origin = owner.Node, Generation = owner.Generation };
                if (owner.Node != request.Origin || owner.Generation != request.Generation)
                    throw new RegistryConflictException("Respawn partition ownership changed");
                targetNode = registry.Nodes().SingleOrDefault(node => node.Node == owner.Node && node.Epoch == owner.Epoch
                    && node.Role == NodeRole.Regular && node.State == NodeState.Active)
                    ?? throw new RegistryConflictException("Respawn partition has no active owner");
            }
            if (string.IsNullOrWhiteSpace(targetNode.ControlEndpoint))
                throw new RegistryConflictException("Respawn target has no control endpoint");
            IPEndPoint targetBackend = EndpointParser.Parse(targetNode.Endpoint, DirectTransportConstants.DefaultPort);
            lock (session.RelayLock)
            {
                session.SwitchingBackend = true;
                session.BufferServerPackets = true;
                session.SuppressJoinMessages = true;
            }
            // The pod's fresh partition does not exist until the spawn, so there is nothing to route to
            // yet: clear the route (a null registry route is always route-valid) and let the target
            // node's heartbeat re-establish it from the fresh partition's client claim - the same
            // route-follow the local pod spawn already uses. Rollback restores the previous route.
            // A previewing client has no character in any partition either, and its respawn at the
            // medbay is a local spawn on the target, so it follows the same way.
            ClientRoute? route = pod || preview ? registry.RestoreClientRoute(session.ClientId, null)
                : registry.RouteClient(session.ClientId, request.PartitionId);
            routeChanged = true;
            lock (session.RelayLock)
            {
                session.RegistryRoute = route;
                session.RegistryRouteValidated = false;
            }
            switched = true;
            SwitchBackend(session, targetBackend);
            AttachBackend(session);

            // Pods spawn their prefab asynchronously; the target reports not-ready until the
            // controlled entity exists, so give that path more headroom than the same-frame medbay
            // spawn - but stay under the routing node's 30s switch-call timeout.
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(pod ? 20 : 10);
            RespawnExecutionResult result;
            while (true)
            {
                using var targetRequest = new HttpRequestMessage(HttpMethod.Post,
                    targetNode.ControlEndpoint.TrimEnd('/') + "/cluster/respawn-execute")
                {
                    Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(authorization))
                    targetRequest.Headers.TryAddWithoutValidation("Authorization", authorization);
                using HttpResponseMessage response = await _http.SendAsync(targetRequest);
				string payload = await response.Content.ReadAsStringAsync();
				if (!response.IsSuccessStatusCode)
					throw new InvalidOperationException($"Respawn target returned HTTP {(int)response.StatusCode}: {payload}");
				result = JsonSerializer.Deserialize<RespawnExecutionResult>(payload,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Target returned an empty respawn result");
                if (result.Ready) break;
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Target player was not ready for respawn");
                await Task.Delay(100);
            }
            if (!result.Success || !preview && result.ControlledEntityId <= 0)
                throw new InvalidOperationException("Target rejected the respawn request");
            success = true;
			try
			{
				if (!pod && !preview)
				{
					route = registry.RouteClient(session.ClientId, request.PartitionId, result.ControlledEntityId);
					lock (session.RelayLock) session.RegistryRoute = route;
				}
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine($"Respawn {request.OperationId:D} succeeded before controlled-entity route persistence: {exception.Message}");
			}
            return result;
        }
		catch (Exception failure)
        {
			try
			{
				if (routeChanged)
				{
					ClientRoute? restored = registry.RestoreClientRoute(session.ClientId, previous);
					lock (session.RelayLock)
					{
						session.RegistryRoute = restored;
						session.RegistryRouteValidated = false;
					}
				}
				if (switched)
				{
					SwitchBackend(session, sourceBackend);
					AttachBackend(session);
				}
			}
			catch (Exception rollback)
			{
				throw new AggregateException("Respawn switch and rollback failed", failure, rollback);
			}
			throw;
        }
        finally
        {
            // T-0296 r3: the lock is released whatever the completion does. Leaked, the next handover or close-node
            // switch of this client waits on it forever while holding the rotation lock.
            bool healthy = false;
            try
            {
                healthy = FinishSwitching(session, () =>
                {
                    if (RouteHealthy(session)) FlushClientBacklog(session);
                    if (success && RouteHealthy(session))
                        FlushServerBacklog(session);
                    else
                        DiscardServerBacklog(session);
                    session.BufferServerPackets = false;
                });
            }
            finally { session.SwitchLock.Release(); }
            if (!healthy) session.Client?.Disconnect();
        }
    }

    private async Task UnfencedHandover(ClientSession session, string source, string target, IPEndPoint backend, ulong partition)
    {
        lock (session.RelayLock)
            session.SwitchingBackend = true;
        try
        {
            string query = "?partition=" + partition;
            byte[] capture = await _http.GetByteArrayAsync(ClusterUrl(source, "capture") + query + "&clients=" + session.ClientId);
            HandoverCapture handover = HandoverCapture.Parse(capture);

            using (StringContent state = new(handover.NetworkState))
            {
                HttpResponseMessage prepare = await _http.PostAsync(ClusterUrl(target, "prepare-import"), state);
                prepare.EnsureSuccessStatusCode();
            }
            using (ByteArrayContent content = new(handover.Sector))
            {
                HttpResponseMessage import = await _http.PostAsync(ClusterUrl(target, "import") + query, content);
                import.EnsureSuccessStatusCode();
                Console.WriteLine((await import.Content.ReadAsStringAsync()).Trim());
            }

            session.SuppressJoinMessages = true;
            lock (session.RelayLock)
            {
                session.TargetServerPacketSeen = false;
                session.TargetReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            session.BufferServerPackets = true;
            SwitchBackend(session, backend);
            lock (session.RelayLock) session.ValidationLink = session.Link;
            AttachBackend(session);

            HttpResponseMessage adopt = await _http.PostAsync(ClusterUrl(target, "adopt") + "?client=" + session.ClientId + "&state=" + Uri.EscapeDataString(handover.StateFor(session.ClientId)), null);
            adopt.EnsureSuccessStatusCode();
            Console.WriteLine((await adopt.Content.ReadAsStringAsync()).Trim());

            if (!HasHealthyRoute(session, backend))
                throw new IOException("Target backend disconnected before handover completion " + backend);

            HttpResponseMessage remove = await _http.PostAsync(ClusterUrl(source, "remove") + query, null);
            remove.EnsureSuccessStatusCode();
            Console.WriteLine((await remove.Content.ReadAsStringAsync()).Trim());
        }
        finally
        {
			bool healthy = FinishSwitching(session, () =>
            {
				if (RouteHealthy(session))
                {
                    FlushClientBacklog(session);
                    FlushServerBacklog(session);
                }
                session.BufferServerPackets = false;
			});
            if (!healthy)
                session.Client?.Disconnect();
        }
    }

    /// <param name="sessions">
    /// Every client whose character rides this partition. They all leave the source in ONE transfer:
    /// the partition is frozen once, all their views are captured from that single frozen state, and
    /// they are switched to the target inside the same frozen window before a single registry commit
    /// makes the move authoritative. Handing them over one at a time would put two players who share
    /// a grid on two different nodes for the duration, which is the desync this exists to prevent.
    /// </param>
    private async Task FencedHandover(IReadOnlyList<ClientSession> sessions, string source, string target, IPEndPoint backend, ulong partition, bool pauseAfterCapture,
        TimeSpan? readinessBudget = null)
    {
        GatewayRegistry registry = _registry!;
        (NodeRegistration sourceNode, NodeRegistration targetNode) = ResolveFencedNodes(registry, source, target, backend);
        if (sessions.Count == 0)
            throw new InvalidOperationException("Handover needs at least one client session");
        IPEndPoint sourceEndpoint = EndpointParser.Parse(sourceNode.Endpoint, DirectTransportConstants.DefaultPort);
        foreach (ClientSession each in sessions)
            if (!each.BackendEndpoint.Equals(sourceEndpoint))
                throw new RegistryConflictException($"Client {each.ClientId} is not attached to the handover source backend");
        PartitionOwnership? row;
        try
        {
            row = registry.GetPartitionOwner(partition);
        }
        catch (ArgumentException) when (registry.IsAllocatedUnclaimed(partition))
        {
            // T-0354b: an id the registry handed out and nobody claimed yet is a layout the source made a moment ago; its
            // own claim is under way. Claimed here, the row would be the node's at generation 1 and the node's claim
            // (expecting generation 0) would be refused - which the node reads as the registry closing it.
            throw new RegistryApplyPendingRefusal($"Partition {partition} is not claimed yet (the source's claim is under way)");
        }
        catch (ArgumentException) when (sourceNode.State == NodeState.Closed && ClusterProtocol.PlaceholderPartitionIds.Contains(partition))
        {
            // T-0374: a Closed source's row-less partition is a placeholder layout around a stray; ClaimPartition refuses
            // a Closed node, and the crew waited for the force drain. Claimed below, just before Begin (#1283).
            row = null;
        }
        catch (ArgumentException)
        {
            row = registry.ClaimPartition(partition, sourceNode.Node, sourceNode.Epoch, 0);
            Console.WriteLine($"Claimed runtime partition {partition} for {sourceNode.Node} generation {row.Generation}");
        }
        // T-0373: the registry hands out its LIVE row, and a topology change releases it IN PLACE (Node = null).
        // Live 2026-10-02 07:04:35.26: 307625 was split on node-187 between this read and Begin below, Begin got
        // owner.Node == null and its node lookup threw "Value cannot be null. (Parameter 'key')". A copy keeps what
        // was checked; Begin then refuses the stale generation as "ownership is fenced".
        row = row == null ? null : row with { };
        if (row != null && (sourceNode.Node != row.Node || sourceNode.Epoch != row.Epoch))
            // w22 2026-08-21: partitions 1 and 3 hit this 27 times in 4.5 h and never recovered, and the
            // message could not distinguish a dead epoch from a different live node from a null owner -
            // see findings/partitions-1-and-3-are-permanently-unschedulable.md. Name the actual owner.
            throw new RegistryConflictException($"Partition {partition} owner does not match source endpoint '{source}'"
                + $" (source {sourceNode.Node} epoch {sourceNode.Epoch}, registry owner {row.Node} epoch {row.Epoch})");

        string transferId = Guid.NewGuid().ToString("D");
        string clientList = string.Join(",", sessions.Select(x => x.ClientId));
        IPEndPoint sourceBackend = sessions[0].BackendEndpoint;
        bool begun = false;
        bool aborted = false;
        bool captured = false;
        bool prepared = false;
        bool committed = false;
        bool safeRoute = true;
        // Sessions that have actually been moved onto the target backend, in switch order, so a
        // failure part-way through the fan-out restores exactly those and leaves the rest alone.
        List<ClientSession> switchedSessions = [];
        // T-0369: the capture those sessions left the source with; an abort hands each its view back there.
        HandoverCapture? switchedWith = null;
        // T-0369: clients the TARGET adopted. Their replication view moved on there, so the captured view is stale:
        // an abort ends such a session instead of handing it back to the source.
        HashSet<ClientSession> adoptedOnTarget = [];

        // The un-freeze. `SwitchingBackend` buffers the client's traffic in BOTH directions
        // (SendToBackend queues into ClientBacklog; FlushClientBacklog refuses to drain), so the
        // player's blackout ends exactly when this runs. Idempotent by construction - on a second
        // call the flag is already clear and both backlogs are empty - which is what lets it run
        // once on the success path and again in the `finally`.
        void CompleteSwitch()
        {
            foreach (ClientSession each in sessions)
            {
                bool healthy = FinishSwitching(each, () =>
                {
                    if ((committed || safeRoute) && RouteHealthy(each))
                        FlushClientBacklog(each);
                    if (committed && RouteHealthy(each))
                        FlushServerBacklog(each);
                    each.BufferServerPackets = false;
                    each.ValidationLink = null;
                });
                if ((committed || safeRoute) && !healthy)
                    each.Client?.Disconnect();
            }
        }
        HandoverPause? pause = null;

        // Size proxies for the transfer record, read before the freeze so telemetry never
        // lengthens the frozen window; a lagging report just leaves them null.
        PartitionInfo? reported = null;
        try { reported = GetPartitions(source).Partitions.SingleOrDefault(x => x.Id == partition); }
        catch (Exception) { }

        // HandoverDelta: stage the partition on the target before anything is frozen.
        string? presyncId = await TryPresync(source, target, partition);
        string presyncQuery = presyncId == null ? "" : "&presync=" + presyncId;

        // #1283 (T-0296 F38): no heartbeat renews a placeholder row (it is not in the node's m_owned). Claimed before the
        // partition report and the presync above (10 s client timeout each), its lease (claim + ttl) lapsed under them:
        // ExpireLeases released it unowned at generation 2, Begin refused generation 1 as fenced, and the unrecoverable
        // row paused balancing (RecoveryPending) until the 5-min unowned reap. Claimed here, Begin follows on the same
        // thread, and from Begin on the transfer exempts the row from expiry; an exit before Commit releases it.
        bool placeholderClaimed = row == null;
        PartitionOwnership owner = row ?? (registry.ClaimClosedNodePlaceholder(partition, sourceNode.Node, sourceNode.Epoch) with { });
        if (placeholderClaimed)
            Console.WriteLine($"Claimed placeholder partition {partition} for Closed {sourceNode.Node} generation {owner.Generation}");
        uint generation = checked((uint)owner.Generation);
        string fence = $"?transferId={transferId}&partition={partition}&generation={generation}";

        foreach (ClientSession each in sessions)
            lock (each.RelayLock)
                each.SwitchingBackend = true;
        try
        {
            registry.Begin(transferId, partition, owner.Node, owner.Epoch, owner.Generation, targetNode.Node, targetNode.Epoch);
            begun = true;
            HandoverTelemetry.Transfer telemetry = _handoverTelemetry.Begin(transferId, partition,
                owner.Node, targetNode.Node, clients: sessions.Count, reported?.Pcu, reported?.MemberCount);
            LogTransfer(transferId, partition, "begun", (sessions.Count > 1 ? $" clients={clientList}" : "") + (presyncId == null ? "" : $" presync={presyncId}"));

            captured = true;
            byte[] capture = await FencedCapture(ClusterUrl(source, "capture") + fence + "&clients=" + clientList + presyncQuery, partition);
            HandoverCapture handover = HandoverCapture.Parse(capture);
            // Every client must have come back with a view. A missing one would only surface at its
            // own adopt, after other clients are already on the target - too late to abort cleanly.
            foreach (ClientSession each in sessions) handover.StateFor(each.ClientId);
            telemetry.SectorBytes = handover.Sector.Length;
            telemetry.NetworkStateBytes = handover.NetworkState.Length;
            telemetry.ClientStateBytes = handover.ClientStateBytes;
            LogTransfer(transferId, partition, "captured",
                $" sectorBytes={handover.Sector.Length} clients={sessions.Count} clientStateBytes={handover.ClientStateBytes}");

            if (pauseAfterCapture)
            {
                pause = new HandoverPause(transferId, partition, sessions[0]);
                if (!_handoverPauses.TryAdd(transferId, pause))
                    throw new InvalidOperationException("Duplicate handover pause " + transferId);
                LogTransfer(transferId, partition, "paused-after-capture");
                await pause.Release.Task;
                LogTransfer(transferId, partition, "pause-released");
            }

            using (StringContent state = new(handover.NetworkState))
            {
                prepared = true;
                TransferGrant grant = registry.IssueGrant(new("handover", transferId, partition, owner.Generation, 0,
                    sourceNode.Node, sourceNode.Epoch, targetNode.Node));
                // The target is told up front which clients this transfer carries, so it can refuse to
                // commit on a partial adoption instead of trusting the gateway to have asked for all.
                HttpResponseMessage prepare = await PostGranted(ClusterUrl(target, "prepare-import") + fence + "&clients=" + clientList, state, grant.Token);
                prepare.EnsureSuccessStatusCode();
            }
            LogTransfer(transferId, partition, "prepared");

            handover = await ImportWithFallback(source, target, partition, fence, "&clients=" + clientList, handover, transferId, presyncId != null,
                () => registry.IssueGrant(new("handover", transferId, partition, owner.Generation, 0, sourceNode.Node, sourceNode.Epoch, targetNode.Node)));
            LogTransfer(transferId, partition, "imported");

            // From here to the commit the partition exists frozen on the source and imported on the
            // target, so no client can observe it simulating in two places. Every session is moved
            // inside that window; the move only becomes authoritative at the single Commit below.
            safeRoute = false;
            switchedWith = handover;
            foreach (ClientSession each in sessions)
            {
                each.SuppressJoinMessages = true;
                // Arm the target-readiness detector BEFORE the connect packet is relayed, which is
                // what the single-client path above has always done. This loop had the two halves
                // inverted: it relayed (and slept 100 ms inside AttachBackend) and only THEN set
                // ValidationLink and installed TargetReady. For that whole window a reply from the
                // target was dropped twice over - the "targetMessage == 7" match cannot fire while
                // ValidationLink is unset, and this block then reset TargetServerPacketSeen and
                // installed a fresh TargetReady, discarding anything that had landed - so the wait
                // on TargetReady later in this method blocked for the NEXT qualifying packet. The
                // faster the target answered, the likelier its answer fell in the hole.
                //
                // CORRECTNESS ONLY - no performance claim. The window is almost never populated:
                // adopted->target-traffic-ready over 98 transfers is p50 45-49 ms, p95 109-177 ms
                // with a single 3596 ms outlier, so this is NOT expected to move any measurement.
                // The 100 ms sleep in AttachBackend is a separate change, measured separately.
                lock (each.RelayLock)
                {
                    each.TargetServerPacketSeen = false;
                    each.TargetReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                each.BufferServerPackets = true;
                SwitchBackend(each, backend);
                switchedSessions.Add(each);
                lock (each.RelayLock) each.ValidationLink = each.Link;
                AttachBackend(each);
            }

            // imported->adopt-ready is the largest per-transfer span in the handover (w22 baseline-0:
            // p50 148 ms, p95 3289 ms, max 4547 ms) and it is entirely inside the frozen window, so
            // it is latency the player feels directly. It holds two sequential-per-client loops - the
            // backend switch above and the adopt-ready poll below - and one stamp between them is
            // what says which of the two to attack. Split before optimising either.
            LogTransfer(transferId, partition, "sessions-attached");

            string Adoption(ClientSession each) =>
                fence + "&client=" + each.ClientId + "&state=" + Uri.EscapeDataString(handover.StateFor(each.ClientId));
            // One budget for the whole crew, because it bounds the frozen window - but it grows with
            // the crew, so a big platform partition does not spend the last client's share waiting
            // for the first. The per-client 30 s of the single-client path is the floor. An optional move
            // passes a shorter budget (BalanceReadinessBudget).
            using (CancellationTokenSource readinessTimeout = new(readinessBudget ?? TimeSpan.FromSeconds(30 + 10 * (sessions.Count - 1))))
            {
                foreach (ClientSession each in sessions)
                {
                    while (true)
                    {
                        try
                        {
                            // T-0369: a transport failure of one poll is retried like a "not ready" answer. Only the
                            // answer was retried, so one failed POST (2026-10-02 00:05:08, transfer 8b6dd7a7) aborted a
                            // handover whose client was already on the target. adopt-ready is a poll by design: asking
                            // again after an answer that was lost finds the player it created and re-validates.
                            using HttpResponseMessage ready = await SendThroughTransportFailures(
                                token => _http.PostAsync(ClusterUrl(target, "adopt-ready") + Adoption(each), null, token),
                                $"adopt-ready of client {each.ClientId} on {target}", readinessTimeout.Token);
                            if (ready.IsSuccessStatusCode)
                                break;
                            await Task.Delay(100, readinessTimeout.Token);
                        }
                        catch (OperationCanceledException) when (readinessTimeout.IsCancellationRequested)
                        {
                            throw new TimeoutException("Target client adoption readiness timed out");
                        }
                    }
                }
            }
            LogTransfer(transferId, partition, "adopt-ready");

            foreach (ClientSession each in sessions)
            {
                // T-0369: a fenced adopt is idempotent per client on the node (the transfer keeps the count it
                // answered and returns it again for the same view), so a transport failure is asked again.
                using HttpResponseMessage adopt = await SendThroughTransportFailures(
                    token => _http.PostAsync(ClusterUrl(target, "adopt") + Adoption(each), null, token),
                    $"adopt of client {each.ClientId} on {target}");
                adopt.EnsureSuccessStatusCode();
                adoptedOnTarget.Add(each);
            }
            LogTransfer(transferId, partition, "adopted");

            // The clients answer the target in parallel, so this is one shared window rather than a
            // sum - widened a little per extra client because their first packets are not simultaneous.
            //
            // The window used to be 1 s, and a single quiet client failed the WHOLE fenced handover:
            // close-node answered 500, the partition rolled back, and the rotation stalled - all
            // evening on world-7, because with constant churn some client of a 20-partition drain is
            // always mid-death. A busy target can also exceed 1 s for a healthy client, so first
            // give everyone a real window; then a client still silent is almost certainly dead, and
            // it must cost only itself: disconnect it (its reconnect resolves through the registry
            // to wherever the partition lands) and commit the move for the partition and the crew
            // that did answer.
            List<ClientSession> live = sessions.ToList();
            // adopted -> target-traffic-ready was the largest span in the whole handover on w22
            // (p95 3.4 s of a 4.5 s transfer), and nothing said what was in it. It is not
            // game-thread cost - it is this wait for each client's first packet to reach the
            // target, so no import/capture/release knob can touch it and it cannot cause a sim
            // dip. It is still latency the player feels, so record it: the clients answer in
            // parallel while this loop awaits them in order, so an entry is an upper bound for
            // every client but the last, and the last one is the exact total.
            System.Diagnostics.Stopwatch readiness = System.Diagnostics.Stopwatch.StartNew();
            List<string> observed = new();
            using (CancellationTokenSource trafficTimeout = new(TimeSpan.FromSeconds(10 + 0.5 * (sessions.Count - 1))))
            {
                foreach (ClientSession each in sessions)
                {
                    try
                    {
                        await each.TargetReady.Task.WaitAsync(trafficTimeout.Token);
                        observed.Add($"{each.ClientId}={readiness.Elapsed.TotalMilliseconds:F0}ms");
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"Client {each.ClientId} showed no target traffic within the readiness window; "
                            + $"disconnecting it and committing partition {partition} without it");
                        each.Client?.Disconnect();
                        live.Remove(each);
                        observed.Add($"{each.ClientId}=timeout");
                    }
                }
            }
            if (sessions.Count != 0)
                Console.WriteLine($"handover_traffic transferId={transferId} partition={partition} clients={sessions.Count} "
                    + $"totalMs={readiness.Elapsed.TotalMilliseconds:F0} observed=[{string.Join(" ", observed)}]");
            LogTransfer(transferId, partition, "target-traffic-ready");
            await AwaitDurableTee(source, transferId, partition, handover.Sector);

            HandoverRecord committedHandover;
			while (true)
            {
				foreach (ClientSession each in live) EnsureAttachedBindingCurrent(each);
				// One commit for all of them, under every session's relay lock at once: a client
				// whose route went unhealthy between the checks must not be committed as switched.
				// Only the sessions that survived the traffic validation take part - a client
				// disconnected there must not veto the commit it was dropped from.
				using (RelayLocks held = RelayLocks.Take(live))
				{
					foreach (ClientSession each in live)
						if (!backend.Equals(each.BackendEndpoint) || !RouteHealthy(each))
							throw new IOException("Target backend disconnected before handover commit " + backend);
					if (live.Any(each => !AttachedBindingCurrent(each))) continue;
					registry.Activate(transferId);
					committedHandover = registry.Commit(transferId);
					committed = true;
				}
				break;
            }
            LogTransfer(transferId, partition, "active");
            LogTransfer(transferId, partition, "committed");

            await PostCommittedWithRetry(ClusterUrl(target, "commit") + fence + "&committedGeneration=" + committedHandover.CommittedGeneration, "target commit");
            LogTransfer(transferId, partition, "target-committed");

            // PLAYER-VISIBLE: the blackout ends HERE, not after the source release below.
            // registry.Commit has already run, and the commit block above throws unless every live
            // session is bound to the target - so no live session can still point at the node whose
            // grids the release is about to delete, and an early un-freeze cannot deliver a packet
            // to it. The release is the SOURCE's cleanup; the client does not depend on it.
            // Waiting for it cost 9.7% of all player blackout time: p95 562 ms, of which 541 ms is
            // the source's own game thread inside CloseEntities. It is a hump, not a flat tax -
            // ~14% of the 2.7-3.1 s band of multi-second freezes and ~4% of the extreme tail, where
            // a slow import dominates instead. See cluster-regression
            // findings/the-player-waits-for-the-source-node-to-delete-the-grids.md
            //
            // Failure semantics unchanged: `committed` is already true, so a release failure
            // rethrows past the catch's cleanup block exactly as before, and the `finally` calls
            // CompleteSwitch again harmlessly.
            CompleteSwitch();
            // Separate stamp: begun->released still measures the TRANSFER, begun->unfrozen measures
            // the PLAYER. Without it this change looks like no change in every existing table.
            LogTransfer(transferId, partition, "unfrozen");

            await PostCommittedWithRetry(ClusterUrl(source, "release") + fence, "source release");
            LogTransfer(transferId, partition, "released");
            registry.FinalizeReconciliation(transferId);
        }
        catch (Exception failure)
        {
            DiagnosticReports.Write("handover_failure", "Partition handover failed", failure,
                transferId, partition, "handover");
            if (!committed)
            {
                List<Exception> cleanupFailures = [];
                // Every session that made it onto the target goes back to the source, which is still
                // holding the partition frozen; one that fails to come back is disconnected below.
                List<ClientSession> stranded = [];
                foreach (ClientSession each in switchedSessions)
                {
                    try
                    {
                        SwitchBackend(each, sourceBackend);
                        AttachBackend(each);
                    }
                    catch (Exception e) { stranded.Add(each); cleanupFailures.Add(e); }
                }
                if (switchedSessions.Count != 0 && stranded.Count == 0)
                {
                    safeRoute = true;
                    LogTransfer(transferId, partition, "source-restored");
                }
                if (prepared)
                {
                    try
                    {
                        bool found = await PostCleanup(ClusterUrl(target, "discard") + fence);
                        LogTransfer(transferId, partition, found ? "discarded" : "discard-not-found");
                    }
                    catch (Exception e) { cleanupFailures.Add(e); }
                }
                // T-0369: the switch DETACHED each of these clients from the source, which is a disconnect there
                // (vanilla ClientLeft: the player is removed, the body stays), and the restore above attached them
                // again as fresh connections. Nothing made such a connection a player again: the source thawed,
                // reported the partition with no client, and the session stayed attached, routed and served by
                // nobody (303693 / client ...023, 2026-10-02 00:05:08 until the forced reconnect at 00:11:48).
                // The source is asked what the target would have been asked, with the view the client left with,
                // BEFORE the thaw: the thaw resumes the client's replication, and an adoption needs an empty view.
                // A client the source cannot take back is ended instead, so its reconnect is a real join.
                List<ClientSession> unadopted = [];
                if (switchedWith != null && switchedSessions.Count != stranded.Count)
                {
                    List<ClientSession> returned = [.. switchedSessions.Except(stranded)];
                    using CancellationTokenSource budget = new(SourceReadoptBudget + TimeSpan.FromSeconds(returned.Count - 1));
                    foreach (ClientSession each in returned)
                    {
                        try
                        {
                            if (adoptedOnTarget.Contains(each))
                                throw new InvalidOperationException("the target had already adopted it, its captured view is stale");
                            await ReadoptOnSource(source, each, switchedWith.StateFor(each.ClientId), budget.Token);
                        }
                        catch (Exception e)
                        {
                            unadopted.Add(each);
                            Console.WriteLine($"Client {each.ClientId} could not be re-adopted on source {source} after the aborted handover "
                                + $"of partition {partition}: {e.Message}; ending its session so it rejoins");
                        }
                    }
                    if (unadopted.Count != returned.Count)
                        LogTransfer(transferId, partition, "source-readopted",
                            $" clients={string.Join(",", returned.Except(unadopted).Select(x => x.ClientId))}");
                }
                if (captured)
                {
                    try
                    {
                        bool found = await PostCleanup(ClusterUrl(source, "thaw") + fence);
                        LogTransfer(transferId, partition, found ? "source-thawed" : "source-thaw-not-found");
                    }
                    catch (Exception e) { cleanupFailures.Add(e); }
                }
                if (cleanupFailures.Count == 0)
                {
                    try
                    {
                        if (begun)
                        {
                            registry.Abort(transferId);
                            aborted = true;
                            LogTransfer(transferId, partition, "aborted");
                            if (failure is HandoverImportException { Signature: { } signature } rejected)
                                registry.RecordValidationAbort(partition, signature, rejected.Reason, rejected.Entities);
                        }
                    }
                    catch (Exception e) { cleanupFailures.Add(e); }
                }
                // #1283: kept, the claimed placeholder row would lapse unowned and unrecoverable (see the claim). Released,
                // it is reaped as stillborn (no save, no lineage) and the next pass claims the placeholder afresh. Not while
                // a begun transfer is still open: that one is reconciliation's.
                if (placeholderClaimed && (!begun || aborted))
                {
                    try
                    {
                        registry.ReleasePartition(partition, owner.Node!, owner.Epoch, owner.Generation);
                        LogTransfer(transferId, partition, "placeholder-released");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Could not release placeholder partition {partition} after its failed handover: {e.Message}");
                    }
                }

                foreach (ClientSession each in sessions)
                    DiscardServerBacklog(each);
                foreach (ClientSession each in stranded)
                    each.Client?.Disconnect();
                foreach (ClientSession each in unadopted)
                    ForceReconnect(each, "handover-abort-source-readopt-failed");
                if (cleanupFailures.Count != 0)
                    throw new AggregateException([failure, .. cleanupFailures]);
            }
            throw;
        }
        finally
        {
            if (pause != null)
            {
                pause.Release.TrySetResult();
                _handoverPauses.TryRemove(new KeyValuePair<string, HandoverPause>(transferId, pause));
            }
			// Idempotent; on the success path it has already run, before the source release.
			CompleteSwitch();
        }
    }

    /// <summary>
    /// Holds the relay locks of several sessions at once, always in client-id order so two handovers
    /// that overlap on a client can never take them in opposite orders and deadlock.
    /// </summary>
    private sealed class RelayLocks : IDisposable
    {
        private readonly List<object> _held = [];

        public static RelayLocks Take(IEnumerable<ClientSession> sessions)
        {
            RelayLocks locks = new();
            try
            {
                foreach (ClientSession session in sessions.OrderBy(x => x.ClientId))
                {
                    Monitor.Enter(session.RelayLock);
                    locks._held.Add(session.RelayLock);
                }
                return locks;
            }
            catch
            {
                locks.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            for (int i = _held.Count - 1; i >= 0; i--)
                Monitor.Exit(_held[i]);
            _held.Clear();
        }
    }

    /// <summary>
    /// T-0354b: the fenced capture. A node answers 409 when a registry request about the partition is unanswered - the
    /// claim of a layout it made a frame ago, a topology apply - and nothing is frozen yet (ClusterControlServer.Capture);
    /// that becomes a <see cref="RegistryApplyPendingRefusal"/>, which a requested move retries.
    /// </summary>
    private async Task<byte[]> FencedCapture(string url, ulong partition)
    {
        try { return await _http.GetByteArrayAsync(url); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            throw new RegistryApplyPendingRefusal($"Partition {partition} capture refused: a registry request about it is unanswered on the source", e);
        }
    }

    /// <summary>T-0354b: the waits between attempts of <see cref="WhenRegistryApplied"/> (3.1 s in all: a claim's 3 s request timeout and a frame).</summary>
    internal static readonly TimeSpan[] RegistryApplyRetryDelays =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1600)];

    /// <summary>
    /// T-0354b: a REQUESTED handover (POST /handover, a pull demand) of a partition whose source has a registry request
    /// about it unanswered. Since T-0354 the claim of a new layout's id is answered a frame or two after the layout is
    /// made, not inside the membership pass, and a move asked right after a spawn or paste (P3-HND-02) was refused with
    /// a 500. The refusal comes before anything is frozen, and the cleanup has thawed and aborted cleanly when it
    /// surfaces here, so the whole handover is asked again, at <see cref="RegistryApplyRetryDelays"/>; then it fails as
    /// <see cref="RegistryBusyException"/> (503: come back later). An automatic move is not wrapped: its backoff retries it.
    /// </summary>
    internal static async Task WhenRegistryApplied(ulong partition, Func<Task> handover, Func<TimeSpan, Task>? delay = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await handover();
                if (attempt != 0) Console.WriteLine($"Partition {partition} handover went through after {attempt} retr{(attempt == 1 ? "y" : "ies")} (registry request answered)");
                return;
            }
            catch (RegistryApplyPendingRefusal refusal)
            {
                if (attempt == RegistryApplyRetryDelays.Length)
                    throw new RegistryBusyException(refusal.Message + $"; still refused after {attempt} retries");
                Console.WriteLine($"{refusal.Message}; handover asked again in {RegistryApplyRetryDelays[attempt].TotalMilliseconds:F0} ms");
                await (delay ?? Task.Delay)(RegistryApplyRetryDelays[attempt]);
            }
        }
    }

    private async Task FencedClientlessHandover(string source, string target, IPEndPoint backend, ulong partition, bool pauseAfterCapture = false, bool allowRemoteUse = false)
    {
        GatewayRegistry registry = _registry ?? throw new InvalidOperationException("Clientless handover requires the registry");
        (NodeRegistration sourceNode, NodeRegistration targetNode) = ResolveFencedNodes(registry, source, target, backend);
        PartitionInfo sourcePartition = GetPartitions(source).Partitions.SingleOrDefault(x => x.Id == partition)
            ?? throw new InvalidOperationException($"Source did not report partition {partition}");
        // A node reports a client for every ONLINE player controlling an entity in the partition -
        // and a disconnected player's body re-binds as such on every warm boot, which made any
        // partition holding an abandoned character permanently unmovable ("not clientless",
        // partition 74's spawn-point pulls, 2026-08-11). Only a client with a live gateway session
        // needs its session moved with the partition; a resident body without one is cargo.
        if (!allowRemoteUse && (sourcePartition.RemoteUsers is { Length: > 0 } || registry.HasRemoteUse(partition)))
            throw new InvalidOperationException("remote-use");
        ulong[] connectedReported = sourcePartition.Clients.Where(_clients.ContainsKey).ToArray();
        if (connectedReported.Length != 0)
            throw new InvalidOperationException($"Partition {partition} is not clientless (connected: {string.Join(",", connectedReported)})");
        // The node report lags the registry after a client switch; a partition routed to a
        // connected client must never move without its client, whatever the report says -
        // but only for the length of that lag. This is the same rule as the scheduler's
        // route-over-report count and it expires the same way, because expiring only one
        // half converts a quiet deferral into a thrown handover and the partition stays
        // pinned either way. Past the grace the node report wins: the owning node has said
        // "no connected client on this partition" for RouteOverReportGrace, so the route is
        // stale and the partition may move. A missing entry means the disagreement has not
        // been observed by a scheduler pass yet, which is the conservative case - throw.
        ulong[] routedConnected = [.. _clients.Keys
            .Where(client => registry.GetClientPartition(client)?.Partition == partition)];
        if (routedConnected.Length != 0)
        {
            if (!RoutePinStale(partition))
                throw new InvalidOperationException($"Partition {partition} is routed to a connected client");
            Console.WriteLine($"Partition {partition} is routed to connected client(s) "
                + $"{string.Join(",", routedConnected)} but its node has reported it clientless for at least "
                + $"{RouteOverReportGrace.TotalSeconds:F0}s; moving it on the node's report");
        }
        // T-0313 (race review F7): the joining-client guard ran only on the automatic client-bearing path, so a
        // pull demand, an operator/rotator /handover or a drain could move the home or placement partition of a
        // client still joining (held, not world-ready, or inside the post-ready settle) - it then attaches where
        // its body is not, gets a fresh one, and the election despawns one of the two (T-0237 shape). Every
        // clientless move comes through here, so the guard lives here; the automatic path's backoff and the
        // callers' failure handling treat the refusal like any other deferral.
        if (JoiningClientPlacedIn(partition) is var joining && joining != 0)
            throw new InvalidOperationException($"Partition {partition} is the placement or home of client {joining}, which is still joining; clientless handover deferred");
        PartitionOwnership owner;
        try { owner = registry.GetPartitionOwner(partition); }
        catch (ArgumentException) when (registry.IsAllocatedUnclaimed(partition))
        {
            // T-0354b: the source made the layout a moment ago and its claim has not reached the registry yet.
            throw new RegistryApplyPendingRefusal($"Partition {partition} is not claimed yet (the source's claim is under way)");
        }
        // T-0373: the registry hands out its LIVE row, and a topology change releases it IN PLACE (Node = null).
        // Live 2026-10-02 07:04:35.26: 307625 was split on node-187 between this read and Begin below, Begin got
        // owner.Node == null and its node lookup threw "Value cannot be null. (Parameter 'key')". A copy keeps what
        // was checked; Begin then refuses the stale generation as "ownership is fenced".
        owner = owner with { };
        if (sourceNode.Node != owner.Node || sourceNode.Epoch != owner.Epoch)
            // w22 2026-08-21: partitions 1 and 3 hit this 27 times in 4.5 h and never recovered, and the
            // message could not distinguish a dead epoch from a different live node from a null owner -
            // see findings/partitions-1-and-3-are-permanently-unschedulable.md. Name the actual owner.
            throw new RegistryConflictException($"Partition {partition} owner does not match source endpoint '{source}'"
                + $" (source {sourceNode.Node} epoch {sourceNode.Epoch}, registry owner {owner.Node} epoch {owner.Epoch})");

        uint generation = checked((uint)owner.Generation);
        string transferId = Guid.NewGuid().ToString("D");
        string fence = $"?transferId={transferId}&partition={partition}&generation={generation}";
        bool begun = false;
        bool captured = false;
        bool prepared = false;
        bool committed = false;
        HandoverPause? pause = null;
        string? presyncId = await TryPresync(source, target, partition);
        string presyncQuery = presyncId == null ? "" : "&presync=" + presyncId;
        try
        {
            registry.Begin(transferId, partition, owner.Node, owner.Epoch, owner.Generation, targetNode.Node, targetNode.Epoch);
            begun = true;
            HandoverTelemetry.Transfer telemetry = _handoverTelemetry.Begin(transferId, partition,
                owner.Node, targetNode.Node, clients: 0, sourcePartition.Pcu, sourcePartition.MemberCount);
            LogTransfer(transferId, partition, "begun", presyncId == null ? "" : $" presync={presyncId}");

            captured = true;
            byte[] capture = await FencedCapture(ClusterUrl(source, "capture") + fence + "&clientless=true"
                + (allowRemoteUse ? "&allowRemoteUse=true" : "") + presyncQuery, partition);
            HandoverCapture handover = HandoverCapture.Parse(capture);
            if (handover.ClientStates.Count != 0)
                throw new InvalidDataException("Clientless capture included client state");
            telemetry.SectorBytes = handover.Sector.Length;
            telemetry.NetworkStateBytes = handover.NetworkState.Length;
            LogTransfer(transferId, partition, "captured", $" sectorBytes={handover.Sector.Length}");

            if (pauseAfterCapture)
            {
                pause = new HandoverPause(transferId, partition, null);
                if (!_handoverPauses.TryAdd(transferId, pause))
                    throw new InvalidOperationException("Duplicate handover pause " + transferId);
                LogTransfer(transferId, partition, "paused-after-capture");
                await pause.Release.Task;
                LogTransfer(transferId, partition, "pause-released");
            }

            using (StringContent state = new(handover.NetworkState))
            {
                prepared = true;
                TransferGrant grant = registry.IssueGrant(new("handover", transferId, partition, owner.Generation, 0,
                    sourceNode.Node, sourceNode.Epoch, targetNode.Node));
                HttpResponseMessage prepare = await PostGranted(ClusterUrl(target, "prepare-import") + fence + "&clientless=true", state, grant.Token);
                prepare.EnsureSuccessStatusCode();
            }
            LogTransfer(transferId, partition, "prepared");

            handover = await ImportWithFallback(source, target, partition, fence, "&clientless=true", handover, transferId, presyncId != null,
                () => registry.IssueGrant(new("handover", transferId, partition, owner.Generation, 0, sourceNode.Node, sourceNode.Epoch, targetNode.Node)));
            LogTransfer(transferId, partition, "imported");
            await AwaitDurableTee(source, transferId, partition, handover.Sector);

            registry.Activate(transferId);
            HandoverRecord committedHandover = registry.Commit(transferId);
            committed = true;
            LogTransfer(transferId, partition, "active");
            LogTransfer(transferId, partition, "committed");

            await PostCommittedWithRetry(ClusterUrl(target, "commit") + fence + "&committedGeneration=" + committedHandover.CommittedGeneration, "target commit");
            LogTransfer(transferId, partition, "target-committed");

            await PostCommittedWithRetry(ClusterUrl(source, "release") + fence, "source release");
            LogTransfer(transferId, partition, "released");
            registry.FinalizeReconciliation(transferId);
        }
        catch (Exception failure)
        {
            DiagnosticReports.Write("handover_failure", "Partition handover failed", failure,
                transferId, partition, "handover");
            if (!committed)
            {
                List<Exception> cleanupFailures = [];
                if (prepared)
                {
                    try
                    {
                        bool found = await PostCleanup(ClusterUrl(target, "discard") + fence);
                        LogTransfer(transferId, partition, found ? "discarded" : "discard-not-found");
                    }
                    catch (Exception e) { cleanupFailures.Add(e); }
                }
                if (captured)
                {
                    try
                    {
                        bool found = await PostCleanup(ClusterUrl(source, "thaw") + fence);
                        LogTransfer(transferId, partition, found ? "source-thawed" : "source-thaw-not-found");
                    }
                    catch (Exception e) { cleanupFailures.Add(e); }
                }
                if (cleanupFailures.Count == 0)
                {
                    try
                    {
                        if (begun)
                        {
                            registry.Abort(transferId);
                            LogTransfer(transferId, partition, "aborted");
                            if (failure is HandoverImportException { Signature: { } signature } rejected)
                                registry.RecordValidationAbort(partition, signature, rejected.Reason, rejected.Entities);
                        }
                    }
                    catch (Exception e) { cleanupFailures.Add(e); }
                }
                if (cleanupFailures.Count != 0)
                    throw new AggregateException([failure, .. cleanupFailures]);
            }
            throw;
        }
        finally
        {
            if (pause != null)
            {
                pause.Release.TrySetResult();
                _handoverPauses.TryRemove(new KeyValuePair<string, HandoverPause>(transferId, pause));
            }
        }
    }

    private bool MatchesConfiguredPair(string source, string target, IPEndPoint backend)
        => _options.SourceControl != null && _options.TargetControl != null && _options.TargetBackend != null
            && string.Equals(source.TrimEnd('/'), _options.SourceControl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.TrimEnd('/'), _options.TargetControl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && backend.Equals(_options.TargetBackend);

    /// <summary>
    /// A fenced transfer is not pinned to a configured rotation direction: any pair of distinct
    /// registered regular nodes is valid as long as the endpoints match their registrations and the
    /// target is active (a closing source is legal - that is what draining is). The
    /// partition-ownership check downstream still requires the source to own the moving partition,
    /// and the client-bearing path requires the session to sit on the resolved source backend.
    /// The configured-pair shortcut keeps the registry-less debug shape working.
    /// </summary>
    private (NodeRegistration Source, NodeRegistration Target) ResolveFencedNodes(GatewayRegistry registry, string source, string target, IPEndPoint backend)
    {
        if (MatchesConfiguredPair(source, target, backend))
            return (registry.GetNodeByEndpoint(_options.Backend.ToString()), registry.GetNodeByEndpoint(backend.ToString()));
        return ResolveNodePair(registry.Nodes(), source, target, backend)
            ?? throw new ArgumentException("Handover endpoints do not match the configured source and target nodes");
    }

    internal static (NodeRegistration Source, NodeRegistration Target)? ResolveNodePair(
        IEnumerable<NodeRegistration> nodes, string source, string target, IPEndPoint backend)
    {
        NodeRegistration? sourceNode = nodes.SingleOrDefault(node => node.Role == NodeRole.Regular
            && node.State is NodeState.Active or NodeState.Closed && ControlEndpointMatches(source, node.ControlEndpoint));
        NodeRegistration? targetNode = nodes.SingleOrDefault(node => node.Role == NodeRole.Regular
            && node.State == NodeState.Active && ControlEndpointMatches(target, node.ControlEndpoint));
        if (sourceNode == null || targetNode == null || sourceNode.Node == targetNode.Node
            || !backend.Equals(EndpointParser.Parse(targetNode.Endpoint, DirectTransportConstants.DefaultPort)))
            return null;
        return (sourceNode, targetNode);
    }

    // Registered control endpoints carry the http:// scheme while handover endpoints (and ClusterUrl)
    // use bare host:port - compare and pass them scheme-less.
    private static bool ControlEndpointMatches(string endpoint, string? control)
        => control != null && string.Equals(StripScheme(endpoint).TrimEnd('/'), StripScheme(control).TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static string StripScheme(string endpoint)
        => endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? endpoint["http://".Length..]
            : endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? endpoint["https://".Length..]
            : endpoint;

    private async Task ReconcileHandovers()
    {
        GatewayRegistry registry = _registry!;
        NodeRegistration[] nodes = registry.Nodes();
        foreach (HandoverRecord handover in registry.PendingReconciliation())
        {
            // T-0296 round 2: one record that cannot be settled this pass (a node answering an error, a registry
            // fence) must not end the pass. The automatic loop runs this FIRST, so a throw here used to repeat at the
            // same record every second and wedge the whole loop: balancing, scale-in drains, the catalogue GC and
            // every other record's reconciliation.
            try { await ReconcileHandover(registry, nodes, handover); }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Handover reconciliation of {handover.Id} (partition {handover.Partition}, {handover.State}) "
                    + $"failed this pass: {e.GetType().Name}: {SecretRedactor.Redact(e.Message)}");
            }
        }
    }

    private async Task ReconcileHandover(GatewayRegistry registry, NodeRegistration[] nodes, HandoverRecord handover)
    {
        // The record names its nodes by (id, epoch). T-0296 round 2: a side whose recorded epoch is gone (the id
        // re-registered under a new epoch, or its row was released) or whose control endpoint is unknown is probed as
        // UNREACHABLE: if it is still the live recorded incarnation that means Wait, otherwise it is dead and the other
        // side decides. Skipping such a record - as this did - left it Begun forever once that epoch was gone: joins
        // to the partition refused as "moving", no new handover of it, the source's frozen copy never thawed.
        NodeRegistration? sourceNode = nodes.FirstOrDefault(x => x.Node == handover.SourceNode && x.Epoch == handover.SourceEpoch);
        NodeRegistration? targetNode = nodes.FirstOrDefault(x => x.Node == handover.TargetNode && x.Epoch == handover.TargetEpoch);
        string? sourceControl = sourceNode?.ControlEndpoint is { } sourceEndpoint ? StripScheme(sourceEndpoint) : null;
        string? targetControl = targetNode?.ControlEndpoint is { } targetEndpoint ? StripScheme(targetEndpoint) : null;
        uint generation = checked((uint)handover.SourceGeneration);
        string fence = $"?transferId={handover.Id}&partition={handover.Partition}&generation={generation}";
        Task<TransferProbe> sourceTask = sourceControl == null ? Task.FromResult(new TransferProbe(false, null)) : ProbeTransfer(sourceControl, fence);
        Task<TransferProbe> targetTask = targetControl == null ? Task.FromResult(new TransferProbe(false, null)) : ProbeTransfer(targetControl, fence);
        await Task.WhenAll(sourceTask, targetTask);
        TransferProbe source = await sourceTask;
        TransferProbe target = await targetTask;
        bool sourceOwnerLive = registry.HasLiveOwnership(handover.Partition, handover.SourceNode, handover.SourceEpoch, handover.SourceGeneration);
        bool targetOwnerLive = registry.HasLiveOwnership(handover.Partition, handover.TargetNode, handover.TargetEpoch, handover.SourceGeneration + 1);
        // T-0296 round 2: whether the row is still at the handover's source generation, lease or not. Once it moved on
        // (the dead source's lease expiry released it, a recovery claimed it) neither Activate/Commit nor Abort can
        // pass RequireOwner: completing is impossible, and the target's copy belongs to nobody.
        bool sourceGenerationCurrent = registry.OwnedAt(handover.Partition, handover.SourceNode, handover.SourceEpoch, handover.SourceGeneration);
        ReconciliationAction action = DecideReconciliation(handover, source, target, sourceOwnerLive, targetOwnerLive,
            registry.HasLiveNode(handover.SourceNode, handover.SourceEpoch), registry.HasLiveNode(handover.TargetNode, handover.TargetEpoch),
            sourceGenerationCurrent);
        if (action == ReconciliationAction.Wait)
            return;

        if (action == ReconciliationAction.Abort)
        {
            if (target.Status is { Discarded: false })
                await PostCleanup(ClusterUrl(targetControl!, "discard") + fence);
            if (source.Status is { Released: false, Thawed: false })
                await PostCleanup(ClusterUrl(sourceControl!, "thaw") + fence);
            registry.Abort(handover.Id);
        }
        else if (action == ReconciliationAction.Complete)
        {
            HandoverRecord committed = handover.State == HandoverState.Committed
                ? handover
                : registry.Commit(registry.Activate(handover.Id).Id);
            if (target.Status != null && target.Status.CommittedGeneration != committed.CommittedGeneration)
                await PostCommittedWithRetry(ClusterUrl(targetControl!, "commit") + fence + "&committedGeneration=" + committed.CommittedGeneration, "reconciled target commit");
            if (source.Status is { Released: false })
                await PostCommittedWithRetry(ClusterUrl(sourceControl!, "release") + fence, "reconciled source release");
            registry.FinalizeReconciliation(handover.Id);
        }
        else
        {
            if (target.Status is { Discarded: false, CommittedGeneration: 0 })
                await PostCleanup(ClusterUrl(targetControl!, "discard") + fence);
            if (source.Status is { Released: false, Thawed: false })
                await PostCleanup(ClusterUrl(sourceControl!, "thaw") + fence);
            registry.AbandonReconciliation(handover.Id);
        }
        LogTransfer(handover.Id, handover.Partition, "restart-" + action.ToString().ToLowerInvariant());
    }

    internal static ReconciliationAction DecideReconciliation(HandoverRecord handover, TransferProbe source, TransferProbe target,
        bool sourceOwnerLive, bool targetOwnerLive, bool sourceNodeLive, bool targetNodeLive, bool sourceGenerationCurrent = true)
    {
        if (!source.Reachable && sourceNodeLive || !target.Reachable && targetNodeLive)
            return ReconciliationAction.Wait;
        // T-0296 round 2: an uncommitted handover whose source generation is gone can be neither completed nor aborted
        // through the registry; Recover discards the target's copy and closes the record without touching the row.
        if (handover.State != HandoverState.Committed && !sourceGenerationCurrent)
            return ReconciliationAction.Recover;
        bool targetReady = target.Status is { Imported: true, StateVerified: true, Discarded: false }
            && (target.Status.Clientless || target.Status.Adopted);
        if (handover.State == HandoverState.Committed)
            // T-0296 round 2: a live target that answers "unknown transfer" while it owns the committed generation has swept
            // its finished record (CLUSTER_TRANSFER_RECORD_SECONDS) - or, inside the 20 s finalization lease, never had it;
            // Recover released that live row, and the target discarded the partition it was simulating. Completing is safe
            // either way: a target that does not report the generation loses the row when the finalization lease ends.
            return targetReady || targetOwnerLive && target is { Reachable: true, Status: null }
                ? ReconciliationAction.Complete : ReconciliationAction.Recover;
        bool sourceViable = sourceOwnerLive || source.Status is { Captured: true, Released: false };
        return sourceViable ? ReconciliationAction.Abort
            : targetReady ? ReconciliationAction.Complete
            : ReconciliationAction.Recover;
    }

    private async Task<TransferProbe> ProbeTransfer(string endpoint, string fence)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(ClusterUrl(endpoint, "status") + fence);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(true, null);
            response.EnsureSuccessStatusCode();
            return new(true, JsonSerializer.Deserialize<TransferStatus>(await response.Content.ReadAsStringAsync())
                ?? throw new InvalidDataException("Empty transfer status from " + endpoint));
        }
        catch (HttpRequestException) { return new(false, null); }
        catch (TaskCanceledException) { return new(false, null); }
    }

    /// <summary>T-0369: how many times in a row one node control request may fail in transport before it fails the handover.</summary>
    internal const int TransportAttempts = 20;

    /// <summary>T-0369: how long an aborted handover waits for the source to take its clients back (plus 1 s per extra client).</summary>
    internal static TimeSpan SourceReadoptBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// T-0369: one node control request that survives a transport failure - a connection that failed or broke
    /// (HttpRequestException) or the HTTP client's own per-request timeout, which is not <paramref name="budget"/>.
    /// It is asked again every 100 ms, at most <see cref="TransportAttempts"/> times in a row, so a node that is
    /// really gone still fails the caller in about two seconds instead of holding a frozen partition for the whole
    /// budget. Only for requests the node answers the same way twice (a poll, a read, a fenced adopt).
    /// </summary>
    private static async Task<HttpResponseMessage> SendThroughTransportFailures(Func<CancellationToken, Task<HttpResponseMessage>> send,
        string operation, CancellationToken budget = default)
    {
        for (int failures = 1; ; failures++)
        {
            try { return await send(budget); }
            // Without a budget the per-request timeout (100 s) is not asked again: twenty of those is no retry.
            catch (Exception e) when (failures < TransportAttempts && !budget.IsCancellationRequested
                && (e is HttpRequestException || e is OperationCanceledException && budget.CanBeCanceled))
            {
                Console.WriteLine($"{operation} failed in transport ({e.Message}); asking again ({failures}/{TransportAttempts - 1})");
            }
            await Task.Delay(100, budget);
        }
    }

    /// <summary>
    /// T-0369: makes the client of an aborted handover a player on the SOURCE again. The node routes are the
    /// target's, unfenced: adopt-ready (polled: the node answers an error until the re-attached connection has
    /// landed, then creates the player for the body that never left) and adopt (rebuilds the replication view the
    /// client still holds and wires its controlled entity). An unfenced adopt is NOT idempotent, so it is sent once.
    /// </summary>
    private async Task ReadoptOnSource(string source, ClientSession session, string state, CancellationToken budget)
    {
        string query = "?client=" + session.ClientId + "&state=" + Uri.EscapeDataString(state);
        try
        {
            while (true)
            {
                using HttpResponseMessage ready = await SendThroughTransportFailures(
                    token => _http.PostAsync(ClusterUrl(source, "adopt-ready") + query, null, token),
                    $"adopt-ready of client {session.ClientId} on source {source}", budget);
                if (ready.IsSuccessStatusCode) break;
                await Task.Delay(100, budget);
            }
            using HttpResponseMessage adopt = await _http.PostAsync(ClusterUrl(source, "adopt") + query, null, budget);
            if (!adopt.IsSuccessStatusCode)
                throw new InvalidOperationException($"source adopt answered HTTP {(int)adopt.StatusCode}: "
                    + HandoverImportException.FirstLine(await adopt.Content.ReadAsStringAsync()));
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            throw new TimeoutException("the source did not take the client back in time");
        }
    }

    private async Task<bool> PostCleanup(string url)
    {
        using HttpResponseMessage response = await _http.PostAsync(url, null);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    private async Task<HttpResponseMessage> PostGranted(string url, HttpContent content, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new("Bearer", token);
        return await _http.SendAsync(request);
    }

    internal enum ReconciliationAction { Wait, Abort, Complete, Recover }
    internal sealed record TransferProbe(bool Reachable, TransferStatus? Status);
    internal sealed record TransferStatus(
        [property: JsonPropertyName("captured")] bool Captured,
        [property: JsonPropertyName("imported")] bool Imported,
        [property: JsonPropertyName("stateVerified")] bool StateVerified,
        [property: JsonPropertyName("adopted")] bool Adopted,
        [property: JsonPropertyName("clientless")] bool Clientless,
        [property: JsonPropertyName("committedGeneration")] long CommittedGeneration,
        [property: JsonPropertyName("released")] bool Released,
        [property: JsonPropertyName("thawed")] bool Thawed,
        [property: JsonPropertyName("discarded")] bool Discarded);

    /// <summary>
    /// HandoverImprovements.md §4 tee.async: with the async tee the source returns its capture before the
    /// durable copy is catalogued, so durability is checked here, before activation, instead of inside
    /// the capture frame. A source running the synchronous tee (or an older build) answers known=false
    /// and the wait is one round trip.
    /// </summary>
    private async Task AwaitDurableTee(string source, string transferId, ulong partition, byte[]? sector)
    {
        string url = ClusterUrl(source, "tee-status") + "?transferId=" + transferId;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        int transportFailures = 0;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url);
            }
            catch (HttpRequestException error) when (TeeCatalogued(partition, sector) is SaveId tee)
            {
                // T-0161: the source died after its tee was catalogued. The registry's copy is the
                // durability the tee-status answer would have confirmed, so the target may commit
                // without the source instead of discarding the move and waiting for lease recovery.
                LogTransfer(transferId, partition, "tee-durable-from-registry",
                    $" save={tee.Generation}/{tee.Sequence} sourceError={error.Message}");
                return;
            }
            catch (HttpRequestException error) when (++transportFailures < TransportAttempts && DateTime.UtcNow < deadline)
            {
                // T-0369: a read, asked again. Here every client is already adopted on the target, so letting one
                // failed GET through aborted the handover at its most expensive point.
                Console.WriteLine($"tee-status of partition {partition} on {source} failed in transport ({error.Message}); "
                    + $"asking again ({transportFailures}/{TransportAttempts - 1})");
                await Task.Delay(100);
                continue;
            }
            using var _ = response;
            if (response.StatusCode == HttpStatusCode.NotFound)
                return;  // pre-§4 node without the route
            response.EnsureSuccessStatusCode();
            using JsonDocument status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement root = status.RootElement;
            if (!root.GetProperty("known").GetBoolean())
                return;
            if (root.GetProperty("completed").GetBoolean())
            {
                if (root.GetProperty("succeeded").GetBoolean())
                {
                    LogTransfer(transferId, partition, "tee-durable");
                    return;
                }
                throw new InvalidOperationException("Handover tee was not durably catalogued: "
                    + (root.TryGetProperty("error", out JsonElement error) ? error.ToString() : "save failed"));
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Handover tee of partition " + partition + " not durable within 30s");
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// The handover tee of this capture, if the registry has catalogued it: the captured sector is the
    /// tee file itself (an MPS1 save whose header names partition, generation and sequence), so its
    /// save id is known without asking the source. Null for any other sector shape.
    /// </summary>
    private SaveId? TeeCatalogued(ulong partition, byte[]? sector)
    {
        if (_registry == null || sector == null || sector.Length < 24
            || sector[0] != (byte)'M' || sector[1] != (byte)'P' || sector[2] != (byte)'S' || sector[3] != (byte)'1')
            return null;
        ulong id = BitConverter.ToUInt64(sector, 8);
        uint generation = BitConverter.ToUInt32(sector, 16);
        uint sequence = BitConverter.ToUInt32(sector, 20);
        if (id != partition) return null;
        SaveId tee = new(partition, generation, sequence);
        return _registry.Saves().Any(save => save.SaveId == tee && save.Kind == SaveKind.HandoverTee && !save.PendingDelete)
            ? tee : null;
    }

    private async Task PostCommittedWithRetry(string url, string operation)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Exception? last = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using HttpResponseMessage response = await _http.PostAsync(url, null, timeout.Token);
                if (response.IsSuccessStatusCode)
                    return;
                if ((int)response.StatusCode < 500 && response.StatusCode is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests)
                    throw new InvalidOperationException($"{operation} returned non-retryable HTTP {(int)response.StatusCode}");
                last = new HttpRequestException($"{operation} returned HTTP {(int)response.StatusCode}");
            }
            catch (OperationCanceledException e) when (!timeout.IsCancellationRequested) { last = e; }
            catch (OperationCanceledException e) { last = e; break; }
            catch (HttpRequestException e) { last = e; }
            try { await Task.Delay(250, timeout.Token); }
            catch (OperationCanceledException) { break; }
        }
        throw new TimeoutException($"Timed out retrying {operation}", last);
    }

    private static bool HasHealthyRoute(ClientSession session, IPEndPoint endpoint)
    {
        lock (session.RelayLock)
            return endpoint.Equals(session.BackendEndpoint) && RouteHealthy(session);
    }

    private static bool RouteHealthy(ClientSession session) => ReferenceEquals(session.AttachedLink, session.Link)
        && session.Link?.IsConnected == true;

    /// <summary>
    /// Relays the client's original CLIENT_CONNECTED packet to the backend it is now linked to.
    ///
    /// This used to end in an unconditional `await Task.Delay(100)`, awaiting no acknowledgement of
    /// any kind. Sequentially, per client, inside the frozen window: at a p95 crew of 8 that was
    /// ~800 ms of a ~3900 ms player-visible freeze spent asleep, and it was the largest single
    /// measured cost on the handover path.
    ///
    /// It was also redundant. Two separate things could have needed waiting for, and BOTH are
    /// already covered before this method is reached:
    ///
    ///   1. LINK ATTACHMENT. Every one of this method's five callers invokes SwitchBackend first,
    ///      and SwitchBackend already performs the Attach/AttachAck handshake SYNCHRONOUSLY - it
    ///      loops on Attach(), waits on session.AttachCompleted against a 10 s deadline, and only
    ///      returns once AttachedLink is the target. So the node has already acknowledged the link
    ///      by the time we get here, and RouteHealthy is satisfied. That is what the rollback
    ///      callers (respawn-rollback, handover-rollback) depend on, and it is unaffected.
    ///   2. APPLICATION READINESS - the node's game server actually processing the relayed packet.
    ///      The handover paths gate on the per-client `adopt-ready` poll, which asks the target
    ///      directly and retries against a real budget. The respawn path gates on its own deadline
    ///      and RespawnExecutionResult poll. Both are authoritative where a fixed sleep is a guess.
    ///
    /// So the delay was not the gate for either concern; the gates already existed on both sides of
    /// it. Deleted rather than shortened - a smaller sleep would be a smaller guess, not a fix.
    /// Requires the arming reorder (afc8d72): with readiness armed before the relay, a fast reply
    /// can no longer land in the window this delay used to cover.
    /// </summary>
    private void AttachBackend(ClientSession session)
    {
        if (session.ClientConnectedPacket == null || session.Link == null)
            throw new InvalidOperationException("Cannot attach backend without the original CLIENT_CONNECTED packet");
        if (_relay != null)
        {
            // gateway-rs captured the same packet and replays it to the session's current link.
            _relay.Send(RelayIpcType.ReplayConnect, 0, new { clientId = session.ClientId, session = session.RelaySession });
            return;
        }
        SendToNode(session.Link, session.ClientConnectedPacket);
    }

    private void LogTransfer(string transferId, ulong partition, string state, string extra = "")
    {
        long? atMs = _handoverTelemetry.Stamp(transferId, state);
        if (state.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || state.Contains("abort", StringComparison.OrdinalIgnoreCase)
            || state.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || state.Contains("recover", StringComparison.OrdinalIgnoreCase))
            DiagnosticReports.Write("handover_event", state + extra, transferId: transferId,
                partition: partition, phase: state);
        // T-0251: a committed balance migration is an arrival (the pin names the partition until it expires).
        bool arrival = state == "committed" && MigrationPinned(partition);
        if (arrival) _registry?.NoteArrival(partition);
        Console.WriteLine($"handover transferId={transferId} partition={partition} state={state}"
            + (atMs is { } ms ? $" atMs={ms}" : "") + extra + (arrival ? " arrival=noted" : ""));
    }

    // ------------------------------------------------------------------ HandoverDelta.md: presync
    //
    // Before the freeze, the target pulls the partition from the source and stages dormant copies;
    // the capture then carries only a patch. Off by default (presync.enabled in the handover-config
    // document); a failure here is logged and the handover proceeds as a full one - presync is an
    // optimisation, never a gate. A partition whose delta import aborted on the gate is kept full
    // for PresyncQuarantine so a patch that cannot reproduce the state does not loop.
    private readonly ConcurrentDictionary<ulong, DateTime> _presyncQuarantine = new();
    private static readonly TimeSpan PresyncQuarantine = TimeSpan.FromMinutes(10);

    private bool PresyncEnabled()
    {
        try
        {
            return _registry != null && _registry.HandoverConfig().Values.TryGetValue("presync.enabled", out string? value)
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private async Task<string?> TryPresync(string source, string target, ulong partition)
    {
        if (!PresyncEnabled()) return null;
        if (_presyncQuarantine.TryGetValue(partition, out DateTime until))
        {
            if (DateTime.UtcNow < until) return null;
            _presyncQuarantine.TryRemove(partition, out _);
        }
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await _http.PostAsync(ClusterUrl(target, "presync") + $"?partition={partition}&source={Uri.EscapeDataString(source)}", null);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"presync partition={partition} target={target} failed HTTP {(int)response.StatusCode}: {HandoverImportException.FirstLine(body)} ({watch.ElapsedMilliseconds} ms); full handover");
                return null;
            }
            using JsonDocument document = JsonDocument.Parse(body);
            string id = document.RootElement.GetProperty("presyncId").GetString()!;
            Console.WriteLine($"presync partition={partition} {source}->{target} presync={id} entities={document.RootElement.GetProperty("entities").GetInt32()}"
                + $" staged={document.RootElement.GetProperty("staged").GetInt32()} ms={watch.ElapsedMilliseconds}");
            return id;
        }
        catch (Exception e)
        {
            Console.WriteLine($"presync partition={partition} target={target} failed: {e.Message} ({watch.ElapsedMilliseconds} ms); full handover");
            return null;
        }
    }

    /// <summary>
    /// POST /import with the captured sector; on HTTP 424 (the target lost its presync stage) the
    /// capture is re-fetched in full from the source and imported again. Returns the sector that was
    /// imported (the full one after a fallback, so telemetry and retries see what the target did).
    /// </summary>
    private async Task<HandoverCapture> ImportWithFallback(string source, string target, ulong partition, string fence, string captureQuery,
        HandoverCapture handover, string transferId, bool delta, Func<TransferGrant> grant)
    {
        using (ByteArrayContent content = new(handover.Sector))
        {
            HttpResponseMessage import = await PostGranted(ClusterUrl(target, "import") + fence, content, grant().Token);
            if (import.IsSuccessStatusCode) return handover;
            string body = await import.Content.ReadAsStringAsync();
            if ((int)import.StatusCode == 424)
                LogTransfer(transferId, partition, "presync-missing", $" {HandoverImportException.FirstLine(body)}");
            else if (delta)
            {
                // A delta import that fails for any other reason (an applier left the dormant entity
                // in a state the activate frame rejects - w22 20:42, MyInventoryAggregate) must not
                // be retried as a delta: the gateway's automatic retry would re-freeze the source
                // every 2 s for as long as the partition stays movable. Quarantine presync for this
                // partition and finish THIS transfer with a full import.
                _presyncQuarantine[partition] = DateTime.UtcNow + PresyncQuarantine;
                LogTransfer(transferId, partition, "delta-failed", $" HTTP {(int)import.StatusCode} {HandoverImportException.FirstLine(body)}; presync quarantined {PresyncQuarantine.TotalMinutes:0} min");
            }
            else
            {
                HandoverImportException failure = new(partition, (int)import.StatusCode, body);
                if (failure.Signature != null) _presyncQuarantine[partition] = DateTime.UtcNow + PresyncQuarantine;
                throw failure;
            }
        }
        byte[] capture = await _http.GetByteArrayAsync(ClusterUrl(source, "capture") + fence + captureQuery + "&full=true");
        HandoverCapture full = HandoverCapture.Parse(capture);
        LogTransfer(transferId, partition, "recaptured-full", $" sectorBytes={full.Sector.Length}");
        using (ByteArrayContent content = new(full.Sector))
        {
            HttpResponseMessage import = await PostGranted(ClusterUrl(target, "import") + fence, content, grant().Token);
            if (!import.IsSuccessStatusCode)
                throw new HandoverImportException(partition, (int)import.StatusCode, await import.Content.ReadAsStringAsync());
        }
        return full;
    }

    private static string ClusterUrl(string endpoint, string action) => $"http://{endpoint.TrimEnd('/')}/cluster/{action}";

    private void Resume()
    {
		lock (_pauseLock)
        {
			_paused = false;
			foreach (ClientSession session in _clients.Values)
			{
				if (PlayerAdmissionRequired(session) && session.PlayerReady == null) continue;
				NodeLink link = session.Link ?? GetNodeLink(session.BackendEndpoint);
				Attach(session, link);
				FlushClientBacklog(session);
			}
        }
    }

    private static bool IsControlVerb(HttpListenerContext context) =>
        context.Request.HttpMethod == "GET" || context.Request.HttpMethod == "POST";

    private static bool IsAdminRoute(string path) => !path.StartsWith("/registry/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/status", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/node-plan", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/scale", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/executors", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/shutdown", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/checkpoint", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/leases/expire", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/snapshots", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/retention", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/registry/handover-blocked/clear", StringComparison.OrdinalIgnoreCase)
        // T-0340 step 7: the host agents' file move re-keys the catalogue; the admin token only, never a node's join token.
        || path.StartsWith("/registry/host-store", StringComparison.OrdinalIgnoreCase);

    /// <summary>T-0237 save stamps, "entity:version,...". Absent = a node build without stamps (null); present but
    /// empty = a stamped save with no characters in it.</summary>
    internal static Dictionary<long, long>? ParseHomeStamps(string? value)
    {
        if (value == null) return null;
        var stamps = new Dictionary<long, long>();
        foreach (string pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out long entity) || !long.TryParse(parts[1], out long version) || version <= 0)
                throw new ArgumentException($"Malformed home stamp '{pair}'");
            stamps[entity] = version;
        }
        return stamps;
    }

    private static string Required(HttpListenerContext context, string key) =>
        context.Request.QueryString[key] ?? throw new ArgumentException("Missing query parameter '" + key + "'");

    private static T ReadJson<T>(HttpListenerContext context) =>
        JsonSerializer.Deserialize<T>(context.Request.InputStream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        })
        ?? throw new ArgumentException("Request body is required");

    private static void WriteText(HttpListenerContext context, int status, string text)
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes(SecretRedactor.Redact(text));
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.Close();
    }

    /// <summary>
    /// Streams the JSON straight to the response, redacting each string as it is written. It used to build the
    /// whole body as one byte[], then redact it via a UTF-16 string and back: ~6 large-object copies of every
    /// response, and the registry answers several 40-125 MB documents (/registry/partitions, /diag?depth=cluster,
    /// /registry/lineage) that the rotator polls every 2.5 s during a drain - the Registry's 4-4.6 GB peaks at
    /// every rotation (w32 2026-09-25: 20-100 MB/s allocated, LOH 0.9-1.2 GB between collections). Secrets are
    /// whole configured values inside a string or a key, so per-string redaction redacts the same text.
    /// </summary>
    internal static void WriteJson(HttpListenerContext context, int status, object value, bool redact = true)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        try
        {
            // Small bodies (nearly every call) go out with a Content-Length in one write; only a body that
            // outgrows the buffer switches to chunked streaming. Chunking EVERY response (4fb59f6) made each
            // request on a kept-alive connection wait out Nagle + delayed ACK, ~45 ms: a node's game thread
            // reports each online player's character home synchronously every 5 s, so 16-18 players cost a
            // 700-900 ms hitch every 5 s on every node (w32 2026-09-25 06:07 on, sim down to 0.32x).
            using var body = new SpillToChunkedStream(context.Response);
            JsonSerializer.Serialize(body, value, redact ? RedactingJson : JsonSerializerOptions.Default);
            body.Complete();
            context.Response.Close();
        }
        catch
        {
            context.Response.Abort();   // a half-written body must not read as a complete document
            throw;
        }
    }

    /// <summary>
    /// Buffers a response body up to <see cref="BufferBytes"/> (below the 85,000-byte LOH threshold); <see cref="Complete"/>
    /// sends a buffered body with a Content-Length in a single write. Past the buffer it switches the response to
    /// chunked and streams the rest, so a 40-125 MB report never materialises whole (4fb59f6).
    /// </summary>
    internal sealed class SpillToChunkedStream(HttpListenerResponse response) : Stream
    {
        internal const int BufferBytes = 64 * 1024;
        private readonly byte[] _buffer = new byte[BufferBytes];
        private int _length;
        private bool _spilled;

        public bool Spilled => _spilled;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { if (_spilled) response.OutputStream.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            if (!_spilled && _length + data.Length <= BufferBytes)
            {
                data.CopyTo(_buffer.AsSpan(_length));
                _length += data.Length;
                return;
            }
            if (!_spilled)
            {
                _spilled = true;
                response.SendChunked = true;
                response.OutputStream.Write(_buffer, 0, _length);
                _length = 0;
            }
            response.OutputStream.Write(data);
        }

        /// <summary>Sends a still-buffered body with its Content-Length; a spilled one is already streaming.</summary>
        public void Complete()
        {
            if (_spilled) return;
            response.ContentLength64 = _length;
            response.OutputStream.Write(_buffer, 0, _length);
        }
    }

    internal static readonly JsonSerializerOptions RedactingJson = new() { Converters = { new RedactingStringConverter(), new RedactingElementConverter() } };

    private sealed class RedactingStringConverter : System.Text.Json.Serialization.JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(SecretRedactor.Redact(value));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(SecretRedactor.Redact(value));
        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString()!;
    }

    private void SendToBackend(ClientSession session, GatewayPacket packet)
    {
        lock (session.RelayLock)
        {
            NodeLink? link = session.Link;
			if (!RelayPaused && !session.SwitchingBackend && AttachedBindingRoutable(session) && ReferenceEquals(session.AttachedLink, link)
                && link?.IsConnected == true)
                SendClientPacketToNode(session, link, packet);
            else
            {
                // A deliberate hold (pause or backend switch) is exempt from the age bound;
                // the byte cap still catches a wedged switch. Genuine unroutability ages.
                if (RelayPaused || session.SwitchingBackend)
                    session.ClientBacklog.RefreshAge();
                if (!session.ClientBacklog.TryEnqueue(packet))
                    ForceReconnect(session, "client-backlog-overflow (" + session.ClientBacklog.Describe() + ")");
            }
        }
    }

    private void SendToClient(ClientSession session, GatewayPacket packet)
    {
        ClientChannel? client = session.Client;
        if (client?.Connected == true)
        {
            client.Send(packet);
            int reliableBacklog = client.ReliableBacklogPackets;
            if (reliableBacklog >= 4096)
            {
                session.ReliableBacklogStarted ??= DateTimeOffset.UtcNow;
                session.WithheldCreditBytes += packet.Payload.Length + 10;
            }
            else
            {
                if (session.ReliableBacklogStarted == null)
                {
                    int credit = session.WithheldCreditBytes + packet.Payload.Length + 10;
                    session.WithheldCreditBytes = 0;
                    if (session.Link != null) SendCredit(session.Link, session.ClientId, credit);
                }
                else
                    session.WithheldCreditBytes += packet.Payload.Length + 10;
            }
        }
    }

    private void ReplenishClientCredit(ClientSession session)
    {
        lock (session.RelayLock)
        {
            ClientChannel? client = session.Client;
            if (session.ReliableBacklogStarted == null || client?.Connected != true
                || client.ReliableBacklogPackets >= 3072)
                return;
            session.ReliableBacklogStarted = null;
            int credit = session.WithheldCreditBytes;
            session.WithheldCreditBytes = 0;
            if (credit != 0 && session.Link != null)
                SendCredit(session.Link, session.ClientId, credit);
        }
    }

    private bool ShouldRelayServerPacket(ClientSession session, GatewayPacket packet)
    {
        if (DirectTransportCodec.TryGetGameMessageId(packet, out byte messageId))
        {
            if (session.SuppressJoinMessages && session.TypeTableSent && messageId is 2 or 12 or 13 or 20)
            {
                Console.Error.WriteLine($"Dropped duplicate join message {messageId} for client {session.ClientId}");
                return false;
            }
            if (messageId == 6)
            {
                if (session.TypeTableSent)
                {
                    Console.Error.WriteLine($"Dropped duplicate SERVER_DATA for client {session.ClientId}");
                    session.DuplicateServerData++;
                    Interlocked.Increment(ref _duplicateServerData);
                    return false;
                }
                session.TypeTableSent = true;
            }
        }
        return true;
    }

    private bool Authorized(HttpListenerContext context)
    {
        string supplied = context.Request.Headers["Authorization"] ?? string.Empty;
        const string prefix = "Bearer ";
        if (!supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || _options.AdminToken == null)
            return false;
        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(_options.AdminToken));
        byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied[prefix.Length..]));
        // T-0321: after a gateway-only restart that brought a re-minted admin token, the start's persisted token stays
        // the gateway's own (the running nodes hold it) and the new one is accepted as well (StartCredentials).
        return CryptographicOperations.FixedTimeEquals(expected, actual)
            | ClusterProtocol.ClusterNet.TokenMatches(supplied[prefix.Length..], _options.ExtraAdminTokens ?? []);
    }

    private bool NodeAuthorized(HttpListenerContext context, string path)
    {
        // T-0321: a node's control-liveness rescue (ControlReassertService) is the one non-registry route a node calls.
        // Only the punitive-free reconnect form: an admin kick still needs the admin token.
        if (path.Equals("/admission/kick", StringComparison.OrdinalIgnoreCase)
            && string.Equals(context.Request.QueryString["reason"], "reconnect", StringComparison.OrdinalIgnoreCase))
            return JoinTokenPresented(context);
        if (!path.StartsWith("/registry/", StringComparison.OrdinalIgnoreCase)
            || IsAdminRoute(path)
            || path.Equals("/registry/nodes", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/registry/grants/validate", StringComparison.OrdinalIgnoreCase))
            return path.Equals("/registry/nodes", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/registry/grants/validate", StringComparison.OrdinalIgnoreCase);
        string supplied = context.Request.Headers["Authorization"] ?? string.Empty;
        const string prefix = "Bearer ";
        return supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && _options.Admission?.AcceptsToken(supplied[prefix.Length..]) == true;
    }

    /// <summary>
    /// Orchestration.md §11.1: the one front door for runtime state. Registry-owned facts come straight
    /// from the registry (transactional, class E in TestPlan §1.3); node-local facts are fanned out to the
    /// nodes' own <c>/cluster/diag</c>, each of which captures its section on the game thread at a frame
    /// boundary. Sections carry their own <c>capturedAt</c>/<c>simFrameCounter</c> so the bench can tell a
    /// real disagreement from capture skew rather than being promised a cluster-wide instant.
    /// </summary>
    /// <remarks>
    /// A node that is hung or dead is reported as stale with its heartbeat age, never fabricated - the
    /// P1-INF-01 assertion that matters most, because a silently-omitted node reads as "no partitions
    /// there" to every invariant built on this snapshot.
    /// </remarks>
    private object DiagSnapshot(HttpListenerContext context)
    {
        string depth = context.Request.QueryString["depth"] ?? "partitions";
        string? nodeFilter = context.Request.QueryString["node"];
        // registry=summary: the registry section without its history - live partition rows plus counts, and
        // the EntityId issue log as a summary - for pollers such as cluster-status (1 Hz in the harness), which
        // otherwise pulled 34.7 MB per call, 37.5 MB of it retired partition rows (w32, 2026-09-23).
        bool summary = string.Equals(context.Request.QueryString["registry"], "summary", StringComparison.OrdinalIgnoreCase);
        string query = context.Request.Url?.Query ?? string.Empty;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeRegistration[] nodes = _registry?.Nodes() ?? Array.Empty<NodeRegistration>();
        // Publish the same effective load the balancer derives from its /partitions reports.
        // The node diag's health.simSpeed is a separate capture and must not recalculate it.
        var balanceLoads = new Dictionary<string, (long Effective, long Partitions)>();
        if (_lastPartitionReports is { } loadSnapshot
            && DateTime.UtcNow - loadSnapshot.At <= TimeSpan.FromSeconds(15)
            && loadSnapshot.Reports.Length != 0
            && loadSnapshot.Reports.All(x => x.Report.SimLoadSupported))
            foreach (var report in loadSnapshot.Reports)
                balanceLoads[report.Node] = (EffectiveNodeLoad(report.Report, true),
                    report.Report.Partitions.Sum(p => p.SimLoadUs));
        var sections = new List<object>();
        foreach (NodeRegistration node in nodes)
        {
            if (nodeFilter != null && !node.Node.Equals(nodeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            // The lease is refreshed by the heartbeat, so age since the last one is the TTL minus what is
            // left on the lease. Reporting (now - LeaseExpires) instead reads 0 for every healthy node,
            // which is the one value a staleness check must never see from a node that has gone quiet.
            double heartbeatAge = _options.LeaseTtl.TotalMilliseconds - (node.LeaseExpires - now).TotalMilliseconds;
            object? section = null;
            string? error = null;
            // T-0320: a gone node (Empty, Dead) is listed but never queried. Its id is never reused, but its port slot is:
            // the row's control endpoint may serve ANOTHER node by now, whose section would appear twice, once under the
            // stopped node's name (bench run 3). Its partitions are elsewhere or offline; "stale" says the section is absent.
            if (node.State is NodeState.Empty or NodeState.Dead && depth != "cluster")
                error = $"node is {node.State}; not queried (its endpoint may serve another node now)";
            else if (node.ControlEndpoint != null && depth != "cluster")
                try
                {
                    using HttpRequestMessage request = new(HttpMethod.Get,
                        ClusterUrl(node.ControlEndpoint.Replace("http://", string.Empty), "diag" + query));
                    // Headers only: the body is read straight into RelayedJson's chunks, not buffered whole by HttpClient
                    // first (its growing buffer was another large-object allocation per node per call).
                    using HttpResponseMessage response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    // Relayed as the node's bytes, not parsed into a JsonDocument (LOH), and redacted per string on
                    // the way out - see RelayedJson.
                    section = RelayedJson.Read(response.Content.ReadAsStream());
                }
                catch (Exception e) { error = e.Message; }
            bool hasBalanceLoad = balanceLoads.TryGetValue(node.Node, out var balanceLoad);
            sections.Add(new
            {
                nodeId = node.Node, slot = node.Node, role = node.Role.ToString(), state = node.State.ToString(),
                endpoint = node.Endpoint, controlEndpoint = node.ControlEndpoint, epoch = node.Epoch,
                leaseExpires = node.LeaseExpires,
                lastHeartbeatAgeMs = Math.Max(0, heartbeatAge), quarantined = node.Quarantined,
                balanceLoadUs = hasBalanceLoad ? balanceLoad.Effective : (long?)null,
                balancePartitionLoadUs = hasBalanceLoad ? balanceLoad.Partitions : (long?)null,
                // Explicitly stale rather than absent: an unreachable node's partitions are unknown, not gone.
                stale = section == null && depth != "cluster", sectionError = error, section
            });
        }
        return new
        {
            schemaVersion = 1,
            capturedAt = now,
            depth,
            registry = _registry == null ? null : new
            {
                status = _registry.Status(),
                // PartitionsReport, not Partitions: the leaner shape omits Retired, and every
                // consumer reading this report has to tell a live row from a retired husk that
                // still names its old Node. bin/node-lifecycle-audit.py could not, so it billed
                // every retired row as a stuck partition for the rest of the run (w19 23:38,
                // partitions 2711 and 2729). HandoverBlocked comes along and is null on all but
                // a handful of rows; IsRetired is an O(1) index lookup since 3bac425.
                partitions = _registry.PartitionsReport(summary),
                partitionCounts = summary ? _registry.PartitionCounts() : null,
                handovers = _registry.Incomplete(),
                handoverBlocked = _registry.HandoverBlocks(),
                // T-0296: runtime invariant audit (duplicate copies, abandoned partitions), Docs/ConcurrencyReview.md 8.
                invariants = _registry.InvariantAudit(),
                // T-0292: partitions the latest node reports mark fast, and what that suppressed.
                fastPartitions = FastPartitionsNow(),
                fastSuppressed = FastPartitionCounters.Snapshot(),
                worldAuthority = _registry.WorldAuthority(),
                // EntityId ranges (Docs/EntityIdRanges.md): every range ever issued, with the node
                // incarnation that took it, and the index the next request gets. cluster-status pairs
                // these with the node sections' entityIdRange to show each node's current segment.
                entityIdRanges = summary ? null : _registry.EntityIdRanges(),
                entityIdRangeSummary = summary ? _registry.EntityIdRangeSummary() : null,
                nextEntityIdRange = _registry.NextEntityIdRange(),
                // T-0321: refused credentials and peer-rejection reports (fingerprints only), newest last.
                credentialRejections = CredentialRejectionsDiag(),
                // T-0321 P3: live partitions whose newest save has holders on one host only (gate: 0 before a host cut).
                replicaHosts = _registry.ReplicaHostSpread(),
                clientPartitions = _clients.Keys.OrderBy(client => client)
                    .Select(client => new { client, location = _registry.GetClientPartition(client) }).ToArray()
            },
            clients = _clients.Values.OrderBy(session => session.ClientId).Select(session => new
            {
                clientId = session.ClientId, transport = session.Client?.Frontend ?? "direct",
                routedNodeId = GatewayRegistry.LiveFirst(nodes.Where(node => node.Endpoint == session.BackendEndpoint.ToString()))?.Node,
                backend = session.BackendEndpoint.ToString(), switching = session.SwitchingBackend,
                connectedAt = session.ConnectedAt, disconnectReason = session.DisconnectReason
            }).ToArray(),
            nodes = sections.ToArray()
        };
    }

    /// <summary>
    /// Orchestration.md §11.2: run a scheduled maintenance task now. <c>balance</c> and the registry-owned
    /// tasks run here; everything else is forwarded to the node that owns the work, unchanged.
    /// </summary>
    private object RunTrigger(HttpListenerContext context, string task)
    {
        string? nodeFilter = context.Request.QueryString["node"];
        string? partition = context.Request.QueryString["partition"];
        switch (task)
        {
            case "balance":
            {
                // The balancer already runs a round a second in AutomaticHandoverLoop; a trigger releases
                // the cooldown that would otherwise make the next round a no-op and reports the round's
                // outcome. --unthrottled additionally lifts the per-partition dwell, which is refused
                // outside the --direct frontend (Security.md test-mode admission).
                bool unthrottled = context.Request.QueryString["unthrottled"] == "true";
                if (unthrottled && !_options.DirectFrontend)
                    throw new ArgumentException("--unthrottled requires --direct");
                DateTime before = _lastBalanceMove;
                _lastBalanceMove = DateTime.MinValue;
                if (unthrottled) lock (_lastHandover) _lastHandover.Clear();
                return new { task, released = true, previousMoveAt = before == DateTime.MinValue ? (DateTime?)null : before, unthrottled };
            }
            case "rotation-check":
                return new { task, nodes = _registry?.Nodes().Select(node => new { node.Node, state = node.State.ToString(), node.LeaseExpires }).ToArray() };
            case "catalog-gc":
                return new { task, deletions = _registry?.PlanRetention() };
            default:
            {
                var results = new List<object>();
                foreach (NodeRegistration node in _registry?.Nodes() ?? Array.Empty<NodeRegistration>())
                {
                    // T-0341: not the rows of stopped nodes - a live node on a reused port slot was triggered once per row.
                    if (node.ControlEndpoint == null || node.State is NodeState.Empty or NodeState.Dead) continue;
                    if (nodeFilter != null && !node.Node.Equals(nodeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    string url = ClusterUrl(node.ControlEndpoint.Replace("http://", string.Empty), "trigger/" + task)
                        + (partition == null ? string.Empty : "?partition=" + partition);
                    try
                    {
                        using HttpResponseMessage response = _http.Send(new HttpRequestMessage(HttpMethod.Post, url), HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                        results.Add(new { node = node.Node, result = RelayedJson.Read(response.Content.ReadAsStream()) });
                    }
                    catch (Exception e) { results.Add(new { node = node.Node, error = e.Message }); }
                }
                return new { task, nodes = results.ToArray() };
            }
        }
    }

    private object TransportStatus()
    {
        long ticks = Interlocked.Read(ref _relayTicks), packets = Interlocked.Read(ref _clientPackets) + Interlocked.Read(ref _nodePackets);
        (JsonElement? Status, string? Error) relay = ExternalRelayStatus();
        Dictionary<(ulong, ulong), DateTimeOffset> nodeActivity = ExternalNodeActivity(relay.Status);
        return new
        {
            vanillaVersion = DirectTransportConstants.VanillaVersion,
            wireFingerprint = DirectTransportConstants.WireFingerprint,
            counters = new
            {
                clientPackets = Interlocked.Read(ref _clientPackets), clientBytes = Interlocked.Read(ref _clientBytes),
                nodePackets = Interlocked.Read(ref _nodePackets), nodeBytes = Interlocked.Read(ref _nodeBytes),
                relayCpuMilliseconds = ticks * 1000d / Stopwatch.Frequency,
                averageRelayMicroseconds = packets == 0 ? 0 : ticks * 1_000_000d / Stopwatch.Frequency / packets,
                relayAllocatedBytes = Interlocked.Read(ref _relayAllocations), duplicateServerData = Interlocked.Read(ref _duplicateServerData),
                queueOverflows = Interlocked.Read(ref _queueOverflows),
                globalMessages = Interlocked.Read(ref _globalMessages), rejectedGlobalMessages = Interlocked.Read(ref _rejectedGlobalMessages),
                admissionRejections = Interlocked.Read(ref _admissionRejections),
                singleFlightRefusals = Interlocked.Read(ref _singleFlightRefusals),
                steamBootsToMenu = Interlocked.Read(ref _steamBootsToMenu),
            },
            reconnectStreaks = SteamReconnectStreaks(),
            admission = new
            {
                directFrontend = _options.DirectFrontend, acceptAll = _options.DirectFrontend,
                memberLimit = _admission.Settings.MemberLimit, groupId = _admission.Settings.GroupId,
                familySharing = _admission.Settings.FamilySharing, clusterPlayers = ClusterPlayers().Count,
            },
            steam = _steam?.Status() ?? ExternalSteamStatus(relay),
            eos = _eos?.Status(),
            sessions = _clients.Values.Select(session => new
            {
                client = session.ClientId, connectedAt = session.ConnectedAt, lastClientActivity = session.LastClientActivity,
                lastNodeActivity = nodeActivity.TryGetValue((session.ClientId, session.RelaySession), out DateTimeOffset nodeAt) ? nodeAt : session.LastNodeActivity,
                disconnectReason = session.DisconnectReason,
                backend = session.BackendEndpoint.ToString(),
                clientBacklogBytes = session.ClientBacklog.Bytes, serverBacklogBytes = session.ServerBacklog.Bytes,
                frontend = session.Client?.Frontend, remote = session.Client?.Remote,
                reliableBacklogPackets = session.Client?.ReliableBacklogPackets ?? 0,
                reliableBacklogStarted = session.ReliableBacklogStarted,
                withheldCreditBytes = session.WithheldCreditBytes,
                joiningWindowBytes = 4 * 1024 * 1024, typeTableSent = session.TypeTableSent,
                worldReady = session.WorldReady,
                duplicateServerData = session.DuplicateServerData,
                admissionState = session.AdmissionState, identityId = session.PlayerReady?.Identity.IdentityId,
                serviceName = session.Credential?.ServiceName, userId = session.Credential?.UserId,
                ownerId = session.Credential?.OwnerId, playerName = session.Credential?.Name,
                isAdmin = session.Credential?.IsAdmin,
				authorityGeneration = session.PlayerReady?.AuthorityGeneration,
				attachedIdentityId = session.AttachedIdentityId,
				attachedAuthorityGeneration = session.AttachedAdmissionGeneration,
				bindingRevision = session.BindingRevision,
				attachedBindingRevision = session.AttachedBindingRevision,
                admissionError = session.AdmissionError,
            }).ToArray(),
            nodes = _nodes.Values.Select(link => new
            {
                endpoint = link.Endpoint.ToString(), connected = link.IsConnected,
                connectedAt = link.ConnectedAt, lastActivity = link.LastActivity, disconnectedAt = link.DisconnectedAt,
                lastDisconnectReason = link.LastDisconnectReason, attachedClients = link.Clients.Count,
                // T-0154: packets LiteNetLib still holds for this link (sent, unacknowledged, or
                // waiting for the window). Channel 0 ordered carries the global-state publishes
                // (antenna snapshots), channel 0 unordered the WA commands; the remaining 15
                // channels relay client traffic. A queue that only grows is retained heap and a
                // stalled link, whichever the process counters say.
                reliableQueue = ReliableQueueDepths(link.Peer),
            }).ToArray(),
            // T-0321 P6: gateway-rs's own node links (path local/lan, traffic counters, rtt, invalid/unowned frames).
            // The packets never pass through C#, so nothing above carries them.
            relayNodes = relay.Status is { } relayStatus && relayStatus.TryGetProperty("nodes", out JsonElement relayNodes) ? relayNodes : (JsonElement?)null,
            recentDisconnects = _disconnects.ToArray(),
        };
    }

    private static object? ReliableQueueDepths(NetPeer? peer)
    {
        if (peer == null) return null;
        int orderedTotal = 0, unorderedTotal = 0;
        for (byte channel = 0; channel < NodeLinkChannels; channel++)
        {
            orderedTotal += peer.GetPacketsCountInReliableQueue(channel, true);
            unorderedTotal += peer.GetPacketsCountInReliableQueue(channel, false);
        }
        return new
        {
            globalOrdered = peer.GetPacketsCountInReliableQueue(0, true),
            globalUnordered = peer.GetPacketsCountInReliableQueue(0, false),
            orderedTotal, unorderedTotal, mtu = peer.Mtu, ping = peer.Ping,
        };
    }

    private void ForceReconnect(ClientSession session, string reason)
    {
        lock (session.RelayLock)
        {
            ClientChannel? peer = session.Client;
            if (session.DisconnectReason != null)
            {
                // #1221: a later reason (an admin kick, node-link-lost) for a session whose connection is gone.
                if (peer?.Connected != true) RemoveDeadSession(session, reason);
                return;
            }
            session.DisconnectReason = reason;
            Console.WriteLine($"Force reconnect client {session.ClientId}: {reason}{NoteSteamForcedEnd(session)}");
            if (reason.Contains("overflow", StringComparison.Ordinal)) Interlocked.Increment(ref _queueOverflows);
            _disconnects.Enqueue(new(session.ClientId, DateTimeOffset.UtcNow, reason));
            while (_disconnects.Count > 100) _disconnects.TryDequeue(out _);
            if (peer?.Connected != true)
            {
                // #1221: there is nothing to disconnect, so no disconnect event will ever remove the session.
                RemoveDeadSession(session, reason);
                return;
            }
            byte[] payload = new byte[10];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, 1); // MyControlMessageEnum.Disconnected
            // T-0244: the Client field must be the id the client knows its server by, or it is not
            // "the host left" to it (MyMultiplayerClient_ClientLeft) and the vanilla client ignores it.
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(2), ReconnectServerId(peer));
            peer.Send(new GatewayPacket(session.ClientId, 0, payload, DeliveryMethod.ReliableOrdered));
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(25); peer.Disconnect(); });
        }
    }

    private void FlushClientBacklog(ClientSession session)
    {
        if (_relay != null)
        {
            // gateway-rs holds the backlog and forwards it only if the pushed state allows.
            PushRouting(session, flushClient: true);
            return;
        }
		lock (session.RelayLock)
		{
			NodeLink? link = session.Link;
			if (RelayPaused || session.SwitchingBackend || !AttachedBindingRoutable(session) || !ReferenceEquals(session.AttachedLink, link)
				|| link?.IsConnected != true)
				return;
			while (AttachedBindingRoutable(session) && session.ClientBacklog.TryDequeue(out GatewayPacket? packet))
				SendClientPacketToNode(session, link, packet!);
		}
    }

    /// <summary>Releases the node-to-client packets held while <c>BufferServerPackets</c> was set.</summary>
    private void FlushServerBacklog(ClientSession session)
    {
        if (_relay != null) { PushRouting(session, flushServer: true); return; }
        while (session.ServerBacklog.TryDequeue(out GatewayPacket? packet))
            SendToClient(session, packet!);
    }

    /// <summary>Drops the held node-to-client packets (a switch that did not commit).</summary>
    private void DiscardServerBacklog(ClientSession session)
    {
        if (_relay != null) { PushRouting(session, discardServer: true); return; }
        while (session.ServerBacklog.TryDequeue(out _)) { }
    }

    private void SendClientPacketToNode(ClientSession session, NodeLink link, GatewayPacket packet)
    {
        SendToNode(link, packet);
    }

    private static void CompleteTargetReadiness(ClientSession session)
    {
        if (session.TargetServerPacketSeen)
            session.TargetReady.TrySetResult();
    }

    private static void SendToNode(NodeLink link, GatewayPacket packet) =>
        SendNodeLink(link, new(NodeLinkMessage.Relay, packet.ClientId, checked((byte)packet.Channel), packet.Payload, packet.Delivery));

    private void SendAttach(NodeLink link, ClientSession session)
    {
		lock (_pauseLock)
        {
			if (RelayPaused) return;
			if (_registry != null && session.PlayerReady is PlayerReady ready
				&& ready.AuthorityGeneration != _registry.WorldAuthority().Generation)
			{
				// Single-flight: resetting the debounce here spawned a NEW admission task per
				// attach attempt (~120/s after a WA replacement), saturating the new WA with
				// players commands whose late timeouts kept overwriting an already-successful
				// admission back to Stale. The in-flight attempt's own completion re-arms the
				// retry (success attaches, failure resets AdmissionStarted); this path only
				// needs to ensure one attempt is running.
				session.AdmissionState = "Refreshing";
				if (Volatile.Read(ref session.AdmissionStarted) == 0)
					BeginPlayerAdmission(session);
				return;
			}
            lock (link.Sync)
            {
				lock (session.RelayLock)
				{
					long admissionGeneration = session.PlayerReady?.AuthorityGeneration ?? 0;
					long identityId = session.PlayerReady?.Identity.IdentityId ?? 0;
					long bindingRevision = session.BindingRevision;
					if ((ReferenceEquals(session.AttachedLink, link) && session.AttachedBindingRevision == bindingRevision)
						|| ReferenceEquals(session.AttachingLink, link))
						return;
					// T-0357: every other reason not to send the Attach used to return silently; a Steam join
					// during boot was admitted and bound, no Attach left the gateway, nothing was logged and the
					// client backlog expired after 10 s. Named once per session and reason.
					string? skipped = !link.IsConnected ? "node link not connected"
						: !_clients.TryGetValue(session.ClientId, out ClientSession? current) || !ReferenceEquals(current, session) ? "session replaced"
						: !ReferenceEquals(session.Link, link) ? "session routed to another link"
						: session.Client?.Connected != true ? "client not connected"
						: !RouteStillValid(session, link) ? "route no longer valid"
						: PlayerAdmissionRequired(session) && session.PlayerReady == null ? "player binding not ready"
						: null;
					if (skipped != null)
					{
						if (session.AttachSkipped != skipped)
							Console.WriteLine($"Attach of client {session.ClientId} to {link.Endpoint} not sent: {skipped}");
						session.AttachSkipped = skipped;
						return;
					}
					session.AttachSkipped = null;
					session.AttachCompleted.Reset();
					byte[] binding = AttachBinding(session);
					SendNodeLink(link, new(NodeLinkMessage.Attach, session.ClientId, 0, binding, DeliveryMethod.ReliableOrdered));
					session.AttachingAdmissionGeneration = admissionGeneration;
					session.AttachingIdentityId = identityId;
					session.AttachingBindingRevision = bindingRevision;
					session.AttachingLink = link;
					Console.WriteLine($"Connected backend {link.Endpoint} as client {session.ClientId}");
				}
            }
        }
    }

    private static bool SendDetach(NodeLink link, ClientSession session)
    {
        lock (session.RelayLock)
        {
            if (!ReferenceEquals(session.AttachedLink, link) && !ReferenceEquals(session.AttachingLink, link))
                return false;
            if (!link.IsConnected)
            {
                session.AttachedLink = null;
                session.AttachingLink = null;
                return false;
            }
            session.DetachCompleted.Reset();
            SendNodeLink(link, new(NodeLinkMessage.Detach, session.ClientId, 0, [], DeliveryMethod.ReliableOrdered));
            session.DetachingLink = link;
            session.AttachedLink = null;
            session.AttachingLink = null;
            return true;
        }
    }

    private static void SendNodeLink(NodeLink link, NodeLinkPacket packet)
    {
        if (link.External)
        {
            SendExternalNodeLink(link, packet);
            return;
        }
        NetPeer? peer = link.Peer;
        if (peer?.ConnectionState == ConnectionState.Connected)
        {
            NetDataWriter writer = DirectTransportCodec.EncodeNodeLink(packet);
            DeliveryMethod delivery = packet.Delivery;
            if (delivery == DeliveryMethod.Unreliable && writer.Length > peer.GetMaxSinglePacketSize(delivery))
                delivery = DeliveryMethod.ReliableUnordered;
            peer.Send(writer, (byte)(packet.ClientId % NodeLinkChannels), delivery);
        }
    }

    private static void SendCredit(NodeLink link, ulong clientId, int bytes) =>
        SendNodeLink(link, new(NodeLinkMessage.Credit, clientId, 0, [], DeliveryMethod.ReliableOrdered, bytes));

    /// <summary>
    /// #1221: removes a session whose client connection is gone. A session normally ends with its frontend's
    /// disconnect event (OnPeerDisconnected, a relay "disconnected", the Steam close); when none is coming - a
    /// peer LiteNetLib never connected, a relay session gateway-rs no longer has - it stayed in _clients for
    /// good, and single-flight refused the identity until the gateway restarted.
    /// </summary>
    private void RemoveDeadSession(ClientSession session, string why)
    {
        if (RemoveSession(session))
            Console.WriteLine($"Removed the session of client {session.ClientId}: its client connection is gone ({why})");
    }

    /// <summary>How long a session may outlive its client connection before maintenance removes it. Longer than
    /// LiteNetLib's DisconnectTimeout and the Steam rejoin grace (30 s each), so it only takes what no event will.</summary>
    internal static TimeSpan DeadClientGrace = TimeSpan.FromSeconds(45);

    private bool ReapDeadClientSession(ClientSession session, long now)
    {
        if (session.Client?.Connected == true)
        {
            session.ClientGoneSince = 0;
            return false;
        }
        if (session.ClientGoneSince == 0)
        {
            session.ClientGoneSince = now;
            return false;
        }
        if (Stopwatch.GetElapsedTime(session.ClientGoneSince, now) < DeadClientGrace)
            return false;
        RemoveDeadSession(session, $"not connected for {DeadClientGrace.TotalSeconds:F0}s");
        return true;
    }

    private bool RemoveSession(ClientSession session)
    {
        if (!_clients.TryRemove(new KeyValuePair<ulong, ClientSession>(session.ClientId, session)))
            return false;
        lock (session.RelayLock)
        {
            NodeLink? link = session.Link;
            if (link != null)
            {
                link.Clients.TryRemove(session.ClientId, out _);
                SendDetach(link, session);
            }
            session.Client = null;
            session.AdmissionCancellation.Cancel();
        }
        return true;
    }

    private static NetManager CreateManager(INetEventListener listener) => new(listener)
    {
        UnsyncedEvents = false,
        AutoRecycle = true,
        DisconnectTimeout = 30000,
        ReconnectDelay = 500,
        MaxConnectAttempts = 20,
        ChannelsCount = NodeLinkChannels,
    };

    private Thread StartPollThread(string name, NetManager manager)
    {
        var thread = new Thread(() => PollLoop(manager)) { Name = name, IsBackground = true };
        thread.Start();
        return thread;
    }

    private bool RouteStillValid(ClientSession session, NodeLink link)
    {
        if (session.RegistryRoute == null || session.RegistryRouteValidated)
            return true;
        try
        {
            ClientRoute? current = _registry?.ResolveClientRoute(session.ClientId);
            bool valid = current == session.RegistryRoute
                && EndpointParser.Parse(current.Endpoint, DirectTransportConstants.DefaultPort).Equals(link.Endpoint);
            if (!valid)
            {
                Console.WriteLine($"Route no longer valid for client {session.ClientId}:"
                    + $" resolved {current?.Node}@{current?.Endpoint} attached {link.Endpoint}");
                session.Client?.Disconnect();
            }
            return valid;
        }
        catch (RegistryBusyException)
        {
            // T-0197: transient. The route is unconfirmed, not wrong - a partition being re-homed
            // resolves to the same node again more often than not, and dropping a playing client
            // over it would be a self-inflicted version of the disconnect this check exists to
            // follow. Leave the session attached and unvalidated; the next pass asks again.
            return true;
        }
        catch (RegistryConflictException)
        {
            session.Client?.Disconnect();
            return false;
        }
    }

    /// <summary>
    /// Keep a connection request the registry called transient, instead of rejecting it.
    ///
    /// The request is simply not answered: LiteNetLib leaves it pending, the client's own connect
    /// attempts keep arriving against the same pending request rather than raising new ones, and
    /// <see cref="RetryHeldJoins"/> answers it from the poll thread. Accept() and Reject() are
    /// therefore always called from the thread that polls the frontend, which is the only thread
    /// allowed to create or drop peers on that NetManager.
    /// </summary>
    private void HoldJoin(IJoinRequest request, ulong clientId, string? assertedName, string reason, DateTimeOffset? holdUntil)
    {
        if (JoinHold <= TimeSpan.Zero)
        {
            Console.Error.WriteLine($"Rejected client {clientId}: {reason} (holding disabled)");
            request.Reject();
            return;
        }
        // An external relay re-asks every second itself and enforces the hold's deadline
        // (Config.joinHoldSeconds); here the answer is only "not yet".
        if (request.Hold(reason))
        {
            // gateway-rs re-asks every second as a fresh request: log once per relay session, so the
            // hold is as visible as the in-process one (P3-PLR-02 asserts this line).
            if (request is ExternalJoinRequest external && FirstExternalHold(clientId, external.Session))
                Console.WriteLine($"Holding join for client {clientId}: {reason};"
                    + $" answering when the registry can route it, or refusing at {DateTimeOffset.UtcNow + JoinHold:HH:mm:ss}Z");
            return;
        }
        DateTimeOffset deadline = holdUntil ?? DateTimeOffset.UtcNow + JoinHold;
        lock (_heldJoins)
        {
            // ONE HELD REQUEST PER CLIENT. LiteNetLib gives up on a connection attempt after its
            // own handful of retries, and the client plugin then makes a NEW attempt - which
            // arrives here as a second request while the first is still pending and unanswerable
            // (T-0197's passing run retried for 39 s, so this is the normal shape, not an edge).
            // The older request is dead weight: refuse it so nothing accumulates, and let the new
            // one carry its own full hold rather than inheriting a clock that is already running.
            for (int i = _heldJoins.Count - 1; i >= 0; i--)
            {
                if (_heldJoins[i].ClientId != clientId || ReferenceEquals(_heldJoins[i].Request, request)) continue;
                try { _heldJoins[i].Request.Reject(); } catch (Exception e) { Console.Error.WriteLine("Superseded join reject failed: " + e.Message); }
                _heldJoins.RemoveAt(i);
            }
            _heldJoins.Add(new HeldJoin(request, clientId, assertedName, deadline, reason));
            if (holdUntil == null)
                Console.WriteLine($"Holding join for client {clientId}: {reason};"
                    + $" answering when the registry can route it, or refusing at {deadline:HH:mm:ss}Z");
        }
    }

    /// <summary>
    /// Answer every held join that can be answered now, and refuse the ones whose hold has run out.
    /// Runs on the frontend poll thread, once per poll pass. A retry re-enters
    /// <see cref="OnConnectionRequest(IJoinRequest, DateTimeOffset?, ValueTuple{ulong, string}?)"/> whole rather than
    /// jumping into the middle of it: the handshake decode, the single-flight check and the
    /// admission test are all still the right questions the second time, and carrying the original
    /// deadline back in is what keeps a still-transient answer from resetting the clock.
    /// </summary>
    private void RetryHeldJoins()
    {
        // ONCE A SECOND, NOT ONCE A POLL PASS. The poll loop spins at ~1 kHz and every retry takes
        // the registry lock that the 1 Hz lease sweep and every node heartbeat also take; the state
        // being waited for is re-homing, which moves on the lease sweep's cadence anyway.
        long now_ticks = Environment.TickCount64;
        if (now_ticks < _heldJoinsNextSweep) return;
        _heldJoinsNextSweep = now_ticks + 1000;
        HeldJoin[] held;
        lock (_heldJoins)
        {
            if (_heldJoins.Count == 0) return;
            held = _heldJoins.ToArray();
            _heldJoins.Clear();
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (HeldJoin join in held)
        {
            if (now >= join.Deadline)
            {
                Console.Error.WriteLine($"Rejected client {join.ClientId} after holding {JoinHold.TotalSeconds:F0}s:"
                    + $" {join.Reason} - the state that was expected to clear did not");
                try { join.Request.Reject(); }
                catch (Exception e) { Console.Error.WriteLine("Held join reject failed: " + e.Message); }
                continue;
            }
            try { OnConnectionRequest(join.Request, join.Deadline, (join.ClientId, join.AssertedName)); }
            catch (Exception e) { Console.Error.WriteLine("Held join retry failed: " + e); }
        }
    }

    private void PollLoop(NetManager manager)
    {
        bool frontend = ReferenceEquals(manager, _front);
        while (_running)
        {
            try { manager.PollEvents(); }
            catch (Exception e) { Console.Error.WriteLine("Poll failed: " + e); }
            // Held joins belong to the frontend's peers, and only this thread may answer them.
            if (frontend)
            {
                try { RetryHeldJoins(); }
                catch (Exception e) { Console.Error.WriteLine("Held join sweep failed: " + e); }
            }
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Per-session flow control and liveness, on its own thread so it runs for every frontend -
    /// the Direct Transport poll thread may not exist at all.
    /// </summary>
    private void StartSessionMaintenance()
    {
        var thread = new Thread(() =>
        {
            while (_running)
            {
                try
                {
                    long now = Stopwatch.GetTimestamp();
                    foreach (ClientSession session in _clients.Values)
                    {
                        if (ReapDeadClientSession(session, now))
                            continue;
                        ReplenishClientCredit(session);
                        // A join still waiting for its ticket verdict has nowhere to flush to
                        // (§3.2 attaches no node before admission), so the backlog's *age* is no
                        // evidence of a stuck client - only its size is, and overflow still drops
                        // it. Ageing a pending join out cost real rejoins their first attempt.
                        if (session.Credential != null && session.ClientBacklog.IsExpired(now))
                            ForceReconnect(session, "client-backlog-expired");
                        // No activity-based silence watchdog on Direct Transport: during the
                        // post-respawn re-stream flood the poll thread's own event processing lags
                        // 30s+ behind (the client's packets ARRIVE but sit unprocessed), so
                        // LastClientActivity reads as silence while the client is provably alive -
                        // and the force-reconnect zombies it. A dead DT peer is LiteNetLib's
                        // DisconnectTimeout to detect. Steam P2P has no such event, so a Steam
                        // session that goes quiet far past any re-stream flood is reaped here.
                        else if (session.ReliableBacklogStarted is DateTimeOffset congested
                            && DateTimeOffset.UtcNow - congested >= TimeSpan.FromSeconds(10))
                            ForceReconnect(session, "client-ack-backlog");
                        // gateway-rs sees the traffic of its sessions and reports "silent" itself.
                        else if (session.Client is { Frontend: not "dt" } and not ExternalClientChannel
                            && DateTimeOffset.UtcNow - session.LastClientActivity >= TimeSpan.FromSeconds(180))
                            ForceReconnect(session, "client-silent");
                        else if (SteamSessionWithoutLogin(session))
                            EndSteamSessionWithoutLogin(session);
                    }
                    PruneSteamReconnectStreaks();
                }
                catch (Exception e) { Console.Error.WriteLine("Session maintenance failed: " + e); }
                Thread.Sleep(100);
            }
        }) { Name = "Gateway-Session-Maintenance", IsBackground = true };
        thread.Start();
    }

    private sealed class ClientSession
    {
        /// <summary>T-0357: why the last Attach was not sent (null = sent or nothing to say); logged on change.</summary>
        public string? AttachSkipped;
        /// <summary>T-0357: the last reason RetryPlayerAttach logged for waiting, and the last reason the
        /// backlog age was not enforced (ClientBacklogHeld); each logged on change.</summary>
        public volatile string? AttachWaitLogged;
        public volatile string? BacklogHoldLogged;
        /// <summary>T-0357: the link whose node last sent Detach for this session (see RetryPlayerAttach).</summary>
        public volatile NodeLink? AttachRetryDetachedBy;
        public readonly ulong ClientId;
        public ClientRoute? RegistryRoute;
        public readonly object RelayLock = new();
        public readonly SemaphoreSlim SwitchLock = new(1, 1);
        public readonly ManualResetEventSlim AttachCompleted = new(false);
        public readonly ManualResetEventSlim DetachCompleted = new(false);
        // 4 MB: a held client streams ~10-30 KB/s of input/position traffic, so the byte cap must
        // outlast any deliberate hold (pause, slow backend switch); the age bound is the wedge
        // detector, the byte cap only guards runaway memory.
        public readonly BoundedPacketQueue ClientBacklog = new(4 * 1024 * 1024, TimeSpan.FromSeconds(10));
        public readonly BoundedPacketQueue ServerBacklog = new(1024 * 1024, TimeSpan.MaxValue);
        public IPEndPoint BackendEndpoint;
        public ClientChannel? Client;
        /// <summary>#1221: Stopwatch timestamp since which <see cref="Client"/> has not been connected; 0 while it is.</summary>
        public long ClientGoneSince;
        /// <summary>
        /// T-0229: set by the external relay adapter. The routing fields below are mirrored into
        /// gateway-rs, which applies the per-packet rules; every change is pushed from here, so no
        /// write site can forget to. Null for the in-process relay.
        /// </summary>
        public Action<ClientSession>? RoutingChanged;
        /// <summary>
        /// T-0229: held while PushRouting snapshots the routing fields and sends them, so the
        /// SessionUpdates of one session reach gateway-rs in the order they were read. Without it
        /// the join thread's snapshot (not yet attached) could be sent after the attach-ack
        /// thread's (attached), and gateway-rs kept a stale "not attached" (relay bench, 300
        /// clients: 4 never forwarded and were force-reconnected when their backlog aged out).
        /// Taken only in PushRouting, around lock-free reads and the IPC send.
        /// </summary>
        public readonly object RoutingPushLock = new();
        /// <summary>gateway-rs's session number for this client (Docs/Gateway/RelayIpc.md); 0 in process.</summary>
        public ulong RelaySession;
        private NodeLink? _link, _attachedLink, _validationLink;
        private volatile bool _switchingBackend, _bufferServerPackets, _suppressJoinMessages;
        public NodeLink? Link { get => _link; set { if (!ReferenceEquals(_link, value)) { _link = value; RoutingChanged?.Invoke(this); } } }
        public NodeLink? AttachedLink { get => _attachedLink; set { if (!ReferenceEquals(_attachedLink, value)) { _attachedLink = value; RoutingChanged?.Invoke(this); } } }
        public NodeLink? AttachingLink;
        public NodeLink? DetachingLink;
        public GatewayPacket? ClientConnectedPacket;
        /// <summary>T-0244: gateway-rs reported this session's CLIENT_CONNECTED (set on receipt, before
        /// the ordered worker gets to it). A Steam session without one is not a join.</summary>
        public volatile bool LoginPresented;
        /// <summary>Set once the perimeter admitted this client; null means not yet validated.</summary>
        public ClientCredential? Credential;
        public string? AssertedName;
        public string? AssertedServiceName;
        public volatile bool IsProfiling;
        public ulong TicketOwnerId;
        /// <summary>SE-1158: the DLC app ids Steam says the player has no license for; null without a Steam ticket.</summary>
        public uint[]? MissingDlcs;
        /// <summary>SE-1158: the skins the player's last inventory blob proved; null until a node asked.</summary>
        public volatile string[]? Skins;
        public readonly CancellationTokenSource AdmissionCancellation = new();
        public int AdmissionStarted;
		public int AttachRetryStarted;
        public volatile string AdmissionState = "WaitingForClient";
        public volatile string? AdmissionError;
        public PlayerReady? PlayerReady;
		public long BindingRevision;
		public long AttachingBindingRevision;
		public long AttachedBindingRevision;
		public long AttachingAdmissionGeneration;
		public long AttachingIdentityId;
		public long AttachedAdmissionGeneration;
		public long AttachedIdentityId;
        public bool SwitchingBackend { get => _switchingBackend; set { if (_switchingBackend != value) { _switchingBackend = value; RoutingChanged?.Invoke(this); } } }
        public bool BufferServerPackets { get => _bufferServerPackets; set { if (_bufferServerPackets != value) { _bufferServerPackets = value; RoutingChanged?.Invoke(this); } } }
        public volatile bool TypeTableSent;
        public bool SuppressJoinMessages { get => _suppressJoinMessages; set { if (_suppressJoinMessages != value) { _suppressJoinMessages = value; RoutingChanged?.Invoke(this); } } }
        /// <summary>Set on the client's CLIENT_READY: the world is loaded and the join sequence fully
        /// consumed. Client-bearing handovers are refused until then - a backend switch suppresses
        /// join messages, and a still-joining client starves on that suppression.</summary>
        public volatile bool WorldReady;
        /// <summary>When CLIENT_READY arrived (set just before <see cref="WorldReady"/>).</summary>
        public DateTimeOffset WorldReadyAt;
        public volatile bool RegistryRouteValidated;
        /// <summary>T-0237: why a Steam session was accepted without a route (the registry said "come back
        /// shortly"); admission resolves it again before attaching. See ResolveDeferredSteamRoute.</summary>
        public volatile string? RouteDeferred;
        public int RouteWaitStarted;
        public DateTimeOffset ConnectedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset LastClientActivity = DateTimeOffset.UtcNow;
        public DateTimeOffset LastNodeActivity = DateTimeOffset.UtcNow;
        public string? DisconnectReason;
        public long DuplicateServerData;
        public DateTimeOffset? ReliableBacklogStarted;
        public int WithheldCreditBytes;
        public NodeLink? ValidationLink { get => _validationLink; set { if (!ReferenceEquals(_validationLink, value)) { _validationLink = value; RoutingChanged?.Invoke(this); } } }
        public bool TargetServerPacketSeen;
        public TaskCompletionSource TargetReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ClientSession(ulong clientId, IPEndPoint backendEndpoint, ClientRoute? registryRoute = null)
        {
            ClientId = clientId;
            BackendEndpoint = backendEndpoint;
            RegistryRoute = registryRoute;
        }
    }

    internal sealed class BoundedPacketQueue
    {
        private readonly Queue<GatewayPacket> _packets = new();
        private readonly int _maximumBytes;
        private readonly TimeSpan _maximumAge;
        private int _bytes;
        private long _started;

        public BoundedPacketQueue(int maximumBytes, TimeSpan maximumAge)
        {
            _maximumBytes = maximumBytes;
            _maximumAge = maximumAge;
        }

        public void RefreshAge()
        {
            lock (_packets)
                _started = Stopwatch.GetTimestamp();
        }

        public string Describe()
        {
            lock (_packets)
                return $"bytes={_bytes}/{_maximumBytes} packets={_packets.Count} " +
                    $"ageMs={(_packets.Count == 0 ? 0 : (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds)}";
        }

        public bool TryEnqueue(GatewayPacket packet)
        {
            lock (_packets)
            {
                int size = packet.Payload.Length + 2;
                long now = Stopwatch.GetTimestamp();
                if (_bytes + size > _maximumBytes || _packets.Count != 0 && Stopwatch.GetElapsedTime(_started, now) > _maximumAge)
                    return false;
                if (_packets.Count == 0)
                    _started = now;
                _packets.Enqueue(packet);
                _bytes += size;
                return true;
            }
        }

        public bool TryDequeue(out GatewayPacket? packet)
        {
            lock (_packets)
            {
                if (!_packets.TryDequeue(out packet))
                    return false;
                _bytes -= packet.Payload.Length + 2;
                return true;
            }
        }

        public bool IsExpired(long now)
        {
            lock (_packets)
                return _packets.Count != 0 && Stopwatch.GetElapsedTime(_started, now) > _maximumAge;
        }

        public int Count { get { lock (_packets) return _packets.Count; } }
        public int Bytes { get { lock (_packets) return _bytes; } }
    }

    private sealed record HandoverPause(string TransferId, ulong Partition, ClientSession? Session)
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record DisconnectEvent(ulong Client, DateTimeOffset Timestamp, string Reason);

    private sealed class NodeLink
    {
        public readonly IPEndPoint Endpoint;
        public readonly object Sync = new();
        public readonly ManualResetEventSlim Connected = new(false);
        public readonly ConcurrentDictionary<ulong, ClientSession> Clients = new();
        public NetPeer? Peer;
        /// <summary>T-0229: an external relay (gateway-rs) owns this link; its state arrives as LinkState events.</summary>
        public readonly bool External;
        public RelayIpcServer? Relay;
        public volatile bool ExternalConnected;
        /// <summary>The link can carry frames now: the LiteNetLib peer is connected, or gateway-rs says its link is.</summary>
        public bool IsConnected => External ? ExternalConnected : Peer?.ConnectionState == ConnectionState.Connected;
        public DateTimeOffset ConnectedAt;
        public DateTimeOffset LastActivity;
        public DateTimeOffset? DisconnectedAt;
        public string? LastDisconnectReason;

        public NodeLink(IPEndPoint endpoint, bool external = false)
        {
            Endpoint = endpoint;
            External = external;
        }
    }

    private sealed record LineageRequest(string OpId, LineageKind Kind, PartitionVersion[] Inputs, PartitionVersion[] Outputs, Dictionary<ulong, long[]>? Members, long ClusterTime);
    private sealed record TopologyRequest(string OpId, LineageKind Kind, string Node, long Epoch, PartitionVersion[] Inputs, ulong[] OutputIds, Dictionary<ulong, long[]>? Members, long ClusterTime,
        Dictionary<ulong, long[]>? Seated = null);
    private sealed record SnapshotRequest(string SnapshotId, SnapshotKind Kind, long ClusterTime, SaveId[] PartitionRefs, string[] Dirty,
        GlobalSaveId? GlobalRef = null, VoxelRef[]? VoxelRefs = null);
    private sealed record HandoverRequest(string Id, ulong Partition, string SourceNode, long SourceEpoch, long SourceGeneration, string TargetNode, long TargetEpoch);
    private sealed record HeartbeatRequest(string Node, long Epoch, PartitionVersion[] Ownerships, ClientPartitionReport[]? ClientPartitions,
        double LocalSendMilliseconds, long? SimulationFrame, SaveFailureReport? SaveFailure, double? AuthorityMilliseconds,
        long? RecoveryCapacity = null, ulong[]? Quarantined = null, long? ProjectedLoad = null, long? LoadCapacity = null,
        long? RecoveryLoadsPending = null, HitchReport[]? Hitches = null, long? ConfigRevision = null, bool? Serving = null,
        string? DeploymentRevision = null, bool ConfigurationVerified = false, string? ConfigurationFailure = null,
        bool Liveness = false, Dictionary<ulong, RemoteUseReport>? RemoteUsers = null, AccountingClockSample? AccountingClock = null,
        string? ServerData = null);
    /// <summary>A game-thread frame over the node's hitch threshold, with the handover phase that held it (§3).</summary>
    private sealed record HitchReport(DateTime Utc, double Ms, string Phase, string? TransferId, ulong Partition, double PhaseMs);
    private sealed record AccountingClockRequest(string Node, long Epoch, AccountingClockSample Sample);
    private sealed record HeartbeatResponse(string Node, long Epoch, string Endpoint, DateTimeOffset Registered, NodeState State,
        DateTimeOffset LeaseExpires, DateTimeOffset? ForceDrainAt, NodeRole Role, long AuthorityGeneration, long WorldAuthorityGeneration,
        PartitionVersion[] Ownerships, double LocalSendMilliseconds, double GatewayMilliseconds, double? AuthorityMilliseconds,
        long HandoverConfigRevision = 0, bool ClusterDraining = false, bool TopologyTransfer = true, AccountingClockSample? AccountingClock = null,
        bool ServerDataWanted = false);
    // SE-1076: ServerDataWanted asks the node for MyCachedServerItem.GetServerData() in its next heartbeat's ServerData
    // (base64), which the Steam frontend publishes as the "sc" rules of the server details. A node without the field
    // never sends it; a registry without it never asks.
    // T-0339 (design C): TopologyTransfer tells the node this registry applies a transfer (GatewayRegistry.IsTransfer); a
    // registry without the field is one that refuses it, and the node then makes the split and the merge instead.
    private sealed record RecoveryRequest(string Node, long Epoch, ulong[] Partitions);
    private sealed record NodeSlotRequest(string SlotKey, string Host, NodeRole Role, NodeDesiredState Desired);
    private sealed record ExecutorHeartbeatRequest(string Executor, SpawnObservation[] Observations);
    private sealed record GlobalEnvelope(string Kind, long Generation, string Subsystem, long Sequence, string Origin, string? Target,
		string Payload, Guid OperationId = default);
    private sealed record GlobalCommandResult(bool Success, string Payload, string Error);
	private sealed record RespawnSwitchRequest(Guid OperationId, ulong UserId, int SerialId, long IdentityId,
		long RespawnEntityId, long GridEntityId, string Origin, ulong PartitionId, long Generation,
		bool JoinGame, string? ModelName, uint PackedColor, string? ClientState, string? RespawnShipId = null,
		bool NewIdentity = false, bool Preview = false);
	private sealed record RespawnExecutionResult(bool Ready, bool Success, long ControlledEntityId);
	private sealed record PendingGatewayCommand(long Generation, string Subsystem, Guid OperationId, TaskCompletionSource<string> Completion);
    private sealed record PlayerBindingRequest(string ServiceName, ulong UserId, ulong OwnerId, int SerialId, string Name, bool IsAdmin,
        long ExpectedIdentityId = 0);
    /// <summary>What the perimeter validated about a client (Security.md §3.5 identity trust chain).</summary>
    internal sealed record ClientCredential(string ServiceName, ulong UserId, ulong OwnerId, string Name, bool IsAdmin);

    /// <summary>ServiceName of a client admitted through Direct Transport, with no Steam identity.</summary>
    internal const string DirectTransportService = "DirectTransport";
    private sealed record IdentityAuthorityEntry(long IdentityId, string Name, string? Model, bool HasColor,
        float ColorX, float ColorY, float ColorZ, bool IsNpc);
	private sealed record PlayerBindingEntry(ulong UserId, int SerialId, long IdentityId);
	private sealed record IdentityAuthoritySnapshot(IdentityAuthorityEntry[] Identities, PlayerBindingEntry[] Bindings);
    /// <summary>The World Authority's player binding. The Gateway adds what Steam says the player owns (SE-1158) when it attaches them.</summary>
    private sealed record PlayerReady(long AuthorityGeneration, string ServiceName, ulong UserId, ulong OwnerId, int SerialId,
        bool IsAdmin, IdentityAuthorityEntry Identity, uint[]? MissingDlcs = null, string[]? Skins = null);

    /// <summary>The attach binding a node gets: the player binding with the Steam ownership of SE-1158.</summary>
    private static byte[] AttachBinding(ClientSession session) => session.PlayerReady == null ? []
        : JsonSerializer.SerializeToUtf8Bytes(session.PlayerReady with { MissingDlcs = session.MissingDlcs, Skins = session.Skins });
}
