using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Groups;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// MEASUREMENT ONLY - counts ACTIVE (non-sleeping) rigid bodies and splits them by whether anyone is
/// aboard.
///
/// Why this exists: <c>MyPhysics.StepWorlds</c> is 43-91 % of game-thread FRAME time on every trace
/// taken today - eight traces, two build eras, both sim regimes, a 5x entity range - while the whole
/// cluster plugin is 2.5-4.2 % and handover-marshalled work is 0.16-0.64 %. The game thread is
/// physics. But 88 % of that subtree is <c>UNMANAGED_CODE_TIME</c> with no managed frames, so a
/// sampling profiler cannot say WHAT is being stepped, and "how much of it is abandoned wreckage
/// that should be asleep" is unanswerable from any trace. It needs a census, which is this.
///
/// The question it answers: of the bodies Havok is actually integrating, how many belong to grids
/// with nobody aboard? This world carries ~251 abandoned wrecks.
///
/// Registered falsifier, before any data: if active pilotless bodies are a SMALL share of active
/// bodies, wreck sleep is not the lever and the physics cost is the game the cluster exists to run.
/// A large share does not by itself prove the cost is reducible - a body can be legitimately awake -
/// but it is the precondition for the question being worth pursuing at all.
///
/// Cost: the walk is O(entities) ONCE PER <see cref="ReportEveryTicks"/>, not per tick. Measuring a
/// cost must not add one - the same rule the latch probe was written to.
/// </summary>
public static class PhysicsBodyCensus
{
	/// <summary>
	/// Ticks of the plugin update between reports. NOT frames: <see cref="ClusterRuntime.Update"/> is
	/// driven by a 250 ms <c>Timer</c> (ClusterRuntime.cs:315), so it runs 4x a second, not 60x.
	/// This was 3600 on the assumption of a per-frame call, which made the period 15 MINUTES against a
	/// 20-minute node lifetime - the census reported nothing at all on its first deployment
	/// (2026-08-22 15:48-16:05, five nodes, zero lines). 240 ticks is the 60 s that was intended.
	/// </summary>
	private const int ReportEveryTicks = 240;
	/// <summary>Bound on the per-report height list, so one crowded node cannot write an unbounded line.</summary>
	private const int MaxHeights = 512;
	/// <summary>Bound on the cached resting-height table; cleared wholesale rather than grown.</summary>
	private const int MaxRestingHeights = 4096;

	/// <summary>Clearance of each SLEEPING grid, computed once per resting place. See the census walk.</summary>
	private static readonly Dictionary<long, double> RestingHeights = new Dictionary<long, double>();

	private static long m_ticks;
	private static readonly HashSet<long> Occupied = new HashSet<long>();

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		if (++m_ticks % ReportEveryTicks != 0) return;

		// Who is aboard what. One pass over the online players (<= 24), keyed by the TOP-MOST parent:
		// a seated pilot's top-most parent is the grid, a walking player's is the character itself, so
		// a grid is "occupied" exactly when some player's top-most parent is that grid.
		Occupied.Clear();
		foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
		{
			MyEntity top = player?.Character?.GetTopMostParent();
			if (top == null) continue;
			Occupied.Add(top.EntityId);

			// A rotor or piston subgrid is a SEPARATE MyCubeGrid with its own rigid body, and it is
			// not an entity-hierarchy child of the grid it is bolted to - so an EntityId test alone
			// would report a piloted ship's turret head as pilotless and inflate the very number
			// this census exists to measure. Mechanical, not Physical: Physical also merges docked
			// ships and landing-gear locks, which would count every ship parked at an occupied
			// platform as occupied - the opposite error.
			if (top is not MyCubeGrid piloted) continue;
			MyGroups<MyCubeGrid, MyGridMechanicalGroupData>.Group group =
				MyCubeGridGroups.Static?.Mechanical.GetGroup(piloted);
			if (group == null) continue;
			foreach (MyGroups<MyCubeGrid, MyGridMechanicalGroupData>.Node node in group.Nodes)
				Occupied.Add(node.NodeData.EntityId);
		}

