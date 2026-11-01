using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace ClusterPlugin;

/// <summary>
/// CLUSTER-SIM-COST: per-import recovery instrument (2026-08-25). Measures target-side commit ->
/// sim recovered for every handover import, because two open decisions wait on its answer:
///   1. Cost-model shape: is the receiver's dip a FIXED per-import cost (then CostModel wants a
///      constant per-import term) or does recovery scale with the moved partition's heat (then
///      simLoadUs belongs in CostMs)? Depth looked constant across a 1-partition box and a
///      50-partition blob (0.15 vs 0.05, CM 2026-08-25), but depth at 0.4-0.7 Hz sampling cannot
///      answer it - recovery TIME can. Join per line with the gateway's pin log (by partition) for
///      the moved partition's simLoadUs.
///   2. Drain pacing knobs: the paced drain's K=5 posts-per-open-gate and 2.5 s breather are
///      one-trace guesses (cluster-regression fd3bb7f); the recovery distribution replaces them
///      with numbers.
/// One line per import, logged when the node's sim ratio holds >= 0.9 for two consecutive plugin
/// ticks (~0.5 s, included in recoveredMs) or at the 120 s timeout. `concurrent` counts imports
/// still recovering when this one committed, so overlapping arrivals can be excluded or studied
/// as the co-arrival case. Cost: one ratio read per 250 ms tick while any entry is open; nothing
/// when idle.
/// </summary>
public static class ImportRecoveryProbe
{
	private sealed class Entry
	{
		public Guid TransferId;
		public ulong Partition;
		public int Members;
		public int Clients;
		public long StartStamp;
		public double MinSim = double.MaxValue;
		public int HealthyTicks;
		public int ConcurrentAtStart;
	}

	private const double RecoveredRatio = 0.9;
	private const int RequiredHealthyTicks = 2;
	private const double TimeoutSeconds = 120;

	private static readonly object m_lock = new object();
	private static readonly List<Entry> m_open = new List<Entry>();

	/// <summary>Called at the end of the target-side Commit (game thread).</summary>
	public static void Note(Guid transferId, ulong partition, int members, int clients)
	{
		lock (m_lock)
			m_open.Add(new Entry
			{
				TransferId = transferId, Partition = partition, Members = members, Clients = clients,
				StartStamp = Stopwatch.GetTimestamp(), ConcurrentAtStart = m_open.Count,
			});
	}

	/// <summary>Called from ClusterRuntime.Update (250 ms timer, game thread).</summary>
	public static void Update()
	{
		if (m_open.Count == 0) return;
		double ratio = Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;
		lock (m_lock)
			for (int i = m_open.Count - 1; i >= 0; i--)
			{
				Entry entry = m_open[i];
				if (ratio < entry.MinSim) entry.MinSim = ratio;
				entry.HealthyTicks = ratio >= RecoveredRatio ? entry.HealthyTicks + 1 : 0;
				double elapsed = (Stopwatch.GetTimestamp() - entry.StartStamp) / (double)Stopwatch.Frequency;
				bool recovered = entry.HealthyTicks >= RequiredHealthyTicks;
				if (!recovered && elapsed < TimeoutSeconds) continue;
				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"[ClusterPlugin] import-recovery transferId={0} partition={1} members={2} clients={3}"
					+ " concurrent={4} minSim={5:F2} recoveredMs={6:F0}{7}",
					entry.TransferId, entry.Partition, entry.Members, entry.Clients,
					entry.ConcurrentAtStart, entry.MinSim == double.MaxValue ? ratio : entry.MinSim,
					elapsed * 1000, recovered ? "" : " timeout"));
				m_open.RemoveAt(i);
			}
	}
}
