using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using LiteNetLib;

namespace ClusterRegistry;

/// <summary>
/// #1279 (T-0296 round 5, F36): <c>ClientSession.AttachRetryDetachedBy</c> exempts the attach retry from a
/// backend switch's hold only for the link whose node sent Detach. It was never cleared, so after one Detach
/// from node L during a switch, every later switch toward L let a granted binding attach mid-switch, which the
/// switch does itself.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// Switch 1: node L detaches the client, the retry re-attaches it to L (exempt) and the node acknowledges.
    /// Switch 2 toward L: a granted binding must be held, no Attach until the switch ends. Then the switch ends
    /// and the held retry attaches (the known positive that the retry was alive and holding).
    /// </summary>
    private static void TestAttachRetrySwitchHold()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("attach-retry-switch-hold: " + what);
        }
        const ulong clientId = 1279;
        int nodePort = FreeUdpPort();
        int gatewayPort = FreeUdpPort();
        var attaches = new ConcurrentQueue<DateTimeOffset>();
        NetPeer? nodePeer = null;
        var listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += request => nodePeer = request.Accept();
        listener.NetworkReceiveEvent += (peer, reader, _, delivery) =>
        {
            NodeLinkPacket packet = DirectTransportCodec.DecodeNodeLink(
                reader.GetRemainingBytes(),
                delivery
            );
            if (
                packet.ClientId != clientId
                || packet.Kind is not (NodeLinkMessage.Attach or NodeLinkMessage.Detach)
            )
                return;
            if (packet.Kind == NodeLinkMessage.Attach)
                attaches.Enqueue(DateTimeOffset.UtcNow);
            NodeLinkMessage kind =
                packet.Kind == NodeLinkMessage.Attach
                    ? NodeLinkMessage.AttachAck
                    : NodeLinkMessage.DetachAck;
            NodeLinkPacket ack = new(kind, clientId, 0, [], DeliveryMethod.ReliableOrdered);
            peer.Send(
                DirectTransportCodec.EncodeNodeLink(ack),
                (byte)(clientId % 16),
                ack.Delivery
            );
        };
        NetManager node = TestManager(listener);
        Assert(node.Start(IPAddress.Loopback, IPAddress.IPv6Any, nodePort));
        var options = new GatewayOptions(
            new(IPAddress.Loopback, gatewayPort),
            new(IPAddress.Loopback, nodePort),
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
            DirectFrontend: true
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
                Console.Error.WriteLine("attach-retry-switch-hold gateway failed: " + e);
            }
        })
        {
            IsBackground = true,
            Name = "Gateway-Attach-Retry-Switch-Hold-Self-Test",
        };
        gatewayThread.Start();
        try
        {
            using TestClient client = new(clientId, gatewayPort);
            NetManager[] managers = [node, client.Manager];
            PumpUntil(() => attaches.Count == 1, managers);

            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var clients = (IDictionary)
                typeof(DirectGateway).GetField("_clients", flags)!.GetValue(gateway)!;
            object session = clients[clientId]!;
            Type type = session.GetType();
            void Switching(bool value) =>
                type.GetProperty("SwitchingBackend")!.SetValue(session, value);
            bool Retrying() => (int)type.GetField("AttachRetryStarted")!.GetValue(session)! != 0;
            PumpUntil(() => !Retrying(), managers);

            // Switch 1 toward node L: L detaches the client, the retry re-attaches it there even mid-switch.
            Switching(true);
            NodeLinkPacket detach = new(
                NodeLinkMessage.Detach,
                clientId,
                0,
                [],
                DeliveryMethod.ReliableOrdered
            );
            nodePeer!.Send(
                DirectTransportCodec.EncodeNodeLink(detach),
                (byte)(clientId % 16),
                detach.Delivery
            );
            PumpUntil(() => attaches.Count == 2, managers);
            PumpUntil(() => !Retrying(), managers);
            Switching(false);

            // Switch 2 toward the same node: a granted binding (a new revision, as "Player binding granted" makes
            // it) is held until the switch ends.
            Switching(true);
            FieldInfo revision = type.GetField("BindingRevision")!;
            revision.SetValue(session, (long)revision.GetValue(session)! + 1);
            typeof(DirectGateway)
                .GetMethod("AttachGrantedPlayer", flags)!
                .Invoke(gateway, [session]);
            PumpFor(TimeSpan.FromSeconds(1), managers);
            Check(
                attaches.Count == 2,
                $"no Attach mid-switch for a granted binding ({attaches.Count - 2} sent during switch 2 toward the node that detached in switch 1)"
            );
            Check(Retrying(), "the granted binding's attach retry is running");
            DateTimeOffset ended = DateTimeOffset.UtcNow;
            Switching(false);
            PumpUntil(() => attaches.Count == 3, managers);
            Check(attaches.Last() >= ended, "the held attach goes out once the switch ends");
            PumpUntil(() => !Retrying(), managers);
            Console.WriteLine(
                "attach-retry-switch-hold: a granted binding is held through a later switch toward the node that detached earlier"
            );
        }
        finally
        {
            gateway.Stop();
            gatewayThread.Join(2000);
            node.Stop();
        }
    }
}
