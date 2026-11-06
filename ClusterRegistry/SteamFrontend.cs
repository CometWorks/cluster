using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LiteNetLib;
using Steamworks;

namespace ClusterRegistry;

/// <summary>
/// Steam termination for the cluster (Gateway.md §4.2): the Gateway owns the single Steam
/// GameServer identity in VAC-secure mode, answers master-server queries on the game port,
/// terminates GameServer P2P on the vanilla channels {0, 2}, and validates auth tickets before
/// any node sees the client. Mirrors MySteamGameServer/MySteamPeer2Peer without linking any SE
/// assembly - Steamworks.NET is Valve's binding, not a game assembly.
///
/// Every Steamworks call is made from the single pump thread; the relay path only enqueues.
/// </summary>
internal sealed class SteamFrontend : IDisposable
{
    private const uint SpaceEngineersAppId = 244850;

    /// <summary>
    /// SE-1158: the game's DLC app ids (Content/Data/Game/DLCs.sbc, MyDLCs.DLCs), which a vanilla DS asks
    /// UserHasLicenseForApp about for every client. The nodes can't ask: Direct Transport answers yes to all. A DLC
    /// released after this list counts as owned by everyone until it is added here.
    /// </summary>
    internal static readonly uint[] DlcAppIds =
    [
        573160, 999999990, 999999991, 1049790, 1084680, 1135960, 1167910, 1241550, 1307680, 1374610, 1475830, 1676100,
        1783760, 1958640, 2504720, 2569770, 2914120, 3066290, 3601770, 3858380, 4116960, 4502100, 4835530,
    ];

    /// <summary>How long a Steam inventory blob may stay pending before the check gives up.</summary>
    private const long SkinCheckTimeoutMilliseconds = 5000;
    private const int MaximumPacket = 1024 * 1024 + 1024;

    private readonly IPEndPoint _endpoint;
    private readonly string _version;
    private readonly AdmissionBook _admission;
    private readonly bool _acceptAllTag;
    private readonly ConcurrentQueue<Outgoing> _outgoing = new();
    private readonly ConcurrentQueue<SkinCheck> _skinChecks = new();
    /// <summary>Inventory blobs Steam is still deserializing; pump thread only.</summary>
    private readonly List<SkinCheck> _pendingSkinChecks = [];
    /// <summary>asset_modifier_id of each Steam item definition that is an armor or block skin, "" for the rest; pump thread only.</summary>
    private readonly Dictionary<int, string> _skinOfItem = [];
    /// <summary>Steam sent the item definitions; until then a blob's items can't be named.</summary>
    private bool _definitionsLoaded;
    private readonly ConcurrentDictionary<ulong, SteamSession> _sessions = new();
    private readonly List<IDisposable> _callbacks = [];
    private Socket? _query;
    private Thread? _pump;
    private Thread? _queryThread;
    private volatile bool _running;
    private volatile bool _publishDirty = true;
    /// <summary>Players already reported as unpublishable, so the log says it once each.</summary>
    private readonly HashSet<ulong> _unnamedReported = [];
    private long _sentPackets, _receivedPackets, _queries, _authRequests, _authRejections;
    /// <summary>The Workshop size query of the world's mods (#1529): started once logged on, null when done or not needed.</summary>
    private CallResult<SteamUGCQueryCompleted_t>? _modSizes;
    private bool _modSizesAsked;

    /// <summary>A client opened a P2P session; return false to ignore it (admission closed).</summary>
    public Func<ulong, bool> SessionRequested = _ => false;
    public Action<ulong, GatewayPacket> Received = (_, _) => { };
    public Action<ulong, string> SessionFailed = (_, _) => { };
    /// <summary>The Gateway closed the session; Steam P2P has no disconnect event of its own.</summary>
    public Action<ulong> SessionClosed = _ => { };
    /// <summary>
    /// Ticket verdict, also fired for mid-session revocations (§2.6). The last argument is the DLC app ids of
    /// <see cref="DlcAppIds"/> the player has no license for (SE-1158), null unless the verdict is OK.
    /// </summary>
    public Action<ulong, JoinResult, ulong, uint[]?> TicketValidated = (_, _, _, _) => { };
    public Action<ulong, bool> GroupStatus = (_, _) => { };
    /// <summary>Cluster-wide player list for the browser row: id and display name.</summary>
    /// <summary>
    /// The cluster's live players. <c>Steam</c> is false for a client admitted without a Steam
    /// auth session (Direct Transport): BUpdateUserData cannot represent one, so it is counted
    /// rather than named. See <see cref="Publish"/>.
    /// </summary>
    public Func<IReadOnlyList<(ulong UserId, string Name, bool Steam)>> Players = () => [];

