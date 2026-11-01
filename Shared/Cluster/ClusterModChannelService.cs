using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using Sandbox;
using Sandbox.ModAPI;

namespace ClusterPlugin;

/// <summary>
/// P2-MOD: the cluster mod channel. Mods cannot reference the plugin assembly - they live in the script sandbox
/// and their only interop surface is <c>IMyUtilities.SendModMessage</c> / <c>RegisterMessageHandler</c> - so the
/// channel is a pair of well-known message ids. A mod sends an <c>object[]</c> to <see cref="SendMessageId"/> and
/// receives an <c>object[]</c> on <see cref="ReceiveMessageId"/>:
/// <para>
/// Mod to plugin: <c>["broadcast", channel, payload]</c> fans to every other active node;
/// <c>["request", channel, payload, correlationId]</c> goes to the World Authority's mods;
/// <c>["respond", channel, payload, correlationId, replyTo]</c> answers a request back to its origin node.
/// </para>
/// <para>
/// Plugin to mod: <c>["broadcast", channel, origin, payload]</c>, <c>["request", channel, origin, payload,
/// correlationId]</c>, <c>["response", channel, origin, payload, correlationId]</c>, and
/// <c>["fault", channel, error, correlationId]</c>. Faults are the contract's error path: a quota violation, an
/// oversized payload, an unreachable node or a refused delivery comes back to the sending mod as a fault instead
/// of vanishing - a mod cannot read a log file, so the log is not an error channel.
/// </para>
/// <para>
/// #1280 (v1, <see cref="ApiVersion"/>) adds what a class D mod needs to decide on the WA and act where the grid
/// or the player is: <c>role</c>, <c>to-node</c>, <c>to-owner</c>, <c>owner-of</c>, <c>player-nodes</c>, the
/// handover hooks and <c>reset</c>. Docs/ModChannel.md is the mod author's reference.
/// </para>
/// Delivery is exactly-once per operation id and in per-(origin, channel) order; sends are queued to a background
/// worker so a mod's game-thread send never blocks on another node's HTTP round trip. A sender that restarts
/// starts a new order: its sequence numbers begin again at 1 under a new <see cref="Incarnation"/>.
/// <c>to-owner</c> and <c>owner-of</c> are unordered and run on their own worker.
/// </summary>
public static class ClusterModChannelService
{
	/// <summary>Mods send here: MyAPIGateway.Utilities.SendModMessage(SendMessageId, object[] { ... }).</summary>
	public const long SendMessageId = 0x434C_5553_4D4F_4453; // "CLUSMODS"
	/// <summary>Mods listen here: MyAPIGateway.Utilities.RegisterMessageHandler(ReceiveMessageId, handler).</summary>
	public const long ReceiveMessageId = 0x434C_5553_4D4F_4452; // "CLUSMODR"
	/// <summary>The protocol version a <c>role</c> answer carries: 1 since #1280.</summary>
	public const int ApiVersion = 1;

	public const int MaxChannelLength = 64;
	public const int MaxPayloadLength = 16 * 1024;
	/// <summary>Token bucket per channel: this many sends in <see cref="QuotaWindowMilliseconds"/>.</summary>
	public const int QuotaMessages = 30;
	public const int QuotaWindowMilliseconds = 10_000;
	/// <summary>How long <c>to-owner</c> and <c>owner-of</c> keep looking while the entity is in a handover.</summary>
	public const int OwnerSearchMilliseconds = 10_000;

	private const int MaxDeliveredKeys = 4096;
	private const int MaxBufferedPerSender = 64;
	private const int MaxJournal = 128;
	private const int MaxChannels = 256;
	private const int MaxOwnerCache = 4096;
	private const double RosterCacheMilliseconds = 5_000;
	private const int RosterPollMilliseconds = 5_000;
	private const int OwnerRetryMilliseconds = 250;
	private const string HandoverPlugin = "cluster.mod-channel";

