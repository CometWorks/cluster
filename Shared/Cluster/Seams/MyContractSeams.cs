using System;
using Sandbox.Game.Contracts;
using Sandbox.Game.SessionComponents;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="Sandbox.Game.Contracts.MyContract"/>. Harmony side: Shared/Patches/MyContractPatch.cs.
/// </summary>
public static class MyContractSeams
{
	// Activate: reserve the starting deposit at the World Authority before any contract state changes.
	public static Func<long, long, (IDisposable Scope, bool Approved)> ClusterBeginDeposit;
	public static Action<IDisposable> ClusterCommitDeposit;

	// Activate, after the deposit: the World Authority takes the contract into its ledger of accepted contracts
	// (contract, accepting player). False refuses the activation. Withdraw (contract id) undoes it when Activate
	// does not get as far as the commit.
	public static Func<MyContract, long, bool> ClusterAccept;
	public static Action<long> ClusterWithdraw;

	// Finish/Fail: every node holding a copy of a contract could reach a terminal state on its own - a player
	// finishing here while the World Authority's timeout fails it there - and each would settle the reward and
	// deposit again. The terminal transition is arbitrated once, cluster-wide: (contract, "Finish"|"Fail",
	// Fail's punishOwner).
	public static Func<MyContract, string, bool, bool> ClusterTrySettle;

	// An accepted contract changed without a terminal transition: its condition finished, an owner joined or left.
	public static Action<MyContract> ClusterChanged;

	// Update of the contract types that watch entities (an override of MyContract.Update): false skips it.
	public static Func<bool> ClusterEntityContractUpdates;

	// RemoveAllContractGpsForPlayer (contract, identity): true when the seam removed them, false runs vanilla.
	public static Func<MyContract, long, bool> ClusterRemoveContractGps;

	// MySessionComponentContractSystem.AddContract(IMyContract) posted an offer: (system, contract id).
	public static Action<MySessionComponentContractSystem, long> ClusterPosted;
}
