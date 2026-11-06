using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ClusterRegistry;

/// <summary>What the join logic needs from a pending connection, whichever relay terminated it.</summary>
internal interface IJoinRequest
{
    /// <summary>The Direct Transport handshake bytes, or null when the relay already decoded them.</summary>
    NetDataReader? Data { get; }
    object RemoteEndPoint { get; }
    void Reject();
    /// <summary>Accepts the connection and returns the client half of the session, or null when there is no
    /// new connection to bind it to (see <see cref="LiteNetLibJoinRequest.Accept"/>).</summary>
    ClientChannel? Accept(object session);
    /// <summary>Answers "not yet" if the relay re-asks by itself (true); false leaves holding to the caller.</summary>
    bool Hold(string reason);
}

/// <summary>The in-process relay's pending connection.</summary>
internal sealed class LiteNetLibJoinRequest(ConnectionRequest request) : IJoinRequest
{
    public NetDataReader? Data => request.Data;
    public object RemoteEndPoint => request.RemoteEndPoint;
    public void Reject() => request.Reject();
    /// <summary>
    /// #1221: LiteNetLib 1.3.1's OnConnectionSolved returns whatever peer already sits at the request's
    /// address, in any state and with no Connect event, and only creates one when there is none. A client
    /// that retries from one port while the gateway refuses or holds its requests leaves such peers behind
    /// (a refusal creates one). A session bound to a stale one never ends: nothing connects it, so nothing
    /// disconnects it, and single-flight then refuses the identity until the gateway restarts (72 identities
    /// on the #1191 octahedron run). One bound to a live one would take another session's peer. Neither is a
    /// new connection: the client's next attempt meets no request and LiteNetLib makes a fresh one.
    /// Measured with LiteNetLib 1.3.1 on loopback (8 clients retrying from fixed ports, a third of the
    /// requests refused, the rest held 5-60 ms): 2396 of 39830 accepts returned a Disconnected peer and
    /// 5560 a peer an earlier accept had already tagged.
    /// </summary>
    public ClientChannel? Accept(object session)
    {
        NetPeer? peer = request.Accept();
        if (peer == null || peer.Tag != null || peer.ConnectionState != ConnectionState.Connected)
        {
            Console.Error.WriteLine($"LiteNetLib accepted {request.RemoteEndPoint} onto "
                + (peer == null ? "no peer" : $"an existing peer ({peer.ConnectionState}{(peer.Tag != null ? ", bound to a session" : "")})"));
            return null;
        }
        var channel = new DirectClientChannel(peer);
        peer.Tag = session;
        return channel;
    }
    public bool Hold(string reason) => false;
}

/// <summary>
/// T-0229: the client half of a session terminated by gateway-rs. Sends and disconnects become relay
/// IPC primitives (Docs/Gateway/RelayIpc.md); the reliable backlog and credit live in gateway-rs.
/// </summary>
internal sealed class ExternalClientChannel(RelayIpcServer relay, ulong clientId, ulong session, string frontend, string remote) : ClientChannel
{
    private volatile bool _connected = true;
    public override string Frontend => frontend;
    public override string Remote => remote;
    public override bool Connected => _connected && relay.Connected;
    public override int ReliableBacklogPackets => 0;
    public ulong Session => session;

    public override void Send(GatewayPacket packet)
    {
        if (!Connected) return;
        byte[] frame = new byte[18 + packet.Payload.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(frame, clientId);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(8), session);
        frame[16] = checked((byte)packet.Channel);
        frame[17] = (byte)packet.Delivery;
        packet.Payload.CopyTo(frame.AsSpan(18));
        try { relay.Send(RelayIpcType.ClientSend, 0, frame); }
        catch (IOException) { }
    }

    public override void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        try { relay.Send(RelayIpcType.Disconnect, 0, new { clientId, session, reason = "gateway" }); }
        catch (IOException) { }
    }

    /// <summary>gateway-rs reported the client gone.</summary>
    public void MarkDisconnected() => _connected = false;
}

internal sealed partial class DirectGateway
{
    /// <summary>T-0229: the IPC to gateway-rs when <c>--relay external</c>; null for the in-process relay.</summary>
    private RelayIpcServer? _relay;
    private readonly object _relayStateLock = new();
    private bool _lastPushedPaused;
    /// <summary>
    /// Serialises the admission decisions gateway-rs asks for (joins and Steam verdicts). The
    /// in-process relay makes them on one thread; without this the thread pool ran them in
    /// parallel and every one passed the member-limit check before any was counted (74 bench
    /// clients against a limit of 16 admitted 32).
    /// </summary>
    private readonly object _relayAdmissionLock = new();
    /// <summary>
    /// Node-link Global/Plugin frames from gateway-rs, handled one at a time in arrival order. The
    /// in-process relay routes them inline on its network thread; a thread-pool item per frame (as
    /// here until 2026-09-24) forwarded global-state frames OUT OF ORDER, and node reports are
    /// ordered deltas on a periodic full base (AntennaGraphService and others). The first live run
    /// with gateway-rs had wa-1 at 15 GB within 8 min of the fleet joining; the internal relay on
    /// the same build stayed at 1.8 GB. A dedicated thread, not the IPC reader, so that routing
    /// (which sends back over the same IPC socket) can never block the reader.
    /// </summary>
    private readonly OrderedWorker _relayNodeFrames = new("relay-node-frames");
    /// <summary>Client messages from gateway-rs, in arrival order for the same reason (the in-process
    /// relay runs OnClientMessage inline, per client in order).</summary>
    private readonly OrderedWorker _relayClientMessages = new("relay-client-messages");
    private volatile bool _steamPublishDirty = true;
    private long _nextSteamPublish;
    private Timer? _steamPublishTimer;

