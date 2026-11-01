using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Server-side view of a grid's AI Flight autopilot (control route <c>/cluster/autopilot-state</c>).
///
/// T-0054: gravity AI legs stall INBOUND far more than outbound (22 % vs 55 %), and the only readout
/// the harness had was the client's replica of the AI Basic block's object builder - whose
/// CurrentWaypoint never advances on a client (AdvanceWaypoint runs on the server only), so it read
/// ".1" for every sample of every leg and said nothing. The decisions that make a ship hover at
/// 1-3 m/s live in <see cref="MyAutopilotComponent"/> on the owning node: the min-height climb
/// (m_heightAdjustmentWaypoint), stuck detection, evasive manoeuvres, a paused movement. This dumps
/// them, with the ship's and the current waypoint's height over the planet surface.
/// </summary>
public static class AutopilotStateService
{
	public static object Status(long gridEntityId)
	{
		if (!MyEntities.TryGetEntityById(gridEntityId, out MyEntity entity) || entity is not MyCubeGrid grid)
			return new { found = false, entityId = gridEntityId, reason = "not a grid on this node" };
		MyFlightMovementBlock flight = grid.GetFatBlocks<MyFlightMovementBlock>().FirstOrDefault();
		if (flight == null)
			return new { found = false, entityId = gridEntityId, reason = "no AI Flight block" };
		MyAutopilotComponent autopilot = flight.m_autopilotComponent;
		Vector3D position = grid.PositionComp.GetPosition();
		MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(position);
		MyAutopilotWaypoint current = autopilot?.m_currentWaypoint;
		MyAutopilotWaypoint destination = autopilot?.m_destinationWaypoint;
		MyAutopilotWaypoint climb = autopilot?.m_heightAdjustmentWaypoint;
		bool activated = flight.Components.TryGet<IMyAiBlockComponent>(out IMyAiBlockComponent component) && component.IsActivated;
		return new
		{
			found = true,
			entityId = gridEntityId,
			flightBlock = flight.EntityId,
			activated,
			autopilotEnabled = autopilot?.AutoPilotEnabled.Value,
			movementPaused = autopilot?.IsMovementPaused,
			collisionAvoidance = flight.CollisionAvoidance,
			precisionMode = flight.PrecisionMode,
			speedLimit = flight.SpeedLimit,
			minimalAltitude = flight.MinimalAltitude,
			waypoints = autopilot?.Waypoints?.Count ?? 0,
			currentWaypoint = current?.Name,
			// T-0105: currentIndex = -1 meant two unrelated things, and reading them as one cost a
			// 22-minute investigation. Either there is no current waypoint at all, or there IS one
			// and it is not a member of Waypoints - which is the NORMAL state while collision
			// avoidance is steering: MyPathFindingAvoidance.CreateWaypoint builds a waypoint named
			// "PathFindingVertex" with IsPathfindingVertex = true and keeps it in its own
			// m_pathfindingWaypoints list, never in the route, so IndexOf correctly returns -1 for
			// every one of them. 18 episodes of "ap=PathFindingVertex#-1" were read as a degenerate
			// planner state; they were 18 obstacle detours. currentIndexReason says which case it is
			// so nobody has to guess again.
			currentIndex = current == null ? -1 : autopilot.Waypoints.IndexOf(current),
			currentIndexReason = current == null ? "no current waypoint"
				: current.IsPathfindingVertex ? "pathfinding vertex, not in the route by design"
				: autopilot.Waypoints.IndexOf(current) < 0 ? "waypoint is not in the route"
				: "route waypoint",
			currentIsPathfindingVertex = current?.IsPathfindingVertex,
			// The one shape that IS a defect. A pathfinding vertex is placed at
			// vertex.WorldPosition + InflateDirection * (volumeRadius * 2), so an InflateDirection
			// pointing into the planet puts it under the terrain, where the ship can never arrive -
			// and the detour then never completes. pilot-4 sat on wpAGL=-23 for 22 minutes on
			// 2026-09-09. Reported as its own flag rather than left for a reader to notice that one
			// number in a 20-field line is negative.
			currentWaypointUnreachableBelowSurface =
				current != null && current.IsPathfindingVertex && Agl(planet, current.Coords) is double vertexAgl && vertexAgl < 0.0,
			currentDistance = current == null ? (double?)null : Vector3D.Distance(position, current.Coords),
			currentAgl = current == null ? null : Agl(planet, current.Coords),
			destinationWaypoint = destination?.Name,
			heightAdjustment = climb != null,
			heightAdjustmentAgl = climb == null ? null : Agl(planet, climb.Coords),
			// The game's own height reading (vertical, planet height map) beside the closest-surface
			// AGL below: the min-height climb band is computed from THIS one.
			gameHeightFromSurface = MyPlanets.Static == null ? (float?)null : MyPlanets.Static.GetHeightFromSurface(position),
			waypointGameHeight = current == null || MyPlanets.Static == null ? (float?)null : MyPlanets.Static.GetHeightFromSurface(current.Coords),
			minHeightAboveTerrainRaw = autopilot?.MinHeightAboveTerraingRaw,
			waypointThreshold = autopilot?.m_waypointThresholdDistance.Value,
			minHeightCounter = autopilot?.m_minHeightCounter,
			minHeightClimbRatio = autopilot?.m_minHeightClimbRatio,
			stuck = autopilot?.m_stuckDetection?.IsStuck,
			evasiveDirection = autopilot?.m_evasiveDirection,
			evasiveDuration = autopilot?.m_evasiveManeuversDuration,
			speed = grid.Physics == null ? (double?)null : grid.Physics.LinearVelocity.Length(),
			agl = Agl(planet, position),
			position = new[] { position.X, position.Y, position.Z },
		};
	}

	/// <summary>
	/// Sets the AI Flight autopilot's waypoint advance distance (control route
	/// <c>/cluster/autopilot-threshold</c>). With the default of 0 the autopilot advances a waypoint
	/// only inside GridSize x 3 = 7.5 m for a small grid; a Visitor in gravity reaches 18-36 m of a
	/// terrain-following waypoint, never 7.5 m, and then drifts upward at ~2 m/s for minutes
	/// (T-0054). The value is a FromServer sync, so only the owning node can set it.
	/// </summary>
	public static object SetThreshold(long gridEntityId, float meters)
	{
		if (!MyEntities.TryGetEntityById(gridEntityId, out MyEntity entity) || entity is not MyCubeGrid grid)
			return new { set = false, entityId = gridEntityId, reason = "not a grid on this node" };
		MyFlightMovementBlock flight = grid.GetFatBlocks<MyFlightMovementBlock>().FirstOrDefault();
		MyAutopilotComponent autopilot = flight?.m_autopilotComponent;
		if (autopilot == null)
			return new { set = false, entityId = gridEntityId, reason = "no AI Flight block" };
		float previous = autopilot.WaypointThresholdDistance;
		autopilot.WaypointThresholdDistance = meters;
		return new { set = true, entityId = gridEntityId, flightBlock = flight.EntityId, previous, threshold = autopilot.WaypointThresholdDistance };
	}

	/// <summary>Height over the nearest planet surface; null off-planet.</summary>
	private static double? Agl(MyPlanet planet, Vector3D point)
	{
		if (planet == null) return null;
		Vector3D surface = planet.GetClosestSurfacePointGlobal(point);
		double sign = Vector3D.Distance(point, planet.PositionComp.GetPosition()) >= Vector3D.Distance(surface, planet.PositionComp.GetPosition()) ? 1 : -1;
		return sign * Vector3D.Distance(point, surface);
	}
}
