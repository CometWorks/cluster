using System;
using System.Collections.Generic;
using System.Linq;
using PluginSdk.Stats;

namespace ClusterPlugin;

/// <summary>
/// #1443: the cluster-wide cap on active container drops (drops.maxActive, default 3), World Authority only. Vanilla decides
/// drops per player, so the cluster's rate grows with its pilots (#1378: ~+240/h at ~160 pilots). The WA is the one place
/// that dispatches drops (<see cref="DropContainerDirectorService"/>), so the cap holds there and no node can race past it.
/// <para>
/// Active = an unopened drop container still in the world: a census row with the drop flag (<see cref="NpcCensusService"/>),
/// online on any node or in an offline partition, until its vanilla removal deadline (an offline row keeps the one it had at
/// the freeze, and the drop leaves the world at it when its partition loads, DropContainerRemovalService). Opening a drop
/// takes its component away, so a collected drop stops counting. Two lags are covered, both towards counting more: a drop
/// the census no longer lists counts for <see cref="SeenHoldMilliseconds"/> more (a partition between two nodes' reports), and
/// a drop the WA dispatched counts as pending until a census pass <see cref="PendingSlackMilliseconds"/> after the dispatch
/// (the prefab spawn finishes after vanilla's answer) or until the census first lists a new drop (<see cref="Merge"/>). With no census, or one older than 60 s, the count is unknown and every
/// due drop is skipped.
/// </para>
/// </summary>
internal static class DropContainerCap
{
	public const string StatsProvider = "cluster.drops";
	internal const double SeenHoldMilliseconds = 30_000;
	internal const double PendingSlackMilliseconds = 30_000;
	/// <summary>How long a drop id stays known after the census last listed it, so a drop listed again is not taken for a new spawn.</summary>
	private const double KnownMilliseconds = 3_600_000;
	private const double SkipLogMilliseconds = 60_000;

	internal readonly record struct Seen(double At, bool Offline, long Expires);

	internal readonly record struct Count(int Active, int Online, int Offline, int Pending)
	{
		public static readonly Count Unknown = new(-1, 0, 0, 0);
	}

	private static readonly object Sync = new();
	private static readonly Dictionary<long, Seen> s_seen = new();
	private static readonly List<double> s_dispatchedAt = new();
	private static NpcCensusService.Snapshot s_snapshot;
	private static long s_spawned, s_skippedAtCap, s_skippedUnknown, s_skippedSinceLog;
	private static int s_loggedActive = int.MinValue;
	private static double s_nextSkipLog;

	/// <summary>The census drops seen within the hold that are not past their deadline, plus the pending dispatches.</summary>
	internal static Count Tally(IEnumerable<Seen> seen, IEnumerable<double> dispatchedAt, double now, double censusPass, long nowUnix)
	{
		int online = 0, offline = 0;
		foreach (Seen drop in seen)
		{
			if (now - drop.At > SeenHoldMilliseconds || drop.Expires > 0 && drop.Expires <= nowUnix) continue;
			if (drop.Offline) offline++;
			else online++;
		}
		int pending = dispatchedAt.Count(at => at > censusPass - PendingSlackMilliseconds);
		return new Count(online + offline + pending, online, offline, pending);
	}