    public ulong ServerId { get; private set; }
    public bool LoggedOn { get; private set; }
    public bool Secure { get; private set; }
    public IPAddress PublicAddress { get; private set; } = IPAddress.Any;

    public SteamFrontend(IPEndPoint endpoint, string version, AdmissionBook admission, bool acceptAllTag)
    {
        _endpoint = endpoint;
        _version = version;
        _admission = admission;
        _acceptAllTag = acceptAllTag;
    }

    public void Start()
    {
        InstallSteamClient();
        // .NET keeps its own environment copy on Unix, so the native SDK never sees a managed
        // SetEnvironmentVariable - the app id has to go through setenv or Init fails.
        SetNativeEnvironment("SteamAppId", SpaceEngineersAppId.ToString());
        SetNativeEnvironment("SteamGameId", SpaceEngineersAppId.ToString());
        uint address = _endpoint.Address.Equals(IPAddress.Any) ? 0
            : BitConverter.ToUInt32(_endpoint.Address.GetAddressBytes().Reverse().ToArray());
        if (!GameServer.Init(address, (ushort)_endpoint.Port, ushort.MaxValue,
            EServerMode.eServerModeAuthenticationAndSecure, AdmissionBook.BuildNumber(_version)))
            throw new InvalidOperationException($"Steam GameServer.Init failed on {_endpoint}");

        _callbacks.Add(Callback<ValidateAuthTicketResponse_t>.CreateGameServer(OnValidateAuthTicket));
        _callbacks.Add(Callback<P2PSessionRequest_t>.CreateGameServer(OnSessionRequest));
        _callbacks.Add(Callback<P2PSessionConnectFail_t>.CreateGameServer(OnSessionConnectFail));
        _callbacks.Add(Callback<GSClientGroupStatus_t>.CreateGameServer(OnGroupStatus));
        _callbacks.Add(Callback<SteamServersConnected_t>.CreateGameServer(_ =>
        {
            LoggedOn = true;
            Console.WriteLine($"Steam GameServer connected as {SteamGameServer.GetSteamID()}");
            // As MySteamInventoryServer: the item definitions name the skin each inventory item is (SE-1158).
            // Asked before the logon, Steam sent them only with the first inventory blob, too late for it.
            SteamGameServerInventory.LoadItemDefinitions();
        }));
        _callbacks.Add(Callback<SteamServersDisconnected_t>.CreateGameServer(result =>
        {
            LoggedOn = false;
            Console.Error.WriteLine("Steam GameServer disconnected: " + result.m_eResult);
        }));
        _callbacks.Add(Callback<SteamServerConnectFailure_t>.CreateGameServer(result =>
            Console.Error.WriteLine($"Steam GameServer logon failed: {result.m_eResult} retrying={result.m_bStillRetrying}")));
        _callbacks.Add(Callback<GSPolicyResponse_t>.CreateGameServer(result => Secure = result.m_bSecure != 0));
        _callbacks.Add(Callback<SteamInventoryDefinitionUpdate_t>.CreateGameServer(_ => LoadSkinDefinitions()));

        SteamGameServer.SetProduct("Space Engineers");
        SteamGameServer.SetGameDescription("Space Engineers");
        SteamGameServer.SetModDir("Space Engineers");
        SteamGameServer.SetDedicatedServer(true);
        SteamGameServer.SetServerName(_admission.Settings.ServerName);
        SteamGameServer.SetMapName(_admission.Settings.WorldName);
        SteamGameServer.SetMaxPlayerCount(_admission.Settings.MemberLimit);
        SteamGameServer.SetBotPlayerCount(0); // replaced every Publish by the unnamed-player count
        SteamGameServer.SetPasswordProtected(_admission.Settings.PasswordProtected);
        SteamGameServer.SetGameTags(_admission.GameTags(_version, _acceptAllTag));
        SteamGameServer.SetGameData(AdmissionBook.BuildNumber(_version));
        SteamGameServer.SetKeyValue("CONSOLE_COMPATIBLE", "0");
        SteamGameServer.LogOnAnonymous();
        SteamGameServer.SetAdvertiseServerActive(true);

        _query = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _query.Bind(_endpoint);
        _running = true;
        _pump = new Thread(PumpLoop) { Name = "Gateway-Steam-Pump", IsBackground = true };
        _pump.Start();
        _queryThread = new Thread(QueryLoop) { Name = "Gateway-Steam-Query", IsBackground = true };
        _queryThread.Start();

        for (int attempt = 0; attempt < 300 && ServerId == 0; attempt++)
            Thread.Sleep(100);
        Console.WriteLine($"Steam frontend listening on {_endpoint} as {ServerId}, public address {PublicAddress},"
            + $" secure {Secure}, tags '{_admission.GameTags(_version, _acceptAllTag)}'");
        if (ServerId == 0)
            throw new InvalidOperationException("Steam GameServer did not obtain an identity");
    }

