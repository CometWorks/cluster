using System;
using Sandbox;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using VRage.Game;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MySandboxGame"/>. Shared/Patches/MySandboxGamePatch.cs transpiles the premade
/// checkpoint load in <c>InitQuickLaunch</c> so the dedicated config's session settings only replace the
/// world's when the seam says so.
/// </summary>
public static class MySandboxGameSeams
{
	// ClusterPlugin seam: when true, a premade world's session settings survive the dedicated config.
	public static Func<bool> ClusterKeepPremadeSessionSettings;

	// ClusterPlugin seam: the folder under Saves a premade world loads into, whatever the world is called.
	public static Func<string> ClusterSaveFolder;

	private static string _premadeWorldName;

	/// <summary>
	/// Transpiler target standing in for <c>checkpoint.Settings = sessionSettings</c>. A premade cluster
	/// world carries the cluster-wide game rules. Every node auto-generates its own default dedicated
	/// config on first boot, and letting that config replace the world's settings makes each node
	/// re-decide rules like block limits for itself - silently, and differently from what the world was
	/// built with.
	/// </summary>
	public static void ApplyPremadeSessionSettings(MyObjectBuilder_Checkpoint checkpoint, MyObjectBuilder_SessionSettings sessionSettings)
	{
		if (ClusterKeepPremadeSessionSettings == null || !ClusterKeepPremadeSessionSettings())
		{
			checkpoint.Settings = sessionSettings;
		}
	}

	/// <summary>
	/// Transpiler target standing in for <c>checkpoint.SessionName = worldName</c>. MySession.Load takes the
	/// mods' world storage path from it and InitQuickLaunch the path of the first save. Quasar seeds a managed
	/// node's mod configs into Saves/&lt;world id&gt; and renames must not lose them (SE-788).
	/// </summary>
	public static void ApplyPremadeSessionName(MyObjectBuilder_Checkpoint checkpoint, string worldName)
	{
		string folder = ClusterSaveFolder?.Invoke();
		checkpoint.SessionName = string.IsNullOrEmpty(folder) ? worldName : folder;
		_premadeWorldName = string.IsNullOrEmpty(folder) ? null : worldName;
	}

	/// <summary>After MySession.Load: the session goes back to the name players see, before the server starts.</summary>
	public static void RestorePremadeWorldName()
	{
		if (_premadeWorldName == null) return;
		MySession.Static.Name = MyStatControlText.SubstituteTexts(_premadeWorldName);
		_premadeWorldName = null;
	}
}
