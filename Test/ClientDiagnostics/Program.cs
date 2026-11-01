using System.Net;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using ClusterPlugin;
using CometWorks.ClusterGateway.AdminContract.V1;
using ClusterClientDiagnostics;
using VRage.Game.ModAPI;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

void Reject(Action action, string message)
{
    try { action(); } catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException) { return; }
    throw new Exception(message);
}

var id = Guid.NewGuid();
Check(DiagnosticsProtocol.TryRequest(DiagnosticsProtocol.Request(id), out var requestId) && requestId == id, "request round trip");
Check(!DiagnosticsProtocol.TryRequest(new byte[18], out _), "reject malformed requests");
Check(!DiagnosticsProtocol.TryRequest(DiagnosticsProtocol.Request(Guid.Empty), out _), "reject empty session");
var snapshot = new DiagnosticsSnapshot
{
    Current = new NodeDiagnostics { Name = "node-1", Endpoint = "10.1.0.1:27016", Epoch = 1, AgeSeconds = 300, ProcessUptimeSeconds = 320, Rotation = RotationState.Assigned, RotationDueAt = 400 },
    RegistryAvailable = true, Registry = "private-registry", CapturedAt = 100, ServerAt = 100, TotalNodes = 1,
    Nodes = new[] { new NodeDiagnostics { Name = "secret-wa", Authority = true, AuthorityGeneration = 7 } }
};
byte[] basic = DiagnosticsProtocol.Encode(snapshot, id, false);
Check(!Encoding.UTF8.GetString(basic).Contains("private-registry") && !Encoding.UTF8.GetString(basic).Contains("secret-wa"), "basic wire payload redaction");
var decoded = DiagnosticsProtocol.Decode(basic, id);
Check(!decoded.Admin && decoded.Nodes.Length == 0 && decoded.Registry.Length == 0, "basic shape");
Check(decoded.Current.AgeSeconds == 300 && decoded.Current.ProcessUptimeSeconds == 320, "node age and process uptime stay distinct");
Check(decoded.Current.Rotation == RotationState.Assigned && decoded.Current.RotationDueAt == 400 && decoded.ServerAt == 100, "player receives only own rotation assignment and server time");
decoded = DiagnosticsProtocol.Decode(DiagnosticsProtocol.Encode(snapshot, id, true), id);
Check(decoded.Admin && decoded.Nodes[0].AuthorityGeneration == 7 && decoded.RegistryAvailable, "advanced round trip");
Reject(() => DiagnosticsProtocol.Decode(basic, Guid.NewGuid()), "session fencing");
for (int i = 0; i < basic.Length; i++) Reject(() => DiagnosticsProtocol.Decode(basic.Take(i).ToArray(), id), "reject truncated packet at " + i);
Reject(() => DiagnosticsProtocol.Decode(basic.Concat(new byte[1]).ToArray(), id), "reject trailing bytes");
Reject(() => DiagnosticsProtocol.Decode(new byte[DiagnosticsProtocol.MaxPacketBytes + 1], id), "reject oversized packet");
var maximumNode = new NodeDiagnostics { Name = new string('n', 512), Endpoint = new string('i', 512), State = new string('s', 512) };
var maximum = new DiagnosticsSnapshot { Current = maximumNode, Registry = new string('r', 512), TotalNodes = 100, Nodes = Enumerable.Repeat(maximumNode, 100).ToArray() };
byte[] bounded = DiagnosticsProtocol.Encode(maximum, id, true);
Check(bounded.Length <= DiagnosticsProtocol.MaxPacketBytes && DiagnosticsProtocol.Decode(bounded, id).Nodes.Length == DiagnosticsProtocol.MaxNodes, "worst case bounded packet");

