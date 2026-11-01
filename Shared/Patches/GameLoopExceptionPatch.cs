using System;
using HarmonyLib;
using Sandbox;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// #1287: an exception out of a game-thread Invoke unwinds MySandboxGame.Update and ends the dedicated server
/// through its clean shutdown, so the game log showed "Network readers disposed ... Done" with no reason; the
/// stack reached only the launcher's stdout, which catches it and exits (no AppDomain.UnhandledException).
/// The finalizer writes the reason into the game log while it is still open, and rethrows unchanged.
/// </summary>
[HarmonyPatch(typeof(MySandboxGame), "Update", new Type[0])]
public static class GameLoopExceptionPatch
{
	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		if (__exception != null)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] game loop exception, the server is stopping: "
				+ ClusterPlugin.SecretRedactor.Redact(__exception.ToString()));
			MyLog.Default?.Flush();
		}
		return __exception;
	}
}
