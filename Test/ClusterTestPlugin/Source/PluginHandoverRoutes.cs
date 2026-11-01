using System;
using System.Collections.Generic;
using System.Text;
using PluginSdk.Clustering;

namespace ClusterTestPlugin;

/// <summary>T-0256: PluginSdk grid handover hooks as a plugin on a node sees them.</summary>
internal static class PluginHandoverRoutes
{
    private static readonly object Sync = new();
    private static readonly List<object> Events = new();
    private static readonly Dictionary<long, string> States = new();
    private static IDisposable registration;

    private sealed class Hooks : IPluginGridHandover
    {
        private static void Record(string hook, long grid, byte[] state = null)
        {
            lock (Sync)
            {
                if (Events.Count >= 512) Events.RemoveAt(0);
                Events.Add(new { hook, grid, state = state == null ? null : Encoding.UTF8.GetString(state),
                    gameThread = Sandbox.MySandboxGame.Static?.UpdateThread == System.Threading.Thread.CurrentThread });
            }
        }
        public byte[] Leaving(long gridId)
        {
            string state;
            lock (Sync) States.TryGetValue(gridId, out state);
            byte[] bytes = state == null ? null : Encoding.UTF8.GetBytes(state);
            Record("Leaving", gridId, bytes);
            return bytes;
        }
        public void Left(long gridId) => Record("Left", gridId);
        public void Stayed(long gridId) => Record("Stayed", gridId);
        public void Arrived(long gridId, byte[] state) => Record("Arrived", gridId, state);
    }

    public static void Register()
    {
        // GET /plugin-handover[?grid=ID&state=TEXT] - register the hooks (idempotent), optionally set the state
        // Leaving returns for a grid, and list the hook calls seen here: {node, registered, events:[{hook, grid, state, gameThread}]}.
        TestRoutes.Add("GET", "/plugin-handover", context =>
        {
            string id = PluginStorageRoutes.OwnId();
            string error = null;
            lock (Sync)
            {
                if (registration == null)
                    try { registration = PluginCluster.ForPlugin(id).RegisterGridHandover(new Hooks()); }
                    catch (Exception failure) { error = failure.GetType().Name + ": " + failure.Message; }
                if (long.TryParse(context.Request.QueryString["grid"], out long grid)) States[grid] = context.Request.QueryString["state"];
            }
            object[] events;
            lock (Sync) events = Events.ToArray();
            TestRoutes.Json(context, 200, new
            {
                node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                pluginId = id,
                registered = registration != null,
                error,
                events,
            });
        });
    }
}