	/// <summary>Must be called under <see cref="Sync"/>.</summary>
	private static Count Current(double now)
	{
		NpcCensusService.Snapshot snapshot = NpcCensusService.Current;
		if (snapshot == null || !(snapshot.Age(now) <= NpcCensusService.MaxAgeSeconds)) return Count.Unknown;
		if (snapshot != s_snapshot)
		{
			s_snapshot = snapshot;
			Merge(s_seen, s_dispatchedAt, snapshot.Drops, snapshot.AnsweredAt, snapshot.AnsweredAt - snapshot.AgeAtAnswer * 1000);
		}
		foreach (long id in s_seen.Where(pair => now - pair.Value.At > KnownMilliseconds).Select(pair => pair.Key).ToArray())
			s_seen.Remove(id);
		double censusPass = now - snapshot.Age(now) * 1000;
		s_dispatchedAt.RemoveAll(at => at <= censusPass - PendingSlackMilliseconds);
		return Tally(s_seen.Values, s_dispatchedAt, now, censusPass, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
	}

	/// <summary>
	/// Takes one census answer's drop rows into <paramref name="seen"/>. A drop listed for the first time is most likely
	/// the oldest pending spawn dispatched before that census pass, so that spawn stops counting as pending: otherwise
	/// a fresh drop would count twice for up to <see cref="PendingSlackMilliseconds"/>.
	/// </summary>
	internal static void Merge(Dictionary<long, Seen> seen, List<double> dispatchedAt, IEnumerable<NpcCensusService.DropRow> drops,
		double answeredAt, double pass)
	{
		foreach (NpcCensusService.DropRow drop in drops)
		{
			if (!seen.ContainsKey(drop.Id))
			{
				int oldest = -1;
				for (int i = 0; i < dispatchedAt.Count; i++)
					if (dispatchedAt[i] < pass && (oldest < 0 || dispatchedAt[i] < dispatchedAt[oldest])) oldest = i;
				if (oldest >= 0) dispatchedAt.RemoveAt(oldest);
			}
			seen[drop.Id] = new Seen(answeredAt, drop.Offline, drop.Expires);
		}
	}

	/// <summary>Whether one more drop may be dispatched now; counts the skip when not. cap 0 = no cap.</summary>
	public static bool Allow(int cap, double now)
	{
		if (cap <= 0) return true;
		lock (Sync)
		{
			Count count = Current(now);
			if (count.Active >= 0 && count.Active < cap) return true;
			if (count.Active < 0) s_skippedUnknown++;
			else s_skippedAtCap++;
			s_skippedSinceLog++;
			return false;
		}
	}

	/// <summary>A drop vanilla accepted on its node: pending until the census can list it.</summary>
	public static void Spawned(double now)
	{
		lock (Sync)
		{
			s_dispatchedAt.Add(now);
			s_spawned++;
		}
	}

	/// <summary>
	/// Once per WA sweep: publishes the statistics and logs a line when the active count changed, or at most once a minute
	/// when due drops were skipped since the last line.
	/// </summary>
	public static void Report(int cap, double now)
	{
		DropCapStats stats;
		string line = null;
		lock (Sync)
		{
			Count count = Current(now);
			stats = Stats(cap, count);
			if (count.Active != s_loggedActive || s_skippedSinceLog > 0 && now >= s_nextSkipLog)
			{
				line = "[ClusterPlugin] drop_container cap active=" + count.Active + " cap=" + cap + " online=" + count.Online
					+ " offline=" + count.Offline + " pending=" + count.Pending + " skipped=" + s_skippedSinceLog
					+ " skippedAtCapTotal=" + s_skippedAtCap + " skippedUnknownTotal=" + s_skippedUnknown + " spawnedTotal=" + s_spawned;
				s_loggedActive = count.Active;
				s_skippedSinceLog = 0;
				s_nextSkipLog = now + SkipLogMilliseconds;
			}
		}
		if (line != null) Console.WriteLine(line);
		PluginStats.Publish(StatsProvider, new StatsSnapshot
		{
			UtcTimestamp = DateTime.UtcNow,
			Groups = { StatsSchema.Build(typeof(DropCapStats)).CaptureGroup(new[] { stats }) },
		});
	}

	public static object Status(int cap, double now)
	{
		lock (Sync)
		{
			Count count = Current(now);
			return new
			{
				cap, active = count.Active, online = count.Online, offline = count.Offline, pending = count.Pending,
				spawned = s_spawned, skippedAtCap = s_skippedAtCap, skippedUnknown = s_skippedUnknown,
			};
		}
	}

	/// <summary>Must be called under <see cref="Sync"/>.</summary>
	private static DropCapStats Stats(int cap, Count count) => new()
	{
		Cap = cap, Active = count.Active, Online = count.Online, Offline = count.Offline, Pending = count.Pending,
		Spawned = s_spawned, SkippedAtCap = s_skippedAtCap, SkippedUnknown = s_skippedUnknown,
	};

	/// <summary>The "cluster.drops" statistics row, one per WA sweep. Counters are totals since the WA started.</summary>
	public sealed class DropCapStats
	{
		[Discrete("drops.maxActive, 0 = no cap", OverTime = TimeAggregation.Last)] public int Cap { get; set; }
		[Gauge("Active container drops cluster-wide, -1 = census unknown", OverTime = TimeAggregation.Max)] public int Active { get; set; }
		[Gauge("Active drops in online partitions", OverTime = TimeAggregation.Max)] public int Online { get; set; }
		[Gauge("Active drops in offline partitions", OverTime = TimeAggregation.Max)] public int Offline { get; set; }
		[Gauge("Spawned drops the census does not list yet", OverTime = TimeAggregation.Max)] public int Pending { get; set; }
		[Counter("Drops spawned (vanilla accepted the dispatch)", OverTime = TimeAggregation.Last)] public long Spawned { get; set; }
		[Counter("Due drops skipped at the cap", OverTime = TimeAggregation.Last)] public long SkippedAtCap { get; set; }
		[Counter("Due drops skipped while the census was unknown", OverTime = TimeAggregation.Last)] public long SkippedUnknown { get; set; }
	}
}
