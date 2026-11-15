using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    // Real node HTTP authentication and relay IPC, with Steam's verdict scripted on the other end.
    private static void TestSteamSkins()
    {
        string directory = Directory.CreateTempSubdirectory("steam-skins-").FullName;
        string socket = Path.Combine(directory, "relay.sock");
        int control = FreeTcpPort();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()),
            new(IPAddress.Loopback, control), false, null, null, null, null, TimeSpan.FromMinutes(1), Admission, "manage", [],
            SteamListen: new(IPAddress.Loopback, FreeUdpPort()), ExternalRelay: true, RelaySocket: socket);
        using var gateway = new DirectGateway(options);
        using var server = new RelayIpcServer(socket);
        server.Start();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(DirectGateway).GetField("_relay", flags)!.SetValue(gateway, server);
        var sessions = (IDictionary)typeof(DirectGateway).GetField("_clients", flags)!.GetValue(gateway)!;
        Type sessionType = typeof(DirectGateway).GetNestedType("ClientSession", BindingFlags.NonPublic)!;
        const ulong user = 76561198000000001, relaySession = 71;
        object Connect(string frontend = "steam", bool admitted = true)
        {
            object session = Activator.CreateInstance(sessionType, [user, options.Backend, null])!;
            sessionType.GetField("Client")!.SetValue(session, new ExternalClientChannel(server, user, relaySession, frontend, "test"));
            if (admitted) sessionType.GetField("Credential")!.SetValue(session, new DirectGateway.ClientCredential("Steam", user, user, "Pilot", false));
            sessions[user] = session;
            return session;
        }
        using var relay = new ScriptedRelay(socket);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{control}/"); listener.Start();
        MethodInfo dispatch = typeof(DirectGateway).GetMethod("HandleControl", flags)!;
        var thread = new Thread(() =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = listener.GetContext(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => dispatch.Invoke(gateway, [context]));
            }
        }) { IsBackground = true };
        thread.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<HttpResponseMessage> Request(string data = "AQID", string? token = CurrentToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{control}/registry/steam/skins");
            if (token != null) request.Headers.Add("Authorization", "Bearer " + token);
            request.Content = new StringContent(JsonSerializer.Serialize(new { userId = user, data }), Encoding.UTF8, "application/json");
            return await http.SendAsync(request);
        }
        try
        {
            object session = Connect();
            Assert(Request(token: "wrong").Result.StatusCode == HttpStatusCode.Forbidden);
            Assert(Request("invalid base64!").Result.StatusCode == HttpStatusCode.BadRequest);
            Assert(Request("").Result.StatusCode == HttpStatusCode.BadRequest);
            Connect(frontend: "direct");
            Assert(Request().Result.StatusCode == HttpStatusCode.NotFound);
            Connect(admitted: false);
            Assert(Request().Result.StatusCode == HttpStatusCode.NotFound);
            sessions[user] = session;

            Task<HttpResponseMessage> checking = Request();
            RelayIpcMessage asked = relay.Read(RelayIpcType.SteamSkins);
            Assert(asked.Correlation != 0 && asked.Json.GetProperty("clientId").GetUInt64() == user
                && asked.Json.GetProperty("session").GetUInt64() == relaySession && asked.Json.GetProperty("data").GetString() == "AQID");
            relay.Send(RelayIpcType.SteamSkins, asked.Correlation, new { skins = new[] { "Battered_Armor" } });
            using HttpResponseMessage answer = checking.Result;
            Assert(answer.StatusCode == HttpStatusCode.OK && answer.Content.ReadAsStringAsync().Result.Contains("Battered_Armor"));
            Assert(sessionType.GetField("Skins")!.GetValue(session) is string[] skins && skins.SequenceEqual(new[] { "Battered_Armor" }));

            checking = Request();
            asked = relay.Read(RelayIpcType.SteamSkins);
            relay.Send(RelayIpcType.SteamSkins, asked.Correlation, new { skins = (string[]?)null });
            Assert(checking.Result.StatusCode == HttpStatusCode.UnprocessableEntity);

            checking = Request();
            asked = relay.Read(RelayIpcType.SteamSkins);
            object reconnected = Connect();
            relay.Send(RelayIpcType.SteamSkins, asked.Correlation, new { skins = new[] { "Silver_Armor" } });
            Assert(checking.Result.StatusCode == HttpStatusCode.Conflict);
            Assert(sessionType.GetField("Skins")!.GetValue(reconnected) == null);
        }
        finally
        {
            listener.Stop(); thread.Join(5000);
            Directory.Delete(directory, true);
        }
        Console.WriteLine("steam-skins: authenticated node requests reach the external relay, cache verified skins, reject invalid data and discard reconnect-stale answers");
    }
}
