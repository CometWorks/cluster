using System;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using VRage;
using VRage.Library.Collections;
using VRage.Network;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// MyTransportLayer diagnostic: the pair to the replication layer's
/// <c>rpc_in</c> line - an RPC the transport dispatches but the replication layer never parses died between
/// the two, and one with no handler died here. Rather than logging inside ProcessMessage after the handler
/// lookup, the prefix peeks the two header bytes the method is about to read (restoring the read position,
/// the same trick HandleMessage uses) and repeats the read-only handler lookup.
///
/// Gated on the same CLUSTER_RPC_TRACE=1 environment flag as the replication layer's RpcTrace, read
/// here so this patch does not depend on the replication layer's seam holder. Off: one static bool test.
/// </summary>
[HarmonyPatch(typeof(MyTransportLayer), "ProcessMessage")]
public static class MyTransportLayerPatch
{
    public static readonly bool RpcTrace = Environment.GetEnvironmentVariable("CLUSTER_RPC_TRACE") == "1";

    [HarmonyPrefix]
    private static void ProcessMessagePrefix(MyTransportLayer __instance, MyPacket p)
    {
        if (!RpcTrace)
            return;
        try
        {
            BitStream stream = p.BitStream;
            long position = stream.BitPosition;
            MyMessageId messageId = (MyMessageId)stream.ReadByte();
            byte receiverIndex = stream.ReadByte();
            stream.SetBitPositionRead(position);
            if (messageId != MyMessageId.RPC)
                return;
            if (!__instance.m_handlers.TryGetValue(new MyTransportLayer.HandlerId { messageId = messageId, receiverIndex = receiverIndex }, out Action<MyPacket> handler))
            {
                __instance.m_handlers.TryGetValue(new MyTransportLayer.HandlerId { messageId = messageId, receiverIndex = byte.MaxValue }, out handler);
            }
            MyLog.Default?.WriteLine($"rpc_msg index={receiverIndex} bytes={stream.ByteLength} "
                + $"handler={handler != null} buffering={__instance.IsBuffering} source={p.Sender.Id.Value}");
        }
        catch (Exception)
        {
            // Diagnostics must never be able to take down the network thread.
        }
    }
}