    /// <summary>A Steam frontend is configured: in process (<see cref="_steam"/>) or in gateway-rs.</summary>
    private bool SteamEnabled => _options.SteamListen != null;

    /// <summary>The relay session whose hold was last logged, per client (see <see cref="HoldJoin"/>).</summary>
    private readonly ConcurrentDictionary<ulong, ulong> _externalHoldLogged = new();

    private bool FirstExternalHold(ulong clientId, ulong session)
    {
        ulong previous = _externalHoldLogged.GetValueOrDefault(clientId);
        _externalHoldLogged[clientId] = session;
        return previous != session;
    }

    private sealed class ExternalJoinRequest(DirectGateway gateway, RelayIpcMessage message, ulong session, string service, string remote) : IJoinRequest
    {
        private int _answered;
        public ulong Session => session;
        public NetDataReader? Data => null;
        public object RemoteEndPoint => remote;
        public void Reject() => Answer(new { decision = "reject" });
        public ClientChannel Accept(object sessionObject)
        {
            var created = (ClientSession)sessionObject;
            // Everything a client message needs is in place BEFORE the answer goes out. gateway-rs forwards
            // the client's first packet (its login) as soon as it reads the accept, and TryRelaySession
            // drops a message whose session does not carry this relay session yet. Answering first lost
            // that login under load: the session then sat login-less until the Steam grace force-reconnected
            // it (the registry self-test steam-reopen-without-login caught it on a CI runner and on polaris
            // pinned to one core, "no relay message 20").
            // Hooked before anything can attach it, so the first Link assignment already reaches gateway-rs.
            created.RelaySession = session;
            created.RoutingChanged = changed => gateway.PushRouting(changed);
            var channel = new ExternalClientChannel(gateway._relay!, created.ClientId, session, service == "Steam" ? "steam" : "dt", remote);
            created.Client = channel;
            Answer(new { decision = "accept" });
            return channel;
        }
        public bool Hold(string reason)
        {
            Answer(new { decision = "hold", reason });
            return true;
        }
        private void Answer(object body)
        {
            if (Interlocked.Exchange(ref _answered, 1) == 0)
                gateway._relay!.Answer(message, RelayIpcType.JoinAnswer, body);
        }
    }

    private void StartExternalRelay()
    {
        string path = _options.RelaySocket ?? throw new InvalidOperationException("--relay external needs a relay socket path");
        _relay = new RelayIpcServer(path);
        _relay.PeerReady += OnRelayReady;
        _relay.PeerLost += OnRelayLost;
        _relay.Message += OnRelayMessage;
        _relay.Start();
        if (SteamEnabled)
            _steamPublishTimer = new Timer(_ => { PushSteamPublish(force: false); PollSteamServerId(); }, null, 1000, 1000);
        Console.WriteLine($"Relay: external (gateway-rs) on {path}; waiting for it to connect");
    }

    /// <summary>A (re)connected gateway-rs knows nothing yet: configure it and restate every link it should hold.</summary>
    private void OnRelayReady()
    {
        RelayIpcServer relay = _relay!;
        // A new gateway-rs process logs on to Steam again, possibly as another GameServer.
        Volatile.Write(ref _steamServerId, 0UL);
        Interlocked.Exchange(ref _nextSteamIdentityPoll, 0);
        relay.Send(RelayIpcType.Config, 0, new
        {
            direct = new { enabled = _options.DirectFrontend, bind = _options.Listen.Address.ToString(), port = _options.Listen.Port, dualStack = true },
            steam = new { enabled = _options.SteamListen != null, bind = _options.SteamListen?.Address.ToString(), port = _options.SteamListen?.Port ?? 0,
                version = AdmissionBook.BuildNumber(DirectTransportConstants.VanillaVersion) },
            backlog = new { clientBytes = 4 * 1024 * 1024, clientAgeMs = 10000, serverBytes = 1024 * 1024 },
            credit = new { joinWindow = 4 * 1024 * 1024, withholdAbove = 4096, releaseBelow = 3072 },
            timeouts = new { clientDisconnectMs = 120000, nodeDisconnectMs = 30000 },
            joinHoldSeconds = JoinHold.TotalSeconds,
            joinToken = _options.JoinToken ?? string.Empty,
            acceptAll = _options.DirectFrontend,
            nodeLinkChannels = NodeLinkChannels,
        });
        PushPaused(force: true);
        PushSteamPublish(force: true);
        foreach (NodeLink link in _nodes.Values)
            relay.Send(RelayIpcType.EnsureLink, 0, new { endpoint = link.Endpoint.ToString() });
    }

    /// <summary>gateway-rs is gone and its sessions with it: every client of this gateway disconnected.</summary>
    private void OnRelayLost(string reason)
    {
        foreach (NodeLink link in _nodes.Values)
            if (link.ExternalConnected)
            {
                link.ExternalConnected = false;
                link.Connected.Reset();
            }
        foreach (ClientSession session in _clients.Values)
        {
            (session.Client as ExternalClientChannel)?.MarkDisconnected();
            Console.WriteLine($"Client {session.ClientId} disconnected: relay lost ({reason})");
            RemoveSession(session);
        }
    }

    private void OnRelayMessage(RelayIpcMessage message)
    {
        switch (message.Type)
        {
            case RelayIpcType.JoinRequest:
                // Admission reads the registry and may take its lock; never on the IPC receive thread.
                ThreadPool.QueueUserWorkItem(_ => { lock (_relayAdmissionLock) OnRelayJoin(message); });
                return;
            case RelayIpcType.ClientMessage:
                OnRelayClientMessage(message.Json);
                return;
            case RelayIpcType.ClientEvent:
                OnRelayClientEvent(message.Json);
                return;
            case RelayIpcType.LinkState:
                OnRelayLinkState(message.Json);
                return;
            case RelayIpcType.NodeControl:
                OnRelayNodeControl(message.Json);
                return;
            case RelayIpcType.NodeGlobal:
            case RelayIpcType.NodePlugin:
                OnRelayTunnel(message);
                return;
            case RelayIpcType.SteamEvent:
                // Verdicts run admission, which reads the registry: never on the IPC receive thread.
                ThreadPool.QueueUserWorkItem(_ => { lock (_relayAdmissionLock) OnRelaySteamEvent(message.Json); });
                return;
        }
    }

