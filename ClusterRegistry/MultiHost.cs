using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ClusterProtocol;

namespace ClusterRegistry;

/// <summary>
/// T-0321 multi-host, gateway side: the per-request loopback exemption of the dual control prefix, the credential
/// refusals nodes act on (ClusterNet.RejectionCode), peer-rejection reports, and the fault-injection silence hook.
/// </summary>
internal sealed partial class DirectGateway
{
    /// <summary>
    /// A request that arrived on the automatic 127.0.0.1 prefix of a LAN --control. It is served exactly as a
    /// loopback-bound gateway without tokens served every request before T-0321. A loopback-bound gateway (one
    /// prefix) is unaffected: this is false and its rules are unchanged.
    /// </summary>
    private bool LoopbackExempt(HttpListenerContext context) =>
        _options.Control != null && !IPAddress.IsLoopback(_options.Control.Address)
        && context.Request.LocalEndPoint is { } local && IPAddress.IsLoopback(local.Address);

    private bool JoinTokenPresented(HttpListenerContext context) =>
        ClusterNet.Bearer(context.Request.Headers["Authorization"]) is { } token && _options.Admission?.AcceptsToken(token) == true;

    /// <summary>Recent credential refusals and peer-rejection reports, for /diag (newest last, bounded).</summary>
    private readonly ConcurrentQueue<object> _credentialRejections = new();
    private long _credentialRejectionCount;

    private void RememberRejection(object entry)
    {
        Interlocked.Increment(ref _credentialRejectionCount);
        _credentialRejections.Enqueue(entry);
        while (_credentialRejections.Count > 64 && _credentialRejections.TryDequeue(out _)) { }
    }

    internal object CredentialRejectionsDiag() => new { count = Interlocked.Read(ref _credentialRejectionCount), recent = _credentialRejections.ToArray() };

    /// <summary>
    /// Refuses a legacy control request that presented no acceptable credential, with a code a node can act on:
    /// <list type="bullet">
    /// <item><c>credential_required</c> (401): no bearer at all;</item>
    /// <item><c>admin_scope_required</c> (403): a credential this gateway knows (a join token, a scoped token) on a route
    /// that needs the admin token - a scope problem, never a stale process;</item>
    /// <item><c>credential_rejected</c> (401): a bearer that matches nothing this start accepts. The node that sent it
    /// belongs to another start (or a rotated-out generation) and exits on this answer.</item>
    /// </list>
    /// Every refusal is logged with the caller's identity headers and the token FINGERPRINT, never the token.
    /// </summary>
    private void RefuseControlCredential(HttpListenerContext context, string path)
    {
        string? token = ClusterNet.Bearer(context.Request.Headers["Authorization"]);
        bool known = token != null && (_options.Admission?.AcceptsToken(token) == true || _adminTokens?.Match(token) != null);
        string code = token == null ? ClusterNet.CredentialRequired : known ? "admin_scope_required" : ClusterNet.CredentialRejected;
        var entry = new
        {
            at = DateTimeOffset.UtcNow,
            code,
            path,
            remote = context.Request.RemoteEndPoint?.ToString(),
            node = context.Request.Headers[ClusterNet.NodeHeader] ?? context.Request.QueryString["node"],
            epoch = context.Request.Headers[ClusterNet.EpochHeader] ?? context.Request.QueryString["epoch"],
            host = context.Request.Headers[ClusterNet.HostHeader],
            pid = context.Request.Headers[ClusterNet.PidHeader],
            tokenFingerprint = ClusterNet.Fingerprint(token),
        };
        RememberRejection(entry);
        Console.WriteLine($"WARNING: control credential refused code={code} path={path} remote={entry.remote} node={entry.node ?? "?"} "
            + $"epoch={entry.epoch ?? "?"} host={entry.host ?? "?"} pid={entry.pid ?? "?"} token_fp={entry.tokenFingerprint}");
        context.Response.Headers["WWW-Authenticate"] = "Bearer";
        WriteJson(context, known ? 403 : 401, new { error = "bearer token required", code });
    }