    public void Dispose()
    {
        _running = false;
        try { _query?.Close(); } catch { }
        try { _pump?.Join(2000); } catch { }
        try { _queryThread?.Join(2000); } catch { }
        foreach (IDisposable callback in _callbacks) callback.Dispose();
        if (ServerId != 0)
        {
            try { SteamGameServer.SetAdvertiseServerActive(false); } catch { }
            try { SteamGameServer.LogOff(); } catch { }
        }
        try { GameServer.Shutdown(); } catch { }
    }

    public ClientChannel Channel(ulong userId) => new SteamClientChannel(this, userId);

    public void Send(ulong userId, GatewayPacket packet)
    {
        if (!_sessions.ContainsKey(userId))
            return;
        _outgoing.Enqueue(new Outgoing(userId, checked((byte)packet.Channel), packet.Payload, packet.Delivery));
    }

    public bool Active(ulong userId) => _sessions.TryGetValue(userId, out SteamSession? session)
        && session.DetachedAt == 0;

    public int QueuedPackets(ulong userId) =>
        _sessions.TryGetValue(userId, out SteamSession? session) ? session.QueuedPackets : 0;

    public void BeginAuth(ulong userId, byte[] token)
    {
        Interlocked.Increment(ref _authRequests);
        _outgoing.Enqueue(new Outgoing(userId, 0, token, DeliveryMethod.ReliableOrdered, OutgoingKind.BeginAuth));
    }

    public void EndAuth(ulong userId) =>
        _outgoing.Enqueue(new Outgoing(userId, 0, [], DeliveryMethod.ReliableOrdered, OutgoingKind.EndAuth));

    public void RequestGroupStatus(ulong userId, ulong groupId) =>
        _outgoing.Enqueue(new Outgoing(userId, 0, BitConverter.GetBytes(groupId), DeliveryMethod.ReliableOrdered,
            OutgoingKind.GroupStatus));

    /// <summary>
    /// Ends the auth session and detaches the client, but leaves the Steam P2P session itself open
    /// for a grace period. A player who comes straight back sends the first packet of the new join
    /// into that session and no new session request fires, so closing it here loses that packet and
    /// the rejoin dies of a Steam timeout (Findings/2026-07-25-p3-steam-termination.md). The pump
    /// closes it for real once the grace expires with nobody back.
    /// </summary>
    public void Close(ulong userId)
    {
        if (_sessions.TryGetValue(userId, out SteamSession? session))
            session.DetachedAt = Environment.TickCount64;
        _outgoing.Enqueue(new Outgoing(userId, 0, [], DeliveryMethod.ReliableOrdered, OutgoingKind.EndAuth));
        // Ending the auth session takes the player off Steam's active list, so the browser row is
        // stale from this moment: republish rather than wait for the 5 s tick or the 30 s hard close.
        _publishDirty = true;
        SessionClosed(userId);
    }

    public void PublishDirty() => _publishDirty = true;

    public object Status() => new
    {
        endpoint = _endpoint.ToString(), serverId = ServerId, loggedOn = LoggedOn, secure = Secure,
        publicAddress = PublicAddress.ToString(), sessions = _sessions.Count,
        sentPackets = Interlocked.Read(ref _sentPackets), receivedPackets = Interlocked.Read(ref _receivedPackets),
        queries = Interlocked.Read(ref _queries), authRequests = Interlocked.Read(ref _authRequests),
        authRejections = Interlocked.Read(ref _authRejections),
        serverName = _admission.Settings.ServerName, worldName = _admission.Settings.WorldName,
        maxPlayers = _admission.Settings.MemberLimit, tags = _admission.GameTags(_version, _acceptAllTag),
    };

