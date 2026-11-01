using System;
using System.Diagnostics;
using PluginSdk.Clustering;

namespace ClusterTestPlugin;

/// <summary>T-0256: PluginSdk's cluster clock as a plugin on a node or the WA reads it.</summary>
internal static class PluginClockRoutes
{
    public static void Register()
    {
        // GET /plugin-clock - {node, gameTimeMs, clockMs, synchronized, authoritative, uptimeMs}; uptimeMs is the
        // host's Stopwatch in the same game-thread read, so (clockMs - uptimeMs) compares across processes.
        TestRoutes.Add("GET", "/plugin-clock", context =>
        {
            string id = PluginStorageRoutes.OwnId();
            PluginClusterClient client = PluginCluster.ForPlugin(id);
            var sample = TestRoutes.OnGameThread(() =>
            {
                PluginClusterTime time = client.Time();
                return new
                {
                    node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                    available = time != null,
                    gameTimeMs = time?.GameTime.TotalMilliseconds,
                    clockMs = time?.ClockMilliseconds,
                    synchronized = time?.Synchronized,
                    authoritative = time?.Authoritative,
                    uptimeMs = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency,
                };
            });
            TestRoutes.Json(context, 200, sample);
        });
    }
}
