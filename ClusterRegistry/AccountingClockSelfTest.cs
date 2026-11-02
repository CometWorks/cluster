using System.Text.Json;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    private static void TestAccountingClock()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-accounting-clock-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(400);
        var registry = new GatewayRegistry(root, Admission, () => now, TimeSpan.FromSeconds(10));
        int checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        void Refuses(Action action)
        {
            try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { checks++; return; }
            throw new Exception("Invalid clock/history was admitted");
        }
        try
        {
            var wa = registry.RegisterNode(Join("wa", "wa", Guid.NewGuid()) with { Role = NodeRole.WA });
            var node = registry.RegisterNode(Join("node", "node", Guid.NewGuid()));
            string epoch = Guid.NewGuid().ToString("D");
            var first = new AccountingClockSample("world", epoch, 100, wa.AuthorityGeneration, 20000000, 0, new() { [0] = 0, [50] = 10000000 });
            var accepted = registry.AccountingClock(wa.Node, wa.Epoch, first)!;
            Check(accepted.Frame == 100 && accepted.SunRuns.Count == 0, "only durable progress is published; history remains in its owner");
            Refuses(() => registry.AccountingClock(node.Node, node.Epoch, first));
            Refuses(() => registry.AccountingClock(wa.Node, wa.Epoch, first with { Epoch = Guid.NewGuid().ToString("D") }));
            Refuses(() => registry.AccountingClock(wa.Node, wa.Epoch, first with { Frame = 99 }));
            Refuses(() => registry.AccountingClock(wa.Node, wa.Epoch, first with { SunRuns = new() { [50] = 123 } }));
            Refuses(() => registry.AccountingClock(wa.Node, wa.Epoch, accepted with { Frame = 200, HistoryFrom = 101 }));
            string history = JsonSerializer.Serialize(registry.AccountingSun(epoch, 25, 75));
            registry.Dispose();
            registry = new GatewayRegistry(root, Admission, () => now, TimeSpan.FromSeconds(10));
            Check(JsonSerializer.Serialize(registry.AccountingSun(epoch, 25, 75)) == history, "clock and ordered sun path share the durable SQLite transaction/restart");
            now += TimeSpan.FromDays(10);
            Check(registry.AccountingClock("reader", 0, null)!.Frame == 100, "downtime/wall passage grants zero native work");
            Check(JsonSerializer.Serialize(registry.AccountingSun(epoch, 25, 75)) == history, "old first capped interval is not replaced by a rolling history window");
            Refuses(() => registry.AccountingSun(Guid.NewGuid().ToString("D"), 0, 100));
            Refuses(() => registry.AccountingSun(epoch, 0, 101));
            registry.ExpireLeases();
            var replacement = registry.RegisterNode(Join("wa-2", "wa-2", Guid.NewGuid()) with { Role = NodeRole.WA });
            var next = accepted with { Frame = 110, Generation = replacement.AuthorityGeneration, HistoryFrom = 100, SunRuns = new() { [100] = 50000000 } };
            var recovered = registry.AccountingClock(replacement.Node, replacement.Epoch, next)!;
            Check(recovered.Frame == 110 && recovered.Epoch == epoch, "new WA generation advances the same explicitly recovered accounting epoch");
            Refuses(() => registry.AccountingClock(wa.Node, wa.Epoch, next));
            Check(JsonSerializer.Serialize(registry.AccountingSun(epoch, 25, 75)) == history, "WA replacement does not overwrite unpaid history");
            Console.WriteLine($"PASS {checks} owning durable accounting-clock/history checks");
        }
        finally { registry.Dispose(); Directory.Delete(root, true); }
    }
}
