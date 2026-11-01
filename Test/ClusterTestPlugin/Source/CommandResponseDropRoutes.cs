using System;
using System.Threading;
using HarmonyLib;

namespace ClusterTestPlugin;

/// <summary>
/// #1271 bench route: lose the World Authority's next answers to this node's commands of one global-state
/// subsystem, the way a lost frame on the way back loses them. A Harmony prefix on the cluster plugin's
/// <c>GlobalStateChannel.ReceiveAtReplica</c>, installed on first arm, on this test plugin only; inert while nothing
/// is armed. The command still runs at the World Authority; only its Response never reaches the waiting sender.
/// </summary>
internal static class CommandResponseDropRoutes
{
    private static string m_subsystem;
    private static long m_armed;
    private static long m_dropped;
    private static bool m_patched;

    public static void Register()
    {
        // POST /test-drop-command-responses?subsystem=&count= - arm: drop the next `count` Responses of `subsystem`.
        TestRoutes.Add("POST", "/test-drop-command-responses", context =>
        {
            string subsystem = TestRoutes.Query(context, "subsystem");
            long count = TestRoutes.Long(context, "count");
            lock (typeof(CommandResponseDropRoutes))
            {
                EnsurePatched();
                m_subsystem = subsystem;
                Interlocked.Exchange(ref m_armed, count);
                Interlocked.Exchange(ref m_dropped, 0);
            }
            Plugin.Log($"command-response-drop armed subsystem={subsystem} count={count} (#1271)");
            TestRoutes.Json(context, 200, new { armed = count, subsystem });
        });
        // GET /test-drop-command-responses - how many Responses were dropped since the last arm, and how many are left.
        TestRoutes.Add("GET", "/test-drop-command-responses", context =>
            TestRoutes.Json(context, 200, new { subsystem = m_subsystem, armed = Interlocked.Read(ref m_armed), dropped = Interlocked.Read(ref m_dropped) }));
    }

    private static void EnsurePatched()
    {
        if (m_patched) return;
        var channel = AccessTools.TypeByName("ClusterPlugin.GlobalStateChannel") ?? throw new TypeLoadException("ClusterPlugin.GlobalStateChannel");
        var original = AccessTools.Method(channel, "ReceiveAtReplica") ?? throw new MissingMethodException("GlobalStateChannel.ReceiveAtReplica");
        new Harmony("ClusterTestPlugin.CommandResponseDrop").Patch(original,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(CommandResponseDropRoutes), nameof(ReceiveAtReplicaPrefix))));
        m_patched = true;
    }

    private static bool ReceiveAtReplicaPrefix(object envelope)
    {
        if (Interlocked.Read(ref m_armed) <= 0) return true;
        var fields = Traverse.Create(envelope);
        if (fields.Property("Kind").GetValue<string>() != "Response" || fields.Property("Subsystem").GetValue<string>() != m_subsystem) return true;
        if (Interlocked.Decrement(ref m_armed) < 0)
        {
            Interlocked.Increment(ref m_armed);
            return true;
        }
        Interlocked.Increment(ref m_dropped);
        Plugin.Log($"command-response-drop dropped subsystem={m_subsystem} sequence={fields.Property("Sequence").GetValue()} (#1271)");
        return false;
    }
}
