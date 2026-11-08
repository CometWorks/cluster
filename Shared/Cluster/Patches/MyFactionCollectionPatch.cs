using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;
using VRage.ObjectBuilders;
using VRageMath;

namespace ClusterPlugin;

public static class MyFactionCollectionPatch
{
	private const string Subsystem = "factions";
	private static readonly object SnapshotSync = new object();
	private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
	private static readonly MethodInfo CreateRequest = RequiredMethod("CreateFactionRequest", typeof(MyFactionCollection.AddFactionMsg));
	private static readonly MethodInfo EditRequest = RequiredMethod("EditFactionRequest", typeof(MyFactionCollection.AddFactionMsg));
	private static readonly MethodInfo AutoAcceptRequest = RequiredMethod("ChangeAutoAcceptRequest", typeof(long), typeof(bool), typeof(bool));
	private static readonly MethodInfo StateChangeRequest = RequiredMethod("FactionStateChangeRequest", typeof(MyFactionStateChange), typeof(long), typeof(long), typeof(long));
	private static readonly FieldInfo FactionsByType = typeof(MyFactionCollection).GetField("m_factionsByType", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new MissingFieldException(typeof(MyFactionCollection).FullName, "m_factionsByType");
	private static string m_snapshot;
	private static long m_snapshotGeneration;
	private static bool m_snapshotReady;
	private static volatile MySession m_readySession;
	private static double m_nextRefresh;
	[ThreadStatic] private static bool m_applying;
	[ThreadStatic] private static bool m_accepted;
	[ThreadStatic] private static bool m_successPublishFailed;

	public static void Install()
	{
		MyFactionCollectionSeams.ClusterFactionRequestPrefix = SeamRequestPrefix;
		MyFactionCollectionSeams.ClusterFactionBoolPrefix = SeamBoolPrefix;
		MyFactionCollectionSeams.ClusterFactionVoidPrefix = SeamVoidPrefix;
		MyFactionCollectionSeams.ClusterFactionAcceptedPostfix = AcceptedPostfix;
		MyFactionSeams.ClusterAcceptHumansPrefix = AcceptHumansPrefix;
		MyFactionCollectionSeams.ClusterDefaultFactionsPrefix = DefaultFactionsPrefix;
		MyFactionCollectionSeams.ClusterDefaultFactionsPostfix = DefaultFactionsPostfix;
		GlobalStateChannel.RegisterCommandHandler(Subsystem, ExecuteAtAuthority, journaled: true, replay: ReplayAtAuthority);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplyReplicaPayload);
		GlobalStateChannel.RegisterSnapshot(Subsystem, CurrentSnapshot, ApplyReplicaPayload);
	}

	public static void Update()
	{
		MySession session = MySession.Static;
		if (session?.Ready != true) return;
		if (m_readySession != session)
		{
			lock (SnapshotSync)
			{
				m_snapshot = null;
				m_snapshotReady = false;
				m_snapshotGeneration = 0;
			}
			m_readySession = session;
		}
		FlushDamageReputation();
		if (!ClusterRuntime.IsWorldAuthority || ClusterRuntime.LocalMilliseconds < m_nextRefresh) return;
		m_nextRefresh = ClusterRuntime.LocalMilliseconds + 1000;
		PublishSnapshotIfChanged();
	}

	public static bool SeamRequestPrefix(string operation, object[] arguments)
		=> operation switch
		{
			"Create" => CreateRequestPrefix((MyFactionCollection.AddFactionMsg)arguments[0]),
			"Edit" => EditRequestPrefix((MyFactionCollection.AddFactionMsg)arguments[0]),
			"AutoAccept" => AutoAcceptRequestPrefix((long)arguments[0], (bool)arguments[1], (bool)arguments[2]),
			"StateChange" => StateChangeRequestPrefix((MyFactionStateChange)arguments[0], (long)arguments[1], (long)arguments[2], (long)arguments[3]),
			_ => throw new ArgumentOutOfRangeException(nameof(operation))
		};

	public static bool CreateRequestPrefix(MyFactionCollection.AddFactionMsg msg) => ForwardRequest("Create", new object[] { msg });
	public static bool EditRequestPrefix(MyFactionCollection.AddFactionMsg msgEdit) => ForwardRequest("Edit", new object[] { msgEdit });
	public static bool AutoAcceptRequestPrefix(long factionId, bool autoAcceptMember, bool autoAcceptPeace)
		=> ForwardRequest("AutoAccept", new object[] { factionId, autoAcceptMember, autoAcceptPeace });
	public static bool AcceptHumansPrefix(MyFaction __instance, bool value)
		=> __instance.AcceptHumans == value || ForwardRequest("AcceptHumans", new object[] { __instance.FactionId, value });
	public static bool StateChangeRequestPrefix(MyFactionStateChange action, long fromFactionId, long toFactionId, long playerId)
		=> ForwardRequest("StateChange", new object[] { action, fromFactionId, toFactionId, playerId });

	private static bool ForwardRequest(string operation, object[] arguments)
	{
		if (RunOriginal()) return true;
		try { GlobalStateChannel.SendCommand(Subsystem, FactionAuthorityCodec.Write(Request(operation, arguments))); }
		catch (Exception exception) { CommandFailed(operation, exception); }
		return false;
	}

