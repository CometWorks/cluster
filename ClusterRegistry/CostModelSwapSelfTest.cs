using System.Net;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// #1278 (T-0296 round 5, F35): two POST /balance/cost-model overrides of different fields race on the thread
    /// pool. Each one must land on top of the other: a field set earlier never reverts to an older value. Each thread
    /// owns one field and counts it up, so before each write its field must still hold the value it wrote last;
    /// the unfenced read-modify-write (before = _costModel; _costModel = WithOverrides(before, ...)) fails this
    /// within a few hundred rounds.
    /// </summary>
    private static void TestCostModelOverrideRace()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("cost-model-override-race: " + what); }
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()), null,
            false, null, null, null, null, TimeSpan.FromSeconds(1), null, null, []);
        using var gateway = new DirectGateway(options);
        CostModel initial = gateway.CurrentCostModel;
        const int Rounds = 20_000;
        string?[] lost = new string?[2];
        using var start = new Barrier(2);
        Thread Writer(int slot, string field, Func<CostModel, double> read) => new(() =>
        {
            start.SignalAndWait();
            for (int i = 1; i <= Rounds && lost[slot] == null; i++)
            {
                double held = read(gateway.CurrentCostModel);
                if (i > 1 && held != i - 1) lost[slot] = $"{field} wrote {i - 1}, reads {held} before round {i}";
                gateway.OverrideCostModel([KeyValuePair.Create(field, i.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            }
        }) { IsBackground = true, Name = "CostModelOverride-" + field };
        Thread[] writers = [Writer(0, "HitchMsPerMove", m => m.HitchMsPerMove), Writer(1, "BudgetMovesPerNodeHour", m => m.BudgetMovesPerNodeHour)];
        foreach (Thread writer in writers) writer.Start();
        foreach (Thread writer in writers) Check(writer.Join(TimeSpan.FromSeconds(120)), writer.Name + " did not finish");
        Check(lost[0] == null && lost[1] == null, "an override lost the other's field: " + string.Join("; ", lost.Where(x => x != null)));
        CostModel final = gateway.CurrentCostModel;
        Check(final.HitchMsPerMove == Rounds && final.BudgetMovesPerNodeHour == Rounds,
            $"both last overrides hold: HitchMsPerMove {final.HitchMsPerMove}, BudgetMovesPerNodeHour {final.BudgetMovesPerNodeHour}");
        Check(final with { HitchMsPerMove = initial.HitchMsPerMove, BudgetMovesPerNodeHour = initial.BudgetMovesPerNodeHour } == initial,
            "no other field changed");
    }
}
