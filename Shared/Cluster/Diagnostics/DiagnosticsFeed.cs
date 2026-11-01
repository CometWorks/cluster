using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using CometWorks.ClusterGateway.AdminContract.V1;
using System.Text.Json;
using VRage.Utils;
using VRage.Game.ModAPI;

namespace ClusterPlugin;

internal sealed class DiagnosticsFeed : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly ConcurrentDictionary<ulong, Guid> pending = new ConcurrentDictionary<ulong, Guid>();
    private readonly Dictionary<ulong, DateTimeOffset> lastSent = new Dictionary<ulong, DateTimeOffset>();
    private readonly List<IMyPlayer> players = new List<IMyPlayer>();
    private readonly DateTimeOffset started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private readonly string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? "";
    private readonly string endpoint = Environment.GetEnvironmentVariable("CLUSTER_NODE_ENDPOINT") ?? "";
    private readonly string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
    private IMyMultiplayer multiplayer;
    private DiagnosticsSnapshot registry = new DiagnosticsSnapshot();
    private DateTimeOffset nextFetch;
    private int fetching;
    private bool disposed;

    internal DiagnosticsFeed(HttpClient client = null, Func<DateTimeOffset> clock = null)
    {
        http = client ?? ClusterHttp.Client(TimeSpan.FromSeconds(3));
        http.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        ClusterRuntime.AuthorizeRegistryClient(http);
        utcNow = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Update()
    {
        if (disposed) return;
        var current = MyAPIGateway.Session != null && MyAPIGateway.Multiplayer?.IsServer == true && node.Length != 0
            && !string.IsNullOrWhiteSpace(gateway) ? MyAPIGateway.Multiplayer : null;
        if (!ReferenceEquals(current, multiplayer))
        {
            multiplayer?.UnregisterSecureMessageHandler(DiagnosticsProtocol.Channel, OnMessage);
            multiplayer = current;
            pending.Clear();
            lastSent.Clear();
            multiplayer?.RegisterSecureMessageHandler(DiagnosticsProtocol.Channel, OnMessage);
        }
        if (multiplayer == null || pending.IsEmpty) return;

        DateTimeOffset now = utcNow();
        if (now >= nextFetch && Interlocked.CompareExchange(ref fetching, 1, 0) == 0)
        {
            nextFetch = now.AddSeconds(5);
            _ = Fetch();
        }

        players.Clear();
        MyAPIGateway.Players.GetPlayers(players);
        var connected = new HashSet<ulong>(players.Select(p => p.SteamUserId));
        foreach (ulong user in lastSent.Keys.Where(id => !connected.Contains(id)).ToArray()) lastSent.Remove(user);
        foreach (var request in pending.Take(32))
        {
            if (!pending.TryRemove(request.Key, out Guid session) || !connected.Contains(request.Key)) continue;
            if (lastSent.TryGetValue(request.Key, out var sent) && now - sent < TimeSpan.FromSeconds(2)) continue;
            lastSent[request.Key] = now;
            try
            {
                // Re-evaluate rank on the game thread immediately before every unicast.
                bool admin = MySession.Static?.IsUserAdmin(request.Key) == true;
                byte[] packet = DiagnosticsProtocol.Encode(Capture(now), session, admin);
                multiplayer.SendMessageTo(DiagnosticsProtocol.Channel, packet, request.Key);
            }
            catch (Exception exception) { MyLog.Default?.WriteLine("[ClusterPlugin] Could not send diagnostics: " + exception.GetType().Name); }
        }
    }

    private void OnMessage(ushort channel, byte[] data, ulong sender, bool fromServer)
    {
        if (!disposed && !fromServer && sender != 0 && pending.Count < 1024
            && DiagnosticsProtocol.TryRequest(data, out Guid session)) pending[sender] = session;
    }

    private DiagnosticsSnapshot Capture(DateTimeOffset now)
    {
        DiagnosticsSnapshot cached = Volatile.Read(ref registry);
        bool fresh = cached.RegistryAvailable && now.ToUnixTimeSeconds() - cached.CapturedAt < 15;
        NodeDiagnostics local = fresh && cached.Current.Name == node ? cached.Current : null;
        return new DiagnosticsSnapshot
        {
            Current = new NodeDiagnostics
            {
                Name = node, Endpoint = endpoint, AgeSeconds = local?.AgeSeconds ?? -1,
                ProcessUptimeSeconds = (long)(now - started).TotalSeconds,
                Epoch = local?.Epoch ?? 0, ServingSeconds = local?.ServingSeconds ?? -1,
                DrainAt = local?.DrainAt ?? 0, State = local?.State ?? "Unknown",
                Serving = local?.Serving ?? false, Spare = local?.Spare ?? false,
                Rotation = local?.Rotation ?? RotationState.Unknown, RotationDueAt = local?.RotationDueAt ?? 0,
                RotationExpiresAt = local?.RotationExpiresAt ?? 0
            },
            RegistryAvailable = fresh, Registry = SafeRegistry(), CapturedAt = cached.CapturedAt, ServerAt = now.ToUnixTimeSeconds(),
            TotalNodes = fresh ? cached.TotalNodes : 0, Nodes = fresh ? cached.Nodes : Array.Empty<NodeDiagnostics>()
        };
    }

    private string SafeRegistry() => Uri.TryCreate(gateway, UriKind.Absolute, out Uri uri) ? uri.Authority : "";

    private async Task Fetch()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, gateway + "/registry/client-diagnostics?node=" + Uri.EscapeDataString(node) + "&epoch=" + ClusterRuntime.RegistryNodeEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string token = Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_CURRENT") ?? Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN");
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            DiagnosticsSnapshot snapshot = JsonSerializer.Deserialize<DiagnosticsSnapshot>(json)
                ?? throw new InvalidOperationException("Empty diagnostic response");
            Volatile.Write(ref registry, snapshot);
        }
        catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException || exception is System.Text.Json.JsonException || exception is InvalidOperationException || exception is UriFormatException)
        {
            // Failed refreshes invalidate old data; no exception text or raw registry JSON reaches a client.
            Volatile.Write(ref registry, new DiagnosticsSnapshot());
        }
        finally { Volatile.Write(ref fetching, 0); }
    }

    public void Dispose()
    {
        disposed = true;
        multiplayer?.UnregisterSecureMessageHandler(DiagnosticsProtocol.Channel, OnMessage);
        multiplayer = null;
        pending.Clear();
        http.Dispose();
    }
}