	private static readonly object Sync = new object();
	private static readonly HttpClient Transport = ClusterHttp.Client(TimeSpan.FromSeconds(10));
	private static readonly Dictionary<Guid, bool> Delivered = new();
	private static readonly Queue<Guid> DeliveredOrder = new();
	private static readonly Dictionary<string, long> ReceivedSequence = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, SortedDictionary<long, ClusterModDelivery>> Buffered = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, long> Sequences = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, Queue<double>> QuotaBuckets = new(StringComparer.Ordinal);
	private static readonly List<object> Journal = new();
	private static readonly BlockingCollection<ClusterModDelivery> Outgoing = new(new ConcurrentQueue<ClusterModDelivery>());
	private static readonly BlockingCollection<OwnerJob> Routed = new(new ConcurrentQueue<OwnerJob>());
	/// <summary>Channels a mod sent or received on here: a reset goes to each of them.</summary>
	private static readonly HashSet<string> Channels = new(StringComparer.Ordinal);
	/// <summary>Channels whose mods asked for the handover hooks (game thread).</summary>
	private static readonly List<string> HandoverChannels = new();
	/// <summary>Last node that accepted a <c>to-owner</c> for an entity: asked first next time.</summary>
	private static readonly Dictionary<long, string> OwnerCache = new();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	/// <summary>This process. A restarted node keeps its name, so receivers key the order by both.</summary>
	private static readonly Guid Incarnation = Guid.NewGuid();
	private static readonly string Gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
	private static ModRoster m_roster = ModRoster.Empty;
	private static double m_rosterExpires;
	/// <summary>The handover capture a mod's <c>handover-state</c> answers into (game thread, inside Leaving).</summary>
	private static (long Grid, Dictionary<string, string> States)? m_capture;
	private static IDisposable m_handoverRegistration;
	private static bool m_used;
	private static long m_sent;
	private static long m_received;
	private static long m_faults;
	private static long m_resets;

	internal static void Install()
	{
		ClusterRuntime.AuthorizeRegistryClient(Transport);
		MyAPIUtilities.Static.RegisterMessageHandler(SendMessageId, HandleModSend);
		new Thread(SendLoop) { IsBackground = true, Name = "ClusterModChannel sender" }.Start();
		new Thread(OwnerLoop) { IsBackground = true, Name = "ClusterModChannel owner router" }.Start();
		new Thread(RosterLoop) { IsBackground = true, Name = "ClusterModChannel roster" }.Start();
	}

	/// <summary>Entry point for mods; runs on the game thread, inside the sending mod's SendModMessage call.</summary>
	private static void HandleModSend(object payload)
	{
		string channel = null;
		long correlation = 0;
		try
		{
			if (payload is not object[] parts || parts.Length < 2 || parts[0] is not string kind || parts[1] is not string channelName)
				throw new InvalidOperationException("mod message must be object[] { kind, channel, ... }");
			channel = channelName;
			if (string.IsNullOrEmpty(channel) || channel.Length > MaxChannelLength)
				throw new InvalidOperationException("channel must be 1.." + MaxChannelLength + " characters");
			// Answered even without a registry: InCluster false is the answer a mod gating itself needs.
			if (kind == "role")
			{
				Hand(new object[] { "role", channel, ClusterRuntime.IsClustered, ModCompatService.Role, Node, ApiVersion });
				return;
			}
			if (!ClusterRuntime.IsClustered) return;
			NoteChannel(channel);
			switch (kind)
			{
				case "broadcast":
				{
					string body = Body(parts, 2);
					TakeQuota(channel);
					foreach (string target in Roster().Others)
						Enqueue(NewDelivery("broadcast", channel, body, 0, target));
					break;
				}
				case "request":
				{
					string body = Body(parts, 2);
					if (parts.Length < 4 || (correlation = AsLong(parts[3])) == 0)
						throw new InvalidOperationException("request needs a non-zero correlation id");
					TakeQuota(channel);
					string authority = Roster().WorldAuthority
						?? throw new InvalidOperationException("no active World Authority to receive the request");
					Enqueue(NewDelivery("request", channel, body, correlation, authority));
					break;
				}
				case "respond":
				{
					string body = Body(parts, 2);
					if (parts.Length < 5 || (correlation = AsLong(parts[3])) == 0 || parts[4] is not string replyTo
						|| string.IsNullOrEmpty(replyTo))
						throw new InvalidOperationException("respond needs a correlation id and the origin node to reply to");
					TakeQuota(channel);
					Enqueue(NewDelivery("response", channel, body, correlation, replyTo));
					break;
				}
				case "to-node":
				{
					string body = Body(parts, 2);
					if (parts.Length < 4 || parts[3] is not string node || string.IsNullOrEmpty(node) || node.Length > 128)
						throw new InvalidOperationException("to-node needs the target node");
					TakeQuota(channel);
					Enqueue(NewDelivery("to-node", channel, body, 0, node));
					break;
				}
				case "to-owner":
				{
					string body = Body(parts, 2);
					long entity = parts.Length >= 4 ? AsLong(parts[3]) : 0;
					if (entity == 0) throw new InvalidOperationException("to-owner needs a non-zero entity id");
					// Optional: the correlation id its fault carries, so the mod knows which send to retry.
					correlation = parts.Length >= 5 ? AsLong(parts[4]) : 0;
					TakeQuota(channel);
					Route(new OwnerJob(channel, entity, correlation, new ClusterModDelivery(Guid.NewGuid(), Node, "to-owner", channel, body,
						correlation, 1, Node, Incarnation, entity)));
					break;
				}
				case "owner-of":
				{
					long entity = parts.Length >= 3 ? AsLong(parts[2]) : 0;
					if (entity == 0 || parts.Length < 4 || (correlation = AsLong(parts[3])) == 0)
						throw new InvalidOperationException("owner-of needs a non-zero entity id and correlation id");
					TakeQuota(channel);
					Route(new OwnerJob(channel, entity, correlation, null));
					break;
				}
				case "player-nodes":
					Hand(new object[] { "player-nodes", channel, PlayerNodes() });
					break;
				case "handover-hooks":
					SubscribeHandover(channel);
					break;
				case "handover-state":
				{
					long grid = parts.Length >= 3 ? AsLong(parts[2]) : 0;
					string state = Body(parts, 3);
					if (m_capture is not { } capture || capture.Grid != grid)
						throw new InvalidOperationException("handover-state answers a before-handover for that grid, inside its handler");
					capture.States[channel] = state;
					break;
				}
				default:
					throw new InvalidOperationException("unknown mod message kind " + kind);
			}
		}
		catch (Exception exception)
		{
			// The sender is on the game thread inside its own SendModMessage; fault it synchronously.
			Fault(channel ?? string.Empty, SecretRedactor.Redact(exception.Message), correlation);
		}
	}

