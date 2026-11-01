using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.ModAPI;
using Shared.Tools;

namespace ClusterPlugin;

/// <summary>
/// T-0124. Writes <see cref="MyEntitySeams.SimCostTicks"/>, the balancer's per-entity CPU term.
///
/// WHY THIS EXISTS. PartitionManager.SampleSimLoad sums `entity.SimCostTicks` deltas over a
/// partition's members, and that field HAD NO WRITER: nothing charged it from
/// MyParallelEntityUpdateOrchestrator's dispatch loops. So the term was identically zero cluster-wide and the
/// per-partition load was the physics share alone - the whole node's Havok step handed out pro
/// rata by active-body count, which tracks MOVEMENT and is close to orthogonal to the cost of
/// holding content. T-0120 patched around it by charging the unattributed main-thread remainder
/// spread by member count; that fixed the per-NODE total but left the metric with no per-entity
/// measurement at all. This restores one.
///
/// HOW. Every call the orchestrator makes into an entity's update is redirected to a timing
/// wrapper of the same signature. The alternative - rewriting the loop bodies to keep a chained
/// timestamp - is one Stopwatch read per entity instead of two, but it has
/// to reconstruct the loop shape from IL and breaks silently when the game's loops change. A
/// call redirect asserts an exact site count and throws when the count moves.
///
/// COST. Two Stopwatch.GetTimestamp() calls per entity-update. The each-frame lists dominate:
/// ~1500 entities x 60 Hz x 2 reads is ~180k reads/s, order 4 ms/s of wall time on one thread
/// against the ~1000 ms/s budget a node has. Measured, not assumed - see the ticket. It is gated
/// anyway: `balance.entityCostSampling` defaults on but turns the wrapper into a straight
/// passthrough when off, so the cost can be removed live without a redeploy.
/// </summary>
[HarmonyPatch(typeof(MyParallelEntityUpdateOrchestrator))]
internal static class MyEntitySimCostPatch
{
	/// <summary>
	/// Read once per dispatch rather than per entity: ClusterConfig.Get walks a dictionary and
	/// does a string compare, which is not something to do 90,000 times a second. Refreshed by
	/// <see cref="RefreshEnabled"/> from the orchestrator's own dispatch entry points.
	/// </summary>
	private static bool m_enabled = true;

	internal static void RefreshEnabled()
	{
		m_enabled = ClusterRuntime.IsClustered && ClusterConfig.GetBool("balance.entityCostSampling");
	}

	// ---- the timing wrappers -------------------------------------------------------------
	//
	// Each has the signature of the call it replaces, receiver first. The inner call is a
	// virtual dispatch made from THIS assembly, which Harmony has not patched, so there is no
	// recursion. Exceptions are not caught: an entity that throws out of its update did so
	// before this patch existed too, and swallowing it here would hide it.

	private static void Charge(MyEntity entity, long started)
	{
		// Interlocked inside AddSimCostTicks: the parallel handlers run on worker threads.
		entity.AddSimCostTicks(Stopwatch.GetTimestamp() - started);
	}

	public static void UpdateBeforeSimulation(MyEntity entity)
	{
		if (!m_enabled) { entity.UpdateBeforeSimulation(); return; }
		long started = Stopwatch.GetTimestamp();
		try { entity.UpdateBeforeSimulation(); } finally { Charge(entity, started); }
	}

	public static void UpdateBeforeSimulation10(MyEntity entity)
	{
		if (!m_enabled) { entity.UpdateBeforeSimulation10(); return; }
		long started = Stopwatch.GetTimestamp();
		try { entity.UpdateBeforeSimulation10(); } finally { Charge(entity, started); }
	}

	public static void UpdateBeforeSimulation100(MyEntity entity)
	{
		if (!m_enabled) { entity.UpdateBeforeSimulation100(); return; }
		long started = Stopwatch.GetTimestamp();
		try { entity.UpdateBeforeSimulation100(); } finally { Charge(entity, started); }
	}

