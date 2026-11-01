using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace ClusterPlugin;

/// <summary>
/// T-0354: what the membership pass needs from the registry, without the game thread ever waiting for it. A split, a
/// merge or a new layout stays applied in memory; its registry request (a topology apply, or the claim of a new
/// layout's id) is queued here and sent by a worker, in order, the same request again until the registry ANSWERS -
/// the topology apply is idempotent by its OpId, so a lost answer is harmless. The game thread takes the answers on
/// its next update. Until then every layout the request names is locked (no split, merge, adoption, save, capture):
/// a change may not be reverted on a mere timeout - the registry may have applied it - and nothing may be built on it
/// before the registry is known to have it. Partition ids come from a reserve the same worker keeps filled.
/// </summary>
/// <remarks>
/// T-0350 stopped NEW changes while the registry is unreachable (PartitionManager.RegistryHold). A change already
/// under way when the outage began still retried inside PartitionManager.Evaluate - the simulation stood still until
/// the registry answered. Plain data and delegates, no game types: Tools/MembershipEquivalence runs the scenarios too.
/// </remarks>
internal sealed class RegistryApplyQueue
{
	internal enum Outcome
	{
		/// <summary>The registry applied it (now, or on an earlier attempt whose answer was lost).</summary>
		Applied,
		/// <summary>Refused because the registry is phasing this node out: revert, hold the topology, drain.</summary>
		Closed,
		/// <summary>A definitive refusal: the node fences.</summary>
		Rejected,
		/// <summary>No usable answer; the same request is sent again.</summary>
		Unavailable,
		/// <summary>
		/// T-0339 (design C): a topology transfer answered 400 - a registry that does not apply transfers refuses one as
		/// a malformed split and changes nothing. Revert it and what is queued behind it as for <see cref="Closed"/>, stop
		/// proposing transfers; no fence, no hold.
		/// </summary>
		TransferRefused,
	}

	internal enum Progress { Idle, Unavailable, Answered }

	internal sealed class Operation
	{
		/// <summary>The membership change to apply; null for the claim of a new layout (<see cref="Claim"/>).</summary>
		public PartitionMembership.Change Change;
		public ulong Claim;
		/// <summary>The request body, built on the game thread when the change was made (generations, members, OpId).</summary>
		public object Request;
		public long Epoch;
		public int Attempts;
		public Outcome Outcome;
		/// <summary>Applied: the partitions the registry answered with, and their generations.</summary>
		public (ulong Partition, uint Generation)[] Answer;
		/// <summary>The ownership the registry holds for this node once it has applied the request (see <see cref="Expected"/>).</summary>
		public (ulong Partition, uint Generation)[] Expect = Array.Empty<(ulong, uint)>();
		public string Error;

		/// <summary>Every layout id the request names: locked from the change until the game thread took the answer.</summary>
		public ulong[] Layouts => Change == null ? new[] { Claim }
			: Change.Inputs.Concat(Change.Outputs.Select(x => x.Id)).Distinct().ToArray();
	}

	private readonly object m_lock = new object();
	private readonly Queue<Operation> m_waiting = new Queue<Operation>();
	private readonly Queue<Operation> m_answered = new Queue<Operation>();
	private readonly List<ulong> m_reserve = new List<ulong>();
	private int m_shortfall;
	private bool m_asked;
	// A refusal waits for the game thread: what is queued behind it was made on top of it and is not sent.
	private bool m_halted;
	// Copy on write: the membership pass asks per layout, per pair in the merge search - no lock on that path.
	private volatile HashSet<ulong> m_locked = new HashSet<ulong>();
	// T-0354b: the layouts a queued or untaken TOPOLOGY request names (a subset of m_locked); same copy on write.
	private volatile HashSet<ulong> m_topology = new HashSet<ulong>();

