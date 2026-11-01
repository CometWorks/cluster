using ClusterProtocol;

namespace ClusterRegistry;

/// <summary>T-0323 (ReplicaPacing.cs): the registry paces every save copy with one byte budget, and says so when the
/// copies do not keep up with what the recycles return to the queue. Earned reds: without the global budget the whole
/// backlog is handed out with no time passing; a backlog that drains, however large, raises nothing.</summary>
internal static partial class SelfTest
{
    /// <summary><paramref name="count"/> new partitions of <paramref name="node"/>, each with one single-copy save.</summary>
    private static List<SaveId> SingleCopySaves(NodeControlFixture f, string node, int count)
    {
        var saves = new List<SaveId>();
        for (int i = 0; i < count; i++)
        {
            NodeRegistration row = f.Row(node);
            ulong partition = f.Registry.AllocatePartitionIds(row.Node, row.Epoch, 1)[0];
            f.Registry.ClaimPartition(partition, row.Node, row.Epoch, null);
            saves.Add(SaveOn(f, node, partition, 1));
        }
        return saves;
    }

    /// <summary>A node's replica pump with no time passing: polls until nothing is handed out or <paramref name="limit"/> copies are made.</summary>
    private static List<SaveId> PumpReplicas(NodeControlFixture f, string node, int limit = int.MaxValue)
    {
        var copied = new List<SaveId>();
        while (copied.Count < limit && f.Registry.ReplicaWork(node, f.Row(node).Epoch).SingleOrDefault(x => !x.Global) is { } work)
        {
            CopyTo(f, work.SaveId, node);
            copied.Add(work.SaveId);
        }
        return copied;
    }

