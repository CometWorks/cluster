using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game.World;
using Shared.Patches;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// T-0292: which partition members move FAST - faster than any partition machinery can follow.
/// A fast member gets a partition of its own that is never merged, adopted into, balance-migrated,
/// pulled, or used as a trigger to load other partitions, until it slows down again.
/// </summary>
/// <remarks>
/// SPEED IS MEASURED FROM POSITIONS OVER TIME, never read from Physics.LinearVelocity or from a
/// speed mod's configured limit (Viktor 2026-09-29): a warp drive (STC 4.0) moves the grid by
/// teleporting it ahead every tick with no physics velocity at all, and speed mods change their
/// limits by dynamic rules mid-flight. Each member's world position is sampled at most once per
/// <see cref="MinSampleSeconds"/>; speed = displacement / elapsed time between two samples.
///
/// THE THRESHOLD FOLLOWS THE SYNC DISTANCE (Viktor 2026-09-29): fast = faster than SyncDistance / 3 m/s,
/// i.e. the member crosses its own sync distance in under 3 s. The sync distance is the one in use,
/// the outermost replication layer (MyLayers, set from the world's SyncDistance at load); the default
/// 3000 m gives 1000 m/s. <c>fast.enterMps</c> "auto" (the default) derives it; a number overrides it
/// (the bench red switches the state off with a huge value). <see cref="Threshold"/> names the source,
/// and the tracker logs it once per value (fast_threshold).
///
/// HYSTERESIS AND DWELL, relative to the threshold T. A member ENTERS fast after <c>fast.enterSamples</c>
/// consecutive samples above T, and LEAVES after <c>fast.exitSamples</c> consecutive samples below
/// <c>fast.exitFraction</c> x T. Defaults 2 samples in, 5 samples below 0.8 T out (1000 / 800 m/s at the
/// default sync distance): a single teleport (respawn, admin move, one warp hop) is one spike and never
/// enters, and a ship cruising at the threshold does not flap.
///
/// MEASUREMENT TOLERANCE. "Faster than T" means faster by more than <c>fast.enterMarginFraction</c> x T
/// (default 0.01, 10 m/s at T = 1000): a speed mod that caps ships at exactly 1000 m/s (Max Speed 1000,
/// the mod chosen for the cluster) holds them AT the threshold, and a position-over-time reading of such a
/// ship scatters around 1000 by the integration and sampling error. Without the margin two readings of
/// 1000.x in a row would put an ordinary max-speed cruiser in the fast state.
///
/// TIME. The node passes GAME time (gameplay frames x 1/60 s), not wall time: positions advance per
/// simulation step, so displacement over game time is the ship's own speed, independent of frame
/// jitter and of the sim-speed ratio. The World Authority's player positions arrive on its own clock,
/// so it passes wall time and <c>holdUnchangedSeconds</c>.
///
/// A member here is what PartitionManager.IsMember counts: top-level grids and UNSEATED characters
/// (a seated pilot is a child of its cockpit and rides its grid). A grid group is fast when any of
/// its members is fast; attached grids move together, so in practice all of them are.
///
/// THE TICK DETECTOR (SE1-0084, <see cref="Step"/>). The samples above put a ship that jumps to warp
/// speed into the fast state 1.8 to 2.7 s late and keep it there ~4.5 s after it stopped, long enough
/// to wake and merge what it passes. So the node also feeds the members' positions every game tick
/// (static grids excluded: they do not move, and a base-heavy node holds hundreds of them):
/// a member enters when each of its last <c>fast.tickSteps</c> (K) steps is faster than the threshold
/// (with the margin), and leaves when each of its last K steps is slower than the exit speed. The
/// minimum, not the sum: one 10 km teleport and two still ticks average 200 km/s, and a single
/// teleport (respawn, admin move, jump drive) or a teleport and a correction on the next tick must
/// never enter. K = 3 rejects both; a warp drive or Trade Lanes steps the same distance every tick.
///
/// Both detectors run: a member is fast when either says so. A mod that hops every Nth tick and sits
/// still in between, or a grid whose server position advances in lumps, never shows K fast steps in a
/// row, and only the samples catch it. When the tick detector had entered and then sees K slow steps,
/// it ends the samples' fast state as well: their 0.9 s windows still span the stop and would hold the
/// member fast for the whole exit dwell. A member the tick detector never entered leaves by the dwell.
/// A member the tick detector has fast but no longer feeds (a grid converted to a station, the detector
/// switched off) leaves its tick state after <see cref="StaleTickSeconds"/>; the samples decide from there.
/// </remarks>
internal sealed class FastMotionTracker
{
	internal const double MinSampleSeconds = 0.9;
	internal const double StaleTickSeconds = 1.0;

	private sealed class State
	{
		public bool Sampled;
		public Vector3D Position;
		public double Time;
		public double Speed;
		public int Above;
		public int Below;
		public bool Fast;
		public double Since;
		// The tick detector: the previous tick's position, and the run lengths of fast and slow steps.
		public long TickFrame = -1;
		public Vector3D TickPosition;
		public double TickSpeed;
		public int FastSteps;
		public int SlowSteps;
		public bool TickFast;
		public bool IsFast => Fast || TickFast;
	}

