using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace Shared.Patches;

/// <summary>
/// T-0180, Cycle Objects half: make EVERY entity reachable from the Admin tool's cycler.
///
/// The Admin screen's "Sort by" orders are not really sorts, they are FILTERS.
/// <c>MyEntityCycling.FindNext</c> walks all of <c>MyEntities.GetEntities()</c> and skips anything
/// whose <c>GetMetric(order, entity)</c> returns 0, and the kind restriction lives entirely in that
/// method: Characters is <c>entity is MyCharacter</c>, FloatingObjects is <c>MyFloatingObject</c>,
/// Planets is <c>MyPlanet</c>, the grid orders return block counts. Missiles, meteors, safe zones,
/// voxel maps and bags are in <c>MyEntities</c> and never score under any order, so no combination
/// of the screen's controls can reach them.
///
/// THE WIDENING IS ONE ORDER, NOT ALL OF THEM. Under <c>StaticObjects</c> an entity that vanilla
/// scores 0 now scores 1. Vanilla already scores 1 for anything it considers static, so the two
/// populations share a metric and <c>Metric</c> breaks the tie on EntityId - which turns that one
/// order into a stable walk of every entity in id order. The other orders keep their exact vanilla
/// meaning, so an admin who picks "Characters" still gets characters and nothing else.
///
/// WHY NOT THE "both checkboxes" TRICK. The ticket suggested hijacking the contradictory
/// <c>OnlySmallGrids &amp;&amp; OnlyLargeGrids</c> combination as a free "everything" bit, since a
/// grid cannot be both and vanilla therefore matches nothing. That works, but it has to be read in
/// <c>FindNext</c>, whose signature takes an internal <c>CyclingOptions</c> - a type this patch
/// would then have to name correctly forever, for a control the admin has no reason to guess.
/// Widening one visible order costs nothing and is discoverable from the screen itself.
///
/// WHAT THE ADMIN GETS, and what they do not. Camera attach, spectator jump, Teleport here, Remove,
/// Stop and Depower all address the entity by id or by position and work unchanged
/// (<c>ProceedEntity_Implementation</c>, <c>TeleportControlledEntity</c>). The NAME LABEL goes
/// stale for an entity that is not replicated to that client - <c>UpdateRemoveAndDepowerButton</c>
/// returns early when <c>MyEntities.TryGetEntityById</c> fails - which is a vanilla client-side
/// limitation this server-side patch cannot reach, and is why the ticket calls the label out.
///
/// CLUSTER_ADMIN_WIDEN=0 restores vanilla behaviour on both halves (see MyEntityListPatch).
/// </summary>
public static class MyEntityCyclingPatch
{
	internal static readonly bool Enabled =
		(Environment.GetEnvironmentVariable("CLUSTER_ADMIN_WIDEN") ?? "1") != "0";

	[HarmonyPatch(typeof(MyEntityCycling), nameof(MyEntityCycling.GetMetric))]
	private static class GetMetric
	{
		[HarmonyPostfix]
		private static void Postfix(MyEntityCyclingOrder order, MyEntity entity, ref float __result)
		{
			// Only ever RAISES a zero. An entity vanilla already scores keeps its own metric, so
			// the ordering within the static population is untouched and this can never hide one.
			if (!Enabled || __result != 0f || order != MyEntityCyclingOrder.StaticObjects) return;
			if (entity == null) return;
			__result = 1f;
		}
	}
}
