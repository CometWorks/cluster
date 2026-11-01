using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.Game.World;
using VRage.Utils;

namespace ClusterPlugin;

public static class GlobalStateChannel
{
	private static readonly object Sync = new object();
	private static readonly Dictionary<string, SubsystemState> States = new Dictionary<string, SubsystemState>(StringComparer.Ordinal);
	private static readonly Dictionary<string, Action<string, string>> AuthorityHandlers = new Dictionary<string, Action<string, string>>(StringComparer.Ordinal);
	private static readonly Dictionary<string, Func<string, string, string>> CommandHandlers = new Dictionary<string, Func<string, string, string>>(StringComparer.Ordinal);
	private static readonly HashSet<string> Queries = new HashSet<string>(StringComparer.Ordinal);
	private static readonly HashSet<string> Durable = new HashSet<string>(StringComparer.Ordinal);
	/// <summary>Subsystems whose answers wait for their append to the registry's journal (#1318).</summary>
	private static readonly HashSet<string> Journaled = new HashSet<string>(StringComparer.Ordinal);
	/// <summary>#1318: how a replacement World Authority replays a journaled command whose handler would not give the same result.</summary>
	private static readonly Dictionary<string, Func<string, string, string, string>> Replays = new Dictionary<string, Func<string, string, string, string>>(StringComparer.Ordinal);
	/// <summary>World Authority: answers to durable commands until a recoverable global save holds them (#1270).</summary>
	private static readonly List<HeldResponse> HeldResponses = new List<HeldResponse>();
	private static long m_heldTotal;
	private static long m_heldDropped;
	private static readonly Dictionary<string, Action<string>> ReplicaHandlers = new Dictionary<string, Action<string>>(StringComparer.Ordinal);
	private static readonly Dictionary<string, Func<string>> SnapshotProviders = new Dictionary<string, Func<string>>(StringComparer.Ordinal);
	private static readonly Dictionary<string, Action<string>> SnapshotHandlers = new Dictionary<string, Action<string>>(StringComparer.Ordinal);
	private static readonly Queue<Envelope> AuthorityQueue = new Queue<Envelope>();
	private static readonly Queue<QueuedEnvelope> ReplicaQueue = new Queue<QueuedEnvelope>();
	private static readonly HashSet<string> PendingResync = new HashSet<string>(StringComparer.Ordinal);
	private static string m_node = string.Empty;
	private static readonly Dictionary<long, TaskCompletionSource<GlobalCommandResult>> PendingCommands = new Dictionary<long, TaskCompletionSource<GlobalCommandResult>>();
	private static long m_commandSequence;
	private static long m_linkEpoch;
	private static long m_resyncGeneration;
	private static long m_received;
	private static long m_rejected;
	private static long m_completedCommands;
	private static long m_timedOutCommands;
	private static double m_nextResync;

	public static void Start(string node)
	{
		m_node = node ?? throw new ArgumentNullException(nameof(node));
		DirectTransportNodeLinkPatch.GlobalMessageReceived = Receive;
		DirectTransportNodeLinkPatch.GlobalLinkChanged = LinkChanged;
	}

