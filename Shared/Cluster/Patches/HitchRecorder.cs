using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Game-thread hitch recorder (HandoverImprovements.md §3): every frame longer than
/// <see cref="ThresholdMs"/> is recorded with the handover phase active at the time, so a sim-speed
/// dip becomes "phase X of transfer Y held the game thread for N ms" instead of "dip near handover".
/// Linked into Sandbox.Game (seam: Game.RunSingleFrame) and into the plugin - stdlib only.
/// </summary>
public static class HitchRecorder
{
	/// <summary>
	/// PhaseMs is how much of the frame handover phases actually held, which is the number that says
	/// whether a knob can help: a 3 s frame with phaseMs=2.9 s is the handover, a 3 s frame with
	/// phaseMs=20 ms is something else that merely happened during one.
	/// </summary>
	public sealed record Hitch(DateTime Utc, double Ms, string Phase, string TransferId, ulong Partition, double PhaseMs);

	private const int Capacity = 512;
	private static readonly object m_lock = new object();
	private static readonly Queue<Hitch> m_ring = new Queue<Hitch>();
	private static long m_sequence;

	/// <summary>Set by ClusterConfig (hitch.thresholdMs); 0 disables recording.</summary>
	public static volatile int ThresholdMs = 100;
	/// <summary>The handover phase currently running on the game thread, or null (set by HandoverPhaseTimer).</summary>
	public static volatile string ActivePhase;
	public static volatile string ActiveTransferId;
	public static ulong ActivePartition;

	// Frame-scoped attribution, game thread only. The seam runs AFTER UpdateInternal returns, and a
	// handover phase runs INSIDE it, so by the time a long frame is measured the phase has closed and
	// ActivePhase is null again: on w22 that made 100% of hitches "phase=none" while 14 of 16 dip
	// episodes were in fact the target side of a handover. Worse, ActivePartition was never cleared,
	// so each hitch carried the previous phase's partition and read like an attribution it was not.
	// A phase therefore reports itself as it closes, and the frame claims what ran inside it.
	private static string m_framePhase;
	private static string m_frameTransferId;
	private static ulong m_framePartition;
	private static double m_framePhaseMs;
	private static double m_frameDominantMs;

	/// <summary>Called by HandoverPhaseTimer as a phase closes, so the frame it ran in can name it.</summary>
	public static void NotePhase(string phase, string transferId, ulong partition, double heldMs)
	{
		m_framePhaseMs += heldMs;
		if (heldMs < m_frameDominantMs) return;
		m_frameDominantMs = heldMs;
		m_framePhase = phase;
		m_frameTransferId = transferId;
		m_framePartition = partition;
	}

	/// <summary>
	/// Per-frame engine breakdown, so a hitch line says WHAT the frame did, not only that it was slow.
	///
	/// Why: of 2691 hitch records on 2026-08-22, the largest class is <c>phase=none</c> - 1167 records,
	/// 1113 s of stall, **0.0% accounted** - larger than <c>commit</c> (591, 824 s, 4.6% accounted), and
	/// rising 2.6x through the day (62.8 -> 165.2 s/h) faster than the world grows (105 -> 228 s per
	/// 1000 entity-hours). No handover phase is open during any of it, so no phase timer can ever see it,
	/// and a sampling profiler cannot be aimed at it either - a stall lasts ~1-3 s and dotnet-trace takes
	/// ~10 s to attach. One frame-level capture on 2026-08-22 caught a 1443 ms frame whose largest term
	/// was 831 ms of <c>MyVoxelPhysicsBody.RequestShapeBlocking</c> with no handover in flight; these
	/// buckets turn that one lucky sample into a census.
	///
	/// Costs two <c>Stopwatch.GetTimestamp()</c> calls per instrumented call site per frame. The buckets
	/// are [ThreadStatic] because only the game thread's frame is being described - a worker thread
	/// accumulating into the same array would mix parallel time into a wall-clock breakdown.
	/// </summary>
	public static class Bucket
	{
		public const int PhysicsStep = 0;
		public const int VoxelShapeBlocking = 1;
		public const int EntityUpdateBefore = 2;
		public const int EntityUpdateAfter = 3;
		public const int AntennaScan = 4;
		public const int SessionComponents = 5;
		public const int Count = 6;
		public static readonly string[] Names = { "physics", "voxel_shape", "ent_before", "ent_after", "antenna", "components" };
	}

	[ThreadStatic] private static double[] m_buckets;
	[ThreadStatic] private static int[] m_bucketCalls;

