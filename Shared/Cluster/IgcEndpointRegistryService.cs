using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// WA-4: a cluster-wide directory of programmable-block IGC endpoints. Vanilla resolves a unicast addressee with
/// <c>GetContextForPB</c>, which only sees blocks loaded on the calling node, so without this an addressee on
/// another node looks exactly like a non-existent one and the send silently returns false. Regular nodes report
/// their endpoints, the World Authority merges and replicates them, and every node can then tell whether an
/// unresolved address is genuinely unknown or merely remote.
/// </summary>
public static class IgcEndpointRegistryService
{
	private const string Subsystem = "igc";
	private static readonly object Sync = new object();
	private static readonly Dictionary<string, Report> Reports = new Dictionary<string, Report>(StringComparer.Ordinal);
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	private static ClusterIgcEndpoint[] m_endpoints = Array.Empty<ClusterIgcEndpoint>();
	private static string m_snapshot = IgcEndpointCodec.Write(Array.Empty<ClusterIgcEndpoint>());
	private static double m_nextReport;

	public static IReadOnlyList<ClusterIgcEndpoint> Endpoints
	{
		get { lock (Sync) return m_endpoints.ToArray(); }
	}

	internal static void Install()
	{
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ApplySnapshot);
	}

	/// <summary>Resolves the full endpoint record, which carries the host grid and owner a send has to check.</summary>
	public static bool TryGetEndpoint(long endpointId, out ClusterIgcEndpoint endpoint)
	{
		lock (Sync)
		{
			endpoint = m_endpoints.FirstOrDefault(x => x.EndpointId == endpointId);
			return endpoint != null;
		}
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true
			|| ClusterRuntime.LocalMilliseconds < m_nextReport) return;
		m_nextReport = ClusterRuntime.LocalMilliseconds + 1000;
		if (ClusterRuntime.IsWorldAuthority)
		{
			// The World Authority owns no partitions, so it contributes no endpoints of its own.
			lock (Sync) Reports[Node] = new Report(ClusterRuntime.LocalMilliseconds, Array.Empty<ClusterIgcEndpoint>());
			PublishMerged();
		}
		else GlobalStateChannel.SendRequest(Subsystem, IgcEndpointCodec.Write(CaptureLocal()));
	}

	public static void AuthorityChanged(long generation)
	{
		lock (Sync)
		{
			Reports.Clear();
			m_endpoints = Array.Empty<ClusterIgcEndpoint>();
			m_snapshot = IgcEndpointCodec.Write(m_endpoints);
		}
	}

	private static ClusterIgcEndpoint[] CaptureLocal()
	{
		var endpoints = new List<ClusterIgcEndpoint>();
		foreach (MyProgrammableBlock block in MyEntities.GetEntities().OfType<MyCubeGrid>()
			.SelectMany(grid => grid.GetFatBlocks().OfType<MyProgrammableBlock>()))
		{
			if (block.Closed || block.CubeGrid == null || block.CubeGrid.Closed) continue;
			endpoints.Add(new ClusterIgcEndpoint(Node, block.EntityId, block.CubeGrid.EntityId, block.OwnerId));
		}
		return endpoints.OrderBy(x => x.EndpointId).ToArray();
	}

	private static void ReceiveReport(string origin, string payload)
	{
		ClusterIgcSnapshot snapshot = IgcEndpointCodec.Read(payload);
		ClusterIgcEndpoint[] endpoints = snapshot.Endpoints.Select(x => x with { Origin = origin }).ToArray();
		lock (Sync) Reports[origin] = new Report(ClusterRuntime.LocalMilliseconds, endpoints);
		PublishMerged();
	}

	private static void PublishMerged()
	{
		string snapshot;
		lock (Sync)
		{
			double cutoff = ClusterRuntime.LocalMilliseconds - 5000;
			foreach (string origin in Reports.Where(x => x.Value.Received < cutoff).Select(x => x.Key).ToArray())
				Reports.Remove(origin);
			// A block that just moved between nodes is reported by both for a moment; the most recent report wins.
			m_endpoints = Reports.OrderBy(x => x.Value.Received).ThenBy(x => x.Key, StringComparer.Ordinal)
				.SelectMany(x => x.Value.Endpoints).GroupBy(x => x.EndpointId).Select(x => x.Last())
				.OrderBy(x => x.EndpointId).ToArray();
			snapshot = IgcEndpointCodec.Write(m_endpoints);
			if (snapshot == m_snapshot) return;
			m_snapshot = snapshot;
		}
		try { GlobalStateChannel.Publish(Subsystem, snapshot); }
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=igc error=" + SecretRedactor.Redact(exception.Message));
		}
	}

	private static void ApplySnapshot(string payload)
	{
		ClusterIgcSnapshot snapshot = IgcEndpointCodec.Read(payload);
		lock (Sync)
		{
			m_endpoints = snapshot.Endpoints;
			m_snapshot = payload;
		}
	}

	private static string Snapshot() { lock (Sync) return m_snapshot; }
	private sealed record Report(double Received, ClusterIgcEndpoint[] Endpoints);
}
