using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Sandbox.Game.GameSystems.BankingAndCurrency;

namespace ClusterPlugin;

/// <summary>
/// SE-1525, on a regular node: store payments this node approved whose debit its banking replica does not show yet. A
/// store trade checks the payer's replica balance minus these holds and finishes at once; the World Authority settles
/// the debit in the background (<see cref="StorePaymentJournal"/>). A hold ends once the replica has applied the WA
/// snapshot that carries the settlement, so the same money cannot pay twice on this node. Holds and approvals run on
/// the game thread; the settle worker only queues releases.
/// </summary>
internal static class StorePaymentHolds
{
    /// <summary>A settled hold the replica never confirmed is dropped after this long (a lost snapshot, a WA change).</summary>
    private const double StaleMilliseconds = 60000;

    private sealed record Release(long Payer, long Amount, long Version, double QueuedAt);

    private static readonly Dictionary<long, long> Held = new Dictionary<long, long>();
    private static readonly ConcurrentQueue<Release> Settled = new ConcurrentQueue<Release>();
    private static readonly List<Release> Waiting = new List<Release>();

    /// <summary>Game thread: approves and holds <paramref name="amount"/> when the payer's replica balance covers it.</summary>
    public static bool TryHold(long payer, long amount)
    {
        Update();
        if (amount <= 0)
            return true;
        if (MyBankingSystem.Static?.TryGetAccountInfo(payer, out MyAccountInfo account) != true)
            return false;
        Held.TryGetValue(payer, out long held);
        if (account.Balance - held < amount)
            return false;
        Held[payer] = held + amount;
        return true;
    }

    /// <summary>
    /// Any thread: the hold ends once the replica has applied banking version <paramref name="version"/> (0: at once,
    /// when the payment never reached the World Authority).
    /// </summary>
    public static void End(long payer, long amount, long version) =>
        Settled.Enqueue(new Release(payer, amount, version, ClusterRuntime.LocalMilliseconds));

    /// <summary>What this node holds of <paramref name="payer"/>'s balance, for tests.</summary>
    public static long HeldOf(long payer)
    {
        Update();
        return Held.TryGetValue(payer, out long held) ? held : 0;
    }

    /// <summary>Game thread, every frame: ends the holds whose settlement the replica shows.</summary>
    public static void Update()
    {
        while (Settled.TryDequeue(out Release release))
            Waiting.Add(release);
        if (Waiting.Count == 0)
            return;
        long applied = MyBankingSystemPatch.AppliedVersion;
        double now = ClusterRuntime.LocalMilliseconds;
        Waiting.RemoveAll(release =>
        {
            if (release.Version > applied && now - release.QueuedAt < StaleMilliseconds)
                return false;
            if (Held.TryGetValue(release.Payer, out long held))
            {
                if (held <= release.Amount)
                    Held.Remove(release.Payer);
                else
                    Held[release.Payer] = held - release.Amount;
            }
            return true;
        });
    }
}
