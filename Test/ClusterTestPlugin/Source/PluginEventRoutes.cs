using System;
using System.Collections.Generic;
using System.Linq;
using PluginSdk.Clustering;

namespace ClusterTestPlugin;

/// <summary>T-0256: PluginSdk cluster events and the node roster as a plugin on a node or the WA sees them.</summary>
internal static class PluginEventRoutes
{
    private static readonly object Sync = new();
    private static readonly List<object> Events = new();
    private static bool subscribed;

    public static void Register()
    {
        // GET /plugin-nodes - NodesAsync only, no subscription: {node, nodes:[{node, incarnation, role}] | null}.
        TestRoutes.Add("GET", "/plugin-nodes", context =>
        {
            var nodes = PluginCluster.ForPlugin(PluginStorageRoutes.OwnId()).NodesAsync().GetAwaiter().GetResult();
            TestRoutes.Json(context, 200, new
            {
                node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                nodes = nodes?.Select(n => new { node = n.Node, incarnation = n.Incarnation, role = n.Role }).ToArray(),
            });
        });
        // GET /plugin-events - subscribe (idempotent) and list: {node, events:[{kind, node, incarnation, role, partition,
        // generation, gameThread}], nodes:[{node, incarnation, role}] | null}.
        TestRoutes.Add("GET", "/plugin-events", context =>
        {
            PluginClusterClient client = PluginCluster.ForPlugin(PluginStorageRoutes.OwnId());
            lock (Sync)
                if (!subscribed)
                {
                    client.ClusterEvent += change =>
                    {
                        lock (Sync)
                        {
                            if (Events.Count >= 512) Events.RemoveAt(0);
                            Events.Add(new { kind = change.Kind.ToString(), node = change.Node, incarnation = change.Incarnation,
                                role = change.Role, partition = change.Partition.ToString(), generation = change.Generation,
                                gameThread = Sandbox.MySandboxGame.Static?.UpdateThread == System.Threading.Thread.CurrentThread });
                        }
                    };
                    subscribed = true;
                }
            var nodes = client.NodesAsync().GetAwaiter().GetResult();
            object[] events;
            lock (Sync) events = Events.ToArray();
            TestRoutes.Json(context, 200, new
            {
                node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                events,
                nodes = nodes?.Select(n => new { node = n.Node, incarnation = n.Incarnation, role = n.Role }).ToArray(),
            });
        });
    }
}