    internal sealed record PeerRejectionReport(string? Reporter, long ReporterEpoch, string? PeerEndpoint, string? Route, string? Code,
        string? Peer, long PeerEpoch, string? TokenFingerprint);

    /// <summary>
    /// <c>POST /registry/peer-rejections</c>: a node reports that a PEER refused its credential. A stale reporter is
    /// answered 403 <c>stale_incarnation</c> (and exits on it); otherwise the registry judges the peer and logs it.
    /// </summary>
    private void HandlePeerRejection(HttpListenerContext context)
    {
        PeerRejectionReport report = ReadJson<PeerRejectionReport>(context);
        try
        {
            string verdict = _registry!.JudgePeerRejection(report.Reporter, report.ReporterEpoch, report.Peer, report.PeerEpoch, report.PeerEndpoint);
            var entry = new
            {
                at = DateTimeOffset.UtcNow, code = "peer:" + report.Code, verdict, reporter = report.Reporter, reporterEpoch = report.ReporterEpoch,
                reporterHost = context.Request.Headers[ClusterNet.HostHeader], reporterPid = context.Request.Headers[ClusterNet.PidHeader],
                peer = report.Peer, peerEpoch = report.PeerEpoch, peerEndpoint = report.PeerEndpoint, route = report.Route,
                tokenFingerprint = report.TokenFingerprint,
            };
            RememberRejection(entry);
            Console.WriteLine($"WARNING: peer credential rejection verdict={verdict} reporter={report.Reporter}/{report.ReporterEpoch}"
                + $"@{entry.reporterHost ?? "?"} pid={entry.reporterPid ?? "?"} peer={report.Peer ?? "?"}/{report.PeerEpoch} endpoint={report.PeerEndpoint} "
                + $"route={report.Route} code={report.Code} token_fp={report.TokenFingerprint}");
            WriteJson(context, 200, new { verdict });
        }
        catch (StaleCredentialException stale)
        {
            Console.WriteLine($"WARNING: peer credential rejection from a stale reporter {report.Reporter}/{report.ReporterEpoch}: {stale.Message}");
            WriteJson(context, 403, new { error = stale.Message, code = stale.Code });
        }
    }