	public static void UpdateAfterSimulation(MyEntity entity)
	{
		if (!m_enabled) { entity.UpdateAfterSimulation(); return; }
		long started = Stopwatch.GetTimestamp();
		try { entity.UpdateAfterSimulation(); } finally { Charge(entity, started); }
	}

	public static void UpdateAfterSimulation10(MyEntity entity)
	{
		if (!m_enabled) { entity.UpdateAfterSimulation10(); return; }
		long started = Stopwatch.GetTimestamp();
		try { entity.UpdateAfterSimulation10(); } finally { Charge(entity, started); }
	}

	public static void UpdateAfterSimulation100(MyEntity entity)
	{
		if (!m_enabled) { entity.UpdateAfterSimulation100(); return; }
		long started = Stopwatch.GetTimestamp();
		try { entity.UpdateAfterSimulation100(); } finally { Charge(entity, started); }
	}

	public static void Simulate(MyEntity entity)
	{
		if (!m_enabled) { entity.Simulate(); return; }
		long started = Stopwatch.GetTimestamp();
		try { entity.Simulate(); } finally { Charge(entity, started); }
	}

	// The parallel handlers take IMyParallelUpdateable, not MyEntity. Every implementer in the
	// orchestrator's lists is a MyEntity, but the static type is not, so the charge is skipped
	// rather than cast-and-throw: an un-chargeable implementer must not take the update with it.
	public static void UpdateBeforeSimulationParallel(IMyParallelUpdateable entity)
	{
		if (!m_enabled || entity is not MyEntity charged) { entity.UpdateBeforeSimulationParallel(); return; }
		long started = Stopwatch.GetTimestamp();
		try { entity.UpdateBeforeSimulationParallel(); } finally { Charge(charged, started); }
	}

	public static void UpdateAfterSimulationParallel(IMyParallelUpdateable entity)
	{
		if (!m_enabled || entity is not MyEntity charged) { entity.UpdateAfterSimulationParallel(); return; }
		long started = Stopwatch.GetTimestamp();
		try { entity.UpdateAfterSimulationParallel(); } finally { Charge(charged, started); }
	}

	// ---- the redirects -------------------------------------------------------------------

