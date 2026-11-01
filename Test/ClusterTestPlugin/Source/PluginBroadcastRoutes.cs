using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PluginSdk.Clustering;

namespace ClusterTestPlugin;

/// <summary>T-0256: PluginSdk broadcast as a plugin on a node or the WA uses it.</summary>
internal static class PluginBroadcastRoutes
{
    private const string Topic = "t0256.broadcast";
    private static readonly object Sync = new();
    private static readonly List<object> Received = new();
    private static IDisposable subscription;

    public static void Register()
    {
        // GET /plugin-broadcast - subscribe this process (idempotent) and list what it received:
        //   {node, pluginId, subscribed, received:[{operation, source, incarnation, text, gameThread}]}
        // GET /plugin-broadcast?send=TEXT[&wa=0][&operation=GUID] - broadcast TEXT from here; per-node codes and replies.
        TestRoutes.Add("GET", "/plugin-broadcast", context =>
        {
            string id = PluginStorageRoutes.OwnId();
            PluginClusterClient client;
            try { client = PluginCluster.ForPlugin(id); }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                TestRoutes.Json(context, 200, new { error = error.GetType().Name, message = error.Message, pluginId = id });
                return;
            }
            string subscribeError = Subscribe(client);
            string send = context.Request.QueryString["send"];
            object broadcast = null;
            if (send != null)
            {
                Guid operation = Guid.TryParse(context.Request.QueryString["operation"], out var given) ? given : Guid.NewGuid();
                bool wa = context.Request.QueryString["wa"] != "0";
                var result = client.BroadcastAsync(Topic, Encoding.UTF8.GetBytes(send), operation, TimeSpan.FromSeconds(10), wa)
                    .GetAwaiter().GetResult();
                broadcast = new
                {
                    operation,
                    code = result.Code.ToString(),
                    nodes = result.Nodes.ToDictionary(n => n.Key, n => new
                    {
                        code = n.Value.Code.ToString(),
                        reply = n.Value.Payload == null ? null : Encoding.UTF8.GetString(n.Value.Payload),
                    }),
                };
            }
            object[] received;
            lock (Sync) received = Received.ToArray();
            TestRoutes.Json(context, 200, new
            {
                node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                pluginId = id,
                subscribed = subscribeError == null,
                subscribeError,
                broadcast,
                received,
            });
        });
        // GET /plugin-broadcast-burst?count=N - N broadcasts at once from here: how many answers of each code, per node (SE-852).
        TestRoutes.Add("GET", "/plugin-broadcast-burst", context =>
        {
            PluginClusterClient client = PluginCluster.ForPlugin(PluginStorageRoutes.OwnId());
            int count = int.TryParse(context.Request.QueryString["count"], out var given) ? given : 24;
            var results = Task.WhenAll(Enumerable.Range(0, count).Select(i =>
                client.BroadcastAsync(Topic, Encoding.UTF8.GetBytes("burst-" + i), Guid.NewGuid(), TimeSpan.FromSeconds(10), true))).GetAwaiter().GetResult();
            TestRoutes.Json(context, 200, new
            {
                nodes = results.SelectMany(r => r.Nodes).GroupBy(n => n.Key).ToDictionary(n => n.Key,
                    n => n.GroupBy(a => a.Value.Code.ToString()).ToDictionary(a => a.Key, a => a.Count())),
            });
        });
        // GET /game-thread-stall?ms=N - one game-thread frame takes N ms, the way a save or a big import does (SE-852).
        TestRoutes.Add("GET", "/game-thread-stall", context =>
        {
            int ms = int.TryParse(context.Request.QueryString["ms"], out var given) ? given : 600;
            Sandbox.MySandboxGame.Static.Invoke(() => System.Threading.Thread.Sleep(ms), "test stall");
            TestRoutes.Json(context, 200, new { stalledMs = ms });
        });
    }

    private static string Subscribe(PluginClusterClient client)
    {
        lock (Sync)
        {
            if (subscription != null) return null;
            try
            {
                subscription = client.RegisterHandler(Topic, message =>
                {
                    string text = message.Payload == null ? null : Encoding.UTF8.GetString(message.Payload);
                    lock (Sync)
                    {
                        if (Received.Count >= 256) Received.RemoveAt(0);
                        Received.Add(new { operation = message.OperationId, source = message.Source, incarnation = message.Incarnation, text,
                            gameThread = Sandbox.MySandboxGame.Static?.UpdateThread == System.Threading.Thread.CurrentThread });
                    }
                    return Task.FromResult(Encoding.UTF8.GetBytes((Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? "?") + ":" + text));
                });
                return null;
            }
            catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
        }
    }
}
