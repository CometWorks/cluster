using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game.ObjectBuilders.Components;
using VRage.Game.ObjectBuilders.Components.Contracts;
using VRage.Library.Utils;
using VRage.ObjectBuilder;
using VRage.ObjectBuilders.Private;

namespace ClusterPlugin;

/// <summary>
/// SE1-0247, WorldAuthority.md §6.3: the World Authority keeps the accepted contracts.
/// <para>
/// Its own <see cref="MySessionComponentContractSystem"/> is the ledger: activation on a regular node hands the
/// contract over (<c>Accept</c>, refused for a contract already accepted or over the player's limit), and the
/// global save keeps it with the rest of the checkpoint, so it outlives the node it was accepted on and a World
/// Authority restart. A terminal transition is admitted only while the World Authority still holds the contract
/// as Active, and admitting it removes it, so it is settled once however many nodes hold a copy.
/// </para>
/// <para>
/// The ids it settled are kept with the global save too (the newest <see cref="SettledIdsKept"/>), and an offer
/// with one of them is refused at acceptance: a partition rolled back to a save from before the acceptance
/// carries the offer again in its block's entry (F30, #1272). The node that asked removes that offer.
/// </para>
/// <para>
/// Every regular node holds a replica of the accepted contracts (published here, about once a second when they
/// change): the player sees them and can finish or abandon one at any contract block, on any node. A replica is
/// a plain copy of the builder; the instance the accepting node activated keeps its subscriptions and is updated
/// in place. Condition and owner changes go back to the World Authority (<c>Update</c>).
/// </para>
/// <para>
/// Money: payouts already go through the banking ledger. The one leg that depends on where the transition runs
/// is a player-made contract's refund on Fail, which vanilla pays to the owner of the start block it finds
/// locally. Away from that block (a timeout at the World Authority, an abandon at another node) the World
/// Authority pays the owner recorded at acceptance instead; a really destroyed block clears that owner, as the
/// vanilla lookup would fail.
/// </para>
/// <para>
/// Mod API callbacks (<c>OnContractAcquired/Failed/Succeeded</c>) are delegates of the mod instance on the node
/// that posted the contract. They are taken off the offer there and fired there, from the published ledger,
/// wherever the transition ran: exactly once, a frame or two after vanilla would. The session component's
/// Custom* events fire where the transition runs.
/// </para>
/// <para>
/// Not covered yet: contract types that watch entities (find, repair, escort, salvage, grid hauling, bounties)
/// are not updated anywhere in a cluster. The World Authority holds no entities and skips their update
/// (<c>ClusterEntityContractUpdates</c>), and regular nodes run no contract updates.
/// </para>
/// </summary>
internal static class ContractLedger
{
	private const string Subsystem = "contracts";
	private const string AlreadySettled = "already settled";
	private const int SettlementsKept = 256;
	private const int SettledIdsKept = 16384;
	private const double PublishCheckMilliseconds = 1000;
	private const double RefreshMilliseconds = 30000;
	private const double AcceptGraceMilliseconds = 30000;
	private const double UnansweredRetryMilliseconds = 15000;

	private static readonly object Sync = new();
	// World Authority: start block, its owner and the accepting player of each accepted contract, from acceptance.
	private static readonly Dictionary<long, Acceptance> Accepted = new();
	// Settlement probes (negative ids, never registered): the first settlement wins.
	private static readonly HashSet<long> ProbesSettled = new();
	private static readonly List<Settlement> Settlements = new();
	// Every role: ids of settled contracts, oldest first. The World Authority's are persisted and refuse a stale
	// offer's acceptance; a regular node's come from the snapshots and keep a stale offer from being adopted.
	private static readonly HashSet<long> SettledIds = new();
	private static readonly Queue<long> SettledOrder = new();
	private static string m_snapshot;
	private static string m_signature;
	private static double m_nextCheck;
	private static double m_nextRefresh;
	// Every role: mod API callbacks of the offers posted on this node.
	private static readonly Dictionary<long, Callbacks> PostedCallbacks = new();
	// Regular node: contracts accepted here that no snapshot listed yet.
	private static readonly Dictionary<long, double> AcceptedHere = new();
	// Regular node: settlements sent from here that got no answer, by contract (#1271). The World Authority may have
	// admitted one, so the contract is kept here and the transition runs again under the same operation, whose
	// stored result the World Authority's command ledger replays.
	private static readonly Dictionary<long, Unanswered> UnansweredSettlements = new();
	private static double m_nextUnansweredRetry;