	private static string Body(object[] parts, int index)
	{
		if (parts.Length <= index || parts[index] is not string body)
			throw new InvalidOperationException("mod message must be object[] { kind, channel, payload, ... }");
		if (body.Length > MaxPayloadLength)
			throw new InvalidOperationException("payload exceeds " + MaxPayloadLength + " characters");
		return body;
	}

	/// <summary>Accepts a delivery from another node. Game thread (control server Invoke).</summary>
	public static object Deliver(ClusterModDelivery delivery)
	{
		ClusterModChannelCodec.Validate(delivery);
		if (delivery.Kind == "to-owner") return DeliverToOwner(delivery);
		lock (Sync)
		{
			if (Delivered.ContainsKey(delivery.OperationId)) return new { delivered = true, replay = true };
			MarkDelivered(delivery.OperationId);
		}
		string sender = delivery.Origin + "/" + delivery.Incarnation + "/" + delivery.Channel;
		var ready = new List<ClusterModDelivery>();
		bool gap = false;
		lock (Sync)
		{
			ReceivedSequence.TryGetValue(sender, out long last);
			if (last == 0) last = delivery.Sequence - 1;
			if (delivery.Sequence <= last) return new { delivered = false, error = "duplicate sequence" };
			SortedDictionary<long, ClusterModDelivery> pending = Buffered.TryGetValue(sender, out var existing)
				? existing : Buffered[sender] = new SortedDictionary<long, ClusterModDelivery>();
			pending[delivery.Sequence] = delivery;
			while (pending.Count > 0)
			{
				long next = pending.Keys.First();
				if (next != last + 1 && pending.Count <= MaxBufferedPerSender) break;
				gap |= next != last + 1;
				ready.Add(pending[next]);
				pending.Remove(next);
				last = next;
			}
			ReceivedSequence[sender] = last;
			if (pending.Count == 0) Buffered.Remove(sender);
		}
		NoteChannel(delivery.Channel);
		// The skipped messages are not coming: the order toward this sender broke, which is what a reset says.
		if (gap) Reset(delivery.Channel, "gap:" + delivery.Origin);
		foreach (ClusterModDelivery item in ready)
		{
			Interlocked.Increment(ref m_received);
			object[] handed = item.Kind switch
			{
				"broadcast" => new object[] { "broadcast", item.Channel, item.Origin, item.Payload },
				"request" => new object[] { "request", item.Channel, item.Origin, item.Payload, item.CorrelationId },
				"response" => new object[] { "response", item.Channel, item.Origin, item.Payload, item.CorrelationId },
				"to-node" => new object[] { "to-node", item.Channel, item.Origin, item.Payload },
				_ => null
			};
			if (handed != null) MyAPIUtilities.Static.SendModMessage(ReceiveMessageId, handed);
		}
		return new { delivered = true };
	}

