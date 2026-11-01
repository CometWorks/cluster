using System;
using System.Reflection;
using ClusterPlugin;
using Sandbox.Engine.Multiplayer;

namespace ClusterTestPlugin;

/// <summary>
/// SE-732: run the remote-player mirror's reconcile pass inside the server's ClientJoined, so a fresh
/// client is seen before it can ask for the world. A member message sent then reaches the client
/// before the FLUSH that answers its world request, and the FLUSH drops it. Live, a pass falls into
/// that window for a few logins in a hundred (one pass a second, the world request 30-50 ms after the
/// join); with this harness on, it does for every login.
/// POST /test-early-mirror-pass?on=1|0
/// </summary>
internal static class EarlyMirrorPassHarness
{
    private static readonly FieldInfo Next = typeof(RemotePlayerMirrorService).GetField("m_next", BindingFlags.NonPublic | BindingFlags.Static);
    private static bool m_on;
    private static MyMultiplayerBase m_hooked;
    private static int m_forced;

    public static void Register()
    {
        TestRoutes.Add("POST", "/test-early-mirror-pass", context =>
        {
            bool on = TestRoutes.Query(context, "on") != "0";
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                m_on = on;
                MyMultiplayerBase multiplayer = MyMultiplayer.Static;
                if (on && multiplayer != null && !ReferenceEquals(m_hooked, multiplayer))
                {
                    // Subscribed after vanilla's handlers, so the client's state exists when this runs.
                    // Through reflection: the publicized assembly makes the event and its backing field ambiguous.
                    typeof(MyMultiplayerBase).GetEvent("ClientJoined")
                        .AddEventHandler(multiplayer, new Action<ulong, string>(OnClientJoined));
                    m_hooked = multiplayer;
                }
                return (object)new { on = m_on, forced = m_forced };
            }));
        });
    }

    private static void OnClientJoined(ulong client, string name)
    {
        if (!m_on) return;
        Next.SetValue(null, 0.0);
        RemotePlayerMirrorService.Update();
        m_forced++;
        Plugin.Log($"early mirror pass at the join of {client} (SE-732)");
    }
}
