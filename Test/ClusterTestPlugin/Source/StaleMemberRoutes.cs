using System;
using System.Linq;
using System.Reflection;
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

    // SE-1444: the client whose node-link SessionRequest is held back until its CLIENT_CONNECTED ran, and the held call.
    private static readonly object m_lateSync = new object();
    private static ulong m_lateClient;
    private static object m_lateInstance;
    private static MethodInfo m_raiseSessionRequest;
    [ThreadStatic] private static bool m_releasing;

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

        // POST /session-request-late?client= - arm (0 disarms): the client's next node-link SessionRequest reaches the
        // game thread only after its CLIENT_CONNECTED has run, as it did live after a 725 ms hitch while 14 bots joined
        // (SE-1444). The SessionRequest is held where the node link queues it and queued again from a
        // ClientConnected postfix.
        TestRoutes.Add("POST", "/session-request-late", context =>
        {
            ulong client = (ulong)TestRoutes.Long(context, "client");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                EnsureLatePatched();
                lock (m_lateSync)
                {
                    m_lateClient = client;
                    m_lateInstance = null;
                }
                Plugin.Log($"session-request-late armed client={client} (SE-1444)");
                return (object)new { armed = client };
            }));
        });
    }

    private static void EnsureLatePatched()
    {
        if (m_raiseSessionRequest != null)
            return;
        MethodInfo raise = AccessTools.Method(AccessTools.TypeByName("ClusterPlugin.NodeLinkTransportBinding"), "RaiseSessionRequest")
            ?? throw new MissingMethodException("ClusterPlugin.NodeLinkTransportBinding", "RaiseSessionRequest");
        Harmony harmony = new Harmony("ClusterTestPlugin.SessionRequestLate");
        harmony.Patch(raise, prefix: new HarmonyMethod(AccessTools.Method(typeof(StaleMemberRoutes), nameof(RaiseSessionRequestPrefix))));
        harmony.Patch(
            AccessTools.Method(typeof(MyDedicatedServerBase), nameof(MyDedicatedServerBase.ClientConnected)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(StaleMemberRoutes), nameof(ClientConnectedPostfix))));
        m_raiseSessionRequest = raise;
    }

    // Poll thread: hold the armed client's SessionRequest.
    private static bool RaiseSessionRequestPrefix(object instance, ulong client)
    {
        if (m_releasing)
            return true;
        lock (m_lateSync)
        {
            if (client == 0 || client != m_lateClient || m_lateInstance != null)
                return true;
            m_lateInstance = instance;
        }
        Plugin.Log($"session-request-late held client={client} (SE-1444)");
        return false;
    }

    // Game thread: the held client's CLIENT_CONNECTED has run, queue its SessionRequest now.
    private static void ClientConnectedPostfix(MyPacket packet)
    {
        ulong client = packet.Sender.Id.Value;
        object instance;
        lock (m_lateSync)
        {
            if (client == 0 || client != m_lateClient || m_lateInstance == null)
                return;
            instance = m_lateInstance;
            m_lateClient = 0;
            m_lateInstance = null;
        }
        Plugin.Log($"session-request-late released after CLIENT_CONNECTED client={client} (SE-1444)");
        m_releasing = true;
        try
        {
            m_raiseSessionRequest.Invoke(null, new[] { instance, (object)client });
        }
        finally
        {
            m_releasing = false;
        }
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
