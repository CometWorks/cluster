using LiteNetLib;

namespace ClusterRegistry;

/// <summary>
/// The client half of a session, abstracted over the frontends that can terminate it
/// (Gateway.md §3.1: Direct Transport, Steam, EOS). Everything behind §3.2's normalization
/// boundary - routing, flip, buffering, credit - only ever sees this.
/// </summary>
internal abstract class ClientChannel
{
    public abstract string Frontend { get; }
    public abstract string Remote { get; }
    public abstract bool Connected { get; }

    /// <summary>Packets still queued on the reliable channel; drives the join-window credit.</summary>
    public abstract int ReliableBacklogPackets { get; }

    public abstract void Send(GatewayPacket packet);
    public abstract void Disconnect();

    /// <summary>
    /// SE-1824: ends the session of a client that ignores the host-left message, so its own
    /// transport reports the server gone and it takes vanilla's host-left path. Only Steam does
    /// more than a disconnect: it closes the P2P session and refuses the client's session requests
    /// for <paramref name="block"/>.
    /// </summary>
    public virtual void Boot(TimeSpan block) => Disconnect();
}

internal sealed class DirectClientChannel : ClientChannel
{
    public readonly NetPeer Peer;

    public DirectClientChannel(NetPeer peer) => Peer = peer;

    public override string Frontend => "dt";
    public override string Remote => Peer.ToString();
    public override bool Connected => Peer.ConnectionState == ConnectionState.Connected;
    public override int ReliableBacklogPackets =>
        Peer.GetPacketsCountInReliableQueue(0, ordered: true);

    public override void Send(GatewayPacket packet) =>
        Peer.Send(DirectTransportCodec.EncodePacket(packet), packet.Delivery);

    public override void Disconnect() => Peer.Disconnect();
}
