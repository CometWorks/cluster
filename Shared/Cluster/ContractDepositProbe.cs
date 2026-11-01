using System;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Contracts;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilder;
using VRage.Game.ObjectBuilders.Components.BankingAndCurrency;
using VRage.Game.ObjectBuilders.Components.Contracts;
using VRage.Library.Utils;

namespace ClusterPlugin;

/// <summary>
/// Drives a real <see cref="MyContract.Activate"/> so the World Authority deposit seam is exercised end to end.
/// Vanilla checks the payer's balance in CanActivate_Internal and then debits it in Activate_Internal without
/// re-checking, so in a cluster two nodes can both pass the check and both debit. Calling the payment scope
/// directly would only prove the scope works; only a real contract proves the seam is wired into Activate.
/// </summary>
public sealed class ContractDepositProbe : MyContract
{
	private static long m_probeId;

	// Activation is the subject of the probe, so never let vanilla's own preconditions decide the outcome:
	// a refusal here would be indistinguishable from the World Authority refusing the reservation.
	protected override MyActivationResults CanActivate_Internal(long playerId) => MyActivationResults.Success;

	// Same reasoning for settlement: the probe has no condition, and vanilla's finish precondition must not be
	// mistaken for the cluster's terminal-state arbitration refusing the second settler.
	public override bool CanBeFinished_Internal() => true;

	public static object Activate(long payerId, long deposit)
	{
		long before = Balance(payerId);
		ContractDepositProbe contract = Create(payerId, deposit);
		bool activated = contract.Activate(payerId, MyTimeSpan.FromTicks(MySession.Static.ElapsedGameTime.Ticks));
		long after = Balance(payerId);
		// Leave no trace in the identity's contract list: the probe's contract has no condition to finish.
		MySession.Static.Players.TryGetIdentity(payerId)?.ActiveContracts.Remove(contract.Id);
		return new
		{
			payerId,
			deposit,
			activated,
			balanceBefore = before,
			balanceAfter = after,
			debited = before - after,
			state = contract.State.ToString()
		};
	}

	/// <summary>
	/// Drives a real terminal transition - <see cref="MyContract.Finish"/> or <see cref="MyContract.Fail"/> - on
	/// a contract copy with a caller-chosen id, so two nodes can race the same contract and the lane can assert
	/// the World Authority admits exactly one settlement. The winner's deposit return is the observable money
	/// movement; the loser must move nothing and stay Active.
	/// </summary>
	public static object Settle(long contractId, long payerId, long deposit, string kind)
	{
		long before = Balance(payerId);
		ContractDepositProbe contract = Create(payerId, deposit, contractId, MyContractStateEnum.Active);
		string outcome;
		if (kind == "Finish") outcome = contract.Finish().ToString();
		else
		{
			contract.Fail(abandon: false, punishOwner: false);
			outcome = contract.State.ToString();
		}
		MySession.Static.Players.TryGetIdentity(payerId)?.ActiveContracts.Remove(contract.Id);
		long after = Balance(payerId);
		return new
		{
			contractId,
			payerId,
			kind,
			outcome,
			state = contract.State.ToString(),
			settled = contract.State is MyContractStateEnum.Finished or MyContractStateEnum.Failed,
			balanceBefore = before,
			balanceAfter = after,
			credited = after - before
		};
	}

	/// <summary>
	/// Drives a real Finish over a contract with TWO owners and a money reward, so the vanilla
	/// per-owner payout loop runs under the cluster's exactly-once settlement arbitration. The owners
	/// are real replicated identities that may be hosted on different nodes; each must receive exactly
	/// reward/ownerCount through the WA-routed ChangeBalance. A replayed settlement of the same id is
	/// refused by the arbitration and must move nothing.
	/// </summary>
	public static object SettleSplit(long contractId, long ownerA, long ownerB, int reward)
	{
		long beforeA = Balance(ownerA);
		long beforeB = Balance(ownerB);
		ContractDepositProbe contract = Create(ownerA, 0, contractId, MyContractStateEnum.Active, ownerB, reward);
		string outcome = contract.Finish().ToString();
		MySession.Static.Players.TryGetIdentity(ownerA)?.ActiveContracts.Remove(contract.Id);
		MySession.Static.Players.TryGetIdentity(ownerB)?.ActiveContracts.Remove(contract.Id);
		return new
		{
			contractId,
			ownerA,
			ownerB,
			reward,
			outcome,
			state = contract.State.ToString(),
			settled = contract.State == MyContractStateEnum.Finished,
			creditedA = Balance(ownerA) - beforeA,
			creditedB = Balance(ownerB) - beforeB
		};
	}

	private static ContractDepositProbe Create(long payerId, long deposit)
		=> Create(payerId, deposit, System.Threading.Interlocked.Decrement(ref m_probeId), MyContractStateEnum.Inactive);

	private static ContractDepositProbe Create(long payerId, long deposit, long contractId, MyContractStateEnum state,
		long coOwner = 0, int reward = 0)
	{
		// MyContract.Init dereferences the resolved contract type definition, so a real one must be named. Any
		// of them will do: the probe overrides the only behavior the definition would otherwise decide.
		MyDefinitionId definitionId = MyDefinitionManager.Static.GetContractTypeDefinitions().Select(entry => entry.Key)
			.OrderBy(id => id.SubtypeName, StringComparer.Ordinal).FirstOrDefault();
		if (definitionId == default) throw new InvalidOperationException("No contract type definition is loaded");
		var contract = new ContractDepositProbe();
		var owners = new MySerializableList<long>();
		// An Active probe needs an owner: FinishFor_Internal returns the deposit to each owner, which is the
		// settlement the race asserts on.
		if (state == MyContractStateEnum.Active) owners.Add(payerId);
		if (coOwner != 0) owners.Add(coOwner);
		contract.Init(new MyObjectBuilder_ContractCustom
		{
			// Probe ids are negative and never registered with the contract system, so they cannot collide
			// with, or be mistaken for, a real contract. Settlement callers pass their own (negative) id so two
			// nodes can name the same contract.
			Id = contractId,
			IsPlayerMade = false,
			State = state,
			Owners = owners,
			StartingDeposit = deposit,
			RewardMoney = reward,
			ContractTypeDefinitionId = definitionId,
			DefinitionId = definitionId,
			Creation = MySession.Static.ElapsedGameTime.Ticks
		});
		return contract;
	}

	private static long Balance(long ownerId)
		=> MyBankingSystem.Static.TryGetAccountInfo(ownerId, out MyAccountInfo account) ? account.Balance : 0;
}