    /// <summary>The gateway's own calls to nodes: a node that refuses the gateway's credential is judged like a peer report.</summary>
    private sealed class NodeCallHandler(DirectGateway gateway) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 401 or 403 && request.RequestUri != null)
            {
                try
                {
                    // Peer rejection envelopes are metadata too. This handler runs before
                    // the caller sees headers, so it must not bypass its deadline or cap.
                    await response.Content.LoadIntoBufferAsync(OfflineMetadataLimit, cancellationToken)
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (ClusterNet.RejectionCode((int)response.StatusCode, body) is { } code && gateway._registry is { } registry)
                    {
                        (string? peer, long epoch) = ClusterNet.RejectingPeer(body);
                        string endpoint = request.RequestUri.GetLeftPart(UriPartial.Authority);
                        string verdict = registry.JudgePeerRejection(null, 0, peer, epoch, endpoint);
                        gateway.RememberRejection(new { at = DateTimeOffset.UtcNow, code = "node:" + code, verdict, reporter = "gateway", peer, peerEpoch = epoch,
                            peerEndpoint = endpoint, route = request.RequestUri.AbsolutePath });
                        Console.WriteLine($"WARNING: node {peer ?? "?"}/{epoch} at {endpoint} refused the gateway's credential ({code}) on "
                            + $"{request.RequestUri.AbsolutePath}; verdict={verdict}");
                    }
                }
                catch { response.Dispose(); throw; }
            }
            return response;
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => SendAsync(request, cancellationToken).GetAwaiter().GetResult();
    }

    private string[] _silencedHosts = [];
    private long _silencedHostsRead;
    private long _faultSilenced;

    /// <summary>
    /// T-0321 P4 fault injection (<c>CLUSTER_FAULT_SILENCE_HOSTS</c> = a file of host names, re-read at most once a
    /// second): requests from nodes of a listed host (their X-Cluster-Host) and executor polls for a listed host are
    /// aborted without an answer - to the node an outage, never a rejection. Unset or empty = nothing silenced.
    /// </summary>
    private bool FaultSilenced(HttpListenerContext context, string path)
    {
        string? file = Environment.GetEnvironmentVariable("CLUSTER_FAULT_SILENCE_HOSTS");
        if (string.IsNullOrWhiteSpace(file)) return false;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _silencedHostsRead) >= 1000)
        {
            try
            {
                _silencedHosts = File.Exists(file) ? File.ReadAllLines(file).Select(line => line.Trim())
                    .Where(line => line.Length != 0 && !line.StartsWith('#')).ToArray() : [];
            }
            catch (IOException) { }
            Interlocked.Exchange(ref _silencedHostsRead, now);
        }
        string[] silenced = _silencedHosts;
        if (silenced.Length == 0) return false;
        string? host = context.Request.Headers[ClusterNet.HostHeader];
        if (host == null && path.Equals(CometWorks.ClusterGateway.AdminContract.V1.ExecutorProtocol.HeartbeatRoute, StringComparison.OrdinalIgnoreCase)
            && ClusterNet.Bearer(context.Request.Headers["Authorization"]) is { } token)
            host = _adminTokens?.Match(token)?.Name;
        if (host == null || !silenced.Contains(host, StringComparer.OrdinalIgnoreCase)) return false;
        if (Interlocked.Increment(ref _faultSilenced) % 100 == 1)
            Console.WriteLine($"fault injection: silenced a request from host {host} ({path}); {Interlocked.Read(ref _faultSilenced)} so far");
        context.Response.Abort();
        return true;
    }
}

/// <summary>A registry answer that names the caller's own incarnation as stale; answered 403 with <see cref="Code"/>.</summary>
internal sealed class StaleCredentialException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal sealed partial class GatewayRegistry
{
    /// <summary>
    /// T-0321: judges a credential rejection between two cluster processes. The REPORTER (a node; null for the gateway
    /// itself) must be a current incarnation, else <see cref="StaleCredentialException"/> - it is the stale one and exits
    /// on the 403. The rejecting PEER, identified by name or by its control endpoint:
    /// <list type="bullet">
    /// <item><c>peer-unknown</c>: no registered node answers at that endpoint or under that name (a leftover process);</item>
    /// <item><c>peer-stale-incarnation</c>: the peer named an epoch that is not its current one - an old incarnation still
    /// holding the port. It is already fenced: every registry call with that epoch is refused and it exits on the refusal;</item>
    /// <item><c>peer-dead</c>: the registered incarnation is Dead or Empty - fenced the same way;</item>
    /// <item><c>both-current</c>: two current incarnations disagree about the join token. Nobody is stale, so nobody is
    /// fenced; this is a token distribution error for the operator (logged as a warning by the caller).</item>
    /// </list>
    /// </summary>
    public string JudgePeerRejection(string? reporter, long reporterEpoch, string? peer, long peerEpoch, string? peerEndpoint)
    {
        lock (_lock)
        {
            if (reporter != null)
            {
                if (!_state.Nodes.TryGetValue(reporter, out NodeRegistration? self))
                    throw new StaleCredentialException(ClusterNet.NodeIdRetired, $"Node '{reporter}' is not registered");
                if (self.Epoch != reporterEpoch || self.State == NodeState.Dead)
                    throw new StaleCredentialException(ClusterNet.StaleIncarnation, $"Node '{reporter}' epoch {reporterEpoch} is not current");
            }
            string? wanted = peerEndpoint == null ? null : StripScheme(peerEndpoint);
            NodeRegistration? row = peer != null && _state.Nodes.TryGetValue(peer, out NodeRegistration? named) ? named
                : wanted == null ? null
                : LiveFirst(_state.Nodes.Values.Where(x => x.ControlEndpoint != null && StripScheme(x.ControlEndpoint) == wanted));
            string verdict = row == null ? "peer-unknown"
                : peerEpoch > 0 && peerEpoch != row.Epoch ? "peer-stale-incarnation"
                : row.State is NodeState.Dead or NodeState.Empty ? "peer-dead"
                : "both-current";
            Emit("node.credential-rejection", row?.Node ?? peer, new Dictionary<string, string>
            {
                ["verdict"] = verdict, ["reporter"] = reporter ?? "gateway", ["peerEpoch"] = peerEpoch.ToString(),
                ["endpoint"] = peerEndpoint ?? string.Empty,
            });
            return verdict;
        }
    }

