using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LiteNetLib;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    // Real admin HTTP/auth/operation persistence and fenced UDP command transport. The WA's
    // game is simulated here; Test/PlayerModeration exercises the actual promotion service.
    private static void TestPlayerModeration()
    {
        string directory = Directory.CreateTempSubdirectory("player-moderation-").FullName;
        string tokens = Path.Combine(directory, "tokens.json");
        string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        File.WriteAllText(tokens, JsonSerializer.Serialize(new { tokens = new[] { new { name = "reader", scope = "query", sha256 = Hash("query") } } }));
        int control = FreeTcpPort(), nodePort = FreeUdpPort();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, nodePort),
            new(IPAddress.Loopback, control), false, null, null, null, Path.Combine(directory, "registry"),
            TimeSpan.FromMinutes(10), Admission, "manage", [], AdminTokensPath: tokens);
        var gateway = new DirectGateway(options);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var registry = (GatewayRegistry)typeof(DirectGateway).GetField("_registry", flags)!.GetValue(gateway)!;
        var book = (AdmissionBook)typeof(DirectGateway).GetField("_admission", flags)!.GetValue(gateway)!;
        var backend = (NetManager)typeof(DirectGateway).GetField("_back", flags)!.GetValue(gateway)!;
        var wa = registry.RegisterNode(Join("wa", $"127.0.0.1:{nodePort}", Guid.NewGuid()) with { Role = NodeRole.WA });
        long generation = registry.WorldAuthority().Generation;
        int effects = 0, commands = 0;
        bool corrupt = false, reject = false, notReady = false;
        var levels = new Dictionary<ulong, (int Level, long Revision)>();
        var ledger = new Dictionary<Guid, string>();
        var ids = new List<Guid>();
        var nodeListener = new EventBasedNetListener();
        nodeListener.ConnectionRequestEvent += request => request.Accept();
        nodeListener.NetworkReceiveEvent += (peer, reader, _, delivery) =>
        {
            var packet = DirectTransportCodec.DecodeNodeLink(reader.GetRemainingBytes(), delivery);
            if (packet.Kind != NodeLinkMessage.Global) return;
            using var document = JsonDocument.Parse(packet.Payload);
            var envelope = document.RootElement;
            string subsystem = envelope.GetProperty("Subsystem").GetString()!;
            Guid id = envelope.GetProperty("OperationId").GetGuid();
            Assert(envelope.GetProperty("Origin").GetString() == "$gateway" && envelope.GetProperty("Generation").GetInt64() == generation);
            string result;
            if (subsystem == "promotion-query")
                result = JsonSerializer.Serialize(new { Entries = levels.Select(entry => new { SteamId = entry.Key, Level = entry.Value.Level, Revision = entry.Value.Revision }).ToArray() });
            else if (notReady) result = string.Empty;
            else
            {
                Interlocked.Increment(ref commands);
                ids.Add(id);
                Assert(subsystem == "promotion-set");
                if (!ledger.TryGetValue(id, out result!))
                {
                    using var payload = JsonDocument.Parse(envelope.GetProperty("Payload").GetString()!);
                    ulong user = payload.RootElement.GetProperty("SteamId").GetUInt64();
                    int level = payload.RootElement.GetProperty("Level").GetInt32();
                    Assert(user == 7 && level is >= 0 and <= 4);
                    long expected = payload.RootElement.GetProperty("ExpectedRevision").GetInt64();
                    Assert(expected == levels.GetValueOrDefault(user).Revision);
                    levels[user] = (level, expected + 1);
                    Interlocked.Increment(ref effects);
                    ledger[id] = result = JsonSerializer.Serialize(new { SteamId = user, Level = level, Revision = expected + 1 });
                }
            }
            if (corrupt && subsystem == "promotion-set") result = "{\"SteamId\":999,\"Level\":4}";
            byte[] reply = JsonSerializer.SerializeToUtf8Bytes(new { Kind = "Response", Generation = generation, Subsystem = subsystem,
                Sequence = envelope.GetProperty("Sequence").GetInt64(), Origin = wa.Node, Target = "$gateway",
                Payload = JsonSerializer.Serialize(new { Success = !reject && !notReady, Payload = result,
                    Error = notReady ? "promotion_not_ready" : reject ? "No handler on old WA" : "" }), OperationId = id });
            var response = new NodeLinkPacket(NodeLinkMessage.Global, 0, 0, reply, DeliveryMethod.ReliableUnordered);
            peer.Send(DirectTransportCodec.EncodeNodeLink(response), 0, response.Delivery);
        };
        NetManager node = TestManager(nodeListener);
        Assert(node.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, nodePort));
        Assert(backend.Start());
        using var stop = new CancellationTokenSource();
        var networkThread = new Thread(() => { while (!stop.IsCancellationRequested) { node.PollEvents(); backend.PollEvents(); Thread.Sleep(1); } }) { IsBackground = true };
        networkThread.Start();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{control}/"); listener.Start();
        var dispatch = typeof(DirectGateway).GetMethod("HandleControl", flags)!;
        var thread = new Thread(() =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = listener.GetContext(); } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                dispatch.Invoke(gateway, [c]);
            }
        }) { IsBackground = true }; thread.Start();
        using var http = new HttpClient();
        (int Status, JsonElement Body) Request(string path, string? token = "manage", string method = "GET", string? key = null, string? body = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{control}/admin/v1" + path);
            if (token != null) request.Headers.Add("Authorization", "Bearer " + token);
            if (key != null) request.Headers.Add("Idempotency-Key", key);
            if (method != "GET") request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
            using var response = http.Send(request);
            using var json = JsonDocument.Parse(response.Content.ReadAsStream());
            return ((int)response.StatusCode, json.RootElement.Clone());
        }
        try
        {
            Assert(Request("/players/moderation", null).Status == 401);
            var capabilities = Request("/capabilities", "query").Body.GetProperty("data");
            Assert(capabilities.GetProperty("operations").EnumerateArray().Any(item => item.GetString() == "PUT /admin/v1/players/{userId}/promotion"));
            Assert(capabilities.GetProperty("features").GetProperty("playerModeration").GetString() == "v1");
            foreach (var route in new[] { ("/players/7/promotion", "PUT"), ("/admission/kicks/7", "DELETE") })
            {
                Assert(Request(route.Item1, "query", route.Item2, "denied", "{\"level\":\"Admin\",\"expectedRevision\":0}").Status == 403);
                Assert(Request(route.Item1, null, route.Item2, "unauth", "{\"level\":\"Admin\",\"expectedRevision\":0}").Status == 401);
                Assert(Request(route.Item1, method: route.Item2, body: "{\"level\":\"Admin\",\"expectedRevision\":0}").Status == 400);
            }
            foreach (string invalid in new[] { "{}", "{\"level\":\"Owner\"}", "{\"level\":5}", "{\"level\":-1}" })
                Assert(Request("/players/7/promotion", method: "PUT", key: "invalid", body: invalid).Status == 400);
            Assert(Request("/players/0/promotion", method: "PUT", key: "zero", body: "{\"level\":\"Admin\",\"expectedRevision\":0}").Status == 400);
            Assert(Request("/admission/kicks/0", method: "DELETE", key: "zero").Status == 400);
            Assert(effects == 0 && registry.AdminOperationList(100).Length == 0);
            // A synthetic disconnected transport exercises the real kick/session-removal owner;
            // no game client or player is contacted. A completed retry must work after removal.
            var sessions = (System.Collections.IDictionary)typeof(DirectGateway).GetField("_clients", flags)!.GetValue(gateway)!;
            var sessionType = typeof(DirectGateway).GetNestedType("ClientSession", BindingFlags.NonPublic)!;
            sessions[9UL] = Activator.CreateInstance(sessionType, [9UL, new IPEndPoint(IPAddress.Loopback, nodePort), null]);
            var kicked = Request("/clients/9/kick", method: "POST", key: "kick-once").Body.GetProperty("data");
            Assert(kicked.GetProperty("state").GetString() == "Succeeded" && book.WasKickedRecently(9) && !sessions.Contains(9UL));
            book.ClearKickCooldown(9);
            var kickReplay = Request("/clients/9/kick", method: "POST", key: "kick-once").Body.GetProperty("data");
            Assert(kickReplay.GetProperty("operationId").GetString() == kicked.GetProperty("operationId").GetString() && !book.WasKickedRecently(9));
            notReady = true;
            var loading = Request("/players/7/promotion", method: "PUT", key: "startup", body: "{\"level\":\"Moderator\",\"expectedRevision\":0}").Body.GetProperty("data");
            Assert(loading.GetProperty("state").GetString() == "Failed"
                && loading.GetProperty("error").GetProperty("code").GetString() == "promotion_not_ready"
                && effects == 0 && !registry.ResumePlayerPromotion(loading.GetProperty("operationId").GetString()!));
            notReady = false;
            foreach (string level in Enum.GetNames<Admin.PlayerPromotionLevel>())
            {
                string body = JsonSerializer.Serialize(new { level, expectedRevision = effects });
                var response = Request("/players/7/promotion", method: "PUT", key: level, body: body);
                Assert(response.Status == 200 && response.Body.GetProperty("data").GetProperty("state").GetString() == "Succeeded");
                int before = effects;
                Assert(Request("/players/7/promotion", method: "PUT", key: level, body: body).Body.GetProperty("data").GetProperty("operationId").GetString()
                    == response.Body.GetProperty("data").GetProperty("operationId").GetString());
                Assert(effects == before);
            }
            Assert(effects == 5);
            Assert(Request("/players/8/promotion", method: "PUT", key: "Admin", body: "{\"level\":\"Admin\",\"expectedRevision\":4}").Status == 409);
            Assert(Request("/players/7/promotion", method: "PUT", key: "Admin", body: "{\"level\":\"None\",\"expectedRevision\":5}").Status == 409);
            corrupt = true;
            var uncertain = Request("/players/7/promotion", method: "PUT", key: "uncertain", body: "{\"level\":\"None\",\"expectedRevision\":5}").Body.GetProperty("data");
            Assert(uncertain.GetProperty("state").GetString() == "Failed" && uncertain.GetProperty("error").GetProperty("code").GetString() == "promotion_uncertain");
            int beforeRetry = effects;
            corrupt = false;
            var retried = Request("/players/7/promotion", method: "PUT", key: "uncertain", body: "{\"level\":\"None\",\"expectedRevision\":5}").Body.GetProperty("data");
            Assert(retried.GetProperty("state").GetString() == "Succeeded" && effects == beforeRetry && ids[^1] == ids[^2]);
            Assert(Request("/admission/bans", method: "POST", key: "ban-seven", body: "{\"userId\":7}").Body.GetProperty("data").GetProperty("state").GetString() == "Succeeded");
            book.Kick(7); book.Kick(8);
            var observed = Request("/players/moderation", "query").Body.GetProperty("data");
            Assert(observed.GetProperty("promotions")[0].GetProperty("level").GetString() == "None");
            Assert(observed.GetProperty("kickCooldowns").GetArrayLength() == 2);
            Assert(Request("/admission/kicks/7", method: "DELETE", key: "clear").Body.GetProperty("data").GetProperty("state").GetString() == "Succeeded");
            Assert(!book.WasKickedRecently(7) && book.WasKickedRecently(8) && book.IsBanned(7));
            book.Kick(7);
            Request("/admission/kicks/7", method: "DELETE", key: "clear");
            Assert(book.WasKickedRecently(7)); // A replay cannot clear a subsequent kick.
            Assert(Request("/admission/bans/7", method: "DELETE", key: "unban-seven").Body.GetProperty("data").GetProperty("state").GetString() == "Succeeded");
            Assert(!book.IsBanned(7) && book.WasKickedRecently(7) && book.WasKickedRecently(8));
            reject = true;
            var unavailable = Request("/players/moderation").Body.GetProperty("data");
            Assert(unavailable.GetProperty("promotions").ValueKind == JsonValueKind.Null && unavailable.GetProperty("kickCooldowns").GetArrayLength() == 2);
            Assert(Request("/players/7/promotion", method: "PUT", key: "old-wa", body: "{\"level\":\"Admin\",\"expectedRevision\":6}").Body.GetProperty("data").GetProperty("state").GetString() == "Failed");
            registry.Flush();
            using var restored = new GatewayRegistry(Path.Combine(directory, "registry"), Admission);
            Assert(restored.AdminOperationView(retried.GetProperty("operationId").GetString()!)?.State == Admin.AdminOperationState.Succeeded);
            var unfinished = restored.AdminOperationList(100).Single(operation => operation.IdempotencyKey == "old-wa");
            Assert(restored.ResumePlayerPromotion(unfinished.OperationId));
            Assert(restored.AdminOperationView(unfinished.OperationId)?.State == Admin.AdminOperationState.Running);
            Assert(new AdmissionBook(new AdmissionSettings()).KickCooldowns().Length == 0);
            Console.WriteLine($"Player moderation HTTP/auth/UDP/idempotency checks passed; {commands} WA commands, {effects} effects.");
        }
        finally
        {
            listener.Stop(); thread.Join(2000);
            stop.Cancel(); networkThread.Join(2000);
            node.Stop(); backend.Stop();
            gateway.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