    private static void TestReplicaBudget()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("replica-budget: " + what); }
        int all = GatewayRegistry.ReplicaBytesPerSecond, evacuate = GatewayRegistry.EvacuateBytesPerSecond;
        using var unpaced = new NodeControlFixture("replica-budget-red");
        using var f = new NodeControlFixture("replica-budget");
        try
        {
            GatewayRegistry.EvacuateBytesPerSecond = 1 << 30;
            // Earned red: with no budget to speak of one node is handed the whole backlog without a second passing.
            GatewayRegistry.ReplicaBytesPerSecond = 1 << 30;
            unpaced.Apply(("polaris", 2, 0));
            BootServing(unpaced, "node-1"); BootServing(unpaced, "node-2");
            List<SaveId> unpacedSaves = SingleCopySaves(unpaced, "node-1", 30);
            int[] ranked = [.. PumpReplicas(unpaced, "node-2").Select(id => unpacedSaves.IndexOf(id))];
            Check(ranked.Length == 30, $"RED expected: unpaced, all 30 copies go out at once: {ranked.Length}");

            GatewayRegistry.ReplicaBytesPerSecond = 20_000;   // one 20 KB test save per second, 10 s burst
            f.Apply(("polaris", 2, 0));
            BootServing(f, "node-1"); BootServing(f, "node-2");
            List<SaveId> saves = SingleCopySaves(f, "node-1", 30);
            int[] burst = [.. PumpReplicas(f, "node-2").Select(id => saves.IndexOf(id))];
            Check(burst.Length is >= 9 and <= 11, $"a 10 s burst of the budget and no more without time passing: {burst.Length} copies");
            Check(burst.SequenceEqual(ranked.Take(burst.Length)), "the budget hands out the queue's own order: " + string.Join(",", burst));
            Check(f.Registry.ReplicaWork("node-2", f.Row("node-2").Epoch).Length == 0, "with the budget spent no directive is handed out");
            // The budget refills with the clock, and the best-ranked save takes it: a newer exposed save, not the backlog.
            f.Advance(1);
            SaveId fresh = SingleCopySaves(f, "node-1", 1)[0];
            List<SaveId> next = PumpReplicas(f, "node-2");
            Check(next.Count == 1 && next[0] == fresh, $"one second refills one copy, and it is the freshest save: {next.Count} copies");
            f.Advance(5);
            int rate = PumpReplicas(f, "node-2").Count;
            Check(rate is >= 4 and <= 6, $"then the rate: {rate} copies in 5 s at 20 KB/s of 20 KB saves");

            // Evacuation spends the same budget: its own is wide open here, and the copies off the stopped nodes still
            // stop at the global burst.
            StopAndStart(f, ("polaris", 2, 0));
            BootServing(f, "node-3"); BootServing(f, "node-4");
            int moved = PumpReplicas(f, "node-3").Count + PumpReplicas(f, "node-4").Count;
            Check(moved is >= 9 and <= 11, $"evacuation is paced by the global budget too: {moved} copies");
        }
        finally
        {
            GatewayRegistry.ReplicaBytesPerSecond = all;
            GatewayRegistry.EvacuateBytesPerSecond = evacuate;
        }
        Console.WriteLine("replica-budget: 30 copies at once without the budget; with it a 10 s burst in rank order, nothing when spent, refill by the clock, freshest first, evacuation included");
    }

    private static void TestReplicaAlarm()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("replica-alarm: " + what); }
        int all = GatewayRegistry.ReplicaBytesPerSecond;
        GatewayRegistry.ReplicaBytesPerSecond = 1 << 30;
        try
        {
            // One minute of a cluster: node-1 writes `added` saves, a minute passes, node-2 copies `copied` of them.
            // The registry samples at node-2's first poll, so a sample sees the minute's new saves uncopied.
            static GatewayRegistry.ReplicaAlarmView Minute(NodeControlFixture f, int added, int copied)
            {
                SingleCopySaves(f, "node-1", added);
                f.Advance(60);
                if (copied == 0) f.Registry.ReplicaWork("node-2", f.Row("node-2").Epoch);   // polls, and its copy fails
                else PumpReplicas(f, "node-2", copied);
                return f.Registry.ReplicaAlarm();
            }
            static NodeControlFixture Cluster(string name)
            {
                var f = new NodeControlFixture(name);
                f.Apply(("polaris", 3, 0));
                foreach (string node in f.Live()) BootServing(f, node);
                return f;
            }

            using (NodeControlFixture f = Cluster("replica-alarm-eta"))
            {
                // Turnover: what a minute adds the next minute copies. Never an alarm.
                for (int minute = 0; minute < 12; minute++) Minute(f, 3, 3);
                Check(!f.Registry.ReplicaAlarm().Active, "turnover is not a backlog");
                // A stop's worth of saves that clears inside a few minutes: the earned red of the alarm.
                Minute(f, 200, 100);
                for (int minute = 0; minute < 10; minute++) Minute(f, 3, 60);
                Check(f.Registry.ReplicaAlarmsRaised == 0, "a backlog that drains raises nothing");
                // The same backlog at 1 copy a minute would take hours.
                Minute(f, 100, 1);
                for (int minute = 0; minute < 3; minute++)
                    Check(!Minute(f, 1, 1).Active, "not before the verdict has held for five samples");
                GatewayRegistry.ReplicaAlarmView alarm = default!;
                for (int minute = 0; minute < 3; minute++) alarm = Minute(f, 1, 1);
                Check(alarm.Active && f.Registry.ReplicaAlarmsRaised == 1, "raised: 100 saves queued at 1 copy/min");
                Check(alarm.Depth is >= 100 and <= 110 && alarm.CopiesPerMinute is > 0.9 and < 1.1 && alarm.EtaMinutes > 90
                    && alarm.IntervalMinutes == 30 && alarm.Reason!.Contains("outlasts") && alarm.Since != null,
                    "the alarm carries depth, rate, eta and the interval: " + alarm);
                for (int minute = 0; minute < 20; minute++) Minute(f, 1, 1);
                Check(f.Registry.ReplicaAlarmsRaised == 1 && f.Registry.ReplicaAlarm().Active, "one warning per episode");
                // No copies at all: no eta to give, still the same episode.
                for (int minute = 0; minute < 7; minute++) alarm = Minute(f, 1, 0);
                Check(alarm.Active && alarm.EtaMinutes == null && alarm.CopiesPerMinute == 0, "no copies: active, no eta: " + alarm);
                Minute(f, 0, int.MaxValue);
                for (int minute = 0; minute < 3; minute++)
                    Check(Minute(f, 3, 3).Active, "not cleared before the queue has stayed clear for five samples");
                for (int minute = 0; minute < 3; minute++) alarm = Minute(f, 3, 3);
                Check(!alarm.Active && alarm.Reason == null && alarm.Since == null && f.Registry.ReplicaAlarmsCleared == 1, "cleared once the queue drained: " + alarm);
            }

            // A queue that would clear inside the interval at its rate, but never gets smaller: capacity equals
            // invalidation. Earned red: one copy a minute more and the same queue shrinks, with no alarm.
            foreach (int copied in new[] { 6, 5 })
            {
                using NodeControlFixture f = Cluster("replica-alarm-flat-" + copied);
                Minute(f, 100, 0);
                bool active = false;
                for (int minute = 0; minute < 28; minute++) active |= Minute(f, 5, copied).Active;
                Check(!active, $"{copied} copies/min: nothing before 30 min of samples");
                for (int minute = 0; minute < 12; minute++) active |= Minute(f, 5, copied).Active;
                if (copied == 6) Check(!active && f.Registry.ReplicaAlarmsRaised == 0, "a queue that shrinks (5 in, 6 out a minute) is not an alarm");
                else
                    Check(active && f.Registry.ReplicaAlarm().Reason!.Contains("not shrunk") && f.Registry.ReplicaAlarm().EtaMinutes < 30,
                        "a queue that does not shrink in 30 min is, although its eta is inside the interval: " + f.Registry.ReplicaAlarm());
            }

            // The interval comes from the node history: recycles 6 min apart leave 6 min to clear a queue. Earned red:
            // the same queue and rate without recycles (30 min assumed) is fine.
            foreach (bool recycles in new[] { false, true })
            {
                using NodeControlFixture f = Cluster("replica-alarm-interval-" + recycles);
                Minute(f, 40, 0);
                bool active = false;
                string spare = "node-3";
                for (int minute = 1; minute <= 24; minute++)
                {
                    active |= Minute(f, 4, 4).Active;
                    if (!recycles || minute % 6 != 0) continue;
                    NodeControlChange change = f.Registry.RecycleNode(spare, now: false, timeout: null, "loopback", "rotator", "minute " + minute);
                    BootServing(f, change.Replacement!);
                    f.DrainToEmpty(spare);
                    f.Report(spare, SpawnState.Gone);
                    spare = change.Replacement!;
                }
                GatewayRegistry.ReplicaAlarmView alarm = f.Registry.ReplicaAlarm();
                if (!recycles) Check(!active && alarm.IntervalMinutes == 30 && alarm.EtaMinutes is > 9 and < 12, "no recycles: 30 min assumed, 11 min to clear is fine: " + alarm);
                else
                    Check(active && alarm.Active && alarm.IntervalMinutes is > 5.5 and < 6.5 && alarm.Reason!.Contains("outlasts"),
                        "recycles 6 min apart: 11 min to clear outlasts the interval: " + alarm);
            }
        }
        finally { GatewayRegistry.ReplicaBytesPerSecond = all; }
        Console.WriteLine("replica-alarm: quiet on turnover and on a backlog that drains; raised once per episode when the queue outlasts the recycle interval (from the node history, else 30 min) or has not shrunk in 30 min; cleared after the drain");
    }
}