    /// <summary>T-0321: the gateway host's name as the hosts file spells it (CLUSTER_GATEWAY_HOST); null = unknown, no check.</summary>
    internal string? GatewayHost { get; set; }

    /// <summary>
    /// T-0321: a node on ANOTHER host than the gateway that advertises a loopback node-link or control endpoint can never
    /// be reached - the gateway would dial its own host's loopback (another process, or nothing). Refused at admission
    /// with a message naming the fix, instead of a node that registers and then silently never links. A node on the
    /// gateway host, or any node while the gateway host is unknown, is not checked (single-host: unchanged).
    /// </summary>
    private void RequireReachableFromGateway(NodeJoinRequest request)
    {
        if (GatewayHost == null || request.Host == null || string.Equals(request.Host, GatewayHost, StringComparison.OrdinalIgnoreCase)) return;
        static bool Loopback(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            string host = StripScheme(endpoint);
            int colon = host.LastIndexOf(':');
            if (colon > 0 && !host.EndsWith(']')) host = host[..colon];
            host = host.Trim('[', ']');
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
        }
        if (Loopback(request.Endpoint) || Loopback(request.ControlEndpoint))
            throw new RegistryConflictException($"Node '{request.Node}' on host '{request.Host}' advertises a loopback endpoint "
                + $"(node link {request.Endpoint}, control {request.ControlEndpoint ?? "none"}) that the gateway on '{GatewayHost}' cannot reach; "
                + "set CLUSTER_NODE_ENDPOINT and CLUSTER_NODE_CONTROL_ENDPOINT to the host's LAN address");
    }

    private static string StripScheme(string endpoint) =>
        (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? endpoint[7..] : endpoint).TrimEnd('/');
}

/// <summary>
/// T-0321: the join and admin tokens of the running start survive a gateway-only restart. Tokens are minted per start
/// (PLAN DECISION 2026-10-01), so a gateway relaunched with a freshly minted environment would otherwise reject every
/// running node's token - and each node exits on credential_rejected: a restart turned into a mass exit.
/// </summary>
/// <remarks>
/// Keyed by <c>CLUSTER_START_ID</c> (minted by the launcher per start, kept for a gateway-only restart). The file
/// (<c>CLUSTER_START_CREDENTIALS</c>, default <c>&lt;registry&gt;/../secrets/start-credentials.json</c>, outside the registry
/// directory so world backups do not copy it) is written mode 0600. Same start id on restart: the persisted join token
/// stays the one the gateway presents and accepts, the persisted admin token stays the one it sends to nodes, and a
/// differing token from the environment is accepted as well (logged by fingerprint). A new start id replaces the file,
/// so the previous start's nodes are rejected and exit. Without a start id nothing is persisted (the pre-T-0321 behaviour).
/// </remarks>
internal static class StartCredentials
{
    internal sealed record Stored(int Version, string StartId, string JoinToken, string? JoinTokenPrevious, string? AdminToken, DateTimeOffset Written);

