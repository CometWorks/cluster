using System.Diagnostics;
using System.Net;
using LiteNetLib;

namespace ClusterRegistry;

/// <summary>
/// #1221 (#1191 octahedron run, cluster v0.1.20): after node fencings, 72 gateway sessions sat in
/// WaitingForClient for hours with no client connection behind them, and single-flight refused every rejoin
/// of those identities until the gateway restarted. Each check here fails on v0.1.20.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// A session whose client connection is gone and for which no disconnect event comes - gateway-rs no
    /// longer has the relay session, so its "disconnected" never arrives - must not lock the identity out:
    /// a second force-reconnect (an admin kick) removes it at once, and maintenance removes it after
    /// <see cref="DirectGateway.DeadClientGrace"/> when nothing else happens.
    /// </summary>
    private static void TestDeadClientSession()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("dead-client-session: " + what);
        }
        string directory = Directory.CreateTempSubdirectory("dead-client-selftest-").FullName;
        string path = Path.Combine(directory, "gateway-relay.sock");
        Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", "true");
        TimeSpan grace = DirectGateway.DeadClientGrace;
        // SE-1450: these joins never send CLIENT_READY; the "silent" reports below must end them at once.
        TimeSpan loading = DirectGateway.LoadingSilenceLimit;
        DirectGateway.LoadingSilenceLimit = TimeSpan.Zero;
        var options = new GatewayOptions(
            new(IPAddress.Loopback, FreeUdpPort()),
            new(IPAddress.Loopback, FreeUdpPort()),
            null,
            false,
            null,
            null,
            null,
            null,
            TimeSpan.FromSeconds(10),
            null,
            null,
            [],
            DirectFrontend: true,
            ExternalRelay: true,
            RelaySocket: path
        );
        using var gateway = new DirectGateway(options);
        var gatewayThread = new Thread(() =>
        {
            try
            {
                gateway.Run();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("dead-client-session gateway failed: " + e);
            }
        })
        {
            IsBackground = true,
            Name = "Gateway-Dead-Client-Self-Test",
        };
        gatewayThread.Start();
        try
        {
            using var relay = new ScriptedRelay(path);
            relay.Read(RelayIpcType.Config);
            uint correlation = 0x1221_0000;
            string Join(ulong clientId, ulong session)
            {
                uint asked = ++correlation;
                relay.Send(
                    RelayIpcType.JoinRequest,
                    asked,
                    new
                    {
                        clientId,
                        session,
                        service = "DirectTransport",
                        name = "p" + clientId,
                        remote = "127.0.0.1:1",
                    }
                );
                return relay
                    .Read(RelayIpcType.JoinAnswer, m => m.Correlation == asked)
                    .Json.GetProperty("decision")
                    .GetString()!;
            }
            // gateway-rs reports the client silent; the force-reconnect asks it to disconnect a session it then
            // turns out not to have, so no "disconnected" event follows.
            void LoseConnection(ulong clientId, ulong session)
            {
                relay.Send(
                    RelayIpcType.ClientEvent,
                    0,
                    new
                    {
                        clientId,
                        session,
                        @event = "silent",
                    }
                );
                relay.Read(
                    RelayIpcType.Disconnect,
                    m =>
                        m.Json.GetProperty("clientId").GetUInt64() == clientId
                        && m.Json.GetProperty("session").GetUInt64() == session
                );
            }

            // A later force-reconnect of a session whose connection is gone removes it now (v0.1.20 returned early).
            DirectGateway.DeadClientGrace = TimeSpan.FromMinutes(10);
            const ulong kicked = 76561199500000138;
            Check(Join(kicked, 1) == "accept", "the first join is accepted");
            LoseConnection(kicked, 1);
            Check(
                Join(kicked, 2) == "reject",
                "single-flight still refuses while the session may yet end by itself"
            );
            relay.Send(
                RelayIpcType.ClientEvent,
                0,
                new
                {
                    clientId = kicked,
                    session = 1UL,
                    @event = "silent",
                }
            );
            string afterKick = "";
            for (int i = 0; i < 20 && afterKick != "accept"; i++, Thread.Sleep(50))
                afterKick = Join(kicked, 3 + (ulong)i);
            Check(
                afterKick == "accept",
                "a second force-reconnect of the dead session lets the identity join again"
            );

            // With nothing else happening, maintenance removes it after the grace (v0.1.20 kept it until restart).
            DirectGateway.DeadClientGrace = TimeSpan.FromSeconds(2);
            const ulong stranded = 76561199500000042;
            Check(Join(stranded, 101) == "accept", "the first join is accepted");
            LoseConnection(stranded, 101);
            var waited = Stopwatch.StartNew();
            string rejoin = "";
            for (
                ulong session = 102;
                waited.Elapsed < TimeSpan.FromSeconds(10) && rejoin != "accept";
                session++, Thread.Sleep(250)
            )
                rejoin = Join(stranded, session);
            Check(
                rejoin == "accept",
                "maintenance removes a session whose connection is gone, and the identity joins again"
            );
            Check(
                waited.Elapsed >= TimeSpan.FromSeconds(1.5),
                $"the session outlived its connection for the grace (rejoined after {waited.Elapsed.TotalSeconds:F1}s)"
            );
            Console.WriteLine(
                $"dead-client-session: kicked dead session removed at once; stranded one removed, rejoined after {waited.Elapsed.TotalSeconds:F1}s (grace 2s)"
            );
        }
        finally
        {
            DirectGateway.DeadClientGrace = grace;
            DirectGateway.LoadingSilenceLimit = loading;
            gateway.Stop();
            Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", null);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// LiteNetLib 1.3.1 hands an existing peer back from ConnectionRequest.Accept() when one already sits at
    /// the request's address (OnConnectionSolved), in any state and with no Connect event. Clients that retry
    /// from one port while the server refuses some requests and holds others make that happen within
    /// milliseconds on loopback. The in-process relay's accept must bind a session only to a fresh, connected
    /// peer: on v0.1.20 it bound sessions to Disconnected peers (never ended) and re-tagged peers other
    /// sessions already held. The check also asserts that the stale case was provoked, so a LiteNetLib that
    /// no longer does this shows up here instead of passing silently.
    /// </summary>
    private static void TestJoinAcceptStalePeer()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("join-accept-stale-peer: " + what);
        }
        var serverEvents = new EventBasedNetListener();
        var server = new NetManager(serverEvents)
        {
            DisconnectTimeout = 30000,
            ReconnectDelay = 500,
            MaxConnectAttempts = 20,
        };
        Check(server.Start(IPAddress.Loopback, IPAddress.IPv6None, 0), "the server binds");
        var held = new List<(LiteNetLibJoinRequest Request, long At)>();
        int requests = 0,
            bound = 0,
            refusedStale = 0,
            wrong = 0;
        serverEvents.ConnectionRequestEvent += request =>
        {
            // A third are refused at once (single-flight, superseded holds), the rest held briefly.
            if (++requests % 3 == 0)
                request.Reject();
            else
                held.Add((new LiteNetLibJoinRequest(request), Environment.TickCount64));
        };
        var clients = new List<NetManager>();
        for (int i = 0; i < 8; i++)
        {
            var events = new EventBasedNetListener();
            var client = new NetManager(events)
            {
                DisconnectTimeout = 30000,
                ReconnectDelay = 1,
                MaxConnectAttempts = 1_000_000,
                UpdateTime = 1,
            };
            client.Start();
            // Every attempt ends and the next leaves from the same socket, as the Direct Transport client does.
            events.PeerDisconnectedEvent += (_, _) =>
                client.Connect("127.0.0.1", server.LocalPort, "k");
            events.PeerConnectedEvent += peer => peer.Disconnect();
            client.Connect("127.0.0.1", server.LocalPort, "k");
            clients.Add(client);
        }
        var random = new Random(1221);
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < TimeSpan.FromSeconds(10) && (refusedStale < 20 || bound < 200))
            {
                server.PollEvents();
                foreach (NetManager client in clients)
                    client.PollEvents();
                long now = Environment.TickCount64;
                for (int i = held.Count - 1; i >= 0; i--)
                {
                    if (now - held[i].At < random.Next(5, 60))
                        continue;
                    LiteNetLibJoinRequest request = held[i].Request;
                    held.RemoveAt(i);
                    var session = new object();
                    if (request.Accept(session) is not DirectClientChannel channel)
                    {
                        refusedStale++;
                        continue;
                    }
                    bound++;
                    if (
                        channel.Peer.ConnectionState != ConnectionState.Connected
                        || !ReferenceEquals(channel.Peer.Tag, session)
                    )
                        wrong++;
                    // Ends it from this side too, so the next accept at the address meets a peer in each state.
                    if (bound % 2 == 0)
                        channel.Disconnect();
                }
                Thread.Sleep(0);
            }
        }
        finally
        {
            foreach (NetManager client in clients)
                client.Stop();
            server.Stop();
        }
        Console.WriteLine(
            $"join-accept-stale-peer: {requests} requests in {clock.Elapsed.TotalSeconds:F1}s, {bound} bound to a fresh peer, "
                + $"{refusedStale} accepts refused for handing back an existing peer, {wrong} bound wrongly"
        );
        Check(
            wrong == 0,
            $"{wrong} accepts bound a session to a peer that is not connected or not its own"
        );
        Check(bound > 0, "some accepts bind a session");
        Check(
            refusedStale > 0,
            "LiteNetLib handed back no existing peer: the case this guards was not provoked"
        );
    }
}