	public static bool SeamBoolPrefix(string operation, object[] arguments, ref bool __result)
	{
		if (operation != "AddReputation") throw new ArgumentOutOfRangeException(nameof(operation));
		return AddReputationPrefix((long)arguments[0], (long)arguments[1], (int)arguments[2],
			(ReputationChangeReason)arguments[3], (bool)arguments[4], (bool)arguments[5], ref __result);
	}

	public static bool AddReputationPrefix(long playerIdentityId, long factionId, int delta, ReputationChangeReason reason,
		bool propagate, bool adminChange, ref bool __result)
	{
		if (RunOriginal()) return true;
		try
		{
			string result = GlobalStateChannel.SendCommand(Subsystem, FactionAuthorityCodec.Write(Request("AddReputation",
				new object[] { playerIdentityId, factionId, delta, reason, propagate, adminChange })));
			__result = JsonSerializer.Deserialize<bool>(result);
		}
		catch (Exception exception) { CommandFailed("AddReputation", exception); __result = false; }
		return false;
	}

	public static bool SeamVoidPrefix(string operation, object[] arguments)
		=> operation switch
		{
			"DamageReputation" => DamageReputationPrefix((long)arguments[0], (long)arguments[1], (MyReputationDamageType)arguments[2], (float)arguments[3]),
			"SetFactionReputation" => SetFactionReputationPrefix((long)arguments[0], (long)arguments[1], (int)arguments[2]),
			"SetPlayerReputation" => SetPlayerReputationPrefix((long)arguments[0], (long)arguments[1], (int)arguments[2], (ReputationChangeReason)arguments[3]),
			_ => throw new ArgumentOutOfRangeException(nameof(operation))
		};

	/// <summary>
	/// Weapon damage reputation, queued and sent to the WA without waiting. Vanilla calls this once per
	/// projectile or explosion hit on another identity's block (MySlimBlock.DoDamage), and forwarding each
	/// call as a blocking SendCommand held the game thread for a WA round trip per hit: w33 2026-09-25 23:00,
	/// node-1 at 0.15x sim with 68% of wall time in ForwardVoid (217 calls, median 59 ms) while pasted armed
	/// ships fought at the Earth base. Hits are summed per (attacker, attacked, type) and flushed once per
	/// DamageReputationFlushMs from Update(). For Damaging and GrindingWelding vanilla's delta telescopes
	/// over the running DamageInflicted total (floor(total/L) - floor(before/L)), so one call with the summed
	/// amount gives the same reputation as the separate calls; Stealing and Killing are a constant per call and
	/// are sent once per queued call.
	/// </summary>
	public static bool DamageReputationPrefix(long playerIdentityId, long attackedIdentityId, MyReputationDamageType repDamageType, float damageAmount)
	{
		if (RunOriginal()) return true;
		if (attackedIdentityId == 0) return false; // vanilla returns before doing anything
		var key = (playerIdentityId, attackedIdentityId, repDamageType);
		lock (PendingDamageSync)
		{
			PendingDamage.TryGetValue(key, out var pending);
			PendingDamage[key] = (pending.Amount + damageAmount, pending.Calls + 1);
			m_damageCallsQueued++;
		}
		return false;
	}

	private const int DamageReputationFlushMs = 1000;
	private static readonly object PendingDamageSync = new object();
	private static readonly Dictionary<(long Player, long Attacked, MyReputationDamageType Type), (float Amount, int Calls)> PendingDamage = new();
	private static double m_nextDamageFlush;
	private static double m_nextDamageLog;
	private static long m_damageCallsQueued;
	private static long m_damageCommandsSent;
	private static int m_damageFlushInFlight;

	/// <summary>
	/// SE-1280: an absolute reputation write (SetReputationBetweenPlayerAndFaction or SetReputationBetweenFactions),
	/// keyed by its pair. Player writes and faction writes are told apart by <see cref="Faction"/>.
	/// </summary>
	private readonly record struct SetKey(bool Faction, long First, long Second);

	/// <summary>SE-1280: queued absolute writes, the last value per pair; sent with the damage queue.</summary>
	private static readonly Dictionary<SetKey, (int Reputation, ReputationChangeReason Reason)> PendingSets = new();
	/// <summary>SE-1280: the writes the pool thread is sending; replicas from the WA must not undo them locally either.</summary>
	private static Dictionary<SetKey, (int Reputation, ReputationChangeReason Reason)> m_setsInFlight = new();
	private static long m_setCallsQueued;
	private static long m_setCommandsSent;