DateTimeOffset now = DateTimeOffset.UtcNow;
var report = new DiagnosticsSnapshot
{
    RegistryAvailable = true, CapturedAt = now.ToUnixTimeSeconds(), ServerAt = now.ToUnixTimeSeconds(), TotalNodes = 2,
    Current = new NodeDiagnostics { Name = "node-1", Endpoint = "10.1.0.1:27016", Epoch = 8, AgeSeconds = 120,
        ServingSeconds = 60, Serving = true, State = "Active", Rotation = RotationState.Assigned,
        RotationDueAt = now.AddMinutes(10).ToUnixTimeSeconds(), RotationExpiresAt = now.AddMinutes(2).ToUnixTimeSeconds() }
};
report.Nodes = new[] { report.Current, new NodeDiagnostics { Name = "wa", Authority = true } };
string registryJson = System.Text.Json.JsonSerializer.Serialize(report);
Check(DiagnosticsText.Rotation(report.Current, report.ServerAt, 5).Contains("00:09:55"), "server-clock countdown");
Check(DiagnosticsText.Rotation(report.Current, report.ServerAt, 121) == "Rotation: unavailable", "publication expires without waiting for network refresh");
report.Current.Rotation = RotationState.Disabled;
Check(DiagnosticsText.Rotation(report.Current, report.ServerAt, 121) == "Rotation: unavailable", "disabled publication expires too");
report.Current.Rotation = RotationState.Assigned;
Environment.SetEnvironmentVariable("CLUSTER_NODE_ID", "node-1");
Environment.SetEnvironmentVariable("CLUSTER_NODE_ENDPOINT", "10.1.0.1:27016");
Environment.SetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY", "http://registry:28016");
var world = new MySession();
MySession.Static = world;
MyAPIGateway.Session = world;
var server = new FakeMultiplayer { IsServer = true };
MyAPIGateway.Multiplayer = server;
MyAPIGateway.Players.Online.Add(new Player(11));
MyAPIGateway.Players.Online.Add(new Player(22));
world.Admins.Add(22);
var handler = new RegistryHandler(registryJson);
using (var feed = new DiagnosticsFeed(new HttpClient(handler), () => now))
{
    feed.Update();
    server.Receive(DiagnosticsProtocol.Request(id), 11, false);
    server.Receive(DiagnosticsProtocol.Request(id), 22, false);
    feed.Update();
    Check(server.Sent.Count == 2, "one unicast per connected requester");
    Check(!DiagnosticsProtocol.Decode(server.Sent.Single(s => s.User == 11).Data, id).Admin, "server derives player mode");
    Check(DiagnosticsProtocol.Decode(server.Sent.Single(s => s.User == 11).Data, id).Current.AgeSeconds == 120, "current node age follows Cluster Registry registration");
    Check(DiagnosticsProtocol.Decode(server.Sent.Single(s => s.User == 11).Data, id).Current.Rotation == RotationState.Assigned, "assigned rotation reaches player through rank-filtered feed");
    Check(DiagnosticsProtocol.Decode(server.Sent.Single(s => s.User == 22).Data, id).Admin, "server derives admin mode");
    server.Receive(DiagnosticsProtocol.Request(id), 11, false);
    server.Receive(DiagnosticsProtocol.Request(id), 99, false);
    feed.Update();
    Check(server.Sent.Count == 2, "rate limit and disconnected sender rejection");
    now = now.AddSeconds(3);
    world.Admins.Remove(22);
    server.Receive(DiagnosticsProtocol.Request(id), 22, false);
    feed.Update();
    Check(!DiagnosticsProtocol.Decode(server.Sent.Last().Data, id).Admin, "rank downgrade applies to next response");
    world.Admins.Add(22);
    now = now.AddSeconds(5);
    handler.Fail = true;
    server.Receive(DiagnosticsProtocol.Request(id), 22, false);
    feed.Update();
    var unavailable = DiagnosticsProtocol.Decode(server.Sent.Last().Data, id);
    Check(unavailable.Admin && !unavailable.RegistryAvailable && unavailable.Nodes.Length == 0, "registry failure clears cached roster");
    Check(unavailable.Current.Name == "node-1" && unavailable.Current.Endpoint == "10.1.0.1:27016", "local diagnostics survive registry outage");
    Check(unavailable.Current.Rotation == RotationState.Unknown && unavailable.Current.RotationDueAt == 0, "outage clears stale rotation countdown");
    server.Receive(DiagnosticsProtocol.Request(id), 11, true);
    now = now.AddSeconds(3);
    feed.Update();
    Check(server.Sent.Count == 4, "server-origin requests rejected");
}
Check(server.Handlers == 0, "server unregisters on dispose");
var clientTransport = new FakeMultiplayer { MyId = 22 };
MyAPIGateway.Multiplayer = clientTransport;
world.Admins.Add(22);
using (var client = new ClusterClientDiagnostics.DiagnosticsClient())
{
    client.Update();
    Check(clientTransport.Handlers == 1 && DiagnosticsProtocol.TryRequest(clientTransport.LastRequest, out requestId), "client registers and requests");
    clientTransport.Receive(DiagnosticsProtocol.Encode(snapshot, requestId, true), 0, true);
    Check(client.CanShowAdvanced && MyAPIGateway.Utilities.Notifications.Count == 0, "no false switch on initial join");
    clientTransport.Receive(new byte[1], 0, true);
    Check(client.Snapshot.Current.Name == "node-1", "malformed server data ignored");
    snapshot.Current.Name = "node-2";
    snapshot.Current.Epoch = 2;
    clientTransport.Receive(DiagnosticsProtocol.Encode(snapshot, requestId, true), 77, false);
    Check(client.Snapshot.Current.Name == "node-1", "reject client-origin diagnostics");
    clientTransport.Receive(DiagnosticsProtocol.Encode(snapshot, requestId, true), 0, true);
    clientTransport.Receive(DiagnosticsProtocol.Encode(snapshot, requestId, true), 0, true);
    Check(MyAPIGateway.Utilities.Notifications.Count == 1, "one notification per switch");
    snapshot.Current.Epoch = 3;
    clientTransport.Receive(DiagnosticsProtocol.Encode(snapshot, requestId, true), 0, true);
    Check(MyAPIGateway.Utilities.Notifications.Count == 2, "same-name restart notification");
    world.Admins.Clear();
    client.Update();
    Check(!client.CanShowAdvanced && client.Snapshot.Nodes.Length == 0 && client.Snapshot.Registry == "", "client clears advanced data on demotion");
    MyAPIGateway.Session = new MySession();
    client.Update();
    Check(client.Snapshot == null, "session change clears cache");
    clientTransport.Receive(DiagnosticsProtocol.Encode(snapshot, requestId, true), 0, true);
    Check(client.Snapshot == null, "old session response rejected");
    MyAPIGateway.Session = null;
    client.Update();
    Check(clientTransport.Handlers == 0 && !client.Connected, "disconnect unregisters");
}