	/// <summary>True from the change (or the id leaving the reserve) until the game thread took the registry's answer. Any thread.</summary>
	public bool IsPending(ulong layout)
	{
		HashSet<ulong> locked = m_locked;
		return locked.Count != 0 && locked.Contains(layout);
	}

	/// <summary>
	/// T-0354b: pending, but no topology request names it - its id left the reserve for a new layout made around
	/// unassigned members, and the only request about it is the claim of that id (queued, or queued at the end of this
	/// pass). Such a layout may ADOPT (<see cref="AdoptionLock"/>): an adoption is a local change of members that the
	/// registry learns from the layout's first save and report, exactly as it learns the members the layout was made
	/// around, and the claim is about the id. A split's or merge's layout stays locked against adoption until the apply
	/// is answered: Revert puts the inputs back and keeps an output only for what it holds beyond them - an adopted
	/// member would keep alive a layout the registry never made. A split output taken in this pass is not named yet
	/// either, but the adoption step runs before the split step in PartitionMembership.Evaluate and the pass names
	/// every output before it returns, so no adoption sees one. Any thread.
	/// </summary>
	public bool IsClaimOnly(ulong layout)
	{
		HashSet<ulong> locked = m_locked;
		return locked.Count != 0 && locked.Contains(layout) && !m_topology.Contains(layout);
	}

	/// <summary>
	/// T-0354b: the lock of the membership pass's ADOPTION step. <paramref name="locked"/>: the layouts locked for a
	/// reason other than ownership and the registry (frozen members, the held id); <paramref name="owned"/>:
	/// the registry's ownership (null = no registry); <paramref name="pending"/> and <paramref name="claimOnly"/>:
	/// <see cref="IsPending"/> and <see cref="IsClaimOnly"/> (null under a hold: a claim-only layout stays locked). A layout whose only unanswered request is the claim of its
	/// own id is neither owned yet nor free of a request, and both used to lock it: the second grid of a pair spawned a
	/// frame apart got a layout of its own, and the two waited the 10 s maturity delay before they could merge - a
	/// handover in that window moved one grid without the other (P3-HND-02, 2026-10-03). Splits and merges keep the
	/// full lock (PartitionMembership's own predicate).
	/// </summary>
	internal static Func<ulong, bool> AdoptionLock(Func<ulong, bool> locked, Func<ulong, bool> owned, Func<ulong, bool> pending,
		Func<ulong, bool> claimOnly)
		=> id => locked(id) || (owned != null && !owned(id) || pending != null && pending(id)) && claimOnly?.Invoke(id) != true;

	/// <summary>
	/// T-0347 x F39 (#1284): why the local stop does not capture an owned layout's Final yet, or null. A layout whose
	/// topology apply is unanswered is the node's view of a change the registry may not have made: a split that kept the
	/// id holds only the members it kept, and the next start would catalogue that Final as the layout's newest save while
	/// the split-off layout has no row - its members lost. Such a layout is waited for (the worker keeps sending the
	/// request) and, at the deadline, left to its last registered save. Every other capture path checks the same.
	/// </summary>
	internal static string LocalStopBlocked(bool saveInFlight, bool membersInitializing, bool frozen, bool registryPending)
		=> saveInFlight ? "save in flight" : membersInitializing ? "members initializing" : frozen ? "frozen"
			: registryPending ? "registry pending" : null;

	/// <summary>Requests not yet answered, plus answers the game thread has not taken.</summary>
	public int Count { get { lock (m_lock) return m_waiting.Count + m_answered.Count; } }

	public int Reserve { get { lock (m_lock) return m_reserve.Count; } }

	/// <summary>
	/// Heartbeat thread: what this node owns IF the registry has applied the requests whose answer the game thread
	/// has not taken. Reported beside the known ownership so the lease of such a row is renewed: a request applied
	/// with its answer lost leaves a row the node does not know it owns, and unreported its lease lapses - the
	/// registry would then offer for recovery a partition this node is simulating. A row the registry does not hold
	/// at that generation is ignored by its heartbeat.
	/// </summary>
	public (ulong Partition, uint Generation)[] Expected()
	{
		lock (m_lock) return m_waiting.Concat(m_answered).SelectMany(x => x.Expect).ToArray();
	}