    private void OnRelayJoin(RelayIpcMessage message)
    {
        try
        {
            JsonElement join = message.Json;
            ulong clientId = join.GetProperty("clientId").GetUInt64();
            ulong session = join.GetProperty("session").GetUInt64();
            string service = join.GetProperty("service").GetString() ?? "DirectTransport";
            string remote = join.TryGetProperty("remote", out JsonElement r) ? r.GetString() ?? "" : "";
            string? name = join.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var request = new ExternalJoinRequest(this, message, session, service, remote);
            if (service == "Steam")
            {
                OnRelaySteamJoin(request, clientId);
                return;
            }
            if (service != "DirectTransport")
            {
                Console.Error.WriteLine($"Rejected {service} client {clientId}: gateway-rs terminates only Direct Transport and Steam");
                request.Reject();
                return;
            }
            OnConnectionRequest(request, null, (clientId, name));
            if (_clients.TryGetValue(clientId, out ClientSession? created) && created.RelaySession == session)
                PushRouting(created);
        }
        catch (Exception error) { Console.Error.WriteLine("Relay join failed: " + error); }
    }

    /// <summary>
    /// <see cref="OpenSteamSession"/> for a Steam P2P session gateway-rs terminated: a session
    /// request, or the first packet of a rejoin inside the frontend's grace. The session waits in
    /// TicketPending with no node attached until CLIENT_CONNECTED's ticket validates.
    /// </summary>
    private void OnRelaySteamJoin(ExternalJoinRequest request, ulong userId)
    {
        ClientSession? session = null;
        try
        {
            if (!SteamEnabled || JoiningClosed)
            {
                Console.Error.WriteLine($"Refused Steam session with {userId}: cluster admission is closed");
                request.Reject();
                return;
            }
            if (_clients.ContainsKey(userId))
            {
                Console.Error.WriteLine($"Refused Steam session with {userId}: a session is already open");
                request.Reject();
                return;
            }
            if (JoinHeldForHomeRecovery(userId))
            {
                // T-0298, the T-0197 correction on the Steam path: a refusal here is a failed login. After every
                // restart all character homes are offline, so every player's FIRST join asked for its home's load
                // and was refused; the second attempt got in (Viktor, r33 21:11:12 refused, home online 21:11:18,
                // 21:11:32 in). gateway-rs holds a Steam join on "hold" and re-asks every second until
                // Config.joinHoldSeconds, so each re-ask comes back here until the home is claimed. T-0365: it
                // accepts the P2P session at the first hold and keeps what the client sends (its login), which
                // arrives here as the ClientMessage right after the accept; left unaccepted for a 41 s hold the
                // login was lost and the accepted session was ended as one without a login.
                HoldJoin(request, userId, null, "character home partition is recovering", null);
                return;
            }
            ClientRoute? route = null;
            string? deferred = null;
            try { route = _registry?.ResolveClientRoute(userId); }
            // T-0237: "come back shortly" is resolved again at admission (AdmitValidatedClient).
            catch (RegistryBusyException busy) { deferred = busy.Message; Console.Error.WriteLine($"Route lookup for {userId} deferred to admission: {busy.Message}"); }
            catch (Exception e) { Console.Error.WriteLine($"Route lookup for {userId} failed: {e.Message}"); }
            IPEndPoint backend = route == null ? UnroutedBackend()
                : EndpointParser.Parse(route.Endpoint, DirectTransportConstants.DefaultPort);
            session = new ClientSession(userId, backend, route) { AdmissionState = "TicketPending", RouteDeferred = deferred };
            if (!_clients.TryAdd(userId, session))
            {
                request.Reject();
                return;
            }
            session.Client = request.Accept(session);
            Console.WriteLine($"Accepted client {userId} from steam:{userId} (relay)");
            if (route != null)
                Console.WriteLine($"Resolved client {userId} partition {route.Partition} to {route.Node} epoch {route.Epoch} at {backend}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Steam join failed: " + e);
            if (session?.Client != null)
                session.Client.Disconnect();
            else
                request.Reject();
            // #1221: also a session that threw between TryAdd and the accept, or single-flight refuses the identity for good.
            if (session != null)
                RemoveSession(session);
        }
    }

    /// <summary>SteamEvent: the frontend's TicketValidated and GroupStatus callbacks, from gateway-rs.</summary>
    private void OnRelaySteamEvent(JsonElement body)
    {
        try
        {
            ulong userId = body.GetProperty("clientId").GetUInt64();
            switch (body.GetProperty("kind").GetString())
            {
                case "ticket":
                    var response = (Steamworks.EAuthSessionResponse)body.GetProperty("response").GetInt32();
                    ulong owner = body.TryGetProperty("owner", out JsonElement o) ? o.GetUInt64() : 0;
                    uint[]? missingDlcs = body.TryGetProperty("missingDlcs", out JsonElement m)
                        ? m.EnumerateArray().Select(app => app.GetUInt32()).ToArray() : null;
                    OnSteamTicket(userId, SteamFrontend.Verdict(response), owner, missingDlcs);
                    return;
                case "ticketRejected":
                    // SteamFrontend.DrainOutgoing: a BeginAuthSession failure is TicketInvalid.
                    OnSteamTicket(userId, JoinResult.TicketInvalid, 0);
                    return;
                case "group":
                    OnSteamGroupStatus(userId, body.GetProperty("member").GetBoolean());
                    return;
            }
        }
        catch (Exception error) { Console.Error.WriteLine("Relay Steam event failed: " + error); }
    }

