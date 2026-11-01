using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using VRage.Network;
using VRage.Replication;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// T-0225: re-sends block-level child replicables a client should hold and does not.
///
/// A pilot whose client lost part of its ship's subtree could see the grid and reach none of the missing
/// blocks. Its AI route never activated, it could not sit in the cockpit, and only a client recycle cured
/// it. The owning node's own bookkeeping showed the gap (/cluster/client-view): pilot-63 lacked the
/// cockpit and flight-movement terminal replicables (2026-09-23 20:45), pilot-53 two MyAutopilotComponent
/// proxies (22:18). The gap came after a client-view handover adopt, and also after a plain re-entry into
/// view with no handover at all.
///
/// Vanilla adds a streamed grid's children once, when the grid's stream completes
/// (MyReplicationServer, the state-sync path that walks GetAllChildren). AddForClient skips any child that
/// is not IsReadyForReplication at that moment, and nothing retries it: RefreshReplicable only runs for
/// replicables in an update layer or on a priority list, and a non-spatial block child is in neither.
/// Vanilla's intended state for a non-spatial child is simply "present while its parent is"
/// (RefreshReplicable), and its own ResendMissingReplicableChildren does exactly this for one grid on
/// request. This service applies that rule periodically.
///
/// Every <c>CLUSTER_CHILD_REPAIR_SECONDS</c> (default 5; 0 = off), one client per game-thread tick, round
/// robin. For each top-level replicable the client fully holds (not pending, not streaming), every tree
/// child the client lacks goes through vanilla AddForClient. AddForClient keeps its own checks (ready,
/// valid, ShouldReplicate) and adds the child's children. A child still not ready is simply tried again
/// on the next pass. One line per repaired (client, parent):
/// <c>client_view_child_repair client=… parent=… added=… [types]</c>.
/// </summary>
internal static class ClientChildRepair
{
	private static readonly double IntervalSeconds = double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_CHILD_REPAIR_SECONDS"),
		NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 5.0;

	private static readonly List<Endpoint> m_pending = new List<Endpoint>();
	private static readonly List<IMyReplicable> m_parents = new List<IMyReplicable>();
	private static readonly List<IMyReplicable> m_missing = new List<IMyReplicable>();
	private static double m_nextPass;
	internal static long Repairs;
	internal static long ChildrenAdded;

	internal static void Update()
	{
		if (IntervalSeconds <= 0 || MySession.Static?.Ready != true) return;
		if (MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer server) return;
		double now = MySession.Static.ElapsedGameTime.TotalSeconds;
		if (m_pending.Count == 0)
		{
			if (now < m_nextPass) return;
			m_nextPass = now + IntervalSeconds;
			m_pending.AddRange(server.m_clientStates.Keys);
			if (m_pending.Count == 0) return;
		}
		Endpoint endpoint = m_pending[m_pending.Count - 1];
		m_pending.RemoveAt(m_pending.Count - 1);
		try
		{
			RepairClient(server, endpoint);
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLine("client_view_child_repair failed client=" + endpoint.Id.Value + ": " + exception.Message);
		}
	}

	private static void RepairClient(MyReplicationServer server, Endpoint endpoint)
	{
		if (!server.m_clientStates.TryGetValue(endpoint, out MyClient client)) return;
		// Collect first: AddForClient adds to client.Replicables, which must not change under enumeration.
		m_parents.Clear();
		foreach (KeyValuePair<IMyReplicable, MyReplicableClientData> entry in client.Replicables)
			if (!entry.Value.IsPending && !entry.Value.IsStreaming && entry.Key.GetParent() == null)
				m_parents.Add(entry.Key);
		foreach (IMyReplicable parent in m_parents)
		{
			m_missing.Clear();
			CollectMissing(server, client, parent);
			if (m_missing.Count == 0) continue;
			int before = client.Replicables.Count;
			foreach (IMyReplicable child in m_missing)
				if (!client.HasReplicable(child) && (child.GetParent() is not IMyReplicable up || client.HasReplicable(up)))
					server.AddForClient(child, endpoint, client, false);
			int added = client.Replicables.Count - before;
			if (added <= 0) continue;
			Repairs++;
			ChildrenAdded += added;
			MyLog.Default?.WriteLine($"client_view_child_repair client={endpoint.Id.Value} parent={Describe(parent)} added={added} "
				+ $"[{string.Join(", ", m_missing.Where(client.HasReplicable).GroupBy(x => x.GetType().Name).Select(g => g.Key + "x" + g.Count()))}]");
		}
		m_parents.Clear();
		m_missing.Clear();
	}

	/// <summary>Children of <paramref name="parent"/> the client lacks and could be sent now; descends only through children it holds.</summary>
	private static void CollectMissing(MyReplicationServer server, MyClient client, IMyReplicable parent)
	{
		foreach (IMyReplicable child in server.m_replicables.GetChildren(parent))
		{
			if (client.HasReplicable(child))
				CollectMissing(server, client, child);
			// Non-spatial only: those follow their parent unconditionally (RefreshReplicable). A spatial
			// child can be out of this client's update layer on purpose, and re-adding it would fight vanilla.
			else if (!child.IsSpatial && child.IsReadyForReplication && child.IsValid)
				m_missing.Add(child);
		}
	}

	private static string Describe(IMyReplicable replicable)
	{
		object instance = (replicable as Sandbox.Game.Replication.MyExternalReplicable)?.GetInstance();
		return instance is VRage.Game.Entity.MyEntity entity
			? entity.GetType().Name + " " + entity.EntityId.ToString(CultureInfo.InvariantCulture)
			: replicable.GetType().Name;
	}
}
