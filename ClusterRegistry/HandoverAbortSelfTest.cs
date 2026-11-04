using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// T-0369 harness: one client-bearing <c>FencedHandover</c> against a scripted relay (gateway-rs's end of the
    /// IPC socket: it acknowledges every Attach/Detach and reports target traffic on the replayed connect) and two
    /// scripted node control servers. <paramref name="respond"/> answers one node control request: a status code,
    /// or null to drop the connection without an answer (a transport failure to the gateway).
    /// </summary>
    private sealed class ScriptedHandover : IDisposable
    {
        public const ulong Client = 76561199500000023, Partition = 303693, RelaySession = 7;
        public const string ClientState = "view-of-23";
        /// <summary>Every node control request in arrival order: "source adopt-ready", "target commit", ...</summary>
        public readonly ConcurrentQueue<string> Calls = new();
        /// <summary>The same requests with their query string, for the assertions on what was asked.</summary>
        public readonly ConcurrentQueue<(string Call, System.Collections.Specialized.NameValueCollection Query)> Requests = new();
        public readonly GatewayRegistry Registry;
        public volatile bool ClientDisconnected;
        /// <summary>T-0373: the source's /cluster/partitions answer (JSON); null answers like any other action.</summary>
        public volatile string? SourcePartitions;
        private readonly string _directory = Directory.CreateTempSubdirectory("handover-abort-selftest-").FullName;
        private readonly DirectGateway _gateway;
        private readonly ScriptedRelay _relay;
        private readonly HttpListener[] _listeners;
        private readonly string _sourceControl, _targetControl, _sourceEndpoint, _targetEndpoint;
        private volatile bool _stop;
        private volatile string _attachedTo = "";

        public ScriptedHandover(Func<string, int, int?> respond)
        {
            int sourcePort = FreeTcpPort(), targetPort = FreeTcpPort();
            _sourceControl = "127.0.0.1:" + sourcePort;
            _targetControl = "127.0.0.1:" + targetPort;
            _sourceEndpoint = "127.0.0.1:" + FreeUdpPort();
            do _targetEndpoint = "127.0.0.1:" + FreeUdpPort(); while (_targetEndpoint == _sourceEndpoint);
            var seen = new ConcurrentDictionary<string, int>();
            _listeners = [Serve("source", sourcePort), Serve("target", targetPort)];
            HttpListener Serve(string node, int port)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/cluster/");
                listener.Start();
                new Thread(() =>
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext context;
                        try { context = listener.GetContext(); } catch (Exception) { return; }
                        string action = context.Request.Url!.AbsolutePath["/cluster/".Length..];
                        string call = node + " " + action;
                        Calls.Enqueue(call);
                        Requests.Enqueue((call, context.Request.QueryString));
                        int? status = respond(call, seen.AddOrUpdate(call, 1, (_, count) => count + 1));
                        if (status == null)
                        {
                            // The connection dies inside the answer: the gateway's request fails with an HttpRequestException
                            // (HttpListener.Abort alone still completes an empty 200 on this runtime).
                            try
                            {
                                context.Response.ContentLength64 = 4096;
                                context.Response.OutputStream.Write("cut"u8);
                                context.Response.OutputStream.Flush();
                            }
                            catch (Exception) { }
                            context.Response.Abort();
                            continue;
                        }
                        byte[] body = action == "capture" ? CaptureBytes(context.Request.QueryString["clientless"] == "true")
                            : action == "partitions" && node == "source" && SourcePartitions is { } partitions ? System.Text.Encoding.UTF8.GetBytes(partitions)
                            : action == "tee-status" ? "{\"known\":false}"u8.ToArray() : "ok\n"u8.ToArray();
                        try
                        {
                            context.Response.StatusCode = status.Value;
                            context.Response.OutputStream.Write(body);
                            context.Response.Close();
                        }
                        catch (Exception) { }
                    }
                }) { IsBackground = true, Name = "Scripted-Handover-" + node }.Start();
                return listener;
            }

            string path = Path.Combine(_directory, "gateway-relay.sock");
            Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", "true");
            var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), IPEndPoint.Parse(_sourceEndpoint), null,
                false, null, null, null, Path.Combine(_directory, "registry"), TimeSpan.FromMinutes(10), Admission, null, [],
                DirectFrontend: true, ExternalRelay: true, RelaySocket: path);
            _gateway = new DirectGateway(options);
            new Thread(() =>
            {
                try { _gateway.Run(); }
                catch (Exception e) { Console.Error.WriteLine("handover-abort gateway failed: " + e); }
            }) { IsBackground = true, Name = "Gateway-Handover-Abort-Self-Test" }.Start();
            Registry = (GatewayRegistry)typeof(DirectGateway).GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_gateway)!;
            NodeRegistration source = Registry.RegisterNode(Join("source", _sourceEndpoint, Guid.NewGuid()) with { ControlEndpoint = _sourceControl });
            Registry.RegisterNode(Join("target", _targetEndpoint, Guid.NewGuid()) with { ControlEndpoint = _targetControl });
            Registry.ClaimPartition(Partition, source.Node, source.Epoch, 0);

            _relay = new ScriptedRelay(path);
            _relay.OnMessage = message =>
            {
                if (message.Type == RelayIpcType.NodeFrame)
                {
                    System.Text.Json.JsonElement frame = message.Json;
                    string kind = frame.GetProperty("kind").GetString()!, endpoint = frame.GetProperty("endpoint").GetString()!;
                    if (kind is not ("attach" or "detach")) return;
                    if (kind == "attach") _attachedTo = endpoint;
                    _relay!.Send(RelayIpcType.NodeControl, 0, new { clientId = Client, session = RelaySession, endpoint, kind = kind + "Ack" });
                }
                // gateway-rs replays the client's connect to the link it is now on; the node's first answer is the target traffic.
                else if (message.Type == RelayIpcType.ReplayConnect)
                    _relay!.Send(RelayIpcType.ClientEvent, 0, new { clientId = Client, session = RelaySession, @event = "targetReady", detail = _attachedTo });
                else if (message.Type == RelayIpcType.Disconnect && message.Json.GetProperty("clientId").GetUInt64() == Client)
                    ClientDisconnected = true;
            };
            _relay.Read(RelayIpcType.Config);
            _relay.Send(RelayIpcType.JoinRequest, 0x900, new { clientId = Client, session = RelaySession, service = "DirectTransport", name = "Pilot-023", remote = "127.0.0.1:1" });
            Assert(_relay.Read(RelayIpcType.JoinAnswer, m => m.Correlation == 0x900).Json.GetProperty("decision").GetString() == "accept");
            _relay.Send(RelayIpcType.ClientMessage, 0, new { clientId = Client, session = RelaySession, messageId = 14, channel = 2, packet = Convert.ToBase64String(ClientConnectedFixture) });
            _relay.Read(RelayIpcType.SessionUpdate, m => m.Json.GetProperty("clientId").GetUInt64() == Client
                && m.Json.TryGetProperty("attached", out System.Text.Json.JsonElement attached) && attached.ValueKind == System.Text.Json.JsonValueKind.True);
            new Thread(() =>
            {
                while (!_stop)
                    try { _relay.Read(ushort.MaxValue); }
                    catch (Exception) { }
            }) { IsBackground = true, Name = "Scripted-Handover-Relay" }.Start();
        }

        /// <param name="clientless">T-0354b: a clientless capture carries no client state.</param>
        private static byte[] CaptureBytes(bool clientless = false)
        {
            using MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                byte[] state = clientless ? [] : System.Text.Encoding.UTF8.GetBytes(ClientState);
                writer.Write(-1);
                writer.Write(2);
                writer.Write(clientless ? 0 : 1);
                if (!clientless)
                {
                    writer.Write(Client);
                    writer.Write(state.Length);
                }
                writer.Write(0);
                writer.Write(2);
                writer.Write(state);
                writer.Write(new byte[] { 4, 5 });
            }
            return stream.ToArray();
        }

        /// <summary>Runs the client-bearing handover source -> target; returns its failure, or null when it committed.</summary>
        public Exception? Handover() => Handover(Partition, null);

        /// <summary>
        /// T-0354b: the client-bearing handover of <paramref name="partition"/>, and with <paramref name="delay"/> set as a
        /// requested move does it (DirectGateway.WhenRegistryApplied, its waits replaced by <paramref name="delay"/>).
        /// </summary>
        public Exception? Handover(ulong partition, Func<TimeSpan, Task>? delay)
        {
            const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
            object session = ((IDictionary)typeof(DirectGateway).GetField("_clients", Private)!.GetValue(_gateway)!)[Client]
                ?? throw new InvalidOperationException("self-test: the client has no gateway session");
            Array sessions = Array.CreateInstance(session.GetType(), 1);
            sessions.SetValue(session, 0);
            Task Once() => (Task)typeof(DirectGateway).GetMethod("FencedHandover", Private)!
                .Invoke(_gateway, [sessions, _sourceControl, _targetControl, IPEndPoint.Parse(_targetEndpoint), partition, false])!;
            return Wait(delay == null ? Once() : DirectGateway.WhenRegistryApplied(partition, Once, delay));
        }

        /// <summary>T-0354b: the clientless handover of <paramref name="partition"/>, plain or as a requested move (see <see cref="Handover(ulong, Func{TimeSpan, Task}?)"/>).</summary>
        public Exception? Clientless(ulong partition, Func<TimeSpan, Task>? delay)
        {
            const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
            Task Once() => (Task)typeof(DirectGateway).GetMethod("FencedClientlessHandover", Private)!
                .Invoke(_gateway, [_sourceControl, _targetControl, IPEndPoint.Parse(_targetEndpoint), partition, false, false])!;
            return Wait(delay == null ? Once() : DirectGateway.WhenRegistryApplied(partition, Once, delay));
        }

        private static Exception? Wait(Task handover)
        {
            try
            {
                if (!handover.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("self-test: the handover did not finish in 60 s");
                return null;
            }
            catch (AggregateException e) { return e.InnerExceptions.Count == 1 ? e.InnerException : e; }
        }

        /// <summary>
        /// T-0373: closes the source in the registry and runs the automatic pass's drain catch-up (DrainClosedNodes) with
        /// the target as the only serving node; the client counts as world-ready and settled. Returns what escaped it.
        /// </summary>
        public Exception? DrainClosedSource()
        {
            const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
            object session = ((IDictionary)typeof(DirectGateway).GetField("_clients", Private)!.GetValue(_gateway)!)[Client]
                ?? throw new InvalidOperationException("self-test: the client has no gateway session");
            session.GetType().GetField("WorldReadyAt")!.SetValue(session, DateTimeOffset.UtcNow.AddMinutes(-1));
            session.GetType().GetField("WorldReady")!.SetValue(session, true);
            NodeRegistration source = Registry.Nodes().Single(x => x.Node == "source"), target = Registry.Nodes().Single(x => x.Node == "target");
            Registry.CloseNode(source.Node, source.Epoch);
            Type servedType = typeof(DirectGateway).GetNestedType("ServedNode", BindingFlags.NonPublic)!;
            object served = Activator.CreateInstance(servedType, target.Node, target.Epoch, _targetControl, IPEndPoint.Parse(_targetEndpoint))!;
            Type pair = typeof(ValueTuple<,>).MakeGenericType(servedType, typeof(PartitionReport));
            Array active = Array.CreateInstance(pair, 1);
            active.SetValue(Activator.CreateInstance(pair, served, new PartitionReport(3000, 100, [], 0)), 0);
            try
            {
                typeof(DirectGateway).GetMethod("DrainClosedNodes", Private)!.Invoke(_gateway, [active]);
                return null;
            }
            catch (TargetInvocationException e) { return e.InnerException; }
        }

        public string Owner => Registry.GetPartitionOwner(Partition).Node ?? "none";
        public int Count(string call) => Calls.Count(x => x == call);
        public int IndexOf(string call) => Calls.ToList().IndexOf(call);

        public void Dispose()
        {
            _stop = true;
            try { _gateway.Stop(); } catch (Exception) { }
            foreach (HttpListener listener in _listeners) try { listener.Close(); } catch (Exception) { }
            _relay.Dispose();
            _gateway.Dispose();
            Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", null);
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// T-0369 defect A (2026-10-02 00:05:08, transfer 8b6dd7a7): the adopt-ready poll retried a non-success answer
    /// but let a transport failure escape, so one failed POST aborted a handover whose client was already switched
    /// to the target. Here the connection of the first adopt-ready, of the first adopt and of the first tee-status
    /// request dies: the handover must still commit.
    /// </summary>
    private static void TestHandoverAdoptReadyTransportFailure()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("handover-adopt-ready-transport-failure: " + what); }
        using var scripted = new ScriptedHandover((call, nth) =>
            call is "target adopt-ready" or "target adopt" or "source tee-status" && nth == 1 ? null : 200);
        Exception? failure = scripted.Handover();
        Console.WriteLine($"handover-adopt-ready-transport-failure: failure={failure?.GetType().Name ?? "none"}"
            + (failure == null ? "" : $" ({failure.Message})") + $" owner={scripted.Owner} calls=[{string.Join(", ", scripted.Calls)}]");
        Check(failure == null, "a dropped adopt-ready connection aborted the handover: " + failure?.Message);
        Check(scripted.Owner == "target", "the partition is owned by " + scripted.Owner + ", not the target");
        Check(scripted.Count("target adopt-ready") == 2 && scripted.Count("target adopt") == 2 && scripted.Count("source tee-status") == 2,
            "the dropped requests were not retried once each");
        Check(scripted.Count("target commit") == 1 && scripted.Count("source release") == 1, "no target commit or source release");
        Check(scripted.Count("source thaw") == 0 && scripted.Count("target discard") == 0, "a committed handover was cleaned up as aborted");
        Check(!scripted.ClientDisconnected, "the client was disconnected");
    }

    /// <summary>
    /// T-0369 defect B: a handover that aborts AFTER its client was switched to the target. The switch detached the
    /// client from the source (vanilla ClientLeft: its player is gone there) and the rollback attaches it again as
    /// a fresh connection, which the source only turns back into a player controlling its body when it is adopted -
    /// what the target would have been asked. Without it the source reports the partition clientless while the
    /// route names the client (303693 for 6.5 minutes). Here the target refuses the adopt: the source must be asked
    /// adopt-ready and adopt for the client, unfenced and with the captured view, before the thaw, and the client
    /// keeps its session. Second run: the source refuses the re-adoption, so the client is disconnected (its
    /// reconnect is a real join) and the cleanup still thaws and aborts.
    /// </summary>
    private static void TestHandoverAbortReadoptsOnSource()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("handover-abort-readopts-on-source: " + what); }
        using (var scripted = new ScriptedHandover((call, _) => call == "target adopt" ? 500 : 200))
        {
            Exception? failure = scripted.Handover();
            Console.WriteLine($"handover-abort-readopts-on-source: failure={failure?.GetType().Name ?? "none"} owner={scripted.Owner} "
                + $"disconnected={scripted.ClientDisconnected} calls=[{string.Join(", ", scripted.Calls)}]");
            Check(failure != null, "the handover did not abort");
            Check(scripted.Owner == "source", "the partition is owned by " + scripted.Owner + " after the abort");
            Check(scripted.Count("target discard") == 1 && scripted.Count("source thaw") == 1, "the abort did not discard the target copy and thaw the source");
            Check(scripted.Count("source adopt-ready") >= 1 && scripted.Count("source adopt") == 1,
                "the source was not asked to adopt the client it got back (it stops reporting the client on the partition)");
            Check(scripted.IndexOf("source adopt") < scripted.IndexOf("source thaw"), "the source was thawed before it re-adopted its client");
            foreach ((string call, System.Collections.Specialized.NameValueCollection query) in scripted.Requests.Where(x => x.Call is "source adopt-ready" or "source adopt"))
                Check(query["client"] == ScriptedHandover.Client.ToString() && query["state"] == ScriptedHandover.ClientState && query["transferId"] == null,
                    call + " did not carry the client's captured view unfenced");
            Check(!scripted.ClientDisconnected, "the re-adopted client was disconnected");
            Check(scripted.Registry.Incomplete().Length == 0, "the handover record was left incomplete");
        }
        using (var scripted = new ScriptedHandover((call, _) => call is "target adopt" or "source adopt" ? 500 : 200))
        {
            Exception? failure = scripted.Handover();
            for (int waited = 0; waited < 50 && !scripted.ClientDisconnected; waited++) Thread.Sleep(100);
            Console.WriteLine($"handover-abort-readopts-on-source: source refuses: failure={failure?.GetType().Name ?? "none"} owner={scripted.Owner} "
                + $"disconnected={scripted.ClientDisconnected} calls=[{string.Join(", ", scripted.Calls)}]");
            Check(failure != null && scripted.Owner == "source", "the handover did not abort back to the source");
            Check(scripted.ClientDisconnected, "a client the source could not re-adopt kept its session (routed, attached, reported by no node)");
            Check(scripted.Count("source thaw") == 1 && scripted.Registry.Incomplete().Length == 0, "a failed re-adoption kept the cleanup from thawing and aborting");
        }
    }
}
