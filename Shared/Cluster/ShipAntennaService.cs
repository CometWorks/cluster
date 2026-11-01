using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// T-0186: keep a ship's antenna ON only while the ship is in use, so idle hulls cost nothing.
///
/// WHY THIS EXISTS. Turning every ship antenna off cluster-wide bought a real saving -
/// receiverSources.total fell 2012 -> 565 per node (-72%), and the client main thread had been
/// spending 8 of 8 profiler samples inside MyRadioBroadcaster.UpdateReceiversInRange. It also
/// stranded pilots' own ships: with its antenna off a ship leaves every client's receiver set and
/// the client falls back to proximity streaming, so "under way at N m/s with nobody aboard" went
/// from 0-2 per 10 min to 73-93. Viktor's instruction is to have both: "turn the antenna ON only
/// on the ships currently piloted. Ships not currently piloted must have their antennas turned OFF
/// by some automation."
///
/// WHY A PERIODIC RECONCILE AND NOT EVENTS OR AN IN-WORLD BLOCK.
///   * An Event Controller block wiring cockpit occupancy to the antenna (Viktor's example) has to
///     be BUILT INTO every hull - ~1500 free hulls plus 393 Visitor Ions plus anything spawned
///     later - and drifts per ship. This covers existing grids and cannot drift.
///   * Hooking MyCockpit.OnPilotAttached and patching RemovePilot would be event-driven and
///     cheaper, but a missed edge leaves a ship wrong until someone sits in it again. Reconciling
///     is self-healing: a dropped edge, a grid that changed node, a ship that was welded a new
///     cockpit - all correct themselves on the next pass. Same reasoning as
///     <see cref="RemotePlayerMirrorService"/>.
///
/// THE PREDICATE IS THE OPEN QUESTION, SO IT IS CONFIGURATION, NOT A CONSTANT. Occupancy alone has
/// a defect that shows up in exactly the case this ticket is about: the failure is a ship "under
/// way with NOBODY ABOARD", so an occupancy rule switches the antenna off at the precise moment
/// the ship becomes a runaway and the harness needs to find it. CLUSTER_SHIP_ANTENNA_RULE:
///   off       (default) - do nothing at all. The world keeps whatever bin/antenna-ab set.
///   occupied  - Viktor's rule as literally stated.
///   grace     - occupied, plus CLUSTER_SHIP_ANTENNA_GRACE seconds after the pilot leaves.
///   moving    - occupied, OR the grid is moving faster than CLUSTER_SHIP_ANTENNA_SPEED m/s.
///   assigned  - occupied, OR recently occupied (grace), OR moving. Viktor 2026-09-22, after the
///               measurement below: keep the antenna on for a ship with an assigned or
///               recently-aboard pilot REGARDLESS OF SPEED.
///
/// WHY `assigned` EXISTS, measured rather than argued. On 2026-09-22 the `moving` rule was verified
/// to act correctly - rule=moving on every node, non-zero writes, 13-28 antennas on per node, zero
/// failures, and the falsifier (antenna proxies must not be zero cluster-wide) passing at 70 per
/// node - and the runaway rate did NOT move: 52.7 events per 10 min over a window bin/window-check
/// certified clean, against a pre-sweep baseline of 0-2. 95% of those events are a pilot failing to
/// board a ship that is ALREADY under way, and under `moving` such a ship already has its antenna
/// on, so visibility-while-moving was never the binding constraint.
///
/// `moving` leaves a hole that `assigned` closes: the instant a pilot leaves a STATIONARY ship, the
/// antenna goes off (there is no grace term in the `moving` branch), and it does not come back
/// until the grid has drifted past the speed threshold AND the next pass notices. The ship is dark
/// for that whole window, which is when the client drops it from its receiver set.
///               This is the one that keeps a runaway visible: a parked hull is stationary and
///               unoccupied, a runaway is moving, and the two are exactly what we want to separate.
/// DEFAULT off, deliberately: this service writes to the world, and a rule nobody chose should not
/// start doing that on deploy. Picking the rule is a config change, not a rebuild.
/// </summary>
public static class ShipAntennaService
{
	/// <summary>Reconcile cadence. Antenna state follows a human sitting down; sub-second is pointless.</summary>
	private static readonly double IntervalMilliseconds = Seconds("CLUSTER_SHIP_ANTENNA_SECONDS", 5.0) * 1000.0;

	private static readonly string Rule =
		(Environment.GetEnvironmentVariable("CLUSTER_SHIP_ANTENNA_RULE") ?? "off").Trim().ToLowerInvariant();

	private static readonly double GraceSeconds = Seconds("CLUSTER_SHIP_ANTENNA_GRACE", 120.0);

	/// <summary>Above this speed a grid counts as under way. 1 m/s is well clear of drift and jitter.</summary>
	private static readonly double SpeedThreshold = Seconds("CLUSTER_SHIP_ANTENNA_SPEED", 1.0);

