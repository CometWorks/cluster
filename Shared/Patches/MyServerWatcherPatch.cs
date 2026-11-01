using HarmonyLib;
using VRage.Dedicated;

namespace Shared.Patches;

/// <summary>
/// Clustered process liveness belongs to the
/// Gateway registry; the vanilla watcher can terminate a healthy but temporarily slow node without
/// generation fencing or recovery coordination. ClusterPlugin.MyServerWatcherPatch.Prefix disables
/// AutoRestartEnabled and skips the watcher thread only when CLUSTER_GATEWAY_REGISTRY is configured;
/// standalone servers execute the original unchanged.
/// </summary>
[HarmonyPatch(typeof(MyServerWatcher), nameof(MyServerWatcher.Start))]
public static class MyServerWatcherPatch
{
    [HarmonyPrefix]
    private static bool StartPrefix()
    {
        return ClusterPlugin.MyServerWatcherPatch.Prefix();
    }
}