	/// <summary>
	/// A <c>to-owner</c> lands only where the entity is owned at the moment of delivery; the sender tries the nodes
	/// in turn, so this refusal is the routing. Checked before the operation id is recorded: a refused id may still
	/// be accepted by the real owner, and a replay of an accepted one is a success. Unordered - no sequence.
	/// </summary>
	private static object DeliverToOwner(ClusterModDelivery delivery)
	{
		lock (Sync)
			if (Delivered.ContainsKey(delivery.OperationId)) return new { delivered = true, replay = true };
		string residence = ResidenceOf(delivery.EntityId);
		if (residence != nameof(PluginEntityResidence.Local))
			return new { delivered = false, error = "not the owner", residence };
		lock (Sync) MarkDelivered(delivery.OperationId);
		NoteChannel(delivery.Channel);
		Interlocked.Increment(ref m_received);
		MyAPIUtilities.Static.SendModMessage(ReceiveMessageId,
			new object[] { "to-owner", delivery.Channel, delivery.Origin, delivery.Payload, delivery.EntityId, delivery.CorrelationId });
		return new { delivered = true };
	}

	/// <summary>Where an entity is from this node's view: Local (owned here), Frozen (in a handover), Unowned, Absent. Game thread.</summary>
	public static string ResidenceOf(long entityId) =>
		(PluginServices.Instance?.LocateEntity(entityId).Residence ?? PluginEntityResidence.Absent).ToString();

	private static void MarkDelivered(Guid operation)
	{
		Delivered[operation] = true;
		DeliveredOrder.Enqueue(operation);
		if (DeliveredOrder.Count > MaxDeliveredKeys) Delivered.Remove(DeliveredOrder.Dequeue());
	}

	public static object Status()
	{
		lock (Sync)
			return new
			{
				node = Node,
				sent = m_sent,
				received = m_received,
				faults = m_faults,
				resets = m_resets,
				queued = Outgoing.Count,
				routing = Routed.Count,
				buffered = Buffered.Sum(entry => entry.Value.Count),
				channels = Channels.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
				handoverChannels = HandoverChannels.ToArray(),
				roster = new { worldAuthority = m_roster.WorldAuthority, nodes = m_roster.Epochs },
				journal = Journal.ToArray()
			};
	}

	/// <summary>
	/// Sender-side quota, taken before anything leaves the node: a mod that floods its channel gets faults, not a
	/// silently shaped stream, and the cluster never carries traffic that was going to be rejected anyway.
	/// </summary>
	private static void TakeQuota(string channel)
	{
		lock (Sync)
		{
			Queue<double> bucket = QuotaBuckets.TryGetValue(channel, out var existing)
				? existing : QuotaBuckets[channel] = new Queue<double>();
			double now = ClusterRuntime.LocalMilliseconds;
			while (bucket.Count > 0 && bucket.Peek() <= now - QuotaWindowMilliseconds) bucket.Dequeue();
			if (bucket.Count >= QuotaMessages)
				throw new InvalidOperationException("channel quota exceeded: " + QuotaMessages + " messages per "
					+ QuotaWindowMilliseconds / 1000 + " s");
			bucket.Enqueue(now);
		}
	}

	private static ClusterModDelivery NewDelivery(string kind, string channel, string payload, long correlation, string target)
	{
		string stream = channel + "/" + target;
		long sequence;
		lock (Sync)
		{
			Sequences.TryGetValue(stream, out sequence);
			Sequences[stream] = ++sequence;
		}
		return new ClusterModDelivery(Guid.NewGuid(), Node, kind, channel, payload, correlation, sequence, target,
			Incarnation);
	}

	private static void Enqueue(ClusterModDelivery delivery)
	{
		if (!Outgoing.TryAdd(delivery))
			throw new InvalidOperationException("mod channel send queue rejected the message");
	}

	private static void Route(OwnerJob job)
	{
		if (!Routed.TryAdd(job))
			throw new InvalidOperationException("mod channel owner queue rejected the message");
	}

	private static void SendLoop()
	{
		foreach (ClusterModDelivery delivery in Outgoing.GetConsumingEnumerable())
		{
			string error = null;
			try
			{
				using JsonDocument result = Post(delivery);
				if (!result.RootElement.TryGetProperty("delivered", out JsonElement delivered) || !delivered.GetBoolean())
					error = result.RootElement.TryGetProperty("error", out JsonElement why) ? why.GetString() : "delivery refused";
			}
			catch (Exception exception)
			{
				error = SecretRedactor.Redact(exception.Message);
			}
			Interlocked.Increment(ref m_sent);
			RecordJournal(delivery, error);
			if (error != null) FaultLater(delivery.Channel, error, delivery.CorrelationId);
		}
	}

