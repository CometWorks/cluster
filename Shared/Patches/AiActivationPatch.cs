using System;
using System.Collections.Concurrent;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace Shared.Patches;

/// <summary>
/// T-0225: an AI Flight block whose own activation switches it straight back off.
///
/// MyAiBlockComponent.Deactivate() - the AI system's internal path - sets IsActivated = false WITHOUT the
/// ValueChanged handler, so the grid's MyAiBlockSystem keeps the component registered as its active
/// Movement component. The next real activation (a client's ActivateBehavior = true) reaches
/// MyAiBlockSystem.ActivateComponent, finds that stale entry IS this component, calls Deactivate() on it -
/// switching the block that is being switched on back off - and then registers it again, deactivated.
/// Every Basic block listening then pushes an EMPTY route (ExecuteBehavior sees IsActivated false), and
/// the harness's off/on retry cannot break the loop: "off" is a no-op on a value that is already off, so
/// each "on" bounces again. That is the owning node's view on 2026-09-23 15:36-15:39 (pilot-27, four
/// misses in a row): Basic block 2 waypoints, activated, selected - route received every time - and the
/// Flight block waypoints=0 activated=False autopilot disabled.
///
/// The prefix drops the stale self-entry so vanilla registers the component as a fresh activation. Every
/// activation of a Visitor's Movement block that does NOT end activated is logged with the reason
/// (another block holds the slot, CanActivateComponent refused, or none that we can see), so a miss that
/// is not this mechanism still says what it is. At most 10 lines per grid per minute.
/// </summary>
[HarmonyPatch(typeof(MyAiBlockSystem), nameof(MyAiBlockSystem.ActivateComponent), typeof(IMyAiBlockComponent))]
public static class AiActivationPatch
{
	private static readonly ConcurrentDictionary<long, (DateTime Window, int Count)> Lines = new();

	private static bool Admit(long grid)
	{
		DateTime now = DateTime.UtcNow;
		(DateTime window, int count) = Lines.TryGetValue(grid, out var seen) ? seen : (now, 0);
		if (now - window > TimeSpan.FromMinutes(1)) (window, count) = (now, 0);
		Lines[grid] = (window, ++count);
		if (Lines.Count > 4096) Lines.Clear();
		return count <= 10;
	}

	private static MyCubeGrid VisitorGrid(IMyAiBlockComponent component)
	{
		if (component?.AiBlockType != MyAiBlockType.Movement || component.Entity is not MyCubeBlock block)
			return null;
		MyCubeGrid grid = block.CubeGrid;
		return grid?.DisplayName != null && grid.DisplayName.StartsWith("Visitor", StringComparison.Ordinal) ? grid : null;
	}

	[HarmonyPrefix]
	private static void Prefix(MyAiBlockSystem __instance, IMyAiBlockComponent component, out string __state)
	{
		__state = null;
		if (!ClusterPlugin.ClusterRuntime.IsClustered || !Sync.IsServer || component == null)
			return;
		if (__instance.CombatStateActive && component.AiBlockType == MyAiBlockType.Task)
			return;
		__instance.m_activeAiComponents.TryGetValue(component.AiBlockType, out IMyAiBlockComponent held);
		if (held == null)
		{
			__state = "slot empty";
			return;
		}
		if (held != component)
		{
			__state = "slot held by " + (held.Entity?.EntityId ?? 0) + (held.IsActivated ? " (activated)" : " (not activated)");
			return;
		}
		if (component.Entity is IMyPathRecorderBlock)
			return;
		// The stale self-entry: vanilla would Deactivate() the component it is activating.
		__instance.m_activeAiComponents.Remove(component.AiBlockType);
		__state = "stale self-registration cleared";
		MyCubeGrid grid = VisitorGrid(component) ?? (component.Entity as MyCubeBlock)?.CubeGrid;
		if (grid != null && Admit(grid.EntityId))
			VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ai_activation stale self-registration cleared grid="
				+ grid.EntityId + " name='" + grid.DisplayName + "' block=" + (component.Entity?.EntityId ?? 0)
				+ " type=" + component.AiBlockType + " (T-0225)");
	}

	[HarmonyPostfix]
	private static void Postfix(MyAiBlockSystem __instance, IMyAiBlockComponent component, string __state)
	{
		if (__state == null)
			return;
		MyCubeGrid grid = VisitorGrid(component);
		if (grid == null || !Admit(grid.EntityId))
			return;
		bool registered = __instance.GetActiveAiComponent(component.AiBlockType) == component;
		bool working = component.Entity is MyCubeBlock block && block.IsWorking;
		VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ai_activation grid=" + grid.EntityId
			+ " name='" + grid.DisplayName + "' block=" + (component.Entity?.EntityId ?? 0)
			+ " activated=" + component.IsActivated + " registered=" + registered + " working=" + working
			+ " before=" + __state + " (T-0225)");
	}
}
