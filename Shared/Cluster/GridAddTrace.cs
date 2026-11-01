using System;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Diagnostic: logs every sizeable grid added to the world, with the call stack that added it.
/// <para>
/// Written for the 2026-08-16 duplicate-incarnation investigation. Copies of NPC grids
/// ("Pirate Base Large mk.2" entity 131309635519957431, "Deterrent mk.1" 101329189107324757)
/// materialized on a second node with the SAME entity id and NO logged origin: no handover
/// import, no recovery, no concealment wake, no encounter dispatch - every instrumented path
/// was excluded by the logs, so the creator is a path that does not log. Each copy then seeded
/// a registered partition, and the pair wedged both sides HandoverBlocked forever (the
/// co-location import bounces off the duplicate-incarnation guard by design).
/// </para>
/// <para>
/// This hook turns the next occurrence into a solved case: the add of the second copy will be
/// in the log with the stack that performed it. Ten blocks is the floor so grid-split debris
/// and missiles stay out; a handover import legitimately logs its members once per transfer,
/// which is cheap and self-labelling (the stack says Import). T-0401: the staged import's activation
/// adds are no longer traced by default (diag.gridAddTraceImports).
/// </para>
/// </summary>
internal static class GridAddTrace
{
	private const int MinBlocks = 10;

	public static void Install()
	{
		MyEntities.OnEntityAdd += Trace;
	}

	private static void Trace(MyEntity entity)
	{
		try
		{
			if (entity is not MyCubeGrid grid || grid.BlocksCount < MinBlocks) return;
			if (Skips(entity, ClusterConfig.GetBool("diag.gridAddTraceImports"))) return;
			// One line per add; the stack is flattened so a grep for the entity id returns the
			// whole story. Skip the two outermost frames (this method and the event invoker).
			string stack = Environment.StackTrace.Replace(Environment.NewLine, " | ");
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] grid-add entity=" + entity.EntityId
				+ " name='" + (entity.DisplayName ?? grid.Name ?? "?")
				+ "' blocks=" + grid.BlocksCount + " stack=" + stack);
		}
		catch
		{
			// Diagnostics never break entity creation.
		}
	}

	/// <summary>
	/// T-0401: a handover import's activation Add (ClusterControlServer.ActivateEntities, marked by ImportAddDiag)
	/// is already logged by the import path, and the stack walk cost it on the game thread mid-activation. Skipped
	/// unless diag.gridAddTraceImports is true (the old behaviour, for an A/B). Every other add is traced as before.
	/// </summary>
	internal static bool Skips(object entity, bool traceImports) => !traceImports && ImportAddDiag.IsImportAdd(entity);
}
