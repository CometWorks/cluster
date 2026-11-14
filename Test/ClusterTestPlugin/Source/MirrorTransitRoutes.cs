using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClusterPlugin;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using VRage.GameServices;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1476 bench route: make the player mirror lose a member on a client after that client's handover export and
/// before its release, the window that took a sender's name off LJS's chat live (2026-11-14 00:14Z). Live it takes a
/// logout a few seconds before a handover and a grace that runs out mid-transfer; here it happens on every arm.
///
/// POST /test-mirror-transit?client=&amp;mode=pending|after|leave&amp;subject=
///   pending - the mirror stops passing once it has the subject's withdrawal pending (the subject logged out), and
///             the export of `client` lets it withdraw on the next frame: the live sequence.
///   after   - the mirror stops passing now, so the subject's absence is not noticed before the export; the export
///             lets it withdraw on the next frame with the grace already over (as a phantom of this node, which it
///             is not when the client arrived already holding it).
///   leave   - on the frame after the export, vanilla's disconnect runs here for the subject (a player that leaves
///             this node for real, like a respawn switch away, while `client` is in transit).
/// GET /test-mirror-transit - the state and what happened.
/// Harmony postfixes on HeldBy and Update, on this test plugin only; inert while nothing is armed.
/// </summary>
internal static class MirrorTransitRoutes
{
    private static readonly Type Mirror = typeof(RemotePlayerMirrorService);
    private static readonly FieldInfo Next = Mirror.GetField("m_next", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo PendingField = Mirror.GetField("Pending", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo InstalledField = Mirror.GetField("Installed", BindingFlags.NonPublic | BindingFlags.Static);

    private static bool m_patched;
    private static ulong m_client, m_subject;
    private static string m_mode;
    private static bool m_frozen;
    private static readonly List<string> m_events = new();

    public static void Register()
    {
        TestRoutes.Add("POST", "/test-mirror-transit", context =>
        {
            ulong client = (ulong)TestRoutes.Long(context, "client");
            ulong subject = (ulong)TestRoutes.Long(context, "subject");
            string mode = TestRoutes.Query(context, "mode");
            if (mode is not ("pending" or "after" or "leave")) throw new ArgumentException("mode");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                EnsurePatched();
                (m_client, m_subject, m_mode, m_frozen) = (client, subject, mode, false);
                m_events.Clear();
                if (mode == "after") Freeze("armed");
                Note($"armed client={client} mode={mode} subject={subject}");
                return (object)new { armed = true, client, mode, subject };
            }));
        });
        TestRoutes.Add("GET", "/test-mirror-transit", context =>
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
                (object)new { armed = m_client != 0, client = m_client, mode = m_mode, frozen = m_frozen, events = m_events.ToArray() })));
    }

    private static void EnsurePatched()
    {
        if (m_patched) return;
        var harmony = new Harmony("ClusterTestPlugin.MirrorTransit");
        harmony.Patch(AccessTools.Method(Mirror, nameof(RemotePlayerMirrorService.Update)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MirrorTransitRoutes), nameof(UpdatePostfix))));
        harmony.Patch(AccessTools.Method(Mirror, "HeldBy"),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MirrorTransitRoutes), nameof(HeldByPostfix))));
        m_patched = true;
    }

    private static void Note(string text)
    {
        m_events.Add(text);
        Plugin.Log("mirror-transit " + text + " (SE-1476)");
    }

    private static void Freeze(string why)
    {
        Next.SetValue(null, double.MaxValue);
        m_frozen = true;
        Note("mirror frozen: " + why);
    }

    private static void UpdatePostfix()
    {
        if (m_client == 0 || m_frozen || m_mode != "pending") return;
        var pending = (IDictionary)PendingField.GetValue(null);
        foreach (object key in pending.Keys)
            if (((ValueTuple<ulong, int>)key).Item1 == m_subject)
            {
                Freeze("subject pending");
                return;
            }
    }

    private static void HeldByPostfix(ulong client, List<MirrorHolding> __result)
    {
        if (m_client == 0 || client != m_client) return;
        string mode = m_mode;
        ulong subject = m_subject;
        m_client = 0;
        bool vouched = __result?.Any(h => h.SteamId == subject && h.Member) == true;
        Note($"export client={client} vouchedSubject={vouched}");
        MySandboxGame.Static.Invoke(() =>
        {
            if (mode == "leave")
            {
                AccessTools.Method(typeof(MyDedicatedServerBase), "MyDedicatedServer_ClientLeft")
                    .Invoke(MyMultiplayer.Static, new object[] { subject, MyChatMemberStateChangeEnum.Disconnected });
                Note($"vanilla leave of {subject} after the export");
                return;
            }
            // Expire every absent phantom now, so the next pass withdraws it while the client is still attached.
            var pending = (IDictionary)PendingField.GetValue(null);
            var installed = (IDictionary)InstalledField.GetValue(null);
            var online = new HashSet<(ulong, int)>(OnlinePlayersService.Cluster.Select(e => (e.SteamId, e.SerialId)));
            // A client that arrived holding the subject got no phantom from this node, which then has nothing to
            // withdraw. Live the source had installed it for its other clients; stand in for them.
            if (mode == "after" && !installed.Contains((subject, 0))) installed[(subject, 0)] = string.Empty;
            foreach (object key in installed.Keys.Cast<object>().ToArray())
                if (!online.Contains((ValueTuple<ulong, int>)key))
                    pending[key] = double.MinValue / 2;
            m_frozen = false;
            Next.SetValue(null, 0.0);
            Note($"mirror released after the export, absent phantoms expired ({pending.Count} pending)");
        }, "ClusterTestPlugin mirror transit");
    }
}
