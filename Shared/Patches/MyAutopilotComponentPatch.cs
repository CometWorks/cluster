using System.Collections.Generic;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Multiplayer;

namespace Shared.Patches;

/// <summary>
/// T-0170: an AI Flight autopilot loses its place in the route when its grid is loaded, and a
/// handover import is a load. The Flight block's own builder is fine - MyAutopilotComponent.Serialize
/// writes CurrentWaypointIndex and InitWithObjectBuilder restores it. What undoes it is the AI Basic
/// task block: MyBasicMissionAutopilot re-pushes its route through
/// MyAiBlockSystem.SetMovementWaypoints(m_waypoints) whenever the block starts working or is selected,
/// both of which happen again on the importing node, and that call passes
/// continueOnCurrentCourse = false, so MyAutopilotComponent.SetWaypoints flies from Waypoints[0].
/// On w32 a ship crossing a node boundary on its way to A turned back to A.1, crossed the boundary
/// again, and looped until the leg budget ran out (pilot-20, 2026-09-17 15:28-15:48).
///
/// The prefix remembers the route index being flown when the pushed list is the SAME route
/// (same count, every waypoint equal by result coordinates); the postfix puts the autopilot back on it
/// if vanilla restarted at 0. A different route, an empty one, or a reversed-course push is left to
/// vanilla. A current waypoint that is a collision-avoidance vertex is resolved to the route waypoint
/// it detours to (m_destinationWaypoint).
/// </summary>
[HarmonyPatch(typeof(MyAutopilotComponent), nameof(MyAutopilotComponent.SetWaypoints))]
public static class MyAutopilotComponentPatch
{
	private static void Prefix(MyAutopilotComponent __instance, List<MyAutopilotWaypoint> newWaypoints,
		bool continueOnCurrentCourse, bool continueOnReversedCourse, out int __state)
	{
		__state = -1;
		if (!ClusterRuntime.IsClustered || !Sync.IsServer || continueOnCurrentCourse || continueOnReversedCourse)
			return;
		List<MyAutopilotWaypoint> route = __instance.Waypoints;
		if (newWaypoints == null || route == null || route.Count < 2 || newWaypoints.Count != route.Count)
			return;
		for (int i = 0; i < route.Count; i++)
			if (!MyAutopilotWaypoint.AreEqualByResultCoordinates(route[i], newWaypoints[i]))
				return;
		MyAutopilotWaypoint current = __instance.CurrentWaypoint;
		if (current != null && current.IsPathfindingVertex)
			current = __instance.m_destinationWaypoint;
		int index = current == null ? -1 : route.IndexOf(current);
		if (index > 0)
			__state = index;
	}

	private static void Postfix(MyAutopilotComponent __instance, List<MyAutopilotWaypoint> newWaypoints, int __state)
	{
		if (__state <= 0)
			return;
		List<MyAutopilotWaypoint> route = __instance.Waypoints;
		if (route == null || __state >= route.Count)
			return;
		MyAutopilotWaypoint current = __instance.CurrentWaypoint;
		if (current != null && current != route[0])
			return;
		__instance.CurrentWaypoint = route[__state];
		long grid = (__instance.Entity as Sandbox.Game.Entities.MyCubeBlock)?.CubeGrid?.EntityId ?? 0;
		VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] autopilot_route_kept grid=" + grid + " block=" + (__instance.Entity?.EntityId ?? 0)
			+ " index=" + __state + " of " + route.Count + " name='" + route[__state].Name
			+ "' (a same-route re-push had restarted it at " + (current == null ? "none" : "0") + ")");
	}
}