    private void PumpLoop()
    {
        byte[] buffer = new byte[MaximumPacket];
        long nextPublish = 0;
        var published = new Dictionary<ulong, string>();
        while (_running)
        {
            try
            {
                GameServer.RunCallbacks();
                if (ServerId == 0 && SteamGameServer.BLoggedOn())
                {
                    ServerId = (ulong)SteamGameServer.GetSteamID();
                    PublicAddress = SteamGameServer.GetPublicIP().ToIPAddress();
                }
                if (!_modSizesAsked && LoggedOn)
                    QueryModSizes();
                DrainOutgoing();
                PumpSkinChecks();
                foreach (byte channel in VanillaChannels)
                    while (SteamGameServerNetworking.IsP2PPacketAvailable(out uint size, channel) && size <= MaximumPacket)
                    {
                        if (!SteamGameServerNetworking.ReadP2PPacket(buffer, (uint)buffer.Length, out uint read,
                            out CSteamID remote, channel))
                            break;
                        Interlocked.Increment(ref _receivedPackets);
                        byte[] payload = buffer[..(int)read];
                        // Traffic means the peer is there: adopt the session whether it was
                        // detached by a forced disconnect or never announced by a request.
                        _sessions.GetOrAdd((ulong)remote, id => new SteamSession(id)).DetachedAt = 0;
                        Received((ulong)remote, new GatewayPacket((ulong)remote, channel, payload,
                            DeliveryMethod.ReliableOrdered));
                    }
                foreach (SteamSession session in _sessions.Values)
                    session.QueuedPackets = SteamGameServerNetworking.GetP2PSessionState((CSteamID)session.UserId,
                        out P2PSessionState_t state) ? state.m_nPacketsQueuedForSend : 0;
                ReapDetachedSessions();
                long now = Environment.TickCount64;
                if (_publishDirty || now >= nextPublish)
                {
                    _publishDirty = false;
                    nextPublish = now + 5000;
                    Publish(published);
                }
            }
            catch (Exception e) { Console.Error.WriteLine("Steam pump failed: " + e); }
            Thread.Sleep(1);
        }
    }

    private static readonly byte[] VanillaChannels = [0, 2];

    /// <summary>How long a detached Steam session stays open for the player to come straight back.</summary>
    private const long RejoinGraceMilliseconds = 30_000;

    private void ReapDetachedSessions()
    {
        long now = Environment.TickCount64;
        foreach (SteamSession session in _sessions.Values)
        {
            long detachedAt = session.DetachedAt;
            if (detachedAt == 0 || now - detachedAt < RejoinGraceMilliseconds)
                continue;
            HardClose(session.UserId, "the rejoin grace expired");
        }
    }

    /// <summary>Ends the session at Steam too; the browser player row goes with it.</summary>
    private void HardClose(ulong userId, string reason)
    {
        SteamGameServer.EndAuthSession((CSteamID)userId);
        SteamGameServerNetworking.CloseP2PSessionWithUser((CSteamID)userId);
        if (_sessions.TryRemove(userId, out _))
        {
            _publishDirty = true;
            Console.WriteLine($"Closed the Steam session with {userId}: {reason}");
        }
    }

    private void DrainOutgoing()
    {
        while (_outgoing.TryDequeue(out Outgoing message))
        {
            var user = (CSteamID)message.UserId;
            switch (message.Kind)
            {
                case OutgoingKind.Packet:
                    EP2PSend send = message.Delivery == DeliveryMethod.Unreliable
                        ? EP2PSend.k_EP2PSendUnreliable : EP2PSend.k_EP2PSendReliable;
                    if (SteamGameServerNetworking.SendP2PPacket(user, message.Payload, (uint)message.Payload.Length,
                        send, message.Channel))
                        Interlocked.Increment(ref _sentPackets);
                    break;
                case OutgoingKind.BeginAuth:
                    EBeginAuthSessionResult result = SteamGameServer.BeginAuthSession(message.Payload,
                        message.Payload.Length, user);
                    if (result != EBeginAuthSessionResult.k_EBeginAuthSessionResultOK)
                    {
                        Interlocked.Increment(ref _authRejections);
                        Console.Error.WriteLine($"Steam BeginAuthSession for {message.UserId} failed: {result}");
                        TicketValidated(message.UserId, JoinResult.TicketInvalid, 0, null);
                    }
                    break;
                case OutgoingKind.EndAuth:
                    SteamGameServer.EndAuthSession(user);
                    break;
                case OutgoingKind.GroupStatus:
                    SteamGameServer.RequestUserGroupStatus(user, (CSteamID)BitConverter.ToUInt64(message.Payload));
                    break;
            }
        }
    }

