using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;

namespace ClusterPlugin;

/// <summary>
/// WA-4: cross-node IGC delivery.
/// <para>
/// Vanilla resolves an addressee with <c>MyIGCSystemSessionComponent.GetContextForPB</c>, which only sees blocks
/// loaded on the calling node, and drops a broadcast that has no local listener. Both are correct on one server
/// and wrong in a cluster: a programmable block one partition away is indistinguishable from one that does not
/// exist. <see cref="IgcEndpointRegistryService"/> already answers "where does this address live"; this service
/// carries the message there.
/// </para>
/// <para>
/// Reachability is decided on the sending node, where the source block, its grid and the proxy antennas of remote
/// grids all exist. The receiving node therefore delivers what it is given without re-evaluating the antenna
/// graph - it could not, since the sender's block is not loaded there.
/// </para>
/// </summary>
public static class IgcDeliveryService
{
	private const string Subsystem = "igc-listeners";
	private static readonly object Sync = new object();
	private static readonly HttpClient Transport = ClusterHttp.Client(TimeSpan.FromSeconds(10));
	private static readonly Dictionary<Guid, ClusterIgcDeliveryResult> Delivered = new();
	private static readonly Queue<Guid> DeliveredOrder = new();
	private static readonly Dictionary<string, long> ReceivedSequence = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, SortedDictionary<long, ClusterIgcDelivery>> Buffered = new(StringComparer.Ordinal);
	private static readonly Dictionary<long, List<ClusterIgcDelivery>> HandoverHeld = new();
	private static readonly List<ScopedRetry> ScopedRetries = new();
	private const int ScopedRetryWindowMilliseconds = 30000;
	private static readonly Dictionary<string, ClusterIgcListeners> Reports = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, long> Sequences = new(StringComparer.Ordinal);
	private static readonly List<object> Journal = new();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	private static ClusterIgcListeners[] m_listeners = Array.Empty<ClusterIgcListeners>();
	private static string m_snapshot = IgcDeliveryCodec.WriteListeners(Array.Empty<ClusterIgcListeners>());
	private static double m_nextReport;
	private static long m_sequence;

	private const int MaxDeliveredKeys = 4096;
	private const int MaxBufferedPerSender = 64;
	private const int MaxJournal = 128;
	private const int MaxHeldPerRoot = 256;

	// Waiting for the delivery on the calling thread would stop a script's run until the receiving node's game thread
	// answers. Two nodes sending to each other at once would each wait for the other until the HTTP timeout, game threads
	// included. So deliveries go out in the background, one request at a time per node: the receiver takes the first
	// number it sees from a stream as the stream's base, so they have to arrive in order. A request carries all that
	// waits, as the receiver takes one request per frame.
	private const int MaxOutboxPerTarget = 1024;
	private const int MaxRequestPayload = 1024 * 1024;
	private static readonly Dictionary<string, Queue<ClusterIgcDelivery>> Outbox = new(StringComparer.Ordinal);
	private static readonly HashSet<string> Sending = new(StringComparer.Ordinal);

