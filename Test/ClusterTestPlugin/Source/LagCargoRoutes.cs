using System;
using System.Globalization;
using System.Threading;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game.Entity;
using VRage.Network;

namespace ClusterTestPlugin;

/// <summary>
/// SE-2010 bench route: a client far from the server acknowledges a replicable a round trip after it was sent,
/// so a handover in that window exports it as pending. Here the window is made to last: while armed, this node
/// drops the client's ReplicableReady for the replicables of one grid (its blocks and inventories; with
/// children=false the grid's own too),
/// as if the acknowledgements were still on their way when the client left. A Harmony prefix on
/// MyReplicationServer.ReplicableReady, installed on first arm, on this test plugin only.
/// </summary>
internal static class LagCargoRoutes
{
    private static ulong m_client;
    private static long m_grid;
    private static long m_dropped;
    private static bool m_childrenOnly;
    private static bool m_patched;

    public static void Register()
    {
        // POST /lag-cargo-test-hold-acks?client=&grid=&armed=true|false&children=true|false
        TestRoutes.Add(
            "POST",
            "/lag-cargo-test-hold-acks",
            context =>
            {
                ulong client = ulong.Parse(
                    TestRoutes.Query(context, "client"),
                    CultureInfo.InvariantCulture
                );
                long grid = TestRoutes.Long(context, "grid");
                bool armed = TestRoutes.Query(context, "armed", "true") == "true";
                bool childrenOnly = TestRoutes.Query(context, "children", "true") == "true";
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        EnsurePatched();
                        m_client = client;
                        m_childrenOnly = childrenOnly;
                        Interlocked.Exchange(ref m_grid, armed ? grid : 0);
                        return new
                        {
                            armed,
                            client,
                            grid,
                            childrenOnly,
                            dropped = Interlocked.Read(ref m_dropped),
                        };
                    })
                );
            }
        );
    }

    private static void EnsurePatched()
    {
        if (m_patched)
            return;
        var original =
            AccessTools.Method(typeof(MyReplicationServer), "ReplicableReady")
            ?? throw new MissingMethodException("MyReplicationServer.ReplicableReady");
        new Harmony("ClusterTestPlugin.LagCargo").Patch(
            original,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(LagCargoRoutes), nameof(ReplicableReadyPrefix))
            )
        );
        m_patched = true;
    }

    private static bool ReplicableReadyPrefix(MyReplicationServer __instance, MyPacket packet)
    {
        long grid = Interlocked.Read(ref m_grid);
        if (grid == 0 || packet.Sender.Id.Value != m_client)
            return true;
        long position = packet.BitStream.BitPosition;
        NetworkId id = packet.BitStream.ReadNetworkId();
        packet.BitStream.SetBitPositionRead(position);
        if (
            !(__instance.GetObjectByNetworkId(id) is IMyReplicable replicable)
            || TopGrid(replicable) != grid
            || (m_childrenOnly && replicable.GetParent() == null)
        )
            return true;
        Interlocked.Increment(ref m_dropped);
        packet.Return();
        return false;
    }

    private static long TopGrid(IMyReplicable replicable)
    {
        for (int depth = 0; replicable != null && depth < 8; depth++)
        {
            if (replicable is IMyEntityReplicable entity)
                return MyEntities.TryGetEntityById(entity.EntityId, out MyEntity found)
                    ? found.GetTopMostParent().EntityId
                    : 0;
            replicable = replicable.GetParent();
        }
        return 0;
    }
}
