using System;
using System.Linq;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using VRage;
using VRage.GameServices;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1156 bench routes: put a node's vanilla member list into the state live node-0 was in, a player listed with no
/// session behind them, either directly or through the race that caused it live (a leave run right before the
/// client's queued CLIENT_CONNECTED). A Harmony prefix installed on first arm, ahead of the production one.
/// </summary>
internal static class StaleMemberRoutes
{
    private static ulong m_raceClient;
    private static bool m_patched;

    public static void Register()
    {
        // GET /members - the node's vanilla member list (MyDedicatedServerBase.m_members), the server's own id included,
        // its vanilla member limit and the world's MaxPlayers (SE-1203).
        TestRoutes.Add("GET", "/members", context =>
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => (object)new
            {
                members = Server().m_members.ToArray(),
                memberLimit = Server().MemberLimit,
                maxPlayers = MySession.Static?.MaxPlayers ?? 0,
            })));

        // POST /stale-member-inject?client= - list the client as a member, with no session: the leaked entry itself.
        TestRoutes.Add("POST", "/stale-member-inject", context =>
        {
            ulong client = (ulong)TestRoutes.Long(context, "client");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                Server().m_members.Add(client);
                Plugin.Log($"stale-member-inject client={client} (SE-1156)");
                return (object)new { members = Server().m_members.ToArray() };
            }));
        });

        // POST /stale-member-race?client= - arm (0 disarms): the client's next CLIENT_CONNECTED is preceded by a leave
        // on the game thread, as its relayed Disconnected message did live after the message was already queued.
        TestRoutes.Add("POST", "/stale-member-race", context =>
        {
            ulong client = (ulong)TestRoutes.Long(context, "client");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                EnsurePatched();
                m_raceClient = client;
                Plugin.Log($"stale-member-race armed client={client} (SE-1156)");
                return (object)new { armed = client };
            }));
        });
    }

    private static MyDedicatedServerBase Server() =>
        MyMultiplayer.Static as MyDedicatedServerBase ?? throw new InvalidOperationException("no dedicated server session");

    private static void EnsurePatched()
    {
        if (m_patched)
            return;
        new Harmony("ClusterTestPlugin.StaleMemberRace").Patch(
            AccessTools.Method(typeof(MyDedicatedServerBase), nameof(MyDedicatedServerBase.ClientConnected)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(StaleMemberRoutes), nameof(ClientConnectedPrefix)))
            {
                priority = Priority.First,
            });
        m_patched = true;
    }

    private static void ClientConnectedPrefix(MyPacket packet)
    {
        ulong client = packet.Sender.Id.Value;
        if (client == 0 || client != m_raceClient)
            return;
        m_raceClient = 0;
        Plugin.Log($"stale-member-race leave before CLIENT_CONNECTED client={client} (SE-1156)");
        Server().RaiseClientLeft(client, MyChatMemberStateChangeEnum.Disconnected);
    }
}
