using ClusterPlugin;
using HarmonyLib;
using VRage.Scripting;

namespace Shared.Patches;

/// <summary>
/// ScriptCompilerPatch policy prefix: clearing the whitelist
/// invalidates the compiled-script cache first. Inert while the seam delegate is null.
/// </summary>
[HarmonyPatch(typeof(MyVRageScriptingInternal), nameof(MyVRageScriptingInternal.ClearWhitelist))]
public static class MyVRageScriptingInternalPatch
{
    [HarmonyPrefix]
    private static void ClearWhitelistPrefix()
    {
        MyScriptCompilerSeams.ClusterWhitelistChanged?.Invoke();
    }
}