	/// <summary>
	/// Game thread: <paramref name="count"/> ids from the reserve, locked until their request is answered, or null when
	/// the reserve is short - the caller leaves that change for a later pass, it never waits. An id the node already
	/// knows (<paramref name="known"/>: a fresh registry restarts its id space) is dropped.
	/// </summary>
	public ulong[] Take(int count, Func<ulong, bool> known)
	{
		lock (m_lock)
		{
			m_asked = true;
			m_reserve.RemoveAll(id => known(id));
			if (m_reserve.Count < count)
			{
				m_shortfall = Math.Max(m_shortfall, count);
				return null;
			}
			ulong[] ids = m_reserve.GetRange(0, count).ToArray();
			m_reserve.RemoveRange(0, count);
			Lock(ids);
			return ids;
		}
	}

	/// <summary>
	/// Worker: how many ids to ask the registry for now (0: the reserve is full enough). Nothing before the node's
	/// first request for an id: a node that never makes a partition (a dormant spare, the World Authority) asks for none.
	/// </summary>
	public int Wanted(int target)
	{
		lock (m_lock)
			return !m_asked || m_shortfall == 0 && m_reserve.Count > target / 2 ? 0 : Math.Max(0, target + m_shortfall - m_reserve.Count);
	}

	/// <summary>Worker: ids the registry allocated for this node.</summary>
	public void Refill(IEnumerable<ulong> ids)
	{
		lock (m_lock)
		{
			m_reserve.AddRange(ids);
			m_shortfall = 0;
		}
	}

	/// <summary>Game thread: queue the request; its layouts are locked. Never sends.</summary>
	public void Enqueue(Operation operation)
	{
		lock (m_lock)
		{
			// The topology set first: an id a claim-only check sees locked is never briefly "claim only" for a topology request.
			if (operation.Change != null) m_topology = new HashSet<ulong>(m_topology.Concat(operation.Layouts));
			Lock(operation.Layouts);
			m_waiting.Enqueue(operation);
		}
	}

	/// <summary>
	/// Worker: one attempt at the oldest request. <paramref name="send"/> runs outside the lock and may take as long
	/// as its timeout. Unavailable leaves the request at the head, to be sent again unchanged.
	/// </summary>
	public Progress Step(Func<Operation, Outcome> send)
	{
		Operation head;
		lock (m_lock)
		{
			if (m_halted || m_waiting.Count == 0) return Progress.Idle;
			head = m_waiting.Peek();
		}
		head.Attempts++;
		Outcome outcome = send(head);
		if (outcome == Outcome.Unavailable) return Progress.Unavailable;
		lock (m_lock)
		{
			// Abandoned meanwhile (a fence): the answer has nobody to take it.
			if (m_waiting.Count == 0 || !ReferenceEquals(m_waiting.Peek(), head)) return Progress.Idle;
			head.Outcome = outcome;
			m_waiting.Dequeue();
			m_answered.Enqueue(head);
			if (outcome != Outcome.Applied) m_halted = true;
		}
		return Progress.Answered;
	}

	/// <summary>Game thread: the next answered request, in the order the changes were made. Call <see cref="Finish"/> after applying it.</summary>
	public bool TryTakeAnswered(out Operation operation)
	{
		lock (m_lock) return m_answered.TryDequeue(out operation);
	}

	/// <summary>
	/// Game thread: the answer is applied (or the change reverted); its layouts are free again - those that no other
	/// request still names. A merge and a split keep an input's id as an output (T-0339: the largest group of a
	/// distance split), and the layout under it is not locked during the pass that made the change, so the same pass
	/// can change it again: a split X -> X, Y and then a merge of X with Z queue two requests over X. Freed with the
	/// first answer, X would be saved, split or merged while the registry does not have the merge yet.
	/// </summary>
	public void Finish(Operation operation) => Unlock(operation.Layouts);