	public static void Publish(string subsystem, string payload, long? generation = null, bool skipSequence = false, bool retry = true)
	{
		if (!ClusterRuntime.IsWorldAuthority) throw new InvalidOperationException("Only the World Authority can publish global state");
		if (string.IsNullOrWhiteSpace(subsystem)) throw new ArgumentException("A subsystem is required", nameof(subsystem));
		long authorityGeneration = generation ?? ClusterRuntime.AuthorityGeneration;
		lock (Sync)
		{
			SubsystemState state = State(subsystem, authorityGeneration);
			long sequence = state.Pending?.Sequence ?? state.Sequence + (skipSequence ? 2 : 1);
			var envelope = new Envelope("Broadcast", authorityGeneration, subsystem, sequence, m_node, null, payload ?? string.Empty);
			if (TrySend(envelope))
			{
				state.Sequence = sequence;
				state.Payload = envelope.Payload;
				state.Pending = null;
				m_disconnectedPublishLogged.Remove(subsystem);
				return;
			}
			if (retry)
			{
				state.Sequence = sequence;
				state.Payload = envelope.Payload;
				state.Pending = envelope;
				// The envelope is queued and RetryPendingPublishes re-sends it when the link is back,
				// so a disconnected link is not an error for the caller - and most callers run on the
				// game thread inside ClusterRuntime.Update with no catch: IgcDeliveryService
				// .PublishMerged threw this from Update at EVERY stop of w29 (6 of 6 on 2026-09-08,
				// after the gateway had gone), the game's unhandled-exception path took the World
				// Authority down with exit 1, and a gateway restart mid-run would have done the same.
				if (m_disconnectedPublishLogged.Add(subsystem))
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] global-state publish queued: node link disconnected subsystem=" + subsystem);
				return;
			}
		}
		throw new InvalidOperationException("Global-state node link is disconnected");
	}

	/// <summary>Subsystems whose publish has been queued on a disconnected link since the last successful send: one log line per outage, not one per tick.</summary>
	private static readonly HashSet<string> m_disconnectedPublishLogged = new HashSet<string>(StringComparer.Ordinal);

	public static bool SendRequest(string subsystem, string payload)
	{
		if (ClusterRuntime.IsWorldAuthority || string.IsNullOrWhiteSpace(subsystem)) return false;
		return TrySend(new Envelope("Request", ClusterRuntime.AuthorityGeneration, subsystem, 0, m_node, null, payload ?? string.Empty));
	}

	public static string SendCommand(string subsystem, string payload, int timeoutMilliseconds = 5000)
		=> SendCommand(subsystem, payload, Guid.NewGuid(), timeoutMilliseconds);

	public static string SendCommand(string subsystem, string payload, Guid operationId, int timeoutMilliseconds = 5000)
	{
		if (ClusterRuntime.IsWorldAuthority) throw new InvalidOperationException("The World Authority cannot send a command to itself");
		if (string.IsNullOrWhiteSpace(subsystem)) throw new ArgumentException("A subsystem is required", nameof(subsystem));
		if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required", nameof(operationId));
		if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
		long generation = ClusterRuntime.AuthorityGeneration;
		if (generation <= 0) throw new InvalidOperationException("The World Authority is unavailable");
		long sequence = Interlocked.Increment(ref m_commandSequence);
		var pending = new TaskCompletionSource<GlobalCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (Sync) PendingCommands.Add(sequence, pending);
		try
		{
			// T-0140: the sending end of the same question.
			Console.WriteLine("[ClusterPlugin] global_command_send subsystem=" + subsystem
				+ " sequence=" + sequence + " operation=" + operationId + " generation=" + generation);
			if (!TrySend(new Envelope("Command", generation, subsystem, sequence, m_node, null, payload ?? string.Empty, operationId)))
				throw new InvalidOperationException("Global-state node link is disconnected");
			if (!pending.Task.Wait(timeoutMilliseconds))
			{
				Interlocked.Increment(ref m_timedOutCommands);
				throw new TimeoutException("World Authority command timed out");
			}
			GlobalCommandResult result = pending.Task.Result;
			if (!result.Success) throw new InvalidOperationException(result.Error);
			Interlocked.Increment(ref m_completedCommands);
			return result.Payload;
		}
		finally
		{
			lock (Sync) PendingCommands.Remove(sequence);
		}
	}

	public static void RegisterAuthorityHandler(string subsystem, Action<string, string> handler)
	{
		lock (Sync) AuthorityHandlers[subsystem] = handler;
	}

	/// <summary>
	/// <paramref name="durable"/>: the World Authority answers only once a global save that holds the command's result is
	/// registered and held by a node besides itself, so a replacement restores it (persist before answer, #1270). For a command whose effect the asking node acts on and a replacement World
	/// Authority must still know - a stored ship whose grids then close, an accepted contract - and not for frequent
	/// ones on a node's game thread, which would wait for a save each time.
	/// <paramref name="journaled"/>: those frequent ones. The World Authority answers once the command is appended to the
	/// registry's journal (<see cref="GlobalCommandJournal"/>, one round trip), and a replacement replays it after the save
	/// it restores (#1318). <paramref name="replay"/> (origin, payload, recorded result payload) replays a command whose
	/// handler would not reproduce its result, like an identity allocation that must keep the identity id it handed out;
	/// without it the replay runs the handler again.
	/// </summary>
	public static void RegisterCommandHandler(string subsystem, Func<string, string, string> handler, bool durable = false,
		bool journaled = false, Func<string, string, string, string> replay = null)
	{
		if (durable && journaled) throw new ArgumentException("A command is either durable or journaled", nameof(journaled));
		lock (Sync)
		{
			CommandHandlers[subsystem] = handler;
			if (durable) Durable.Add(subsystem);
			else Durable.Remove(subsystem);
			if (journaled) Journaled.Add(subsystem);
			else Journaled.Remove(subsystem);
			if (replay != null) Replays[subsystem] = replay;
			else Replays.Remove(subsystem);
		}
	}

	/// <summary>
	/// A command that only reads, answered outside <see cref="GlobalCommandLedger"/>: the ledger keeps every result
	/// in the global save, which suits small results and not large reads like a stored ship's blueprint.
	/// </summary>
	public static void RegisterQueryHandler(string subsystem, Func<string, string, string> handler)
	{
		lock (Sync)
		{
			CommandHandlers[subsystem] = handler;
			Queries.Add(subsystem);
		}
	}

	public static void RegisterReplicaHandler(string subsystem, Action<string> handler)
	{
		lock (Sync) ReplicaHandlers[subsystem] = handler;
	}

	public static void RegisterSnapshot(string subsystem, Func<string> provider, Action<string> handler)
	{
		lock (Sync)
		{
			SnapshotProviders[subsystem] = provider;
			SnapshotHandlers[subsystem] = handler;
			if (!ClusterRuntime.IsWorldAuthority && ClusterRuntime.AuthorityGeneration > 0)
			{
				m_resyncGeneration = ClusterRuntime.AuthorityGeneration;
				PendingResync.Add(subsystem);
			}
		}
	}

	public static void AuthorityChanged(long generation)
	{
		if (ClusterRuntime.IsWorldAuthority || generation < 0) return;
		TaskCompletionSource<GlobalCommandResult>[] pending;
		lock (Sync)
		{
			m_resyncGeneration = generation;
			PendingResync.Clear();
			ReplicaQueue.Clear();
			if (generation > 0)
				foreach (string subsystem in SnapshotHandlers.Keys) PendingResync.Add(subsystem);
			m_nextResync = 0;
			pending = PendingCommands.Values.ToArray();
			PendingCommands.Clear();
		}
		foreach (TaskCompletionSource<GlobalCommandResult> command in pending)
			command.TrySetResult(new GlobalCommandResult(false, string.Empty,
				generation > 0 ? "World Authority generation changed" : "World Authority is unavailable"));
	}

	/// <summary>
	/// Counts this replica's node-link (re)connections. A subsystem that reports deltas to the
	/// authority keys its base on it: a request sent on a link that then dropped may never have
	/// arrived, so the first report on a new link must be a full one.
	/// </summary>
	public static long LinkEpoch { get { lock (Sync) return m_linkEpoch; } }

	public static void RequestResync(string subsystem)
	{
		if (string.IsNullOrWhiteSpace(subsystem)) throw new ArgumentException("A subsystem is required", nameof(subsystem));
		RequestResync(subsystem, ClusterRuntime.AuthorityGeneration);
	}

	public static void Update()
	{
		if (ClusterRuntime.IsAuthorityFenced) return;
		if (ClusterRuntime.IsWorldAuthority)
		{
			// #1318: the commands journaled since the restored save run first; everything that arrived meanwhile is queued.
			GlobalCommandJournal.ReplayRestored();
			if (GlobalCommandJournal.ReplayPending) return;
			ApplyAuthorityQueue();
			RetryPendingPublishes();
			ReleaseHeldResponses();
			return;
		}
		ApplyReplicaQueue();
		string[] subsystems;
		long generation;
		lock (Sync)
		{
			generation = m_resyncGeneration;
			if (generation <= 0 || generation != ClusterRuntime.AuthorityGeneration || PendingResync.Count == 0
				|| ClusterRuntime.LocalMilliseconds < m_nextResync) return;
			m_nextResync = ClusterRuntime.LocalMilliseconds + 1000;
			subsystems = PendingResync.ToArray();
		}
		foreach (string subsystem in subsystems)
			if (!TrySend(new Envelope("Resync", generation, subsystem, 0, m_node, null, string.Empty))) return;
	}

	public static object Status()
	{
		lock (Sync)
			return new
			{
				node = m_node,
				generation = ClusterRuntime.AuthorityGeneration,
				received = m_received,
				rejected = m_rejected,
				pendingCommands = PendingCommands.Count,
				pendingResync = PendingResync.OrderBy(x => x).ToArray(),
				queuedAuthorityMessages = AuthorityQueue.Count,
				queuedReplicaMessages = ReplicaQueue.Count,
				completedCommands = m_completedCommands,
				timedOutCommands = m_timedOutCommands,
				heldResponses = HeldResponses.Count,
				heldResponsesTotal = m_heldTotal,
				heldResponsesDropped = m_heldDropped,
				durableLedgerOrder = GlobalCommandLedger.DurableOrder,
				journal = GlobalCommandJournal.Status(),
				// T-0154: LiteNetLib packets still queued on the gateway link (channel 0, ordered =
				// publishes, unordered = commands). Growth here is retained heap on this node.
				linkQueue = DirectTransportNodeLinkPatch.GlobalQueueDepths(),
				subsystems = States.OrderBy(x => x.Key).Select(x => new
				{
					subsystem = x.Key, generation = x.Value.Generation, sequence = x.Value.Sequence,
					payload = x.Value.Payload, gaps = x.Value.Gaps, snapshots = x.Value.Snapshots,
					pendingPublish = x.Value.Pending != null
				}).ToArray()
			};
	}

	private static void Receive(byte[] bytes)
	{
		if (TestPartitionActive)
		{
			Interlocked.Increment(ref m_testDroppedFrames);
			return;
		}
		Envelope envelope;
		try
		{
			envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidOperationException("Empty global-state frame");
			if (!ClusterRuntime.AcceptAuthorityMessage(envelope.Generation))
			{
				lock (Sync) m_rejected++;
				return;
			}
			lock (Sync) m_received++;
			if (ClusterRuntime.IsWorldAuthority && (MySession.Static?.Ready != true || GlobalCommandJournal.ReplayPending))
			{
				lock (Sync)
				{
					if (AuthorityQueue.Count >= 4096) throw new InvalidOperationException("Global-state authority queue is full");
					AuthorityQueue.Enqueue(envelope);
				}
			}
			else if (ClusterRuntime.IsWorldAuthority) ReceiveAtAuthority(envelope);
			else if (envelope.Kind == "Response")
			{
				// T-0140: did the answer come back at all, and how late?
				Console.WriteLine("[ClusterPlugin] global_command_reply subsystem=" + envelope.Subsystem
					+ " sequence=" + envelope.Sequence + " operation=" + envelope.OperationId);
				ReceiveAtReplica(envelope);
			}
			else
			{
				if (envelope.Target != null && envelope.Target != m_node) return;
				lock (Sync)
				{
					if (ReplicaQueue.Count >= 4096)
					{
						RequestResync(envelope.Subsystem, envelope.Generation);
						throw new InvalidOperationException("Global-state replica queue is full");
					}
					ReplicaQueue.Enqueue(new QueuedEnvelope(envelope, m_linkEpoch));
				}
			}
		}
		catch (Exception exception)
		{
			lock (Sync) m_rejected++;
			Console.WriteLine("[ClusterPlugin] global_state rejected=true error=" + SecretRedactor.Redact(exception.Message));
		}
	}

	private static void ReceiveAtAuthority(Envelope envelope)
	{
		if (envelope.Kind == "Command")
		{
			// T-0140 instrumentation. Every node->WA command times out while the WA is idle, its
			// queues empty and no thread in any handler, so the open question is simply whether the
			// frame ever reaches this method. One line per command is nothing: the whole cluster
			// issues a handful a minute, against the ~30 frames/s of publishes that are NOT logged.
			Console.WriteLine("[ClusterPlugin] global_command_in subsystem=" + envelope.Subsystem
				+ " origin=" + envelope.Origin + " sequence=" + envelope.Sequence
				+ " operation=" + envelope.OperationId);
			Func<string, string, string> handler;
			bool query, durable, journaled;
			lock (Sync)
			{
				CommandHandlers.TryGetValue(envelope.Subsystem, out handler);
				query = Queries.Contains(envelope.Subsystem);
				durable = Durable.Contains(envelope.Subsystem);
				journaled = Journaled.Contains(envelope.Subsystem);
			}
			GlobalCommandResult Run()
			{
				try
				{
					if (handler == null) throw new InvalidOperationException("No World Authority command handler for " + envelope.Subsystem);
					return new GlobalCommandResult(true, handler(envelope.Origin, envelope.Payload) ?? string.Empty, string.Empty);
				}
				catch (Exception exception)
				{
					return new GlobalCommandResult(false, string.Empty, SecretRedactor.Redact(exception.Message));
				}
			}
			long order = 0;
			GlobalCommandResult result = query ? Run() : GlobalCommandLedger.Execute(envelope.Subsystem, envelope.OperationId, envelope.Payload, Run, out order);
			var response = new Envelope("Response", envelope.Generation, envelope.Subsystem, envelope.Sequence, m_node, envelope.Origin,
				GlobalCommandCodec.Write(result), envelope.OperationId);
			// #1270 persist before answer: the asking node acts on the answer at once (closes the stored grids, pastes
			// the taken ship, pays out), and a replacement World Authority restores the last registered global save.
			// An answer sent before that save is recoverable would let the two disagree for good. A replay (the same operation id
			// again) waits the same way until its result is durable.
			if (durable && order > 0 && ClusterRuntime.GlobalSavesPersist && !GlobalCommandLedger.IsDurable(order))
			{
				lock (Sync)
				{
					HeldResponses.Add(new HeldResponse(order, response));
					m_heldTotal++;
				}
				ClusterRuntime.RequestGlobalSave();
				Console.WriteLine("[ClusterPlugin] global_command_held subsystem=" + envelope.Subsystem
					+ " target=" + envelope.Origin + " sequence=" + envelope.Sequence + " order=" + order
					+ " success=" + result.Success);
				return;
			}
			// #1318 write-ahead: the same rule for the frequent commands, at the cost of one registry round trip. A
			// handler that threw changed nothing, so its answer is not journaled.
			if (journaled && order > 0 && result.Success && GlobalCommandJournal.Active && !GlobalCommandLedger.IsDurable(order)
				&& !GlobalCommandJournal.Covers(order))
			{
				GlobalCommandJournal.Entry entry = GlobalCommandJournal.Append(order, envelope.Subsystem, envelope.OperationId,
					envelope.Origin, envelope.Payload, GlobalCommandCodec.Write(result));
				lock (Sync) HeldResponses.Add(new HeldResponse(order, response, entry, ClusterRuntime.LocalMilliseconds));
				// The append may have landed before the hold was registered.
				ReleaseHeldResponses();
				return;
			}
			// The other half of the T-0140 question: if the command arrives and this line is reached
			// promptly, the loss is on the way BACK and the gateway relay is the next suspect.
			Console.WriteLine("[ClusterPlugin] global_command_out subsystem=" + envelope.Subsystem
				+ " target=" + envelope.Origin + " sequence=" + envelope.Sequence
				+ " success=" + result.Success);
			Send(response);
			return;
		}
		if (envelope.Kind == "Request")
		{
			Action<string, string> handler;
			lock (Sync) AuthorityHandlers.TryGetValue(envelope.Subsystem, out handler);
			if (handler == null) throw new InvalidOperationException("No World Authority handler for " + envelope.Subsystem);
			handler(envelope.Origin, envelope.Payload);
			return;
		}
		if (envelope.Kind != "Resync") throw new InvalidOperationException("World Authority received an invalid global-state direction");
		long sequence;
		string fallback;
		Func<string> provider;
		lock (Sync)
		{
			SubsystemState state = State(envelope.Subsystem, envelope.Generation);
			sequence = state.Sequence;
			fallback = state.Payload;
			SnapshotProviders.TryGetValue(envelope.Subsystem, out provider);
		}
		string payload = provider != null ? provider() : fallback;
		if (payload != null)
			Send(new Envelope("Snapshot", envelope.Generation, envelope.Subsystem, sequence, m_node, envelope.Origin, payload));
	}

	private static void ReceiveAtReplica(Envelope envelope)
	{
		if (envelope.Target != null && envelope.Target != m_node) return;
		if (envelope.Kind != "Response") throw new InvalidOperationException("Replica received an invalid immediate global-state message");
		TaskCompletionSource<GlobalCommandResult> pending;
		lock (Sync) PendingCommands.TryGetValue(envelope.Sequence, out pending);
		pending?.TrySetResult(GlobalCommandCodec.Read(envelope.Payload));
	}

	private static void ApplyReplica(QueuedEnvelope queued)
	{
		Envelope envelope = queued.Envelope;
		if (!ClusterRuntime.AcceptAuthorityMessage(envelope.Generation)) return;
		bool snapshot = envelope.Kind == "Snapshot";
		Action<string> applied = null;
		bool hasHandler;
		lock (Sync)
		{
			SubsystemState state = State(envelope.Subsystem, envelope.Generation);
			if (snapshot)
			{
				if (envelope.Sequence < state.Sequence) return;
				hasHandler = SnapshotHandlers.TryGetValue(envelope.Subsystem, out applied);
			}
			else
			{
				if (envelope.Kind != "Broadcast") throw new InvalidOperationException("Replica received an invalid global-state direction");
				if (envelope.Sequence <= state.Sequence) return;
				if (envelope.Sequence == state.Sequence + 1)
					hasHandler = ReplicaHandlers.TryGetValue(envelope.Subsystem, out applied);
				else
				{
					state.Gaps++;
					RequestResync(envelope.Subsystem, envelope.Generation);
					return;
				}
			}
		}
		if (hasHandler) applied(envelope.Payload);
		lock (Sync)
		{
			if (!ClusterRuntime.AcceptAuthorityMessage(envelope.Generation))
			{
				RequestResync(envelope.Subsystem, ClusterRuntime.AuthorityGeneration);
				return;
			}
			SubsystemState state = State(envelope.Subsystem, envelope.Generation);
			if (envelope.Sequence >= state.Sequence)
			{
				state.Sequence = envelope.Sequence;
				state.Payload = envelope.Payload;
				if (snapshot)
				{
					state.Snapshots++;
					if (queued.LinkEpoch == m_linkEpoch) PendingResync.Remove(envelope.Subsystem);
					if (PendingResync.Count == 0) m_resyncGeneration = 0;
				}
			}
		}
	}

	private static void ApplyReplicaQueue()
	{
		while (true)
		{
			QueuedEnvelope queued;
			lock (Sync)
			{
				if (ReplicaQueue.Count == 0) return;
				queued = ReplicaQueue.Dequeue();
			}
			try { ApplyReplica(queued); }
			catch (Exception exception)
			{
				lock (Sync) m_rejected++;
				RequestResync(queued.Envelope.Subsystem, queued.Envelope.Generation);
				Console.WriteLine("[ClusterPlugin] global_state apply_failed=true subsystem=" + queued.Envelope.Subsystem
					+ " error=" + SecretRedactor.Redact(exception.Message));
			}
		}
	}

	private static void ApplyAuthorityQueue()
	{
		while (true)
		{
			Envelope envelope;
			lock (Sync)
			{
				if (AuthorityQueue.Count == 0) return;
				envelope = AuthorityQueue.Dequeue();
			}
			try
			{
				if (ClusterRuntime.AcceptAuthorityMessage(envelope.Generation)) ReceiveAtAuthority(envelope);
			}
			catch (Exception exception)
			{
				lock (Sync) m_rejected++;
				Console.WriteLine("[ClusterPlugin] global_state authority_apply_failed=true subsystem=" + envelope.Subsystem
					+ " error=" + SecretRedactor.Redact(exception.Message));
			}
		}
	}

	/// <summary>
	/// SE-865: done once a recoverable global save holds the ledger up to <paramref name="order"/>, on the same terms as a
	/// held answer: a fenced World Authority never finishes it, and neither does one that dies first. At once when this
	/// World Authority writes no saves, so there is nothing to wait for.
	/// </summary>
	internal static Task UntilDurable(long order)
	{
		if (!ClusterRuntime.GlobalSavesPersist || GlobalCommandLedger.IsDurable(order)) return Task.CompletedTask;
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (Sync)
		{
			HeldResponses.Add(new HeldResponse(order, null, Done: done));
			m_heldTotal++;
		}
		ClusterRuntime.RequestGlobalSave();
		return done.Task;
	}

	/// <summary>An answer covered by a ledger order up to <paramref name="order"/> is held, waiting for a save to be recoverable.</summary>
	internal static bool HoldsResponsesThrough(long order)
	{
		lock (Sync) return HeldResponses.Any(held => held.Journal == null && held.Order <= order);
	}

	/// <summary>
	/// World Authority game thread: sends the held answers a recoverable global save now covers. While any is still
	/// held the next save is asked for again, so a request that raced a capture cannot leave one waiting for the
	/// periodic save. A fenced World Authority never sends one, and its saves are refused, so it never answers what its
	/// replacement does not know.
	/// Also the journal writer's thread, right after an append (#1318), so a journaled answer does not wait for the next
	/// WA frame. A journaled answer whose append has not been acknowledged after <see cref="GlobalCommandJournal.FailOpen"/>
	/// is sent anyway; one whose append was refused (this WA is fenced) never is.
	/// </summary>
	internal static void ReleaseHeldResponses()
	{
		if (ClusterRuntime.IsAuthorityFenced) return;
		List<HeldResponse> ready = null;
		bool waiting;
		lock (Sync)
		{
			if (HeldResponses.Count == 0) return;
			long durable = GlobalCommandLedger.DurableOrder;
			double now = ClusterRuntime.LocalMilliseconds;
			bool Ready(HeldResponse held) => held.Order <= durable || held.Journal != null
				&& (held.Journal.State is GlobalCommandJournal.EntryState.Appended or GlobalCommandJournal.EntryState.Unjournaled
					|| held.Journal.State == GlobalCommandJournal.EntryState.Queued && now - held.HeldAt >= GlobalCommandJournal.FailOpen.TotalMilliseconds);
			if (HeldResponses.Any(Ready))
			{
				ready = HeldResponses.Where(Ready).ToList();
				HeldResponses.RemoveAll(held => Ready(held));
			}
			waiting = HeldResponses.Any(held => held.Journal == null);
		}
		if (waiting) ClusterRuntime.RequestGlobalSave();
		if (ready == null) return;
		foreach (HeldResponse held in ready)
		{
			if (held.Done != null)
			{
				held.Done.TrySetResult();
				continue;
			}
			Envelope envelope = held.Response;
			bool failedOpen = held.Journal?.State == GlobalCommandJournal.EntryState.Queued && held.Order > GlobalCommandLedger.DurableOrder;
			if (failedOpen) GlobalCommandJournal.CountFailOpen();
			bool sent = TrySend(envelope);
			if (!sent) lock (Sync) m_heldDropped++;
			// The node retries a lost answer under the same operation id, and the ledger answers it again. A journaled
			// answer is logged only when it failed open: the plain answers are not logged either.
			if (held.Journal == null || failedOpen)
				Console.WriteLine("[ClusterPlugin] global_command_out subsystem=" + envelope.Subsystem
					+ " target=" + envelope.Target + " sequence=" + envelope.Sequence + " order=" + held.Order
					+ (held.Journal == null ? " durable=true" : " journaled=false failed_open=true") + " sent=" + sent);
		}
	}

	/// <summary>
	/// #1318: a journaled command, run again on a replacement World Authority's game thread (GlobalCommandJournal.ReplayRestored):
	/// the subsystem's replay with the result its node was given, or else its handler.
	/// </summary>
	internal static GlobalCommandResult ReplayCommand(string subsystem, string origin, string payload, string recordedPayload)
	{
		Func<string, string, string> handler;
		Func<string, string, string, string> replay;
		lock (Sync)
		{
			CommandHandlers.TryGetValue(subsystem, out handler);
			Replays.TryGetValue(subsystem, out replay);
		}
		try
		{
			if (replay == null && handler == null) throw new InvalidOperationException("No World Authority command handler for " + subsystem);
			return new GlobalCommandResult(true, (replay != null ? replay(origin, payload, recordedPayload) : handler(origin, payload)) ?? string.Empty,
				string.Empty);
		}
		catch (Exception exception)
		{
			return new GlobalCommandResult(false, string.Empty, SecretRedactor.Redact(exception.Message));
		}
	}

	private static void RetryPendingPublishes()
	{
		lock (Sync)
		{
			foreach (SubsystemState state in States.Values)
			{
				if (state.Pending == null) continue;
				if (!TrySend(state.Pending)) return;
				state.Pending = null;
			}
		}
	}

	private static void LinkChanged(bool connected)
	{
		if (ClusterRuntime.IsWorldAuthority) return;
		lock (Sync)
		{
			m_linkEpoch++;
			if (!connected || ClusterRuntime.AuthorityGeneration <= 0) return;
			m_resyncGeneration = ClusterRuntime.AuthorityGeneration;
			foreach (string subsystem in SnapshotHandlers.Keys) PendingResync.Add(subsystem);
			m_nextResync = 0;
		}
	}

	private static void RequestResync(string subsystem, long generation)
	{
		lock (Sync)
		{
			if (generation != ClusterRuntime.AuthorityGeneration) return;
			m_resyncGeneration = generation;
			PendingResync.Add(subsystem);
			m_nextResync = 0;
		}
	}

	private static SubsystemState State(string subsystem, long generation)
	{
		if (!States.TryGetValue(subsystem, out SubsystemState state) || state.Generation != generation)
			States[subsystem] = state = new SubsystemState { Generation = generation };
		return state;
	}

	private static void Send(Envelope envelope)
	{
		if (!TrySend(envelope)) throw new InvalidOperationException("Global-state node link is disconnected");
	}

	private static bool TrySend(Envelope envelope)
	{
		if (TestPartitionActive)
		{
			Interlocked.Increment(ref m_testDroppedFrames);
			return false;
		}
		// T-0154: a command or its response must not queue behind the bulk publishes (the merged
		// antennas snapshot alone is ~5.8 MB per publish on a 64-cluster lattice, and every node
		// reports its antennas every second); they travel on the unordered reliable channel.
		return DirectTransportNodeLinkPatch.SendGlobal(JsonSerializer.SerializeToUtf8Bytes(envelope),
			ordered: envelope.Kind is not ("Command" or "Response"));
	}

	/// <summary>
	/// Chaos-test knob: drops every global-state frame this host sends OR receives until the deadline,
	/// simulating a network partition from the World Authority without killing any process. Time-bounded
	/// so a lane failure can never leave a host silently partitioned. Zero in production traffic.
	/// </summary>
	public static object ConfigureTestPartition(int milliseconds)
	{
		if (milliseconds is < 0 or > 120000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
		Interlocked.Exchange(ref m_testPartitionUntil,
			milliseconds == 0 ? 0L : (long)ClusterRuntime.LocalMilliseconds + milliseconds);
		return TestPartitionStatus();
	}

	public static object TestPartitionStatus()
		=> new { active = TestPartitionActive, droppedFrames = Interlocked.Read(ref m_testDroppedFrames) };

	/// <summary>Other WA-bound transports (the voxel journal's HTTP path) honor the same chaos
	/// partition - packet loss does not pick transports.</summary>
	public static bool IsTestPartitionActive => TestPartitionActive;

	private static bool TestPartitionActive
	{
		get
		{
			long until = Interlocked.Read(ref m_testPartitionUntil);
			return until != 0 && ClusterRuntime.LocalMilliseconds < until;
		}
	}

	private static long m_testPartitionUntil;
	private static long m_testDroppedFrames;

	private sealed record Envelope(string Kind, long Generation, string Subsystem, long Sequence, string Origin, string Target,
		string Payload, Guid OperationId = default);
	private sealed record QueuedEnvelope(Envelope Envelope, long LinkEpoch);
	private sealed record HeldResponse(long Order, Envelope Response, GlobalCommandJournal.Entry Journal = null, double HeldAt = 0, TaskCompletionSource Done = null);

	private sealed class SubsystemState
	{
		public long Generation;
		public long Sequence;
		public string Payload = string.Empty;
		public long Gaps;
		public long Snapshots;
		public Envelope Pending;
	}
}
