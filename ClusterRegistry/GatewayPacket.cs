using LiteNetLib;

namespace ClusterRegistry;

internal sealed record GatewayPacket(ulong ClientId, int Channel, byte[] Payload, DeliveryMethod Delivery);