	/// <summary>Game thread: turn the queued hits and writes into WA commands and send them on a pool thread.</summary>
	private static void FlushDamageReputation()
	{
		if (ClusterRuntime.IsWorldAuthority || ClusterRuntime.LocalMilliseconds < m_nextDamageFlush) return;
		m_nextDamageFlush = ClusterRuntime.LocalMilliseconds + DamageReputationFlushMs;
		// One flush at a time: a slow WA backs the queue up here instead of piling up pool threads.
		if (Volatile.Read(ref m_damageFlushInFlight) != 0) return;
		List<(string Operation, string Payload)> payloads;
		long queued, setsQueued;
		lock (PendingDamageSync)
		{
			if (PendingDamage.Count == 0 && PendingSets.Count == 0) return;
			payloads = new List<(string, string)>(PendingDamage.Count + PendingSets.Count);
			// The absolute writes go first: before SE-1280 they reached the WA at once and the hits up to a second later.
			foreach (var entry in PendingSets)
				payloads.Add((entry.Key.Faction ? "SetFactionReputation" : "SetPlayerReputation", FactionAuthorityCodec.Write(entry.Key.Faction
					? new FactionAuthorityMessage { Operation = "SetFactionReputation", First = entry.Key.First, Second = entry.Key.Second, Number = entry.Value.Reputation }
					: new FactionAuthorityMessage
					{
						Operation = "SetPlayerReputation", First = entry.Key.First, Second = entry.Key.Second,
						Number = entry.Value.Reputation, Reason = entry.Value.Reason
					})));
			m_setsInFlight = new Dictionary<SetKey, (int, ReputationChangeReason)>(PendingSets);
			PendingSets.Clear();
			foreach (var entry in PendingDamage)
			{
				bool summable = entry.Key.Type is MyReputationDamageType.Damaging or MyReputationDamageType.GrindingWelding;
				for (int call = 0; call < (summable ? 1 : entry.Value.Calls); call++)
					payloads.Add(("DamageReputation", FactionAuthorityCodec.Write(new FactionAuthorityMessage
					{
						Operation = "DamageReputation", First = entry.Key.Player, Second = entry.Key.Attacked,
						Number = (int)entry.Key.Type, Amount = summable ? entry.Value.Amount : 0f
					})));
			}
			PendingDamage.Clear();
			queued = m_damageCallsQueued;
			setsQueued = m_setCallsQueued;
		}
		if (ClusterRuntime.LocalMilliseconds >= m_nextDamageLog)
		{
			m_nextDamageLog = ClusterRuntime.LocalMilliseconds + 10000;
			Console.WriteLine("[ClusterPlugin] factions damage_reputation flush commands=" + payloads.Count
				+ " hits_queued_total=" + queued + " commands_sent_total=" + Interlocked.Read(ref m_damageCommandsSent)
				+ " sets_queued_total=" + setsQueued + " set_commands_sent_total=" + Interlocked.Read(ref m_setCommandsSent));
		}
		Interlocked.Exchange(ref m_damageFlushInFlight, 1);
		Task.Run(() =>
		{
			string operation = null;
			try
			{
				foreach (var (op, payload) in payloads)
				{
					operation = op;
					GlobalStateChannel.SendCommand(Subsystem, payload);
					if (op == "DamageReputation") Interlocked.Increment(ref m_damageCommandsSent);
					else Interlocked.Increment(ref m_setCommandsSent);
				}
			}
			catch (Exception exception) { CommandFailed(operation, exception); }
			finally
			{
				lock (PendingDamageSync) m_setsInFlight = new Dictionary<SetKey, (int, ReputationChangeReason)>();
				Interlocked.Exchange(ref m_damageFlushInFlight, 0);
			}
		});
	}

	public static bool SetFactionReputationPrefix(long fromFactionId, long toFactionId, int reputation)
		=> QueueSet(new SetKey(true, fromFactionId, toFactionId), reputation, default);
	public static bool SetPlayerReputationPrefix(long identityId, long factionId, int reputation, ReputationChangeReason reason)
		=> QueueSet(new SetKey(false, identityId, factionId), reputation, reason);

	/// <summary>
	/// SE-1280: an absolute reputation write on a regular node is applied here at once and queued for the WA, which gets
	/// only the pair's last value with the next flush; the game thread never waits for it. On octahedron-test
	/// (2026-11-08 16:48Z) a MES damage trigger set the damage owner's reputation with a list of factions on every hit
	/// (read, add, set), each a blocking WA round trip: over a thousand of them in one frame of node-4, 68 s, until the
	/// Registry declared the node dead. Mods and scripts reach these through IMyFactionCollection.SetReputation and
	/// SetReputationBetweenPlayerAndFaction, so any of them can do the same. The local apply keeps read-modify-write
	/// callers like MES correct: the next read sees the value just written, not the WA's older one.
	/// </summary>
	private static bool QueueSet(SetKey key, int reputation, ReputationChangeReason reason)
	{
		if (RunOriginal()) return true;
		ApplySetLocally(key, reputation, reason);
		lock (PendingDamageSync)
		{
			PendingSets[key] = (reputation, reason);
			m_setCallsQueued++;
		}
		return false;
	}

	private static void ApplySetLocally(SetKey key, int reputation, ReputationChangeReason reason, bool skipUnchanged = false)
	{
		MyFactionCollection factions = MySession.Static?.Factions;
		if (factions == null) return;
		if (skipUnchanged && reputation == (key.Faction ? factions.GetRelationBetweenFactions(key.First, key.Second).Item2
			: factions.GetRelationBetweenPlayerAndFaction(key.First, key.Second).Item2)) return;
		bool applying = m_applying;
		m_applying = true;
		try
		{
			if (key.Faction) factions.SetReputationBetweenFactions(key.First, key.Second, reputation);
			else factions.SetReputationBetweenPlayerAndFaction(key.First, key.Second, reputation, reason);
		}
		finally { m_applying = applying; }
	}

	/// <summary>
	/// SE-1280: a replica or snapshot from the WA predates the writes still queued or in flight here; put them back so
	/// this node keeps reading its own last write until the WA's answer to it arrives.
	/// </summary>
	private static void ReapplyQueuedSets()
	{
		List<KeyValuePair<SetKey, (int Reputation, ReputationChangeReason Reason)>> sets;
		lock (PendingDamageSync)
		{
			if (PendingSets.Count == 0 && m_setsInFlight.Count == 0) return;
			sets = m_setsInFlight.Where(entry => !PendingSets.ContainsKey(entry.Key)).Concat(PendingSets).ToList();
		}
		// Unchanged pairs are skipped: each write fires the collection's reputation event again.
		foreach (var entry in sets) ApplySetLocally(entry.Key, entry.Value.Reputation, entry.Value.Reason, skipUnchanged: true);
	}

