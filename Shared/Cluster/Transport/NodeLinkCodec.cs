using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using LiteNetLib;

namespace ClusterPlugin;

internal enum NodeLinkMessage : byte
{
	Relay = 1,
	Attach = 2,
	Detach = 3,
	AttachAck = 4,
	DetachAck = 5,
	Credit = 6,
	Global = 7,
	Plugin = 8
}

internal readonly struct NodeLinkPacket
{
	public readonly NodeLinkMessage Kind;
	public readonly ulong ClientId;
	public readonly byte Channel;
	public readonly byte[] Payload;
	public readonly int Credit;

	public NodeLinkPacket(NodeLinkMessage kind, ulong clientId, byte channel, byte[] payload, int credit = 0)
	{
		Kind = kind;
		ClientId = clientId;
		Channel = channel;
		Payload = payload;
		Credit = credit;
	}
}

internal static class NodeLinkCodec
{
	public const ulong NodeLinkId = ulong.MaxValue;
	public const string ProtocolKey = "SE-ClusterNodeLink-v2";

	public static byte[] Write(NodeLinkMessage kind, ulong clientId, byte channel = 0, byte[] payload = null, int payloadLength = 0, int credit = 0)
	{
		if (kind is not (NodeLinkMessage.Relay or NodeLinkMessage.Global or NodeLinkMessage.Plugin or NodeLinkMessage.Attach) && (channel != 0 || payloadLength != 0))
			throw new ArgumentException("Node-link control packets cannot carry payload");
		if ((kind is NodeLinkMessage.Relay or NodeLinkMessage.Global or NodeLinkMessage.Plugin or NodeLinkMessage.Attach) && (payloadLength < 0
			|| payload == null && payloadLength != 0 || payload != null && payloadLength > payload.Length
			|| kind == NodeLinkMessage.Attach && payloadLength > 4096))
			throw new ArgumentException("Invalid node-link relay payload");

		if (kind == NodeLinkMessage.Credit && credit <= 0) throw new ArgumentException("Node-link credit must be positive");
		byte[] result = new byte[1 + sizeof(ulong) + (kind == NodeLinkMessage.Relay ? 1 + payloadLength
			: kind is NodeLinkMessage.Global or NodeLinkMessage.Plugin or NodeLinkMessage.Attach ? payloadLength : kind == NodeLinkMessage.Credit ? sizeof(int) : 0)];
		result[0] = (byte)kind;
		BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(1), clientId);
		if (kind == NodeLinkMessage.Relay)
		{
			result[9] = channel;
			if (payloadLength != 0) Buffer.BlockCopy(payload, 0, result, 10, payloadLength);
		}
		else if (kind is NodeLinkMessage.Global or NodeLinkMessage.Plugin or NodeLinkMessage.Attach && payloadLength != 0)
			Buffer.BlockCopy(payload, 0, result, 9, payloadLength);
		else if (kind == NodeLinkMessage.Credit)
			BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(9), credit);
		return result;
	}

	public static NodeLinkPacket Read(byte[] data)
	{
		if (data == null || data.Length < 9)
			throw new InvalidOperationException("Node-link packet is truncated");
		NodeLinkMessage kind = (NodeLinkMessage)data[0];
		ulong clientId = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(1, 8));
		if (kind == NodeLinkMessage.Attach)
		{
			if (data.Length > 4105) throw new InvalidOperationException("Node-link attach payload is too large");
			return new NodeLinkPacket(kind, clientId, 0, data.AsSpan(9).ToArray());
		}
		if (kind is NodeLinkMessage.Detach or NodeLinkMessage.AttachAck or NodeLinkMessage.DetachAck)
		{
			if (data.Length != 9)
				throw new InvalidOperationException("Node-link control packet has trailing data");
			return new NodeLinkPacket(kind, clientId, 0, Array.Empty<byte>());
		}
		if (kind == NodeLinkMessage.Credit)
		{
			if (data.Length != 13) throw new InvalidOperationException("Invalid node-link credit packet");
			int credit = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(9));
			if (credit <= 0) throw new InvalidOperationException("Node-link credit must be positive");
			return new NodeLinkPacket(kind, clientId, 0, Array.Empty<byte>(), credit);
		}
		if (kind is NodeLinkMessage.Global or NodeLinkMessage.Plugin)
		{
			if (clientId != 0 || data.Length == 9 || kind == NodeLinkMessage.Plugin && data.Length > 128 * 1024 + 9) throw new InvalidOperationException("Invalid node-link global packet");
			return new NodeLinkPacket(kind, 0, 0, data.AsSpan(9).ToArray());
		}
		if (kind != NodeLinkMessage.Relay || data.Length < 10)
			throw new InvalidOperationException("Unknown or truncated node-link packet");
		byte[] payload = new byte[data.Length - 10];
		Buffer.BlockCopy(data, 10, payload, 0, payload.Length);
		return new NodeLinkPacket(kind, clientId, data[9], payload);
	}
}

