using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-1525, on the World Authority: what store payments still owe. A node finishes a store trade against its own replica
/// and settles the payer's debit afterwards; when the payer spent the money on another node in between, the WA takes
/// what is there ("Collect") and books the rest here. The account's next credits through the WA pay it off first, so
/// the money ends up where a refused trade would have left it. Kept in the world's sandbox variables, so a global save
/// and a replacement World Authority keep it. Game thread only, like the banking commands that use it.
/// </summary>
internal static class StoreDebt
{
    private const string Variable = "ClusterStoreDebt";
    private static Dictionary<long, long> m_debts;
    private static MySession m_session;

    /// <summary>Books <paramref name="amount"/> as owed by <paramref name="ownerId"/>.</summary>
    public static void Add(long ownerId, long amount)
    {
        if (amount <= 0)
            return;
        Dictionary<long, long> debts = Load();
        debts.TryGetValue(ownerId, out long owed);
        debts[ownerId] = owed + amount;
        Save();
    }

    /// <summary>How much of a credit of <paramref name="amount"/> goes to the owner's debt instead.</summary>
    public static long Repay(long ownerId, long amount)
    {
        if (amount <= 0)
            return 0;
        Dictionary<long, long> debts = Load();
        if (!debts.TryGetValue(ownerId, out long owed) || owed <= 0)
            return 0;
        long repaid = Math.Min(owed, amount);
        if (owed == repaid)
            debts.Remove(ownerId);
        else
            debts[ownerId] = owed - repaid;
        Save();
        MyLog.Default?.WriteLineAndConsole(
            $"[ClusterPlugin] store debt repaid owner={ownerId} repaid={repaid} left={owed - repaid}"
        );
        return repaid;
    }

    /// <summary>What <paramref name="ownerId"/> owes, for tests and admins.</summary>
    public static long Owed(long ownerId) => Load().TryGetValue(ownerId, out long owed) ? owed : 0;

    private static Dictionary<long, long> Load()
    {
        if (m_debts != null && m_session == MySession.Static)
            return m_debts;
        m_session = MySession.Static;
        m_debts = new Dictionary<long, long>();
        try
        {
            if (
                MyAPIUtilities.Static?.Variables != null
                && MyAPIUtilities.Static.Variables.TryGetValue(Variable, out object saved)
                && saved is string text
                && !string.IsNullOrWhiteSpace(text)
            )
                m_debts =
                    JsonSerializer.Deserialize<Dictionary<long, long>>(text)
                    ?? new Dictionary<long, long>();
        }
        catch (Exception exception)
        {
            MyLog.Default?.WriteLineAndConsole(
                "[ClusterPlugin] ERROR: store debt could not be read: " + exception.Message
            );
        }
        return m_debts;
    }

    private static void Save()
    {
        if (MyAPIUtilities.Static?.Variables != null)
            MyAPIUtilities.Static.Variables[Variable] = JsonSerializer.Serialize(m_debts);
    }
}
