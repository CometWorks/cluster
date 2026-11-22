using System;
using System.Collections.Generic;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// <summary>
    /// SE-1525: a store trade the node finished on its own settles its reserve and credit afterwards. A fake WA keeps its
    /// results by operation id, as GlobalCommandLedger does, and books store debt the way the real one does. Covers a
    /// plain settlement, a reserve refused because the money went elsewhere (clawback and debt), a lost answer asked
    /// again, a replay after a restart, and a refused credit. Money is conserved in every case once the debt is repaid.
    /// </summary>
    private static void StorePaymentAsync()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("store-payment-async: " + message);
        }
        const long Payer = 1,
            Payee = 2;
        var balances = new Dictionary<long, long>();
        var debts = new Dictionary<long, long>();
        var ledger = new Dictionary<Guid, BankingAuthorityResult>();
        var refused = new HashSet<long>();
        long version = 0;
        Guid flaky = Guid.Empty;
        int calls = 0;
        BankingAuthorityResult Command(BankingAuthorityRequest request, Guid operation)
        {
            calls++;
            if (!ledger.TryGetValue(operation, out BankingAuthorityResult result))
            {
                long owner = request.OwnerId;
                if (refused.Contains(owner))
                    result = new BankingAuthorityResult(false, version);
                else if (request.Kind == "Collect")
                {
                    long collected = Math.Min(balances[owner], request.Amount);
                    balances[owner] -= collected;
                    debts.TryGetValue(owner, out long owed);
                    debts[owner] = owed + request.Amount - collected;
                    result = new BankingAuthorityResult(
                        true,
                        ++version,
                        collected,
                        request.Amount - collected
                    );
                }
                else if (request.Amount < 0 && balances[owner] + request.Amount < 0)
                    result = new BankingAuthorityResult(false, version);
                else
                {
                    debts.TryGetValue(owner, out long owed);
                    long repaid = request.Amount > 0 ? Math.Min(owed, request.Amount) : 0;
                    debts[owner] = owed - repaid;
                    balances[owner] += request.Amount - repaid;
                    result = new BankingAuthorityResult(true, ++version);
                }
                ledger[operation] = result;
            }
            if (operation == flaky)
            {
                flaky = Guid.Empty;
                throw new TimeoutException("World Authority command timed out");
            }
            return result;
        }
        long Owed(long owner) => debts.TryGetValue(owner, out long owed) ? owed : 0;
        (Guid, Guid, Guid) Ids() => (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        void Reset(long payer)
        {
            balances[Payer] = payer;
            balances[Payee] = 0;
            debts.Clear();
            ledger.Clear();
            refused.Clear();
            calls = 0;
        }
        long Settle((Guid Reserve, Guid Refund, Guid Credit) ids, long amount) =>
            StorePaymentJournal.SettleAsyncNow(
                Command,
                ids.Reserve,
                ids.Refund,
                Payer,
                amount,
                ids.Credit,
                Payee,
                amount
            );

        Reset(1000);
        long settled = Settle(Ids(), 300);
        Check(
            balances[Payer] == 700 && balances[Payee] == 300,
            "a trade settles: debit and credit"
        );
        Check(settled == version && calls == 2, "two legs, and the version of the last one");

        // The payer spent 250 on another node between the trade and its settlement: 50 left of 300.
        Reset(50);
        Settle(Ids(), 300);
        Check(
            balances[Payer] == 0 && balances[Payee] == 300 && Owed(Payer) == 250,
            "a refused reserve is clawed back: 50 collected, 250 owed, the payee paid in full"
        );
        Command(new BankingAuthorityRequest("Change", Payer, 400), Guid.NewGuid());
        Check(
            balances[Payer] == 150 && Owed(Payer) == 0,
            "the next credit repays the debt first: " + balances[Payer]
        );
        Check(
            balances[Payer] + balances[Payee] == 50 + 400,
            "money is conserved once the debt is repaid"
        );

        Reset(1000);
        var lost = Ids();
        flaky = lost.Item1;
        Settle(lost, 300);
        Check(
            balances[Payer] == 700 && balances[Payee] == 300 && calls == 3,
            "a reserve whose answer is lost is asked again with its id and applies once: " + calls
        );

        // A restart replays the journal entry: every leg dedupes to its first answer.
        Settle(lost, 300);
        Check(
            balances[Payer] == 700 && balances[Payee] == 300 && Owed(Payer) == 0,
            "a replay after a restart pays nothing twice"
        );

        Reset(1000);
        refused.Add(Payee);
        Settle(Ids(), 300);
        Check(
            balances[Payer] == 1000 && balances[Payee] == 0,
            "a refused credit is refunded to the payer"
        );
    }
}
