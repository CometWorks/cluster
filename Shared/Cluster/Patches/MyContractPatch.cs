using System;

namespace ClusterPlugin;

/// <summary>
/// Contract activation charges a starting deposit after only <em>reading</em> the balance, so two regular nodes
/// can each accept a contract the player can afford once. This reuses the store payment scope to reserve the
/// deposit at the World Authority before any contract state changes, which turns the check-then-act into a
/// single authoritative decision. The rest of the accepted contract's life, its terminal-state arbitration
/// included, is <see cref="ContractLedger"/>'s. (Offers posted at a contract block travel with the block:
/// <see cref="ContractOffers"/>.)
/// </summary>
public static class MyContractPatch
{
	public static void Install()
	{
		MyContractSeams.ClusterBeginDeposit = SeamBeginDeposit;
		MyContractSeams.ClusterCommitDeposit = SeamCommitDeposit;
		ContractLedger.Install();
	}

	private static (IDisposable Scope, bool Approved) SeamBeginDeposit(long payerId, long amount)
	{
		MyStoreBlockPatch.StorePaymentScope deposit = MyStoreBlockPatch.BeginPayment(payerId, amount);
		return (deposit, deposit.Approved);
	}

	private static void SeamCommitDeposit(IDisposable scope) => ((MyStoreBlockPatch.StorePaymentScope)scope).Commit();
}
