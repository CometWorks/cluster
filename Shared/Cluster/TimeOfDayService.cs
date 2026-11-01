using System;
using System.Globalization;
using System.Text.Json;
using Sandbox.Game.World;
using Sandbox.Game.WorldEnvironment;

namespace ClusterPlugin;

/// <summary>
/// WorldAuthority.md §8.2: the admin menu's time of day as WA-owned state with delta semantics.
///
/// Vanilla implements the slider by rebasing <see cref="MySession.ElapsedGameTime"/> against a
/// frozen first-capture anchor (<c>OriginalTime</c>) on whatever server the admin is attached to.
/// In a cluster that is wrong twice over: a regular node's game time is not its own
/// (SessionClockPatch follows the WA clock), and the rebase silently discards the time elapsed
/// between sets - re-sending even the same value rewinds the sun ("loopback").
///
/// The cluster never runs the vanilla rebase anywhere. A node forwards the request to the WA; the
/// WA applies a pure delta - <c>ElapsedGameTime += (new − last)·60 s</c> - and accumulates it in
/// <see cref="OffsetMilliseconds"/> (the persistent <c>SunTimeOffset</c>). The offset travels to
/// nodes as one ordered, sequence-numbered snapshot on the global state channel and is applied by
/// SessionClockPatch ON TOP of the slew-only replication clock, so an admin set steps game time
/// exactly once per node and never steps the replication timestamp (Partitioning.md §5: "slewed,
/// never stepped"). After applying a step each node re-raises the unchanged vanilla
/// UpdateTimeOfDayClient broadcast to its own clients with the post-step authoritative ticks -
/// retail clients need no change. <c>SunTimeOffset</c> + the last slider value persist as a
/// section of the WA global save; the applied adjustment also survives inside the persisted
/// ElapsedGameTime, so a restore never re-applies it.
/// </summary>
public static class TimeOfDayService
{
	private const string Subsystem = "time-of-day";

	private static readonly object Sync = new object();
	private static string m_snapshot = JsonSerializer.Serialize(new TimeOfDaySnapshot(0f, 0));
	private static float m_timeOfDay;
	// The accumulated clock adjustment (WorldAuthority.md §8.2 SunTimeOffset), authoritative on the
	// WA, replicated to nodes via the subsystem snapshot. Always >= 0 and non-decreasing: a set
	// that would move time backwards is served whole sun rotations forward instead - the sun angle
	// is periodic, the clock only moves one way.
	private static long m_offsetMilliseconds;
	private static bool m_publishPending;

	/// <summary>
	/// A dragged slider delivers a value per frame, and each one is a whole clock change the entire
	/// cluster has to chase. Only the value the admin came to rest on is a real request, so requests
	/// are collected and applied once the stream has been quiet this long.
	/// </summary>
	private const long SettleMilliseconds = 1000;
	private static float? m_requested;
	private static long m_requestedAt;

	public sealed record TimeOfDaySnapshot(float TimeOfDay, long OffsetMilliseconds);

	/// <summary>The node-local view of the accumulated offset, read by SessionClockPatch every frame.</summary>
	public static long OffsetMilliseconds { get { lock (Sync) return m_offsetMilliseconds; } }

