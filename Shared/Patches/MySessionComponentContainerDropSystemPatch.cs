using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.SessionComponents;

namespace Shared.Patches;

/// <summary>
/// MySessionComponentContainerDropSystemPatch.UpdateContainerSpawnerPrefix:
/// regular nodes must not advance per-player drop timers or create container drops on their own; the seam
/// keeps that decision on the World Authority. The target is a private instance method, hence the string
/// name. Inert while the seam is null. The ExecuteDrop call-in is
/// <see cref="MySessionComponentContainerDropSystemSeams.ClusterSpawnContainerDrop"/> (no patch needed).
/// </summary>
[HarmonyPatch(typeof(MySessionComponentContainerDropSystem), "UpdateContainerSpawner")]
public static class MySessionComponentContainerDropSystemPatch
{
    [HarmonyPrefix]
    private static bool UpdateContainerSpawnerPrefix()
    {
        var prefix = MySessionComponentContainerDropSystemSeams.ClusterSpawnerPrefix;
        return prefix == null || prefix();
    }
}
