using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using VRage.Network;
using VRage.Utils;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// T-0300: refreshes a client's replication update layers early when their content went stale.
///
/// Vanilla adds a spatial replicable to a client only when the update layer containing it refreshes
/// (MyReplicationServer.SendUpdate). The layers come from MyLayers.SetSyncDistance: at SyncDistance 3000
/// they are 20/80/320/1280/3000 m, refreshed every 60/120/240/480/960 frames, so 1 to 16 s. Grids in the
/// 1.28-3 km band therefore wait up to 16 s before they are even queued for streaming, and streaming then
/// sends at most one grid per pass (about 30 grids/s). Measured with a Steam client on r30 (2026-09-30,
/// 6 teleport trials to 1.5 km above the Earth base): about 227 grids drained by +11 s, then nothing for
/// about 5 s while the queue was empty, then the last 54 grids at +16 s, when the outer layer refreshed.
/// A ship at cruise speed (~409 m/s) crosses the whole 3 km sync radius in about 7 s, faster than that
/// 16 s refresh.
///
/// Two triggers call vanilla MyClient.ResetLayerTimers, which vanilla itself uses to refresh every layer
/// over the next frames (SetClientBatchConfrmation):
///  - motion: the client has moved more than CLUSTER_LAYER_REFRESH_METERS (default 500; 0 = off) since
///    its last refresh. A fast approacher gets a refresh every ~1.2 s at cruise speed instead of 16 s.
///  - import: a partition was loaded or adopted on this node (recovery, offline wake, handover import).
///    Its grids are new replicables that no client is offered until a layer refresh, so the clients within
///    sync distance of the partition (its members' bounding sphere) are refreshed 0.5 s and 2 s after the
///    import (the second catches replicables whose ReadyForReplication came late), each client at most
///    once per 2 s for imports. r31 refreshed EVERY client on every import (~3 imports/s per loaded node,
///    CM gate 2026-09-30 19:3x) and leaned worse on sim below 0.9. CLUSTER_LAYER_REFRESH_ON_IMPORT=0 turns
///    this off.
/// Counters go to one summary line every 5 minutes when either moved:
/// <c>client_layer_refresh motion=… import=… (T-0300)</c>.
/// </summary>
internal static class ClientLayerRefresh
{
	private static readonly double MotionMeters = double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_LAYER_REFRESH_METERS"),
		NumberStyles.Float, CultureInfo.InvariantCulture, out double meters) ? meters : 500.0;
	private static readonly bool OnImport = Environment.GetEnvironmentVariable("CLUSTER_LAYER_REFRESH_ON_IMPORT") != "0";
	private static readonly double[] ImportDelays = { 0.5, 2.0 };
	private const double SummarySeconds = 300;

	private static readonly Dictionary<Endpoint, Vector3D> m_anchors = new Dictionary<Endpoint, Vector3D>();
	private static readonly List<Endpoint> m_gone = new List<Endpoint>();
	private static readonly double ImportClientCooldown = 2.0;
	private static readonly System.Collections.Concurrent.ConcurrentQueue<long[]> m_importsNoted = new System.Collections.Concurrent.ConcurrentQueue<long[]>();
	private static readonly List<(double Due, long[] Members, BoundingSphereD? Sphere)> m_importDue = new List<(double, long[], BoundingSphereD?)>();
	private static readonly Dictionary<Endpoint, double> m_lastImportRefresh = new Dictionary<Endpoint, double>();
	private static double m_nextSummary;
	private static long m_summaryMotion, m_summaryImport;
	internal static long MotionRefreshes;
	internal static long ImportRefreshes;

	/// <summary>Any thread: a partition's entities (its members) were just created on this node.</summary>
	internal static void NoteImport(IEnumerable<long> members)
	{
		if (OnImport) m_importsNoted.Enqueue(System.Linq.Enumerable.ToArray(members));
	}

	/// <summary>Game thread, every tick.</summary>
	internal static void Update()
	{
		if (MySession.Static?.Ready != true) return;
		if (MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer server) return;
		double now = MySession.Static.ElapsedGameTime.TotalSeconds;
		while (m_importsNoted.TryDequeue(out long[] members))
			foreach (double delay in ImportDelays)
				m_importDue.Add((now + delay, members, null));
		List<BoundingSphereD> dueSpheres = null;
		for (int i = m_importDue.Count - 1; i >= 0; i--)
		{
			if (m_importDue[i].Due > now) continue;
			BoundingSphereD? sphere = MemberSphere(m_importDue[i].Members);
			if (sphere.HasValue) (dueSpheres ??= new List<BoundingSphereD>()).Add(sphere.Value);
			m_importDue.RemoveAt(i);
		}
		double reach = VRage.Network.MyLayers.GetSyncDistance();
		try
		{
			foreach (KeyValuePair<Endpoint, MyClient> entry in server.m_clientStates)
			{
				MyClient client = entry.Value;
				if (!client.IsReady || client.State?.Position is not Vector3D position) continue;
				if (dueSpheres != null && (!m_lastImportRefresh.TryGetValue(entry.Key, out double last) || now - last >= ImportClientCooldown)
					&& dueSpheres.Exists(sphere => Vector3D.Distance(sphere.Center, position) - sphere.Radius <= reach))
				{
					client.ResetLayerTimers();
					m_anchors[entry.Key] = position;
					m_lastImportRefresh[entry.Key] = now;
					ImportRefreshes++;
				}
				else if (!m_anchors.TryGetValue(entry.Key, out Vector3D anchor))
					m_anchors[entry.Key] = position;
				else if (MotionMeters > 0 && Vector3D.DistanceSquared(anchor, position) > MotionMeters * MotionMeters)
				{
					client.ResetLayerTimers();
					m_anchors[entry.Key] = position;
					MotionRefreshes++;
				}
			}
			if (m_anchors.Count > server.m_clientStates.Count)
			{
				foreach (Endpoint endpoint in m_anchors.Keys)
					if (!server.m_clientStates.ContainsKey(endpoint)) m_gone.Add(endpoint);
				foreach (Endpoint endpoint in m_gone) { m_anchors.Remove(endpoint); m_lastImportRefresh.Remove(endpoint); }
				m_gone.Clear();
			}
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLine("client_layer_refresh failed: " + exception.Message);
		}
		if (now < m_nextSummary) return;
		m_nextSummary = now + SummarySeconds;
		if (MotionRefreshes == m_summaryMotion && ImportRefreshes == m_summaryImport) return;
		MyLog.Default?.WriteLine($"client_layer_refresh motion={MotionRefreshes - m_summaryMotion} import={ImportRefreshes - m_summaryImport} "
			+ $"clients={server.m_clientStates.Count} thresholdM={MotionMeters.ToString(CultureInfo.InvariantCulture)} (T-0300)");
		m_summaryMotion = MotionRefreshes;
		m_summaryImport = ImportRefreshes;
	}

	/// <summary>Bounding sphere of the members that exist on this node now; null when none does.</summary>
	private static BoundingSphereD? MemberSphere(long[] members)
	{
		BoundingSphereD? sphere = null;
		foreach (long id in members)
		{
			if (!Sandbox.Game.Entities.MyEntities.TryGetEntityById(id, out VRage.Game.Entity.MyEntity entity) || entity.PositionComp == null) continue;
			BoundingSphereD own = entity.PositionComp.WorldVolume;
			sphere = sphere.HasValue ? BoundingSphereD.CreateMerged(sphere.Value, own) : own;
		}
		return sphere;
	}
}