	/// <summary>Accumulate elapsed time since <paramref name="startTimestamp"/> into one bucket.</summary>
	public static void Add(int bucket, long startTimestamp)
	{
		double[] buckets = m_buckets ??= new double[Bucket.Count];
		int[] calls = m_bucketCalls ??= new int[Bucket.Count];
		buckets[bucket] += (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		calls[bucket]++;
	}

	/// <summary>The frame's breakdown as log text, or an empty string when nothing was instrumented.</summary>
	private static string Breakdown()
	{
		double[] buckets = m_buckets;
		int[] calls = m_bucketCalls;
		if (buckets == null) return "";
		System.Text.StringBuilder text = new System.Text.StringBuilder();
		for (int i = 0; i < Bucket.Count; i++)
			if (buckets[i] > 0.05)
				text.Append(' ').Append(Bucket.Names[i]).Append('=')
					.Append(buckets[i].ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
					.Append('/').Append(calls[i]);
		return text.ToString();
	}

	private static void ClearBuckets()
	{
		double[] buckets = m_buckets;
		int[] calls = m_bucketCalls;
		if (buckets == null) return;
		Array.Clear(buckets, 0, buckets.Length);
		Array.Clear(calls, 0, calls.Length);
	}

	/// <summary>Seam: called once per frame from Game.RunSingleFrame with the frame's wall time.</summary>
	public static void Frame(double milliseconds)
	{
		int threshold = ThresholdMs;
		if (threshold > 0 && milliseconds >= threshold)
		{
			// A phase still open wins over one that closed: the frame is being held right now.
			bool phaseWasOpen = ActivePhase != null;
			string phase = ActivePhase ?? m_framePhase;
			Hitch hitch = new Hitch(DateTime.UtcNow, milliseconds, phase ?? "none",
				phaseWasOpen ? ActiveTransferId : m_frameTransferId,
				phaseWasOpen ? ActivePartition : m_framePartition,
				m_framePhaseMs);
			lock (m_lock)
			{
				m_ring.Enqueue(hitch);
				while (m_ring.Count > Capacity) m_ring.Dequeue();
				m_sequence++;
			}
			Log(hitch, phaseWasOpen);
		}
		// Cleared on EVERY frame, not only recorded ones, or a quiet frame after a handover would
		// inherit the previous frame's phase.
		m_framePhase = null;
		m_frameTransferId = null;
		m_framePartition = 0;
		m_framePhaseMs = 0;
		m_frameDominantMs = 0;
		// Cleared on EVERY frame for the same reason the phase fields are: otherwise a quiet frame
		// inherits the previous frame's work and the breakdown is a running total, not a frame.
		ClearBuckets();
	}

	/// <summary>
	/// Threshold for writing a hitch to the node log as well as the ring; 0 disables logging.
	/// The ring is per-process, capped at <see cref="Capacity"/> and never persisted, so every
	/// rotation destroyed the whole hitch history with the node - on 2026-08-22 the only way to
	/// study a freeze was to catch a live node before it rotated, and a node lives ~20 minutes.
	/// Logging is cheap because hitches are rare by construction (~80 across six processes in
	/// 25 minutes at a 100 ms threshold).
	/// </summary>
	public static volatile int LogThresholdMs = 250;

	// Process start, for the age field. Age is the variable that actually separates hitches:
	// every frame over 1 s observed so far falls in a process's first ~6 minutes.
	private static readonly DateTime m_startedUtc = ProcessStartUtc();

	/// <summary>Seconds since this process started (the hitch lines' age; T-0401 nodeAgeS).</summary>
	internal static double ProcessAgeSeconds => (DateTime.UtcNow - m_startedUtc).TotalSeconds;

	private static DateTime ProcessStartUtc()
	{
		try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
		catch (Exception) { return DateTime.UtcNow; }
	}

	/// <summary>
	/// One line per hitch over <see cref="LogThresholdMs"/>. `open` is the field that makes
	/// phaseMs readable: phaseMs is only accumulated as a phase CLOSES, so `phase=X open=true
	/// phaseMs=0` means the phase was still running and had not yet reported - NOT that the
	/// handover was innocent. Only `phase=none` exonerates. Two findings turned on that
	/// distinction and it belongs in the instrument, not in an analyst's head.
	/// </summary>
	private static void Log(Hitch hitch, bool phaseWasOpen)
	{
		int logThreshold = LogThresholdMs;
		if (logThreshold <= 0 || hitch.Ms < logThreshold) return;
		try
		{
			MyLog.Default?.WriteLineAndConsole(string.Format(System.Globalization.CultureInfo.InvariantCulture,
				"[ClusterPlugin] hitch ms={0:F1} ageMs={1:F0} phase={2} open={3} phaseMs={4:F1} partition={5} transferId={6}{7}",
				hitch.Ms, (hitch.Utc - m_startedUtc).TotalMilliseconds, hitch.Phase,
				phaseWasOpen ? "true" : "false", hitch.PhaseMs, hitch.Partition, hitch.TransferId ?? "-",
				Breakdown()));
		}
		catch (Exception)
		{
			// Diagnostics must never be able to take down the game thread.
		}
	}

	/// <summary>Total hitches recorded since start (ring overflow included).</summary>
	public static long Sequence { get { lock (m_lock) return m_sequence; } }

	public static Hitch[] Snapshot(DateTime? sinceUtc = null)
	{
		lock (m_lock) return sinceUtc.HasValue ? m_ring.Where(h => h.Utc > sinceUtc.Value).ToArray() : m_ring.ToArray();
	}
}
