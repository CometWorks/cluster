using ClusterPlugin;
using HarmonyLib;
using VRage.Game.Components.Session;
using VRage.Game.Entity;

namespace Shared.Patches;

/// <summary>
/// Crash guard on <see cref="MyEntityComponentUpdater.AddEntityComponents"/>.
/// An entity created against a half-torn-down state (a departing client's weapon-switch event racing its
/// character's removal) can reach here without a component container; vanilla dereferences it. No seam
/// delegate, so gated on <see cref="ClusterRuntime.IsClustered"/> like the other
/// frozen-body crash guards.
/// </summary>
[HarmonyPatch(typeof(MyEntityComponentUpdater), nameof(MyEntityComponentUpdater.AddEntityComponents))]
public static class MyEntityComponentUpdaterPatch
{
    [HarmonyPrefix]
    private static bool AddEntityComponentsPrefix(MyEntity entity)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        return entity?.Components != null;
    }
}
