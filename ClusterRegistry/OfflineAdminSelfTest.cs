using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    private static void TestOfflineAdmin()
    {
        TestOfflineMetadataBoundary();
        string directory = Directory.CreateTempSubdirectory("offline-admin-").FullName;
        string tokens = Path.Combine(directory, "tokens.json");
        static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        File.WriteAllText(tokens, JsonSerializer.Serialize(new { tokens = new[] { new { name = "reader", scope = "query", sha256 = Hash("query") } } }));
        int port = FreeTcpPort(), nodePort = FreeTcpPort();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()),
            new(IPAddress.Loopback, port), false, null, null, null, Path.Combine(directory, "registry"),
            TimeSpan.FromMinutes(10), Admission, "manage", [], AdminTokensPath: tokens);
        using var gateway = new DirectGateway(options);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var registry = (GatewayRegistry)typeof(DirectGateway).GetField("_registry", flags)!.GetValue(gateway)!;
        string nodeControl = $"http://127.0.0.1:{nodePort}";
        var node = registry.RegisterNode(Join("one", "ep-one", Guid.NewGuid()) with { ControlEndpoint = nodeControl });
        var authority = registry.RegisterNode(Join("wa", "ep-wa", Guid.NewGuid()) with { Role = NodeRole.WA, ControlEndpoint = nodeControl });
        registry.Heartbeat(node.Node, node.Epoch, []);
        var owned = registry.ClaimPartition(17, node.Node, node.Epoch, 0) with { };
        registry.ReportRemoteUse(node.Node, node.Epoch, [new(17, owned.Generation)], new() { [17] = new(owned.Generation, [81]) });
        Assert(registry.AdminPartitions().Single().RemoteUsers is [81]);
        registry.ReportRemoteUse(node.Node, node.Epoch, [new(17, owned.Generation)], new() { [17] = new(owned.Generation, []) });
        registry.RegisterSave(new(17, owned.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 1, "a", 1, "one/17", nodeControl);
        registry.TakeOffline(new(17, node.Node, node.Epoch, owned.Generation, 1, 0, 0, 0, 1, [[0, 0, 0, 1]], [170], 1, "[]", "[]")
        { OfflineGameFrame = 100, ProfileSummary = JsonSerializer.SerializeToElement(new[] { new { Group = 170L, Confidence = "Exact", Unknowns = Array.Empty<string>() } }) });
        var profileOwner = registry.ClaimPartition(18, node.Node, node.Epoch, 0);
        registry.RegisterSave(new(18, profileOwner.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 1, "b", 1, "one/18", nodeControl);
        var other = registry.RegisterNode(Join("two", "ep-two", Guid.NewGuid()) with { ControlEndpoint = nodeControl });
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        int predictions = 0, profiles = 0, clears = 0, epoch = 0, calls = 0;
        bool legacy = false;
        string? capabilityReply = null, clearReply = null;
        Action? afterCapability = null, afterProfile = null;
        int clearStatus = 200;
        var ledger = new Dictionary<string, object>();
        using var fake = new HttpListener(); fake.Prefixes.Add(nodeControl + "/"); fake.Start();
        var nodeThread = new Thread(() =>
        {
            while (fake.IsListening)
            {
                HttpListenerContext c;
                try { c = fake.GetContext(); } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                Interlocked.Increment(ref calls);
                Assert(c.Request.Headers["Authorization"] != "Bearer query");
                string path = c.Request.Url!.AbsolutePath;
                object payload;
                string? raw = null;
                if (legacy) { c.Response.StatusCode = 404; payload = new { }; }
                else if (path.EndsWith("/offline/admin"))
                {
                    payload = new { capability = "v1", groups = Array.Empty<object>(), results = Array.Empty<object>(), cache = new { entries = new[] { new Admin.OfflineRateEntry("key", 1, 1, 1, 0, false, epoch) } } };
                    raw = capabilityReply;
                    var change = afterCapability; afterCapability = null; change?.Invoke();
                }
                else if (path.EndsWith("/trigger/compensate-dry-run"))
                {
                    Assert(c.Request.QueryString["hours"] == "6.5" && c.Request.QueryString["save"] == $"17:{owned.Generation}:1");
                    Interlocked.Increment(ref predictions); payload = Array.Empty<object>();
                }
                else if (path.EndsWith("/trigger/profile")) { Interlocked.Increment(ref profiles); payload = new { task = "profile", partition = 18UL, complete = true }; var change = afterProfile; afterProfile = null; change?.Invoke(); }
                else if (path.EndsWith("/profiles/cache/clear"))
                {
                    Assert(Guid.TryParse(c.Request.QueryString["operation"], out _));
                    Interlocked.Increment(ref clears);
                    string id = c.Request.QueryString["operation"]!;
                    if (!ledger.TryGetValue(id, out payload!))
                    {
                        bool cleared = c.Request.QueryString["expectedEpoch"] == epoch.ToString();
                        if (cleared) epoch++;
                        ledger[id] = payload = new { cleared, code = cleared ? (string?)null : "epoch_mismatch", epoch };
                    }
                    raw = clearReply; c.Response.StatusCode = clearStatus;
                }
                else throw new InvalidOperationException("unexpected node route " + path);
                byte[] bytes = raw == null ? JsonSerializer.SerializeToUtf8Bytes(payload, json) : Encoding.UTF8.GetBytes(raw);
                try { c.Response.ContentLength64 = bytes.Length; c.Response.OutputStream.Write(bytes); c.Response.Close(); }
                catch (HttpListenerException) { c.Response.Abort(); } // Size/deadline refusal closes the reader.
            }
        }) { IsBackground = true }; nodeThread.Start();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
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
        (int Status, JsonElement Json) Request(string path, string? token = "query", string method = "GET", string? key = null, string? body = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}/admin/v1" + path);
            if (token != null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            if (key != null) request.Headers.Add("Idempotency-Key", key);
            if (method != "GET") request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
            using var response = http.Send(request);
            Assert(response.Headers.GetValues("X-Cluster-Gateway-Protocol").Single() == "1");
            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            return ((int)response.StatusCode, document.RootElement.Clone());
        }
        try
        {
            Assert(Request("/offline/partitions", null).Status == 401);
            Assert(Request("/capabilities").Json.GetProperty("data").GetProperty("features").GetProperty("offlinePartitions").GetString() == "v1");
            var catalog = Request("/offline/partitions");
            Assert(catalog.Status == 200 && catalog.Json.GetProperty("data").GetProperty("partitions")[0].GetProperty("holders")[0].GetString() == "one");
            Assert(Request("/partitions").Json.GetProperty("data")[0].GetProperty("offline").GetBoolean());
            var before = JsonSerializer.Serialize(registry.OfflineRow(17));
            Assert(Request("/offline/details?node=one&partition=17").Status == 200);
            Assert(Request("/offline/prediction?node=one&partition=17&hours=6.5").Status == 200 && predictions == 1 && profiles == 0 && clears == 0);
            Assert(before == JsonSerializer.Serialize(registry.OfflineRow(17)) && registry.AdminOperationList(10).Length == 0);
            foreach (string invalid in new[] { "NaN", "Infinity", "-1", "169", "" })
                Assert(Request("/offline/prediction?node=one&partition=17&hours=" + invalid).Status == 400);
            Assert(predictions == 1);
            Assert(Request("/offline/prediction?node=one&partition=18&hours=6").Status == 409);
            Assert(Request("/offline/details?node=http%3A%2F%2Fbad&partition=17").Status == 503);
            Assert(Request("/triggers/profile?node=one&partition=18", method: "POST", key: "deny").Status == 403 && profiles == 0);
            Assert(Request("/offline/cache/clear", method: "POST", key: "deny", body: "{\"key\":\"key\",\"expectedEpoch\":0}").Status == 403 && clears == 0);
            Assert(Request("/offline/cache/clear", "manage", "POST", body: "{\"key\":\"key\",\"expectedEpoch\":0}").Status == 400);
            Assert(Request("/offline/cache/clear", "manage", "POST", "missing", "{\"key\":\"key\"}").Status == 400);
            Assert(Request("/offline/cache").Status == 200);
            var cleared = Request("/offline/cache/clear", "manage", "POST", "clear", "{\"key\":\"key\",\"expectedEpoch\":0}");
            Assert(cleared.Status == 200 && cleared.Json.GetProperty("data").GetProperty("state").GetString() == "Succeeded" && epoch == 1);
            Assert(Request("/offline/cache/clear", "manage", "POST", "clear", "{\"key\":\"key\",\"expectedEpoch\":0}").Json.GetProperty("data").GetProperty("operationId").GetString()
                == cleared.Json.GetProperty("data").GetProperty("operationId").GetString() && clears == 1);
            Assert(Request("/offline/cache/clear", "manage", "POST", "stale", "{\"key\":\"key\",\"expectedEpoch\":0}").Json.GetProperty("data").GetProperty("error").GetProperty("code").GetString() == "epoch_mismatch" && epoch == 1);
            Assert(Request("/offline/cache/clear", "manage", "POST", "clear", "{\"key\":\"key\",\"expectedEpoch\":1}").Status == 409);
            Assert(Request("/triggers/profile?node=one&partition=18", "manage", "POST", "profile").Json.GetProperty("data").GetProperty("state").GetString() == "Succeeded" && profiles == 1);
            Assert(Request("/triggers/profile?node=one&partition=18", "manage", "POST", "profile").Status == 200 && profiles == 1);
            // Real registry identity changes at the two transport boundaries. The old
            // node still answers profile; the coordinator must refuse rather than redirect.
            void MoveProfileOwner()
            {
                profileOwner = registry.ReleasePartition(18, node.Node, node.Epoch, profileOwner.Generation);
                profileOwner = registry.ClaimPartition(18, other.Node, other.Epoch, profileOwner.Generation);
            }
            void RestoreProfileOwner()
            {
                profileOwner = registry.ReleasePartition(18, other.Node, other.Epoch, profileOwner.Generation);
                profileOwner = registry.ClaimPartition(18, node.Node, node.Epoch, profileOwner.Generation);
            }
            afterCapability = MoveProfileOwner;
            var preflightRace = Request("/triggers/profile?node=one&partition=18", "manage", "POST", "profile-preflight-race").Json.GetProperty("data");
            Assert(preflightRace.GetProperty("error").GetProperty("code").GetString() == "offline_target_mismatch" && profiles == 1);
            RestoreProfileOwner();
            afterProfile = MoveProfileOwner;
            var completionRace = Request("/triggers/profile?node=one&partition=18", "manage", "POST", "profile-completion-race").Json.GetProperty("data");
            Assert(completionRace.GetProperty("state").GetString() == "Failed" && profiles == 2);
            RestoreProfileOwner();
            Assert(Request("/triggers/profile?node=one&partition=18", "manage", "POST", "profile-current").Json.GetProperty("data").GetProperty("state").GetString() == "Succeeded" && profiles == 3);
            foreach (string invalid in new[] { "{}", "[]", "{\"capability\":3}", "{\"capability\":\"v2\"}" })
            {
                capabilityReply = invalid;
                Assert(Request("/offline/cache").Status == 501);
                int previous = clears;
                var refusal = Request("/offline/cache/clear", "manage", "POST", "unsupported-" + invalid, JsonSerializer.Serialize(new { key = "key", expectedEpoch = epoch })).Json.GetProperty("data");
                Assert(refusal.GetProperty("state").GetString() == "Failed" && refusal.GetProperty("error").GetProperty("code").GetString() == "offline_unsupported" && clears == previous);
            }
            capabilityReply = null;
            foreach (string invalid in new[] { "{}", "[]", "{", "{\"cleared\":\"true\",\"epoch\":2}", "{\"cleared\":true}", "{\"cleared\":true,\"epoch\":\"2\"}", "{\"cleared\":true,\"epoch\":-1}", "{\"cleared\":false,\"epoch\":0}", "{\"cleared\":true,\"epoch\":999999999999}" })
            {
                clearReply = invalid;
                string key = "malformed-" + invalid;
                string body = JsonSerializer.Serialize(new { key = "key", expectedEpoch = epoch });
                int oldEpoch = epoch;
                var uncertain = Request("/offline/cache/clear", "manage", "POST", key, body).Json.GetProperty("data");
                string id = uncertain.GetProperty("operationId").GetString()!;
                Assert(uncertain.GetProperty("state").GetString() == "Failed" && uncertain.GetProperty("error").GetProperty("code").GetString() == "offline_clear_uncertain" && epoch == oldEpoch + 1);
                // Failed preflight on retry cannot erase uncertainty about the first effect.
                capabilityReply = "{}";
                Assert(Request("/offline/cache/clear", "manage", "POST", key, body).Json.GetProperty("data").GetProperty("error").GetProperty("code").GetString() == "offline_clear_uncertain");
                capabilityReply = null; clearReply = null;
                var reconciled = Request("/offline/cache/clear", "manage", "POST", key, body).Json.GetProperty("data");
                Assert(reconciled.GetProperty("state").GetString() == "Succeeded" && reconciled.GetProperty("operationId").GetString() == id && epoch == oldEpoch + 1);
                int previous = clears;
                Assert(Request("/offline/cache/clear", "manage", "POST", key, body).Json.GetProperty("data").GetProperty("operationId").GetString() == id && clears == previous);
            }
            foreach (string invalid in new[] { "{", "{\"capability\":\"v1\",\"padding\":\"" + new string('x', DirectGateway.OfflineMetadataLimit) + "\"}" })
            {
                capabilityReply = invalid;
                var failedRead = Request("/offline/cache");
                string expected = invalid == "{" ? "node_response_invalid" : "node_response_too_large";
                Assert(failedRead.Status == 502 && failedRead.Json.GetProperty("error").GetProperty("code").GetString() == expected);
                var failedMutation = Request("/offline/cache/clear", "manage", "POST", "bad-preflight-" + expected, JsonSerializer.Serialize(new { key = "key", expectedEpoch = epoch })).Json.GetProperty("data");
                Assert(failedMutation.GetProperty("state").GetString() == "Failed" && failedMutation.GetProperty("error").GetProperty("code").GetString() == expected);
            }
            capabilityReply = null;
            clearStatus = 409;
            string refusalBody = JsonSerializer.Serialize(new { key = "key", expectedEpoch = epoch });
            var nonSuccess = Request("/offline/cache/clear", "manage", "POST", "http-refusal", refusalBody).Json.GetProperty("data");
            Assert(nonSuccess.GetProperty("state").GetString() == "Failed" && nonSuccess.GetProperty("error").GetProperty("code").GetString() == "offline_clear_uncertain");
            clearStatus = 200;
            Assert(Request("/offline/cache/clear", "manage", "POST", "http-refusal", refusalBody).Json.GetProperty("data").GetProperty("state").GetString() == "Succeeded");
            Assert(registry.AdminOperationList(100).All(o => o.State != Admin.AdminOperationState.Running));
            Console.WriteLine("offline-admin boundaries: preflight/completion owner changes refused; malformed schema/syntax/non2xx settle and original-GUID ledger retry applies once");
            legacy = true;
            Assert(Request("/offline/cache").Status == 501);
            Assert(Request("/events").Json.GetRawText().Contains("admin.denied"));
            Assert(!catalog.Json.GetRawText().Contains(nodeControl) && !catalog.Json.GetRawText().Contains("manage"));
            Console.WriteLine($"offline-admin: scoped HTTP/typed catalog/exact targets/no-mutation/finite hours/CAS/replay/legacy/audit passed ({calls} private node calls)");
        }
        finally { listener.Stop(); fake.Stop(); thread.Join(1000); nodeThread.Join(1000); }
    }
}
