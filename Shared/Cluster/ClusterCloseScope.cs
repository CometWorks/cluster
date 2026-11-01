using System;

namespace ClusterPlugin;

/// <summary>
/// Entity closes the cluster makes: handover release and discard, offline unload, the boot drop of the
/// node's world file entities, a discarded dormant import. The entity is not destroyed, it lives on in a
/// catalogued save or on another node. A close handler that settles something on destruction (a refund, a
/// failed contract) checks <see cref="Active"/> and leaves that out. Closes run synchronously inside
/// <see cref="Run"/>, so a handler raised by <c>MyEntities.DeleteRememberedEntities</c> there sees it.
/// SE1-0232; reusable by any per-block state that must survive a move.
/// </summary>
internal static class ClusterCloseScope
{
	[ThreadStatic] private static int t_depth;

	public static bool Active => t_depth > 0;

	public static void Run(Action close)
	{
		t_depth++;
		try { close(); }
		finally { t_depth--; }
	}
}