	private readonly Dictionary<long, State> m_states = new Dictionary<long, State>();
	// The self-test pins the threshold; everything else derives it (null).
	private readonly double? m_enterMps;
	private static double s_logged;

	public FastMotionTracker(double? enterMps = null) => m_enterMps = enterMps;

	/// <summary>Fired on the game thread for every member that entered (true) or left (false) the fast state,
	/// with its speed and the detector that decided ("tick" or "samples").</summary>
	public event Action<long, bool, double, string> Transition;

	public static double EnterMps => Threshold().Mps;
	public static double EnterMarginMps => EnterMps * Fraction("fast.enterMarginFraction");
	public static double ExitMps => EnterMps * Fraction("fast.exitFraction");

	/// <summary>The fast threshold in m/s and where it came from (see the remarks).</summary>
	public static (double Mps, string Source) Threshold()
	{
		string configured = ClusterConfig.Get("fast.enterMps");
		if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
			return (double.Parse(configured, CultureInfo.InvariantCulture), "fast.enterMps=" + configured);
		double sync = MyLayersHelper.SyncDistance();
		string source = "sync distance in use (MyLayers)";
		if (sync <= 0 && MySession.Static?.Settings != null)
		{
			sync = MySession.Static.Settings.SyncDistance;
			source = "world SyncDistance (session settings)";
		}
		if (sync <= 0)
		{
			sync = 3000;
			source = "vanilla default SyncDistance (no session yet)";
		}
		return (sync / 3.0, string.Format(CultureInfo.InvariantCulture, "{0} {1:F0} m / 3", source, sync));
	}

	private static double Fraction(string key) => double.Parse(ClusterConfig.Get(key), CultureInfo.InvariantCulture);
	public static int EnterSamples => Math.Max(1, ClusterConfig.GetInt("fast.enterSamples"));
	public static int ExitSamples => Math.Max(1, ClusterConfig.GetInt("fast.exitSamples"));
	public static int TickSteps => Math.Max(1, ClusterConfig.GetInt("fast.tickSteps"));
	public static bool TickDetector => ClusterConfig.GetBool("fast.tickDetector");

	/// <summary>Start ID in the fast state at POSITION/NOW (T-0292: it arrived fast from another node). It leaves
	/// by the normal exit dwell, measured from here; no Transition fires for the seed itself.</summary>
	public void Seed(long id, Vector3D position, double now, double speed) =>
		m_states[id] = new State { Sampled = true, Position = position, Time = now, Speed = speed, Fast = true, Since = now };

	public bool IsFast(long id) => m_states.TryGetValue(id, out State state) && state.IsFast;
	/// <summary>The latest step's speed while the tick detector has the member fast, else the latest sample's.</summary>
	public double SpeedOf(long id) => m_states.TryGetValue(id, out State state) ? state.TickFast ? state.TickSpeed : state.Speed : 0;
	public int FastCount => m_states.Values.Count(state => state.IsFast);

	private static double LoggedThreshold()
	{
		(double mps, string source) = Threshold();
		if (mps != s_logged)
		{
			s_logged = mps;
			VRage.Utils.MyLog.Default?.WriteLineAndConsole(string.Format(CultureInfo.InvariantCulture,
				"[ClusterPlugin] fast_threshold enterMps={0:F0} (+{1:F0} margin, x{2}) exitMps={3:F0} (x{4}) tickSteps={6}{7} source={5}",
				mps, mps * Fraction("fast.enterMarginFraction"), EnterSamples, mps * Fraction("fast.exitFraction"), ExitSamples, source,
				TickSteps, TickDetector ? "" : " (tick detector off)"));
		}
		return mps;
	}

	/// <summary>
	/// Feed the current member positions. Members not seen again are forgotten (a member that
	/// leaves this node - handover, close - starts from scratch wherever it lands).
	/// </summary>
	/// <param name="holdUnchangedSeconds">For positions that refresh slower than they are sampled (the
	/// World Authority's position registry): a position identical to the last sample is "no new data"
	/// rather than "stopped" until this many seconds have passed, so a refresh cadence of 2 s does not
	/// alternate 0 and 2x the speed and defeat the dwell. 0 (the node's own entities) takes every sample.</param>
	public void Sample(IEnumerable<KeyValuePair<long, Vector3D>> positions, double now, double holdUnchangedSeconds = 0)
	{
		double threshold = m_enterMps ?? LoggedThreshold();
		double enter = threshold * (1 + Fraction("fast.enterMarginFraction")), exit = threshold * Fraction("fast.exitFraction");
		int enterSamples = EnterSamples, exitSamples = ExitSamples;
		HashSet<long> seen = new HashSet<long>();
		foreach ((long id, Vector3D position) in positions)
		{
			seen.Add(id);
			if (!m_states.TryGetValue(id, out State state)) m_states[id] = state = new State();
			if (state.TickFast && now - state.TickFrame / 60.0 > StaleTickSeconds)
			{
				state.TickFast = false;
				if (!state.Fast) Transition?.Invoke(id, false, state.Speed, "samples");
			}
			if (!state.Sampled)
			{
				state.Sampled = true;
				state.Position = position;
				state.Time = now;
				continue;
			}
			double elapsed = now - state.Time;
			if (elapsed < MinSampleSeconds) continue;
			if (holdUnchangedSeconds > 0 && position == state.Position && elapsed < holdUnchangedSeconds) continue;
			state.Speed = Vector3D.Distance(position, state.Position) / elapsed;
			state.Position = position;
			state.Time = now;
			if (!state.Fast)
			{
				state.Above = state.Speed > enter ? state.Above + 1 : 0;
				if (state.Above >= enterSamples)
				{
					state.Fast = true;
					state.Since = now;
					state.Below = 0;
					if (!state.TickFast) Transition?.Invoke(id, true, state.Speed, "samples");
				}
			}
			else
			{
				state.Below = state.Speed < exit ? state.Below + 1 : 0;
				if (state.Below >= exitSamples)
				{
					state.Fast = false;
					state.Since = now;
					state.Above = 0;
					if (!state.TickFast) Transition?.Invoke(id, false, state.Speed, "samples");
				}
			}
		}
		if (seen.Count != m_states.Count)
			foreach (long gone in m_states.Keys.Where(id => !seen.Contains(id)).ToArray())
				m_states.Remove(gone);
	}