    public static string? PathFor(GatewayOptions options, Func<string, string?> environment) =>
        environment("CLUSTER_START_CREDENTIALS") is { Length: > 0 } explicitPath ? explicitPath
        : options.RegistryDirectory == null ? null
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.RegistryDirectory).TrimEnd(Path.DirectorySeparatorChar)) ?? ".", "secrets", "start-credentials.json");

    /// <param name="persist">False only in the self-test's control arm (the variant that must fail the restart check).</param>
    public static GatewayOptions Apply(GatewayOptions options, Func<string, string?>? environment = null, bool persist = true)
    {
        Func<string, string?> read = environment ?? Environment.GetEnvironmentVariable;
        string? startId = read("CLUSTER_START_ID");
        string? path = PathFor(options, read);
        if (!persist || string.IsNullOrWhiteSpace(startId) || path == null || options.Admission == null || string.IsNullOrEmpty(options.JoinToken))
            return options;
        Stored? stored = null;
        try
        {
            if (File.Exists(path)) stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            Console.WriteLine($"WARNING: start credentials at {path} are unreadable ({error.GetType().Name}); writing the environment's");
        }
        if (stored != null && stored.StartId == startId && !string.IsNullOrEmpty(stored.JoinToken))
        {
            SecretRedactor.Add([stored.JoinToken, stored.JoinTokenPrevious, stored.AdminToken]);
            bool joinChanged = stored.JoinToken != options.JoinToken, adminChanged = stored.AdminToken != options.AdminToken;
            Console.WriteLine($"Start {startId}: gateway restart keeps the start's credentials (join {ClusterNet.Fingerprint(stored.JoinToken)}, "
                + $"admin {ClusterNet.Fingerprint(stored.AdminToken)})"
                + (joinChanged || adminChanged ? $"; WARNING: the environment brought different tokens (join {ClusterNet.Fingerprint(options.JoinToken)}, "
                    + $"admin {ClusterNet.Fingerprint(options.AdminToken)}), accepted as well - nodes launched from now on should get the start's tokens" : ""));
            return options with
            {
                JoinToken = stored.JoinToken,
                AdminToken = stored.AdminToken ?? options.AdminToken,
                Admission = options.Admission.Accepting(stored.JoinToken, stored.JoinTokenPrevious),
                ExtraAdminTokens = adminChanged && options.AdminToken != null ? [options.AdminToken] : options.ExtraAdminTokens,
            };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp-" + Environment.ProcessId;
        var create = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, create))
            JsonSerializer.Serialize(stream, new Stored(1, startId!, options.JoinToken!, read("CLUSTER_JOIN_TOKEN_PREVIOUS") is { Length: > 0 } previous ? previous : null,
                options.AdminToken, DateTimeOffset.UtcNow));
        File.Move(temporary, path, overwrite: true);
        Console.WriteLine($"Start {startId}: credentials persisted for gateway restarts (join {ClusterNet.Fingerprint(options.JoinToken)}, "
            + $"admin {ClusterNet.Fingerprint(options.AdminToken)})");
        return options;
    }
}

/// <summary>
/// T-0321 P4: the nightly network windows (hosts file v2 <c>network.networkWindows</c>). Inside a window, plus a grace,
/// the lease of a node on another host than the gateway is HELD instead of expiring: a LAN blip must not fence a healthy
/// remote node, whose return would then self-fence it without saving and recover its partitions from replicas. Its
/// partitions stay owned (no recovery claim) and balancing pauses. Outside the windows the normal TTL applies at once,
/// so a host really lost at 04:00 is recovered right after the window. Off unless <c>CLUSTER_NETWORK_WINDOWS</c> is set.
/// </summary>
internal sealed class NetworkWindows(IReadOnlyList<(TimeSpan Start, TimeSpan End)> windows, TimeSpan grace, string gatewayHost, TimeZoneInfo? zone = null)
{
    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> Windows { get; } = windows;
    public TimeSpan Grace { get; } = grace;
    public string GatewayHost { get; } = gatewayHost;
    private readonly TimeZoneInfo _zone = zone ?? TimeZoneInfo.Local;

