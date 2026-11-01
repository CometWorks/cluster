using System;
using System.Collections.Generic;

namespace ClusterPlugin;

/// <summary>
/// T-0396: what a client-view adopt re-sends, and what it destroys first, for the replicables the client had not
/// finished receiving at the export (ReplicationClientViewPatch.Adopt, client_view_redeliver).
///
/// A redeliver is RemoveForClient + AddForClient: one destroy for the replicable, then a fresh create. On a vanilla
/// client that destroy takes one of two branches (MyReplicationClient.ProcessReplicationDestroy). For a replicable
/// it holds READY it destroys the replicable and every child in its own hierarchy. For one still PENDING it only
/// drops pending entries (PendingReplicableDestroy): a child the client already holds ready is left registered
/// under its network ids. Vanilla never meets that case - its server does not remove a replicable the client has
/// not acknowledged (ScheduleStateGroupSync: "not yet replicated on client") - but a redeliver is exactly that
/// case. The server's RemoveForClient drops the whole subtree from its view of the client, the create sends it
/// again, and the client answers 'Replicated object already exists' for every id of it, every time the grid comes
/// back into view; the new copy's events are 'not recognized by replication'. Field: a character seated in a grid
/// that was still streaming to a viewer (client-52 / 'Visitor Ion', 2026-10-03 16:17:57; bench P3-HND-04).
///
/// So, per redelivered replicable: first an explicit destroy for every descendant the client's view holds,
/// parents before children - the ready branch for the ready ones, the pending branch for the pending ones,
/// both of which the client handles cleanly - then the replicable's own destroy and create. Two exceptions:
/// <list type="bullet">
/// <item>A pending replicable below another one being re-delivered is not re-delivered on its own: the
/// ancestor's RemoveForClient already takes it out of the server's view, and the ancestor's re-send brings it
/// back with its parent. Re-delivering it separately would create it under a parent the client no longer has
/// (the client drops such a create, and the node then waits for it forever).</item>
/// <item>The client's own character and controlled entity, and everything below them, are never destroyed: a
/// client that loses its own character mid-session is worse off than one with a stale id.</item>
/// </list>
/// </summary>
internal static class RedeliverPolicy
{
	public readonly struct Plan<T>
	{
		public Plan(List<T> redeliver, List<T> destroy, List<T> kept)
		{
			Redeliver = redeliver;
			Destroy = destroy;
			Kept = kept;
		}

		/// <summary>The replicables to destroy and create again, in the order given.</summary>
		public List<T> Redeliver { get; }
		/// <summary>Descendants to destroy explicitly before that, parents before children.</summary>
		public List<T> Destroy { get; }
		/// <summary>Descendants left alone because they are (or are below) the client's own bindings.</summary>
		public List<T> Kept { get; }
	}

	/// <param name="pending">What the export held as pending, in adopt order.</param>
	/// <param name="children">The node's replicable hierarchy (the one RemoveForClient walks).</param>
	/// <param name="inView">Whether the client's adopted view holds the replicable.</param>
	/// <param name="binding">The client's own character or controlled entity.</param>
	public static Plan<T> Decide<T>(IEnumerable<T> pending, Func<T, IEnumerable<T>> children, Func<T, bool> inView, Func<T, bool> binding)
	{
		List<T> order = new List<T>(pending);
		HashSet<T> below = new HashSet<T>();
		foreach (T item in order)
			Walk(item, children, new HashSet<T> { item }, descendant => { below.Add(descendant); return true; });
		List<T> redeliver = new List<T>();
		List<T> destroy = new List<T>();
		List<T> kept = new List<T>();
		HashSet<T> listed = new HashSet<T>();
		foreach (T item in order)
		{
			if (below.Contains(item) || !listed.Add(item)) continue;
			redeliver.Add(item);
			Walk(item, children, new HashSet<T> { item }, descendant =>
			{
				if (!inView(descendant)) return true;
				if (binding(descendant)) { kept.Add(descendant); return false; }
				destroy.Add(descendant);
				return true;
			});
		}
		return new Plan<T>(redeliver, destroy, kept);
	}

	/// <summary>Pre-order over the descendants of <paramref name="root"/>; <paramref name="visit"/> false skips a subtree.</summary>
	private static void Walk<T>(T root, Func<T, IEnumerable<T>> children, HashSet<T> seen, Func<T, bool> visit)
	{
		foreach (T child in children(root) ?? Array.Empty<T>())
		{
			if (!seen.Add(child)) continue;
			if (visit(child)) Walk(child, children, seen, visit);
		}
	}
}