	/// <summary>
	/// SE1-0084: one game tick of member positions (see the remarks, THE TICK DETECTOR). <paramref name="frame"/>
	/// is the gameplay frame counter, the clock <see cref="Sample"/> gets in seconds; a step over more than one
	/// frame counts as one step at its average speed. Members are forgotten by <see cref="Sample"/>, not here.
	/// Returns true when a member entered or left the fast state.
	/// </summary>
	public bool Step(IEnumerable<KeyValuePair<long, Vector3D>> positions, long frame)
	{
		double threshold = m_enterMps ?? LoggedThreshold();
		double enter = threshold * (1 + Fraction("fast.enterMarginFraction")), exit = threshold * Fraction("fast.exitFraction");
		int steps = TickSteps;
		bool changed = false;
		foreach ((long id, Vector3D position) in positions)
		{
			if (!m_states.TryGetValue(id, out State state)) m_states[id] = state = new State();
			long frames = state.TickFrame < 0 ? 0 : frame - state.TickFrame;   // 0: the first position
			Vector3D previous = state.TickPosition;
			state.TickFrame = frame;
			state.TickPosition = position;
			if (frames <= 0) continue;
			double speed = Vector3D.Distance(position, previous) * 60.0 / frames;
			state.TickSpeed = speed;
			state.FastSteps = speed > enter ? state.FastSteps + 1 : 0;
			state.SlowSteps = speed < exit ? state.SlowSteps + 1 : 0;
			if (!state.TickFast && state.FastSteps >= steps)
			{
				state.TickFast = true;
				if (state.Fast) continue;
				Transition?.Invoke(id, true, speed, "tick");
				changed = true;
			}
			else if (state.TickFast && state.SlowSteps >= steps)
			{
				state.TickFast = false;
				// The samples restart from here: their window still spans the stop (see the remarks).
				state.Fast = false;
				state.Above = state.Below = 0;
				state.Position = position;
				state.Time = frame / 60.0;
				state.Since = state.Time;
				Transition?.Invoke(id, false, speed, "tick");
				changed = true;
			}
		}
		return changed;
	}
}

/// <summary>
/// SE1-0084: members that keep entering and leaving the fast state. Every transition costs a membership pass and a
/// Gateway pass (both limited to one per 250 ms), so a member flipping all the time is load worth seeing in the log.
/// <see cref="Note"/> takes each transition; a member with <see cref="Threshold"/> or more in the last
/// <see cref="WindowSeconds"/> is reported once per window.
/// </summary>
internal sealed class FastFlapMonitor
{
	internal const double WindowSeconds = 60;
	internal const int Threshold = 10;
	private readonly Dictionary<long, Queue<double>> m_transitions = new Dictionary<long, Queue<double>>();
	private readonly Dictionary<long, double> m_reported = new Dictionary<long, double>();

	/// <summary>Members reported since the start (diag).</summary>
	public long Reports { get; private set; }

	/// <summary>A transition of <paramref name="id"/> at <paramref name="now"/> (seconds). Returns the member's
	/// transitions in the window when it is to be reported now, else 0.</summary>
	public int Note(long id, double now)
	{
		if (!m_transitions.TryGetValue(id, out Queue<double> times)) m_transitions[id] = times = new Queue<double>();
		times.Enqueue(now);
		while (times.Peek() <= now - WindowSeconds) times.Dequeue();
		if (m_transitions.Count > 64)
			foreach (long quiet in m_transitions.Where(x => x.Value.Last() <= now - WindowSeconds).Select(x => x.Key).ToArray())
			{
				m_transitions.Remove(quiet);
				m_reported.Remove(quiet);
			}
		if (times.Count < Threshold || m_reported.TryGetValue(id, out double reported) && now - reported < WindowSeconds) return 0;
		m_reported[id] = now;
		Reports++;
		return times.Count;
	}
}
