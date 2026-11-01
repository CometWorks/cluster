using System;
using System.Runtime.CompilerServices;
using LiteNetLib;
using VRage.GameServices;

namespace ClusterPlugin;

public static class DirectTransportNodeLinkPatch
{
	private const byte Channels = 16;
	private static readonly ConditionalWeakTable<object, NodeLinkState<NetPeer>> States = new ConditionalWeakTable<object, NodeLinkState<NetPeer>>();
	private static readonly object GlobalSync = new object();
	private static NetPeer m_globalPeer;
	public static Action<byte[]> PluginMessageReceived;
    public static bool IsConnected { get { lock (GlobalSync) return m_globalPeer?.ConnectionState == ConnectionState.Connected; } }
    public static bool SendPlugin(byte[] payload)
    {
        if (payload == null || payload.Length == 0 || payload.Length > 128 * 1024) return false;
        lock (GlobalSync)
        {
            if (m_globalPeer?.ConnectionState != ConnectionState.Connected || m_globalPeer.GetPacketsCountInReliableQueue(0, false) > 256) return false;
            m_globalPeer.Send(NodeLinkCodec.Write(NodeLinkMessage.Plugin, 0, payload: payload, payloadLength: payload.Length), 0, DeliveryMethod.ReliableUnordered);
            return true;
        }
    }
    public static Action<byte[]> GlobalMessageReceived;
	public static Action<bool> GlobalLinkChanged;
	public static Func<ulong, byte[], bool> AttachBindingAccepted;
	public static Action<ulong> AttachBindingRemoved;

	/// <summary>
	/// T-0154: <paramref name="ordered"/> false sends on LiteNetLib's ReliableUnordered channel, a
	/// separate queue from the ReliableOrdered one the bulk publishes use, so a small frame never
	/// waits behind megabytes of them. Commands and their responses are matched by sequence and
	/// operation id, so they need reliability, not order.
	/// </summary>
	public static bool SendGlobal(byte[] payload, bool ordered = true)
	{
		if (payload == null || payload.Length == 0) return false;
		lock (GlobalSync)
		{
			if (m_globalPeer?.ConnectionState != ConnectionState.Connected) return false;
			m_globalPeer.Send(NodeLinkCodec.Write(NodeLinkMessage.Global, 0, payload: payload, payloadLength: payload.Length), 0,
				ordered ? DeliveryMethod.ReliableOrdered : DeliveryMethod.ReliableUnordered);
			return true;
		}
	}

	/// <summary>T-0154 diagnostics: packets LiteNetLib still holds on the gateway link, per delivery
	/// kind, channel 0 (global) and all channels summed. Null when there is no link.</summary>
	public static object GlobalQueueDepths()
	{
		NetPeer peer;
		lock (GlobalSync) peer = m_globalPeer;
		if (peer == null) return null;
		int orderedTotal = 0, unorderedTotal = 0;
		for (byte channel = 0; channel < Channels; channel++)
		{
			orderedTotal += peer.GetPacketsCountInReliableQueue(channel, true);
			unorderedTotal += peer.GetPacketsCountInReliableQueue(channel, false);
		}
		return new
		{
			globalOrdered = peer.GetPacketsCountInReliableQueue(0, true),
			globalUnordered = peer.GetPacketsCountInReliableQueue(0, false),
			orderedTotal, unorderedTotal, mtu = peer.Mtu, ping = peer.Ping,
		};
	}