    private void SteamBeginAuth(ulong userId, byte[] ticket)
    {
        if (_relay == null) { _steam!.BeginAuth(userId, ticket); return; }
        try { _relay.Send(RelayIpcType.SteamAuth, 0, new { op = "begin", clientId = userId, ticket = Convert.ToBase64String(ticket) }); }
        catch (IOException) { }
    }

    private void SteamEndAuth(ulong userId)
    {
        if (_relay == null) { _steam?.EndAuth(userId); return; }
        if (!SteamEnabled) return;
        try { _relay.Send(RelayIpcType.SteamAuth, 0, new { op = "end", clientId = userId }); }
        catch (IOException) { }
    }

    private void SteamRequestGroupStatus(ulong userId, ulong groupId)
    {
        if (_relay == null) { _steam!.RequestGroupStatus(userId, groupId); return; }
        try { _relay.Send(RelayIpcType.SteamAuth, 0, new { op = "group", clientId = userId, groupId }); }
        catch (IOException) { }
    }

    /// <summary>
    /// SE-1076: a node's MyCachedServerItem.GetServerData(), published as the "sc" rules. Vanilla keeps it under
    /// 1018 bytes by dropping mods and block limits, so anything far larger is not that blob.
    /// </summary>
    private void ReportServerData(string node, string base64)
    {
        byte[] data;
        try { data = Convert.FromBase64String(base64); }
        catch (FormatException) { Console.Error.WriteLine($"Server data from {node} is not base64; ignored"); return; }
        if (data.Length == 0 || data.Length > 16 * 1024)
        {
            Console.Error.WriteLine($"Server data from {node} is {data.Length} bytes; ignored");
            return;
        }
        if (!_admission.SetServerData(data))
            return;
        Console.WriteLine($"Server data from {node}: {data.Length} bytes, published as the server details' sc rules");
        SteamPublishDirty();
    }

    private void SteamPublishDirty()
    {
        if (_relay == null) _steam?.PublishDirty();
        else _steamPublishDirty = true;
    }

    /// <summary>
    /// The browser row for gateway-rs (SteamFrontend.Publish's inputs): every 5 s, sooner when an
    /// admission changed the player list. gateway-rs re-pushes it to Steam every 5 s by itself.
    /// </summary>
    private void PushSteamPublish(bool force)
    {
        RelayIpcServer? relay = _relay;
        if (relay == null || !SteamEnabled || !relay.Connected) return;
        long now = Environment.TickCount64;
        if (!force && !_steamPublishDirty && now < Interlocked.Read(ref _nextSteamPublish)) return;
        _steamPublishDirty = false;
        Interlocked.Exchange(ref _nextSteamPublish, now + 5000);
        try
        {
            relay.Send(RelayIpcType.SteamPublish, 0, new
            {
                serverName = _admission.Settings.ServerName,
                mapName = _admission.Settings.WorldName,
                maxPlayers = _admission.Settings.MemberLimit,
                passwordProtected = _admission.Settings.PasswordProtected,
                tags = _admission.GameTags(DirectTransportConstants.VanillaVersion, _options.DirectFrontend),
                modCount = _admission.Settings.ModCount, modsTotalSize = _admission.ModsTotalSize,
                rules = _admission.ServerDataRules().Select(rule => new { key = rule.Key, value = rule.Value }).ToArray(),
                players = ClusterPlayers().Select(player => new { userId = player.UserId, name = player.Name }).ToArray(),
            });
        }
        catch (IOException) { }
    }

