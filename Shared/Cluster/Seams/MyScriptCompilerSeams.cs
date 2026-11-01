using System;
using System.Collections.Generic;
using System.Reflection;
using VRage.Scripting;

namespace ClusterPlugin;

/// <summary>
/// Seams of VRage.Scripting.MyScriptCompiler. Installed by
/// Shared.Patches.MyScriptCompilerPatch (compile prefix + image-store transpiler),
/// MyScriptWhitelistPatch and MyVRageScriptingInternalPatch (policy-mutation prefixes).
/// </summary>
public static class MyScriptCompilerSeams
{
	// ClusterPlugin patch point: ScriptCompilerPatch.Prefix - asked for a separately loaded cached
	// assembly before vanilla compilation; null means compile as usual.
	public static Func<MyScriptCompiler, MyApiTarget, IEnumerable<Script>, List<Message>, bool, bool, Assembly> ClusterGetCompiled;

	// ClusterPlugin patch point: ScriptCompilerPatch.StoreImage - handed the PE image of a successful
	// compilation immediately before the original Assembly.Load.
	public static Action<MyScriptCompiler, MyApiTarget, IEnumerable<Script>, List<Message>, bool, bool, byte[]> ClusterStoreCompiled;

	// ClusterPlugin patch point: ScriptCompilerPatch.Clear - whitelist/blacklist policy is about to change.
	public static Action ClusterWhitelistChanged;
}
