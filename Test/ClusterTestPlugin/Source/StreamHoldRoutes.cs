using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using VRage.Network;
using VRage.Replication;

namespace ClusterTestPlugin;

/// <summary>
/// T-0396 bench routes: keep one grid "still streaming" to one client for as long as the bench wants.
///
/// A handover that exports a client's view while a grid is pending/streaming to it makes the target re-deliver
/// the grid (ReplicationClientViewPatch.Adopt: RemoveForClient + AddForClient, i.e. a destroy and a fresh
/// create on the client). Live, that window is a fraction of a second and only a race finds it. Here the
/// stream of the named grid to the named client is simply not sent while the hold is armed: vanilla
/// MyReplicationServer.SendStreamingEntry is skipped for that (client, grid) pair and the entry rescheduled, so
/// the client has the grid's create-begin (pending on both sides) and nothing else of it, for as long as the
/// hold lasts. Everything else of that client, and every other client of that grid, streams as usual.
///
/// A Harmony prefix installed on first arm, on this test plugin only; inert while nothing is armed. The product
/// has no knob for this.
/// </summary>
internal static class StreamHoldRoutes
{
    private static ulong m_client;
    private static long m_grid;
    private static long m_heldSends;
    private static bool m_patched;

    public static void Register()
    {
        // POST /stream-test-hold?client=&entity= - arm: the stream of grid `entity` to client `client` is held.
        TestRoutes.Add("POST", "/stream-test-hold", context =>
        {
            ulong client = ulong.Parse(TestRoutes.Query(context, "client"), System.Globalization.CultureInfo.InvariantCulture);
            long grid = TestRoutes.Long(context, "entity");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                EnsurePatched();
                m_client = client;
                Interlocked.Exchange(ref m_heldSends, 0);
                Interlocked.Exchange(ref m_grid, grid);
                Plugin.Log($"stream-test-hold armed client={client} grid={grid} (T-0396)");
                return (object)new { armed = true, client, grid };
            }));
        });
        // POST /stream-test-release - disarm; the held entry is still scheduled, so the stream goes out next sync.
        TestRoutes.Add("POST", "/stream-test-release", context =>
        {
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                long grid = Interlocked.Exchange(ref m_grid, 0);
                Plugin.Log($"stream-test-hold released client={m_client} grid={grid} heldSends={Interlocked.Read(ref m_heldSends)} (T-0396)");
                return (object)new { armed = false, client = m_client, grid, heldSends = Interlocked.Read(ref m_heldSends) };
            }));
        });
        // GET /stream-test-hold?client=&entity=&character= - what this node's replication server holds for the
        // client: the grid's and the character's view entries (inView / pending / streaming), whether the
        // character replicable is a child of the grid's, and the network ids of the character's whole subtree
        // (replicable, state groups, child replicables and theirs) - the ids a client logs
        // 'Replicated object already exists ... id : N' for.
        TestRoutes.Add("GET", "/stream-test-hold", context =>
        {
            ulong client = ulong.Parse(TestRoutes.Query(context, "client"), System.Globalization.CultureInfo.InvariantCulture);
            long grid = TestRoutes.Long(context, "entity");
            long character = long.Parse(TestRoutes.Query(context, "character", "0"), System.Globalization.CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => State(client, grid, character)));
        });
    }

    private static void EnsurePatched()
    {
        if (m_patched) return;
        var original = AccessTools.Method(typeof(MyReplicationServer), "SendStreamingEntry")
            ?? throw new MissingMethodException("MyReplicationServer.SendStreamingEntry");
        new Harmony("ClusterTestPlugin.StreamHold").Patch(original,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(StreamHoldRoutes), nameof(SendStreamingEntryPrefix))));
        m_patched = true;
    }

    private static bool SendStreamingEntryPrefix(MyReplicationServer __instance, MyClient client, MyStateDataEntry entry)
    {
        long grid = Interlocked.Read(ref m_grid);
        if (grid == 0 || client?.State == null || client.State.EndpointId.Id.Value != m_client) return true;
        if (entry?.Owner is not IMyEntityReplicable owner || owner.EntityId != grid) return true;
        // Not sent: keep it scheduled, as the dirty group would be after a send that wrote nothing.
        Interlocked.Increment(ref m_heldSends);
        __instance.ScheduleStateGroupSync(client, entry, __instance.SyncFrameCounter);
        return false;
    }

    private static object State(ulong clientId, long gridId, long characterId)
    {
        if (MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer server)
            return new { found = false, reason = "not a replication server" };
        Endpoint endpoint = new Endpoint(new EndpointId(clientId), 0);
        server.m_clientStates.TryGetValue(endpoint, out MyClient client);
        IMyReplicable grid = server.m_callback.GetReplicableByEntityId(gridId);
        IMyReplicable character = characterId == 0 ? null : server.m_callback.GetReplicableByEntityId(characterId);
        object Entry(IMyReplicable replicable)
        {
            if (replicable == null) return null;
            MyReplicableClientData held = null;
            bool inView = client != null && client.Replicables.TryGetValue(replicable, out held);
            return new
            {
                networkId = server.GetNetworkIdByObject(replicable).Value,
                inView,
                pending = held?.IsPending ?? false,
                streaming = held?.IsStreaming ?? false,
            };
        }
        List<uint> subtree = new List<uint>();
        if (character != null)
        {
            List<IMyReplicable> all = new List<IMyReplicable> { character };
            server.m_replicables.GetAllChildren(character, all);
            foreach (IMyReplicable replicable in all.Distinct())
            {
                subtree.Add(server.GetNetworkIdByObject(replicable).Value);
                if (server.m_replicableGroups.TryGetValue(replicable, out List<IMyStateGroup> groups))
                    subtree.AddRange(groups.Select(group => server.GetNetworkIdByObject(group).Value));
            }
        }
        MyEntities.TryGetEntityById(characterId, out MyCharacter body);
        return new
        {
            found = true,
            armed = Interlocked.Read(ref m_grid) != 0,
            heldClient = m_client,
            heldGrid = Interlocked.Read(ref m_grid),
            heldSends = Interlocked.Read(ref m_heldSends),
            clientKnown = client != null,
            grid = Entry(grid),
            character = Entry(character),
            characterParentIsGrid = character != null && grid != null && ReferenceEquals(character.GetParent(), grid),
            characterSeated = body?.UsingEntity is MyShipController,
            characterSubtree = subtree.Where(id => id != 0).Distinct().OrderBy(id => id).ToArray(),
        };
    }
}
