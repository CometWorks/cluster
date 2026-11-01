using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Game-thread hold time per handover phase (HandoverImprovements.md §3). One <see cref="Phase"/> per
/// Invoke body, <see cref="Step"/> splits it; disposal logs one `handover_phase` line and files the
/// record in a 256-entry ring served by GET /cluster/handover-stats with per-phase p50/p95/max.
/// Also publishes the active phase to <see cref="HitchRecorder"/> so long frames name their cause.
/// </summary>
public sealed class HandoverPhaseTimer : IDisposable
{
	public sealed record Record(DateTime Utc, string Phase, string TransferId, ulong Partition, double GameThreadMs,
		Dictionary<string, double> Steps, int Entities, long Bytes, string Error);

	private const int Capacity = 256;
	private static readonly object m_lock = new object();
	private static readonly Queue<Record> m_ring = new Queue<Record>();
	[ThreadStatic] private static HandoverPhaseTimer s_current;
	/// <summary>The phase timer open on this (game) thread, for sub-step marks deep inside a phase body.</summary>
	public static HandoverPhaseTimer Current => s_current;

	private readonly string m_phase;
	private readonly string m_transferId;
	private readonly ulong m_partition;
	private readonly Stopwatch m_total = Stopwatch.StartNew();
	private readonly Stopwatch m_step = new Stopwatch();
	private readonly Dictionary<string, double> m_steps = new Dictionary<string, double>();
	private string m_currentStep;
	public int Entities;
	public long Bytes;
	public string Error;
	/// <summary>Extra " key=value" pairs for this phase's handover_phase line (T-0392: per-slice budget fields).</summary>
	public string Fields;

	public static HandoverPhaseTimer Phase(string phase, Guid? transferId, ulong partition) => new HandoverPhaseTimer(phase, transferId, partition);

	private HandoverPhaseTimer(string phase, Guid? transferId, ulong partition)
	{
		m_phase = phase;
		m_transferId = transferId?.ToString("D");
		m_partition = partition;
		HitchRecorder.ActivePartition = partition;
		HitchRecorder.ActiveTransferId = m_transferId;
		HitchRecorder.ActivePhase = phase;
		s_current = this;
	}

	/// <summary>Ends the previous sub-step and starts the named one; null closes the current step.</summary>
	public void Step(string name)
	{
		if (m_currentStep != null)
		{
			m_steps[m_currentStep] = m_steps.GetValueOrDefault(m_currentStep) + m_step.Elapsed.TotalMilliseconds;
		}
		m_currentStep = name;
		m_step.Restart();
	}

	/// <summary>Milliseconds accumulated so far under a CLOSED sub-step (the open one is not counted yet).</summary>
	public double StepMs(string name) => m_steps.GetValueOrDefault(name);

	/// <summary>The phase's game-thread ms so far (T-0392: import.activateMsPerSecond's ledger entry for an activation frame).</summary>
	public double ElapsedMs => m_total.Elapsed.TotalMilliseconds;

	public void Dispose()
	{
		Step(null);
		s_current = null;
		HitchRecorder.ActivePhase = null;
		HitchRecorder.ActiveTransferId = null;
		// Cleared too: leaving it set made every later hitch carry this partition and look attributed.
		HitchRecorder.ActivePartition = 0;
		Record record = new Record(DateTime.UtcNow, m_phase, m_transferId, m_partition, m_total.Elapsed.TotalMilliseconds, m_steps, Entities, Bytes, Error);
		// The frame that ran this phase is measured after it closes, so hand the attribution forward.
		HitchRecorder.NotePhase(m_phase, m_transferId, m_partition, record.GameThreadMs);
		lock (m_lock)
		{
			m_ring.Enqueue(record);
			while (m_ring.Count > Capacity) m_ring.Dequeue();
		}
		MyLog.Default?.WriteLineAndConsole(string.Format(System.Globalization.CultureInfo.InvariantCulture,
			"[ClusterPlugin] handover_phase transferId={0} partition={1} phase={2} gameThreadMs={3:F1} entities={4} bytes={5}{6}{7}{8}",
			m_transferId ?? "-", m_partition, m_phase, record.GameThreadMs, Entities, Bytes,
			m_steps.Count == 0 ? "" : " " + string.Join(" ", m_steps.Select(s => s.Key + "Ms=" + s.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))),
			Fields ?? "",
			Error == null ? "" : " error=" + Error));
	}

	public static string StatsJson()
	{
		Record[] records;
		lock (m_lock) records = m_ring.ToArray();
		var phases = records.GroupBy(r => r.Phase).ToDictionary(g => g.Key, g =>
		{
			double[] sorted = g.Select(r => r.GameThreadMs).OrderBy(x => x).ToArray();
			return new { count = sorted.Length, p50 = Percentile(sorted, 0.5), p95 = Percentile(sorted, 0.95), max = sorted[^1] };
		});
		return JsonSerializer.Serialize(new { phases, recent = records });
	}

	private static double Percentile(double[] sorted, double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1 < 0 ? 0 : (int)Math.Ceiling(p * sorted.Length) - 1)];
}