    /// <summary><c>CLUSTER_NETWORK_WINDOWS</c> = "HH:MM-HH:MM[,...]" in the gateway's local time (a window may cross
    /// midnight); <c>CLUSTER_GATEWAY_HOST</c> (default the machine name); <c>CLUSTER_NETWORK_WINDOW_GRACE_SECONDS</c> (30).</summary>
    public static NetworkWindows? FromEnvironment(Func<string, string?>? environment = null)
    {
        Func<string, string?> read = environment ?? Environment.GetEnvironmentVariable;
        string? value = read("CLUSTER_NETWORK_WINDOWS");
        if (string.IsNullOrWhiteSpace(value)) return null;
        var windows = new List<(TimeSpan, TimeSpan)>();
        foreach (string item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = item.Split('-');
            if (parts.Length != 2 || !TimeSpan.TryParseExact(parts[0].Trim(), @"hh\:mm", null, out TimeSpan start)
                || !TimeSpan.TryParseExact(parts[1].Trim(), @"hh\:mm", null, out TimeSpan end) || start == end)
                throw new ArgumentException($"CLUSTER_NETWORK_WINDOWS entry '{item}' is not HH:MM-HH:MM");
            windows.Add((start, end));
        }
        string? graceValue = read("CLUSTER_NETWORK_WINDOW_GRACE_SECONDS");
        double graceSeconds = 30;
        if (!string.IsNullOrWhiteSpace(graceValue) && (!double.TryParse(graceValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out graceSeconds) || graceSeconds < 0))
            throw new ArgumentException("CLUSTER_NETWORK_WINDOW_GRACE_SECONDS must be a non-negative number");
        string host = read("CLUSTER_GATEWAY_HOST") is { Length: > 0 } named ? named : Environment.MachineName;
        return new NetworkWindows(windows, TimeSpan.FromSeconds(graceSeconds), host);
    }

    /// <summary>Inside a window or its grace, in the gateway's local time.</summary>
    public bool Active(DateTimeOffset now)
    {
        TimeSpan day = TimeSpan.FromDays(1), local = TimeZoneInfo.ConvertTime(now, _zone).TimeOfDay;
        foreach ((TimeSpan start, TimeSpan end) in Windows)
        {
            TimeSpan length = (end - start + day).Ticks % day.Ticks == 0 ? day : TimeSpan.FromTicks((end - start + day).Ticks % day.Ticks);
            TimeSpan into = TimeSpan.FromTicks((local - start + day).Ticks % day.Ticks);
            if (into < length + Grace) return true;
        }
        return false;
    }

    public bool Remote(string? host) => host != null && !string.Equals(host, GatewayHost, StringComparison.OrdinalIgnoreCase);

    public bool Holds(NodeRegistration node, DateTimeOffset now) => Remote(node.Host) && Active(now);
}

internal sealed partial class GatewayRegistry
{
    /// <summary>T-0321 P4: set from CLUSTER_NETWORK_WINDOWS by the gateway; null (default) = no hold.</summary>
    internal NetworkWindows? LeaseHold { get; set; }
    private long _leaseHoldLogged;

    /// <summary>Whether this node's lease is held right now: its host reports its process running (#1218,
    /// <see cref="LiveProcessHeld"/>), or it is inside a network window, on a remote host. Caller holds _lock.</summary>
    private bool LeaseHeld(NodeRegistration node, DateTimeOffset now)
    {
        if (LiveProcessHeld(node, now)) return true;
        if (LeaseHold?.Holds(node, now) != true) return false;
        if (node.LeaseExpires <= now && Interlocked.Exchange(ref _leaseHoldLogged, now.ToUnixTimeSeconds() / 60) != now.ToUnixTimeSeconds() / 60)
            Console.WriteLine($"network window: holding the lapsed lease of remote node {node.Node} epoch {node.Epoch} (host {node.Host}, "
                + $"lease expired {node.LeaseExpires:O}); no recovery until the window and its grace end");
        return true;
    }

