using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ClusterPlugin;
using HarmonyLib;
using PluginSdk.Clustering;

namespace ClusterTestPlugin;

// Read-only snapshots at real host transitions. This observer never changes host
// state, supplies a provider, or invokes Concealment lifecycle methods.
internal static class AccountingInterlockObserver
{
    private static readonly ConcurrentQueue<JsonElement> Events = new();
    private static bool draining;
    private static bool? connected;
    internal static void Register()
    {
        var harmony = new Harmony("ClusterTestPlugin.accounting-interlock-observer");
        harmony.Patch(AccessTools.Method(typeof(ClusterRuntime), "SetNodeState"),
            postfix: new HarmonyMethod(typeof(AccountingInterlockObserver), nameof(StateChanged)));
        harmony.Patch(AccessTools.Method(typeof(PartitionPersistence), "Update"),
            // Update may terminate the process after the drain's last heartbeat;
            // a postfix cannot observe that final closed state.
            prefix: new HarmonyMethod(typeof(AccountingInterlockObserver), nameof(Updated)));
        TestRoutes.Add("GET", "/accounting-interlock", context => TestRoutes.Json(context, 200, Events.ToArray()));
    }
    private static void StateChanged(string __0) => Record(__0);
    private static void Updated(PartitionPersistence __instance)
    {
        if (!draining && (bool)AccessTools.Field(typeof(PartitionPersistence), "m_closed").GetValue(__instance))
        { draining = true; Record("Draining"); }
        bool current = ClusterNodeLink.Current?.IsConnected == true;
        if (connected != current) { connected = current; Record(current ? "LinkConnected" : "LinkDisconnected"); }
    }
    private static void Record(string transition)
    {
        try
        {
            var plugin = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ServerPlugin.Plugin", false))
                .SingleOrDefault(t => t?.GetField("Name")?.GetRawConstantValue() as string == "Concealment");
            object instance = plugin?.GetProperty("Instance").GetValue(null);
            var entry = JsonSerializer.SerializeToElement(new {
                transition, state = ClusterRuntime.NodeState,
                configured = plugin?.GetProperty("ClusterMode").GetValue(null),
                loaded = instance != null,
                manager = instance == null ? null : plugin.GetProperty("Manager").GetValue(instance),
                tick = instance == null ? null : plugin.GetProperty("Tick").GetValue(instance),
                connected = ClusterNodeLink.Current?.IsConnected,
                patches = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m).Owners
                    .Any(o => o == "Concealment" || o.StartsWith("Concealment."))).Select(m => m.Name).ToArray(),
            });
            Events.Enqueue(entry);
            while (Events.Count > 128) Events.TryDequeue(out _);
            Console.WriteLine("[AccountingInterlockObserver] " + entry.GetRawText());
        }
        catch (Exception e) { Console.WriteLine("[AccountingInterlockObserver] observation failed: " + e.GetType().Name); }
    }
}
