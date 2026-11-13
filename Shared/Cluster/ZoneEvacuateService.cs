using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// One-off admin step (control route <c>/cluster/zone-evacuate</c>, SE-1417): moves every ship and every
/// character out of the admin safe zone around a station, each straight away from the zone's center to
/// just outside its edge. A ship docked to the station, or with landing gear locked to it or to a voxel,
/// is released first. Characters get their helmet closed, since outside is vacuum.
///
/// A station patch is refused while a ship is docked to a connector it removes, and a docked ship can only
/// be undocked from its cockpit or by the station's release timer. This route clears the zone for such a
/// patch while the cluster runs.
///
/// What stays: static grids and whatever is mechanically attached to one (the station, its shipyard, trade
/// lane gates, shipyard arms on rotors), grids frozen for a handover, the ids in <c>exclude</c>, and
/// characters seated in the station's seats. A ship with a seated pilot moves with the pilot, as the vanilla
/// admin teleport does.
///
/// The route runs <see cref="Release"/> and <see cref="Move"/> as two game-thread steps with a pause
/// between, so the released links are gone before the move. A ship still attached at the move stays and is
/// reported; calling the route again picks it up.
/// </summary>
public static class ZoneEvacuateService
{
	private const double MaxExtraDistance = 2000;

	public sealed record Plan(object Error, MySafeZone Zone, MyCubeGrid Station, List<MyCubeGrid> Ships, List<MyCharacter> Characters);

	/// <summary>The station grid by id, or by its exact display name when <paramref name="gridId"/> is 0.</summary>
	public static Plan Find(long gridId, string name, ISet<long> exclude)
	{
		MyCubeGrid station = gridId != 0
			? MyEntities.GetEntityById(gridId) as MyCubeGrid
			: MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => g.IsStatic && g.DisplayName == name);
		if (station == null || station.Closed)
			return new Plan(new { evacuated = false, reason = "station grid not on this node", gridId, name }, null, null, null, null);
		Vector3D at = station.PositionComp.WorldAABB.Center;
		MySafeZone zone = MySessionComponentSafeZones.SafeZones
			.Where(z => z.Enabled && !z.MarkedForClose && z.SafeZoneBlockId == 0 && z.Contains(at))
			.OrderBy(z => Vector3D.DistanceSquared(z.PositionComp.GetPosition(), at))
			.FirstOrDefault();
		if (zone == null)
			return new Plan(new { evacuated = false, reason = "no enabled admin safe zone holds the station", gridId = station.EntityId }, null, station, null, null);

