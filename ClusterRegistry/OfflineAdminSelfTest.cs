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
        registry.ClaimPartition(18, node.Node, node.Epoch, 0);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        int predictions = 0, profiles = 0, clears = 0, epoch = 0, calls = 0;
        bool legacy = false;
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
                if (legacy) { c.Response.StatusCode = 404; payload = new { }; }
                else if (path.EndsWith("/offline/admin")) payload = new { capability = "v1", groups = Array.Empty<object>(), results = Array.Empty<object>(), cache = new { entries = new[] { new Admin.OfflineRateEntry("key", 1, 1, 1, 0, false, epoch) } } };
                else if (path.EndsWith("/trigger/compensate-dry-run"))
                {
                    Assert(c.Request.QueryString["hours"] == "6.5" && c.Request.QueryString["save"] == $"17:{owned.Generation}:1");
                    Interlocked.Increment(ref predictions); payload = Array.Empty<object>();
                }
                else if (path.EndsWith("/trigger/profile")) { Interlocked.Increment(ref profiles); payload = new { complete = true }; }
                else if (path.EndsWith("/profiles/cache/clear"))
                {
                    Assert(Guid.TryParse(c.Request.QueryString["operation"], out _));
                    Interlocked.Increment(ref clears);
                    bool cleared = c.Request.QueryString["expectedEpoch"] == epoch.ToString();
                    if (cleared) epoch++;
                    payload = new { cleared, epoch };
                }
                else throw new InvalidOperationException("unexpected node route " + path);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, json);
                c.Response.ContentLength64 = bytes.Length; c.Response.OutputStream.Write(bytes); c.Response.Close();
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
            legacy = true;
            Assert(Request("/offline/cache").Status == 501);
            Assert(Request("/events").Json.GetRawText().Contains("admin.denied"));
            Assert(!catalog.Json.GetRawText().Contains(nodeControl) && !catalog.Json.GetRawText().Contains("manage"));
            Console.WriteLine($"offline-admin: scoped HTTP/typed catalog/exact targets/no-mutation/finite hours/CAS/replay/legacy/audit passed ({calls} private node calls)");
        }
        finally { listener.Stop(); fake.Stop(); thread.Join(1000); nodeThread.Join(1000); }
    }
}