	public static bool SendPrefix(object __instance, ulong remoteUser, byte[] data, int byteCount,
		MyP2PMessageEnum msgType, int channel, ref bool result)
	{
		NodeLinkState<NetPeer> nodeLink = State(__instance);
		if (!nodeLink.TryGetPeer(remoteUser, out NetPeer peer))
			return true;
		bool reliable = msgType == MyP2PMessageEnum.Reliable || msgType == MyP2PMessageEnum.ReliableWithBuffering;
		DeliveryMethod delivery = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
		byte[] envelope = NodeLinkCodec.Write(NodeLinkMessage.Relay, remoteUser, checked((byte)channel), data, byteCount);
		if (!reliable && envelope.Length > peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable))
			delivery = DeliveryMethod.ReliableUnordered;
		if (!nodeLink.SendOrQueue(remoteUser, new PendingRelay(envelope, LinkChannel(remoteUser), delivery), out PendingRelay immediate))
			return true;
		if (immediate.Data != null) peer.Send(immediate.Data, immediate.Channel, immediate.Delivery);
		result = true;
		return false;
	}

	public static bool AcceptSessionPrefix(object __instance, ulong remotePeerId, ref bool result)
	{
		NodeLinkState<NetPeer> nodeLink = State(__instance);
		if (!nodeLink.Contains(remotePeerId))
			return true;
		result = true;
		return false;
	}

	public static bool CloseSessionPrefix(object __instance, ulong remotePeerId, ref bool result)
	{
		NodeLinkState<NetPeer> nodeLink = State(__instance);
		if (!nodeLink.TryGetPeer(remotePeerId, out NetPeer peer))
			return true;
		peer.Send(NodeLinkCodec.Write(NodeLinkMessage.Detach, remotePeerId), LinkChannel(remotePeerId), DeliveryMethod.ReliableOrdered);
		nodeLink.Detach(remotePeerId);
		AttachBindingRemoved?.Invoke(remotePeerId);
		result = true;
		return false;
	}

	public static bool GetSessionStatePrefix(object __instance, ulong remoteUser, ref MyP2PSessionState state, ref bool result)
	{
		NodeLinkState<NetPeer> nodeLink = State(__instance);
		if (!nodeLink.TryGetPeer(remoteUser, out NetPeer peer))
			return true;
		bool connected = peer.ConnectionState == ConnectionState.Connected;
		state = default(MyP2PSessionState);
		state.ConnectionActive = connected;
		state.UsingRelay = false;
		result = connected;
		return false;
	}

	public static bool ConnectionRequestPrefix(object __instance, ConnectionRequest request, NetManager manager,
		string protocolKey, Func<ulong, bool> physicalPeerExists, Action<string> log)
	{
		NodeLinkState<NetPeer> nodeLink = State(__instance);
		int position = request.Data.Position;
		if (!request.Data.TryGetString(out string key) || key != protocolKey || !request.Data.TryGetULong(out ulong clientId))
		{
			request.Data.SetPosition(position);
			return true;
		}
		if (clientId != NodeLinkCodec.NodeLinkId)
		{
			request.Data.SetPosition(position);
			if (nodeLink.Contains(clientId))
			{
				request.Reject();
				return false;
			}
			return true;
		}
		if (request.Data.AvailableBytes == 0 || request.Data.GetString() != NodeLinkCodec.ProtocolKey
			|| request.Data.AvailableBytes == 0 || !NodeLinkAuth.Accepts(request.Data.GetString())
			|| request.Data.AvailableBytes != 0)
		{
			// Security.md §4.1/§4.3: the node⇄Gateway data link carries the registry-issued join
			// token, so reaching the port is not enough to attach clients to this node.
			log("Rejected cluster node link from " + request.RemoteEndPoint + ": join token not accepted");
			request.Reject();
			return false;
		}
		manager.ChannelsCount = Channels;
		NetPeer peer = request.Accept();
		peer.Tag = 0uL;
		if (!nodeLink.TryConnect(peer))
			peer.Disconnect();
		else
		{
			lock (GlobalSync) m_globalPeer = peer;
			GlobalLinkChanged?.Invoke(true);
			log("Accepted cluster node link from " + request.RemoteEndPoint);
		}
		return false;
	}

	public static void DisconnectedPostfix(object __instance, NetPeer peer, string reason, Action<ulong, string> connectionFailed)
	{
		NodeLinkState<NetPeer> nodeLink = State(__instance);
		bool globalDisconnected;
		lock (GlobalSync)
		{
			globalDisconnected = ReferenceEquals(m_globalPeer, peer);
			if (globalDisconnected) m_globalPeer = null;
		}
		if (globalDisconnected) GlobalLinkChanged?.Invoke(false);
		foreach (ulong client in nodeLink.Disconnect(peer))
		{
			AttachBindingRemoved?.Invoke(client);
			connectionFailed(client, reason);
		}
	}

	public static bool ReceivePrefix(object __instance, NetPeer peer, NetPacketReader reader, Action<string> log,
		Func<ulong, bool> physicalPeerExists, Action<ulong> sessionRequest,
		Action<ulong, string> connectionFailed, Action<int, ulong, byte[]> enqueue)
	{
		NodeLinkState<NetPeer> nodeLink = State(__instance);
		if (!nodeLink.IsLink(peer))
			return true;
		NodeLinkPacket packet;
		try
		{
			packet = NodeLinkCodec.Read(reader.GetRemainingBytes());
		}
		catch (Exception exception)
		{
			log("Rejected malformed cluster node-link packet: " + exception.Message);
			peer.Disconnect();
			return false;
		}
		if (packet.Kind == NodeLinkMessage.Attach)
		{
			if (physicalPeerExists(packet.ClientId))
			{
				log("Rejected node-link client ID already used by a physical peer: " + packet.ClientId);
				peer.Send(NodeLinkCodec.Write(NodeLinkMessage.Detach, packet.ClientId), LinkChannel(packet.ClientId), DeliveryMethod.ReliableOrdered);
				return false;
			}
			if (AttachBindingAccepted?.Invoke(packet.ClientId, packet.Payload) == false)
			{
				log("Rejected client without a current World Authority player binding: " + packet.ClientId);
				peer.Send(NodeLinkCodec.Write(NodeLinkMessage.Detach, packet.ClientId), LinkChannel(packet.ClientId), DeliveryMethod.ReliableOrdered);
				return false;
			}
			bool attached = nodeLink.Attach(packet.ClientId);
			if (attached)
			{
				log("Accepted client " + packet.ClientId + " through cluster node link");
				sessionRequest(packet.ClientId);
			}
			if (attached || nodeLink.Contains(packet.ClientId))
				peer.Send(NodeLinkCodec.Write(NodeLinkMessage.AttachAck, packet.ClientId), LinkChannel(packet.ClientId), DeliveryMethod.ReliableOrdered);
		}
		else if (packet.Kind == NodeLinkMessage.Detach)
		{
			if (nodeLink.Detach(packet.ClientId))
			{
				AttachBindingRemoved?.Invoke(packet.ClientId);
				connectionFailed(packet.ClientId, "Gateway detached client");
			}
			peer.Send(NodeLinkCodec.Write(NodeLinkMessage.DetachAck, packet.ClientId), LinkChannel(packet.ClientId), DeliveryMethod.ReliableOrdered);
		}
		else if (packet.Kind == NodeLinkMessage.Credit)
		{
			foreach (PendingRelay pending in nodeLink.GrantCredit(packet.ClientId, packet.Credit))
				peer.Send(pending.Data, pending.Channel, pending.Delivery);
		}
		else if (packet.Kind is NodeLinkMessage.AttachAck or NodeLinkMessage.DetachAck)
		{
			log("Rejected directionally invalid node-link acknowledgement");
			peer.Disconnect();
		}
		else if (packet.Kind == NodeLinkMessage.Plugin)
            PluginMessageReceived?.Invoke(packet.Payload);
        else if (packet.Kind == NodeLinkMessage.Global)
			GlobalMessageReceived?.Invoke(packet.Payload);
		else if (nodeLink.Contains(packet.ClientId))
			enqueue(packet.Channel, packet.ClientId, packet.Payload);
		else
		{
			// The gateway relayed a game packet for a client this node does not consider
			// attached: the two sides have diverged (seen after topology changes). Silence
			// here black-holes the client's requests while its session looks healthy
			// (KI-2, split-brain ghost world). Say so, and send Detach back so the gateway
			// re-attaches the client or drops it cleanly instead of relaying into the void.
			if (NoteUnattachedRelay(packet.ClientId))
			{
				log("Dropped relayed packet for unattached client " + packet.ClientId
					+ "; sent Detach so the gateway re-attaches or disconnects it");
				peer.Send(NodeLinkCodec.Write(NodeLinkMessage.Detach, packet.ClientId),
					LinkChannel(packet.ClientId), DeliveryMethod.ReliableOrdered);
			}
		}
		return false;
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> UnattachedRelayNoted = new();

	/// <summary>At most one notification per client per second, so a fast packet stream
	/// cannot flood the log or the link while the gateway converges.</summary>
	private static bool NoteUnattachedRelay(ulong clientId)
	{
		long now = Environment.TickCount64;
		long last = UnattachedRelayNoted.GetOrAdd(clientId, long.MinValue);
		if (now - last < 1000) return false;
		return UnattachedRelayNoted.TryUpdate(clientId, now, last);
	}

	private static byte LinkChannel(ulong clientId) => (byte)(clientId % Channels);
	private static NodeLinkState<NetPeer> State(object instance) => States.GetValue(instance, _ => new NodeLinkState<NetPeer>());
}