    /// <summary>gateway-rs's Status for /transport/status: null with the in-process relay, an
    /// error string when gateway-rs did not answer.</summary>
    private (JsonElement? Status, string? Error) ExternalRelayStatus()
    {
        RelayIpcServer? relay = _relay;
        if (relay == null) return (null, null);
        try
        {
            JsonElement status = relay.RequestAsync(RelayIpcType.Status, new { }, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult().Json;
            NoteSteamServerId(status);
            return (status, null);
        }
        catch (Exception error) { return (null, error.Message); }
    }

    /// <summary>
    /// T-0244: gateway-rs's Steam GameServer SteamID, 0 until it has logged on. A vanilla client
    /// knows its server by that id (MyMultiplayerClient: <c>ServerId = server.SteamID</c>), never by
    /// <see cref="DirectTransportConstants.ServerId"/>, and acts on a Disconnected control message
    /// only when its Client field is that id.
    /// </summary>
    private ulong _steamServerId;
    private long _nextSteamIdentityPoll;
    private int _steamIdentityPolling;

    /// <summary>Asks gateway-rs's Status for the GameServer id until it has one (every 5 s, one in flight).</summary>
    private void PollSteamServerId()
    {
        RelayIpcServer? relay = _relay;
        if (relay == null || !SteamEnabled || !relay.Connected || Volatile.Read(ref _steamServerId) != 0) return;
        long now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _nextSteamIdentityPoll) || Interlocked.Exchange(ref _steamIdentityPolling, 1) != 0) return;
        Interlocked.Exchange(ref _nextSteamIdentityPoll, now + 5000);
        _ = Task.Run(async () =>
        {
            try { NoteSteamServerId((await relay.RequestAsync(RelayIpcType.Status, new { purpose = "steam-identity" }, TimeSpan.FromSeconds(2))).Json); }
            catch (Exception) { }
            finally { Interlocked.Exchange(ref _steamIdentityPolling, 0); }
        });
    }

    private void NoteSteamServerId(JsonElement status)
    {
        if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("steam", out JsonElement steam) && steam.ValueKind == JsonValueKind.Object
            && steam.TryGetProperty("serverId", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetUInt64(out ulong serverId) && serverId != 0)
            Volatile.Write(ref _steamServerId, serverId);
    }

    /// <summary>The server id a forced reconnect names: the Steam GameServer to a Steam client of
    /// gateway-rs once known, the Direct Transport server otherwise.</summary>
    private ulong ReconnectServerId(ClientChannel peer)
    {
        ulong steam = Volatile.Read(ref _steamServerId);
        return peer is ExternalClientChannel { Frontend: "steam" } && steam != 0 ? steam : DirectTransportConstants.ServerId;
    }

    // ---------------------------------------------------------------- T-0244: reopened Steam sessions

    /// <summary>
    /// How long a gateway-rs Steam session may go without presenting CLIENT_CONNECTED. A vanilla join
    /// sends it as its first packet (it is what opens the P2P session), so a session without one
    /// after this long is a client still playing a session the Gateway already ended: its traffic
    /// reopened the P2P session and can never be admitted (no ticket), and until T-0244 it was held
    /// until the 10 s backlog age force-reconnected it, forever. Shorter than that age and than the
    /// vanilla reconnector's 10 s countdown, so a stray session is gone before a real rejoin arrives.
    /// </summary>
    internal static TimeSpan SteamLoginGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long, from the first forced reconnect since its last admission, a client is given to come
    /// back by itself before it is booted to the main menu. A vanilla client acts on the reconnect
    /// (host left) within a frame and its reconnector rejoins with a login, so traffic without one
    /// this long after is a client that ignored it: 15 s is three grace periods, two refresh retries.
    /// </summary>
    internal static TimeSpan SteamRefreshBound = TimeSpan.FromSeconds(15);

    /// <summary>Forced session ends of one Steam client since its last admission.</summary>
    private sealed class ReconnectStreak(DateTimeOffset since)
    {
        public readonly DateTimeOffset Since = since;
        public DateTimeOffset Last = since;
        /// <summary>T-0365: where <see cref="SteamRefreshBound"/> counts from: the first session ended
        /// without a login since the client last presented one (null until the next such end).</summary>
        public DateTimeOffset? RefreshFrom = since;
        public int Forced;
        public int Boots;
    }

    private readonly ConcurrentDictionary<ulong, ReconnectStreak> _steamReconnectStreaks = new();
    private long _steamBootsToMenu;

    private static bool IsExternalSteam(ClientSession session) => session.Client is ExternalClientChannel { Frontend: "steam" };

    private static bool SteamSessionWithoutLogin(ClientSession session) =>
        IsExternalSteam(session) && !session.LoginPresented && session.Credential == null && session.DisconnectReason == null
        && DateTimeOffset.UtcNow - session.ConnectedAt >= SteamLoginGrace;

    /// <summary>Counts a forced end of a Steam session; the log suffix for it (empty for other clients).</summary>
    private string NoteSteamForcedEnd(ClientSession session, bool boot = false)
    {
        if (!IsExternalSteam(session)) return string.Empty;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ReconnectStreak streak = _steamReconnectStreaks.GetOrAdd(session.ClientId, _ => new ReconnectStreak(now));
        int forced;
        lock (streak)
        {
            forced = ++streak.Forced;
            if (boot) streak.Boots++;
            streak.Last = now;
        }
        return $" ({forced} forced without admission in {(now - streak.Since).TotalSeconds:0}s)";
    }

    /// <summary>
    /// Viktor 2026-09-25: "Should try to refresh the client. If the client does not reconnect in a
    /// short time, then better to boot it to the main menu." Inside <see cref="SteamRefreshBound"/>
    /// the session is force-reconnected again: the host-left control message (now naming the right
    /// server) makes a vanilla client unload and run its own reconnector, which rejoins with a fresh
    /// login. Past it the client gets a JOIN_RESULT refusal, which a vanilla client treats as fatal
    /// even in game (MyMultiplayerClient.OnUserJoined: Dispose, UnloadAndExitToMenu, error box) and
    /// does not retry. Either way the session ends here, and a later one with a login joins normally.
    /// </summary>
    private void EndSteamSessionWithoutLogin(ClientSession session)
    {
        ReconnectStreak streak = _steamReconnectStreaks.GetOrAdd(session.ClientId, _ => new ReconnectStreak(session.ConnectedAt));
        DateTimeOffset from;
        lock (streak) from = streak.RefreshFrom ??= session.ConnectedAt;
        if (DateTimeOffset.UtcNow - from < SteamRefreshBound)
        {
            ForceReconnect(session, "steam-session-without-login");
            return;
        }
        TimeSpan waited = DateTimeOffset.UtcNow - streak.Since;
        Interlocked.Increment(ref _steamBootsToMenu);
        Console.WriteLine($"Booting client {session.ClientId} to the main menu: still no login {waited.TotalSeconds:0}s"
            + $" after its session was ended{NoteSteamForcedEnd(session, boot: true)}");
        RejectClient(session, JoinResult.UserNotConnected);
    }

    /// <summary>
    /// T-0365: a session that presents its login is the client coming back as asked, so the refresh
    /// clock starts over: if that session is lost before admission, the next one without a login is
    /// refreshed again instead of booted for the age of a streak the client already answered. The
    /// streak itself (its counts, the "admitted again" line) still ends only with an admission.
    /// </summary>
    private void NoteSteamLoginPresented(ClientSession session)
    {
        if (session.LoginPresented) return;
        session.LoginPresented = true;
        if (_steamReconnectStreaks.TryGetValue(session.ClientId, out ReconnectStreak? streak))
            lock (streak) streak.RefreshFrom = null;
    }

    /// <summary>An admission ends the streak; says how long the client took to come back.</summary>
    private void EndSteamReconnectStreak(ulong clientId)
    {
        if (_steamReconnectStreaks.TryRemove(clientId, out ReconnectStreak? streak))
            Console.WriteLine($"Client {clientId} is admitted again {(DateTimeOffset.UtcNow - streak.Since).TotalSeconds:0}s"
                + $" after {streak.Forced} forced reconnect(s) without admission, {streak.Boots} boot(s) to the main menu");
    }

    /// <summary>A client that never came back is forgotten ten minutes after its last forced end.</summary>
    private void PruneSteamReconnectStreaks()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        foreach ((ulong clientId, ReconnectStreak streak) in _steamReconnectStreaks)
            if (streak.Last < cutoff && !_clients.ContainsKey(clientId))
                _steamReconnectStreaks.TryRemove(clientId, out _);
    }

    /// <summary>/transport/status: Steam clients whose sessions were ended and who are not back in.</summary>
    private object[] SteamReconnectStreaks() => _steamReconnectStreaks.Select(entry => (object)new
    {
        client = entry.Key, since = entry.Value.Since, last = entry.Value.Last,
        forcedWithoutAdmission = entry.Value.Forced, bootsToMenu = entry.Value.Boots,
    }).ToArray();

    /// <summary>The Steam part of gateway-rs's Status, for /transport/status.</summary>
    private object? ExternalSteamStatus((JsonElement? Status, string? Error) relay)
    {
        if (_relay == null || !SteamEnabled) return null;
        if (relay.Error != null) return new { error = relay.Error };
        return relay.Status is { } status && status.TryGetProperty("steam", out JsonElement steam) ? steam : null;
    }

    /// <summary>When each relay session's node last sent it anything, from gateway-rs's nodeIdleMs:
    /// with the external relay the packets never pass through C#, so ClientSession.LastNodeActivity
    /// would stay at the connect time (bin/airtight-liveness reads it). Keyed by (client, session).</summary>
    private static Dictionary<(ulong, ulong), DateTimeOffset> ExternalNodeActivity(JsonElement? status)
    {
        var activity = new Dictionary<(ulong, ulong), DateTimeOffset>();
        if (status is not { } json || !json.TryGetProperty("sessions", out JsonElement sessions) || sessions.ValueKind != JsonValueKind.Array)
            return activity;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (JsonElement session in sessions.EnumerateArray())
        {
            if (session.TryGetProperty("clientId", out JsonElement client) && session.TryGetProperty("session", out JsonElement id)
                && session.TryGetProperty("nodeIdleMs", out JsonElement idle))
                activity[(client.GetUInt64(), id.GetUInt64())] = now - TimeSpan.FromMilliseconds(idle.GetInt64());
        }
        return activity;
    }

    /// <summary>The client-to-node backlog gateway-rs holds per client (packets, bytes). With the
    /// external relay the packets never pass through this process, so the session's own
    /// ClientBacklog stays empty; empty with the in-process relay.</summary>
    private static Dictionary<ulong, (long Count, long Bytes)> ExternalClientBacklog(JsonElement? status)
    {
        var backlog = new Dictionary<ulong, (long, long)>();
        if (status is not { } json || !json.TryGetProperty("sessions", out JsonElement sessions) || sessions.ValueKind != JsonValueKind.Array)
            return backlog;
        foreach (JsonElement session in sessions.EnumerateArray())
        {
            if (session.TryGetProperty("clientId", out JsonElement client) && session.TryGetProperty("clientBacklog", out JsonElement count)
                && session.TryGetProperty("clientBacklogBytes", out JsonElement bytes))
                backlog[client.GetUInt64()] = (count.GetInt64(), bytes.GetInt64());
        }
        return backlog;
    }

    private bool TryRelaySession(JsonElement body, out ClientSession session)
    {
        session = null!;
        ulong clientId = body.GetProperty("clientId").GetUInt64();
        ulong relaySession = body.GetProperty("session").GetUInt64();
        if (!_clients.TryGetValue(clientId, out ClientSession? found) || found.RelaySession != relaySession) return false;
        session = found;
        return true;
    }

    private void OnRelayClientMessage(JsonElement body)
    {
        if (!TryRelaySession(body, out ClientSession session)) return;
        byte messageId = body.GetProperty("messageId").GetByte();
        byte[] packet = Convert.FromBase64String(body.GetProperty("packet").GetString() ?? "");
        byte channel = body.TryGetProperty("channel", out JsonElement c) ? c.GetByte() : (byte)2;
        session.LastClientActivity = DateTimeOffset.UtcNow;
        if (messageId == VanillaJoinCodec.ClientConnectedMessageId) NoteSteamLoginPresented(session);
        // gateway-rs relays (or backlogs) the packet itself; C# only acts on it, in arrival order.
        _relayClientMessages.Post(() =>
        {
            try { OnClientMessage(session, new GatewayPacket(session.ClientId, channel, packet, DeliveryMethod.ReliableOrdered), messageId); }
            catch (Exception error) { Console.Error.WriteLine($"Client {session.ClientId} message {messageId} failed: {error.Message}"); }
        });
    }

    private void OnRelayClientEvent(JsonElement body)
    {
        if (!TryRelaySession(body, out ClientSession session)) return;
        string kind = body.GetProperty("event").GetString() ?? "";
        string detail = body.TryGetProperty("detail", out JsonElement d) ? d.ToString() : "";
        switch (kind)
        {
            case "disconnected":
                (session.Client as ExternalClientChannel)?.MarkDisconnected();
                Console.WriteLine($"Client {session.ClientId} disconnected: {detail}");
                RemoveSession(session);
                return;
            case "backlogOverflow":
                ForceReconnect(session, $"{detail}-backlog-overflow (relay)");
                return;
            case "backlogExpired":
                // As in session maintenance: a join still waiting for its ticket verdict is exempt
                // from the age bound (gateway-rs re-reports while the condition stands).
                // T-0357: nor does the age end a session something is deliberately holding (the relay pause,
                // a backend switch, an attach still being retried); a gateway-rs that knows exempts those
                // itself, an older one keeps reporting.
                if (session.Credential != null && !ClientBacklogHeld(session)) ForceReconnect(session, "client-backlog-expired");
                return;
            case "silent":
                ForceReconnect(session, "client-silent");
                return;
            case "ackBacklog":
                ForceReconnect(session, "client-ack-backlog");
                return;
            case "targetReady":
                lock (session.RelayLock)
                {
                    if (session.ValidationLink == null || session.ValidationLink.Endpoint.ToString() != detail.Trim('"')) return;
                    session.TargetServerPacketSeen = true;
                    CompleteTargetReadiness(session);
                }
                return;
            case "duplicateServerData":
                session.DuplicateServerData++;
                Interlocked.Increment(ref _duplicateServerData);
                return;
        }
    }

    private void OnRelayLinkState(JsonElement body)
    {
        IPEndPoint endpoint = EndpointParser.Parse(body.GetProperty("endpoint").GetString()!, DirectTransportConstants.DefaultPort);
        bool connected = body.GetProperty("connected").GetBoolean();
        string reason = body.TryGetProperty("reason", out JsonElement r) ? r.GetString() ?? "" : "";
        NodeLink link = GetNodeLink(endpoint);
        if (connected)
        {
            lock (link.Sync)
            {
                if (link.ExternalConnected) return;
                link.ExternalConnected = true;
                link.ConnectedAt = link.LastActivity = DateTimeOffset.UtcNow;
                link.DisconnectedAt = null;
                link.LastDisconnectReason = null;
                link.Connected.Set();
                Console.WriteLine($"Connected node link {link.Endpoint} (relay)");
            }
            ThreadPool.QueueUserWorkItem(_ => OnLinkConnected(link));
        }
        else
        {
            lock (link.Sync)
            {
                if (!link.ExternalConnected) return;
                link.ExternalConnected = false;
                link.Connected.Reset();
            }
            ThreadPool.QueueUserWorkItem(_ => OnLinkDisconnected(link, reason));
        }
    }

    /// <summary>
    /// T-0244: the registry declared a node Dead (its lease expired, or its simulation stalled). Its clients
    /// are force-reconnected now rather than when gateway-rs's node link times out: on 2026-09-27 02:55:48
    /// a kill -9 of node-5 was Dead at +16 s but 'Node link ... disconnected: Timeout' came at +29 s, and
    /// a Steam player sat on a frozen world for those 13 s. The link state itself is left to gateway-rs,
    /// whose disconnect still arrives and finds these sessions already ended (ForceReconnect is once per
    /// session). External relay only: the in-process relay is not changed (Viktor 2026-09-24).
    /// </summary>
    internal void OnRegistryNodeDead(string? node, string endpoint)
    {
        if (_relay == null) return;
        IPEndPoint parsed;
        try { parsed = EndpointParser.Parse(endpoint, DirectTransportConstants.DefaultPort); }
        catch (Exception e) { Console.Error.WriteLine($"Dead node {node} has an unreadable endpoint '{endpoint}': {e.Message}"); return; }
        if (!_nodes.TryGetValue(parsed, out NodeLink? link)) return;
        int moved = 0;
        foreach (ClientSession attached in link.Clients.Values)
            if (ReferenceEquals(attached.Link, link) && !attached.SwitchingBackend && attached.DisconnectReason == null)
            {
                ForceReconnect(attached, "node-dead");
                moved++;
            }
        if (moved > 0)
            Console.WriteLine($"Node {node} at {link.Endpoint} is Dead in the registry: {moved} client(s) force-reconnected "
                + "without waiting for the link timeout");
    }

    private void OnRelayNodeControl(JsonElement body)
    {
        if (!TryRelaySession(body, out ClientSession target)) return;
        IPEndPoint endpoint = EndpointParser.Parse(body.GetProperty("endpoint").GetString()!, DirectTransportConstants.DefaultPort);
        NodeLinkMessage kind = body.GetProperty("kind").GetString() switch
        {
            "attachAck" => NodeLinkMessage.AttachAck,
            "detachAck" => NodeLinkMessage.DetachAck,
            "detach" => NodeLinkMessage.Detach,
            string other => throw new InvalidDataException("unknown node control " + other),
            null => throw new InvalidDataException("node control without a kind"),
        };
        if (!_nodes.TryGetValue(endpoint, out NodeLink? link)) return;
        link.LastActivity = DateTimeOffset.UtcNow;
        lock (target.RelayLock)
        {
            if (!ReferenceEquals(target.Link, link)) return;
            OnNodeControl(target, link, kind);
        }
    }

    private void OnRelayTunnel(RelayIpcMessage message)
    {
        ReadOnlySpan<byte> body = message.Body.Span;
        // [u8 delivery][u16 endpointLen][endpoint][payload]; the delivery is informational this way.
        int length = BinaryPrimitives.ReadUInt16LittleEndian(body[1..]);
        string endpointText = Encoding.UTF8.GetString(body.Slice(3, length));
        byte[] payload = body[(3 + length)..].ToArray();
        IPEndPoint endpoint = EndpointParser.Parse(endpointText, DirectTransportConstants.DefaultPort);
        if (!_nodes.TryGetValue(endpoint, out NodeLink? link)) return;
        link.LastActivity = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _nodePackets);
        Interlocked.Add(ref _nodeBytes, payload.Length);
        _relayNodeFrames.Post(() =>
        {
            try
            {
                if (message.Type == RelayIpcType.NodePlugin) RoutePluginMessage(link, payload);
                else RouteGlobalMessage(link, payload);
            }
            catch (Exception error) { Console.Error.WriteLine($"Relay tunnel frame from {endpointText} failed: {error.Message}"); }
        });
    }

    /// <summary>
    /// Mirrors a session's routing fields into gateway-rs (RelayIpc SessionUpdate). Every field is
    /// sent every time: the update is idempotent, so ordering only has to be that of the IPC socket.
    /// </summary>
    private void PushRouting(ClientSession session, bool flushClient = false, bool flushServer = false, bool discardServer = false)
    {
        RelayIpcServer? relay = _relay;
        if (relay == null || session.RelaySession == 0) return;
        lock (session.RoutingPushLock) PushRoutingLocked(relay, session, flushClient, flushServer, discardServer);
    }

    private void PushRoutingLocked(RelayIpcServer relay, ClientSession session, bool flushClient, bool flushServer, bool discardServer)
    {
        bool routable;
        try { routable = AttachedBindingRoutable(session); }
        catch (Exception) { routable = false; }
        NodeLink? link = session.Link;
        try
        {
            relay.Send(RelayIpcType.SessionUpdate, 0, new
            {
                clientId = session.ClientId,
                session = session.RelaySession,
                link = link?.Endpoint.ToString(),
                attached = link != null && ReferenceEquals(session.AttachedLink, link),
                routable,
                switching = session.SwitchingBackend,
                attachPending = Volatile.Read(ref session.AttachRetryStarted) != 0,
                bufferServer = session.BufferServerPackets,
                suppressJoin = session.SuppressJoinMessages,
                validationLink = session.ValidationLink?.Endpoint.ToString(),
                flushClient,
                flushServer,
                discardServer,
            });
        }
        catch (IOException) { }
    }

    /// <summary>1 Hz from the lease sweep: routability depends on the World Authority generation and
    /// the relay pause, which change outside any session setter.</summary>
    private void RefreshExternalRouting()
    {
        if (_relay == null) return;
        PushPaused(force: false);
        foreach (ClientSession session in _clients.Values)
            if (session.RelaySession != 0) PushRouting(session);
    }

    private void PushPaused(bool force)
    {
        RelayIpcServer? relay = _relay;
        if (relay == null) return;
        bool paused = RelayPaused;
        lock (_relayStateLock)
        {
            if (!force && paused == _lastPushedPaused) return;
            _lastPushedPaused = paused;
        }
        try { relay.Send(RelayIpcType.Paused, 0, new { paused }); }
        catch (IOException) { }
    }

    /// <summary>A control frame toward a node through gateway-rs (the external half of SendNodeLink).</summary>
    private static void SendExternalNodeLink(NodeLink link, NodeLinkPacket packet)
    {
        RelayIpcServer? relay = link.Relay;
        if (relay == null) return;
        try
        {
            switch (packet.Kind)
            {
                case NodeLinkMessage.Attach:
                    relay.Send(RelayIpcType.NodeFrame, 0, new { endpoint = link.Endpoint.ToString(), kind = "attach", clientId = packet.ClientId,
                        body = Convert.ToBase64String(packet.Payload) });
                    return;
                case NodeLinkMessage.Detach:
                    relay.Send(RelayIpcType.NodeFrame, 0, new { endpoint = link.Endpoint.ToString(), kind = "detach", clientId = packet.ClientId });
                    return;
                case NodeLinkMessage.Credit:
                    relay.Send(RelayIpcType.NodeFrame, 0, new { endpoint = link.Endpoint.ToString(), kind = "credit", clientId = packet.ClientId,
                        credit = packet.Credit });
                    return;
                case NodeLinkMessage.Global:
                case NodeLinkMessage.Plugin:
                    byte[] endpoint = Encoding.UTF8.GetBytes(link.Endpoint.ToString());
                    // [u8 delivery][u16 endpointLen][endpoint][payload]: Global Commands and Responses
                    // are ReliableUnordered, other Global frames ReliableOrdered (GlobalDelivery).
                    // The prefix is gathered in front of the payload by SendBinary: no per-node copy of
                    // a multi-MB global (T-0229, 2026-09-24: ~10 LOH copies per message spiked the
                    // Gateway to 2.9 GB at a sweep step's logouts).
                    byte[] prefix = new byte[3 + endpoint.Length];
                    prefix[0] = (byte)packet.Delivery;
                    BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), checked((ushort)endpoint.Length));
                    endpoint.CopyTo(prefix, 3);
                    relay.SendBinary(packet.Kind == NodeLinkMessage.Plugin ? RelayIpcType.NodePlugin : RelayIpcType.NodeGlobal, 0, prefix, packet.Payload);
                    return;
                default:
                    // Client packets never flow through C# with an external relay: backlogs flush and
                    // CLIENT_CONNECTED replays are SessionUpdate / ReplayConnect primitives.
                    Console.Error.WriteLine($"WARNING: relay: node-link {packet.Kind} frame for client {packet.ClientId} has no external path; dropped");
                    return;
            }
        }
        catch (IOException) { }
    }
}

/// <summary>A single thread that runs posted actions one at a time, in the order they were posted.</summary>
internal sealed class OrderedWorker
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
    private readonly string _name;
    private long _highWater = 10_000;

    public OrderedWorker(string name)
    {
        _name = name;
        new Thread(Run) { IsBackground = true, Name = name }.Start();
    }

    public int Pending => _queue.Count;

    public void Post(Action action)
    {
        _queue.Add(action);
        int depth = _queue.Count;
        if (depth >= _highWater)
        {
            // A backlog means the consumer is slower than the relay: say so once per doubling.
            _highWater *= 2;
            Console.Error.WriteLine($"WARNING: {_name}: {depth} actions queued");
        }
    }

    private void Run()
    {
        foreach (Action action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception error) { Console.Error.WriteLine($"{_name}: {error.GetType().Name}: {error.Message}"); }
        }
    }
}
