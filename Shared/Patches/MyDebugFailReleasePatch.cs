using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using VRage;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// T-0128. On Linux a <see cref="MyDebug.FailRelease"/> assertion KILLS THE NODE, whatever it was
/// asserting about.
///
/// THE MECHANISM. MyDebug.FailRelease walks MyDebug.Listeners and calls Fail on each. The
/// release-build listener is VRage.Platform.Windows.Forms.MyDebugListenerProvider.MyTraceListener,
/// registered by MyVRagePlatform, and its Fail() ends in TraceMessage(..., showMessageBox: true).
/// That assembly cannot load on Linux, so the assertion becomes an unhandled
/// FileNotFoundException for System.Windows.Forms on the GAME THREAD and the process exits 1.
///
/// Observed 2026-09-12 02:10 on the w29 stop: MyGridWeaponSystem.Unregister asserted while the
/// cluster plugin was tearing down a grid hierarchy in PartitionPersistence.UpdateDrain. node-1
/// died mid-drain, the registry then waited its full 600 s graceful budget on a node that was
/// already dead, and the gateway had to be SIGKILLed with a dump. A stop that takes ~1 min took
/// 10 and ended dirty. findings/failrelease-on-linux-kills-the-node-via-windows-forms.md
///
/// THE POINT IS THE GENERAL CASE, NOT THE WEAPON ASSERT. Every FailRelease in the codebase is a
/// node kill on Linux; the weapon system is only the one that fired first. Fixing that call site
/// moves the next kill to whichever assertion fires next. This is the provider swap instead.
///
/// WHY A PREFIX RATHER THAN EDITING MyDebug.Listeners. Removing the Windows listener from the
/// list at plugin init would be more surgical, but it depends on running AFTER
/// MyDebugListenerProvider.Register() and on nothing re-adding a listener later. A prefix has no
/// ordering dependency and covers listeners that do not exist yet.
///
/// BEHAVIOUR: on Windows this does nothing at all - the original runs and the message box works
/// as Keen intended. On every other platform the assertion is written to the log with its file,
/// line and stack, and execution CONTINUES. Continuing is the correct target because continuing
/// is what Windows already does: a message box is not an abort. This makes Linux match Windows;
/// it does not invent a new policy.
/// </summary>
[HarmonyPatch(typeof(MyDebug))]
public static class MyDebugFailReleasePatch
{
	private static readonly bool OnWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

	/// <summary>
	/// Assertions can fire in a tight loop - the drain path that found this walks every block of
	/// every grid. An unbounded log write per assertion would turn a node kill into a disk fill,
	/// which is not an improvement. After this many, only every 1000th is written.
	/// </summary>
	private const int FullyLoggedCount = 50;
	private static long m_count;

	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyDebug.FailRelease))]
	private static bool FailReleasePrefix(string message, string file, int line)
	{
		if (OnWindows)
			return true;

		long seen = System.Threading.Interlocked.Increment(ref m_count);
		if (seen <= FullyLoggedCount || seen % 1000 == 0)
		{
			// WriteLineAndConsole, not WriteLine: this was a process-killing event until now and
			// it must not be quieter than the crash it replaces. The stack is what makes the
			// entry actionable - the assert message alone does not say which subsystem asserted.
			MyLog.Default?.WriteLineAndConsole(
				$"[ClusterPlugin] T-0128 FailRelease suppressed (#{seen}): {message}" +
				$"{(file != null ? $" at {file}:{line}" : string.Empty)}\n{new StackTrace(2, true)}");
		}
		return false;
	}
}