	private static double Seconds(string name, double fallback)
	{
		string raw = Environment.GetEnvironmentVariable(name);
		return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			&& value >= 0 ? value : fallback;
	}

	/// <summary>
	/// What this node last SET on each grid, kept ONLY so a transition is reported once rather than
	/// every pass. It is deliberately NOT the input to the write decision - see Update(), which
	/// compares against the blocks themselves. Believing this dictionary is what made the
	/// 2026-09-20 arm inert: the service set an antenna on, the harness set it off on the next
	/// boarding, and the service never looked again because its own memory said "already on".
	/// </summary>
	private static readonly Dictionary<long, bool> Reported = new();

	/// <summary>When each grid was last seen occupied, for the `grace` rule.</summary>
	private static readonly Dictionary<long, double> LastOccupied = new();

	/// <summary>Reconcile passes and writes since start, for /cluster/ship-antennas.</summary>
	private static long m_passes, m_writes, m_failures, m_skipped, m_skippedByGuard;

	private static double m_next;

	public static void Update()
	{
		if (Rule == "off") return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (now < m_next) return;
		m_next = now + IntervalMilliseconds;

		m_passes++;
		var seen = new HashSet<long>();
		foreach (MyCubeGrid grid in Candidates())
		{
			seen.Add(grid.EntityId);
			// T-0392: a grid mid-handover is not ours to reconcile. Kept in `seen` so its bookkeeping
			// (LastOccupied: the grace term) survives an abort that thaws it back here.
			if (Skips(grid.EntityId, out bool byGuard)) { m_skipped++; if (byGuard) m_skippedByGuard++; continue; }
			bool occupied = Occupied(grid);
			if (occupied) LastOccupied[grid.EntityId] = now;
			bool wanted = Wanted(grid, occupied, now);

			// THE WORLD IS THE INPUT, NOT OUR MEMORY OF IT. Anything else writes these blocks too -
			// a player at a terminal, bin/antenna-ab, and the regression harness on every boarding
			// and every flight start (endurance.py, gameplay.py: set_antenna(..., enabled=...)).
			// Against a cache this service goes quiet after one write and the rule stops holding;
			// against the blocks it re-asserts on the next pass, which is the self-healing property
			// the rest of this class is designed around.
			// ON needs EVERY antenna enabled; OFF needs every one of them disabled. One boolean
			// could not say both: a hull whose second antenna was switched back on read as "not
			// on", that equalled wanted=false, and the pass skipped it - so the half-on hull went
			// on broadcasting, which is the receiver-set cost this rule exists to remove (T-0204).
			(bool anyOn, bool allOn) = Current(grid);
			if (wanted ? allOn : !anyOn) { Reported[grid.EntityId] = wanted; continue; }
			try
			{
				AntennaGraphService.SetAntennaEnabled(grid.EntityId, wanted);
				m_writes++;
				// One line per transition, not per pass: a ship flying for an hour is one "on".
				if (!Reported.TryGetValue(grid.EntityId, out bool before) || before != wanted)
					Console.WriteLine($"ship antenna {(wanted ? "on" : "off")} for grid {grid.EntityId}"
						+ $" ({grid.DisplayName}): rule={Rule} occupied={occupied} speed={Speed(grid):F1} m/s");
				Reported[grid.EntityId] = wanted;
			}
			catch (Exception)
			{
				// A grid that lost its last antenna, or went away mid-pass. Leave it unrecorded so
				// the next pass retries rather than believing a write that did not happen.
				m_failures++;
				Reported.Remove(grid.EntityId);
			}
		}

		// A grid that left this node takes its bookkeeping with it: the node that has it now owns
		// its antenna, and a stale entry here would suppress the first REPORT after it came back.
		foreach (long gone in Reported.Keys.Where(id => !seen.Contains(id)).ToArray())
		{
			Reported.Remove(gone);
			LastOccupied.Remove(gone);
		}
	}

	/// <summary>
	/// The grid's antenna state right now, read from the blocks rather than from our memory of
	/// them: <c>AnyOn</c> is true when at least one antenna is enabled, <c>AllOn</c> when the grid
	/// has antennas and every one of them is. A half-applied state (a player switched one back on)
	/// is neither, which is what lets the caller correct it in both directions instead of
	/// accepting it. A grid with no antennas at all is (false, false).
	/// </summary>
	private static (bool AnyOn, bool AllOn) Current(MyCubeGrid grid)
	{
		bool seen = false, any = false, all = true;
		foreach (MyCubeBlock block in grid.GetFatBlocks())
		{
			if (block is not (MyRadioAntenna or MyLaserAntenna)) continue;
			seen = true;
			if (((MyFunctionalBlock)block).Enabled) any = true; else all = false;
		}
		return (any, seen && all);
	}