internal sealed class NodeLinkState<TPeer> where TPeer : class
{
	private readonly object m_sync = new object();
	private readonly HashSet<ulong> m_clients = new HashSet<ulong>();
	private readonly Dictionary<ulong, int> m_credits = new Dictionary<ulong, int>();
	private readonly Dictionary<ulong, Queue<PendingRelay>> m_pending = new Dictionary<ulong, Queue<PendingRelay>>();
	private readonly Dictionary<ulong, int> m_pendingBytes = new Dictionary<ulong, int>();
	private TPeer m_peer;

	public bool TryConnect(TPeer peer)
	{
		lock (m_sync)
		{
			if (m_peer != null)
				return false;
			m_peer = peer;
			return true;
		}
	}

	public bool IsLink(TPeer peer)
	{
		lock (m_sync) return ReferenceEquals(m_peer, peer);
	}

	public bool Attach(ulong clientId)
	{
		lock (m_sync) return clientId != 0 && clientId != NodeLinkCodec.NodeLinkId && m_clients.Add(clientId);
	}

	public bool Detach(ulong clientId)
	{
		lock (m_sync)
		{
			m_credits.Remove(clientId);
			m_pending.Remove(clientId);
			m_pendingBytes.Remove(clientId);
			return m_clients.Remove(clientId);
		}
	}

	public bool SendOrQueue(ulong clientId, PendingRelay relay, out PendingRelay immediate)
	{
		lock (m_sync)
		{
			immediate = default(PendingRelay);
			if (!m_clients.Contains(clientId)) return false;
			int credit = m_credits.GetValueOrDefault(clientId);
			if (credit >= relay.Data.Length)
			{
				m_credits[clientId] = credit - relay.Data.Length;
				immediate = relay;
				return true;
			}
			if (!m_pending.TryGetValue(clientId, out Queue<PendingRelay> queue))
				m_pending[clientId] = queue = new Queue<PendingRelay>();
			int pendingBytes = m_pendingBytes.GetValueOrDefault(clientId);
			if (pendingBytes > 4 * 1024 * 1024 - relay.Data.Length) return false;
			queue.Enqueue(relay);
			m_pendingBytes[clientId] = pendingBytes + relay.Data.Length;
			return true;
		}
	}

	public PendingRelay[] GrantCredit(ulong clientId, int bytes)
	{
		lock (m_sync)
		{
			if (!m_clients.Contains(clientId) || bytes <= 0) return Array.Empty<PendingRelay>();
			int credit = (int)Math.Min(int.MaxValue, (long)m_credits.GetValueOrDefault(clientId) + bytes);
			if (!m_pending.TryGetValue(clientId, out Queue<PendingRelay> queue))
			{
				m_credits[clientId] = credit;
				return Array.Empty<PendingRelay>();
			}
			List<PendingRelay> ready = new List<PendingRelay>();
			while (queue.Count != 0 && queue.Peek().Data.Length <= credit)
			{
				PendingRelay relay = queue.Dequeue();
				m_pendingBytes[clientId] -= relay.Data.Length;
				credit -= relay.Data.Length;
				ready.Add(relay);
			}
			if (queue.Count == 0) { m_pending.Remove(clientId); m_pendingBytes.Remove(clientId); }
			m_credits[clientId] = credit;
			return ready.ToArray();
		}
	}

	public bool Contains(ulong clientId)
	{
		lock (m_sync) return m_clients.Contains(clientId);
	}

	public bool TryGetPeer(ulong clientId, out TPeer peer)
	{
		lock (m_sync)
		{
			peer = m_peer;
			return peer != null && m_clients.Contains(clientId);
		}
	}

	public ulong[] Disconnect(TPeer peer)
	{
		lock (m_sync)
		{
			if (!ReferenceEquals(m_peer, peer))
				return Array.Empty<ulong>();
			m_peer = null;
			ulong[] clients = new ulong[m_clients.Count];
			m_clients.CopyTo(clients);
			m_clients.Clear();
			m_credits.Clear();
			m_pending.Clear();
			m_pendingBytes.Clear();
			return clients;
		}
	}
}

internal readonly struct PendingRelay
{
	public readonly byte[] Data;
	public readonly byte Channel;
	public readonly DeliveryMethod Delivery;
	public PendingRelay(byte[] data, byte channel, DeliveryMethod delivery) { Data = data; Channel = channel; Delivery = delivery; }
}
