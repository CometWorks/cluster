using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace ClusterPlugin;

/// <summary>
/// Seams of the cluster plugins themselves, for the test utility plugin (Test/ClusterTestPlugin).
/// </summary>
/// <remarks>
/// Same convention as the game seams next to this file (<see cref="MyCubeBlockSeams"/>): a static
/// field the product reads and nothing in the product writes. Every member here is INERT while it is
/// null - a release deployment carries no test plugin, so the behaviour behind these seams cannot be
/// reached at all (CleanupPlan.md "Done when": the cluster plugins contain no test-only endpoints or
/// test hooks). Keep it that way: a seam is a null check on a path that already exists, never a
/// branch that does test-only work.
/// </remarks>
public static class ClusterSeams
{
	/// <summary>
	/// Partitioning.md §6.10 / P3-HND-03: perturbs the NORMALISED target document of the import
	/// round-trip gate, so the comparison fails deterministically and the abort path can be observed.
	/// Receives the residual list as well, to report a perturbation that could not be applied.
	/// </summary>
	/// <remarks>
	/// Set = the gate's two shortcuts (raw byte equality, and the chunked compare against a prepared
	/// source) are bypassed in ClusterControlServer.CompareImportedState, because neither of them
	/// walks the document this runs on and both would answer "identical" over a perturbed import.
	/// </remarks>
	public static volatile Action<XDocument, List<string>> CorruptImportedDocument;

	/// <summary>
	/// P1-CHR-03: answers true for a client whose controller rebinding must be suppressed, so the
	/// Partitioning.md §6.6 fallback can be proven to convert a would-be stuck character into a forced
	/// reconnect rather than a dead session. Read once per online player per repair sweep (1 Hz).
	/// </summary>
	public static volatile Func<ulong, bool> SuppressControlRebind;

	/// <summary>
	/// #1270: runs on the World Authority's global-save thread after a save is written and before it is registered
	/// (generation, sequence). A bench that blocks here holds the save, and every answer waiting for it, out of the
	/// catalogue for as long as it wants, so a World Authority can be killed inside that window.
	/// </summary>
	public static volatile Action<uint, uint> BeforeGlobalSaveRegister;

	/// <summary>
	/// SE-1124: runs on the control server's request thread before a switch target executes a respawn or a spawn preview
	/// (/cluster/respawn-execute), outside the game thread. A bench can split the medbay's partition here, between the
	/// Gateway's ownership check and the target's, or throw to have the target refuse the switch.
	/// </summary>
	public static volatile Action<ClusterRespawnExecution> BeforeRespawnExecute;

	/// <summary>
	/// SE-1189: runs on the source node's worker before it asks the Gateway to switch a dead client for a spawn preview,
	/// after the client's view was detached. A bench can hold the request here while the medbay's partition is split away,
	/// or throw to stand for a Gateway that refuses the switch before it moves the client's backend.
	/// </summary>
	public static volatile Action<ClusterRespawnExecution> BeforeSwitchPost;

	private static volatile Action m_servicesInstalled;
	private static volatile bool m_servicesWereInstalled;

	/// <summary>
	/// Runs <paramref name="handler"/> on the game thread at the end of ClusterRuntime's service
	/// installs, the first moment <c>MySession.Static</c> and <c>MyAPIUtilities.Static</c> exist. The
	/// test plugin installs there what a mod would install (ModChannelTestHarness); plugin Init is too
	/// early.
	/// </summary>
	/// <remarks>
	/// Latched, not a plain event, because the two start paths order the plugins differently. Preloaded
	/// (the normal path) the runtime starts on <c>DedicatedServer.Run</c>, after every plugin Init, so a
	/// subscription in Init is in time. On the late-start fallback in ClusterPluginHost, the runtime
	/// starts inside the CLUSTER plugin's Init - which runs first, since the test plugin declares it as
	/// a dependency - and a plain event would have been raised before the handler existed, leaving the
	/// mod-channel harness silently uninstalled. Registering after the fact runs the handler at once.
	/// </remarks>
	public static void OnServicesInstalled(Action handler)
	{
		if (handler == null) throw new ArgumentNullException(nameof(handler));
		lock (typeof(ClusterSeams))
		{
			m_servicesInstalled += handler;
			if (!m_servicesWereInstalled) return;
		}
		handler();
	}

	/// <summary>Raised by ClusterRuntime once, when its services are installed.</summary>
	internal static void RaiseServicesInstalled()
	{
		Action handlers;
		lock (typeof(ClusterSeams))
		{
			m_servicesWereInstalled = true;
			handlers = m_servicesInstalled;
		}
		handlers?.Invoke();
	}
}