		int grids = 0, withPhysics = 0, active = 0, activeOccupied = 0, activeStatic = 0;
		int activeBlocks = 0, sleepingBlocks = 0;
		// Height above the voxel surface, per grid. The damage census records horizontal distance only,
		// so a ship that fell and one that was shot look identical in it, and the obvious free proxy -
		// inferring ground level from the radius of nearby resting wrecks - died on its own control
		// (factory-fa, 2026-08-22: ordinary flight sits at "ground level" by that measure too, and the
		// terrain samples ARE wrecks, so it is circular where it has the most coverage). This is the
		// real measurement, and the walk that reports it already exists.
		List<string> heights = new List<string>();
		bool heightsTruncated = false;
		// Sleeping wrecks get their clearance computed ONCE and cached: they are at rest, so the number
		// cannot change until they move, and a per-poll query for a population that grows all run would
		// be pure waste. Including them was factory-fa's call and it is the right one - 25% of damage
		// events are first seen already below 10/53 blocks and half of those leave the in-use list
		// within one poll, so a resting wreck's clearance is the ONLY direct evidence about how it
		// arrived. That is the fall-versus-collision question, open since w19.
		List<string> resting = new List<string>();
		foreach (MyEntity entity in MyEntities.GetEntities())
		{
			if (entity is not MyCubeGrid grid || grid.MarkedForClose) continue;
			grids++;
			// MyPhysicsComponentBase.IsActive, not Physics.RigidBody.IsActive: the latter names
			// HkRigidBody, which drags a HavokWrapper reference into this project for no gain.
			// The managed property forwards to RigidBody.IsActive when there is a rigid body, and
			// to the character/ragdoll proxies otherwise - which grids do not have.
			if (grid.Physics == null) continue;
			withPhysics++;
			if (!grid.Physics.IsActive)
			{
				sleepingBlocks += grid.BlocksCount;
				if (!grid.IsStatic && !RestingHeights.ContainsKey(grid.EntityId))
				{
					double? rest = HeightAboveVoxel(grid.PositionComp.GetPosition());
					if (rest.HasValue)
					{
						// Bounded like every other table here: drop the lot rather than grow without
						// bound. Refilling costs one query per wreck, which is what it cost the first time.
						if (RestingHeights.Count >= MaxRestingHeights) RestingHeights.Clear();
						RestingHeights[grid.EntityId] = rest.Value;
						resting.Add(grid.EntityId + ":" + rest.Value.ToString("F0", CultureInfo.InvariantCulture));
					}
				}
				continue;
			}
			// It is awake, so any cached resting height is stale - the next time it sleeps it may be
			// somewhere else entirely, and that second resting place is a separate observation.
			RestingHeights.Remove(grid.EntityId);
			active++;
			activeBlocks += grid.BlocksCount;
			if (Occupied.Contains(grid.EntityId)) activeOccupied++;
			if (grid.IsStatic) activeStatic++;
			// Only for ACTIVE, non-static grids: a sleeping wreck's height never changes and a station's
			// is meaningless, so paying a voxel query for either would be cost without information.
			if (!grid.IsStatic)
			{
				if (heights.Count >= MaxHeights) heightsTruncated = true;
				else
				{
					double? height = HeightAboveVoxel(grid.PositionComp.GetPosition());
					if (height.HasValue) heights.Add(grid.EntityId + ":" + height.Value.ToString("F0", CultureInfo.InvariantCulture));
				}
			}
		}

		// Logged every report period even when everything is asleep: a silent probe cannot be told
		// apart from one that never ran, and a clean zero is the whole value of this measurement.
		// A silent cap reads as "that is all there was", so it says when it truncated.
		if (heights.Count > 0)
			Console.WriteLine("[ClusterPlugin] physics_census_heights truncated=" + (heightsTruncated ? "true" : "false")
				+ " " + string.Join(",", heights));
		if (resting.Count > 0)
			Console.WriteLine("[ClusterPlugin] physics_census_rest new=" + resting.Count
				+ " cached=" + RestingHeights.Count + " " + string.Join(",", resting));
		Console.WriteLine("[ClusterPlugin] physics_census grids=" + grids + " withPhysics=" + withPhysics
			+ " active=" + active + " activeOccupied=" + activeOccupied
			+ " activePilotless=" + (active - activeOccupied) + " activeStatic=" + activeStatic
			+ " activeBlocks=" + activeBlocks + " sleepingBlocks=" + sleepingBlocks);
	}

	/// <summary>
	/// Metres from a world position to the closest voxel surface point beneath it, or null off-planet.
	/// </summary>
	/// <remarks>
	/// <c>GetClosestSurfacePointGlobal</c> is a heightmap lookup, not a voxel raycast, so it is cheap
	/// enough to call once per active grid once a minute. Off-planet grids return null rather than a
	/// huge number: a distance to a planet a hundred kilometres away is not a clearance, and letting
	/// it into the series would put an unphysical tail on every percentile computed from it.
	/// </remarks>
	private static double? HeightAboveVoxel(Vector3D position)
	{
		MyPlanet planet = MyPlanets.Static?.GetClosestPlanet(position);
		if (planet == null) return null;
		double fromCentre = Vector3D.Distance(position, planet.PositionComp.GetPosition());
		if (fromCentre > planet.AtmosphereRadius) return null;
		return Vector3D.Distance(position, planet.GetClosestSurfacePointGlobal(position));
	}
}