    /// <summary>
    /// Browser-visible dynamic state (§4.2): the cluster's real player list and count, which only
    /// the Gateway knows - the per-node member lists are meaningless for publishing.
    ///
    /// Every player is re-pushed on every pass, exactly as vanilla does
    /// (MyDedicatedServerBase.UpdateSteamServerData). The previous version pushed a (user, name)
    /// pair once and cached it, which loses the player for good: Close() ends the auth session -
    /// Steam drops him from the active-player list - and a rejoin inside the 30 s grace produces
    /// no new session request, so the cached entry suppressed the re-push and the browser row
    /// decayed to zero over a few handovers.
    ///
    /// A client admitted through Direct Transport has no Steam auth session at all, so
    /// BUpdateUserData structurally cannot represent it ("wasn't for an active player") and its
    /// return value used to be discarded. Those players are counted into the bot slot instead,
    /// which is the only field in the browser row that does not require a Steam identity. Without
    /// this a cluster whose players are all Direct Transport - which every regression client is -
    /// advertises 0 players while a dozen are logged in (Viktor, 2026-08-14).
    /// </summary>
    private void Publish(Dictionary<ulong, string> published)
    {
        IReadOnlyList<(ulong UserId, string Name, bool Steam)> players = Players();
        int unnamed = 0;
        foreach ((ulong userId, string name, bool steam) in players)
        {
            // Try to NAME every player, Steam-authenticated or not: the browser's player column
            // reads the named count (A2S m_nPlayers) and ignores the bot slot entirely, so a
            // client that cannot be named does not show up at all. Direct Transport ids have no
            // auth session behind them and BUpdateUserData is documented to refuse those, but the
            // refusal is cheap and being wrong about it costs the whole count.
            if (SteamGameServer.BUpdateUserData((CSteamID)userId, name, 0))
            {
                published[userId] = name;
                continue;
            }
            unnamed++;
            if (published.Remove(userId) || !_unnamedReported.Contains(userId))
            {
                _unnamedReported.Add(userId);
                Console.WriteLine($"Steam would not publish player {userId} ({name}, "
                    + (steam ? "Steam" : "Direct Transport") + "); counting it in the bot slot");
            }
        }
        _unnamedReported.RemoveWhere(id => players.All(player => player.UserId != id));
        foreach (ulong gone in published.Keys.Where(id => players.All(player => player.UserId != id)).ToArray())
            published.Remove(gone);
        SteamGameServer.SetBotPlayerCount(unnamed);
        SteamGameServer.SetMapName(_admission.Settings.WorldName);
        SteamGameServer.SetMaxPlayerCount(_admission.Settings.MemberLimit);
        SteamGameServer.SetGameTags(_admission.GameTags(_version, _acceptAllTag));
        // MyDedicatedServerBase.ModCount / ModsTotalSize also set these A2S rules
        SteamGameServer.SetKeyValue("mods", _admission.Settings.ModCount.ToString());
        SteamGameServer.SetKeyValue("modsTotalSize", _admission.ModsTotalSize.ToString());
        // MyCachedServerItem.SendSettingsToSteam: the server details' settings and mod list (SE-1076)
        foreach ((string key, string value) in _admission.ServerDataRules())
            SteamGameServer.SetKeyValue(key, value);
    }

