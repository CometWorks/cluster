using System;
using Sandbox.Game.GameSystems.BankingAndCurrency;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyBankingSystem"/>.
/// The Harmony side lives in Shared/Patches/MyBankingSystemPatch.cs; every patch is inert while the
/// delegate it calls is null.
/// </summary>
public static class MyBankingSystemSeams
{
	// ClusterPlugin seam adapters for MyBankingSystemPatch prefixes.
	public static Func<MyBankingSystem, long, long, bool> ClusterCreateAccount;
	public static Func<MyBankingSystem, long, bool?> ClusterRemoveAccount;
	public static Func<long, long, bool?> ClusterChangeBalance;

	// ClusterPlugin seam: on a regular cluster node both balances are authoritative at the World Authority,
	// so the debit-credit pair of Transfer_Internal must settle there atomically - a local mutation would be
	// overwritten by the next replicated snapshot. Validation stays local: it reads players, factions and
	// the RPC sender, which only exist on this node.
	public static Func<long, long, long, bool?> ClusterTransfer;
}
