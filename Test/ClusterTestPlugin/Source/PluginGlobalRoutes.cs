using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PluginSdk.Clustering;

namespace ClusterTestPlugin;

/// <summary>T-0256: PluginSdk idempotent global commands as a plugin on a node or the WA uses them.</summary>
internal static class PluginGlobalRoutes
{
    private const string Command = "t0256.count";
    private static readonly object Sync = new();
    private static IDisposable registration;
    private static int runs;
    private static readonly Dictionary<string, string> saves = new();

    public static void Register()
    {
        // GET /plugin-global[?register=1][&run=TEXT&op=GUID] - register the command here (idempotent); run it (it
        // executes on the WA) with operation id op. {node, registered, runs, result:{code, payload, error}}.
        TestRoutes.Add("GET", "/plugin-global", context =>
        {
            PluginClusterClient client = PluginCluster.ForPlugin(PluginStorageRoutes.OwnId());
            string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
            lock (Sync)
                if (context.Request.QueryString["register"] == "1" && registration == null)
                    registration = client.RegisterGlobalCommand(Command, payload =>
                        Encoding.UTF8.GetBytes(node + ":" + Interlocked.Increment(ref runs) + ":" + Encoding.UTF8.GetString(payload ?? Array.Empty<byte>())));
            object result = null;
            string run = context.Request.QueryString["run"];
            if (run != null)
            {
                Guid operation = Guid.Parse(TestRoutes.Query(context, "op"));
                var outcome = client.ExecuteGlobalAsync(Command, Encoding.UTF8.GetBytes(run), operation, TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                result = new { code = outcome.Code.ToString(), payload = outcome.Payload == null ? null : Encoding.UTF8.GetString(outcome.Payload), error = outcome.Error };
            }
            TestRoutes.Json(context, 200, new { node, registered = registration != null, runs = Volatile.Read(ref runs), result });
        });
        // GET /plugin-global-save?op=GUID[&ms=N] - SE-865: with ms, start waiting up to N ms for a recoverable global save
        // that holds all the World Authority did so far, as a plugin asks for it. {node, code}: null while it waits. Never
        // waits here: this thread also serves the WA's global saves to the nodes, so waiting would hold the save back.
        TestRoutes.Add("GET", "/plugin-global-save", context =>
        {
            string operation = TestRoutes.Query(context, "op");
            if (context.Request.QueryString["ms"] != null)
            {
                TimeSpan timeout = TimeSpan.FromMilliseconds(TestRoutes.Long(context, "ms"));
                lock (Sync) saves[operation] = null;
                _ = Task.Run(async () =>
                {
                    var outcome = await PluginCluster.ForPlugin(PluginStorageRoutes.OwnId())
                        .ExecuteGlobalAsync("cluster.global-save", null, Guid.Parse(operation), timeout).ConfigureAwait(false);
                    lock (Sync) saves[operation] = outcome.Code.ToString();
                });
            }
            string code;
            lock (Sync) saves.TryGetValue(operation, out code);
            TestRoutes.Json(context, 200, new { node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"), code });
        });
    }
}
