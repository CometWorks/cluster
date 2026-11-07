using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClusterProtocol;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    // SE-1197: a node forwards an admin's F3 kick or ban with its join token. Real admin HTTP, auth and the
    // admission book; the sessions are synthetic (no transport), as in TestPlayerModeration.
    private static void TestNodeModeration()
    {
        string directory = Directory.CreateTempSubdirectory("node-moderation-").FullName;
        string admission = Path.Combine(directory, "admission.json");
        // 100 and 102 are cluster admins; 101 is a player. A 2 s kick timeout, as a test sets it.
        File.WriteAllText(admission, "{\"administrators\":[100,102],\"kickTimeoutSeconds\":2}");
        int control = FreeTcpPort();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()),
            new(IPAddress.Loopback, control), false, null, null, null, Path.Combine(directory, "registry"),
            TimeSpan.FromMinutes(10), Admission, "manage", [], AdmissionConfig: admission);
        var gateway = new DirectGateway(options);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var registry = (GatewayRegistry)typeof(DirectGateway).GetField("_registry", flags)!.GetValue(gateway)!;
        var book = (AdmissionBook)typeof(DirectGateway).GetField("_admission", flags)!.GetValue(gateway)!;
        var sessions = (System.Collections.IDictionary)typeof(DirectGateway).GetField("_clients", flags)!.GetValue(gateway)!;
        var sessionType = typeof(DirectGateway).GetNestedType("ClientSession", BindingFlags.NonPublic)!;
        // Each node's backend; a session's node is the one serving its backend endpoint, as for a real arrival.
        var backends = new Dictionary<string, IPEndPoint>();
        foreach (string node in new[] { "node-1", "node-2" })
        {
            backends[node] = new IPEndPoint(IPAddress.Loopback, FreeUdpPort());
            registry.RegisterNode(Join(node, backends[node].ToString(), Guid.NewGuid()));
        }
        void Connect(ulong client, string node) => sessions[client] = Activator.CreateInstance(sessionType,
            [client, backends[node], null]);
        Connect(100, "node-1"); Connect(101, "node-1"); Connect(200, "node-2"); Connect(201, "node-1");

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
        int keys = 0;
        (int Status, JsonElement Body) Request(string path, string? moderator, string? node = "node-1", string method = "POST",
            string? body = null, string? token = CurrentToken)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{control}/admin/v1" + path);
            if (token != null) request.Headers.Add("Authorization", "Bearer " + token);
            if (moderator != null) request.Headers.Add(ClusterNet.ModeratorHeader, moderator);
            if (node != null) request.Headers.Add(ClusterNet.NodeHeader, node);
            request.Headers.Add("Idempotency-Key", "k" + keys++);
            if (method != "GET") request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
            using var response = http.Send(request);
            using var json = JsonDocument.Parse(response.Content.ReadAsStream());
            return ((int)response.StatusCode, json.RootElement.Clone());
        }
        string State((int Status, JsonElement Body) answer) => answer.Body.GetProperty("data").GetProperty("state").GetString()!;
        string Code((int Status, JsonElement Body) answer) => answer.Body.GetProperty("error").GetProperty("code").GetString()!;
        try
        {
            // An admin on node-1 kicks a player on ANOTHER node and one on its own: both are kicked cluster-wide,
            // the cooldown is the configured one, and the audit actor names the node and the admin.
            foreach (ulong target in new ulong[] { 200, 201 })
            {
                var kicked = Request($"/clients/{target}/kick", "100");
                Assert(kicked.Status == 200 && State(kicked) == "Succeeded" && book.WasKickedRecently(target) && !sessions.Contains(target));
            }
            var cooldown = book.KickCooldowns().Single(entry => entry.UserId == 200);
            Assert(cooldown.ExpiresAt - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(2));
            Assert(registry.AdminOperationList(100).All(operation => operation.Actor == "node:node-1/admin:100"));
            // A ban of a player who is online nowhere lands in the cluster book and is persisted.
            Assert(State(Request("/admission/bans", "100", body: "{\"userId\":300}")) == "Succeeded" && book.IsBanned(300));
            Assert(File.ReadAllText(Path.Combine(directory, "registry", "admission-bans.json")).Contains("300"));

            // Refused, with the join token: no moderator (the join token alone opens nothing), a malformed one, a
            // non-admin, an admin who is offline, an admin on another node.
            Connect(200, "node-2");
            book.ClearKickCooldown(200);
            Assert(Request("/clients/200/kick", null).Status == 401);
            foreach ((string? moderator, string? node) in new (string?, string?)[]
                     { ("x", "node-1"), ("101", "node-1"), ("102", "node-1"), ("100", "node-2"), ("100", null) })
            {
                var refused = Request("/clients/200/kick", moderator, node);
                Assert(refused.Status == 403 && Code(refused) == "moderator_refused");
                refused = Request("/admission/bans", moderator, node, body: "{\"userId\":200}");
                Assert(refused.Status == 403 && Code(refused) == "moderator_refused");
            }
            Assert(!book.WasKickedRecently(200) && !book.IsBanned(200) && sessions.Contains(200UL));

            // Every other admin route stays closed to the join token, even with a qualified admin named.
            foreach ((string path, string method) in new[] { ("/admission/bans/300", "DELETE"), ("/admission/kicks/200", "DELETE"),
                         ("/admission/bans", "GET"), ("/players/7/promotion", "PUT"), ("/shutdown", "POST"), ("/clients/abc/kick", "POST") })
                Assert(Request(path, "100", method: method, body: "{\"level\":\"Admin\",\"expectedRevision\":0}").Status == 401);
            // A wrong token is a 401, not a moderator refusal.
            Assert(Request("/clients/200/kick", "100", token: "not-the-join-token").Status == 401);
            Assert(book.IsBanned(300) && !book.WasKickedRecently(200));

            // The admin token keeps working without a moderator, and the kick timeout defaults to vanilla's 5 minutes.
            Assert(State(Request("/clients/200/kick", null, token: "manage")) == "Succeeded");
            var standard = new AdmissionBook(new AdmissionSettings());
            standard.Kick(1);
            Assert(standard.KickCooldowns()[0].ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(299));
            // A loopback Gateway with no admin token opens the admin contract to anyone, but a request naming a
            // moderator is still held to the moderator rules (the bench runs this way).
            var field = typeof(DirectGateway).GetField("_options", flags)!;
            field.SetValue(gateway, options with { AdminToken = null });
            Connect(200, "node-2");
            book.ClearKickCooldown(200);
            var open = Request("/clients/200/kick", "101", token: null);
            Assert(open.Status == 403 && Code(open) == "moderator_refused" && !book.WasKickedRecently(200));
            Assert(Request("/admission/bans/300", "100", method: "DELETE", token: null).Status == 401 && book.IsBanned(300));
            Assert(State(Request("/clients/200/kick", "100", token: null)) == "Succeeded" && book.WasKickedRecently(200));
            Assert(Request("/admission/bans", null, method: "GET", token: null).Status == 200);
            Console.WriteLine("Node moderation (SE-1197) join-token kick/ban, refusals and route confinement passed.");
        }
        finally
        {
            listener.Stop(); thread.Join(2000);
            gateway.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