    /// <summary>
    /// ModsTotalSize the way MyDedicatedServerBase.Mods sums it: each world mod's Workshop file size, asked of
    /// Steam with the GameServer's own UGC interface (a vanilla DS reads the same details when it downloads the
    /// mods). One query per 50 ids (kNumUGCResultsPerPage); an item Steam does not return counts 0. Only when
    /// the deployment gave ids and no total.
    /// </summary>
    private void QueryModSizes()
    {
        _modSizesAsked = true;
        ulong[] ids = _admission.Settings.ModIds ?? [];
        if (ids.Length == 0 || _admission.Settings.ModsTotalSize > 0)
            return;
        ulong total = 0;
        int answered = 0, offset = 0;
        void Next()
        {
            PublishedFileId_t[] page = [.. ids.Skip(offset).Take(50).Select(id => new PublishedFileId_t(id))];
            UGCQueryHandle_t query = SteamGameServerUGC.CreateQueryUGCDetailsRequest(page, (uint)page.Length);
            _modSizes = CallResult<SteamUGCQueryCompleted_t>.Create((result, failed) =>
            {
                if (!failed && result.m_eResult == EResult.k_EResultOK)
                {
                    for (uint i = 0; i < result.m_unNumResultsReturned; i++)
                        if (SteamGameServerUGC.GetQueryUGCResult(result.m_handle, i, out SteamUGCDetails_t details)
                            && details.m_eResult == EResult.k_EResultOK)
                        {
                            total += (ulong)Math.Max(0, details.m_nFileSize);
                            answered++;
                        }
                }
                else
                    Console.Error.WriteLine($"Steam mod size query failed: {(failed ? "IO failure" : result.m_eResult)}");
                SteamGameServerUGC.ReleaseQueryUGCRequest(result.m_handle);
                offset += 50;
                if (offset < ids.Length)
                {
                    Next();
                    return;
                }
                _modSizes = null;
                _admission.ModsTotalSize = total;
                _publishDirty = true;
                Console.WriteLine($"Steam mods: {ids.Length} world mods, {answered} with Workshop details, modsTotalSize {total}");
            });
            _modSizes.Set(SteamGameServerUGC.SendQueryUGCRequest(query));
        }
        Next();
    }