		var ships = new List<MyCubeGrid>();
		var characters = new List<MyCharacter>();
		var seen = new HashSet<long>();
		foreach (MyEntity entity in MyEntities.GetEntities())
		{
			if (entity.Closed || entity.MarkedForClose || exclude.Contains(entity.EntityId))
				continue;
			if (entity is MyCharacter character)
			{
				if (!character.IsDead && character.Parent == null && zone.Contains(character.PositionComp.GetPosition()))
					characters.Add(character);
				continue;
			}
			if (entity is not MyCubeGrid grid || grid.IsStatic || !zone.Contains(grid.PositionComp.WorldAABB.Center))
				continue;
			// One entry per mechanical group (a ship with its rotor and piston subgrids), by its biggest grid
			List<MyCubeGrid> mechanical = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid);
			if (mechanical.Any(g => g.IsStatic) || !seen.Add(mechanical.Min(g => g.EntityId)))
				continue;
			MyCubeGrid main = mechanical.OrderByDescending(g => g.BlocksCount).ThenBy(g => g.EntityId).First();
			if (!exclude.Contains(main.EntityId))
				ships.Add(main);
		}
		return new Plan(null, zone, station, ships, characters);
	}

	/// <summary>Undocks the ships from the station and lets go of their landing gear on it or on a voxel.</summary>
	public static List<object> Release(Plan plan, bool dryRun)
	{
		var released = new List<object>();
		foreach (MyCubeGrid ship in plan.Ships)
		{
			List<MyCubeGrid> own = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(ship);
			foreach (MyCubeGrid grid in own)
			{
				foreach (MyShipConnector connector in grid.GetFatBlocks<MyShipConnector>())
				{
					if (!connector.Connected || connector.Other?.CubeGrid is not { } other || own.Contains(other) || !Anchored(other))
						continue;
					released.Add(new { ship = ship.EntityId, block = connector.EntityId, kind = "connector", to = other.EntityId, toName = other.DisplayName });
					if (!dryRun)
						((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Disconnect();
				}
				foreach (MyLandingGear gear in grid.GetFatBlocks<MyLandingGear>())
				{
					MyEntity to = (((SpaceEngineers.Game.ModAPI.IMyLandingGear)gear).GetAttachedEntity() as MyEntity)?.GetTopMostParent();
					if (to == null || to is MyCubeGrid g && (own.Contains(g) || !Anchored(g)))
						continue;
					released.Add(new { ship = ship.EntityId, block = gear.EntityId, kind = "landing gear", to = to.EntityId, toName = to.DisplayName, working = gear.IsWorking });
					if (!dryRun)
						((SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear)gear).Unlock();
				}
			}
		}
		return released;
	}

	/// <summary>Moves the ships and characters out, each to the nearest free spot on its ray from the zone's center.</summary>
	public static object Move(Plan plan, double margin, bool dryRun)
	{
		Vector3D center = plan.Zone.PositionComp.GetPosition();
		double radius = plan.Zone.Shape == MySafeZoneShape.Sphere ? plan.Zone.Radius : plan.Zone.Size.Length() / 2;
		var taken = new List<BoundingSphereD>();
		var ships = new List<object>();
		var characters = new List<object>();
		var moved = new HashSet<long>();
		foreach (MyCubeGrid ship in plan.Ships)
		{
			if (ship.Closed || ship.MarkedForClose || moved.Contains(ship.EntityId))
				continue;
			List<MyCubeGrid> group = MyCubeGridGroups.Static.Physical.GetGroupNodes(ship);
			if (group.FirstOrDefault(Anchored) is { } stuck)
			{
				// In a dry run the docked ships are still attached: plan their move as if released
				if (!dryRun)
				{
					ships.Add(new { entityId = ship.EntityId, name = ship.DisplayName, moved = false, reason = "still attached to " + stuck.DisplayName, attachedTo = stuck.EntityId });
					continue;
				}
				group = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(ship);
			}
			if (group.Any(g => ClusterRuntime.IsFrozen(g.EntityId)))
			{
				ships.Add(new { entityId = ship.EntityId, name = ship.DisplayName, moved = false, reason = "frozen for a handover in progress" });
				continue;
			}
			BoundingBoxD box = BoundingBoxD.CreateInvalid();
			foreach (MyCubeGrid grid in group)
				box.Include(grid.PositionComp.WorldAABB);
			BoundingSphereD sphere = BoundingSphereD.CreateFromBoundingBox(box);
			Vector3D? to = Spot(center, radius, margin, sphere, new HashSet<long>(group.Select(g => g.EntityId)), taken, plan.Station);
			if (to == null)
			{
				ships.Add(new { entityId = ship.EntityId, name = ship.DisplayName, moved = false, reason = $"no free spot within {MaxExtraDistance} m outside the zone" });
				continue;
			}
			string[] pilots = group.SelectMany(g => g.GetFatBlocks<MyCockpit>()).Where(c => c.Pilot != null)
				.Select(c => c.Pilot.DisplayName).ToArray();
			taken.Add(new BoundingSphereD(to.Value, sphere.Radius));
			foreach (MyCubeGrid grid in group)
				moved.Add(grid.EntityId);
			if (!dryRun)
			{
				MatrixD matrix = ship.WorldMatrix;
				matrix.Translation += to.Value - sphere.Center;
				foreach (MyCubeGrid grid in group)
					grid.Physics?.ClearSpeed();
				ship.Teleport(matrix);
			}
			ships.Add(new
			{
				entityId = ship.EntityId, name = ship.DisplayName, moved = !dryRun, grids = group.Count, pilots,
				from = Round(sphere.Center), to = Round(to.Value), fromZoneCenter = Math.Round(Vector3D.Distance(center, to.Value)),
			});
		}
		foreach (MyCharacter character in plan.Characters)
		{
			if (character.Closed || character.MarkedForClose || character.IsDead || character.Parent != null)
				continue;
			var sphere = new BoundingSphereD(character.PositionComp.WorldAABB.Center, 1.5);
			Vector3D? to = Spot(center, radius, margin, sphere, new HashSet<long> { character.EntityId }, taken, plan.Station);
			if (to == null)
			{
				characters.Add(new { entityId = character.EntityId, name = character.DisplayName, moved = false, reason = $"no free spot within {MaxExtraDistance} m outside the zone" });
				continue;
			}
			bool helmet = character.OxygenComponent?.HelmetEnabled ?? true;
			taken.Add(new BoundingSphereD(to.Value, sphere.Radius));
			if (!dryRun)
			{
				if (character.IsOnLadder)
					character.GetOffLadder();
				MatrixD matrix = character.WorldMatrix;
				matrix.Translation += to.Value - sphere.Center;
				character.Physics?.ClearSpeed();
				character.Teleport(matrix);
				// The ModAPI call raises the vanilla event, which the clients get too
				if (!helmet)
					((VRage.Game.ModAPI.Interfaces.IMyControllableEntity)character).SwitchHelmet();
			}
			characters.Add(new
			{
				entityId = character.EntityId, name = character.DisplayName, moved = !dryRun, closesHelmet = !helmet,
				from = Round(sphere.Center), to = Round(to.Value),
			});
		}
		return new { ships, characters };
	}

	/// <summary>A static grid, or a grid mechanically attached to one: a station part, never moved.</summary>
	private static bool Anchored(MyCubeGrid grid) =>
		grid.IsStatic || MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid).Any(g => g.IsStatic);

	/// <summary>
	/// Where the sphere's center goes: on the ray from the zone's center through it, the sphere just outside
	/// the zone, then further out in steps until nothing else is there (grids, characters, voxels, the spots
	/// given out before). A sphere at the zone's center goes along the station's up.
	/// </summary>
	private static Vector3D? Spot(Vector3D center, double radius, double margin, BoundingSphereD sphere, ISet<long> skip,
		List<BoundingSphereD> taken, MyCubeGrid station)
	{
		Vector3D direction = sphere.Center - center;
		direction = direction.LengthSquared() < 1 ? station.WorldMatrix.Up : Vector3D.Normalize(direction);
		double step = Math.Max(10, sphere.Radius);
		var near = new List<MyEntity>();
		for (double extra = 0; extra <= MaxExtraDistance; extra += step)
		{
			var candidate = new BoundingSphereD(center + direction * (radius + sphere.Radius + margin + extra), sphere.Radius);
			if (taken.Any(t => t.Intersects(candidate)))
				continue;
			var query = new BoundingSphereD(candidate.Center, candidate.Radius + margin);
			near.Clear();
			MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref query, near);
			if (!near.Any(e => Blocks(e, query, skip)))
				return candidate.Center;
		}
		return null;
	}

	private static bool Blocks(MyEntity entity, BoundingSphereD query, ISet<long> skip)
	{
		if (skip.Contains(entity.EntityId) || entity.Closed || entity.MarkedForClose)
			return false;
		switch (entity)
		{
			case MyPlanet planet:
				Vector3D surface = planet.GetClosestSurfacePointGlobal(query.Center);
				Vector3D planetCenter = planet.PositionComp.GetPosition();
				return Vector3D.Distance(query.Center, surface) < query.Radius
					|| Vector3D.DistanceSquared(query.Center, planetCenter) < Vector3D.DistanceSquared(surface, planetCenter);
			case MyVoxelBase or MyCubeGrid or MyCharacter:
				return entity.PositionComp.WorldAABB.Intersects(query);
			default:
				return false;
		}
	}

	private static double[] Round(Vector3D v) => new[] { Math.Round(v.X, 1), Math.Round(v.Y, 1), Math.Round(v.Z, 1) };
}