	/// <summary>
	/// Replace every call to <paramref name="target"/> with a call to the same-named wrapper on
	/// this class, and THROW if the number of sites is not exactly <paramref name="expected"/>.
	///
	/// The count assertion is the whole safety argument. A silent zero-replacement transpiler
	/// leaves SimCostTicks at 0 - which is indistinguishable from the bug this patch exists to
	/// fix, and would be read as "the port did not help" rather than "the port did not apply".
	/// A count that grows is equally a signal: the game added an update path this does not know
	/// about, and the metric would be quietly missing part of the cost.
	/// </summary>
	private static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions,
		MethodBase original, MethodInfo target, int expected)
	{
		MethodInfo wrapper = AccessTools.Method(typeof(MyEntitySimCostPatch), target.Name)
			?? throw new CodeInstructionNotFound($"{original.Name}: no wrapper named {target.Name}");
		List<CodeInstruction> il = instructions.ToList();
		List<int> sites = il.FindAllIndex(ci => ci.Calls(target));
		if (sites.Count != expected)
			throw new CodeInstructionNotFound(
				$"{original.Name}: expected {expected} call(s) to {target.DeclaringType?.Name}.{target.Name}, found {sites.Count}");
		foreach (int site in sites)
		{
			il[site].opcode = OpCodes.Call;
			il[site].operand = wrapper;
		}
		return il;
	}

	private static MethodInfo Entity(string name) =>
		AccessTools.DeclaredMethod(typeof(MyEntity), name, Type.EmptyTypes)
		?? throw new CodeInstructionNotFound($"MyEntity.{name} not found");

	private static MethodInfo Parallel(string name) =>
		AccessTools.DeclaredMethod(typeof(IMyParallelUpdateable), name, Type.EmptyTypes)
		?? throw new CodeInstructionNotFound($"IMyParallelUpdateable.{name} not found");

	// Site counts read off the current decompiled orchestrator: the *10 and *100 loops each walk
	// a plain list and a "Heavy" list, hence two per method; UpdateAfterSimulation walks
	// m_entitiesForUpdate and m_entitiesForUpdateAfter. 14 sites total.

	[HarmonyTranspiler, HarmonyPatch("UpdateBeforeSimulation")]
	private static IEnumerable<CodeInstruction> BeforeTranspiler(IEnumerable<CodeInstruction> il, MethodBase original)
		=> Redirect(il, original, Entity(nameof(MyEntity.UpdateBeforeSimulation)), 1);

	[HarmonyTranspiler, HarmonyPatch("UpdateBeforeSimulation10")]
	private static IEnumerable<CodeInstruction> Before10Transpiler(IEnumerable<CodeInstruction> il, MethodBase original)
		=> Redirect(il, original, Entity(nameof(MyEntity.UpdateBeforeSimulation10)), 2);

	[HarmonyTranspiler, HarmonyPatch("UpdateBeforeSimulation100")]
	private static IEnumerable<CodeInstruction> Before100Transpiler(IEnumerable<CodeInstruction> il, MethodBase original)
		=> Redirect(il, original, Entity(nameof(MyEntity.UpdateBeforeSimulation100)), 2);

	[HarmonyTranspiler, HarmonyPatch("UpdateAfterSimulation")]
	private static IEnumerable<CodeInstruction> AfterTranspiler(IEnumerable<CodeInstruction> il, MethodBase original)
		=> Redirect(il, original, Entity(nameof(MyEntity.UpdateAfterSimulation)), 2);

	[HarmonyTranspiler, HarmonyPatch("UpdateAfterSimulation10")]
	private static IEnumerable<CodeInstruction> After10Transpiler(IEnumerable<CodeInstruction> il, MethodBase original)
		=> Redirect(il, original, Entity(nameof(MyEntity.UpdateAfterSimulation10)), 2);

	[HarmonyTranspiler, HarmonyPatch("UpdateAfterSimulation100")]
	private static IEnumerable<CodeInstruction> After100Transpiler(IEnumerable<CodeInstruction> il, MethodBase original)
		=> Redirect(il, original, Entity(nameof(MyEntity.UpdateAfterSimulation100)), 2);

	[HarmonyTranspiler, HarmonyPatch(nameof(MyParallelEntityUpdateOrchestrator.DispatchSimulate))]
	private static IEnumerable<CodeInstruction> SimulateTranspiler(IEnumerable<CodeInstruction> il, MethodBase original)
		=> Redirect(il, original, Entity(nameof(MyEntity.Simulate)), 1);

	[HarmonyTranspiler, HarmonyPatch("ParallelUpdateHandlerBeforeSimulation")]
	private static IEnumerable<CodeInstruction> ParallelBeforeTranspiler(IEnumerable<CodeInstruction> il, MethodBase original)
		=> Redirect(il, original, Parallel(nameof(IMyParallelUpdateable.UpdateBeforeSimulationParallel)), 1);

	[HarmonyTranspiler, HarmonyPatch("ParallelUpdateHandlerAfterSimulation")]
	private static IEnumerable<CodeInstruction> ParallelAfterTranspiler(IEnumerable<CodeInstruction> il, MethodBase original)
		=> Redirect(il, original, Parallel(nameof(IMyParallelUpdateable.UpdateAfterSimulationParallel)), 1);

	// The config read, hung off the two dispatch entry points so the knob is live-switchable.
	[HarmonyPrefix, HarmonyPatch(nameof(MyParallelEntityUpdateOrchestrator.DispatchBeforeSimulation))]
	private static void RefreshBefore() => RefreshEnabled();
}
