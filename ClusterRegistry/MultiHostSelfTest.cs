using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ClusterProtocol;

namespace ClusterRegistry;

/// <summary>T-0321 multi-host: node addressing and credentials (ClusterNet.cs, shared with the node), the gateway's
/// dual control prefix, the credential-rejection rule, start-credential persistence, host-aware replica placement
/// and the network-window lease hold.</summary>
internal static partial class SelfTest
{
    private static void MultiHostCheck(bool value, string section, string what)
    {
        if (!value) throw new InvalidOperationException(section + ": " + what);
    }

    /// <summary>A non-loopback IPv4 address of this machine, or null (then the live-listener half is skipped and says so).</summary>
    private static IPAddress? OwnLanAddress() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address))
        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

    /// <summary>
    /// The node registers the ADVERTISED control endpoint (CLUSTER_NODE_CONTROL_ENDPOINT), listens on the loopback
    /// prefix beside its LAN bind, and decides the credential per request from the prefix it arrived on. With no
    /// address configured every answer is the pre-T-0321 one.
    /// </summary>
    private static void TestMultiHostNodeAddressing()
    {
        const string S = "multihost-node-addressing";
        // Registration: bind != advertise -> the row carries the advertised value (red before T-0321: the bind).
        MultiHostCheck(ClusterNet.AdvertisedControlEndpoint("http://192.168.1.80:29417/", "127.0.0.1", 29417) == "http://192.168.1.80:29417",
            S, "the advertised endpoint wins over the bind: " + ClusterNet.AdvertisedControlEndpoint("http://192.168.1.80:29417/", "127.0.0.1", 29417));
        MultiHostCheck(ClusterNet.AdvertisedControlEndpoint("http://192.168.1.80:29417", null, 1) == "http://192.168.1.80:29417", S, "advertised without a bind");
        // Unchanged single-host values.
        MultiHostCheck(ClusterNet.AdvertisedControlEndpoint(null, null, 29417) == "http://127.0.0.1:29417", S, "no address: loopback as before");
        MultiHostCheck(ClusterNet.AdvertisedControlEndpoint("", "10.42.0.7", 29418) == "http://10.42.0.7:29418", S, "bind only: the bind as before");
        AssertArgument(() => ClusterNet.AdvertisedControlEndpoint("192.168.1.80:29417", null, 1));

        // Prefixes: one loopback prefix unchanged; a LAN bind adds loopback.
        MultiHostCheck(ClusterNet.ControlPrefixes(IPAddress.Loopback, 29417, "/cluster/").SequenceEqual(["http://127.0.0.1:29417/cluster/"]), S, "loopback: one prefix");
        MultiHostCheck(ClusterNet.ControlPrefixes(IPAddress.Parse("192.168.1.80"), 29417, "/cluster/")
            .SequenceEqual(["http://127.0.0.1:29417/cluster/", "http://192.168.1.80:29417/cluster/"]), S, "LAN: loopback + LAN");

        // Access matrix. Loopback arrival or no auth configured -> open, as before. LAN arrival -> peer routes take
        // the join token, everything else the admin token; grant routes keep the registry grant.
        var access = ClusterNet.ControlAccess.Open;
        foreach ((string path, string method, ClusterNet.ControlAccess wanted) in new[]
        {
            ("/cluster/global-save-file", "GET", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/voxel-base-file", "GET", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/snapshot", "GET", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/partitions", "GET", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/partitions", "POST", ClusterNet.ControlAccess.Admin),
            ("/cluster/igc-deliver", "POST", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/mod-deliver", "POST", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/mod-locate", "GET", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/spawn-execute", "POST", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/transfer-accept-execute", "POST", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/respawn-execute", "POST", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/partition-of", "GET", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/voxel-journal/append", "POST", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/voxel-journal", "GET", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/voxel-journal-tails", "GET", ClusterNet.ControlAccess.JoinOrAdmin),
            ("/cluster/save-file", "GET", ClusterNet.ControlAccess.Grant),
            ("/cluster/prepare-import", "POST", ClusterNet.ControlAccess.Grant),
            ("/cluster/import", "POST", ClusterNet.ControlAccess.Grant),
            ("/cluster/capture", "POST", ClusterNet.ControlAccess.Admin),
            ("/cluster/diag", "GET", ClusterNet.ControlAccess.Admin),
            ("/cluster/config", "PUT", ClusterNet.ControlAccess.Admin),
            ("/cluster/trigger/save", "POST", ClusterNet.ControlAccess.Admin),
        })
        {
            access = ClusterNet.NodeControlAccess(arrivedOnLoopback: false, authConfigured: true, path, method);
            MultiHostCheck(access == wanted, S, $"{method} {path} over the LAN needs {wanted}, got {access}");
            ClusterNet.ControlAccess local = ClusterNet.NodeControlAccess(arrivedOnLoopback: true, authConfigured: true, path, method);
            MultiHostCheck(local == (wanted == ClusterNet.ControlAccess.Grant ? ClusterNet.ControlAccess.Grant : ClusterNet.ControlAccess.Open),
                S, $"{method} {path} over loopback stays as before, got {local}");
            MultiHostCheck(ClusterNet.NodeControlAccess(false, authConfigured: false, path, method)
                == (wanted == ClusterNet.ControlAccess.Grant ? ClusterNet.ControlAccess.Grant : ClusterNet.ControlAccess.Open), S, "no token configured: open");
        }
        // Admission: a node on another host than the gateway may not advertise loopback (the gateway could never reach it).
        string admissionDirectory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            using var registry = new GatewayRegistry(admissionDirectory, Admission, leaseTtl: TimeSpan.FromSeconds(10));
            NodeJoinRequest remote = Join("r1", "127.0.0.1:28417", Guid.NewGuid()) with { Host = "ai", ControlEndpoint = "http://192.168.1.80:29417" };
            registry.RegisterNode(remote with { Node = "unknown-gateway-host" });   // gateway host unknown: not checked
            registry.GatewayHost = "polaris";
            AssertConflict(() => registry.RegisterNode(remote));
            AssertConflict(() => registry.RegisterNode(remote with { Endpoint = "192.168.1.80:28417", ControlEndpoint = "http://127.0.0.1:29417" }));
            AssertConflict(() => registry.RegisterNode(remote with { Endpoint = "localhost:28417" }));
            registry.RegisterNode(remote with { Endpoint = "192.168.1.80:28417" });
            registry.RegisterNode(Join("p1", "127.0.0.1:28418", Guid.NewGuid()) with { Host = "polaris", ControlEndpoint = "http://127.0.0.1:29418" });
        }
        finally { Directory.Delete(admissionDirectory, true); }

        MultiHostCheck(ClusterNet.TokenMatches(CurrentToken, [null, "", PreviousToken, CurrentToken]) && !ClusterNet.TokenMatches("x", [CurrentToken])
            && !ClusterNet.TokenMatches(null, [CurrentToken]) && !ClusterNet.TokenMatches("", [null, ""]), S, "token matching");
        MultiHostCheck(ClusterNet.Bearer("Bearer abc") == "abc" && ClusterNet.Bearer("Basic abc") == null && ClusterNet.Bearer("Bearer ") == null, S, "bearer");
        MultiHostCheck(ClusterNet.Fingerprint(CurrentToken).Length == 12 && !CurrentToken.Contains(ClusterNet.Fingerprint(CurrentToken))
            && ClusterNet.Fingerprint(null) == "none", S, "fingerprint");

        // The live half: one HttpListener with both prefixes answers on each, and LocalEndPoint tells them apart -
        // the per-request decision the node control server and the gateway make.
        IPAddress? lan = OwnLanAddress();
        if (lan == null)
        {
            Console.WriteLine(S + ": no non-loopback IPv4 address on this machine; dual-prefix listener half skipped");
            return;
        }
        int port = FreeTcpPort();
        using var listener = new HttpListener();
        foreach (string prefix in ClusterNet.ControlPrefixes(lan, port, "/cluster/")) listener.Prefixes.Add(prefix);
        listener.Start();
        var seen = new List<bool>();
        var server = new Thread(() =>
        {
            for (int i = 0; i < 2; i++)
            {
                HttpListenerContext context = listener.GetContext();
                bool loopback = IPAddress.IsLoopback(context.Request.LocalEndPoint.Address);
                lock (seen) seen.Add(loopback);
                byte[] body = System.Text.Encoding.UTF8.GetBytes(loopback ? "loopback" : "lan");
                context.Response.OutputStream.Write(body);
                context.Response.Close();
            }
        }) { IsBackground = true };
        server.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string viaLoopback = http.GetStringAsync($"http://127.0.0.1:{port}/cluster/node-state").GetAwaiter().GetResult();
        string viaLan = http.GetStringAsync($"http://{lan}:{port}/cluster/node-state").GetAwaiter().GetResult();
        server.Join(5000);
        MultiHostCheck(viaLoopback == "loopback" && viaLan == "lan", S, $"dual prefix: loopback answered '{viaLoopback}', {lan} answered '{viaLan}'");
        Console.WriteLine($"{S}: dual-prefix listener on 127.0.0.1 and {lan} told the arrivals apart");
    }

    /// <summary>A running gateway for the multi-host HTTP sections: control on this machine's LAN address when it has
    /// one (then the automatic loopback prefix exists too), else loopback; tokens on, so the LAN prefix authenticates.</summary>
    private sealed class MultiHostGateway : IDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "cluster-multihost-" + Guid.NewGuid());
        public readonly DirectGateway Gateway;
        public readonly IPAddress ControlAddress;
        public readonly int Port = FreeTcpPort();
        public readonly bool Dual;
        private readonly Thread _thread;
        public const string AdminToken = PreviousToken;   // the join token is CurrentToken (Admission)

        public MultiHostGateway(Func<GatewayOptions, GatewayOptions>? adjust = null, string? directory = null)
        {
            if (directory != null) Directory = directory;
            IPAddress? lan = OwnLanAddress();
            Dual = lan != null;
            ControlAddress = lan ?? IPAddress.Loopback;
            IpNetwork[] networks = lan == null ? [] : [new IpNetwork(lan, 32)];
            var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()),
                new(ControlAddress, Port), false, null, null, null, Directory, TimeSpan.FromSeconds(10), Admission, AdminToken, networks,
                JoinToken: CurrentToken);
            Gateway = new DirectGateway(adjust == null ? options : adjust(options));
            _thread = new Thread(() =>
            {
                try { Gateway.Run(); }
                catch (Exception e) { Console.Error.WriteLine("multi-host gateway failed: " + e); }
            }) { IsBackground = true, Name = "Gateway-MultiHost-Self-Test" };
            _thread.Start();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            Stopwatch waited = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    using var probe = new HttpRequestMessage(HttpMethod.Get, Url("/admin/v1/health"));
                    probe.Headers.Authorization = new("Bearer", AdminToken);
                    using HttpResponseMessage answer = http.SendAsync(probe).GetAwaiter().GetResult();
                    break;   // any answer: the listener is up (the probe's token need not be this gateway's)
                }
                catch (HttpRequestException) when (waited.Elapsed < TimeSpan.FromSeconds(10)) { }
                if (waited.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("multi-host gateway did not start");
                Thread.Sleep(50);
            }
        }

        public string Url(string path, bool loopback = false) => $"http://{(loopback ? "127.0.0.1" : ControlAddress.ToString())}:{Port}{path}";

        public (int Status, string Body) Send(HttpMethod method, string path, string? bearer = null, string? json = null, bool loopback = false)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(method, Url(path, loopback));
            if (bearer != null) request.Headers.Authorization = new("Bearer", bearer);
            if (json != null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult();
            return ((int)response.StatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }

        public void Dispose()
        {
            Gateway.Stop();
            _thread.Join(2000);
            Gateway.Dispose();
        }
    }

    private static string JoinJson(string node, string control) => JsonSerializer.Serialize(new NodeJoinRequest(node, node + "-endpoint:1", Guid.NewGuid(),
        CurrentToken, "cluster", "world", "config", "binary", "types", "settings", 3000, ControlEndpoint: control, Host: "ai", Pid: 4242));

    /// <summary>
    /// The stale-node credential rule, gateway half. The registry answers a stale token with 401 credential_rejected (the
    /// node exits on it); a missing token, a scope mismatch, a conflict and a 5xx carry nothing a node exits on. A peer's
    /// rejection is judged by the registry: a stale REPORTER gets 403 stale_incarnation (and exits), a stale peer is named,
    /// two current incarnations are nobody's fault. Every refusal is logged with the token fingerprint, never the token.
    /// </summary>
    private static void TestMultiHostCredentialRule()
    {
        const string S = "multihost-credential-rule";
        const string StaleToken = "c3RhbGUtdG9rZW4tZnJvbS1hbm90aGVyLXN0YXJ0LTAxMjM0NQ==";
        // The node's decision, as a pure function of the answer.
        MultiHostCheck(ClusterNet.OnAnswer(true, 401, """{"code":"credential_rejected"}""") == ClusterNet.CredentialAction.ExitNode, S, "registry credential_rejected exits");
        MultiHostCheck(ClusterNet.OnAnswer(true, 403, """{"code":"stale_incarnation"}""") == ClusterNet.CredentialAction.ExitNode, S, "registry stale_incarnation exits");
        MultiHostCheck(ClusterNet.OnAnswer(true, 403, """{"code":"node_id_retired"}""") == ClusterNet.CredentialAction.ExitNode, S, "registry node_id_retired exits");
        MultiHostCheck(ClusterNet.OnAnswer(false, 401, """{"code":"credential_rejected"}""") == ClusterNet.CredentialAction.ReportPeer, S, "a peer's rejection is only reported");
        foreach ((int status, string body) in new[] { (401, """{"code":"credential_required"}"""), (403, """{"code":"admin_scope_required"}"""),
            (401, """{"error":"bearer token required"}"""), (401, "Unauthorized"), (500, """{"code":"credential_rejected"}"""),
            (503, ""), (409, """{"code":"stale_incarnation"}"""), (200, """{"code":"credential_rejected"}"""), (401, "") })
            MultiHostCheck(ClusterNet.OnAnswer(true, status, body) == ClusterNet.CredentialAction.None, S, $"{status} {body} is no rejection");

        TextWriter console = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            using var f = new MultiHostGateway();
            string heartbeat = """{"Node":"ghost","Epoch":1,"Ownerships":[]}""";
            (int status, string body) = f.Send(HttpMethod.Post, "/registry/nodes/heartbeat", StaleToken, heartbeat);
            MultiHostCheck(status == 401 && ClusterNet.OnAnswer(true, status, body) == ClusterNet.CredentialAction.ExitNode,
                S, $"a stale token must be a terminal rejection: {status} {body}");
            (status, body) = f.Send(HttpMethod.Post, "/registry/nodes/heartbeat", null, heartbeat);
            MultiHostCheck(status == 401 && ClusterNet.OnAnswer(true, status, body) == ClusterNet.CredentialAction.None, S, $"no token: not terminal: {status} {body}");
            (status, body) = f.Send(HttpMethod.Get, "/diag", CurrentToken);
            MultiHostCheck(status == 403 && ClusterNet.OnAnswer(true, status, body) == ClusterNet.CredentialAction.None,
                S, $"the join token on an admin route is a scope refusal, not terminal: {status} {body}");
            (status, body) = f.Send(HttpMethod.Post, "/registry/nodes/heartbeat", CurrentToken, heartbeat);
            MultiHostCheck(status is not (401 or 403) && ClusterNet.OnAnswer(true, status, body) == ClusterNet.CredentialAction.None,
                S, $"a current token on a conflict is not a rejection: {status} {body}");
            // The control-liveness reconnect a node sends (ControlReassertService) - 401 before T-0321 on a LAN gateway.
            (status, body) = f.Send(HttpMethod.Post, "/admission/kick?reason=reconnect&user=1", CurrentToken);
            MultiHostCheck(status is not (401 or 403), S, $"a node's reconnect kick is authorized by the join token: {status} {body}");
            (status, body) = f.Send(HttpMethod.Post, "/admission/kick?user=1", CurrentToken);
            MultiHostCheck(status == 403, S, $"an admin kick still needs the admin token: {status} {body}");
            if (f.Dual)
            {
                (status, body) = f.Send(HttpMethod.Get, "/balance/status", loopback: true);
                MultiHostCheck(status == 200, S, $"the automatic loopback prefix stays open: {status} {body}");
                (status, body) = f.Send(HttpMethod.Get, "/admin/v1/capabilities", loopback: true);
                MultiHostCheck(status == 200, S, $"loopback manage actor on the admin contract: {status} {body}");
                (status, body) = f.Send(HttpMethod.Get, "/balance/status");
                MultiHostCheck(status == 401, S, $"the LAN prefix needs a token: {status} {body}");
                (status, body) = f.Send(HttpMethod.Post, ExecutorRoute, loopback: true, json: "{}");
                MultiHostCheck(status == 403, S, $"the executor route keeps its no-exemption rule on loopback: {status} {body}");
            }
            else console.WriteLine(S + ": no LAN address; loopback-exemption half skipped (the gateway runs on one loopback prefix)");

            // Peer rejections, judged by the registry.
            MultiHostCheck(f.Send(HttpMethod.Post, "/registry/nodes", CurrentToken, JoinJson("reporter", "http://10.9.9.1:29417")).Status == 200, S, "register reporter");
            MultiHostCheck(f.Send(HttpMethod.Post, "/registry/nodes", CurrentToken, JoinJson("peer", "http://10.9.9.2:29418")).Status == 200, S, "register peer");
            string Report(long reporterEpoch, string? peer, long peerEpoch, string endpoint) => JsonSerializer.Serialize(new
            {
                Reporter = "reporter", ReporterEpoch = reporterEpoch, PeerEndpoint = endpoint, Route = "/cluster/global-save-file",
                Code = ClusterNet.CredentialRejected, Peer = peer, PeerEpoch = peerEpoch, TokenFingerprint = ClusterNet.Fingerprint(CurrentToken),
            });
            (status, body) = f.Send(HttpMethod.Post, "/registry/peer-rejections", CurrentToken, Report(7, "peer", 1, "http://10.9.9.2:29418"));
            MultiHostCheck(status == 403 && ClusterNet.OnAnswer(true, status, body) == ClusterNet.CredentialAction.ExitNode,
                S, $"a stale reporter is told so, and exits: {status} {body}");
            (status, body) = f.Send(HttpMethod.Post, "/registry/peer-rejections", CurrentToken, Report(1, "peer", 3, "http://10.9.9.2:29418"));
            MultiHostCheck(status == 200 && body.Contains("peer-stale-incarnation"), S, $"an old incarnation of the peer: {status} {body}");
            (status, body) = f.Send(HttpMethod.Post, "/registry/peer-rejections", CurrentToken, Report(1, null, 0, "http://10.9.9.2:29418/"));
            MultiHostCheck(status == 200 && body.Contains("both-current"), S, $"two current incarnations, found by endpoint: {status} {body}");
            (status, body) = f.Send(HttpMethod.Post, "/registry/peer-rejections", CurrentToken, Report(1, null, 0, "http://10.9.9.9:1"));
            MultiHostCheck(status == 200 && body.Contains("peer-unknown"), S, $"nobody registered at the endpoint: {status} {body}");

            (status, body) = f.Send(HttpMethod.Get, "/diag?depth=cluster&registry=summary", MultiHostGateway.AdminToken);
            using JsonDocument diag = JsonDocument.Parse(body);
            JsonElement rejections = diag.RootElement.GetProperty("registry").GetProperty("credentialRejections");
            MultiHostCheck(rejections.GetProperty("count").GetInt64() >= 7, S, "diag counts the refusals and reports: " + rejections);
            MultiHostCheck(body.Contains(ClusterNet.Fingerprint(StaleToken)) && !body.Contains(StaleToken) && !body.Contains(CurrentToken),
                S, "diag carries fingerprints, never tokens");
        }
        finally { Console.SetOut(console); }
        string log = captured.ToString();
        MultiHostCheck(log.Contains("token_fp=" + ClusterNet.Fingerprint(StaleToken)) && log.Contains("node=ghost") == false
            && !log.Contains(StaleToken) && !log.Contains(CurrentToken) && !log.Contains(MultiHostGateway.AdminToken),
            S, "the refusal log names the fingerprint and never a token");
    }

    private const string ExecutorRoute = CometWorks.ClusterGateway.AdminContract.V1.ExecutorProtocol.HeartbeatRoute;

    /// <summary>
    /// A gateway-only restart within one start never becomes a mass exit: the restarted gateway (whose environment
    /// brought re-minted tokens) keeps accepting the running nodes' join token. The control arm - the same restart
    /// without persistence - must fail exactly that check (every running node would get credential_rejected and exit),
    /// which is what makes the green mean something. A NEW start id rejects the previous start's nodes.
    /// </summary>
    private static void TestMultiHostStartCredentials()
    {
        const string S = "multihost-start-credentials";
        const string ReMinted = "cmUtbWludGVkLWpvaW4tdG9rZW4tYWZ0ZXItcmVzdGFydC0wMTI=";
        const string ReMintedAdmin = "cmUtbWludGVkLWFkbWluLXRva2VuLWFmdGVyLXJlc3RhcnQtMDE=";
        string root = Path.Combine(Path.GetTempPath(), "cluster-start-credentials-" + Guid.NewGuid());
        string file = Path.Combine(root, "secrets", "start-credentials.json");
        string registry = Path.Combine(root, "gateway-registry");
        Func<string, string?> Env(string? startId) => name => name switch
        {
            "CLUSTER_START_ID" => startId,
            "CLUSTER_START_CREDENTIALS" => file,
            _ => null,
        };
        GatewayOptions Options(string join, string admin, IPEndPoint? control = null, IpNetwork[]? networks = null) => new(
            new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()), control, false, null, null, null, registry,
            TimeSpan.FromSeconds(10), new NodeAdmission(join, null, "cluster", "world", "config", "binary", "types", "settings", 3000), admin,
            networks ?? [], JoinToken: join);
        TextWriter console = Console.Out;
        Console.SetOut(new StringWriter());
        try
        {
            // Default location is beside the registry directory, never inside it (world backups copy the registry).
            MultiHostCheck(StartCredentials.PathFor(Options(CurrentToken, MultiHostGateway.AdminToken), _ => null)
                == Path.Combine(root, "secrets", "start-credentials.json"), S, "default path <registry>/../secrets");
            // No start id: nothing persisted, options untouched (pre-T-0321).
            GatewayOptions untouched = Options(CurrentToken, MultiHostGateway.AdminToken);
            MultiHostCheck(ReferenceEquals(StartCredentials.Apply(untouched, Env(null)), untouched) && !File.Exists(file), S, "no start id: no persistence");

            // First start: persisted, mode 0600, unchanged options.
            GatewayOptions first = Options(CurrentToken, MultiHostGateway.AdminToken);
            MultiHostCheck(ReferenceEquals(StartCredentials.Apply(first, Env("start-1")), first) && File.Exists(file), S, "first start persists");
            if (!OperatingSystem.IsWindows())
                MultiHostCheck(File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), S, "the file is 0600: " + File.GetUnixFileMode(file));

            // Gateway-only restart with re-minted tokens, same start: the running nodes' token is still the gateway's.
            GatewayOptions restarted = StartCredentials.Apply(Options(ReMinted, ReMintedAdmin), Env("start-1"));
            MultiHostCheck(restarted.JoinToken == CurrentToken && restarted.AdminToken == MultiHostGateway.AdminToken
                && restarted.Admission!.AcceptsToken(CurrentToken) && restarted.Admission.AcceptsToken(ReMinted)
                && restarted.ExtraAdminTokens!.SequenceEqual([ReMintedAdmin]), S, "restart keeps the start's tokens and accepts the new ones");
            GatewayOptions controlArm = StartCredentials.Apply(Options(ReMinted, ReMintedAdmin), Env("start-1"), persist: false);

            // Over HTTP, as a running node sees it after the restart.
            string heartbeat = """{"Node":"ghost","Epoch":1,"Ownerships":[]}""";
            ClusterNet.CredentialAction Survives(GatewayOptions options)
            {
                using var gateway = new MultiHostGateway(options2 => options with { Control = options2.Control, InternalNetworks = options2.InternalNetworks },
                    Path.Combine(root, "run-" + Guid.NewGuid()));
                (int status, string body) = gateway.Send(HttpMethod.Post, "/registry/nodes/heartbeat", CurrentToken, heartbeat);
                return ClusterNet.OnAnswer(true, status, body);
            }
            ClusterNet.CredentialAction persisted = Survives(restarted), unpersisted = Survives(controlArm);
            MultiHostCheck(unpersisted == ClusterNet.CredentialAction.ExitNode,
                S, "control arm: without persistence the restart must reject the running nodes' token (else this test proves nothing): " + unpersisted);
            MultiHostCheck(persisted == ClusterNet.CredentialAction.None, S, "a gateway-only restart must not make a running node exit: " + persisted);

            // A new start: the previous start's token is refused, the file carries the new start.
            GatewayOptions next = StartCredentials.Apply(Options(ReMinted, ReMintedAdmin), Env("start-2"));
            MultiHostCheck(next.JoinToken == ReMinted && !next.Admission!.AcceptsToken(CurrentToken) && Survives(next) == ClusterNet.CredentialAction.ExitNode
                && File.ReadAllText(file).Contains("start-2"), S, "a new start id rejects the previous start's nodes");
        }
        finally
        {
            Console.SetOut(console);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// P3: while more than one host serves, a save's second holder is on another host. A node on the only holders' host is
    /// not handed the copy (it would add a holder, no spread); a node on another host is, and a save with two holders on
    /// ONE host still needs a third on another. /diag's replicaHosts counts the newest saves held on one host only.
    /// With one host every answer is the old one (the queue/brute-force verifier checks each answer as well).
    /// </summary>
    private static void TestMultiHostReplicaHosts()
    {
        const string S = "multihost-replica-hosts";
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(600));
            NodeRegistration On(string node, string host) =>
                registry.RegisterNode(Join(node, node + "-ep", Guid.NewGuid()) with { Host = host, ControlEndpoint = "http://" + node });
            NodeRegistration a1 = On("a1", "ai"), a2 = On("a2", "ai"), p1 = On("p1", "polaris");
            PartitionOwnership one = registry.ClaimPartition(1, a1.Node, a1.Epoch, 0);
            registry.RegisterSave(new SaveId(1, one.Generation, 1), a1.Node, a1.Epoch, SaveKind.Periodic, 1, "a", 1, "a1/1", "http://a1");
            now += TimeSpan.FromSeconds(1);
            PartitionOwnership two = registry.ClaimPartition(2, a1.Node, a1.Epoch, 0);
            registry.RegisterSave(new SaveId(2, two.Generation, 1), a1.Node, a1.Epoch, SaveKind.Periodic, 1, "b", 2, "a1/2", "http://a1");
            registry.RegisterHolder(new SaveId(2, two.Generation, 1), a2.Node, a2.Epoch, 1, "b", "a2/2", "http://a2");
            JsonElement Spread() => JsonDocument.Parse(JsonSerializer.Serialize(registry.ReplicaHostSpread())).RootElement;
            now += TimeSpan.FromSeconds(11);   // past the diag cache
            MultiHostCheck(Spread().GetProperty("multiHost").GetBoolean() && Spread().GetProperty("singleHostNewestSaves").GetInt32() == 2,
                S, "both newest saves are held on ai only: " + Spread());

            ReplicaDirective[] sameHost = registry.ReplicaWork(a2.Node, a2.Epoch);
            MultiHostCheck(sameHost.Length == 0, S, "a node on the only holder's host gets no copy: " + string.Join(",", sameHost.Select(x => x.SaveId)));
            ReplicaDirective first = registry.ReplicaWork(p1.Node, p1.Epoch).Single();
            registry.RegisterHolder(first.SaveId, p1.Node, p1.Epoch, 1, first.Checksum, "p1/x", "http://p1");
            ReplicaDirective second = registry.ReplicaWork(p1.Node, p1.Epoch).Single();
            MultiHostCheck(new[] { first.SaveId.Partition, second.SaveId.Partition }.Order().SequenceEqual([1UL, 2UL]),
                S, $"the other host copies both, including the save with two holders on one host: {first.SaveId} {second.SaveId}");
            registry.RegisterHolder(second.SaveId, p1.Node, p1.Epoch, 1, second.Checksum, "p1/y", "http://p1");
            MultiHostCheck(registry.ReplicaWork(p1.Node, p1.Epoch).Length == 0 && registry.ReplicaWork(a2.Node, a2.Epoch).Length == 0,
                S, "spread on two hosts: nothing left");
            now += TimeSpan.FromSeconds(11);
            MultiHostCheck(Spread().GetProperty("singleHostNewestSaves").GetInt32() == 0, S, "diag after the copies: " + Spread());
        }
        finally { Directory.Delete(directory, true); }

        // One host: the old rule, a same-host peer takes the copy and two holders are enough.
        directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(600));
            NodeRegistration x = registry.RegisterNode(Join("x", "x-ep", Guid.NewGuid()) with { Host = "polaris" });
            NodeRegistration y = registry.RegisterNode(Join("y", "y-ep", Guid.NewGuid()) with { Host = "polaris" });
            PartitionOwnership one = registry.ClaimPartition(1, x.Node, x.Epoch, 0);
            registry.RegisterSave(new SaveId(1, one.Generation, 1), x.Node, x.Epoch, SaveKind.Periodic, 1, "a", 1, "x/1", "http://x");
            ReplicaDirective copy = registry.ReplicaWork(y.Node, y.Epoch).Single();
            registry.RegisterHolder(copy.SaveId, y.Node, y.Epoch, 1, copy.Checksum, "y/1", "http://y");
            JsonElement spread = JsonDocument.Parse(JsonSerializer.Serialize(registry.ReplicaHostSpread())).RootElement;
            MultiHostCheck(registry.ReplicaWork(x.Node, x.Epoch).Length == 0 && !spread.GetProperty("multiHost").GetBoolean()
                && spread.GetProperty("singleHostNewestSaves").GetInt32() == 0, S, "one host: unchanged: " + spread);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// P4: inside a configured network window (plus its grace) a REMOTE node's lapsed lease is held - not Dead, its
    /// partition still owned, its returning heartbeat accepted (so it never self-fences without saving) - while a node on
    /// the gateway host still dies at its TTL. Past the window the normal TTL applies at once. The remote host's executor
    /// lease is held the same way. Control arm: the same silence without the window config kills the remote node, and its
    /// returning heartbeat is refused (the self-fence).
    /// </summary>
    private static void TestMultiHostNetworkWindow()
    {
        const string S = "multihost-network-window";
        // Parsing and the clock arithmetic (a window may cross midnight; the grace extends it).
        NetworkWindows parsed = NetworkWindows.FromEnvironment(name => name switch
        {
            "CLUSTER_NETWORK_WINDOWS" => "03:50-04:08, 23:55-00:05",
            "CLUSTER_GATEWAY_HOST" => "polaris",
            _ => null,
        })!;
        MultiHostCheck(parsed.Windows.Count == 2 && parsed.Grace == TimeSpan.FromSeconds(30) && parsed.GatewayHost == "polaris", S, "parse");
        MultiHostCheck(NetworkWindows.FromEnvironment(_ => null) == null, S, "unset: off");
        AssertArgument(() => NetworkWindows.FromEnvironment(name => name == "CLUSTER_NETWORK_WINDOWS" ? "4:00" : null));
        var utc = new NetworkWindows(parsed.Windows, parsed.Grace, "polaris", TimeZoneInfo.Utc);
        DateTimeOffset day = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(500);
        foreach ((string at, bool active) in new[] { ("03:49:59", false), ("03:50:00", true), ("04:08:29", true), ("04:08:31", false),
            ("23:56:00", true), ("00:04:00", true), ("00:05:20", true), ("00:06:00", false), ("12:00:00", false) })
            MultiHostCheck(utc.Active(day + TimeSpan.Parse(at)) == active, S, $"{at} active={!active}");
        MultiHostCheck(utc.Remote("ai") && !utc.Remote("POLARIS") && !utc.Remote(null), S, "remote = not the gateway host");

        (bool RemoteDead, bool LocalDead, bool PartitionOwned, bool HeartbeatAccepted, bool ExecutorKept, bool DeadAfterWindow) Run(bool configured)
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
            TextWriter console = Console.Out;
            Console.SetOut(new StringWriter());
            try
            {
                DateTimeOffset now = day + TimeSpan.Parse("03:55:00");
                using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
                if (configured) registry.LeaseHold = utc;
                NodeRegistration remote = registry.RegisterNode(Join("remote", "remote-ep", Guid.NewGuid()) with { Host = "ai" });
                NodeRegistration local = registry.RegisterNode(Join("local", "local-ep", Guid.NewGuid()) with { Host = "polaris" });
                PartitionOwnership owned = registry.ClaimPartition(7, remote.Node, remote.Epoch, 0);
                registry.Heartbeat(remote.Node, remote.Epoch, [new(7, owned.Generation)]);
                registry.Heartbeat(local.Node, local.Epoch, []);
                var session = Guid.NewGuid();
                Guid lease = registry.PollExecutor(new("ai", "hostd-ai", session, null, 1, [])).Lease;
                now += TimeSpan.FromSeconds(90);   // the LAN is gone for 90 s: past the 10 s node TTL and the 60 s executor lease
                registry.ExpireLeases();
                bool remoteDead = registry.Nodes().Single(x => x.Node == "remote").State == NodeState.Dead;
                bool localDead = registry.Nodes().Single(x => x.Node == "local").State == NodeState.Dead;
                bool partitionOwned = registry.GetPartitionOwner(7).Node == "remote";
                bool executorKept;
                try { registry.PollExecutor(new("ai", "hostd-ai", session, lease, 2, [])); executorKept = true; }
                catch (RegistryConflictException) { executorKept = false; }
                bool heartbeatAccepted;
                try { registry.Heartbeat(remote.Node, remote.Epoch, [new(7, owned.Generation)]); heartbeatAccepted = true; }
                catch (RegistryConflictException) { heartbeatAccepted = false; }
                // Then silence again, until past the window and its grace: the normal TTL applies at once.
                now = day + TimeSpan.Parse("04:09:00");
                registry.ExpireLeases();
                bool deadAfter = registry.Nodes().Single(x => x.Node == "remote").State == NodeState.Dead;
                return (remoteDead, localDead, partitionOwned, heartbeatAccepted, executorKept, deadAfter);
            }
            finally
            {
                Console.SetOut(console);
                Directory.Delete(directory, true);
            }
        }

        var held = Run(configured: true);
        MultiHostCheck(!held.RemoteDead && held.PartitionOwned, S, "inside the window the remote node is held with its partition: " + held);
        MultiHostCheck(held.LocalDead, S, "a node on the gateway host still dies at its TTL: " + held);
        MultiHostCheck(held.HeartbeatAccepted, S, "the remote node's returning heartbeat is accepted (no self-fence): " + held);
        MultiHostCheck(held.ExecutorKept, S, "the remote host's executor keeps its lease: " + held);
        MultiHostCheck(held.DeadAfterWindow, S, "past the window and its grace the normal TTL applies: " + held);
        var control = Run(configured: false);
        MultiHostCheck(control.RemoteDead && !control.PartitionOwned && !control.HeartbeatAccepted && !control.ExecutorKept,
            S, "control arm: without the window the silenced remote node dies, its return is refused (self-fence): " + control);
    }

    /// <summary>
    /// T-0296 round 2: the network-window hold must mean the same thing to every reader that decides "this node or its
    /// partition is gone", not only to ExpireLeases. Forced interleaving: a client-bearing handover local -> remote is
    /// committed, then the LAN goes (the nightly window) before the source release and the target status reach the
    /// gateway. The held remote node is still Active and still owns the partition, yet before the fix the handover
    /// reconciler read its lapsed lease as dead and chose Recover (AbandonReconciliation releases the held owner's
    /// partition for recovery: the rollback the hold exists to prevent), and a live-generation claim (quarantine repair)
    /// took the held partition from under it. Fixed: both read the held lease as live, the reconciler Waits.
    /// </summary>
    private static void TestMultiHostWindowHoldReaders()
    {
        const string S = "multihost-window-hold-readers";
        DateTimeOffset day = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(500);
        var windows = new NetworkWindows([(TimeSpan.Parse("03:50"), TimeSpan.Parse("04:08"))], TimeSpan.FromSeconds(30), "polaris", TimeZoneInfo.Utc);
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        Console.SetOut(new StringWriter());
        (bool NodeLive, bool OwnerLive, DirectGateway.ReconciliationAction Action, bool ClaimRefused, bool HeldBefore, bool DeadAfter) result;
        string heldSets;
        try
        {
            DateTimeOffset now = day + TimeSpan.Parse("03:55:00");
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            registry.LeaseHold = windows;
            NodeRegistration local = registry.RegisterNode(Join("local", "local-ep", Guid.NewGuid()) with { Host = "polaris" });
            NodeRegistration remote = registry.RegisterNode(Join("remote", "remote-ep", Guid.NewGuid()) with { Host = "ai" });
            PartitionOwnership source = registry.ClaimPartition(7, local.Node, local.Epoch, 0);
            registry.Begin("h7", 7, local.Node, local.Epoch, source.Generation, remote.Node, remote.Epoch);
            registry.Activate("h7");
            HandoverRecord committed = registry.Commit("h7");
            string heldFresh = string.Join(",", registry.LapsedHeldNodes());
            // The LAN goes: the remote node is silent past its TTL and the finalization lease; the local node heartbeats.
            now += TimeSpan.FromSeconds(60);
            registry.Heartbeat(local.Node, local.Epoch, []);
            registry.ExpireLeases();
            string heldLapsed = string.Join(",", registry.LapsedHeldNodes());
            bool heldBefore = registry.Nodes().Single(x => x.Node == "remote").State == NodeState.Active
                && registry.GetPartitionOwner(7) is { Node: "remote" } owner && owner.Generation == committed.CommittedGeneration;
            HandoverRecord pending = registry.PendingReconciliation().Single(x => x.Id == "h7");
            // Exactly the reconciler's inputs (DirectGateway.ReconcileHandovers): the source answers (not yet released),
            // the remote target is unreachable.
            var sourceProbe = new DirectGateway.TransferProbe(true, new(true, false, false, false, false, 0, false, false, false));
            var targetProbe = new DirectGateway.TransferProbe(false, null);
            bool sourceOwnerLive = registry.HasLiveOwnership(7, pending.SourceNode, pending.SourceEpoch, pending.SourceGeneration);
            bool targetOwnerLive = registry.HasLiveOwnership(7, pending.TargetNode, pending.TargetEpoch, pending.SourceGeneration + 1);
            bool targetNodeLive = registry.HasLiveNode(pending.TargetNode, pending.TargetEpoch);
            DirectGateway.ReconciliationAction action = DirectGateway.DecideReconciliation(pending, sourceProbe, targetProbe, sourceOwnerLive,
                targetOwnerLive, registry.HasLiveNode(pending.SourceNode, pending.SourceEpoch), targetNodeLive);
            // A live-generation claim by the local node (the quarantine-repair shape reads the registry's generation first).
            bool claimRefused;
            try { registry.ClaimPartition(7, local.Node, local.Epoch, registry.GetPartitionOwner(7).Generation); claimRefused = false; }
            catch (RegistryConflictException) { claimRefused = true; }
            // Past the window and its grace a still-silent remote node dies as before; nothing is held forever.
            now = day + TimeSpan.Parse("04:09:00");
            registry.Heartbeat(local.Node, local.Epoch, []);
            registry.ExpireLeases();
            bool deadAfter = !registry.HasLiveNode(remote.Node, remote.Epoch);
            result = (targetNodeLive, targetOwnerLive, action, claimRefused, heldBefore, deadAfter);
            heldSets = $"fresh=[{heldFresh}] lapsed=[{heldLapsed}] after=[{string.Join(",", registry.LapsedHeldNodes())}]";
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
        MultiHostCheck(result.HeldBefore, S, "setup: the remote target is held with the committed partition: " + result);
        MultiHostCheck(result.NodeLive && result.OwnerLive, S, "a held remote node and its partition read as live: " + result);
        MultiHostCheck(result.Action == DirectGateway.ReconciliationAction.Wait, S,
            "the reconciler waits for an unreachable held target instead of releasing its partition: " + result);
        MultiHostCheck(result.ClaimRefused, S, "a claim cannot take a held node's partition: " + result);
        MultiHostCheck(result.DeadAfter, S, "past the window the silent remote node is dead: " + result);
        // T-0327: only the lapsed-and-held node is excluded from automatic handovers, and only while the hold lasts.
        MultiHostCheck(heldSets == "fresh=[] lapsed=[remote] after=[]", S, "the handover pass excludes exactly the lapsed held node: " + heldSets);
    }

    /// <summary>
    /// #1277 (T-0296 F34, T-0327 class): Begin, the one entry of every handover, refuses a node whose lease is lapsed and
    /// held by a network window, as target and as source. The drain catch-up handed a crew INTO such a node: the pair
    /// loop skips it, but it still answers /partitions and is the lightest; the partition rolls back when the window
    /// outcome closes it Dead (W3: node-99). Controls: a fresh remote node and the held node once it heartbeats again.
    /// </summary>
    private static void TestMultiHostWindowHoldBegin()
    {
        const string S = "multihost-window-hold-begin";
        DateTimeOffset day = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(500);
        var windows = new NetworkWindows([(TimeSpan.Parse("03:50"), TimeSpan.Parse("04:08"))], TimeSpan.FromSeconds(30), "polaris", TimeZoneInfo.Utc);
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        Console.SetOut(new StringWriter());
        string outcome;
        try
        {
            DateTimeOffset now = day + TimeSpan.Parse("03:55:00");
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            registry.LeaseHold = windows;
            NodeRegistration l = registry.RegisterNode(Join("l", "l-ep", Guid.NewGuid()) with { Host = "polaris" });
            NodeRegistration x = registry.RegisterNode(Join("x", "x-ep", Guid.NewGuid()) with { Host = "polaris" });
            NodeRegistration r = registry.RegisterNode(Join("r", "r-ep", Guid.NewGuid()) with { Host = "ai" });
            long seven = registry.ClaimPartition(7, l.Node, l.Epoch, 0).Generation;
            long eight = registry.ClaimPartition(8, r.Node, r.Epoch, 0).Generation;
            string Try(string id, ulong partition, NodeRegistration from, long generation, NodeRegistration to)
            {
                try { registry.Begin(id, partition, from.Node, from.Epoch, generation, to.Node, to.Epoch); return "begun"; }
                catch (RegistryConflictException) { return "refused"; }
            }
            // The LAN goes: R is silent past its TTL, held by the window; L and X heartbeat.
            now += TimeSpan.FromSeconds(60);
            registry.Heartbeat(l.Node, l.Epoch, [new PartitionVersion(7, seven)]);
            registry.Heartbeat(x.Node, x.Epoch, []);
            registry.ExpireLeases();
            string held = string.Join(",", registry.LapsedHeldNodes());
            string into = Try("into", 7, l, seven, r), outOf = Try("out", 8, r, eight, x);
            // R is back inside the window: its lease is fresh, the hold no longer applies.
            registry.Heartbeat(r.Node, r.Epoch, [new PartitionVersion(8, eight)]);
            string back = Try("back", 8, r, eight, x);
            string local = Try("local", 7, l, seven, x);
            outcome = $"held=[{held}] into={into} out={outOf} back={back} local={local}";
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
        MultiHostCheck(outcome == "held=[r] into=refused out=refused back=begun local=begun", S,
            "Begin refuses exactly the lapsed held node, as target and as source: " + outcome);
    }

    /// <summary>
    /// T-0296 r2: recovery inside a network window must not roll a partition back past a newer save whose only live
    /// holder is a HELD remote node. Partition 11: save S1 on local L + remote R, then a handover to local X and save S2
    /// on X + R. Inside the window R is held (silent, not dead) and X dies. HolderUsable reads R as unusable and X as
    /// dead, so Newest() skipped S2 and the auto-pick handed 11 to L with S1: everything since the handover lost. Fixed:
    /// the partition waits for the window (the hold says "no recovery until the window ends"), and once R is back the
    /// claim names S2.
    /// </summary>
    private static void TestMultiHostWindowRecoveryHolder()
    {
        const string S = "multihost-window-recovery-holder";
        DateTimeOffset day = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(500);
        var windows = new NetworkWindows([(TimeSpan.Parse("03:50"), TimeSpan.Parse("04:08"))], TimeSpan.FromSeconds(30), "polaris", TimeZoneInfo.Utc);
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        Console.SetOut(new StringWriter());
        string setup, inWindow, afterReturn;
        try
        {
            DateTimeOffset now = day + TimeSpan.Parse("03:40:00");
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            registry.LeaseHold = windows;
            NodeRegistration l = registry.RegisterNode(Join("l", "l-ep", Guid.NewGuid()) with { Host = "polaris" });
            NodeRegistration x = registry.RegisterNode(Join("x", "x-ep", Guid.NewGuid()) with { Host = "polaris" });
            NodeRegistration r = registry.RegisterNode(Join("r", "r-ep", Guid.NewGuid()) with { Host = "ai" });
            PartitionOwnership first = registry.ClaimPartition(11, l.Node, l.Epoch, 0);
            var s1 = new SaveId(11, first.Generation, 1);
            registry.RegisterSave(s1, l.Node, l.Epoch, SaveKind.Periodic, 1, "h1", 2, "l/s1", "http://l/control");
            registry.RegisterHolder(s1, r.Node, r.Epoch, 1, "h1", "r/s1", "http://r/control");
            registry.Begin("h11", 11, l.Node, l.Epoch, first.Generation, x.Node, x.Epoch);
            registry.Activate("h11");
            long moved = registry.Commit("h11").CommittedGeneration!.Value;
            var s2 = new SaveId(11, moved, 1);
            registry.RegisterSave(s2, x.Node, x.Epoch, SaveKind.Periodic, 1, "h2", 3, "x/s2", "http://x/control");
            registry.RegisterHolder(s2, r.Node, r.Epoch, 1, "h2", "r/s2", "http://r/control");
            // The window: R goes silent (held), X dies (local: not held), L heartbeats.
            now = day + TimeSpan.Parse("03:55:00");
            registry.Heartbeat(l.Node, l.Epoch, []);
            registry.ExpireLeases();
            NodeRegistration[] rows = registry.Nodes();
            setup = $"x={rows.Single(n => n.Node == "x").State} r={rows.Single(n => n.Node == "r").State} owner={registry.GetPartitionOwner(11).Node ?? "none"}";
            RecoveryClaim[] during = registry.ClaimRecoveries(l.Node, l.Epoch, []);
            inWindow = string.Join(",", during.Select(c => $"{c.Ownership.Partition}:{string.Join("+", c.ResolveCover.Select(v => v.Save.SaveId.Generation))}"));
            if (during.Length != 0) registry.ReleasePartition(11, l.Node, l.Epoch, during[0].Ownership.Generation);
            // R comes back inside the window; the claim now names the newest save.
            now += TimeSpan.FromMinutes(5);
            registry.Heartbeat(l.Node, l.Epoch, []);
            registry.Heartbeat(r.Node, r.Epoch, []);
            registry.ExpireLeases();
            RecoveryClaim[] after = registry.ClaimRecoveries(l.Node, l.Epoch, []);
            afterReturn = string.Join(",", after.Select(c => $"{c.Ownership.Partition}:{string.Join("+", c.ResolveCover.Select(v => v.Save.SaveId.Generation))}"));
            setup += $" s1gen={first.Generation} s2gen={moved}";
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
        MultiHostCheck(setup.StartsWith("x=Dead r=Active owner=none", StringComparison.Ordinal), S, "setup: X dead, R held, 11 unowned: " + setup);
        MultiHostCheck(inWindow == "", S, $"inside the window 11 waits for its newest save's held holder, not an older save: claimed [{inWindow}] ({setup})");
        MultiHostCheck(afterReturn == "11:" + setup.Split("s2gen=")[1], S,
            $"once R is back the claim names S2: [{afterReturn}] ({setup})");
    }

    /// <summary>
    /// P6: every node HTTP call is counted under its CHANNELS.md id and path. The id comes from the route (registry R*,
    /// peer N*, gateway C*); the path is local when the peer address is loopback or this host's own address - which is
    /// what a same-host peer dialled at its advertised LAN address is - and lan otherwise.
    /// </summary>
    private static void TestMultiHostTransportCounters()
    {
        const string S = "multihost-transport-counters";
        foreach ((bool registry, bool fromNode, string path, string method, string channel) in new[]
        {
            (true, true, "/registry/nodes", "POST", "R1"), (true, true, "/registry/network-id-blocks", "POST", "R1"),
            (true, true, "/registry/nodes/heartbeat", "POST", "R2"), (true, true, "/registry/handover-config", "GET", "R3"),
            (true, true, "/registry/claims", "POST", "R4"), (true, true, "/registry/partitions/release", "POST", "R4"),
            (true, true, "/registry/saves/holders", "POST", "R5"), (true, true, "/registry/replicas", "GET", "R5"),
            (true, true, "/registry/character-homes", "POST", "R6"), (true, true, "/registry/pull-demands", "POST", "R7"),
            (true, true, "/registry/plugins/v1", "POST", "R8"), (true, true, "/registry/grants/validate", "POST", "R9"),
            (false, true, "/cluster/save-file", "GET", "N1"), (false, true, "/cluster/global-save-file", "GET", "N2"),
            (false, true, "/cluster/snapshot", "GET", "N4"), (false, true, "/cluster/voxel-journal/append", "POST", "N5"),
            (false, true, "/cluster/voxel-journal-tails", "GET", "N5"),
            (false, true, "/cluster/voxel-base-file", "GET", "N6"), (false, true, "/cluster/partitions", "GET", "N7"),
            (false, true, "/cluster/igc-deliver", "POST", "N8"), (false, true, "/cluster/mod-deliver", "POST", "N9"),
            (false, true, "/cluster/mod-locate", "GET", "N9"),
            (false, true, "/cluster/spawn-execute", "POST", "N10"), (false, true, "/cluster/partition-of", "GET", "N11"),
            (false, false, "/cluster/partitions", "GET", "C1"), (false, false, "/cluster/capture", "POST", "C2"),
            (false, false, "/cluster/diag", "GET", "C3"), (false, false, "/cluster/trigger/save", "POST", "C4"),
            (false, false, "/cluster/respawn-execute", "POST", "C5"),
        })
        {
            string got = ClusterNet.ChannelOf(registry, fromNode, path, method);
            MultiHostCheck(got == channel, S, $"{method} {path} -> {channel}, got {got}");
        }
        MultiHostCheck(TransportCounters.PathTo("127.0.0.1") == TransportPath.Local && TransportCounters.PathTo("localhost") == TransportPath.Local
            && TransportCounters.PathTo("192.0.2.1") == TransportPath.Lan && TransportCounters.PathTo(null) == TransportPath.Lan, S, "path classification");
        if (OwnLanAddress() is { } lan)
            MultiHostCheck(TransportCounters.PathTo(lan.ToString()) == TransportPath.Local, S, $"this host's LAN address {lan} is the same-host path");
        TransportCounters.ResetForTest();
        TransportCounters.Count("N2", false, TransportPath.Local, 1000, TimeSpan.FromMilliseconds(0.3));
        TransportCounters.Count("N2", false, TransportPath.Lan, 500, TimeSpan.FromMilliseconds(3));
        TransportCounters.Count("N2", true, TransportPath.Lan, 70, TimeSpan.FromMilliseconds(1));
        TransportCounters.Count("N2", false, TransportPath.Fallback, 0, TimeSpan.Zero);
        JsonElement n2 = JsonDocument.Parse(JsonSerializer.Serialize(TransportCounters.Snapshot())).RootElement.GetProperty("channels").GetProperty("N2");
        MultiHostCheck(n2.GetProperty("local").GetProperty("n").GetInt64() == 1 && n2.GetProperty("local").GetProperty("bytes").GetInt64() == 1000
            && n2.GetProperty("lan").GetProperty("n").GetInt64() == 1 && n2.GetProperty("fallback").GetInt64() == 1
            && n2.GetProperty("served").GetProperty("lan").GetProperty("bytes").GetInt64() == 70
            && n2.GetProperty("local").GetProperty("p50Ms").GetDouble() is > 0.25 and <= 0.6
            && n2.GetProperty("lan").GetProperty("p99Ms").GetDouble() is > 2.9 and <= 6.2, S, "snapshot: " + n2);
        TransportCounters.ResetForTest();
    }
}