	internal sealed record Acceptance(long Id, long Block, long BlockOwner, long Player);
	internal sealed record Settlement(long Id, string Kind, long Player);
	private sealed record Request(string Kind, long ContractId, string Contract = null, long Player = 0, long Block = 0,
		long BlockOwner = 0, bool BlockHere = false, bool PunishOwner = true, long FirstOwner = 0, long[] Owners = null,
		bool ConditionFinished = false);
	private sealed record Result(bool Success, string Reason = null);
	private sealed record Snapshot(string Contracts, Settlement[] Settled);
	private sealed record Record(Acceptance[] Accepted, long[] Settled);
	private sealed record Unanswered(string Kind, Guid Operation, string Payload, bool PunishOwner);

	private sealed class Callbacks
	{
		public Action<long> Acquired;
		public Action Failed;
		public Action Succeeded;
		public bool AcquiredFired;
	}

	private static bool RegularNode => ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority;

	private static MySessionComponentContractSystem System => MySession.Static?.GetComponent<MySessionComponentContractSystem>();

	public static void Install()
	{
		MyContractSeams.ClusterAccept = Accept;
		MyContractSeams.ClusterWithdraw = Withdraw;
		MyContractSeams.ClusterTrySettle = TrySettle;
		MyContractSeams.ClusterChanged = Changed;
		MyContractSeams.ClusterPosted = Posted;
		MyContractSeams.ClusterRemoveContractGps = RemoveContractGps;
		MyContractSeams.ClusterEntityContractUpdates = () => !ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority;
		GlobalStateChannel.RegisterCommandHandler(Subsystem, HandleCommand, durable: true);
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, HandleRequest);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, Apply);
		GlobalStateChannel.RegisterSnapshot(Subsystem, CurrentSnapshot, Apply);
	}

	// ---- seams, on the node running the vanilla code (game thread) ----

	private static bool Accept(MyContract contract, long playerId)
	{
		if (!ClusterRuntime.IsClustered || contract.Id <= 0) return true;
		long blockOwner = (MyEntities.GetEntityById(contract.StartBlock) as MyCubeBlock)?.OwnerId ?? 0;
		if (ClusterRuntime.IsWorldAuthority)
		{
			// The World Authority's own activation (a station contract a mod accepts there) is vanilla already.
			lock (Sync) Accepted[contract.Id] = new Acceptance(contract.Id, contract.StartBlock, blockOwner, playerId);
			return true;
		}
		MyObjectBuilder_Contract builder = contract.GetObjectBuilder();
		builder.State = MyContractStateEnum.Active;
		builder.Owners = new MySerializableList<long>(contract.Owners.Append(playerId));
		Result result = Send(new Request("Accept", contract.Id, Serialize(new[] { builder }), playerId, contract.StartBlock, blockOwner));
		if (result?.Success != true)
		{
			Console.WriteLine("[ClusterPlugin] contract_accept_refused id=" + contract.Id + " player=" + playerId + " reason=" + (result?.Reason ?? "no answer"));
			// Unanswered, the World Authority may have taken it all the same.
			if (result == null) Send(new Request("Withdraw", contract.Id));
			// A stale offer: the block's entry stops carrying it at the next save (ContractOffers.Saving).
			else if (result.Reason == AlreadySettled)
			{
				lock (Sync) Remember(contract.Id);
				System?.RemoveInactiveContract(contract.Id);
			}
			return false;
		}
		lock (Sync) AcceptedHere[contract.Id] = ClusterRuntime.LocalMilliseconds;
		return true;
	}

	private static void Withdraw(long contractId)
	{
		if (!ClusterRuntime.IsClustered || contractId <= 0) return;
		if (ClusterRuntime.IsWorldAuthority)
		{
			lock (Sync) Accepted.Remove(contractId);
			return;
		}
		lock (Sync) AcceptedHere.Remove(contractId);
		Send(new Request("Withdraw", contractId));
	}

	private static bool TrySettle(MyContract contract, string kind, bool punishOwner)
	{
		if (!ClusterRuntime.IsClustered) return true;
		var request = new Request(kind, contract.Id, BlockHere: MyEntities.GetEntityById(contract.StartBlock) is MyCubeBlock,
			PunishOwner: punishOwner, FirstOwner: contract.Owners.Count > 0 ? contract.Owners[0] : 0);
		// The World Authority settles its own transitions (the timeout sweep) in place.
		if (ClusterRuntime.IsWorldAuthority) return Settle(request, "wa", ranHere: true).Success;
		Unanswered unanswered;
		lock (Sync) UnansweredSettlements.TryGetValue(contract.Id, out unanswered);
		// An earlier settlement of this contract is still unanswered: only that one may run, under its own operation.
		if (unanswered != null && unanswered.Kind != kind)
		{
			Console.WriteLine("[ClusterPlugin] contract_settle_deferred id=" + contract.Id + " kind=" + kind + " unanswered=" + unanswered.Kind);
			return false;
		}
		bool again = unanswered != null;
		// The same request again (the start block may have moved since), so the World Authority's ledger replays it.
		unanswered ??= new Unanswered(kind, Guid.NewGuid(), JsonSerializer.Serialize(request), punishOwner);
		Result result = Send(request, unanswered.Operation, unanswered.Payload);
		lock (Sync)
		{
			// No authority answer means no settlement here yet: the contract stays Active and nothing is paid twice.
			// The World Authority may have admitted it all the same, so it is asked again until it answers.
			if (result == null) UnansweredSettlements[contract.Id] = unanswered;
			else UnansweredSettlements.Remove(contract.Id);
		}
		if (again && result != null)
			Console.WriteLine("[ClusterPlugin] contract_settle_answered id=" + contract.Id + " kind=" + kind + " success=" + result.Success
				+ (result.Reason == null ? "" : " reason=" + result.Reason));
		return result?.Success == true;
	}

	private static void Changed(MyContract contract)
	{
		if (!RegularNode || contract.Id <= 0 || contract.State != MyContractStateEnum.Active) return;
		GlobalStateChannel.SendRequest(Subsystem, JsonSerializer.Serialize(new Request("Update", contract.Id,
			Owners: contract.Owners.ToArray(), ConditionFinished: contract.ContractCondition?.IsFinished == true)));
	}

	private static void Posted(MySessionComponentContractSystem system, long contractId)
	{
		if (!ClusterRuntime.IsClustered || !system.m_inactiveContracts.TryGetValue(contractId, out MyContract contract)) return;
		if (contract.OnContractAcquired == null && contract.OnContractFailed == null && contract.OnContractSucceeded == null) return;
		lock (Sync)
			PostedCallbacks[contractId] = new Callbacks
			{
				Acquired = contract.OnContractAcquired, Failed = contract.OnContractFailed, Succeeded = contract.OnContractSucceeded
			};
		contract.OnContractAcquired = null;
		contract.OnContractFailed = null;
		contract.OnContractSucceeded = null;
	}

	/// <summary>
	/// Vanilla's RemoveAllContractGpsForPlayer, once over the entries: deletes and modifies only travel to the World
	/// Authority from a regular node, so vanilla's loop until none is left locally would never end. The World
	/// Authority's published GPS table removes them here afterwards.
	/// </summary>
	private static bool RemoveContractGps(MyContract contract, long identityId)
	{
		if (!RegularNode || !MySession.Static.Gpss.m_playerGpss.TryGetValue(identityId, out var entries)) return false;
		foreach (MyGps gps in entries.Values.Where(gps => gps.ContractId == contract.Id).ToArray())
		{
			long other = contract.FindContractNeedingSameGps(identityId, gps.Hash);
			if (other != 0)
			{
				gps.ContractId = other;
				MySession.Static.Gpss.SendModifyGpsRequest(identityId, gps);
			}
			else MySession.Static.Gpss.SendDeleteGpsRequest(identityId, gps.Hash);
		}
		return true;
	}

	/// <summary>A contract block really destroyed (not a cluster close): no refund to its owner any more.</summary>
	internal static void BlockGone(long blockId)
	{
		if (!RegularNode || MySession.Static?.IsUnloading != false) return;
		GlobalStateChannel.SendRequest(Subsystem, JsonSerializer.Serialize(new Request("BlockGone", 0, Block: blockId)));
	}

	/// <summary>
	/// One command, sent at most twice under one operation id. Null when unanswered: a settlement then stays in
	/// <see cref="UnansweredSettlements"/> and is sent again under the same operation until it is answered.
	/// </summary>
	private static Result Send(Request request, Guid operation = default, string payload = null)
	{
		if (operation == Guid.Empty) operation = Guid.NewGuid();
		payload ??= JsonSerializer.Serialize(request);
		for (int attempt = 1; ; attempt++)
		{
			try
			{
				return JsonSerializer.Deserialize<Result>(GlobalStateChannel.SendCommand(Subsystem, payload, operation))
					?? throw new InvalidOperationException("Invalid contract ledger result");
			}
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] contract_ledger_unanswered kind=" + request.Kind + " id=" + request.ContractId
					+ " attempt=" + attempt + " error=" + SecretRedactor.Redact(exception.Message));
				if (attempt == 2) return null;
			}
		}
	}

	// ---- World Authority ----

	private static string HandleCommand(string origin, string payload)
	{
		Request request = JsonSerializer.Deserialize<Request>(payload) ?? throw new InvalidOperationException("Invalid contract ledger request");
		MySandboxGame game = MySandboxGame.Static ?? throw new InvalidOperationException("The World Authority game is unavailable");
		if (Thread.CurrentThread == game.UpdateThread) return JsonSerializer.Serialize(Execute(origin, request));
		var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
		game.Invoke(() =>
		{
			try { completion.SetResult(Execute(origin, request)); }
			catch (Exception exception) { completion.SetException(exception); }
		}, "ClusterPlugin contract ledger");
		return JsonSerializer.Serialize(completion.Task.GetAwaiter().GetResult());
	}

	// Fire-and-forget: Update and BlockGone.
	private static void HandleRequest(string origin, string payload)
	{
		Request request = JsonSerializer.Deserialize<Request>(payload) ?? throw new InvalidOperationException("Invalid contract ledger request");
		MySandboxGame game = MySandboxGame.Static ?? throw new InvalidOperationException("The World Authority game is unavailable");
		game.Invoke(() =>
		{
			try { Execute(origin, request); }
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] contract_ledger_failed kind=" + request.Kind + " id=" + request.ContractId
					+ " origin=" + origin + " error=" + exception.Message);
			}
		}, "ClusterPlugin contract ledger");
	}

	private static Result Execute(string origin, Request request)
	{
		if (request.Kind is not ("Finish" or "Fail") && request.Kind != "BlockGone" && request.ContractId <= 0)
			throw new InvalidOperationException("Invalid contract ledger request");
		Result result = request.Kind switch
		{
			"Accept" => AcceptHere(request),
			"Withdraw" => WithdrawHere(request.ContractId),
			"Update" => UpdateHere(request),
			"Finish" or "Fail" => Settle(request, origin, ranHere: false),
			"BlockGone" => BlockGoneHere(request.Block),
			_ => throw new InvalidOperationException("Invalid contract ledger request " + request.Kind),
		};
		Console.WriteLine("[ClusterPlugin] contract_ledger kind=" + request.Kind + " id=" + request.ContractId + " origin=" + origin
			+ " success=" + result.Success + (result.Reason == null ? "" : " reason=" + result.Reason));
		return result;
	}

	private static Result AcceptHere(Request request)
	{
		MySessionComponentContractSystem system = System ?? throw new InvalidOperationException("No contract system");
		MyObjectBuilder_Contract builder = Parse(request.Contract).SingleOrDefault();
		if (builder == null || builder.Id != request.ContractId || builder.State != MyContractStateEnum.Active || builder.Owners == null || builder.Owners.Count == 0)
			throw new InvalidOperationException("Invalid accepted contract");
		if (system.m_activeContracts.ContainsKey(builder.Id)) return new Result(false, "already accepted");
		lock (Sync)
			if (SettledIds.Contains(builder.Id))
			{
				system.RemoveInactiveContract(builder.Id);
				return new Result(false, AlreadySettled);
			}
		var identities = builder.Owners.Select(owner => MySession.Static.Players.TryGetIdentity(owner)).ToArray();
		if (identities.Any(identity => identity == null)) return new Result(false, "unknown owner");
		if (identities.Any(identity => system.GetContractLimitPerPlayer() <= identity.ActiveContracts.Count)) return new Result(false, "contract limit");
		system.RemoveInactiveContract(builder.Id);   // the offer this World Authority loaded with the world
		system.AddContract(MyContractFactory.CreateInstance(builder));
		foreach (MyIdentity identity in identities)
			if (!identity.ActiveContracts.Contains(builder.Id)) identity.ActiveContracts.Add(builder.Id);
		lock (Sync) Accepted[builder.Id] = new Acceptance(builder.Id, request.Block, request.BlockOwner, request.Player);
		return new Result(true);
	}

	private static Result WithdrawHere(long contractId)
	{
		MySessionComponentContractSystem system = System ?? throw new InvalidOperationException("No contract system");
		if (!system.m_activeContracts.TryGetValue(contractId, out MyContract contract) || contract.State != MyContractStateEnum.Active)
			return new Result(false, "not an active contract");
		Forget(system, contract);
		return new Result(true);
	}

	private static Result UpdateHere(Request request)
	{
		MySessionComponentContractSystem system = System ?? throw new InvalidOperationException("No contract system");
		if (!system.m_activeContracts.TryGetValue(request.ContractId, out MyContract contract) || contract.State != MyContractStateEnum.Active)
			return new Result(false, "not an active contract");
		if (request.Owners != null) SetOwners(contract, request.Owners);
		if (request.ConditionFinished) FinishCondition(contract);
		return new Result(true);
	}

	private static Result BlockGoneHere(long blockId)
	{
		lock (Sync)
			foreach (Acceptance acceptance in Accepted.Values.Where(acceptance => acceptance.Block == blockId && acceptance.BlockOwner != 0).ToArray())
				Accepted[acceptance.Id] = acceptance with { BlockOwner = 0 };
		return new Result(true);
	}

	/// <summary>Admits the terminal transition of an accepted contract once. Game thread of the World Authority.</summary>
	private static Result Settle(Request request, string origin, bool ranHere)
	{
		long id = request.ContractId;
		if (id < 0)
			lock (Sync) return new Result(ProbesSettled.Add(id), "probe");
		MySessionComponentContractSystem system = System ?? throw new InvalidOperationException("No contract system");
		if (!system.m_activeContracts.TryGetValue(id, out MyContract contract) || contract.State != MyContractStateEnum.Active)
			return new Result(false, "not an active contract");
		Acceptance acceptance;
		lock (Sync) Accepted.TryGetValue(id, out acceptance);
		if (request.Kind == "Fail" && contract.IsPlayerMade && !request.BlockHere)
			RefundAway(contract, acceptance, request);
		long player = acceptance?.Player ?? request.FirstOwner;
		// A transition that runs on a regular node finishes there; this ledger only lets go of the contract.
		if (!ranHere) Forget(system, contract);
		else lock (Sync) Accepted.Remove(id);
		lock (Sync)
		{
			Settlements.Add(new Settlement(id, request.Kind, player));
			if (Settlements.Count > SettlementsKept) Settlements.RemoveAt(0);
			Remember(id);
		}
		m_signature = null;
		Console.WriteLine("[ClusterPlugin] contract_settle id=" + id + " kind=" + request.Kind + " origin=" + origin + " admitted=True");
		return new Result(true);
	}

	/// <summary>Vanilla Fail's refund of a player-made contract, paid here when the start block is not where Fail runs.</summary>
	private static void RefundAway(MyContract contract, Acceptance acceptance, Request request)
	{
		long owner = acceptance?.BlockOwner ?? 0;
		if (owner == 0)
		{
			Console.WriteLine("[ClusterPlugin] contract_refund_skipped id=" + contract.Id + " reason="
				+ (acceptance == null ? "not accepted through the ledger" : "start block destroyed"));
			return;
		}
		long deposit = 0;
		if (contract.StartingDeposit > 0)
		{
			if (request.PunishOwner) MyBankingSystem.ChangeBalance(owner, deposit = contract.StartingDeposit);
			else if (request.FirstOwner != 0) MyBankingSystem.ChangeBalance(request.FirstOwner, contract.StartingDeposit);
		}
		if (contract.RewardMoney > 0) MyBankingSystem.ChangeBalance(owner, contract.RewardMoney);
		Console.WriteLine("[ClusterPlugin] contract_refund id=" + contract.Id + " owner=" + owner + " reward=" + contract.RewardMoney
			+ " deposit=" + deposit);
	}

	private static void Forget(MySessionComponentContractSystem system, MyContract contract)
	{
		system.RemoveActiveContract(contract.Id);
		foreach (long owner in contract.Owners)
			MySession.Static.Players.TryGetIdentity(owner)?.ActiveContracts.Remove(contract.Id);
		lock (Sync) Accepted.Remove(contract.Id);
		m_signature = null;
	}

	private static void SetOwners(MyContract contract, IReadOnlyCollection<long> owners)
	{
		if (contract.Owners.SequenceEqual(owners)) return;
		foreach (long left in contract.Owners.Except(owners))
			MySession.Static.Players.TryGetIdentity(left)?.ActiveContracts.Remove(contract.Id);
		foreach (long joined in owners.Except(contract.Owners))
		{
			MyIdentity identity = MySession.Static.Players.TryGetIdentity(joined);
			if (identity != null && !identity.ActiveContracts.Contains(contract.Id)) identity.ActiveContracts.Add(contract.Id);
		}
		contract.Owners.Clear();
		contract.Owners.AddRange(owners);
		m_signature = null;
	}

	private static void FinishCondition(MyContract contract)
	{
		if (contract.ContractCondition is not MyContractCondition condition || condition.IsFinished) return;
		condition.IsFinished = true;
		contract.RecalculateUnfinishedCondiCount();
		m_signature = null;
	}

	/// <summary>
	/// Game thread. World Authority: publishes the ledger when it changed, and every 30 s for the clocks. Regular
	/// node: asks again for its unanswered settlements.
	/// </summary>
	public static void Update()
	{
		if (RegularNode)
		{
			RetryUnanswered();
			return;
		}
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority || MySession.Static?.Ready != true) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (now < m_nextCheck) return;
		m_nextCheck = now + PublishCheckMilliseconds;
		MySessionComponentContractSystem system = System;
		if (system == null) return;
		MyContract[] active = system.m_activeContracts.Values.Where(contract => contract.State == MyContractStateEnum.Active)
			.OrderBy(contract => contract.Id).ToArray();
		Settlement[] settled;
		lock (Sync)
		{
			// Accepted records of contracts vanilla removed on its own (a mod's RemoveContract) are no use any more.
			foreach (long gone in Accepted.Keys.Where(id => !system.m_activeContracts.ContainsKey(id)).ToArray()) Accepted.Remove(gone);
			settled = Settlements.ToArray();
		}
		string signature = string.Join(";", active.Select(contract => contract.Id + ":" + string.Join(",", contract.Owners) + ":"
			+ (contract.ContractCondition?.IsFinished == true))) + "|" + settled.Length + ":" + settled.LastOrDefault()?.Id;
		if (signature == m_signature && (now < m_nextRefresh || !active.Any(contract => contract.IsTimeLimited))) return;
		string snapshot = JsonSerializer.Serialize(new Snapshot(Serialize(active.Select(contract => contract.GetObjectBuilder())), settled));
		lock (Sync) m_snapshot = snapshot;
		m_signature = signature;
		m_nextRefresh = now + RefreshMilliseconds;
		GlobalStateChannel.Publish(Subsystem, snapshot);
		Dispatch(id => active.FirstOrDefault(contract => contract.Id == id)?.Owners.FirstOrDefault(), settled);
	}

	private static string CurrentSnapshot()
	{
		lock (Sync) return m_snapshot;
	}

	internal static string PersistedRecord()
	{
		lock (Sync)
			return JsonSerializer.Serialize(new Record(Accepted.Values.OrderBy(acceptance => acceptance.Id).ToArray(), SettledOrder.ToArray()));
	}

	internal static void RestoreRecord(string payload)
	{
		if (string.IsNullOrEmpty(payload)) return;
		// Before #1272 the record was the accepted array alone.
		Record restored = payload.TrimStart().StartsWith("[", StringComparison.Ordinal)
			? new Record(JsonSerializer.Deserialize<Acceptance[]>(payload), null)
			: JsonSerializer.Deserialize<Record>(payload);
		lock (Sync)
		{
			Accepted.Clear();
			foreach (Acceptance acceptance in restored?.Accepted ?? Array.Empty<Acceptance>()) Accepted[acceptance.Id] = acceptance;
			SettledIds.Clear();
			SettledOrder.Clear();
			foreach (long id in restored?.Settled ?? Array.Empty<long>()) Remember(id);
		}
	}

	/// <summary>The contract was settled (as far as this node knows): an offer of it is stale.</summary>
	internal static bool IsSettled(long contractId)
	{
		lock (Sync) return SettledIds.Contains(contractId);
	}

	// Under Sync.
	private static void Remember(long id)
	{
		if (id <= 0 || !SettledIds.Add(id)) return;
		SettledOrder.Enqueue(id);
		if (SettledOrder.Count > SettledIdsKept) SettledIds.Remove(SettledOrder.Dequeue());
	}

	// ---- regular node ----

	/// <summary>
	/// Runs the transition of each unanswered settlement again, every 15 s until the World Authority answers: its
	/// TrySettle reuses the operation, so an admitted settlement is paid here once and a refused one lets go (#1271).
	/// </summary>
	private static void RetryUnanswered()
	{
		if (MySession.Static?.Ready != true) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (now < m_nextUnansweredRetry) return;
		m_nextUnansweredRetry = now + UnansweredRetryMilliseconds;
		KeyValuePair<long, Unanswered>[] pending;
		lock (Sync)
		{
			if (UnansweredSettlements.Count == 0) return;
			pending = UnansweredSettlements.ToArray();
		}
		MySessionComponentContractSystem system = System;
		if (system == null) return;
		foreach ((long id, Unanswered unanswered) in pending)
		{
			if (!system.m_activeContracts.TryGetValue(id, out MyContract contract) || contract.State != MyContractStateEnum.Active)
			{
				lock (Sync) UnansweredSettlements.Remove(id);
				continue;
			}
			Console.WriteLine("[ClusterPlugin] contract_settle_retry id=" + id + " kind=" + unanswered.Kind);
			try
			{
				if (unanswered.Kind == "Finish")
				{
					MyContractResults result = contract.Finish();
					// Finish checks its conditions before it asks: one that no longer holds never reaches TrySettle.
					if (result == MyContractResults.Fail_FinishConditionsNotMet)
					{
						lock (Sync) UnansweredSettlements.Remove(id);
						Console.WriteLine("[ClusterPlugin] contract_settle_retry_dropped id=" + id + " result=" + result);
					}
				}
				else contract.Fail(punishOwner: unanswered.PunishOwner);
			}
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] contract_settle_retry_failed id=" + id + " error=" + exception);
			}
		}
	}

	/// <summary>Applies the published ledger to this node's contract system and identities. Game thread.</summary>
	private static void Apply(string payload)
	{
		if (!RegularNode || MySession.Static?.Ready != true) return;
		MySessionComponentContractSystem system = System;
		if (system == null) return;
		Snapshot snapshot = JsonSerializer.Deserialize<Snapshot>(payload) ?? throw new InvalidDataException("Invalid contract ledger snapshot");
		foreach (Settlement settlement in snapshot.Settled ?? Array.Empty<Settlement>())
		{
			lock (Sync) Remember(settlement.Id);
			// An offer of a settled contract is stale (adopted from an old block entry before this node knew).
			if (!system.m_inactiveContracts.ContainsKey(settlement.Id)) continue;
			system.RemoveInactiveContract(settlement.Id);
			Console.WriteLine("[ClusterPlugin] contract offer withdrawn, already settled: contract=" + settlement.Id);
		}
		Dictionary<long, MyObjectBuilder_Contract> wanted = Parse(snapshot.Contracts)
			.Where(builder => builder.Id > 0 && builder.State == MyContractStateEnum.Active).ToDictionary(builder => builder.Id);
		double now = ClusterRuntime.LocalMilliseconds;
		int added = 0, removed = 0;
		lock (Sync)
			foreach (long id in AcceptedHere.Keys.Where(id => wanted.ContainsKey(id) || now - AcceptedHere[id] > AcceptGraceMilliseconds).ToArray())
				AcceptedHere.Remove(id);
		foreach (long id in system.m_activeContracts.Keys.Where(id => id > 0 && !wanted.ContainsKey(id)).ToArray())
		{
			// Accepted here a moment ago: this snapshot was published before the World Authority took it. Or settled
			// here without an answer: the World Authority may have admitted it, and RetryUnanswered pays it here.
			lock (Sync) if (AcceptedHere.ContainsKey(id) || UnansweredSettlements.ContainsKey(id)) continue;
			system.RemoveActiveContract(id);
			removed++;
		}
		foreach (MyObjectBuilder_Contract builder in wanted.Values)
		{
			if (system.m_activeContracts.TryGetValue(builder.Id, out MyContract local))
			{
				// Settled here, and this snapshot was published before that: the next one drops it.
				if (local.State == MyContractStateEnum.Active) Refresh(local, builder);
				continue;
			}
			system.RemoveInactiveContract(builder.Id);   // the offer, where the block was before
			system.AddContract(MyContractFactory.CreateInstance(builder));
			added++;
		}
		// Vanilla drains these in the update this node does not run.
		system.m_pendingContractChanges.Clear();
		SyncIdentities(wanted.Values);
		if (added + removed > 0)
			Console.WriteLine("[ClusterPlugin] contract_ledger_applied active=" + wanted.Count + " added=" + added + " removed=" + removed);
		Dispatch(id => wanted.TryGetValue(id, out MyObjectBuilder_Contract builder) ? builder.Owners.FirstOrDefault() : (long?)null,
			snapshot.Settled ?? Array.Empty<Settlement>());
	}

	/// <summary>Brings a local Active instance to the ledger's state without replacing it (it may hold subscriptions).</summary>
	private static void Refresh(MyContract local, MyObjectBuilder_Contract builder)
	{
		if (!local.Owners.SequenceEqual(builder.Owners))
		{
			local.Owners.Clear();
			local.Owners.AddRange(builder.Owners);
		}
		if (local.ContractCondition is MyContractCondition condition && builder.ContractCondition != null
			&& condition.IsFinished != builder.ContractCondition.IsFinished)
		{
			// A condition finished here stays finished; the World Authority missed the update, so send it again.
			if (condition.IsFinished) Changed(local);
			else
			{
				condition.IsFinished = true;
				local.RecalculateUnfinishedCondiCount();
			}
		}
		if (builder.RemainingTimeInS.HasValue) local.RemainingTime = MyTimeSpan.FromSeconds(builder.RemainingTimeInS.Value);
	}

	private static void SyncIdentities(IEnumerable<MyObjectBuilder_Contract> active)
	{
		var owned = new Dictionary<long, List<long>>();
		foreach (MyObjectBuilder_Contract builder in active.OrderBy(builder => builder.Id))
			foreach (long owner in builder.Owners)
			{
				if (!owned.TryGetValue(owner, out List<long> ids)) owned[owner] = ids = new List<long>();
				ids.Add(builder.Id);
			}
		foreach (MyIdentity identity in MySession.Static.Players.GetAllIdentities())
		{
			List<long> ids = owned.TryGetValue(identity.IdentityId, out List<long> list) ? list : new List<long>();
			// Settlement probes keep their own (negative) ids out of the ledger; a contract accepted here a moment
			// ago is not in this snapshot yet, and one settled here without an answer may have left it already.
			lock (Sync)
				ids.AddRange(identity.ActiveContracts.Where(id => id < 0
					|| ((AcceptedHere.ContainsKey(id) || UnansweredSettlements.ContainsKey(id)) && !ids.Contains(id))));
			if (identity.ActiveContracts.OrderBy(id => id).SequenceEqual(ids.OrderBy(id => id))) continue;
			identity.ActiveContracts.Clear();
			identity.ActiveContracts.AddRange(ids);
		}
	}

	// ---- every role: mod API callbacks of offers posted here ----

	private static void Dispatch(Func<long, long?> activeOwner, IReadOnlyCollection<Settlement> settled)
	{
		var calls = new List<(long Id, string What, Action Call)>();
		lock (Sync)
		{
			if (PostedCallbacks.Count == 0) return;
			foreach (KeyValuePair<long, Callbacks> entry in PostedCallbacks.ToArray())
			{
				long? owner = activeOwner(entry.Key);
				Settlement settlement = settled.LastOrDefault(item => item.Id == entry.Key);
				Callbacks callbacks = entry.Value;
				if (!callbacks.AcquiredFired && (owner != null || settlement != null))
				{
					callbacks.AcquiredFired = true;
					long player = owner ?? settlement.Player;
					if (callbacks.Acquired != null) calls.Add((entry.Key, "acquired", () => callbacks.Acquired(player)));
				}
				if (settlement == null) continue;
				PostedCallbacks.Remove(entry.Key);
				Action done = settlement.Kind == "Finish" ? callbacks.Succeeded : callbacks.Failed;
				if (done != null) calls.Add((entry.Key, settlement.Kind == "Finish" ? "succeeded" : "failed", done));
			}
		}
		foreach ((long id, string what, Action call) in calls)
		{
			Console.WriteLine("[ClusterPlugin] contract_callback id=" + id + " callback=" + what);
			try { call(); }
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] contract_callback_failed id=" + id + " callback=" + what + " error=" + exception);
			}
		}
	}

	// ---- builders ----

	private static string Serialize(IEnumerable<MyObjectBuilder_Contract> contracts)
	{
		var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SessionComponentContractSystem>();
		builder.ActiveContracts = new MySerializableList<MyObjectBuilder_Contract>(contracts);
		using var stream = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, builder))
			throw new InvalidDataException("Accepted contracts did not serialize");
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static List<MyObjectBuilder_Contract> Parse(string value)
	{
		if (string.IsNullOrEmpty(value)) return new List<MyObjectBuilder_Contract>();
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value), writable: false);
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(stream, out MyObjectBuilder_SessionComponentContractSystem builder))
			throw new InvalidDataException("Unreadable accepted contracts");
		return builder.ActiveContracts?.ToList() ?? new List<MyObjectBuilder_Contract>();
	}
}