	internal static void Install()
	{
		ClusterRuntime.AuthorizeRegistryClient(Transport);
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ApplySnapshot);
		MyIntergridCommunicationContextPatch.Install();
		MyIGCSystemSessionComponentSeams.ClusterFrozenRootOf = FrozenRootOf;
	}

	/// <summary>
	/// Sends a unicast whose addressee is not loaded here. Returns false for an address the cluster has never seen
	/// - which is exactly vanilla's answer for a non-existent addressee - and for a payload shape that cannot
	/// cross a node boundary.
	/// </summary>
	public static bool SendRemoteUnicast(MyProgrammableBlock source, long addressee, string tag, object data)
	{
		if (!ClusterRuntime.IsClustered || source == null || string.IsNullOrEmpty(tag)) return false;
		if (!IgcEndpointRegistryService.TryGetEndpoint(addressee, out ClusterIgcEndpoint endpoint)) return false;
		if (endpoint.Origin == Node) return false;
		if (!IsReachable(source, endpoint)) return false;
		if (!IgcDeliveryCodec.TryEncode(data, out string type, out string payload)) return false;
		return Send(endpoint.Origin, NewDelivery(source, addressee, tag, type, payload, distance: 0, endpoint.Origin));
	}

	/// <summary>
	/// Fans a broadcast out to the other nodes that currently have a listener for the tag. Vanilla drops a
	/// broadcast nobody listens for, so the listener registry keeps a script's heartbeat from becoming
	/// cluster-wide traffic.
	/// </summary>
	public static void SendRemoteBroadcast(MyProgrammableBlock source, string tag, object data, int distance)
	{
		if (!ClusterRuntime.IsClustered || source == null || string.IsNullOrEmpty(tag)) return;
		string[] targets;
		lock (Sync)
			targets = m_listeners.Where(node => node.Origin != Node && node.Tags.Contains(tag, StringComparer.Ordinal))
				.Select(node => node.Origin).ToArray();
		if (targets.Length == 0) return;
		if (!IgcDeliveryCodec.TryEncode(data, out string type, out string payload)) return;
		foreach (string target in targets)
		{
			// A separate operation id per target: each node records its own exactly-once delivery.
			Send(target, NewDelivery(source, 0, tag, type, payload, distance, target));
		}
	}

	/// <summary>
	/// Accepts a delivery on the addressee's node. Exactly-once by operation id, and in per-sender sequence order:
	/// a handover can retry a send or reorder two in flight, and a script that reads a stream of positions must
	/// not see them jump backwards. Out-of-order arrivals are held until the gap closes or the buffer is full.
	/// </summary>
	public static ClusterIgcDeliveryResult Deliver(ClusterIgcDelivery delivery)
	{
		IgcDeliveryCodec.Validate(delivery);
		lock (Sync)
			if (Delivered.TryGetValue(delivery.OperationId, out ClusterIgcDeliveryResult replay)) return replay;
		// The target node is part of the stream identity: sender-side sequences are numbered per target node, so
		// after a handover the forwarded old stream and the fresh stream against the new owner must not share
		// reorder state - the fresh stream restarts at 1 and would otherwise read as duplicate sequences.
		string sender = delivery.Origin + "/" + delivery.SourceAddress + "/" + (delivery.TargetNode ?? string.Empty);
		var ready = new List<ClusterIgcDelivery>();
		lock (Sync)
		{
			ReceivedSequence.TryGetValue(sender, out long last);
			// The first message from a sender establishes the base; there is no gap to wait for.
			if (last == 0) last = delivery.Sequence - 1;
			if (delivery.Sequence <= last)
				return Record(delivery.OperationId, new ClusterIgcDeliveryResult(false, "duplicate sequence"));
			SortedDictionary<long, ClusterIgcDelivery> pending = Buffered.TryGetValue(sender, out var existing)
				? existing : Buffered[sender] = new SortedDictionary<long, ClusterIgcDelivery>();
			pending[delivery.Sequence] = delivery;
			while (pending.Count > 0)
			{
				long next = pending.Keys.First();
				// Release contiguously, or force the oldest through once the buffer is full: past that bound the
				// gap is a lost message, and stalling the sender forever is worse than delivering what arrived.
				if (next != last + 1 && pending.Count <= MaxBufferedPerSender) break;
				ready.Add(pending[next]);
				pending.Remove(next);
				last = next;
			}
			ReceivedSequence[sender] = last;
			if (pending.Count == 0) Buffered.Remove(sender);
		}
		ClusterIgcDeliveryResult result = new ClusterIgcDeliveryResult(false, "buffered out of order");
		foreach (ClusterIgcDelivery item in ready)
		{
			ClusterIgcDeliveryResult applied = ApplyOrHold(item);
			if (item.OperationId == delivery.OperationId) result = applied;
			else Record(item.OperationId, applied);
		}
		return Record(delivery.OperationId, result);
	}

	/// <summary>
	/// Applies a released delivery, unless its addressee sits on an entity frozen for a partition handover. A
	/// message queued on the frozen copy would die silently: the entity was serialized at capture, so anything
	/// enqueued after that lands on a copy that <c>Release</c> is about to close. Holding it here keeps it alive
	/// across the window - <see cref="HandoverReleased"/> forwards it to the partition's new owner, and
	/// <see cref="HandoverThawed"/> applies it locally when the handover aborts and the entity stays.
	/// </summary>
	private static ClusterIgcDeliveryResult ApplyOrHold(ClusterIgcDelivery delivery)
	{
		// A scoped broadcast copy is anchored to its single target listener block the same way a unicast is
		// anchored to its addressee: if that block's grid is frozen (again) here, hold rather than deliver.
		long anchor = delivery.Addressee != 0 ? delivery.Addressee : delivery.ListenerScope;
		long frozenRoot = anchor == 0 ? 0 : FrozenRootOf(anchor);
		if (frozenRoot == 0) return Apply(delivery);
		lock (Sync)
		{
			List<ClusterIgcDelivery> held = HandoverHeld.TryGetValue(frozenRoot, out var existing)
				? existing : HandoverHeld[frozenRoot] = new List<ClusterIgcDelivery>();
			// Reject rather than evict past the bound: dropping the oldest would punch a hole in an ordered
			// stream that was already acknowledged as buffered, while the sender sees this refusal immediately.
			if (held.Count >= MaxHeldPerRoot) return new ClusterIgcDeliveryResult(false, "handover buffer full");
			held.Add(delivery);
		}
		return new ClusterIgcDeliveryResult(true, "buffered for handover");
	}

	private static long FrozenRootOf(long addressee)
	{
		if (!Sandbox.Game.Entities.MyEntities.TryGetEntityById(addressee, out VRage.Game.Entity.MyEntity entity))
			return 0;
		VRage.Game.Entity.MyEntity root = entity.GetTopMostParent() ?? entity;
		return ClusterRuntime.IsFrozen(root.EntityId) ? root.EntityId : 0;
	}

	/// <summary>
	/// The handover committed and the frozen entities were closed: forward everything held for them to the
	/// partition's new owner. Runs the wait off the game thread - the endpoint registry needs a report cycle or
	/// two before the new node's copy surfaces. The forwarded deliveries keep their operation ids and stream
	/// identity, so the receiver's exactly-once and ordering guarantees carry over unchanged.
	/// </summary>
	public static void HandoverReleased(IEnumerable<long> members)
	{
		List<ClusterIgcDelivery> forward = TakeHeld(members);
		if (forward.Count == 0) return;
		System.Threading.Tasks.Task.Run(() =>
		{
			foreach (ClusterIgcDelivery delivery in forward)
			{
				long endpointId = delivery.Addressee != 0 ? delivery.Addressee : delivery.ListenerScope;
				string origin = null;
				for (int attempt = 0; attempt < 40 && origin == null; attempt++)
				{
					if (IgcEndpointRegistryService.TryGetEndpoint(endpointId, out ClusterIgcEndpoint endpoint)
						&& endpoint.Origin != Node) origin = endpoint.Origin;
					else System.Threading.Thread.Sleep(500);
				}
				if (origin == null)
					RecordJournal(delivery, "handover", false, "endpoint did not resurface after handover");
				else Post(origin, new[] { delivery });
			}
		});
	}

	/// <summary>The handover aborted and the entities thawed in place: deliver what was held. Game thread.</summary>
	public static void HandoverThawed(IEnumerable<long> members)
	{
		foreach (ClusterIgcDelivery delivery in TakeHeld(members)) Apply(delivery);
	}

	/// <summary>
	/// Re-attempts forwarded broadcast copies whose moved listener had not re-registered yet. Game thread. A
	/// copy that delivers, or expires, leaves the queue; expiry is journaled as the loss it is.
	/// </summary>
	private static void PumpScopedRetries()
	{
		List<ScopedRetry> due;
		lock (Sync)
		{
			if (ScopedRetries.Count == 0) return;
			due = new List<ScopedRetry>(ScopedRetries);
		}
		foreach (ScopedRetry entry in due)
		{
			ClusterIgcDeliveryResult result;
			try
			{
				MyIGCSystemSessionComponent system = MyIGCSystemSessionComponent.Static;
				if (system == null) continue;
				object data = IgcDeliveryCodec.Decode(entry.Delivery.PayloadType, entry.Delivery.Payload);
				bool delivered = system.ClusterDeliverMessage(0, entry.Delivery.Tag, data,
					entry.Delivery.SourceAddress, entry.Delivery.Distance, entry.Delivery.ListenerScope, null);
				result = new ClusterIgcDeliveryResult(delivered, delivered ? null : "no active listener");
			}
			catch (Exception exception)
			{
				result = new ClusterIgcDeliveryResult(false, SecretRedactor.Redact(exception.Message));
			}
			bool expired = ClusterRuntime.LocalMilliseconds > entry.Deadline;
			if (result.Delivered || expired)
			{
				lock (Sync) ScopedRetries.Remove(entry);
				RecordJournal(entry.Delivery, "handover-retry", result.Delivered,
					result.Delivered ? null : result.Error ?? "listener did not re-register before the deadline");
			}
		}
	}

	private static List<ClusterIgcDelivery> TakeHeld(IEnumerable<long> members)
	{
		var taken = new List<ClusterIgcDelivery>();
		lock (Sync)
			foreach (long member in members)
				if (HandoverHeld.Remove(member, out List<ClusterIgcDelivery> held))
					taken.AddRange(held);
		return taken;
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true
			|| ClusterRuntime.LocalMilliseconds < m_nextReport) return;
		m_nextReport = ClusterRuntime.LocalMilliseconds + 1000;
		PumpScopedRetries();
		if (ClusterRuntime.IsWorldAuthority)
		{
			// The World Authority owns no partitions and runs no scripts, so it listens for nothing.
			lock (Sync) Reports[Node] = new ClusterIgcListeners(Node, Array.Empty<string>());
			PublishMerged();
		}
		else GlobalStateChannel.SendRequest(Subsystem, IgcDeliveryCodec.WriteListeners(new[] { CaptureLocal() }));
	}

	public static void AuthorityChanged(long generation)
	{
		lock (Sync)
		{
			Reports.Clear();
			m_listeners = Array.Empty<ClusterIgcListeners>();
			m_snapshot = IgcDeliveryCodec.WriteListeners(m_listeners);
		}
	}

	public static object Status()
	{
		lock (Sync)
			return new
			{
				node = Node,
				sequence = m_sequence,
				delivered = Delivered.Count,
				buffered = Buffered.Sum(entry => entry.Value.Count),
				held = HandoverHeld.Sum(entry => entry.Value.Count),
				listeners = m_listeners,
				journal = Journal.ToArray()
			};
	}

	/// <summary>
	/// Vanilla's own relay evaluation, applied to a remote addressee. The owner relation is checked exactly as
	/// <c>EvaluateConnectionTo</c> does, and reachability is the presence of the destination grid's antenna in the
	/// set of broadcasters the source can reach - which includes the proxy antennas
	/// <see cref="AntennaGraphService"/> installs for remote grids, so a real relay chain is what answers this.
	/// </summary>
	private static bool IsReachable(MyProgrammableBlock source, ClusterIgcEndpoint endpoint)
	{
		if (!source.GetUserRelationToOwner(endpoint.OwnerIdentityId).IsFriendly()) return false;
		return AntennaGraphService.CanReachGrid(source, endpoint.GridEntityId);
	}

	private static ClusterIgcDelivery NewDelivery(MyProgrammableBlock source, long addressee, string tag,
		string type, string payload, int distance, string target)
	{
		return new ClusterIgcDelivery(Guid.NewGuid(), Node, source.EntityId, addressee, tag, type, payload,
			distance, 0, target);
	}

	/// <summary>
	/// Queues a delivery for its node. False, as for no route, only when that node is too far behind. A dropped delivery
	/// takes no sequence number: a gap would hold back the stream's next ones at the receiver.
	/// The number is per stream, as the receiver orders by <c>(origin, source address)</c> and each target node only ever
	/// sees its own stream: a single node-wide counter would leave a permanent gap in every stream as soon as two
	/// programmable blocks sent.
	/// </summary>
	private static bool Send(string target, ClusterIgcDelivery delivery)
	{
		bool queued, start = false;
		lock (Sync)
		{
			if (!Outbox.TryGetValue(target, out Queue<ClusterIgcDelivery> queue))
				queue = Outbox[target] = new Queue<ClusterIgcDelivery>();
			queued = queue.Count < MaxOutboxPerTarget;
			if (queued)
			{
				string stream = delivery.SourceAddress.ToString(CultureInfo.InvariantCulture) + "/" + target;
				Sequences.TryGetValue(stream, out long sequence);
				Sequences[stream] = ++sequence;
				m_sequence++;
				queue.Enqueue(delivery with { Sequence = sequence });
				start = Sending.Add(target);
			}
		}
		if (!queued) RecordJournal(delivery, target, false, "too many deliveries waiting for this node");
		if (start) System.Threading.Tasks.Task.Run(() => SendQueued(target));
		return queued;
	}

	private static void SendQueued(string target)
	{
		while (true)
		{
			var next = new List<ClusterIgcDelivery>();
			lock (Sync)
			{
				Queue<ClusterIgcDelivery> queue = Outbox[target];
				if (queue.Count == 0)
				{
					Sending.Remove(target);
					return;
				}
				int payload = 0;
				while (queue.Count > 0 && (next.Count == 0 || payload + queue.Peek().Payload.Length <= MaxRequestPayload))
				{
					payload += queue.Peek().Payload.Length;
					next.Add(queue.Dequeue());
				}
			}
			Post(target, next);
		}
	}

	private static void Post(string origin, IReadOnlyList<ClusterIgcDelivery> deliveries)
	{
		string failure;
		try
		{
			(_, string control) = SpawnDirectorService.ResolveNodeControl(origin);
			using StringContent content = new StringContent(IgcDeliveryCodec.WriteRequest(deliveries),
				Encoding.UTF8, "application/json");
			using HttpResponseMessage response = Transport
				.PostAsync(control.TrimEnd('/') + "/cluster/igc-deliver", content).GetAwaiter().GetResult();
			string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
			if (response.IsSuccessStatusCode)
			{
				ClusterIgcDeliveryResult[] results = JsonSerializer.Deserialize<ClusterIgcDeliveryResult[]>(body);
				for (int i = 0; i < deliveries.Count; i++)
					RecordJournal(deliveries[i], origin, results[i].Delivered, results[i].Error);
				return;
			}
			failure = body;
		}
		catch (Exception exception)
		{
			failure = exception.Message;
		}
		foreach (ClusterIgcDelivery delivery in deliveries)
			RecordJournal(delivery, origin, false, SecretRedactor.Redact(failure));
	}

	private static ClusterIgcDeliveryResult Apply(ClusterIgcDelivery delivery)
	{
		try
		{
			MyIGCSystemSessionComponent system = MyIGCSystemSessionComponent.Static
				?? throw new InvalidOperationException("IGC system is not loaded");
			// A delivery numbered for another node is one a handover forwarded here. Its addressee had a live
			// IGC context on the old owner - the context is runtime state the transfer destroys, not a saved
			// property - so recreate it rather than refuse a unicast vanilla would have delivered. A fresh
			// delivery keeps vanilla's semantics: an addressee that never opened IGC receives nothing.
			if (delivery.Addressee != 0 && delivery.TargetNode != null && delivery.TargetNode != Node
				&& Sandbox.Game.Entities.MyEntities.TryGetEntityById(delivery.Addressee, out VRage.Game.Entity.MyEntity moved)
				&& moved is MyProgrammableBlock movedBlock)
				system.GetOrMakeContextFor(movedBlock);
			object data = IgcDeliveryCodec.Decode(delivery.PayloadType, delivery.Payload);
			Dictionary<long, long> frozenListeners = delivery.Addressee == 0 && delivery.ListenerScope == 0
				? new Dictionary<long, long>() : null;
			bool delivered = system.ClusterDeliverMessage(delivery.Addressee, delivery.Tag, data,
				delivery.SourceAddress, delivery.Distance, delivery.ListenerScope, frozenListeners);
			if (frozenListeners is { Count: > 0 })
			{
				// Each frozen listener gets its own scoped copy under its own operation id: the original id
				// already records this broadcast's result, and each copy forwards independently at release.
				lock (Sync)
					foreach (KeyValuePair<long, long> frozen in frozenListeners)
					{
						List<ClusterIgcDelivery> held = HandoverHeld.TryGetValue(frozen.Value, out var existing)
							? existing : HandoverHeld[frozen.Value] = new List<ClusterIgcDelivery>();
						if (held.Count >= MaxHeldPerRoot)
						{
							Console.WriteLine("[ClusterPlugin] igc broadcast copy dropped: handover buffer full root="
								+ frozen.Value + " listener=" + frozen.Key + " tag=" + delivery.Tag);
							continue;
						}
						held.Add(delivery with { OperationId = Guid.NewGuid(), ListenerScope = frozen.Key });
						delivered = true;
					}
			}
			// A forwarded scoped copy can land before the moved block's script re-registers its listener -
			// registration is runtime state the transfer destroys. Retry until the deadline instead of
			// recording a loss vanilla would not have had.
			if (!delivered && delivery.ListenerScope != 0 && delivery.TargetNode != null && delivery.TargetNode != Node)
			{
				lock (Sync)
					if (!ScopedRetries.Any(entry => entry.Delivery.OperationId == delivery.OperationId))
						ScopedRetries.Add(new ScopedRetry(delivery, ClusterRuntime.LocalMilliseconds + ScopedRetryWindowMilliseconds));
				return new ClusterIgcDeliveryResult(true, "waiting for listener re-registration");
			}
			return new ClusterIgcDeliveryResult(delivered, delivered ? null : "no active listener");
		}
		catch (Exception exception)
		{
			return new ClusterIgcDeliveryResult(false, SecretRedactor.Redact(exception.Message));
		}
	}

	private static ClusterIgcDeliveryResult Record(Guid operationId, ClusterIgcDeliveryResult result)
	{
		lock (Sync)
		{
			if (Delivered.TryAdd(operationId, result))
			{
				DeliveredOrder.Enqueue(operationId);
				if (DeliveredOrder.Count > MaxDeliveredKeys) Delivered.Remove(DeliveredOrder.Dequeue());
			}
		}
		return result;
	}

	private static void RecordJournal(ClusterIgcDelivery delivery, string target, bool delivered, string error)
	{
		lock (Sync)
		{
			Journal.Add(new
			{
				operation = delivery.OperationId,
				sequence = delivery.Sequence,
				source = delivery.SourceAddress,
				addressee = delivery.Addressee,
				tag = delivery.Tag,
				target,
				delivered,
				error
			});
			if (Journal.Count > MaxJournal) Journal.RemoveAt(0);
		}
		Console.WriteLine("[ClusterPlugin] igc send operation=" + delivery.OperationId
			+ " source=" + delivery.SourceAddress + " addressee=" + delivery.Addressee
			+ " tag=" + delivery.Tag + " target=" + target + " delivered=" + delivered
			+ " error=" + (error ?? "none"));
	}

	private static ClusterIgcListeners CaptureLocal()
	{
		string[] tags = MyIGCSystemSessionComponent.Static?.ClusterBroadcastTags() ?? Array.Empty<string>();
		return new ClusterIgcListeners(Node, tags.Take(4096).ToArray());
	}

	private static void ReceiveReport(string origin, string payload)
	{
		ClusterIgcListenerSnapshot snapshot = IgcDeliveryCodec.ReadListeners(payload);
		lock (Sync)
			foreach (ClusterIgcListeners node in snapshot.Nodes)
				Reports[origin] = node with { Origin = origin };
		PublishMerged();
	}

	private static void PublishMerged()
	{
		string payload;
		lock (Sync)
		{
			m_listeners = Reports.Values.ToArray();
			payload = m_snapshot = IgcDeliveryCodec.WriteListeners(m_listeners);
		}
		GlobalStateChannel.Publish(Subsystem, payload);
	}

	private static void ApplySnapshot(string payload)
	{
		ClusterIgcListenerSnapshot snapshot = IgcDeliveryCodec.ReadListeners(payload);
		lock (Sync)
		{
			m_listeners = snapshot.Nodes;
			m_snapshot = payload;
		}
	}

	private static string Snapshot() { lock (Sync) return m_snapshot; }

	private sealed record ScopedRetry(ClusterIgcDelivery Delivery, double Deadline);
}
