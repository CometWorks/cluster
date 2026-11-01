using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// T-0232: replace an ACTIVE AI route from the owning node (control route <c>POST /cluster/autopilot-route</c>).
///
/// An AI leg's pilot stands 2.5 km off for the whole leg because a near pilot suppresses the AI, and
/// the route is planned once at the start from the client's asteroid list, which covers only ~20 km
/// around the client - so the far part of every long AI leg was flown blind (1 of 3 voxel hits in
/// ~17.5 h on w32 was an AI leg). The harness re-plans as the ship flies and, when the new plan has a
/// detour, hands it over here. The client's own clear + add also lands from 2.5 km (P1-AI-ROUTE probe,
/// 2026-09-24), but as two events a handover capture can split - exporting the EMPTY list - and only
/// while validation lets them through; this swap is one step on the owner's game thread and answers
/// replaced true/false.
///
/// The swap goes through the block's own synced events, raised on the server, so every client's
/// replica of the list follows (the harness's next-leg clear counts that replica): AddWaypoints the
/// new ones, then RemoveWaypoints the old indices. The Basic block re-pushes each step with
/// continueOnCurrentCourse, which keeps the flight on the OLD route's index - on the new list that
/// would skip the new first detour - so each Flight block is then set onto its list's first point.
/// Activation is not touched: the AI never stops.
/// </summary>
public static class AutopilotRouteService
{
	public static object Replace(long gridEntityId, Vector3D[] positions, string[] names)
	{
		if (positions == null || positions.Length == 0)
			throw new ArgumentException("positions must name at least one waypoint");
		if (names == null || names.Length != positions.Length)
			throw new ArgumentException("names must have one entry per position");
		if (positions.Any(position => !position.IsValid()))
			throw new ArgumentException("positions must be finite");
		if (!MyEntities.TryGetEntityById(gridEntityId, out MyEntity entity) || entity is not MyCubeGrid grid)
			return new { replaced = false, entityId = gridEntityId, reason = "not a grid on this node" };
		if (grid.Closed || grid.MarkedForClose)
			return new { replaced = false, entityId = gridEntityId, reason = "grid is closing" };
		if (ClusterRuntime.IsFrozen(grid.EntityId))
			return new { replaced = false, entityId = gridEntityId, reason = "frozen for a handover in progress" };
		MyBasicMissionAutopilot basic = grid.GetFatBlocks()
			.Select(block => block.Components.Get<MyBasicMissionAutopilot>())
			.FirstOrDefault(found => found != null);
		if (basic == null)
			return new { replaced = false, entityId = gridEntityId, reason = "no AI Basic block" };
		bool basicActive = basic.Entity.Components.TryGet<IMyAiBlockComponent>(out IMyAiBlockComponent basicComponent)
			&& basicComponent.IsActivated;
		if (!basicActive)
			// Swapping the list of an idle block is the client's normal pre-leg path; this route is for
			// a leg in flight only, and refusing keeps it from racing the harness's own hand-over.
			return new { replaced = false, entityId = gridEntityId, reason = "AI Basic block is not activated" };

		int previous = basic.m_waypoints.Count;
		// Patrol mode remembers its direction across legs; a leg that reached its end flipped it, and
		// AdvanceWaypoint would then step BACKWARDS into the new list.
		basic.m_patrolDirectionForward = true;
		MyMultiplayer.RaiseEvent(basic, x => x.AddWaypoints, positions, names);
		if (previous > 0)
			MyMultiplayer.RaiseEvent(basic, x => x.RemoveWaypoints, Enumerable.Range(0, previous).ToArray());
		List<MyAutopilotWaypoint> route = basic.m_waypoints;
		if (route.Count > 0)
			basic.m_currentWaypoint = route[0];
		// The flight is put on the new [0] directly, as vanilla's MovementWaypointsChangedAndContinue
		// does. Re-pushing the list with continueOnCurrentCourse=false does NOT do it: it is the same
		// route the events just pushed, and MyAutopilotComponentPatch (T-0170) keeps the flown index for
		// a same-route re-push - so the flight stayed on [1] and skipped the detour (P1-AI-ROUTE,
		// 2026-09-24 06:30, "flying": "A").
		MyAutopilotWaypoint flying = null;
		foreach (MyFlightMovementBlock flight in grid.GetFatBlocks<MyFlightMovementBlock>())
		{
			List<MyAutopilotWaypoint> flown = flight.m_autopilotComponent?.Waypoints;
			if (flown == null || flown.Count == 0)
				continue;
			flight.CurrentWaypoint = flown[0];
			flying ??= flight.CurrentWaypoint;
		}
		Console.WriteLine("[ClusterPlugin] autopilot_route_replaced grid=" + grid.EntityId + " previous=" + previous
			+ " now=" + route.Count + " flying='" + (flying?.Name ?? "none") + "' (T-0232)");
		return new
		{
			replaced = route.Count == positions.Length,
			entityId = gridEntityId,
			basicBlock = basic.Entity.EntityId,
			previous,
			waypoints = route.Count,
			names = route.Select(waypoint => waypoint.Name).ToArray(),
			current = basic.m_currentWaypoint?.Name,
			flying = flying?.Name,
		};
	}
}