	internal static void Install()
	{
		MyTimeOfDayHelperSeams.ClusterRouteTimeOfDay = Route;
		GlobalStateChannel.RegisterCommandHandler(Subsystem, HandleCommand, journaled: true);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ApplySnapshot);
	}

	/// <summary>
	/// True when the cluster took the change over - always, when clustered: the vanilla rebase body
	/// must never run on any cluster role. The WA collects the request for settling; a node hands it
	/// to the WA and applies nothing locally, because its own clock is not its to move.
	/// </summary>
	private static bool Route(float time)
	{
		if (!ClusterRuntime.IsClustered || !float.IsFinite(time)) return false;
		if (ClusterRuntime.IsWorldAuthority)
		{
			Request(time);
			return true;
		}
		// Never block on the WA round trip: the vanilla admin event delivers this on the game
		// thread, and a blocking wait stalls the whole node for the timeout. Fire and log - the
		// admin sees the result through the sun itself, and a lost forward is re-draggable.
		System.Threading.Tasks.Task.Run(() =>
		{
			try
			{
				GlobalStateChannel.SendCommand(Subsystem, time.ToString("R", CultureInfo.InvariantCulture));
				Console.WriteLine($"[ClusterPlugin] time-of-day forwarded minutes={time.ToString("R", CultureInfo.InvariantCulture)}");
			}
			catch (Exception e)
			{
				Console.WriteLine("[ClusterPlugin] time-of-day forward failed: " + SecretRedactor.Redact(e.Message));
			}
		});
		return true;
	}

	/// <summary>
	/// The §8.2 delta application, on the WA's game thread: ElapsedGameTime += (new − last)·60 s,
	/// never a rebase against a captured anchor. Re-setting the same value is a genuine no-op
	/// (the settle gate drops it), and time elapsed between sets is never discarded. A backward
	/// delta is served whole sun rotations forward - same sky, a clock that still only moves one way.
	/// </summary>
	private static void ApplyOnAuthority(float time)
	{
		MySession session = MySession.Static;
		if (session == null) return;
		long deltaMilliseconds = DeltaMilliseconds(m_timeOfDay, time, session.Settings.SunRotationIntervalMinutes);
		// Offset first, elapsed second: AuthorityClockMilliseconds is ElapsedGameTime − offset read
		// from heartbeat threads, and this order makes a racing read UNDER-report - which the
		// Gateway's monotonic clamp ignores. The other order over-reports by the delta and would
		// level-shift the cluster's replication clock permanently.
		Publish(time, m_offsetMilliseconds + deltaMilliseconds);
		session.ElapsedGameTime += TimeSpan.FromMilliseconds(deltaMilliseconds);
	}

	/// <summary>
	/// The §8.2 delta in milliseconds: (to − from)·60 s, with a backward move served whole sun
	/// rotations forward - the sun angle is periodic, the clock only moves one way. Pure, for the
	/// time-of-day self-test section.
	/// </summary>
	internal static long DeltaMilliseconds(float from, float to, double periodMinutes)
	{
		double deltaMinutes = (double)to - from;
		double period = Math.Max(1, periodMinutes);
		if (deltaMinutes < 0)
			deltaMinutes += Math.Ceiling(-deltaMinutes / period) * period;
		return checked((long)Math.Round(deltaMinutes * 60000));
	}

	/// <summary>Records the value the admin is currently asking for; ApplySettled commits it.</summary>
	private static void Request(float time)
	{
		lock (Sync)
		{
			m_requested = time;
			m_requestedAt = Environment.TickCount64;
		}
	}

	/// <summary>
	/// Applies the settled request, on the game thread. Returns while the admin is still moving the
	/// slider; a request repeating the applied value is dropped - the §8.2 no-loopback rule.
	/// </summary>
	private static void ApplySettled()
	{
		float time;
		lock (Sync)
		{
			if (m_requested == null || Environment.TickCount64 - m_requestedAt < SettleMilliseconds) return;
			time = m_requested.Value;
			m_requested = null;
			if (time == m_timeOfDay) return;
		}
		ApplyOnAuthority(time);
	}

	/// <summary>WA-side command handler for a node's forwarded request (channel receive thread).</summary>
	private static string HandleCommand(string origin, string payload)
	{
		if (!float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out float time) || !float.IsFinite(time))
			throw new InvalidOperationException("Invalid time of day");
		Request(time);
		return "{}";
	}

	private static void Publish(float time, long offsetMilliseconds)
	{
		string snapshot = JsonSerializer.Serialize(new TimeOfDaySnapshot(time, offsetMilliseconds));
		lock (Sync)
		{
			m_timeOfDay = time;
			m_offsetMilliseconds = offsetMilliseconds;
			if (m_snapshot == snapshot) return;
			m_snapshot = snapshot;
			// The WA has no partition clients, but publishing locally keeps Status()/TimeOfDay
			// coherent on every role through the one code path.
			m_publishPending = true;
		}
		GlobalStateChannel.Publish(Subsystem, snapshot);
	}

	/// <summary>
	/// Node/replica + resync handler: adopts the WA's offset and slider value. The ordered,
	/// generation-fenced channel delivers each change exactly once in sequence - the "one ordered
	/// clock step per set" of §8.2. SessionClockPatch applies the new offset on the next frame;
	/// Update() then re-raises the vanilla client broadcast with post-step ticks.
	/// </summary>
	private static void ApplySnapshot(string payload)
	{
		TimeOfDaySnapshot snapshot = payload == null || payload.Length > 1024
			? null : JsonSerializer.Deserialize<TimeOfDaySnapshot>(payload);
		if (snapshot == null || !float.IsFinite(snapshot.TimeOfDay) || snapshot.OffsetMilliseconds < 0)
			throw new InvalidOperationException("Invalid time of day snapshot");
		lock (Sync)
		{
			m_snapshot = payload;
			m_timeOfDay = snapshot.TimeOfDay;
			m_publishPending |= m_offsetMilliseconds != snapshot.OffsetMilliseconds;
			m_offsetMilliseconds = snapshot.OffsetMilliseconds;
		}
	}

	/// <summary>
	/// Tells this node's clients the game time after an admin set landed here - one vanilla
	/// UpdateTimeOfDayClient broadcast per change, never per clock correction (the old
	/// publish-on-every-step is what made the sun snap backward at the clock-sample cadence).
	/// The broadcast waits until SessionClockPatch has actually applied the new offset, so the
	/// ticks it carries are post-step.
	/// </summary>
	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		if (ClusterRuntime.IsWorldAuthority) ApplySettled();
		float time;
		lock (Sync)
		{
			if (!m_publishPending) return;
			// A node's game time carries the offset only after SessionClockPatch ran with it;
			// publishing earlier would hand clients pre-step ticks. The WA applied the delta
			// directly, so it is always current.
			if (!ClusterRuntime.IsWorldAuthority && SessionClockPatch.AppliedOffsetMilliseconds != m_offsetMilliseconds) return;
			m_publishPending = false;
			time = m_timeOfDay;
		}
		MyTimeOfDayHelperSeams.ClusterPublishTimeOfDay(time);
	}

	/// <summary>The control-endpoint setter: the same routing as the vanilla slider event.</summary>
	internal static bool Set(float time) => Route(time);

	/// <summary>The persisted §8.2 state: SunTimeOffset + the last slider value (WA global save section).</summary>
	public static string PersistedRecord()
	{
		lock (Sync) return JsonSerializer.Serialize(new TimeOfDaySnapshot(m_timeOfDay, m_offsetMilliseconds));
	}

	/// <summary>
	/// Restores the persisted state on a WA restart. Never touches the clock: the applied
	/// adjustment already lives inside the persisted ElapsedGameTime, so restoring must neither
	/// lose the offset (the snapshot below would report zero) nor re-apply it (the sun would jump).
	/// </summary>
	public static void RestoreRecord(string payload)
	{
		if (string.IsNullOrEmpty(payload)) return;
		TimeOfDaySnapshot snapshot = JsonSerializer.Deserialize<TimeOfDaySnapshot>(payload);
		if (snapshot == null || !float.IsFinite(snapshot.TimeOfDay) || snapshot.OffsetMilliseconds < 0)
			throw new InvalidOperationException("Invalid persisted time of day record");
		lock (Sync)
		{
			m_timeOfDay = snapshot.TimeOfDay;
			m_offsetMilliseconds = snapshot.OffsetMilliseconds;
			m_snapshot = JsonSerializer.Serialize(snapshot);
		}
	}

	public static object Status()
	{
		VRageMath.Vector3 sun = MySector.SunProperties.SunDirectionNormalized;
		return new
		{
			node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty,
			timeOfDay = MyTimeOfDayHelper.TimeOfDay,
			lastSliderValue = m_timeOfDay,
			sunTimeOffsetMilliseconds = OffsetMilliseconds,
			sunDirection = new { x = sun.X, y = sun.Y, z = sun.Z },
			elapsedGameTime = MySession.Static?.ElapsedGameTime.ToString(),
			elapsedGameTimeTicks = MySession.Static?.ElapsedGameTime.Ticks,
			authorityClockMilliseconds = ClusterRuntime.AuthorityClockMilliseconds,
			isWorldAuthority = ClusterRuntime.IsWorldAuthority,
		};
	}

	private static string Snapshot() { lock (Sync) return m_snapshot; }
}