	/// <summary>POSTs a delivery to its target node's control endpoint; throws with the body on an HTTP error.</summary>
	private static JsonDocument Post(ClusterModDelivery delivery)
	{
		(_, string control) = SpawnDirectorService.ResolveNodeControl(delivery.TargetNode);
		using StringContent content = new StringContent(ClusterModChannelCodec.Write(delivery), Encoding.UTF8, "application/json");
		using HttpResponseMessage response = Transport
			.PostAsync(control.TrimEnd('/') + "/cluster/mod-deliver", content).GetAwaiter().GetResult();
		string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode) throw new InvalidOperationException(SecretRedactor.Redact(body));
		return JsonDocument.Parse(body);
	}

	/// <summary>
	/// <c>to-owner</c> and <c>owner-of</c>: asks the regular nodes in turn (the last owner first) until one owns the
	/// entity. While a node reports it Frozen or Unowned (a handover in flight, or a node that holds only a copy), or
	/// cannot be asked, the search repeats for up to <see cref="OwnerSearchMilliseconds"/>; when every node says Absent it ends.
	/// A request whose outcome is unknown (a timeout after the request left) ends the search with a fault: trying
	/// the next node could deliver twice, if the entity moved there meanwhile.
	/// </summary>
	private static void OwnerLoop()
	{
		foreach (OwnerJob job in Routed.GetConsumingEnumerable())
		{
			string owner = null, error = null;
			double deadline = ClusterRuntime.LocalMilliseconds + OwnerSearchMilliseconds;
			try
			{
				while (owner == null && error == null)
				{
					bool held = false;
					foreach (string node in Candidates(job.Entity))
					{
						string residence;
						try { residence = job.Delivery == null ? Locate(node, job.Entity) : Offer(job.Delivery with { TargetNode = node }); }
						catch (Exception exception) when (job.Delivery != null && OutcomeUnknown(exception))
						{
							error = "delivery outcome unknown at " + node + ": " + SecretRedactor.Redact(exception.Message);
							break;
						}
						catch (Exception) { held = true; continue; } // an unreachable node may be the owner
						if (residence == nameof(PluginEntityResidence.Local)) { owner = node; break; }
						held |= residence != nameof(PluginEntityResidence.Absent);
					}
					if (owner != null || error != null) break;
					if (!held || ClusterRuntime.LocalMilliseconds >= deadline)
					{
						error = "no node owns entity " + job.Entity + (held ? " within " + OwnerSearchMilliseconds / 1000 + " s" : "");
						break;
					}
					Thread.Sleep(OwnerRetryMilliseconds);
				}
			}
			catch (Exception exception)
			{
				error = SecretRedactor.Redact(exception.Message);
			}
			if (owner != null)
				lock (Sync)
				{
					if (OwnerCache.Count >= MaxOwnerCache) OwnerCache.Clear();
					OwnerCache[job.Entity] = owner;
				}
			if (job.Delivery != null)
			{
				Interlocked.Increment(ref m_sent);
				RecordJournal(job.Delivery with { TargetNode = owner ?? "none" }, owner == null ? error : null);
				if (owner == null) FaultLater(job.Channel, error, job.Correlation);
				continue;
			}
			string answer = owner ?? string.Empty;
			Later(() => Hand(new object[] { "owner", job.Channel, job.Entity, answer, job.Correlation }));
		}
	}

	/// <summary>A timeout or a broken response: the request may have been delivered. A refused connection, an HTTP
	/// error status or an unresolved endpoint was not.</summary>
	private static bool OutcomeUnknown(Exception exception) =>
		exception is TaskCanceledException || exception is HttpRequestException { InnerException: not SocketException };

	/// <summary>The active regular nodes, this one included, the entity's last owner first. The WA owns no partition.</summary>
	private static IEnumerable<string> Candidates(long entity)
	{
		string cached;
		lock (Sync) OwnerCache.TryGetValue(entity, out cached);
		string[] regulars = Roster().Regulars;
		return cached != null && regulars.Contains(cached, StringComparer.Ordinal)
			? regulars.Where(node => node != cached).Prepend(cached) : regulars;
	}

	/// <summary>Offers a <c>to-owner</c> delivery to one node: Local when it took it, else that node's residence.</summary>
	private static string Offer(ClusterModDelivery delivery)
	{
		using JsonDocument result = Post(delivery);
		if (result.RootElement.TryGetProperty("delivered", out JsonElement delivered) && delivered.GetBoolean())
			return nameof(PluginEntityResidence.Local);
		return result.RootElement.TryGetProperty("residence", out JsonElement residence)
			? residence.GetString() : nameof(PluginEntityResidence.Absent);
	}

	/// <summary>One node's view of an entity: GET /cluster/mod-locate.</summary>
	private static string Locate(string node, long entity)
	{
		(_, string control) = SpawnDirectorService.ResolveNodeControl(node);
		using HttpResponseMessage response = Transport
			.GetAsync(control.TrimEnd('/') + "/cluster/mod-locate?entity=" + entity).GetAwaiter().GetResult();
		string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode) throw new InvalidOperationException(SecretRedactor.Redact(body));
		using JsonDocument result = JsonDocument.Parse(body);
		return result.RootElement.GetProperty("residence").GetString();
	}

	/// <summary>(identityId, steamId, node) per online player, from the cluster-wide list (OnlinePlayersService).</summary>
	private static object[] PlayerNodes() => OnlinePlayersService.Cluster
		.OrderBy(player => player.IdentityId)
		.Select(player => (object)new object[] { player.IdentityId, player.SteamId, player.Node })
		.ToArray();

	private static void NoteChannel(string channel)
	{
		m_used = true;
		lock (Sync)
			if (Channels.Count < MaxChannels) Channels.Add(channel);
	}

	// -- handover hooks -------------------------------------------------------------------------------------------

	/// <summary>
	/// A mod's game-logic fields reset at every handover (B4): the grid is rebuilt from its object builder on the
	/// target. A channel that asks for the hooks gets <c>["before-handover", channel, grid]</c> on the source while
	/// the grid is frozen for capture, answers with <c>["handover-state", channel, grid, state]</c> inside that
	/// handler, and gets <c>["after-import", channel, grid, state or null]</c> on the target once the grid is live,
	/// then <c>["handover-left", ...]</c> or <c>["handover-stayed", ...]</c> on the source. The states ride the
	/// T-0256 plugin grid-handover pipeline as one plugin entry.
	/// </summary>
	private static void SubscribeHandover(string channel)
	{
		lock (Sync)
			if (!HandoverChannels.Contains(channel)) HandoverChannels.Add(channel);
		m_handoverRegistration ??= PluginHandoverService.Register(HandoverPlugin, new ModHandoverHooks());
	}

	private static string[] SubscribedChannels()
	{
		lock (Sync) return HandoverChannels.ToArray();
	}

	private sealed class ModHandoverHooks : IPluginGridHandover
	{
		public byte[] Leaving(long gridId)
		{
			var states = new Dictionary<string, string>(StringComparer.Ordinal);
			m_capture = (gridId, states);
			try
			{
				foreach (string channel in SubscribedChannels())
					MyAPIUtilities.Static.SendModMessage(ReceiveMessageId, new object[] { "before-handover", channel, gridId });
			}
			finally { m_capture = null; }
			if (states.Count == 0) return null;
			byte[] state = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(states));
			if (state.Length <= PluginGridHandover.MaxStateBytes) return state;
			foreach (string channel in states.Keys)
				Fault(channel, "handover state of grid " + gridId + " dropped: " + state.Length + " bytes over all channels, the limit is "
					+ PluginGridHandover.MaxStateBytes, 0);
			return null;
		}

		public void Left(long gridId) => Notify("handover-left", gridId);

		public void Stayed(long gridId) => Notify("handover-stayed", gridId);

		public void Arrived(long gridId, byte[] state)
		{
			Dictionary<string, string> states = null;
			if (state != null)
				try { states = JsonSerializer.Deserialize<Dictionary<string, string>>(state); }
				catch (Exception error) { Console.WriteLine("[ClusterPlugin] mod_channel handover state unreadable grid=" + gridId + ": " + error.Message); }
			IEnumerable<string> channels = SubscribedChannels().Concat(states?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
			foreach (string channel in channels)
			{
				string carried = null;
				states?.TryGetValue(channel, out carried);
				MyAPIUtilities.Static.SendModMessage(ReceiveMessageId, new object[] { "after-import", channel, gridId, carried });
			}
		}

		private static void Notify(string kind, long gridId)
		{
			foreach (string channel in SubscribedChannels())
				MyAPIUtilities.Static.SendModMessage(ReceiveMessageId, new object[] { kind, channel, gridId });
		}
	}

	// -- reset ----------------------------------------------------------------------------------------------------

	/// <summary>
	/// Delivery is not durable: a message in flight to a node that dies, or to a WA that is replaced, is gone. The
	/// roster is polled while any mod uses the channel, and every change in who serves is told to each channel as
	/// <c>["reset", channel, reason]</c>, the mod's cue to resync its own state.
	/// </summary>
	private static void RosterLoop()
	{
		while (true)
		{
			Thread.Sleep(RosterPollMilliseconds);
			if (!m_used || ClusterRuntime.RegistryUnreachable) continue;
			try { Fetch(); }
			catch (Exception) { /* the next poll tries again; a send meanwhile faults on its own */ }
		}
	}

	private static void Reset(string channel, string reason)
	{
		Interlocked.Increment(ref m_resets);
		Console.WriteLine("[ClusterPlugin] mod_channel reset channel=" + channel + " reason=" + reason);
		MyAPIUtilities.Static.SendModMessage(ReceiveMessageId, new object[] { "reset", channel, reason });
	}

	private static void ResetAll(IReadOnlyList<string> reasons)
	{
		string[] channels;
		lock (Sync) channels = Channels.OrderBy(name => name, StringComparer.Ordinal).ToArray();
		foreach (string reason in reasons)
			foreach (string channel in channels)
				Reset(channel, reason);
	}

	/// <summary>What changed between two rosters, as reset reasons; this node itself is left out.</summary>
	internal static List<string> RosterChanges(ModRoster before, ModRoster after, string self)
	{
		var reasons = new List<string>();
		if (!string.Equals(before.WorldAuthority, after.WorldAuthority, StringComparison.Ordinal))
			reasons.Add("wa-changed:" + (before.WorldAuthority ?? "none") + "->" + (after.WorldAuthority ?? "none"));
		foreach ((string node, long epoch) in before.Epochs.OrderBy(entry => entry.Key, StringComparer.Ordinal))
			if (node == self) continue;
			else if (!after.Epochs.TryGetValue(node, out long now)) reasons.Add("node-gone:" + node);
			else if (now != epoch) reasons.Add("node-restarted:" + node);
		foreach (string node in after.Epochs.Keys.OrderBy(name => name, StringComparer.Ordinal))
			if (node != self && !before.Epochs.ContainsKey(node)) reasons.Add("node-joined:" + node);
		return reasons;
	}

	// -- roster ---------------------------------------------------------------------------------------------------

	/// <summary>Active nodes from the gateway registry, briefly cached: broadcasts fan to all, requests go to the WA.</summary>
	private static ModRoster Roster()
	{
		// T-0350: the sender is on the game thread. While the registry is unreachable the last roster serves whatever
		// its age, and with none the send faults at once, as a failed lookup does - no request is made.
		lock (Sync)
			switch (RegistryHoldPolicy.CachedLookup(ClusterRuntime.RegistryUnreachable, m_rosterExpires != 0,
				ClusterRuntime.LocalMilliseconds, m_rosterExpires))
			{
				case RegistryHoldPolicy.Lookup.Cached: return m_roster;
				case RegistryHoldPolicy.Lookup.Fail: throw new InvalidOperationException("Registry unreachable: no node roster");
			}
		return Fetch();
	}

	private static ModRoster Fetch()
	{
		if (Gateway == null) throw new InvalidOperationException("Gateway registry is not configured");
		string authority = null;
		var others = new List<string>();
		var regulars = new List<string>();
		var epochs = new Dictionary<string, long>(StringComparer.Ordinal);
		try
		{
			using JsonDocument nodes = ClusterRuntime.Get(Transport, Gateway + "/registry/nodes");
			foreach (JsonElement node in nodes.RootElement.EnumerateArray())
			{
				// The registry serializes enums as numbers: State 0 is Active, Role 0 is Regular, 1 the WA.
				if (node.GetProperty("State").GetInt32() != 0) continue;
				string name = node.GetProperty("Node").GetString();
				epochs[name] = node.TryGetProperty("Epoch", out JsonElement epoch) ? epoch.GetInt64() : 0;
				if (node.GetProperty("Role").GetInt32() == 1) authority = name;
				else regulars.Add(name);
				if (!string.Equals(name, Node, StringComparison.Ordinal)) others.Add(name);
			}
		}
		catch (Exception exception) when (ClusterRuntime.NoteRegistryFailure(exception)) { throw; }
		var roster = new ModRoster(authority, others.ToArray(), regulars.ToArray(), epochs);
		List<string> changes;
		lock (Sync)
		{
			changes = m_rosterExpires == 0 ? new List<string>() : RosterChanges(m_roster, roster, Node);
			m_roster = roster;
			m_rosterExpires = ClusterRuntime.LocalMilliseconds + RosterCacheMilliseconds;
		}
		if (changes.Count > 0) Later(() => ResetAll(changes));
		return roster;
	}

	private static long AsLong(object value) => value switch
	{
		long number => number,
		int number => number,
		string text when long.TryParse(text, out long parsed) => parsed,
		_ => 0
	};

	// -- mod hand-off ---------------------------------------------------------------------------------------------

	private static void Hand(object[] message) => MyAPIUtilities.Static.SendModMessage(ReceiveMessageId, message);

	/// <summary>Runs on the game thread, where the mods live; from a worker thread.</summary>
	private static void Later(Action action)
	{
		if (MySandboxGame.Static != null) MySandboxGame.Static.Invoke(action, "ClusterModChannel");
	}

	private static void FaultLater(string channel, string error, long correlation) =>
		Later(() => Fault(channel, error, correlation));

	private static void Fault(string channel, string error, long correlation)
	{
		Interlocked.Increment(ref m_faults);
		MyAPIUtilities.Static.SendModMessage(ReceiveMessageId, new object[] { "fault", channel, error, correlation });
	}

	private static void RecordJournal(ClusterModDelivery delivery, string error)
	{
		lock (Sync)
		{
			Journal.Add(new
			{
				operation = delivery.OperationId,
				kind = delivery.Kind,
				channel = delivery.Channel,
				target = delivery.TargetNode,
				sequence = delivery.Sequence,
				correlation = delivery.CorrelationId,
				entity = delivery.EntityId,
				delivered = error == null,
				error
			});
			if (Journal.Count > MaxJournal) Journal.RemoveAt(0);
		}
		Console.WriteLine("[ClusterPlugin] mod_channel operation=" + delivery.OperationId
			+ " kind=" + delivery.Kind + " channel=" + delivery.Channel + " target=" + delivery.TargetNode
			+ (delivery.EntityId != 0 ? " entity=" + delivery.EntityId : "")
			+ " delivered=" + (error == null) + " error=" + (error ?? "none"));
	}

	private sealed record OwnerJob(string Channel, long Entity, long Correlation, ClusterModDelivery Delivery);
}