	public static void AcceptedPostfix(string operation, object[] arguments)
	{
		switch (operation)
		{
			case "CreateSuccess": CreateAcceptedPostfix((MyFactionCollection.AddFactionMsg)arguments[0]); break;
			case "EditSuccess": EditAcceptedPostfix((MyFactionCollection.AddFactionMsg)arguments[0], (long)arguments[1]); break;
			case "AutoAcceptSuccess": AutoAcceptAcceptedPostfix((long)arguments[0], (bool)arguments[1], (bool)arguments[2]); break;
			case "AcceptHumansSuccess": AcceptHumansAcceptedPostfix((long)arguments[0], (bool)arguments[1]); break;
			case "StateChangeSuccess": StateChangeAcceptedPostfix((MyFactionStateChange)arguments[0], (long)arguments[1], (long)arguments[2], (long)arguments[3], (long)arguments[4]); break;
			case "ReputationSuccess": ReputationAcceptedPostfix((long)arguments[0], (List<MyFactionCollection.MyReputationChangeWrapper>)arguments[1], (ReputationChangeReason)arguments[2]); break;
			default: throw new ArgumentOutOfRangeException(nameof(operation));
		}
	}

	public static void CreateAcceptedPostfix(long factionId, long founderId, string factionTag, string factionName,
		string description, string privateInfo, bool createFromDef, string type, SerializableDefinitionId? factionIconGroupId,
		int factionIconId, Vector3 factionColor, Vector3 factionIconColor, int score)
		=> CreateAcceptedPostfix(new MyFactionCollection.AddFactionMsg
		{
			FactionId = factionId, FounderId = founderId, FactionTag = factionTag, FactionName = factionName,
			FactionDescription = description, FactionPrivateInfo = privateInfo, CreateFromDefinition = createFromDef,
			Type = type, FactionIconGroupId = factionIconGroupId, FactionIconId = factionIconId,
			FactionColor = factionColor, FactionIconColor = factionIconColor, FactionScore = score
		});
	private static void CreateAcceptedPostfix(MyFactionCollection.AddFactionMsg message) => PublishSuccess("CreateSuccess", new object[] { message });
	public static void EditAcceptedPostfix(MyFactionCollection.AddFactionMsg msgEdit, long senderId) => PublishSuccess("EditSuccess", new object[] { msgEdit, senderId });
	public static void AutoAcceptAcceptedPostfix(long factionId, bool autoAcceptMember, bool autoAcceptPeace)
		=> PublishSuccess("AutoAcceptSuccess", new object[] { factionId, autoAcceptMember, autoAcceptPeace });
	public static void AcceptHumansAcceptedPostfix(long factionId, bool acceptHumans)
		=> PublishSuccess("AcceptHumansSuccess", new object[] { factionId, acceptHumans });
	public static void StateChangeAcceptedPostfix(MyFactionStateChange action, long fromFactionId, long toFactionId, long playerId, long senderId)
		=> PublishSuccess("StateChangeSuccess", new object[] { action, fromFactionId, toFactionId, playerId, senderId });
	public static void ReputationAcceptedPostfix(long playerId, List<MyFactionCollection.MyReputationChangeWrapper> changes, ReputationChangeReason reason)
		=> PublishSuccess("ReputationSuccess", new object[] { playerId, changes, reason });

