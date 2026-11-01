using System;
using System.Collections.Generic;
using System.Reflection;
using ClusterPlugin;
using HarmonyLib;
using VRage.Scripting;

namespace Shared.Patches;

/// <summary>
/// ScriptCompilerPatch policy prefixes: every whitelist/blacklist
/// mutation invalidates the compiled-script cache before mutating, so partial failures and in-flight
/// compiles cannot retain stale entries. A prefix runs before the argument/vitality guards, which only adds invalidations on calls that would have thrown -
/// a harmless superset. Inert while MyScriptCompilerSeams.ClusterWhitelistChanged is null.
/// </summary>
[HarmonyPatch]
public static class MyScriptWhitelistPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var blacklist = typeof(MyScriptWhitelist.MyScriptBlacklistBatch);
        var whitelist = typeof(MyScriptWhitelist.MyWhitelistBatch);
        var targets = new (Type Type, string Name)[]
        {
            (blacklist, nameof(MyScriptWhitelist.MyScriptBlacklistBatch.AddNamespaceOfTypes)),
            (blacklist, nameof(MyScriptWhitelist.MyScriptBlacklistBatch.RemoveNamespaceOfTypes)),
            (blacklist, nameof(MyScriptWhitelist.MyScriptBlacklistBatch.AddTypes)),
            (blacklist, nameof(MyScriptWhitelist.MyScriptBlacklistBatch.RemoveTypes)),
            (blacklist, nameof(MyScriptWhitelist.MyScriptBlacklistBatch.AddMembers)),
            (blacklist, nameof(MyScriptWhitelist.MyScriptBlacklistBatch.RemoveMembers)),
            (whitelist, nameof(MyScriptWhitelist.MyWhitelistBatch.AllowNamespaceOfTypes)),
            (whitelist, nameof(MyScriptWhitelist.MyWhitelistBatch.AllowTypes)),
            (whitelist, nameof(MyScriptWhitelist.MyWhitelistBatch.AllowMembers)),
        };
        foreach (var (type, name) in targets)
            yield return AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);
    }

    [HarmonyPrefix]
    private static void Prefix()
    {
        MyScriptCompilerSeams.ClusterWhitelistChanged?.Invoke();
    }
}
