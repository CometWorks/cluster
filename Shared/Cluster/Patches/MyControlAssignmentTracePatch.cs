using Sandbox.Game.Multiplayer;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Records every point at which the server decides what a client controls.
/// </summary>
/// <remarks>
/// A client was found holding the character's replicable, bound on the server to the right entity,
/// with the character alive and controlled - and still reporting no local character. Server-side state
/// was consistent everywhere it could be read, so the gap is in what the client was told, and nothing
/// logged that. This traces the three sites that raise OnControlChangedSuccess plus the entry to
/// SetControlledEntityInternal, so an assignment that is computed but never sent, or sent for the
/// wrong entity, is visible instead of inferred.
///
/// Diagnostic only - it changes no behaviour. Remove it once the defect it is chasing is fixed.
/// </remarks>
public static class MyControlAssignmentTracePatch
{
	public static void Install()
	{
		MyPlayerCollectionSeams.ClusterControlAssignment = Trace;
	}

	private static void Trace(string site, ulong steamId, int serialId, long entityId, bool flag)
	{
		MyLog.Default?.WriteLine($"control_assignment site={site} client={steamId} serial={serialId} "
			+ $"entity={entityId} flag={flag}");
	}
}