	private static void PublishSuccess(string operation, object[] arguments)
	{
		if (!ClusterRuntime.IsWorldAuthority || m_applying || MySession.Static?.Ready != true) return;
		m_accepted = true;
		Console.WriteLine("[ClusterPlugin] factions success_publish operation=" + operation);
		try { GlobalStateChannel.Publish(Subsystem, FactionAuthorityCodec.Write(Success(operation, arguments))); }
		catch (Exception exception)
		{
			m_successPublishFailed = true;
			Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=factions success=" + operation
				+ " error=" + SecretRedactor.Redact(exception.Message));
		}
	}

	/// <summary>
	/// #1318: a replacement World Authority replays a journaled faction command through the handler, except a creation:
	/// vanilla CreateFactionServer allocates the faction id at random, so a replay would found the faction under another id
	/// than the one every node got. It is reported as a failed replay instead (#1335).
	/// </summary>
	private static string ReplayAtAuthority(string origin, string payload, string result)
	{
		if (FactionAuthorityCodec.Read(payload).Operation == "Create")
			throw new InvalidOperationException("A faction creation is not replayed: the World Authority allocated its id at random");
		return ExecuteAtAuthority(origin, payload);
	}

	private static string ExecuteAtAuthority(string origin, string payload)
	{
		FactionAuthorityMessage message = FactionAuthorityCodec.Read(payload);
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		MySandboxGame game = MySandboxGame.Static;
		if (game == null || MySession.Static?.Ready != true || m_readySession != MySession.Static)
			throw new InvalidOperationException("The World Authority faction service is not ready");
		Action execute = () =>
		{
			try
			{
				// Chaos-test knob: hold the mutation in flight on the authority's game thread so a
				// lane can kill the WA mid-execution deterministically. Zero in production traffic.
				if (message.DelayMilliseconds > 0) Thread.Sleep(message.DelayMilliseconds);
				m_accepted = false;
				m_successPublishFailed = false;
				bool result = Execute(message);
				// T-0032: name WHO and WHAT, not just that something was refused. Viktor could
				// not join a faction over the Steam path (2026-09-04) and the 13 refusals in the
				// day's logs were unattributable, because this line carried only the operation
				// and the flag - no player, no faction, no action. A refusal that cannot be tied
				// to a player is not evidence of anything.
				//
				// The three longs are the operation's own arguments. For a StateChange they are
				// fromFaction, toFaction and player, which is exactly the triple needed here; for
				// other operations they carry that operation's ids. StateChange is spelled out
				// because it is the join path and the one under investigation.
				Console.WriteLine("[ClusterPlugin] factions authority operation=" + message.Operation
					+ " result=" + result + " accepted=" + m_accepted
					+ (message.Operation == "StateChange"
						? " action=" + message.StateChange
							+ " fromFaction=" + message.First
							+ " toFaction=" + message.Second
							+ " player=" + message.Third
						: " first=" + message.First + " second=" + message.Second
							+ " third=" + message.Third)
					+ " sender=" + message.Sender);
				if (!m_accepted && message.Operation == "Create") Console.WriteLine(DescribeCreateRefusal(message));
				if (m_accepted)
				{
					try { CaptureSnapshot(publish: m_successPublishFailed); }
					catch (Exception exception)
					{
						Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=factions corrective_snapshot=true error="
							+ SecretRedactor.Redact(exception.Message));
					}
				}
				else if (result && !RequiresAcceptedSuccess(message.Operation)) CaptureSnapshot(publish: true);
				completion.SetResult(JsonSerializer.Serialize(result && (m_accepted || !RequiresAcceptedSuccess(message.Operation))));
			}
			catch (TargetInvocationException exception)
			{
				Exception failure = exception.InnerException ?? exception;
				Console.WriteLine("[ClusterPlugin] factions authority command failed: " + SecretRedactor.Redact(failure.ToString()));
				completion.SetException(failure);
			}
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] factions authority command failed: " + SecretRedactor.Redact(exception.ToString()));
				completion.SetException(exception);
			}
		};
		if (Thread.CurrentThread == game.UpdateThread) execute();
		else game.Invoke(execute, "ClusterPlugin faction authority command");
		return completion.Task.GetAwaiter().GetResult();
	}

	/// <summary>
	/// T-0165: vanilla CreateFactionRequest/CreateFactionServer drop a create without a word. Name
	/// the precondition values they test, so a refused create says which one failed.
	/// </summary>
	private static string DescribeCreateRefusal(FactionAuthorityMessage message)
	{
		MyFactionCollection.AddFactionMsg faction = message.Faction;
		MyFactionCollection factions = MySession.Static.Factions;
		return "[ClusterPlugin] factions create refused founder=" + faction.FounderId
			+ " senderIdentity=" + MySession.Static.Players.TryGetIdentityId(message.Sender)
			+ " local=" + message.IsLocallyInvoked
			+ " npc=" + faction.isNPCFaction
			+ " tag=" + faction.FactionTag + " tagExists=" + (faction.FactionTag != null && factions.FactionTagExists(faction.FactionTag))
			+ " nameLength=" + (faction.FactionName?.Length ?? -1) + " nameExists=" + (faction.FactionName != null && factions.FactionNameExists(faction.FactionName))
			+ " founderFaction=" + (factions.TryGetPlayerFaction(faction.FounderId)?.FactionId ?? 0)
			+ " founderIdentity=" + Sync.Players.HasIdentity(faction.FounderId)
			+ " maxFactions=" + MySession.Static.MaxFactionsCount + " humanFactions=" + factions.HumansCount()
			+ " isServer=" + Sync.IsServer
			+ " iconGroup=" + (faction.FactionIconGroupId?.ToString() ?? "none") + " iconId=" + faction.FactionIconId
			+ " vanillaSenderIdentity=" + VanillaIdentity(message.Sender);
	}

	private static readonly FieldInfo PlayerIdentityIds = typeof(MyPlayerCollection).GetField("m_playerIdentityIds", BindingFlags.NonPublic | BindingFlags.Instance);

	/// <summary>The vanilla map's answer, without the cluster binding prefix (T-0165).</summary>
	private static string VanillaIdentity(ulong sender)
	{
		if (PlayerIdentityIds?.GetValue(MySession.Static.Players) is not System.Collections.IDictionary map) return "unknown";
		object key = new MyPlayer.PlayerId(sender, 0);
		return map.Contains(key) ? map[key].ToString() : "none";
	}

	private static bool Execute(FactionAuthorityMessage message)
	{
		using (MyEventContext.Set(new EndpointId(message.Sender), null, message.IsLocallyInvoked))
		using (message.IsLocallyInvoked ? null : MyPlayerCollectionPatch.ExposeBindingToVanilla(message.Sender))
		{
			switch (message.Operation)
			{
				case "Create": CreateRequest.Invoke(null, new object[] { message.Faction }); return true;
				case "Edit": EditRequest.Invoke(null, new object[] { message.Faction }); return true;
				case "AutoAccept": AutoAcceptRequest.Invoke(null, new object[] { message.First, message.FirstFlag, message.SecondFlag }); return true;
				case "AcceptHumans":
					if (MySession.Static.Factions.TryGetFactionById(message.First) is not MyFaction faction) return false;
					((IMyFaction)faction).AcceptHumans = message.FirstFlag;
					return true;
				case "StateChange": StateChangeRequest.Invoke(null, new object[] { message.StateChange, message.First, message.Second, message.Third }); return true;
				case "AddReputation": return MySession.Static.Factions.AddFactionPlayerReputation(message.First, message.Second,
					message.Number, message.Reason, message.Propagate, message.AdminChange);
				case "DamageReputation":
					MySession.Static.Factions.DamageFactionPlayerReputation(message.First, message.Second,
						(MyReputationDamageType)message.Number, message.Amount);
					return true;
				case "SetFactionReputation": MySession.Static.Factions.SetReputationBetweenFactions(message.First, message.Second, message.Number); return true;
				case "SetPlayerReputation": MySession.Static.Factions.SetReputationBetweenPlayerAndFaction(message.First, message.Second, message.Number, message.Reason); return true;
				default: throw new InvalidOperationException("Invalid faction authority request operation");
			}
		}
	}

	private static void ApplyReplicaPayload(string payload)
	{
		FactionAuthorityMessage message = FactionAuthorityCodec.Read(payload);
		if (message.Operation == "Snapshot")
		{
			ApplySnapshot(message.Snapshot);
			ReapplyQueuedSets();
			return;
		}
		if (!message.Operation.EndsWith("Success", StringComparison.Ordinal))
			throw new InvalidOperationException("Faction replica received a request payload");
		m_applying = true;
		try { MyFactionCollectionSeams.ClusterApplyFactionSuccess(message.Operation, SuccessArguments(message)); }
		finally { m_applying = false; }
		ReapplyQueuedSets();
	}

	private static void ApplySnapshot(string payload)
	{
		MyFactionCollection factions = MySession.Static?.Factions;
		if (factions == null) return;
		var snapshot = FactionAuthorityCodec.ReadSnapshot(payload);
		var before = factions.GetObjectBuilder();
		if (FactionAuthorityCodec.WriteSnapshot(before) == payload) return;
		ReplaceLocally(factions, snapshot);
		int added = 0, removed = 0;
		// #1365: a faction this node holds under another id than the WA's is one it created itself (vanilla allocates
		// faction and founder ids at random in each process); whatever this node made it own lost its owner now.
		var authorityTags = (snapshot.Factions ?? new()).GroupBy(faction => faction.Tag).ToDictionary(group => group.Key, group => group.First());
		foreach (var faction in before.Factions ?? new())
			if (authorityTags.TryGetValue(faction.Tag, out var authority) && authority.FactionId != faction.FactionId)
				Console.WriteLine("[ClusterPlugin] WARNING: factions snapshot replaced_local=true tag=" + faction.Tag
					+ " local=" + faction.FactionId + " localFounder=" + Founder(faction)
					+ " authority=" + authority.FactionId + " authorityFounder=" + Founder(authority)
					+ " (this node created the faction itself; what it owns has no known owner now, #1365)");
		// The corrective snapshot bypassed the per-operation success path, so this node's game
		// clients never heard about factions it added or removed - re-broadcast the diff to them.
		try
		{
			var beforeIds = new HashSet<long>((before.Factions ?? new()).Select(faction => faction.FactionId));
			var afterIds = new HashSet<long>((snapshot.Factions ?? new()).Select(faction => faction.FactionId));
			foreach (var faction in snapshot.Factions ?? new())
				if (!beforeIds.Contains(faction.FactionId)) { MyFactionCollectionSeams.ClusterBroadcastFactionAdded(faction); added++; }
			foreach (var faction in before.Factions ?? new())
				if (!afterIds.Contains(faction.FactionId)) { MyFactionCollectionSeams.ClusterBroadcastFactionRemoved(faction.FactionId); removed++; }
		}
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] faction snapshot client diff broadcast failed error="
				+ SecretRedactor.Redact(exception.Message));
		}
		Console.WriteLine("[ClusterPlugin] factions snapshot_reconciled=true clients_preserved=true"
			+ " clients_added=" + added + " clients_removed=" + removed);
	}

	/// <summary>The faction collection rebuilt from <paramref name="snapshot"/>, outside the authority routing.</summary>
	private static void ReplaceLocally(MyFactionCollection factions, MyObjectBuilder_FactionCollection snapshot)
	{
		m_applying = true;
		try
		{
			MyBankingSystemPatch.ApplyLocally(() =>
			{
				factions.Clear();
				((Dictionary<string, List<MyFaction>>)FactionsByType.GetValue(factions)).Clear();
				factions.Init(snapshot);
			});
		}
		finally { m_applying = false; }
	}

	private static long Founder(MyObjectBuilder_Faction faction) => faction.Members?.FirstOrDefault(member => member.IsFounder).PlayerId ?? 0;

	/// <summary>How long a regular node's load waits for a World Authority global save that holds every default
	/// faction its definitions name: the WA writes one right after it is ready, so this bounds a WA slower than the node.</summary>
	private const int DefaultFactionsWaitSeconds = 180;
	private static List<string> m_localDefaultTags;

	/// <summary>
	/// #1365: a regular node never founds the definitions' NPC factions itself. Vanilla CreateDefaultFactions (session
	/// load, before any session component's BeforeStart) creates every default faction the world lacks, each with a
	/// random faction id and a new founder identity, in every process. A world that does not hold the mods' NPC
	/// factions (the Octahedron template with MES and its packs) so got one set per process; the WA's faction and
	/// identity snapshots then replaced the node's set and dropped its founders, while MES had cached them at
	/// BeforeStart and owned every grid it spawned on the node by an identity the node no longer had. Instead the
	/// node first takes the factions and the NPC identities from the WA's newest catalogued global save: vanilla then
	/// finds every tag and creates nothing, and MES caches the WA's founders. Until the WA has catalogued a save that
	/// holds all of the node's default factions (a fresh cluster, or a mod added since the last save) the load waits,
	/// at most DefaultFactionsWaitSeconds; what is still missing after that is created by vanilla and reported.
	/// </summary>
	public static void DefaultFactionsPrefix(MyFactionCollection factions)
	{
		m_localDefaultTags = null;
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority) return;
		List<string> missing = MissingDefaultTags(factions);
		if (missing.Count == 0) return;
		try { AdoptAuthorityFactions(factions, missing); }
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] WARNING: factions authority_adopt_failed=true error=" + SecretRedactor.Redact(exception.Message));
		}
		m_localDefaultTags = MissingDefaultTags(factions);
	}

	public static void DefaultFactionsPostfix(MyFactionCollection factions)
	{
		List<string> local = m_localDefaultTags;
		m_localDefaultTags = null;
		if (local == null || local.Count == 0) return;
		Console.WriteLine("[ClusterPlugin] WARNING: factions created_locally=true count=" + local.Count + " factions="
			+ string.Join(",", local.Select(tag => tag + ":" + (factions.TryGetFactionByTag(tag) is MyFaction faction
				? faction.FactionId + "/" + faction.FounderId : "none")))
			+ " (the World Authority's global save does not hold them; the WA snapshot will replace them, #1365)");
	}

	private static List<string> MissingDefaultTags(MyFactionCollection factions)
		=> MyDefinitionManager.Static.GetDefaultFactions().Select(definition => definition.Tag)
			.Where(tag => factions.TryGetFactionByTag(tag) == null).Distinct().ToList();

	private static void AdoptAuthorityFactions(MyFactionCollection factions, List<string> missing)
	{
		string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY").TrimEnd('/');
		DateTime started = DateTime.UtcNow;
		GlobalSaveFile.Bundle bundle;
		uint generation, sequence;
		List<string> lacking;
		string error = null;
		bool waiting = false;
		while (true)
		{
			try { bundle = WorldAuthorityCheckpointPatch.ReadNewest(gateway, out generation, out sequence); }
			catch (Exception exception)
			{
				if (exception.Message != error)
					Console.WriteLine("[ClusterPlugin] factions authority_save_unavailable=true error=" + SecretRedactor.Redact(exception.Message));
				error = exception.Message;
				bundle = null;
				generation = sequence = 0;
			}
			if (bundle != null && bundle.Checkpoint.WorldId != MySession.Static.WorldId)
				throw new InvalidOperationException("The World Authority's global save is of another world");
			var held = new HashSet<string>((bundle?.Checkpoint.Factions?.Factions ?? new()).Select(faction => faction.Tag));
			lacking = missing.Where(tag => !held.Contains(tag)).ToList();
			if (bundle != null && lacking.Count == 0 || (DateTime.UtcNow - started).TotalSeconds >= DefaultFactionsWaitSeconds) break;
			if (!waiting)
				Console.WriteLine("[ClusterPlugin] factions waiting_for_authority_save=true missing=" + string.Join(",", lacking)
					+ " saved=" + (bundle == null ? "none" : generation + "/" + sequence) + " wait_limit_s=" + DefaultFactionsWaitSeconds);
			waiting = true;
			Thread.Sleep(2000);
		}
		if (bundle == null) return;
		MyObjectBuilder_Checkpoint checkpoint = bundle.Checkpoint;
		MyPlayerCollection players = MySession.Static.Players;
		var npc = new HashSet<long>(checkpoint.NonPlayerIdentities ?? new());
		int identities = 0;
		foreach (MyObjectBuilder_Identity identity in checkpoint.Identities ?? new())
		{
			if (!npc.Contains(identity.IdentityId) || players.TryGetIdentity(identity.IdentityId) != null) continue;
			players.CreateNewIdentity(identity);
			players.MarkIdentityAsNPC(identity.IdentityId);
			identities++;
		}
		int local = factions.Count();
		ReplaceLocally(factions, checkpoint.Factions ?? new MyObjectBuilder_FactionCollection());
		Console.WriteLine("[ClusterPlugin] factions adopted_from_authority=true generation=" + generation + " sequence=" + sequence
			+ " factions=" + factions.Count() + " local_before=" + local + " npc_identities_added=" + identities
			+ " default_factions_adopted=" + (missing.Count - lacking.Count) + "/" + missing.Count
			+ " waited_s=" + (int)(DateTime.UtcNow - started).TotalSeconds);
	}

	private static void PublishSnapshotIfChanged()
		=> CaptureSnapshot(publish: true);

	private static void CaptureSnapshot(bool publish)
	{
		if (MySession.Static?.Factions == null) return;
		string snapshot = FactionAuthorityCodec.Write(new FactionAuthorityMessage
		{
			Operation = "Snapshot",
			Snapshot = FactionAuthorityCodec.WriteSnapshot(MySession.Static.Factions.GetObjectBuilder())
		});
		lock (SnapshotSync)
		{
			if (m_snapshotReady && m_snapshotGeneration == ClusterRuntime.AuthorityGeneration && m_snapshot == snapshot) return;
			m_snapshotGeneration = ClusterRuntime.AuthorityGeneration;
			m_snapshot = snapshot;
			m_snapshotReady = true;
		}
		if (publish) GlobalStateChannel.Publish(Subsystem, snapshot);
	}

	private static string CurrentSnapshot()
	{
		lock (SnapshotSync) return m_snapshotReady && m_readySession == MySession.Static && MySession.Static?.Ready == true ? m_snapshot : null;
	}
	private static bool RunOriginal() => m_readySession != MySession.Static || m_applying || !ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority;
	private static bool RequiresAcceptedSuccess(string operation) => operation is "Create" or "Edit" or "AutoAccept" or "AcceptHumans" or "StateChange" or "AddReputation";

	private static FactionAuthorityMessage Request(string operation, object[] arguments)
	{
		ulong sender = MyEventContext.Current.Sender.Value;
		bool local = MyEventContext.Current.IsLocallyInvoked;
		return operation switch
		{
			"Create" or "Edit" => new FactionAuthorityMessage { Operation = operation, Sender = sender, IsLocallyInvoked = local, Faction = (MyFactionCollection.AddFactionMsg)arguments[0] },
			"AutoAccept" => new FactionAuthorityMessage { Operation = operation, Sender = sender, IsLocallyInvoked = local, First = (long)arguments[0], FirstFlag = (bool)arguments[1], SecondFlag = (bool)arguments[2] },
			"AcceptHumans" => new FactionAuthorityMessage { Operation = operation, Sender = sender, IsLocallyInvoked = local, First = (long)arguments[0], FirstFlag = (bool)arguments[1] },
			"StateChange" => new FactionAuthorityMessage { Operation = operation, Sender = sender, IsLocallyInvoked = local, StateChange = (MyFactionStateChange)arguments[0], First = (long)arguments[1], Second = (long)arguments[2], Third = (long)arguments[3] },
			"AddReputation" => new FactionAuthorityMessage { Operation = operation, First = (long)arguments[0], Second = (long)arguments[1], Number = (int)arguments[2], Reason = (ReputationChangeReason)arguments[3], Propagate = (bool)arguments[4], AdminChange = (bool)arguments[5] },
			"DamageReputation" => new FactionAuthorityMessage { Operation = operation, First = (long)arguments[0], Second = (long)arguments[1], Number = (int)(MyReputationDamageType)arguments[2], Amount = (float)arguments[3] },
			"SetFactionReputation" => new FactionAuthorityMessage { Operation = operation, First = (long)arguments[0], Second = (long)arguments[1], Number = (int)arguments[2] },
			"SetPlayerReputation" => new FactionAuthorityMessage { Operation = operation, First = (long)arguments[0], Second = (long)arguments[1], Number = (int)arguments[2], Reason = (ReputationChangeReason)arguments[3] },
			_ => throw new ArgumentOutOfRangeException(nameof(operation))
		};
	}

	private static FactionAuthorityMessage Success(string operation, object[] arguments) => operation switch
	{
		"CreateSuccess" => new FactionAuthorityMessage { Operation = operation, Faction = (MyFactionCollection.AddFactionMsg)arguments[0] },
		"EditSuccess" => new FactionAuthorityMessage { Operation = operation, Faction = (MyFactionCollection.AddFactionMsg)arguments[0], First = (long)arguments[1] },
		"AutoAcceptSuccess" => new FactionAuthorityMessage { Operation = operation, First = (long)arguments[0], FirstFlag = (bool)arguments[1], SecondFlag = (bool)arguments[2] },
		"AcceptHumansSuccess" => new FactionAuthorityMessage { Operation = operation, First = (long)arguments[0], FirstFlag = (bool)arguments[1] },
		"StateChangeSuccess" => new FactionAuthorityMessage { Operation = operation, StateChange = (MyFactionStateChange)arguments[0], First = (long)arguments[1], Second = (long)arguments[2], Third = (long)arguments[3], Fourth = (long)arguments[4] },
		"ReputationSuccess" => new FactionAuthorityMessage { Operation = operation, First = (long)arguments[0], ReputationChanges = ((List<MyFactionCollection.MyReputationChangeWrapper>)arguments[1]).ToArray(), Reason = (ReputationChangeReason)arguments[2] },
		_ => throw new ArgumentOutOfRangeException(nameof(operation))
	};

	private static object[] SuccessArguments(FactionAuthorityMessage message) => message.Operation switch
	{
		"CreateSuccess" => new object[] { message.Faction },
		"EditSuccess" => new object[] { message.Faction, message.First },
		"AutoAcceptSuccess" => new object[] { message.First, message.FirstFlag, message.SecondFlag },
		"AcceptHumansSuccess" => new object[] { message.First, message.FirstFlag },
		"StateChangeSuccess" => new object[] { message.StateChange, message.First, message.Second, message.Third, message.Fourth },
		"ReputationSuccess" => new object[] { message.First, new List<MyFactionCollection.MyReputationChangeWrapper>(message.ReputationChanges), message.Reason },
		_ => throw new InvalidOperationException("Invalid faction authority success operation")
	};

	private static MethodInfo RequiredMethod(string name, params Type[] parameters)
		=> typeof(MyFactionCollection).GetMethod(name, PrivateStatic, null, parameters, null)
			?? throw new MissingMethodException(typeof(MyFactionCollection).FullName, name);

	private static void CommandFailed(string operation, Exception exception)
	{
		Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=factions operation=" + operation
			+ " error=" + SecretRedactor.Redact(exception.Message));
		GlobalStateChannel.RequestResync(Subsystem);
	}
}
