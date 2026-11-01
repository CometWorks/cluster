using System;
using System.Collections.Generic;
using PluginSdk.Clustering;

namespace ClusterTestPlugin;

/// <summary>SE-903: PluginSdk offline blocker as a plugin on a node uses it.</summary>
internal static class PluginOfflineRoutes
{
    private static readonly object Sync = new();
    private static readonly HashSet<long> Blocked = new();
    private static readonly HashSet<long> Throwing = new();
    private static IDisposable registration;
    private static long asked;
    private static bool offThread;

    private static string Blocker(long entity)
    {
        if (Sandbox.MySandboxGame.Static?.UpdateThread != System.Threading.Thread.CurrentThread) offThread = true;
        asked++;
        lock (Sync)
        {
            if (Throwing.Contains(entity)) throw new InvalidOperationException("test blocker throws");
            return Blocked.Contains(entity) ? "rig" : null;
        }
    }

    public static void Register()
    {
        // GET /plugin-offline[?register=1][&unregister=1][&block=ID][&unblock=ID][&throw=ID][&nothrow=ID] -
        // register or dispose the blocker and set its answers: {node, registered, supported, second, asked, offThread}.
        // "second" is what registering another while one is live does. Nothing here survives the process.
        TestRoutes.Add("GET", "/plugin-offline", context =>
        {
            var query = context.Request.QueryString;
            string error = null, second = null;
            bool supported = true;
            lock (Sync)
            {
                if (query["unregister"] != null)
                {
                    registration?.Dispose();
                    registration = null;
                }
                if (query["register"] != null && registration == null)
                {
                    var client = PluginCluster.ForPlugin(PluginStorageRoutes.OwnId());
                    try
                    {
                        registration = client.RegisterOfflineBlocker(Blocker);
                        supported = registration != null;
                        if (supported)
                            try { client.RegisterOfflineBlocker(Blocker)?.Dispose(); second = "accepted"; }
                            catch (InvalidOperationException) { second = "refused"; }
                    }
                    catch (Exception failure) { error = failure.GetType().Name + ": " + failure.Message; }
                }
                if (long.TryParse(query["block"], out long block)) Blocked.Add(block);
                if (long.TryParse(query["unblock"], out long unblock)) Blocked.Remove(unblock);
                if (long.TryParse(query["throw"], out long thrower)) Throwing.Add(thrower);
                if (long.TryParse(query["nothrow"], out long calm)) Throwing.Remove(calm);
                TestRoutes.Json(context, 200, new
                {
                    node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                    registered = registration != null,
                    supported,
                    second,
                    error,
                    asked,
                    offThread,
                });
            }
        });
    }
}
