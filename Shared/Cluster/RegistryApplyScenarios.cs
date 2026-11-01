using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ClusterPlugin;

/// <summary>
/// T-0354: the membership pass against a registry that goes silent between the in-memory change and its answer, over
/// the live <see cref="PartitionMembership"/> and <see cref="RegistryApplyQueue"/> and a registry stand-in with the
/// registry's rules (ids must be allocated, inputs owned at their generation, an OpId answered again as recorded).
/// Plain data, no game types: the node runs it as self-test section registry-apply-async, Tools/MembershipEquivalence
/// as "apply". Each control runs the request the way the retry loops did and must show the defect.
/// </summary>
internal static class RegistryApplyScenarios
{
	private const double Distance = 3000, Margin = 300;
	private const int Target = 16;

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException("registry-apply-async: " + message);
	}

	private static PartitionMembership.Member At(long id, double x) => new PartitionMembership.Member(id, x, 0, 0, 0, 0, 0);

	private static string Shape(PartitionMembership membership) => string.Join(" | ", membership.Layouts.OrderBy(layout => layout.Id)
		.Select(layout => layout.Id + ":" + string.Join(",", layout.Members.OrderBy(id => id))));

	/// <summary>A topology request as the node builds it: the OpId and the inputs at the generations it names.</summary>
	private sealed record Request(string OpId, (ulong Partition, uint Generation)[] Inputs);

	/// <summary>The registry's side of the three requests, with a switch that makes it silent and one that loses the next answer.</summary>
	private sealed class Registry
	{
		public readonly Dictionary<ulong, uint> Owned = new Dictionary<ulong, uint>();
		public readonly HashSet<ulong> Allocated = new HashSet<ulong>();
		public readonly Dictionary<string, (ulong, uint)[]> Applied = new Dictionary<string, (ulong, uint)[]>();
		public bool Silent, LoseAnswer, Closed;
		/// <summary>T-0339 (design C): a registry without transfers - a two-input split is a malformed split (400).</summary>
		public bool NoTransfers;
		public int Requests, Applies;
		private ulong m_next = 100;

		public ulong[] Allocate(int count)
		{
			ulong[] ids = Enumerable.Range(0, count).Select(_ => m_next++).ToArray();
			Allocated.UnionWith(ids);
			return ids;
		}

		/// <summary>Throws what the node's HTTP helpers throw; returns the outputs.</summary>
		public (ulong, uint)[] Handle(RegistryApplyQueue.Operation operation, string opId)
		{
			lock (this)
			{
				Requests++;
				if (Silent) throw new HttpRequestException("connection refused");
				(ulong, uint)[] answer;
				if (operation.Change == null)
				{
					bool replay = Owned.ContainsKey(operation.Claim);
					if (Closed) throw new HttpRequestException("Node 'n1' epoch 1 is Closed", null, HttpStatusCode.Conflict);
					// As GatewayRegistry.ClaimPartition: the generation expectation is judged before the owner.
					if (replay && operation.Attempts == 1) throw new HttpRequestException("is generation 1, not 0", null, HttpStatusCode.Conflict);
					if (!replay) { Owned[operation.Claim] = 1; Applies++; }
					answer = new[] { (operation.Claim, Owned[operation.Claim]) };
				}
				else if (Applied.TryGetValue(opId, out (ulong, uint)[] recorded)) answer = recorded;   // T-0308: also when Closed
				else
				{
					if (Closed) throw new HttpRequestException("Node 'n1' epoch 1 is Closed", null, HttpStatusCode.Conflict);
					PartitionMembership.Change change = operation.Change;
					// As GatewayRegistry.ApplyTopology before transfers: the request is validated before any ownership rule.
					if (NoTransfers && change.Split && change.Inputs.Length != 1)
						throw new HttpRequestException("Split requires one input and every output's member list", null, HttpStatusCode.BadRequest);
					// As GatewayRegistry.RequireOwner: every input owned by the node AT the generation the request names.
					if (((Request)operation.Request).Inputs.Any(x => !Owned.TryGetValue(x.Partition, out uint owned) || owned != x.Generation))
						throw new HttpRequestException("Partition ownership is fenced", null, HttpStatusCode.Conflict);
					if (change.Outputs.Any(x => !change.Inputs.Contains(x.Id) && !Allocated.Contains(x.Id)))
						throw new HttpRequestException("Partition was not allocated", null, HttpStatusCode.Conflict);
					answer = change.Outputs.Select(x => (x.Id, (Owned.TryGetValue(x.Id, out uint generation) ? generation : 0) + 1)).ToArray();
					foreach (ulong input in change.Inputs) Owned.Remove(input);
					foreach ((ulong id, uint generation) in answer) Owned[id] = generation;
					Applied[opId] = answer;
					Applies++;
				}
				if (LoseAnswer)
				{
					LoseAnswer = false;
					Silent = true;   // the outage begins with this request under way
					throw new TaskCanceledException("timed out");
				}
				return answer;
			}
		}
	}

	/// <summary>
	/// One node: the membership, its queue, and the sender the worker runs. <paramref name="freshOpId"/> and
	/// <paramref name="knownGenerations"/> (inputs named at the generation the node holds, not the one the queue
	/// gives them) are controls.
	/// </summary>
	private sealed class Node
	{
		public readonly Registry Registry = new Registry();
		public readonly RegistryApplyQueue Queue = new RegistryApplyQueue();
		public readonly PartitionMembership Membership;
		public readonly HashSet<ulong> Owned = new HashSet<ulong>();
		public readonly Dictionary<ulong, uint> Generations = new Dictionary<ulong, uint>();
		private readonly bool m_freshOpId, m_knownGenerations, m_keepUnclaimed, m_plainClassify;
		private int m_operations;
		/// <summary>T-0339 (design C): set by a transfer's 400, as PartitionPersistence.m_transferRefused.</summary>
		public bool TransferRefused;

		/// <param name="claimLocks">Control: adoption under the lock splits and merges use (candC before T-0354b).</param>
		/// <param name="keepUnclaimed">Control: a refused claim's layout is kept as an unregistered placeholder (candC).</param>
		/// <param name="transfer">T-0339 (design C): transfers on (a registry that advertises TopologyTransfer).</param>
		/// <param name="plainClassify">Control: a transfer's refusal classified like any other topology apply (design C's
		/// synchronous rule without its transfer branch).</param>
		/// <param name="lockIgnoresPending">Control: the membership's lock without the registry's pending requests.</param>
		public Node(bool freshOpId = false, bool knownGenerations = false, bool claimLocks = false, bool keepUnclaimed = false,
			bool transfer = false, bool plainClassify = false, bool lockIgnoresPending = false)
		{
			m_freshOpId = freshOpId;
			m_knownGenerations = knownGenerations;
			m_keepUnclaimed = keepUnclaimed;
			m_plainClassify = plainClassify;
			// As PartitionManager wires it: unowned and pending layouts are locked, the allocator is the reserve.
			Membership = new PartitionMembership(Distance, Margin, count => Queue.Take(count, _ => false),
				id => !Owned.Contains(id) || !lockIgnoresPending && Queue.IsPending(id)) { TransferEnabled = transfer };
			// T-0354b: as PartitionManager wires the adoption step (nothing frozen or held here).
			if (!claimLocks)
				Membership.AdoptionLocked = RegistryApplyQueue.AdoptionLock(_ => false, Owned.Contains, Queue.IsPending, Queue.IsClaimOnly);
		}

		/// <summary>
		/// The hold a Closed refusal sets (HoldTopology), as PartitionManager wires it: adoption up to the split distance,
		/// into owned layouts with no request pending. Nothing new is made here (a Closed node gives a far stray a local placeholder).
		/// </summary>
		public void Hold()
		{
			Membership.HoldAdoptionLocked = RegistryApplyQueue.AdoptionLock(_ => false, Owned.Contains, Queue.IsPending, null);
			Membership.DeferNewLayouts = true;
		}

		public void Refill()
		{
			int wanted = Queue.Wanted(Target);
			if (wanted != 0) Queue.Refill(Registry.Allocate(wanted));
		}

		/// <summary>The game thread's part of one pass: evaluate, queue the claims and the applies. No request is sent.</summary>
		public PartitionMembership.Change[] Pass(PartitionMembership.Member[] members, double now)
		{
			PartitionMembership.Change[] changes = Membership.Evaluate(members, now);
			foreach (ulong id in Membership.Created)
				Queue.Enqueue(new RegistryApplyQueue.Operation { Claim = id, Expect = new[] { (id, 1u) } });
			foreach (PartitionMembership.Change change in changes)
			{
				// As PartitionPersistence.ApplyTopology builds the request and its expectation.
				(ulong Partition, uint Generation)[] inputs = change.Inputs.Select(id =>
				{
					Generations.TryGetValue(id, out uint known);
					return (id, m_knownGenerations ? known : Queue.InputGeneration(id, known));
				}).ToArray();
				Queue.Enqueue(new RegistryApplyQueue.Operation { Change = change, Request = new Request("op-" + ++m_operations, inputs),
					Expect = change.Outputs.Select(x => (x.Id, inputs.Where(input => input.Partition == x.Id).Select(input => input.Generation).FirstOrDefault() + 1)).ToArray() });
			}
			return changes;
		}

		public RegistryApplyQueue.Outcome Send(RegistryApplyQueue.Operation operation)
		{
			try
			{
				operation.Answer = Registry.Handle(operation, m_freshOpId || operation.Change == null ? Guid.NewGuid().ToString("N") : ((Request)operation.Request).OpId);
				return RegistryApplyQueue.Outcome.Applied;
			}
			catch (Exception exception)
			{
				operation.Error = exception.Message;
				return RegistryApplyQueue.Classify(operation.Change == null, exception, !m_plainClassify && operation.Change?.Transfer == true);
			}
		}

		/// <summary>The game thread's part of taking the answers, as PartitionPersistence.CompleteRegistryApplies. Returns the refusal, if any.</summary>
		public RegistryApplyQueue.Outcome? Complete()
		{
			RegistryApplyQueue.Outcome? refusal = null;
			while (Queue.TryTakeAnswered(out RegistryApplyQueue.Operation operation))
			{
				if (operation.Outcome == RegistryApplyQueue.Outcome.Applied)
				{
					// Inputs out, THEN outputs in: an output may be an input (a merge, T-0339's split).
					if (operation.Change != null)
						foreach (ulong input in operation.Change.Inputs) { Owned.Remove(input); Generations.Remove(input); }
					foreach ((ulong id, uint generation) in operation.Answer) { Owned.Add(id); Generations[id] = generation; }
					Queue.Finish(operation);
					continue;
				}
				refusal = operation.Outcome;
				if (operation.Outcome == RegistryApplyQueue.Outcome.Rejected) break;
				// T-0339 (design C): as PartitionPersistence - transfers off for the process (RegistryTransfer false, so the
				// next pass's TransferEnabled is false), then the same revert as a Closed refusal, without the hold.
				if (operation.Outcome == RegistryApplyQueue.Outcome.TransferRefused)
				{
					TransferRefused = true;
					Membership.TransferEnabled = false;
				}
				RegistryApplyQueue.Operation[] unsent = Queue.Abandon();
				foreach (PartitionMembership.Change change in unsent.Prepend(operation).Where(x => x.Change != null).Select(x => x.Change).Reverse())
					Membership.Revert(change);
				// T-0354b: as PartitionManager.RevertTopology - the layouts of refused and unsent claims are dissolved.
				if (!m_keepUnclaimed)
					foreach (ulong claim in unsent.Prepend(operation).Where(x => x.Change == null).Select(x => x.Claim))
						Membership.Remove(claim);
				Queue.Finish(operation);
				foreach (RegistryApplyQueue.Operation other in unsent) Queue.Finish(other);
			}
			return refusal;
		}

		/// <summary>Sends and takes every queued request; the first refusal, if any.</summary>
		public RegistryApplyQueue.Outcome? Drain()
		{
			while (Queue.Step(Send) == RegistryApplyQueue.Progress.Answered)
				if (Complete() is { } refusal) return refusal;
			return null;
		}

		/// <summary>Two members in one owned layout, the reserve filled and asked once.</summary>
		public ulong Start()
		{
			Check(Pass(new[] { At(1, 0), At(2, 100) }, 0).Length == 0 && Membership.Layouts.Count == 0,
				"with no id at hand the first pass must make no layout: " + Shape(Membership));
			Check(Registry.Requests == 0, "a pass sent a registry request");
			Refill();
			Pass(new[] { At(1, 0), At(2, 100) }, 1);
			Check(Membership.Layouts.Count == 1 && Queue.IsPending(Membership.Layouts[0].Id), "the new layout must exist and be pending its claim");
			Check(Queue.Step(Send) == RegistryApplyQueue.Progress.Answered && Complete() == null, "the claim must be answered");
			ulong id = Membership.Layouts[0].Id;
			Check(Owned.Contains(id) && !Queue.IsPending(id), "the claimed layout must be owned and free");
			return id;
		}
	}

	private static readonly PartitionMembership.Member[] Apart = { At(1, 0), At(2, 50000) };
	private static readonly PartitionMembership.Member[] Together = { At(1, 0), At(2, 100) };

	public static string[] Run()
	{
		List<string> ran = new List<string>();
		Reserve(); ran.Add("reserve");
		ShortAllocator(); ran.Add("short-allocator");
		LostAnswer(); ran.Add("lost-answer");
		SamePassKeptId(); ran.Add("same-pass-kept-id");
		CallerNeverWaits(); ran.Add("caller-never-waits");
		LostClaimAnswer(); ran.Add("lost-claim-answer");
		ClosedRevert(); ran.Add("closed-revert");
		ClaimPendingAdoption(); ran.Add("claim-pending-adoption");
		RefusedClaimDissolves(); ran.Add("refused-claim-dissolves");
		TransferRefusedReverts(); ran.Add("transfer-refused-reverts");
		TransferTargetPending(); ran.Add("transfer-target-pending");
		TransferShortAllocator(); ran.Add("transfer-short-allocator");
		LocalStopPendingApply(); ran.Add("local-stop-pending-apply");
		ClassifyRules(); ran.Add("classify");
		return ran.ToArray();
	}

	/// The reserve: nothing is asked before the node's first request, a short reserve answers null and says how many
	/// to fetch, a locally known id is dropped, and an id is pending from the moment it leaves the reserve.
	private static void Reserve()
	{
		RegistryApplyQueue queue = new RegistryApplyQueue();
		Check(queue.Wanted(Target) == 0, "a node that never asked for an id must not fetch any");
		Check(queue.Take(40, _ => false) == null && queue.Wanted(Target) == Target + 40, "a short reserve must answer null and want target + the request: " + queue.Wanted(Target));
		queue.Refill(Enumerable.Range(1, 56).Select(x => (ulong)x));
		ulong[] taken = queue.Take(40, id => id == 2);
		Check(taken != null && taken.Length == 40 && !taken.Contains(2UL) && taken.All(queue.IsPending) && !queue.IsPending(50),
			"40 ids must come out, the known one dropped, each pending");
		Check(queue.Reserve == 15 && queue.Wanted(Target) == 0, "15 of 16 left: no refill yet (" + queue.Reserve + ", " + queue.Wanted(Target) + ")");
		queue.Take(7, _ => false);
		Check(queue.Wanted(Target) == 8, "at half the target the reserve is topped up: " + queue.Wanted(Target));
		queue.Unlock(taken);
		Check(!taken.Any(queue.IsPending), "unlocked ids must not stay pending");
	}

	/// The membership with an allocator that has no ids: nothing is rewritten, nothing throws, and the pass that
	/// finds ids does the deferred work. (Before T-0354 the allocator could not say no: it waited for the registry.)
	private static void ShortAllocator()
	{
		Node node = new Node();
		ulong home = node.Start();
		string before = Shape(node.Membership);
		// The reserve is drained: the split that is due, and a stray far away, both need ids.
		node.Queue.Take(node.Queue.Reserve, _ => false);
		PartitionMembership.Member[] apartWithStray = { At(1, 0), At(2, 50000), At(3, 200000) };
		for (int pass = 0; pass < 3; pass++)
			Check(node.Pass(apartWithStray, 100 + pass).Length == 0 && Shape(node.Membership) == before && node.Membership.Created.Count == 0,
				"a pass without ids rewrote the layouts: " + Shape(node.Membership));
		Check(node.Registry.Requests == 1, "a pass without ids sent a request (only the first claim was ever sent): " + node.Registry.Requests);
		node.Refill();
		PartitionMembership.Change[] changes = node.Pass(apartWithStray, 200);
		Check(changes.Length == 1 && changes[0].Split && changes[0].Inputs.Single() == home && node.Membership.Created.Count == 1,
			"with ids at hand the pass must split the layout and make the stray's: " + Shape(node.Membership));
	}

	/// The ticket's case: the outage starts between the in-memory split and the registry's answer, and the answer
	/// is lost AFTER the registry applied the split. The split stays applied and locked, the same request is sent
	/// until answered, the registry applies it once. Control: a retry under a new OpId is refused for good (a fence).
	private static void LostAnswer()
	{
		foreach (bool control in new[] { false, true })
		{
			Node node = new Node(freshOpId: control);
			ulong home = node.Start();
			node.Registry.LoseAnswer = true;
			int requests = node.Registry.Requests;
			PartitionMembership.Change[] changes = node.Pass(Apart, 100);
			Check(changes.Length == 1 && changes[0].Split, "the drifted layout must split in memory");
			Check(node.Registry.Requests == requests, "the pass itself sent the topology request");
			ulong[] outputs = changes[0].Outputs.Select(x => x.Id).ToArray();
			string split = Shape(node.Membership);
			Check(node.Queue.IsPending(home) && outputs.All(node.Queue.IsPending), "input and outputs must be pending");
			Check(outputs.Length == 2 && outputs.Contains(home), "T-0339: the larger group (a tie: the first) must keep the input's id: " + string.Join(",", outputs));
			Check(node.Queue.Expected().Select(x => x.Partition).SequenceEqual(outputs), "the heartbeat must report the rows the split makes, to keep their leases");
			node.Membership.DeferNewLayouts = true;   // as the registry hold does, for the stray below
			// First attempt: applied by the registry, answer lost, the registry silent from here on.
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Unavailable && node.Registry.Applies == 2, "the registry must have applied the split, the node must not know");
			for (int second = 0; second < 300; second++)
			{
				// A five-minute silence: the simulation goes on, the members even come back together, a stray appears
				// beside them - nothing is built on the unanswered split, nothing is reverted, no pass sends anything.
				int sent = node.Registry.Requests;
				Check(node.Pass(second % 2 == 0 ? Together : new[] { At(1, 0), At(2, 50000), At(4, 50) }, 200 + second).Length == 0
					&& Shape(node.Membership) == split, "a pass changed layouts under an unanswered apply: " + Shape(node.Membership));
				Check(node.Registry.Requests == sent, "a pass sent a request during the outage");
				Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Unavailable && node.Complete() == null, "an unanswered request must stay queued");
			}
			Check(node.Owned.Contains(home) && node.Generations[home] == 1 && !outputs.Where(x => x != home).Any(node.Owned.Contains),
				"ownership must be untouched until the answer");
			node.Registry.Silent = false;
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered, "the registry is back: the request must be answered");
			RegistryApplyQueue.Outcome? refusal = node.Complete();
			if (control)
			{
				Check(refusal == RegistryApplyQueue.Outcome.Rejected,
					"control: a retry under a new OpId names retired inputs and must be refused for good (the node fences): " + refusal);
				continue;
			}
			Check(refusal == null && node.Registry.Applies == 2, "the repeat must be answered from the record, the split applied once: " + node.Registry.Applies);
			Check(outputs.All(node.Owned.Contains) && node.Generations[home] == 2 && node.Registry.Owned[home] == 2
				&& !outputs.Any(node.Queue.IsPending) && node.Queue.Count == 0,
				"after the answer the outputs are owned and free, the kept id at its next generation");
			// The stray is adopted and the pass is free to work on the outputs again.
			node.Membership.DeferNewLayouts = false;
			node.Pass(new[] { At(1, 0), At(2, 50000), At(4, 50) }, 1000);
			Check(node.Membership.Layouts.Any(layout => layout.Members.SequenceEqual(new long[] { 1, 4 })), "the released layout must adopt the stray: " + Shape(node.Membership));
		}
	}

	/// T-0339 x T-0354: one pass splits X -> X, Y (X keeps its id) and merges X with Z - two queued requests over X.
	/// The merge must name X at the generation the split gives it, and X must stay locked until the merge is
	/// answered, not only the split. Control: the merge built from the ownership the node holds names the generation
	/// the split retires and is refused for good (the node fences).
	private static void SamePassKeptId()
	{
		foreach (bool control in new[] { false, true })
		{
			Node node = new Node(knownGenerations: control);
			ulong home = node.Start();
			node.Pass(new[] { At(1, 0), At(2, 100), At(3, 200000) }, 2);
			ulong other = node.Membership.Created.Single();
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered && node.Complete() == null && node.Owned.Contains(other),
				"the second layout must be claimed");
			// 2 leaves past the split distance; 3 comes within D of 1. Both layouts are older than the merge delay.
			PartitionMembership.Change[] changes = node.Pass(new[] { At(1, 0), At(2, 50000), At(3, 100) }, 100);
			Check(changes.Length == 2 && changes[0].Split && changes[0].Outputs.Any(x => x.Id == home) && !changes[1].Split
				&& changes[1].Inputs.Contains(home) && changes[1].Inputs.Contains(other) && changes[1].Outputs.Single().Id == home,
				"one pass must split X keeping its id and merge X into the other layout: " + Shape(node.Membership));
			ulong piece = changes[0].Outputs.Single(x => x.Id != home).Id;
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered && node.Complete() == null, "the split must be applied");
			Check(node.Generations[home] == 2 && node.Owned.Contains(piece), "the split's answer must be taken");
			Check(node.Queue.IsPending(home) && node.Queue.IsPending(other) && !node.Queue.IsPending(piece),
				"X must stay locked while the merge over it is unanswered");
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered, "the merge must be answered");
			RegistryApplyQueue.Outcome? refusal = node.Complete();
			if (control)
			{
				Check(refusal == RegistryApplyQueue.Outcome.Rejected, "control: the merge naming X at the retired generation must be refused: " + refusal);
				continue;
			}
			Check(refusal == null && node.Generations[home] == 3 && node.Registry.Owned[home] == 3 && !node.Owned.Contains(other)
				&& node.Owned.Contains(piece) && node.Queue.Count == 0 && !node.Membership.Layouts.Any(x => node.Queue.IsPending(x.Id)),
				"after both answers X is owned at generation 3, Z is retired, nothing is locked");
		}
	}

	/// #1284 (F39): the local stop over a split the registry has not answered. X keeps its id with only the members it
	/// kept, Y is not owned yet: a Final of X then would become X's newest save at the next start (the registry never
	/// moved X on) and Y's members would be lost. X must wait as "registry pending"; once the registry answers, both
	/// layouts are captured and hold every member. Control: the selection without the registry-pending rule (before
	/// #1284) captures X alone, with a member missing.
	private static void LocalStopPendingApply()
	{
		foreach (bool control in new[] { false, true })
		{
			Node node = new Node();
			ulong home = node.Start();
			node.Registry.Silent = true;
			PartitionMembership.Change[] changes = node.Pass(Apart, 100);
			Check(changes.Length == 1 && changes[0].Split && changes[0].Outputs.Any(x => x.Id == home), "the layout must split keeping its id: " + Shape(node.Membership));
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Unavailable && node.Complete() == null, "the silent registry must leave the split unanswered");
			// As PartitionPersistence.UpdateLocalStop: every owned layout the predicate does not block (nothing saving, initializing or frozen here).
			PartitionMembership.Layout[] Captured() => node.Membership.Layouts.Where(x => node.Owned.Contains(x.Id)
				&& RegistryApplyQueue.LocalStopBlocked(false, false, false, !control && node.Queue.IsPending(x.Id)) == null).ToArray();
			PartitionMembership.Layout[] captured = Captured();
			if (control)
			{
				Check(captured.Length == 1 && captured[0].Id == home && captured[0].Members.Length == 1,
					"control: without the rule the stop must capture X alone with the members it kept (the partial Final): " + Shape(node.Membership));
				continue;
			}
			Check(captured.Length == 0 && RegistryApplyQueue.LocalStopBlocked(false, false, false, node.Queue.IsPending(home)) == "registry pending",
				"X must wait as registry pending while its split is unanswered: " + Shape(node.Membership));
			node.Registry.Silent = false;
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered && node.Complete() == null, "the registry is back: the split must be answered");
			captured = Captured();
			Check(captured.Length == 2 && captured.SelectMany(x => x.Members).OrderBy(x => x).SequenceEqual(new long[] { 1, 2 }),
				"after the answer both layouts must be captured with every member: " + Shape(node.Membership));
		}
	}

	/// The game thread never waits: with the worker blocked inside a request to a silent registry, passes go on
	/// and return. Control: the request sent the way the retry loop did - on the calling thread - returns only when
	/// the registry is back.
	private static void CallerNeverWaits()
	{
		Node node = new Node();
		node.Start();
		using ManualResetEventSlim inRequest = new ManualResetEventSlim(), registryBack = new ManualResetEventSlim();
		int attempts = 0;
		RegistryApplyQueue.Outcome Blocking(RegistryApplyQueue.Operation operation)
		{
			Interlocked.Increment(ref attempts);
			inRequest.Set();
			registryBack.Wait(TimeSpan.FromSeconds(30));
			return node.Send(operation);
		}
		node.Pass(Apart, 100);
		Task<RegistryApplyQueue.Progress> worker = Task.Run(() => node.Queue.Step(Blocking));
		Check(inRequest.Wait(TimeSpan.FromSeconds(30)), "the worker never sent the request");
		string split = Shape(node.Membership);
		for (int pass = 0; pass < 100; pass++) node.Pass(Together, 200 + pass);
		Check(!worker.IsCompleted && Volatile.Read(ref attempts) == 1 && Shape(node.Membership) == split && node.Complete() == null,
			"100 passes must run to the end while the request is unanswered, and change nothing");
		registryBack.Set();
		Check(worker.Wait(TimeSpan.FromSeconds(30)) && worker.Result == RegistryApplyQueue.Progress.Answered && node.Complete() == null,
			"the answer must arrive once the registry is back");

		// Control: the loop as it was, on the calling thread.
		Node old = new Node();
		old.Start();
		old.Pass(Apart, 100);
		old.Registry.Silent = true;
		int waited = 0;
		while (old.Queue.Step(old.Send) == RegistryApplyQueue.Progress.Unavailable)
			if (++waited == 300) old.Registry.Silent = false;
		Check(waited == 300, "control: the request retried on the calling thread must hold it for the whole outage: " + waited);
	}

	/// A claim whose answer was lost: the row is the node's at generation 1, the same request (expecting generation 0)
	/// would be refused, so the repeat carries no expectation and is answered with the row.
	private static void LostClaimAnswer()
	{
		Node node = new Node();
		node.Start();
		node.Registry.LoseAnswer = true;
		node.Pass(new[] { At(1, 0), At(2, 100), At(3, 200000) }, 100);
		ulong stray = node.Membership.Created.Single();
		Check(node.Queue.Expected().SequenceEqual(new[] { (stray, 1u) }), "the heartbeat must report the row the claim makes, to keep its lease");
		Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Unavailable && node.Registry.Owned.ContainsKey(stray), "the registry claimed it, the answer was lost");
		node.Registry.Silent = false;
		Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered && node.Complete() == null && node.Owned.Contains(stray),
			"the repeated claim must be answered with the row, not refused");
		Check(node.Queue.Expected().Length == 0, "nothing is expected once the answer is taken");
		// Control: the first request as it is, repeated, is refused - which the node reads as the registry closing it.
		RegistryApplyQueue.Operation again = new RegistryApplyQueue.Operation { Claim = stray, Attempts = 1 };
		Check(node.Send(again) == RegistryApplyQueue.Outcome.Closed, "control: the claim repeated with its generation expectation must be refused");
	}

	/// The registry is closing the node: the refused change and every change queued behind it (none of them sent)
	/// are reverted last first, and the worker sends nothing while the refusal waits for the game thread.
	private static void ClosedRevert()
	{
		Node node = new Node();
		node.Start();
		string before = Shape(node.Membership);
		node.Registry.Closed = true;
		PartitionMembership.Member[] three = { At(1, 0), At(2, 50000), At(3, 200000) };
		node.Pass(three, 100);   // split, and a claim for the stray
		Check(node.Queue.Count == 2, "a claim and a split must be queued: " + node.Queue.Count);
		Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered, "the refusal is an answer");
		int requests = node.Registry.Requests;
		Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Idle && node.Registry.Requests == requests, "nothing is sent behind a refusal");
		Check(node.Complete() == RegistryApplyQueue.Outcome.Closed, "the game thread must see the Closed refusal");
		Check(node.Queue.Count == 0 && node.Membership.Layouts.Any(layout => layout.Members.SequenceEqual(new long[] { 1, 2 })),
			"the unsent split must be reverted into its input: " + Shape(node.Membership) + " (before: " + before + ")");
		Check(!node.Membership.Layouts.Any(layout => node.Queue.IsPending(layout.Id)), "nothing may stay locked after the revert");
	}

	/// T-0354b (P3-HND-02, 2026-10-03): a pair of grids spawned a frame apart. The first one's new layout waits for the
	/// claim of its id; the second grid, within D of it, must be ADOPTED into it - in the next pass and in the same pass -
	/// and the claim's answer then leaves ONE owned layout. A layout an unanswered topology apply names adopts nothing
	/// (lost-answer covers the split; here a merge). Control: adoption under the full lock (candC) gives the second
	/// grid a layout of its own, and the two cannot merge before the 10 s maturity delay.
	private static void ClaimPendingAdoption()
	{
		foreach (bool control in new[] { false, true })
		{
			Node node = new Node(claimLocks: control);
			ulong home = node.Start();
			// Next pass: the first slab appears far from home; its layout's claim is queued, not answered.
			node.Pass(new[] { At(1, 0), At(2, 100), At(3, 100000) }, 100);
			ulong pad = node.Membership.Created.Single();
			Check(node.Queue.IsPending(pad) && node.Queue.IsClaimOnly(pad) && !node.Owned.Contains(pad), "the new layout must wait for its claim only");
			// One pass later the second slab appears beside it. The claim is still unanswered.
			PartitionMembership.Member[] pair = { At(1, 0), At(2, 100), At(3, 100000), At(4, 100050) };
			node.Pass(pair, 100.02);
			// The same in ONE pass: two grids pasted together beside an existing layout - the first makes a layout, the second joins it.
			PartitionMembership.Member[] paste = pair.Concat(new[] { At(5, 300000), At(6, 300050) }).ToArray();
			node.Pass(paste, 100.04);
			if (control)
			{
				Check(node.Membership.Layouts.Count == 5, "control: under the full lock each slab must get a layout of its own: " + Shape(node.Membership));
				while (node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered) Check(node.Complete() == null, "control: a claim refused");
				Check(node.Pass(paste, 105).Length == 0 && node.Membership.Layouts.Count == 5,
					"control: within the maturity delay the slabs' layouts must stay apart: " + Shape(node.Membership));
				continue;
			}
			string shape = Shape(node.Membership);
			Check(node.Membership.Layouts.Count == 3 && node.Membership.Layouts.Any(x => x.Id == pad && x.Members.SequenceEqual(new long[] { 3, 4 }))
				&& node.Membership.Layouts.Any(x => x.Members.SequenceEqual(new long[] { 5, 6 })),
				"each pair must be ONE layout while its claim is pending: " + shape);
			ulong pasted = node.Membership.Layouts.Single(x => x.Members.Contains(5)).Id;
			Check(node.Queue.Count == 2 && node.Queue.IsClaimOnly(pasted), "two claims queued, nothing else: " + node.Queue.Count);
			while (node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered) Check(node.Complete() == null, "a claim was refused");
			Check(node.Owned.Contains(pad) && node.Owned.Contains(pasted) && node.Queue.Count == 0 && !node.Queue.IsPending(pad),
				"both layouts owned and free after the claims");
			Check(node.Pass(paste, 101).Length == 0 && Shape(node.Membership) == shape, "a pass after the claims changed the pairs: " + Shape(node.Membership));
			// A merge over home and pad is queued and unanswered (the registry is silent): neither adopts a stray beside it.
			node.Registry.Silent = true;
			PartitionMembership.Member[] near = { At(1, 0), At(2, 100), At(3, 2500), At(4, 2550), At(5, 300000), At(6, 300050) };
			PartitionMembership.Change[] merge = node.Pass(near, 200);
			Check(merge.Length == 1 && !merge[0].Split && merge[0].Inputs.Contains(home) && merge[0].Inputs.Contains(pad), "home and pad must merge: " + Shape(node.Membership));
			ulong merged = merge[0].Outputs.Single().Id;
			Check(!node.Queue.IsClaimOnly(merged) && node.Queue.IsPending(merged), "a merge output is pending its apply, not a claim");
			node.Membership.DeferNewLayouts = true;   // as the registry hold does: the stray stays unassigned
			string merging = Shape(node.Membership);
			Check(node.Pass(near.Append(At(7, 150)).ToArray(), 201).Length == 0 && Shape(node.Membership) == merging,
				"a layout an unanswered merge names adopted a stray: " + Shape(node.Membership));
		}
	}

	/// T-0354b: the registry refuses a claim (it is closing the node) after the claimed layout adopted a member. The
	/// layout is dissolved - its members are left unassigned, not kept in an unregistered placeholder no drain moves -
	/// and the next pass under the hold places them into the owned layout within the split distance. A claim queued
	/// behind it (never sent) goes the same way. Control: the candC rule keeps the placeholder with both members.
	private static void RefusedClaimDissolves()
	{
		foreach (bool control in new[] { false, true })
		{
			Node node = new Node(keepUnclaimed: control);
			ulong home = node.Start();
			// 3 is beyond D of home's member 2 (gap 3300 m) but within the split distance (3600 m): a layout of its own.
			node.Pass(new[] { At(1, 0), At(2, 100), At(3, 3400) }, 100);
			ulong pad = node.Membership.Created.Single();
			// Next pass 4 appears beside 3 (adopted into pad, claim pending) and 9 far away (a second claim, queued behind).
			PartitionMembership.Member[] all = { At(1, 0), At(2, 100), At(3, 3400), At(4, 3450), At(9, 400000) };
			node.Pass(all, 100.02);
			Check(node.Membership.Layouts.Any(x => x.Id == pad && x.Members.SequenceEqual(new long[] { 3, 4 })) && node.Queue.Count == 2,
				"4 must join pad and 9 get a claim of its own: " + Shape(node.Membership));
			node.Registry.Closed = true;
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered && node.Complete() == RegistryApplyQueue.Outcome.Closed,
				"the claim must be refused as Closed");
			Check(node.Queue.Count == 0 && !node.Membership.Layouts.Any(x => node.Queue.IsPending(x.Id)), "nothing may stay queued or locked after the refusal");
			if (control)
			{
				Check(node.Membership.Layouts.Any(x => x.Id == pad && x.Members.SequenceEqual(new long[] { 3, 4 })) && !node.Owned.Contains(pad),
					"control: the candC rule keeps both members in the unregistered placeholder: " + Shape(node.Membership));
				continue;
			}
			Check(node.Membership.Layouts.Count == 1 && node.Membership.Layouts[0].Id == home,
				"the refused and the unsent claim's layouts must be dissolved: " + Shape(node.Membership));
			node.Hold();
			node.Pass(all, 101);
			Check(node.Membership.Layouts.Count == 1 && node.Membership.Layouts[0].Members.SequenceEqual(new long[] { 1, 2, 3, 4 }),
				"under the hold the dissolved members must join the owned layout within the split distance, the far one stay unassigned: " + Shape(node.Membership));
		}
	}

	/// <summary>
	/// T-0339 (design C): home = {1, 2, 4} and Q = {3} far away, both claimed; the members returned stand at t = 20 (both
	/// layouts past the 10 s merge delay) with 2 gone from home to within D of 3 and 4 past the split distance: the pass
	/// transfers 2 home -> Q, then splits home -> home {1} and a new id {4}, naming home at the generation the transfer gives it.
	/// </summary>
	private static (Node Node, ulong Home, ulong Q, PartitionMembership.Member[] Moved) TransferSetup(bool plainClassify = false)
	{
		Node node = new Node(transfer: true, plainClassify: plainClassify);
		ulong home = node.Start();
		node.Pass(new[] { At(1, 0), At(2, 100), At(3, 200000), At(4, 200) }, 2);
		ulong q = node.Membership.Created.Single();
		Check(node.Drain() == null && node.Owned.Contains(q) && node.Membership.Layouts.Any(x => x.Id == home && x.Members.SequenceEqual(new long[] { 1, 2, 4 })),
			"setup: home must hold 1, 2, 4 and Q a claimed layout of its own: " + Shape(node.Membership));
		return (node, home, q, new[] { At(1, 0), At(2, 198000), At(3, 200000), At(4, 50000) });
	}

	/// T-0339 (design C) x T-0354: a registry without transfers answers the transfer 400. Where the answer is taken, the
	/// transfer AND the split of the same pass built on it (queued behind it, never sent) are reverted, transfers stay
	/// off for the process, nothing is fenced or held, nothing stays locked; the next pass makes the split and the merge.
	/// Control: the 400 classified like any other refused topology apply is a definitive refusal - the node fences.
	private static void TransferRefusedReverts()
	{
		foreach (bool control in new[] { false, true })
		{
			(Node node, ulong home, ulong q, PartitionMembership.Member[] moved) = TransferSetup(plainClassify: control);
			string before = Shape(node.Membership);
			node.Registry.NoTransfers = true;
			int applies = node.Registry.Applies;
			PartitionMembership.Change[] changes = node.Pass(moved, 20);
			Check(changes.Length == 2 && changes[0].Transfer && changes[0].Inputs.SequenceEqual(new[] { home, q }) && changes[0].Moved.SequenceEqual(new long[] { 2 })
				&& changes[1].Split && !changes[1].Transfer && changes[1].Inputs.Single() == home,
				"the pass must transfer 2 to Q, then split the rest of home: " + Shape(node.Membership));
			ulong piece = changes[1].Outputs.Single(x => x.Id != home).Id;
			Check(node.Queue.IsPending(piece) && node.Queue.IsPending(q) && node.Queue.Count == 2, "the transfer and the split must be queued, their layouts locked");
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Answered, "the 400 is an answer");
			int requests = node.Registry.Requests;
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Idle && node.Registry.Requests == requests, "nothing is sent behind the refused transfer");
			RegistryApplyQueue.Outcome? refusal = node.Complete();
			if (control)
			{
				Check(refusal == RegistryApplyQueue.Outcome.Rejected, "control: the transfer's 400 as a plain refusal must fence the node: " + refusal);
				continue;
			}
			Check(refusal == RegistryApplyQueue.Outcome.TransferRefused, "the transfer's 400 must be a transfer refusal, not a fence: " + refusal);
			Check(node.TransferRefused && !node.Membership.TransferEnabled, "transfers must be off for the process");
			Check(Shape(node.Membership) == before, "the transfer and the split behind it must both be reverted: " + Shape(node.Membership) + " (before: " + before + ")");
			Check(node.Queue.Count == 0 && !node.Membership.Layouts.Any(x => node.Queue.IsPending(x.Id)) && !node.Queue.IsPending(piece),
				"nothing may stay queued or locked after the revert, the split's reserved id included");
			Check(node.Registry.Applies == applies && node.Owned.Contains(home) && node.Owned.Contains(q) && node.Generations[home] == 1 && node.Generations[q] == 1
				&& node.Registry.Owned[home] == 1 && node.Registry.Owned[q] == 1, "the registry changed nothing; ownership and generations must be untouched");
			// Transfers off: the split cuts 2 off with an id of its own, and once that is answered (a new id is pending
			// until then, T-0354) the merge pass merges it into Q - the split and the merge design C replaces.
			PartitionMembership.Change[] next = node.Pass(moved, 21);
			Check(next.Length == 1 && next[0].Split && !next[0].Transfer && next[0].Inputs.Single() == home && next[0].Outputs.Length == 3,
				"the next pass must split 2 off with an id of its own: " + Shape(node.Membership));
			Check(node.Drain() == null, "the split must be applied");
			PartitionMembership.Change[] merge = node.Pass(moved, 22);
			Check(merge.Length == 1 && !merge[0].Split && merge[0].Inputs.Contains(q), "the pass after it must merge 2's layout into Q: " + Shape(node.Membership));
			Check(node.Drain() == null && node.Queue.Count == 0, "the merge must be applied: " + Shape(node.Membership));
			Check(node.Membership.Layouts.Any(x => x.Members.SequenceEqual(new long[] { 2, 3 }))
				&& node.Membership.Layouts.Any(x => x.Id == home && x.Members.SequenceEqual(new long[] { 1 })),
				"2 must end with 3, 1 in home: " + Shape(node.Membership));
		}
	}

	/// T-0339 (design C) x T-0354: a layout an unanswered registry request names takes no transfer. (a) Q waits for the
	/// claim of its id (claim-only: it may adopt, but its id is not this node's in the registry yet); (b) Q is owned and
	/// took a transfer whose answer is still out - a second transfer into it would be built on a change the registry is
	/// not known to have. Either way the piece is split off with an id of its own, and not merged into Q either.
	/// Control: the membership's lock without the pending requests transfers the piece into Q in (b).
	private static void TransferTargetPending()
	{
		// (a) Q's claim is unanswered.
		{
			Node node = new Node(transfer: true);
			ulong home = node.Start();
			node.Registry.Silent = true;
			node.Pass(new[] { At(1, 0), At(2, 100), At(3, 200000) }, 2);
			ulong q = node.Membership.Created.Single();
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Unavailable && node.Queue.IsClaimOnly(q), "Q must wait for its claim");
			PartitionMembership.Change[] changes = node.Pass(new[] { At(1, 0), At(2, 198000), At(3, 200000) }, 20);
			Check(changes.Length == 1 && changes[0].Split && !changes[0].Transfer && changes[0].Inputs.Single() == home
				&& node.Membership.Layouts.Single(x => x.Id == q).Members.SequenceEqual(new long[] { 3 }),
				"a layout waiting for its claim must take no transfer (and no merge): " + Shape(node.Membership));
		}
		// (b) Q is owned and named by an unanswered transfer.
		foreach (bool control in new[] { false, true })
		{
			Node node = new Node(transfer: true, lockIgnoresPending: control);
			ulong home = node.Start();
			node.Pass(new[] { At(1, 0), At(2, 100), At(3, 200000), At(5, 100000), At(6, 100100) }, 2);
			Check(node.Drain() == null && node.Membership.Layouts.Count == 3, "setup: three claimed layouts: " + Shape(node.Membership));
			ulong q = node.Membership.Layouts.Single(x => x.Members.Contains(3)).Id;
			ulong other = node.Membership.Layouts.Single(x => x.Members.Contains(5)).Id;
			node.Registry.Silent = true;
			PartitionMembership.Change[] first = node.Pass(new[] { At(1, 0), At(2, 198000), At(3, 200000), At(5, 100000), At(6, 100100) }, 20);
			Check(first.Length == 1 && first[0].Transfer && first[0].Inputs.SequenceEqual(new[] { home, q }), "2 must transfer to Q: " + Shape(node.Membership));
			Check(node.Queue.Step(node.Send) == RegistryApplyQueue.Progress.Unavailable && node.Queue.IsPending(q) && !node.Queue.IsClaimOnly(q),
				"the transfer must stay unanswered, Q locked by it");
			// 6 leaves 5 past the split distance, within D of 3 (1500 m) and beyond D of 2 (3500 m).
			PartitionMembership.Change[] second = node.Pass(new[] { At(1, 0), At(2, 198000), At(3, 200000), At(5, 100000), At(6, 201500) }, 21);
			if (control)
			{
				Check(second.Any(x => x.Transfer && x.Inputs.SequenceEqual(new[] { other, q })),
					"control: without the pending lock 6 must transfer into Q under its unanswered transfer: " + Shape(node.Membership));
				continue;
			}
			Check(second.Length == 1 && second[0].Split && !second[0].Transfer && second[0].Inputs.Single() == other
				&& node.Membership.Layouts.Single(x => x.Id == q).Members.SequenceEqual(new long[] { 2, 3 }),
				"a layout an unanswered transfer names must take no second transfer (and no merge): " + Shape(node.Membership));
			node.Registry.Silent = false;
			Check(node.Drain() == null && node.Queue.Count == 0, "both changes must be applied once the registry answers");
			Check(node.Generations[q] == 2 && node.Generations[home] == 2 && node.Registry.Owned[q] == 2,
				"the transfer moves both its ids on by one generation: " + node.Generations[q] + "," + node.Generations[home]);
		}
	}

	/// T-0339 (design C) x T-0354: the split of what a transfer leaves needs an id and the reserve is empty: nothing of
	/// the layout changes in this pass - no transfer without the split it was planned with - and the next pass with ids
	/// at hand makes both.
	private static void TransferShortAllocator()
	{
		(Node node, ulong home, ulong q, PartitionMembership.Member[] moved) = TransferSetup();
		string before = Shape(node.Membership);
		node.Queue.Take(node.Queue.Reserve, _ => false);
		Check(node.Pass(moved, 20).Length == 0 && Shape(node.Membership) == before && node.Queue.Count == 0,
			"without an id for the split the pass must make no transfer either: " + Shape(node.Membership));
		node.Refill();
		PartitionMembership.Change[] changes = node.Pass(moved, 21);
		Check(changes.Length == 2 && changes[0].Transfer && changes[1].Split && changes[1].Inputs.Single() == home,
			"with ids at hand the pass transfers and splits: " + Shape(node.Membership));
		Check(node.Drain() == null && node.Generations[home] == 3 && node.Generations[q] == 2, "both applied, home through two generations: " + node.Generations[home]);
	}

	private static void ClassifyRules()
	{
		HttpRequestException Http(HttpStatusCode code, string message) => new HttpRequestException(message, null, code);
		Check(RegistryApplyQueue.Classify(true, Http(HttpStatusCode.Conflict, "anything")) == RegistryApplyQueue.Outcome.Closed, "a 409 on a claim is the registry phasing the node out");
		Check(RegistryApplyQueue.Classify(false, Http(HttpStatusCode.Conflict, "Node 'n1' epoch 3 is Closed")) == RegistryApplyQueue.Outcome.Closed, "a Closed refusal of a topology apply");
		Check(RegistryApplyQueue.Classify(false, Http(HttpStatusCode.Conflict, "Partition 7 ownership is fenced")) == RegistryApplyQueue.Outcome.Rejected, "any other 409 on a topology apply is definitive");
		Check(RegistryApplyQueue.Classify(true, Http(HttpStatusCode.NotFound, "x")) == RegistryApplyQueue.Outcome.Rejected, "a 4xx is definitive");
		Check(new Exception[] { Http(HttpStatusCode.InternalServerError, "x"), new HttpRequestException("refused"), new TaskCanceledException(), new System.IO.IOException("reset") }
			.All(x => RegistryApplyQueue.Classify(false, x) == RegistryApplyQueue.Outcome.Unavailable && RegistryApplyQueue.Classify(true, x) == RegistryApplyQueue.Outcome.Unavailable),
			"no answer, a timeout and a 5xx are retried");
		// T-0339 (design C): a 400 is a transfer refusal only for a transfer; on any other topology apply it stays definitive.
		Check(RegistryApplyQueue.Classify(false, Http(HttpStatusCode.BadRequest, "Split requires one input"), true) == RegistryApplyQueue.Outcome.TransferRefused
			&& RegistryApplyQueue.Classify(false, Http(HttpStatusCode.BadRequest, "x")) == RegistryApplyQueue.Outcome.Rejected
			&& RegistryApplyQueue.Classify(false, Http(HttpStatusCode.Conflict, "Partition 7 ownership is fenced"), true) == RegistryApplyQueue.Outcome.Rejected,
			"a transfer's 400 is a transfer refusal; any other refusal keeps its rule");
	}
}