/// <summary>The active nodes as the mod channel last read them: the WA, every node but this one, the regular
/// nodes (this one included) and each node's registry epoch.</summary>
public sealed record ModRoster(string WorldAuthority, string[] Others, string[] Regulars, Dictionary<string, long> Epochs)
{
	public static readonly ModRoster Empty = new(null, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, long>());
}

/// <summary>One mod-channel message crossing a node boundary. <see cref="EntityId"/> is set on <c>to-owner</c> only.</summary>
public sealed record ClusterModDelivery(
	Guid OperationId,
	string Origin,
	string Kind,
	string Channel,
	string Payload,
	long CorrelationId,
	long Sequence,
	string TargetNode,
	Guid Incarnation,
	long EntityId = 0);

public static class ClusterModChannelCodec
{
	public static string Write(ClusterModDelivery delivery)
	{
		Validate(delivery);
		return JsonSerializer.Serialize(delivery);
	}

	public static ClusterModDelivery Read(string payload)
	{
		if (payload == null || payload.Length > 4 * ClusterModChannelService.MaxPayloadLength)
			throw new InvalidOperationException("Invalid mod channel payload");
		ClusterModDelivery delivery = JsonSerializer.Deserialize<ClusterModDelivery>(payload)
			?? throw new InvalidOperationException("Invalid mod channel payload");
		Validate(delivery);
		return delivery;
	}

	public static void Validate(ClusterModDelivery delivery)
	{
		if (delivery == null || delivery.OperationId == Guid.Empty
			|| string.IsNullOrEmpty(delivery.Origin) || delivery.Origin.Length > 128
			|| delivery.Kind is not ("broadcast" or "request" or "response" or "to-node" or "to-owner")
			|| string.IsNullOrEmpty(delivery.Channel) || delivery.Channel.Length > ClusterModChannelService.MaxChannelLength
			|| delivery.Payload == null || delivery.Payload.Length > ClusterModChannelService.MaxPayloadLength
			|| delivery.Sequence <= 0
			|| string.IsNullOrEmpty(delivery.TargetNode) || delivery.TargetNode.Length > 128
			|| (delivery.Kind == "to-owner") != (delivery.EntityId != 0))
			throw new InvalidOperationException("Invalid mod channel payload");
	}
}