    private void QueryLoop()
    {
        // MySteamGameServer.SteamServerEntryPoint: master-server queries arrive as 0xFFFFFFFF-prefixed
        // datagrams on the game port and are answered by the SDK. No game traffic flows here.
        byte[] buffer = new byte[1500];
        Socket socket = _query!;
        while (_running)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int size;
            try { size = socket.ReceiveFrom(buffer, ref remote); }
            catch (Exception) { return; }
            if (size < 4 || BitConverter.ToUInt32(buffer, 0) != uint.MaxValue)
                continue;
            var from = (IPEndPoint)remote;
            Interlocked.Increment(ref _queries);
            // Same split as vanilla: the query socket thread drives HandleIncomingPacket while the
            // pump thread drives RunCallbacks.
            try
            {
                SteamGameServer.HandleIncomingPacket(buffer, size, HostOrder(from.Address), (ushort)from.Port);
                while ((size = SteamGameServer.GetNextOutgoingPacket(buffer, buffer.Length, out uint address,
                    out ushort port)) > 0)
                    socket.SendTo(buffer, size, SocketFlags.None,
                        new IPEndPoint(new IPAddress(BitConverter.GetBytes(
                            (uint)IPAddress.HostToNetworkOrder((int)address))), port));
            }
            catch (Exception e) { Console.Error.WriteLine("Steam query failed: " + e); }
        }
    }

    private static uint HostOrder(IPAddress address)
    {
        byte[] bytes = address.MapToIPv4().GetAddressBytes();
        return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }

    private void OnSessionRequest(P2PSessionRequest_t request)
    {
        ulong userId = (ulong)request.m_steamIDRemote;
        if (!SessionRequested(userId))
        {
            SteamGameServerNetworking.CloseP2PSessionWithUser(request.m_steamIDRemote);
            return;
        }
        _sessions[userId] = new SteamSession(userId);
        SteamGameServerNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
        _publishDirty = true;
        Console.WriteLine($"Accepted Steam P2P session with {userId}");
    }

    private void OnSessionConnectFail(P2PSessionConnectFail_t failure)
    {
        ulong userId = (ulong)failure.m_steamIDRemote;
        string reason = ((EP2PSessionError)failure.m_eP2PSessionError).ToString();
        HardClose(userId, "Steam reported " + reason);
        SessionFailed(userId, reason);
    }

    /// <summary>Security.md §9 item 1: the JoinResult vanilla shows for each Steam verdict.</summary>
    internal static JoinResult Verdict(EAuthSessionResponse response) => response switch
    {
        EAuthSessionResponse.k_EAuthSessionResponseOK => JoinResult.OK,
        EAuthSessionResponse.k_EAuthSessionResponseUserNotConnectedToSteam => JoinResult.UserNotConnected,
        EAuthSessionResponse.k_EAuthSessionResponseNoLicenseOrExpired => JoinResult.NoLicenseOrExpired,
        EAuthSessionResponse.k_EAuthSessionResponseVACBanned => JoinResult.VACBanned,
        EAuthSessionResponse.k_EAuthSessionResponseLoggedInElseWhere => JoinResult.LoggedInElseWhere,
        EAuthSessionResponse.k_EAuthSessionResponseVACCheckTimedOut => JoinResult.VACCheckTimedOut,
        EAuthSessionResponse.k_EAuthSessionResponseAuthTicketCanceled => JoinResult.TicketCanceled,
        EAuthSessionResponse.k_EAuthSessionResponseAuthTicketInvalidAlreadyUsed => JoinResult.TicketAlreadyUsed,
        EAuthSessionResponse.k_EAuthSessionResponseAuthTicketInvalid => JoinResult.TicketInvalid,
        EAuthSessionResponse.k_EAuthSessionResponsePublisherIssuedBan => JoinResult.BannedByAdmins,
        _ => JoinResult.TicketInvalid,
    };

    private void OnValidateAuthTicket(ValidateAuthTicketResponse_t response)
    {
        JoinResult result = Verdict(response.m_eAuthSessionResponse);
        if (result != JoinResult.OK)
            Interlocked.Increment(ref _authRejections);
        uint[]? missingDlcs = result == JoinResult.OK ? MissingDlcs(response.m_SteamID) : null;
        Console.WriteLine($"Steam ticket verdict for {(ulong)response.m_SteamID}: {result}"
            + $" owner {(ulong)response.m_OwnerSteamID}"
            + (missingDlcs == null ? "" : $" DLCs {DlcAppIds.Length - missingDlcs.Length}/{DlcAppIds.Length}"));
        TicketValidated((ulong)response.m_SteamID, result, (ulong)response.m_OwnerSteamID, missingDlcs);
    }

    /// <summary>SE-1158: what MySessionComponentDLC.UpdateClientDLC asks a vanilla DS's game server, for the nodes.</summary>
    private static uint[] MissingDlcs(CSteamID user) => DlcAppIds
        .Where(app => SteamGameServer.UserHasLicenseForApp(user, (AppId_t)app)
            != EUserHasLicenseForAppResult.k_EUserHasLicenseResultHasLicense)
        .ToArray();

    /// <summary>
    /// SE-1158: the armor and block skins in a client's Steam inventory blob, the one the client sends a vanilla DS
    /// in MySessionComponentGameInventory.RequestUpdateClientGameInventory, which the DS checks with its game server
    /// (MySteamInventoryServer.CheckItemData). Null when Steam refuses the blob or it belongs to someone else.
    /// </summary>
    public Task<string[]?> CheckSkins(ulong userId, byte[] data)
    {
        var check = new SkinCheck(userId, data);
        _skinChecks.Enqueue(check);
        return check.Result.Task;
    }

    private void PumpSkinChecks()
    {
        while (_skinChecks.TryDequeue(out SkinCheck? check))
        {
            if (!SteamGameServerInventory.DeserializeResult(out check.Handle, check.Data, (uint)check.Data.Length))
            {
                check.Result.TrySetResult(null);
                continue;
            }
            check.Deadline = Environment.TickCount64 + SkinCheckTimeoutMilliseconds;
            _pendingSkinChecks.Add(check);
        }
        _pendingSkinChecks.RemoveAll(check =>
        {
            EResult status = SteamGameServerInventory.GetResultStatus(check.Handle);
            if ((status == EResult.k_EResultPending || status == EResult.k_EResultOK && !_definitionsLoaded)
                && Environment.TickCount64 < check.Deadline)
                return false;
            check.Result.TrySetResult(status == EResult.k_EResultOK ? SkinsOf(check) : null);
            SteamGameServerInventory.DestroyResult(check.Handle);
            return true;
        });
    }

    private string[]? SkinsOf(SkinCheck check)
    {
        // Vanilla takes any valid blob; a blob of someone else's inventory would lend that player's skins.
        if (!SteamGameServerInventory.CheckResultSteamID(check.Handle, (CSteamID)check.UserId))
            return null;
        uint count = 0;
        if (!SteamGameServerInventory.GetResultItems(check.Handle, null, ref count))
            return null;
        var items = new SteamItemDetails_t[count];
        if (count > 0 && !SteamGameServerInventory.GetResultItems(check.Handle, items, ref count))
            return null;
        return items.Select(item => SkinOf(item.m_iDefinition)).Where(skin => skin.Length > 0).Distinct().ToArray();
    }

    /// <summary>Steam sent the item definitions (MySteamInventoryServer.OnSteamInventoryDefinitionUpdate): learn the skins now.</summary>
    private void LoadSkinDefinitions()
    {
        uint count = 0;
        if (!SteamGameServerInventory.GetItemDefinitionIDs(null, ref count))
            return;
        var definitions = new SteamItemDef_t[count];
        if (!SteamGameServerInventory.GetItemDefinitionIDs(definitions, ref count))
            return;
        int skins = definitions.Count(definition => SkinOf(definition).Length > 0);
        _definitionsLoaded = true;
        Console.WriteLine($"Steam inventory definitions: {definitions.Length} items, {skins} armor and block skins");
    }

    /// <summary>MySessionComponentGameInventory keeps only the Armor and Block slot items, by asset_modifier_id.</summary>
    private string SkinOf(SteamItemDef_t definition)
    {
        if (_skinOfItem.TryGetValue(definition.m_SteamItemDef, out string? skin))
            return skin;
        string slot = DefinitionProperty(definition, "item_slot");
        if (slot.Length == 0)
            return ""; // definitions not loaded yet: ask again next time
        skin = slot is "Armor" or "Block" ? DefinitionProperty(definition, "asset_modifier_id") : "";
        _skinOfItem[definition.m_SteamItemDef] = skin;
        return skin;
    }

    private static string DefinitionProperty(SteamItemDef_t definition, string name)
    {
        uint size = 2048;
        return SteamGameServerInventory.GetItemDefinitionProperty(definition, name, out string value, ref size) ? value : "";
    }

    private void OnGroupStatus(GSClientGroupStatus_t status) =>
        GroupStatus((ulong)status.m_SteamIDUser, status.m_bMember || status.m_bOfficer);

    /// <summary>The Steam loader looks for steamclient.so under ~/.steam/sdk64 when no Steam is installed.</summary>
    private static void InstallSteamClient()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "steamclient.so");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!File.Exists(source) || string.IsNullOrWhiteSpace(home))
            return;
        string directory = Path.Combine(home, ".steam", "sdk64");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "steamclient.so");
        // Stage-and-rename, never copy over the target: any process running a Steam frontend
        // or game server has this file mmap'd, and an in-place overwrite truncates the inode
        // under that mapping - a native SIGBUS (bus error, exit 135) in the mapped process.
        // The rename leaves the old inode whole for existing mappings.
        string staging = target + "." + Environment.ProcessId + ".tmp";
        try
        {
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
            {
                File.Copy(source, staging, overwrite: true);
                File.Move(staging, target, overwrite: true);
            }
        }
        catch (IOException) { /* ponytail: a concurrent instance already staged it; good enough */ }
        finally
        {
            try { File.Delete(staging); } catch (IOException) { }
        }
    }

    private static void SetNativeEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (setenv(name, value, 1) != 0)
            throw new InvalidOperationException($"Could not export {name} to the native Steam SDK");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    private enum OutgoingKind { Packet, BeginAuth, EndAuth, GroupStatus }

    private readonly record struct Outgoing(ulong UserId, byte Channel, byte[] Payload, DeliveryMethod Delivery,
        OutgoingKind Kind = OutgoingKind.Packet);

    private sealed class SkinCheck(ulong userId, byte[] data)
    {
        public readonly ulong UserId = userId;
        public readonly byte[] Data = data;
        public readonly TaskCompletionSource<string[]?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SteamInventoryResult_t Handle;
        public long Deadline;
    }

    private sealed class SteamSession(ulong userId)
    {
        public readonly ulong UserId = userId;
        /// <summary>Tick count when the client was detached, 0 while it is attached.</summary>
        public long DetachedAt;
        public volatile int QueuedPackets;
    }

    private sealed class SteamClientChannel(SteamFrontend frontend, ulong userId) : ClientChannel
    {
        public override string Frontend => "steam";
        public override string Remote => "steam:" + userId;
        public override bool Connected => frontend.Active(userId);
        public override int ReliableBacklogPackets => frontend.QueuedPackets(userId);
        public override void Send(GatewayPacket packet) => frontend.Send(userId, packet);
        public override void Disconnect() => frontend.Close(userId);
    }
}
