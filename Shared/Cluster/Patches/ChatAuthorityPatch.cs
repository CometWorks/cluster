using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using VRage.GameServices;
using VRage.Network;
using VRageMath;

namespace ClusterPlugin;

public static class ChatAuthorityPatch
{
	private const string Subsystem = "chat";
	// Chat history for the Gateway admin API: the WA is the single accept point for global
	// chat, so a bounded ring here is the authoritative cluster chat tail. Sequence numbers
	// restart with the WA incarnation; the Gateway labels history with the WA epoch.
	private const int HistoryCapacity = 1000;
	private static readonly object HistoryLock = new object();
	private static readonly Queue<HistoryEntry> History = new Queue<HistoryEntry>();
	private static long m_historySeq;
	private static readonly MethodInfo ServerRequest = typeof(MyMultiplayerBase).GetMethod("OnChatMessageReceived_Server", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(ChatMsg) }, null)
		?? throw new MissingMethodException(typeof(MyMultiplayerBase).FullName, "OnChatMessageReceived_Server(ChatMsg)");

	public sealed record HistoryEntry(long Seq, DateTimeOffset At, ulong Author, byte Channel, string Text);

	public static void Install()
	{
		MyMultiplayerBaseSeams.ClusterChatRequestPrefix = RequestPrefix;
		MyDedicatedServerSeams.ClusterChatAccepted = AcceptedPostfix;
		MyDedicatedServerSeams.ClusterChatSkipAccepted = SkipAccepted;
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ExecuteAtAuthority);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, RelayAtReplica);
		GlobalStateChannel.RegisterSnapshot(Subsystem, () => string.Empty, _ => { });
	}

	public static bool RequestPrefix(ChatMsg msg)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority) return true;
		Forward(msg, MyEventContext.Current.Sender.Value);
		return false;
	}

	private static void Forward(ChatMsg msg, ulong sender)
	{
		if (!GlobalStateChannel.SendRequest(Subsystem, JsonSerializer.Serialize(Message.From(msg, sender))))
			Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=chat action=dropped");
		// ponytail: drop during WA outages; replay requires persisted deduplication before it is safe.
	}

	// A player's line reaches plugins and mods (ChatMessageReceived, MessageRecieved, admin debug commands) on the
	// player's node when the accepted line comes back from the WA, not on the WA, where the player has no character.
	// Only lines the WA accepted count: its prefixes (Magnetar, Hangar) and checks (anti-spam) still decide (SE-1261).
	private static bool SkipAccepted(ulong sender) => ClusterRuntime.IsClustered && ClusterRuntime.IsWorldAuthority
		&& sender != 0 && sender != MyMultiplayer.Static.ServerId;

	public static void AcceptedPostfix(ChatMsg msg, ulong sender)
	{
		if (ClusterRuntime.IsWorldAuthority)
		{
			Record(msg);
			try { GlobalStateChannel.Publish(Subsystem, JsonSerializer.Serialize(Message.From(msg, sender)), retry: false); }
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=chat action=dropped error="
					+ SecretRedactor.Redact(exception.Message));
			}
		}
	}

	private static void Record(ChatMsg msg)
	{
		lock (HistoryLock)
		{
			History.Enqueue(new HistoryEntry(++m_historySeq, DateTimeOffset.UtcNow, msg.Author, msg.Channel, msg.Text));
			while (History.Count > HistoryCapacity)
				History.Dequeue();
		}
	}

	/// <summary>Accepted global-chat tail with Seq &gt; afterSeq, oldest first, for /cluster/chat-history.</summary>
	public static (IReadOnlyList<HistoryEntry> Entries, long NextCursor) ReadHistory(long afterSeq, int limit)
	{
		lock (HistoryLock)
		{
			List<HistoryEntry> entries = History.Where(entry => entry.Seq > afterSeq)
				.Take(Math.Max(1, Math.Min(limit, HistoryCapacity))).ToList();
			return (entries, entries.Count == 0 ? Math.Max(afterSeq, m_historySeq) : entries[entries.Count - 1].Seq);
		}
	}

	/// <summary>
	/// Cluster-wide chat send for the Gateway admin API. On the World Authority the message
	/// enters the same accept path a player request takes; on a regular node it forwards over
	/// the global-state channel exactly like a relayed player message. The sender must be a
	/// real identity endpoint - authorship is part of the admission model, not a free string.
	/// </summary>
	public static bool SendFromControl(ulong sender, string text)
	{
		var msg = new ChatMsg { Text = text, Author = sender, Channel = (byte)ChatChannel.Global, CrossChatEnabled = true };
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority)
		{
			MySandboxGame.Static.Invoke(() =>
			{
				using (MyEventContext.Set(new EndpointId(sender), null, isInvokedLocally: false))
					ServerRequest.Invoke(null, new object[] { msg });
			}, "ClusterPlugin control chat send");
			return true;
		}
		Forward(msg, sender);
		return true;
	}

	private static void ExecuteAtAuthority(string origin, string payload)
	{
		Message message = JsonSerializer.Deserialize<Message>(payload) ?? throw new InvalidOperationException("Invalid chat request");
		MySandboxGame.Static.Invoke(() =>
		{
			using (MyEventContext.Set(new EndpointId(message.Sender), null, isInvokedLocally: false))
				ServerRequest.Invoke(null, new object[] { message.ToChatMsg() });
		}, "ClusterPlugin World Authority chat request");
	}

	private static void RelayAtReplica(string payload)
	{
		Message message = JsonSerializer.Deserialize<Message>(payload) ?? throw new InvalidOperationException("Invalid chat success");
		ChatMsg msg = message.ToChatMsg();
		var channel = (ChatChannel)message.Channel;
		// Vanilla sends a targeted broadcast controller or scripted message only to its target, also
		// when the target is the player whose request triggered it.
		bool targeted = (channel is ChatChannel.BroadcastController or ChatChannel.GlobalScripted) && message.TargetId > 0;
		foreach (ulong member in MyMultiplayer.Static.Members.Where(member => (targeted || member != message.Sender) && member != MyMultiplayer.Static.ServerId))
		{
			long identity = MySession.Static.Players.TryGetIdentityId(member);
			if ((channel == ChatChannel.Private || targeted) && identity != message.TargetId) continue;
			if (channel == ChatChannel.Faction && MySession.Static.Factions.TryGetFactionById(message.TargetId)?.IsMember(identity) != true) continue;
			MyMultiplayerBaseSeams.ClusterRelayChat(msg, member);
		}
		// The sender's own node, the one it is attached to, accepts the line the way vanilla does (SkipAccepted).
		if (!ClusterRuntime.IsWorldAuthority && message.Sender != 0 && MyMultiplayer.Static.Members.Contains(message.Sender))
			using (MyEventContext.Set(new EndpointId(message.Sender), null, isInvokedLocally: false))
				MyMultiplayer.Static.OnChatMessage(ref msg);
	}

	// CustomData carries the shown author of broadcast controller and scripted messages: without it
	// the client names the node's server member, "Good.bot" (SE-943).
	private sealed record Message(string Text, ulong Author, byte Channel, long TargetId, bool CrossChatEnabled, ulong Sender,
		bool HasCustomData = false, string AuthorName = null, ulong? SenderId = null, uint? TextColor = null)
	{
		public static Message From(ChatMsg msg, ulong sender) => new Message(msg.Text, msg.Author, msg.Channel, msg.TargetId, msg.CrossChatEnabled, sender,
			msg.CustomData.HasValue, msg.CustomData?.AuthorName, msg.CustomData?.SenderId, msg.CustomData?.TextColor?.PackedValue);

		public ChatMsg ToChatMsg() => new ChatMsg
		{
			Text = Text, Author = Author, Channel = Channel, TargetId = TargetId, CrossChatEnabled = CrossChatEnabled,
			CustomData = HasCustomData
				? new ChatMessageCustomData { AuthorName = AuthorName, SenderId = SenderId, TextColor = TextColor.HasValue ? new Color(TextColor.Value) : null }
				: null,
		};
	}
}