    /// <summary>An executor lease of a remote host is held inside a window as well (its host agent keeps its session).</summary>
    private bool ExecutorLeaseHeld(string host, DateTimeOffset now) => LeaseHold is { } hold && hold.Remote(host) && hold.Active(now);

    private readonly HashSet<ulong> _heldDeferredRecoveries = [];

    /// <summary>
    /// T-0296 r2: a newer save of the partition than the newest USABLE one is held by a node whose lease is held.
    /// HolderUsable reads a held holder as unusable, so recovery would otherwise restore an older save (everything since
    /// lost) instead of waiting the hold out - the hold's own promise is "no recovery until the window ends". #1282: the
    /// same for the live-process hold (#1218): a frozen node whose host still reports it running holds the newest save
    /// for up to LiveProcessGrace, and recovery waits that out rather than restore an older save. Caller holds _lock.
    /// </summary>
    private bool NewerSaveHeld(ulong partition, DateTimeOffset now)
    {
        if (LeaseHold is null && LiveProcessGrace <= TimeSpan.Zero) return false;
        SaveRegistration? usable = Newest(partition, long.MaxValue);
        bool held = SavesOf(partition).Any(save => !save.PendingDelete && (usable == null || save.SaveId.CompareTo(usable.SaveId) > 0)
            && save.LocationHolders.Values.Any(holder => !save.CorruptHolders.Contains(holder)
                // T-0340: a host holder is held when its store is (every node of the host lapsed, one of them held).
                && (_hostStore && IsHostHolder(holder) ? StoreState(HostOfHolder(holder)!) == HostStoreState.Held
                    // Step 6: a released holder's files lie on its host; they are held when that host's nodes are.
                    : ReleasedHolderHost(holder) is { } released ? StoreState(released) == HostStoreState.Held
                    : _state.Nodes.TryGetValue(holder, out NodeRegistration? row) && row.State is NodeState.Active or NodeState.Closed
                        && LeaseHeld(row, now))));
        if (!held) _heldDeferredRecoveries.Remove(partition);
        else if (_heldDeferredRecoveries.Add(partition))
            Console.WriteLine($"lease hold: recovery of partition {partition} waits - its newest save is held by a node whose lease is "
                + $"held (network window or live process) (the newest usable save is {(usable == null ? "none" : usable.SaveId.ToString())})");
        return held;
    }

    /// <summary>
    /// T-0327: the nodes whose lease has lapsed and is held by a network window right now. Such a node is unreachable
    /// by definition, so it is neither source nor target of an automatic handover (W3 2026-10-01: co-location handed
    /// four partitions, one with a client, INTO held node-99, which was closed as Dead six minutes later).
    /// </summary>
    public IReadOnlySet<string> LapsedHeldNodes()
    {
        lock (_lock)
        {
            DateTimeOffset now = _clock();
            if (LeaseHold is not { } hold || !hold.Active(now)) return EmptyNodeSet;
            return _state.Nodes.Values.Where(x => LapsedHeld(x, now)).Select(x => x.Node).ToHashSet();
        }
    }

    /// <summary>#1277: one node of <see cref="LapsedHeldNodes"/>. Caller holds _lock.</summary>
    private bool LapsedHeld(NodeRegistration node, DateTimeOffset now) => LeaseHold is { } hold && hold.Active(now)
        && node.State is NodeState.Active or NodeState.Closed && hold.Remote(node.Host) && node.LeaseExpires <= now;

    private static readonly IReadOnlySet<string> EmptyNodeSet = new HashSet<string>();

    /// <summary>For the balancer: a window is active and some remote node's lease has lapsed - its partitions are frozen.</summary>
    public bool LeaseHoldActive()
    {
        lock (_lock)
        {
            DateTimeOffset now = _clock();
            return LeaseHold is { } hold && hold.Active(now)
                && _state.Nodes.Values.Any(x => x.State is NodeState.Active or NodeState.Closed && hold.Remote(x.Host) && x.LeaseExpires <= now);
        }
    }
}