	private static bool Wanted(MyCubeGrid grid, bool occupied, double now)
	{
		double? lastOccupied = LastOccupied.TryGetValue(grid.EntityId, out double left) ? left : null;
		return WantedBy(Rule, occupied, Speed(grid), lastOccupied, now, GraceSeconds, SpeedThreshold);
	}

	/// <summary>
	/// The rule itself, with every input passed in: the decision is what T-0186 is ABOUT, and it was
	/// previously reachable only through a live MyCubeGrid, so nothing tested it. `lastOccupied` is
	/// null when the ship has never been occupied in this process; `now` and `lastOccupied` are both
	/// in milliseconds, `grace` is in seconds.
	/// </summary>
	internal static bool WantedBy(string rule, bool occupied, double speed, double? lastOccupied,
		double now, double grace, double speedThreshold)
	{
		if (occupied) return true;
		bool inGrace = lastOccupied is { } left && now - left < grace * 1000.0;
		switch (rule)
		{
			case "grace":
				return inGrace;
			case "moving":
				return speed > speedThreshold;
			case "assigned":
				// The UNION, deliberately, not a new third condition: a ship is interesting while
				// somebody is aboard, for a bounded time after they leave, and whenever it is under
				// way. Bounded by grace so an abandoned hull still goes quiet - "assigned" must not
				// become "always on", which would give back the whole receiver-set saving
				// (2012 -> 1718 sources per node, -15%) that turning ship antennas off bought.
				return speed > speedThreshold || inGrace;
			default:
				return false;   // "occupied", and any unknown rule: the conservative answer
		}
	}

	/// <summary>
	/// T-0392: grids this pass must not write. (1) A sliced import activation is open for it
	/// (<see cref="ImportActivationGuard"/>): it was Added by an early slice, reads unoccupied (pilots
	/// are reseated at commit) and speed 0 (frozen), so the rule switched its antenna off before the
	/// final frame collected the round-trip builders - live 2026-10-05 00:22:56, residuals=7
	/// 'CubeBlock/Enabled: true != false', handover aborted. (2) It is frozen by a handover on either
	/// side (source between capture and release, target until commit): the same false 'unoccupied,
	/// stopped' reading, and the node that ends up owning it reconciles it after the commit.
	/// </summary>
	internal static bool Skips(long gridId) => ImportActivationGuard.IsOpen(gridId) || ClusterRuntime.IsFrozen(gridId);

	/// <summary>
	/// <see cref="Skips(long)"/> as the reconcile pass asks it: the same answer, and
	/// <paramref name="byGuard"/> says the guard (not the frozen mark) decided it, counted as a
	/// <see cref="GuardUser.ShipAntenna"/> skip for GET /cluster/import-guard.
	/// </summary>
	internal static bool Skips(long gridId, out bool byGuard)
	{
		byGuard = ImportActivationGuard.IsOpenFor(GuardUser.ShipAntenna, gridId);
		return byGuard || ClusterRuntime.IsFrozen(gridId);
	}

	private static double Speed(MyCubeGrid grid)
		=> grid.Physics == null ? 0.0 : grid.Physics.LinearVelocity.Length();

	private static bool Occupied(MyCubeGrid grid)
		=> grid.GetFatBlocks().OfType<MyCockpit>().Any(seat => seat.Pilot != null);

	/// <summary>
	/// The grids this service is allowed to touch: SHIPS with at least one antenna and at least one
	/// cockpit. The cockpit requirement is what keeps stations out - a Space Test Platform has
	/// antennas and no pilot, and switching the platform backbone off would take the whole world's
	/// antenna graph with it (the platforms are what keeps it one component). IsStatic is checked
	/// too, because a ship converted to a station is a station.
	/// </summary>
	private static IEnumerable<MyCubeGrid> Candidates()
	{
		foreach (MyEntity entity in MyEntities.GetEntities())
		{
			if (entity is not MyCubeGrid grid || grid.MarkedForClose || grid.IsStatic) continue;
			bool antenna = false, cockpit = false;
			foreach (MyCubeBlock block in grid.GetFatBlocks())
			{
				antenna |= block is MyRadioAntenna or MyLaserAntenna;
				cockpit |= block is MyCockpit;
				if (antenna && cockpit) break;
			}
			if (antenna && cockpit) yield return grid;
		}
	}

	public static object Status()
	{
		return new
		{
			rule = Rule,
			graceSeconds = GraceSeconds,
			speedThreshold = SpeedThreshold,
			passes = m_passes,
			writes = m_writes,
			failures = m_failures,
			skippedInHandover = m_skipped,
			skippedByGuard = m_skippedByGuard,
			tracked = Reported.Count,
			on = Reported.Count(pair => pair.Value)
		};
	}
}
