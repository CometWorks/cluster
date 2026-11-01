using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.WorldEnvironment;

namespace Shared.Patches;

/// <summary>
/// An admin's time-of-day change moves
/// MySession.ElapsedGameTime, which in a cluster belongs to the World Authority. The seam takes the change
/// over (forwarding it or applying a pure delta on the WA) and the vanilla rebase body never runs on any
/// cluster role. The target is a private static event handler, hence the string name. Inert while the seam
/// is null. Nothing here ever calls the original from inside the prefix, so the old re-entrancy hazard the
/// ledger warns about cannot come back.
/// </summary>
[HarmonyPatch(typeof(MyTimeOfDayHelper), "UpdateTimeOfDayServer")]
public static class MyTimeOfDayHelperPatch
{
    [HarmonyPrefix]
    private static bool Route(float time)
    {
        var route = MyTimeOfDayHelperSeams.ClusterRouteTimeOfDay;
        return route == null || !route(time);
    }
}