	/// <summary>
	/// Game thread, when a change is queued: the generation an input has once every queued request is applied - the
	/// one the last request naming it as an output expects, else <paramref name="known"/> (the ownership the node
	/// holds). The registry checks an input's generation (RequireOwner); built from the known ownership, the second
	/// of two changes over one id in one pass named the generation the first one retires and was refused for good.
	/// Before T-0354 each change committed before the next one's request was built.
	/// </summary>
	public uint InputGeneration(ulong partition, uint known)
	{
		lock (m_lock)
		{
			// Oldest first: what the game thread has not taken yet, then what is not answered yet.
			foreach (Operation operation in m_answered.Concat(m_waiting))
				foreach ((ulong id, uint generation) in operation.Expect)
					if (id == partition) known = generation;
			return known;
		}
	}

	/// <summary>
	/// Game thread, after a refusal: every request that was never sent, in the order the changes were made - the
	/// caller reverts them LAST FIRST. The worker resumes with whatever is queued afterwards.
	/// </summary>
	public Operation[] Abandon()
	{
		lock (m_lock)
		{
			Operation[] rest = m_waiting.ToArray();
			m_waiting.Clear();
			m_halted = false;
			return rest;
		}
	}

	/// <summary>Game thread: ids taken from the reserve that no request will name after all. An id a queued or untaken request names stays locked.</summary>
	public void Unlock(IEnumerable<ulong> layouts)
	{
		lock (m_lock)
		{
			HashSet<ulong> named = m_waiting.Concat(m_answered).SelectMany(x => x.Layouts).ToHashSet();
			HashSet<ulong> locked = new HashSet<ulong>(m_locked);
			locked.ExceptWith(layouts.Where(id => !named.Contains(id)));
			m_locked = locked;
			m_topology = m_waiting.Concat(m_answered).Where(x => x.Change != null).SelectMany(x => x.Layouts).ToHashSet();
		}
	}

	private void Lock(IEnumerable<ulong> layouts)
	{
		HashSet<ulong> locked = new HashSet<ulong>(m_locked);
		locked.UnionWith(layouts);
		m_locked = locked;
	}

	/// <summary>
	/// What a failed request means. Same rules as the loops this replaces: a 409 on a claim, or a topology apply
	/// refused with "is Closed", is the registry phasing the node out (T-0048: not a fence); any other 4xx is
	/// definitive (ClusterRuntime.IsDefinitiveRejection); everything else - no answer, a timeout, 5xx - is retried.
	/// T-0339 (design C): a 400 on a <paramref name="transfer"/> is <see cref="Outcome.TransferRefused"/>, not a fence.
	/// </summary>
	public static Outcome Classify(bool claim, Exception exception, bool transfer = false)
	{
		if (exception is not HttpRequestException { StatusCode: { } status }) return Outcome.Unavailable;
		if (transfer && !claim && status == HttpStatusCode.BadRequest) return Outcome.TransferRefused;
		if (status == HttpStatusCode.Conflict && (claim || exception.Message.Contains(" is Closed", StringComparison.Ordinal)))
			return Outcome.Closed;
		return (int)status >= 400 && (int)status < 500 ? Outcome.Rejected : Outcome.Unavailable;
	}
}

/// <summary>
/// T-0354b: work refused because a registry request about the partition is unanswered (the claim of a new layout's id,
/// a topology apply). Refused before anything is frozen, and it resolves by itself once the registry answers - for a
/// claim within a frame or two of the layout's creation - so the node answers it 409 and the gateway retries a
/// requested handover briefly (DirectGateway.WhenRegistryApplied) instead of failing it.
/// </summary>
internal sealed class RegistryApplyPendingException(string message) : InvalidOperationException(message);
