using System.Diagnostics;
using System.Net;

namespace ClusterRegistry;

/// <summary>
/// SE-1450 (octahedron-test, cluster 0.3.11): the Steam silence limit ended joins whose world load took longer
/// than 180 s. A client that has not sent CLIENT_READY is held to the longer loading limit, a world-ready one
/// keeps the short one, and a loading client that stays silent past the loading limit is still ended. Driven
/// through gateway-rs's "silent" report, the Steam relay path the live gateway can run; the in-process Steam
/// maintenance uses the same check. The limits are seconds here, not 180 and 600.
/// </summary>
internal static partial class SelfTest
{
    private static void TestLoadingSilence()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("loading-silence: " + what);
        }
        string directory = Directory.CreateTempSubdirectory("loading-silence-selftest-").FullName;
        string path = Path.Combine(directory, "gateway-relay.sock");
        Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", "true");
        TimeSpan silence = DirectGateway.ClientSilenceLimit,
            loading = DirectGateway.LoadingSilenceLimit;
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
                Console.Error.WriteLine("loading-silence gateway failed: " + e);
            }
        })
        {
            IsBackground = true,
            Name = "Gateway-Loading-Silence-Self-Test",
        };
        gatewayThread.Start();
        try
        {
            DirectGateway.ClientSilenceLimit = TimeSpan.FromSeconds(1);
            DirectGateway.LoadingSilenceLimit = TimeSpan.FromSeconds(3);
            using var relay = new ScriptedRelay(path);
            relay.Read(RelayIpcType.Config);
            uint correlation = 0x1450_0000;
            void Join(ulong clientId, ulong session)
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
                string decision = relay
                    .Read(RelayIpcType.JoinAnswer, m => m.Correlation == asked)
                    .Json.GetProperty("decision")
                    .GetString()!;
                Check(decision == "accept", $"client {clientId} is accepted ({decision})");
            }
            void Silent(ulong clientId, ulong session) =>
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
            bool WorldReady(ulong clientId) =>
                gateway.SessionForSelfTest(clientId)?.WorldReady == true;
            void AwaitDisconnect(ulong clientId, ulong session) =>
                relay.Read(
                    RelayIpcType.Disconnect,
                    m =>
                        m.Json.GetProperty("clientId").GetUInt64() == clientId
                        && m.Json.GetProperty("session").GetUInt64() == session
                );

            // A loading client silent for longer than the ready limit (1 s) but within the loading limit (3 s) stays.
            const ulong loader = 76561199500001450;
            var loadClock = Stopwatch.StartNew();
            Join(loader, 1);
            // A world-ready client silent for longer than the ready limit is ended.
            const ulong ready = 76561199500001451;
            Join(ready, 2);
            relay.Send(
                RelayIpcType.ClientMessage,
                0,
                new
                {
                    clientId = ready,
                    session = 2UL,
                    messageId = (int)VanillaJoinCodec.ClientReadyMessageId,
                    channel = 2,
                    packet = Convert.ToBase64String([VanillaJoinCodec.ClientReadyMessageId]),
                }
            );
            var readyWait = Stopwatch.StartNew();
            while (!WorldReady(ready) && readyWait.Elapsed < TimeSpan.FromSeconds(5))
                Thread.Sleep(20);
            Check(WorldReady(ready), "CLIENT_READY marks the second client world-ready");
            Check(!WorldReady(loader), "the first client is still loading");

            Thread.Sleep(1500);
            Silent(loader, 1);
            Silent(ready, 2);
            AwaitDisconnect(ready, 2);
            // Both reports are handled in order, so the loader's verdict is in by now.
            Check(
                gateway.SessionForSelfTest(loader) is { DisconnectReason: null },
                $"a loading client silent for {loadClock.Elapsed.TotalSeconds:F1}s (ready limit 1s) is kept"
            );

            // Silent past the loading limit: a dead loading client is still ended.
            while (loadClock.Elapsed < TimeSpan.FromSeconds(3.2))
                Thread.Sleep(50);
            Silent(loader, 1);
            AwaitDisconnect(loader, 1);
            Console.WriteLine(
                $"loading-silence: world-ready client ended at 1.5s of silence; loading client kept at 1.5s, ended at {loadClock.Elapsed.TotalSeconds:F1}s (limits 1s/3s)"
            );
        }
        finally
        {
            DirectGateway.ClientSilenceLimit = silence;
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
}
