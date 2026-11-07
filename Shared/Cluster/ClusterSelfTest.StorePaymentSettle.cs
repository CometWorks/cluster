using System;
using System.Collections.Generic;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1226: a store payment leg the World Authority answers too late is asked again with its id, never refunded on a
	/// timeout. A fake WA keeps its results by operation id, as GlobalCommandLedger does. A lost answer is a leg the node
	/// sent once that applied while the node heard nothing. Every case ends with the payer's and payee's money adding up.
	/// </summary>
	private static void StorePaymentSettle()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("store-payment-settle: " + message); }
		const long Payer = 1, Payee = 2, Amount = 300;
		var balances = new Dictionary<long, long>();
		var ledger = new Dictionary<Guid, bool>();
		var refused = new HashSet<long>();
		Guid flaky = Guid.Empty;
		int calls = 0;
		bool Change(long owner, long amount, Guid operation)
		{
			calls++;
			if (!ledger.TryGetValue(operation, out bool applied))
			{
				applied = !refused.Contains(owner) && balances[owner] + amount >= 0;
				if (applied) balances[owner] += amount;
				ledger[operation] = applied;
			}
			if (operation == flaky)
			{
				flaky = Guid.Empty;
				throw new TimeoutException("World Authority command timed out");
			}
			return applied;
		}
		// lost: 1 reserve, 2 refund, 3 credit
		(long, long) Settle(long payerStart, bool reserved, int lost, bool commit, bool refuseCredit = false, bool lostAgain = false)
		{
			balances[Payer] = payerStart;
			balances[Payee] = 0;
			ledger.Clear();
			refused.Clear();
			Guid reserve = Guid.NewGuid(), refund = Guid.NewGuid(), credit = Guid.NewGuid();
			if (reserved) Change(Payer, -Amount, reserve);
			if (refuseCredit) refused.Add(Payee);
			if (lost == 1) Change(Payer, -Amount, reserve);
			if (lost == 2) Change(Payer, Amount, refund);
			if (lost == 3) Change(Payee, Amount, credit);
			calls = 0;
			if (lostAgain) flaky = lost == 3 ? credit : lost == 2 ? refund : reserve;
			StorePaymentJournal.SettleNow(Change, reserve, refund, Payer, Amount, reserved,
				commit ? credit : Guid.Empty, commit ? Payee : 0, commit ? Amount : 0);
			Check(balances[Payer] + balances[Payee] == payerStart, "the money adds up: " + balances[Payer] + " + " + balances[Payee] + " of " + payerStart);
			return (balances[Payer], balances[Payee]);
		}

		Check(Settle(1000, true, 3, commit: true) == (700, 300), "a credit applied late is paid once and not refunded (the live case)");
		Check(Settle(1000, true, 0, commit: true) == (700, 300), "a credit is paid");
		Check(Settle(1000, true, 0, commit: true, refuseCredit: true) == (1000, 0), "a refused credit is refunded");
		Check(Settle(1000, false, 1, commit: false) == (1000, 0), "a reserve applied late is refunded once");
		Check(Settle(1000, false, 0, commit: false) == (1000, 0), "a reserve that never arrived applies on the retry and is refunded");
		Check(Settle(100, false, 0, commit: false) == (100, 0), "a reserve refused on the retry needs no refund");
		Check(Settle(1000, true, 2, commit: false) == (1000, 0), "a refund applied late is not paid twice");
		Check(calls == 1, "the late refund is asked once more, with its own id: " + calls + " calls");
		Check(Settle(1000, true, 3, commit: true, lostAgain: true) == (700, 300) && calls == 2,
			"a credit whose answer is lost again is asked until the WA answers: " + calls + " calls");
	}
}