if (args.Length != 0)
{
    foreach (var target in new[] { ("Sandbox.Game.dll", "MyGuiScreenDebugOfficial", "RecreateControls"), ("Sandbox.Game.dll", "MyDX9Gui", "SwitchModDebugScreen") })
    {
        using var pe = new PEReader(File.OpenRead(Path.Combine(args[0], target.Item1)));
        var metadata = pe.GetMetadataReader();
        Check(metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Any(t => metadata.GetString(t.Name) == target.Item2 && t.GetMethods().Select(metadata.GetMethodDefinition).Any(m => metadata.GetString(m.Name) == target.Item3)), "F11 patch target exists: " + target.Item2);
    }
}
Console.WriteLine("PASS: protocol bounds/redaction, registry snapshot, rotation expiry/countdowns, server authorization/rate limits, client switches/session lifecycle, F11 targets.");

record Player(ulong SteamUserId) : IMyPlayer;

sealed class RegistryHandler(string json) : HttpMessageHandler
{
    public bool Fail;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(json) });
}

sealed class FakeMultiplayer : IMyMultiplayer
{
    private Action<ushort, byte[], ulong, bool> handler;
    public bool IsServer { get; set; }
    public bool MultiplayerActive => true;
    public ulong MyId { get; set; }
    public int Handlers { get; private set; }
    public readonly List<(ulong User, byte[] Data)> Sent = new();
    public byte[] LastRequest;
    public void RegisterSecureMessageHandler(ushort channel, Action<ushort, byte[], ulong, bool> callback) { handler += callback; Handlers++; }
    public void UnregisterSecureMessageHandler(ushort channel, Action<ushort, byte[], ulong, bool> callback) { handler -= callback; Handlers--; }
    public bool SendMessageTo(ushort channel, byte[] data, ulong user) { Sent.Add((user, data)); return true; }
    public bool SendMessageToServer(ushort channel, byte[] data) { LastRequest = data; return true; }
    public void Receive(byte[] data, ulong sender, bool fromServer) => handler?.Invoke(DiagnosticsProtocol.Channel, data, sender, fromServer);
}
