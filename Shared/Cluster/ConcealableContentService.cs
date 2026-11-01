using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>
/// P2-CON wake-merge absorption: cluster-shared concealable CONTENT. Each node reports the entity
/// ids it knows to be concealable (its boot-catalog concealed saves plus every reconcealed Final);
/// the World Authority merges the contributions into a sticky union and publishes it. The
/// concealment sweep gates on the merged set, so an absorbed beacon handed to ANOTHER node is still
/// recognized as concealable once the passerby leaves - ids never expire (content facts do not go
/// stale the way liveness does). Residual: a replacement WA rebuilds the union from live reports,
/// so a node that died before the replacement came up loses its contribution until it returns.
/// </summary>
internal static class ConcealableContentService
{
	private const string Subsystem = "concealable";
	private const double ReportIntervalMilliseconds = 2000;

	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	private static readonly object Sync = new object();
	private static readonly HashSet<long> Merged = new HashSet<long>();
	private static double m_nextReport;
	private static int m_lastReported = -1;

	private sealed record ContentReport(string Origin, long[] Ids);

	internal static PartitionPersistence Persistence;

	internal static void Install()
	{
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ReceiveSnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ReceiveSnapshot);
	}

	public static bool Contains(long entityId) { lock (Sync) return Merged.Contains(entityId); }

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered) return;
		long[] local = Persistence?.ConcealableMembers();
		if (local == null) return;
		if (ClusterRuntime.LocalMilliseconds < m_nextReport && local.Length == m_lastReported) return;
		m_nextReport = ClusterRuntime.LocalMilliseconds + ReportIntervalMilliseconds;
		m_lastReported = local.Length;
		if (local.Length == 0) return;
		string payload = JsonSerializer.Serialize(new ContentReport(Node, local));
		if (ClusterRuntime.IsWorldAuthority) ReceiveReport(Node, payload);
		else
		{
			try { GlobalStateChannel.SendRequest(Subsystem, payload); }
			catch (Exception)
			{
				// The WA can be mid-restart; the next cadence retries and the union is sticky.
				m_lastReported = -1;
			}
		}
	}

	private static void ReceiveReport(string origin, string payload)
	{
		ContentReport report;
		try { report = JsonSerializer.Deserialize<ContentReport>(payload); }
		catch (Exception) { return; }
		if (report?.Ids == null || report.Ids.Length == 0) return;
		bool grew;
		long[] snapshot;
		lock (Sync)
		{
			int before = Merged.Count;
			foreach (long id in report.Ids) Merged.Add(id);
			grew = Merged.Count != before;
			snapshot = grew ? Merged.OrderBy(id => id).ToArray() : null;
		}
		if (grew && ClusterRuntime.IsWorldAuthority)
			GlobalStateChannel.Publish(Subsystem, JsonSerializer.Serialize(snapshot));
	}

	private static void ReceiveSnapshot(string payload)
	{
		long[] ids;
		try { ids = JsonSerializer.Deserialize<long[]>(payload); }
		catch (Exception) { return; }
		if (ids == null) return;
		lock (Sync) foreach (long id in ids) Merged.Add(id);
	}

	private static string Snapshot()
	{
		lock (Sync) return JsonSerializer.Serialize(Merged.OrderBy(id => id).ToArray());
	}

}
