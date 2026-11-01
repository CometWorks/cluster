using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// Bring an unpiloted grid to rest where it is (control route <c>/cluster/grid-stop</c>, T-0064).
///
/// A manual space pilot that loses its client mid-leg leaves a hull under thrust override or with the
/// dampeners off, and in space that hull coasts at the speed cap forever. The harness can only reach a
/// grid it has streamed, and a character teleported 15 s ahead of a 98 m/s ship gets a ~30 s flyby
/// through a 3 km sync bubble - w29 lost 64 of 89 such hulls "abandoned in flight" after three
/// pursuit rounds each. The owning node can simply park it: every thruster override cleared, the
/// dampeners on, the physics speed cleared, and any activated AI block deactivated so nothing drives
/// it off again. The pilot then teleports next to a ship that is standing still.
///
/// Refusals mirror <see cref="GridMoveService"/>: a station, a grid someone is sitting in, a grid
/// frozen for a handover. Attached groups are allowed - stopping cannot drag anything anywhere.
///
/// <paramref name="aiOnly"/> is the narrow form for a grid whose seat is taken: it only switches off an
/// activated AI flight block and turns the dampeners on, and leaves speed, overrides and the pilot
/// alone. A harness pilot whose client died leaves its body seated AND its AI behaviour flying the
/// ship; the full stop rightly refuses the seated body, and the ship then flew on with nobody at the
/// controls until a node parked it at 92-100 m/s (CM 2026-09-24: pilot-51 after a harness restart,
/// pilot-5 after a client recycle).
///
/// <paramref name="force"/> is the full stop with a body seated. After the kill -9 of node-5 on
/// 2026-09-27 02:55:48 the hot spare recovery-loaded ten manual space ships with their pilots' bodies in
/// the seats, and the reconnected pilots' inputs no longer reached them: each coasted at a constant
/// 94-99 m/s through every stop_timeout retry for 15+ minutes, and pilot-45's hit an asteroid at 03:13:51.
/// A seated body blocks the full stop and dampeners alone do nothing without thrust, so the operator
/// asks for it explicitly; it clears the speed the same way and leaves the pilot seated.
/// </summary>
public static class GridStopService
{
	public static object Stop(long entityId, bool aiOnly = false, bool force = false)
	{
		if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyCubeGrid grid)
			return new { stopped = false, entityId, reason = "not a grid on this node" };
		if (grid.Closed || grid.MarkedForClose)
			return new { stopped = false, entityId, reason = "grid is closing" };
		if (grid.IsStatic)
			return new { stopped = false, entityId, reason = "static grid; stations do not move" };
		if (ClusterRuntime.IsFrozen(grid.EntityId))
			return new { stopped = false, entityId, reason = "frozen for a handover in progress" };
		long[] pilots = grid.GetFatBlocks<MyCockpit>()
			.Where(cockpit => cockpit.Pilot != null)
			.Select(cockpit => cockpit.Pilot.EntityId).ToArray();
		if (aiOnly)
		{
			float speed = grid.Physics?.LinearVelocity.Length() ?? 0f;
			long[] deactivated = DeactivateAi(grid);
			bool dampenersWereOn = grid.DampenersEnabled;
			if (!dampenersWereOn)
				grid.EnableDampingInternal(true, updateProxy: true);
			return new { stopped = false, aiOnly = true, entityId, speedBefore = speed, aiDeactivated = deactivated, dampenersWereOn, pilots };
		}
		if (pilots.Length > 0 && !force)
			return new { stopped = false, entityId, reason = "a cockpit holds a pilot", pilots };

		float speedBefore = grid.Physics?.LinearVelocity.Length() ?? 0f;
		int overridesCleared = 0;
		int thrustersEnabled = 0;
		foreach (MyThrust thrust in grid.GetFatBlocks<MyThrust>())
		{
			if (thrust.ThrustOverride > 0f)
			{
				thrust.ThrustOverride = 0f;
				overridesCleared++;
			}
			if (!thrust.Enabled)
			{
				thrust.Enabled = true;
				thrustersEnabled++;
			}
		}
		long[] aiDeactivated = DeactivateAi(grid);
		bool dampenersBefore = grid.DampenersEnabled;
		if (!dampenersBefore)
			grid.EnableDampingInternal(true, updateProxy: true);
		grid.Physics?.ClearSpeed();
		float speedAfter = grid.Physics?.LinearVelocity.Length() ?? 0f;
		var position = grid.PositionComp.GetPosition();
		return new
		{
			stopped = true,
			entityId,
			blocks = grid.BlocksCount,
			speedBefore,
			speedAfter,
			overridesCleared,
			thrustersEnabled,
			dampenersWereOn = dampenersBefore,
			aiDeactivated,
			forcedWithPilots = force ? pilots : null,
			position = new[] { position.X, position.Y, position.Z },
		};
	}

	private static long[] DeactivateAi(MyCubeGrid grid)
	{
		var aiDeactivated = new List<long>();
		foreach (MyFlightMovementBlock flight in grid.GetFatBlocks<MyFlightMovementBlock>())
		{
			if (flight.Components.TryGet<IMyAiBlockComponent>(out IMyAiBlockComponent component)
				&& component.IsActivated && component is MyAiBlockComponent ai)
			{
				// Through the synced property, never MyAiBlockComponent.Deactivate(): that one is the
				// AI system's own internal path - it skips the ValueChanged handler, so
				// MyAiBlockSystem.DeactivateComponent never runs and the grid's AI system keeps this
				// block registered as the active Movement component. The next ActivateBehavior=true
				// then finds the stale entry IS this component, calls Deactivate() on it and the
				// block switches straight back off: 121 "AI behaviour did not activate" rounds on
				// 2b91c18 in one afternoon. The property setter runs the handler, unregisters the
				// component and replicates the value to every client.
				ai.IsActivated = false;
				aiDeactivated.Add(flight.EntityId);
			}
		}
		return aiDeactivated.ToArray();
	}
}
