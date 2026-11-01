using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// #1218: a node whose process froze (a blocking full GC under host CPU saturation) keeps its lease while its host
    /// reports the process running, up to the grace; a crash (Gone), a host that stopped reporting, the end of the grace
    /// and a disabled grace all still declare it Dead.
    /// </summary>
    private static void TestLiveProcessHold()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("live-process-hold: " + what); }
        TimeSpan ttl = TimeSpan.FromSeconds(15);
        string root = Path.Combine(Path.GetTempPath(), "cluster-live-process-hold-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            using var registry = new GatewayRegistry(root, Admission, () => now, ttl) { LiveProcessGrace = TimeSpan.FromSeconds(60) };
            var request = new Admin.ExecutorPollRequest("host-1", "executor-1", Guid.NewGuid(), null, 1, []);
            request = request with { Lease = registry.PollExecutor(request).Lease };
            // One frozen node per case, each on its own slot of host-1.
            NodeRegistration Start(string node, ulong partition)
            {
                registry.PutNodeSlot("slot-" + node, "host-1", NodeRole.Regular, NodeDesiredState.Wanted);
                NodeRegistration row = Register(registry, node, "127.0.0.1:" + (28400 + (int)partition), Guid.NewGuid());
                registry.ClaimPartition(partition, row.Node, row.Epoch, 0);
                return row;
            }
            Admin.ExecutorNodeReport Report(NodeRegistration row, Admin.NodeObservation state) =>
                new("slot-" + row.Node, "attempt-" + row.Node, state, row.Node, row.Epoch, null);
            void Poll(params Admin.ExecutorNodeReport[] reports)
            {
                request = request with { Sequence = request.Sequence + 1, Reports = reports };
                registry.PollExecutor(request);
            }
            bool Live(NodeRegistration row, ulong partition) => registry.HasLiveNode(row.Node, row.Epoch)
                && registry.HasLiveOwnership(partition, row.Node, row.Epoch, 1);
            NodeState State(NodeRegistration row) => registry.Nodes().Single(x => x.Node == row.Node).State;

            NodeRegistration frozen = Start("frozen", 1), crashed = Start("crashed", 2), orphaned = Start("orphaned", 3);
            NodeRegistration[] all = [frozen, crashed, orphaned];
            Poll([.. all.Select(row => Report(row, Admin.NodeObservation.Ready))]);
            Check(all.All(row => Live(row, (ulong)Array.IndexOf(all, row) + 1)), "every node starts live");

            // 18:47:40 local-node-3: the process freezes right after a renewal. The host keeps reporting it Ready; the
            // crashed node's process is gone; the orphaned node's host stops reporting.
            for (int second = 1; second <= 30; second++)
            {
                now = now.AddSeconds(1);
                if (second % 2 == 0)   // Quasar.Host polls every 2 s on polaris
                    Poll(Report(frozen, Admin.NodeObservation.Ready), Report(crashed, Admin.NodeObservation.Gone));
                registry.ExpireLeases();
            }
            Check(State(frozen) == NodeState.Active && Live(frozen, 1), "a frozen node whose host reports it running is held past its lease");
            Check(State(crashed) == NodeState.Dead && registry.PartitionsOwnedBy(crashed.Node, crashed.Epoch).Length == 0,
                "a node whose process is Gone dies at its lease, as before");
            Check(State(orphaned) == NodeState.Dead, "a node whose host stopped reporting dies once the last report is stale");

            // 18:48:15: it wakes. Its heartbeat renews the lease and keeps the partition: no fence, no players dropped.
            registry.Heartbeat(frozen.Node, frozen.Epoch, [new PartitionVersion(1, 1, 3)]);
            Check(State(frozen) == NodeState.Active && registry.ExpireLeases() == 0 && Live(frozen, 1), "the woken node renews and keeps its partition");

            // A freeze past the grace (counted from the last renewal) is death even while the host reports it running.
            DateTimeOffset renewed = now;
            while (now < renewed + TimeSpan.FromSeconds(59))
            {
                now = now.AddSeconds(1);
                Poll(Report(frozen, Admin.NodeObservation.Ready));   // accepted: a held lease is live (#1218)
                registry.ExpireLeases();
            }
            Check(State(frozen) == NodeState.Active, "held up to the grace");
            now = now.AddSeconds(2);
            registry.ExpireLeases();
            Check(State(frozen) == NodeState.Dead && registry.PartitionsOwnedBy(frozen.Node, frozen.Epoch).Length == 0,
                "past the grace the node is Dead and its partition released");

            // A freeze right after a frame report: the first report after it carries the same frame. The silence is not a
            // game-thread stall (no Closed, no forced drain 9 s after waking) - once a minute; a second freeze soon after is.
            NodeRegistration stalled = Start("stalled", 5);
            long frame = 100;
            void Beat(long simulationFrame) => registry.Heartbeat(stalled.Node, stalled.Epoch, [new PartitionVersion(5, 1, 3)],
                simulationFrame: simulationFrame);
            void Freeze(int seconds)
            {
                for (int second = 1; second <= seconds; second++)
                {
                    now = now.AddSeconds(1);
                    if (second % 2 == 0) Poll(Report(stalled, Admin.NodeObservation.Ready));
                    registry.ExpireLeases();
                }
            }
            for (int second = 0; second < 3; second++) { Beat(++frame); now = now.AddSeconds(1); }
            Poll(Report(stalled, Admin.NodeObservation.Ready));
            Freeze(35);
            Beat(frame);
            Check(State(stalled) == NodeState.Active && Live(stalled, 5), "a node waking from a freeze on an unchanged frame is not closed as stalled");
            for (int second = 0; second < 5; second++) { now = now.AddSeconds(1); Beat(++frame); }
            Freeze(20);
            Beat(frame);
            Check(State(stalled) == NodeState.Closed, "a second freeze within the minute counts as a stall again (closed for a drain)");

            // Control: grace 0 is the old behaviour - the lapse is death however alive the process is.
            registry.LiveProcessGrace = TimeSpan.Zero;
            NodeRegistration unheld = Start("unheld", 4);
            Poll(Report(unheld, Admin.NodeObservation.Ready));
            now = now.AddSeconds(16);   // its Ready report is 16 s old: fresh enough for a hold
            registry.ExpireLeases();
            Check(State(unheld) == NodeState.Dead, "with the grace off a lapsed lease is death");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// #1282 (T-0296 round 5 F37): recovery must not roll a partition back past a newer save whose only live holder is
    /// held by the live-process hold. Partition 11: save S1 on C, a handover to B, save S2 on B + A. A freezes (its host
    /// keeps reporting it Ready), B crashes (Gone). HolderUsable reads A as unusable and B as dead, so Newest() skipped S2
    /// and C's auto-pick restored S1: everything since the handover lost when A woke up to 60 s later. Fixed: 11 waits
    /// while the hold lasts, and once A is back C's claim names S2.
    /// </summary>
    private static void TestLiveProcessHoldRecovery()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("live-process-hold-recovery: " + what); }
        TimeSpan ttl = TimeSpan.FromSeconds(15);
        string root = Path.Combine(Path.GetTempPath(), "cluster-live-process-hold-recovery-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TextWriter console = Console.Out;
        Console.SetOut(new StringWriter());
        try
        {
            using var registry = new GatewayRegistry(root, Admission, () => now, ttl) { LiveProcessGrace = TimeSpan.FromSeconds(60) };
            var request = new Admin.ExecutorPollRequest("host-1", "executor-1", Guid.NewGuid(), null, 1, []);
            request = request with { Lease = registry.PollExecutor(request).Lease };
            NodeRegistration Start(string node, int port)
            {
                registry.PutNodeSlot("slot-" + node, "host-1", NodeRole.Regular, NodeDesiredState.Wanted);
                return Register(registry, node, "127.0.0.1:" + port, Guid.NewGuid());
            }
            Admin.ExecutorNodeReport Report(NodeRegistration row, Admin.NodeObservation state) =>
                new("slot-" + row.Node, "attempt-" + row.Node, state, row.Node, row.Epoch, null);
            void Poll(params Admin.ExecutorNodeReport[] reports)
            {
                request = request with { Sequence = request.Sequence + 1, Reports = reports };
                registry.PollExecutor(request);
            }
            string Claims(RecoveryClaim[] claims) =>
                string.Join(",", claims.Select(c => $"{c.Ownership.Partition}:{string.Join("+", c.ResolveCover.Select(v => v.Save.SaveId.Generation))}"));
            NodeState State(NodeRegistration row) => registry.Nodes().Single(x => x.Node == row.Node).State;

            NodeRegistration c = Start("c", 28501), b = Start("b", 28502), a = Start("a", 28503);
            registry.ClaimPartition(1, a.Node, a.Epoch, 0);   // A owns something: C is the emptiest node for a free recovery
            PartitionOwnership first = registry.ClaimPartition(11, c.Node, c.Epoch, 0);
            var s1 = new SaveId(11, first.Generation, 1);
            registry.RegisterSave(s1, c.Node, c.Epoch, SaveKind.Periodic, 1, "h1", 2, "c/s1", "http://c/control");
            registry.Begin("h11", 11, c.Node, c.Epoch, first.Generation, b.Node, b.Epoch);
            registry.Activate("h11");
            long moved = registry.Commit("h11").CommittedGeneration!.Value;
            var s2 = new SaveId(11, moved, 1);
            registry.RegisterSave(s2, b.Node, b.Epoch, SaveKind.Periodic, 1, "h2", 3, "b/s2", "http://b/control");
            registry.RegisterHolder(s2, a.Node, a.Epoch, 1, "h2", "a/s2", "http://a/control");
            Poll(Report(c, Admin.NodeObservation.Ready), Report(b, Admin.NodeObservation.Ready), Report(a, Admin.NodeObservation.Ready));

            // A freezes and B crashes; C keeps heartbeating, its host reports A running and B gone.
            for (int second = 1; second <= 20; second++)
            {
                now = now.AddSeconds(1);
                registry.Heartbeat(c.Node, c.Epoch, []);
                if (second % 2 == 0)
                    Poll(Report(c, Admin.NodeObservation.Ready), Report(a, Admin.NodeObservation.Ready), Report(b, Admin.NodeObservation.Gone));
                registry.ExpireLeases();
            }
            Check(State(b) == NodeState.Dead && State(a) == NodeState.Active && registry.GetPartitionOwner(11).Node == null,
                $"setup: B dead, A held, 11 unowned (b={State(b)} a={State(a)} owner={registry.GetPartitionOwner(11).Node ?? "none"})");
            string held = Claims(registry.ClaimRecoveries(c.Node, c.Epoch, []));
            Check(held == "", $"while A is held 11 waits for S2 instead of restoring S1: claimed [{held}] (S1 gen {first.Generation}, S2 gen {moved})");
            string asked = Claims(registry.ClaimRecoveries(c.Node, c.Epoch, [11]));
            Check(asked == "", $"an explicit request waits as well: claimed [{asked}]");

            // A wakes: its heartbeat renews the lease, S2 is usable again and the claim names it.
            registry.Heartbeat(a.Node, a.Epoch, [new PartitionVersion(1, 1, 3)]);
            registry.ExpireLeases();
            string after = Claims(registry.ClaimRecoveries(c.Node, c.Epoch, []));
            Check(after == $"11:{moved}", $"once A is back the claim names S2 (gen {moved}): [{after}]");
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// #1282, second arm: the host store. The newest save of a partition lies only in the store of host sdx, whose one node
    /// froze (its host agent keeps reporting it Ready); an older save is in polaris's store too. The store reads Held,
    /// not Dark, and the recovery waits; past the hold's grace the node is Dead, the store Dark, and recovery proceeds
    /// with the older save - the hold delays, it never blocks.
    /// </summary>
    private static void TestLiveProcessHoldHostStore()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("live-process-hold-host-store: " + what); }
        NodeControlFixture f = HostStoreFixture("live-process-hold-host-store", out string top, ("polaris", 2, 0), ("sdx", 1, 0));
        try
        {
            f.Registry.LiveProcessGrace = TimeSpan.FromSeconds(60);
            string sdx = PlannedOn(f, "sdx").Single();
            string onPolaris = PlannedOn(f, "polaris").First(node => f.Row(node).State == NodeState.Active);
            ulong waiting = OwnNewPartition(f, sdx);
            SaveId older = SaveInStore(f, sdx, "sdx", waiting);
            CopyIntoStore(f, older, onPolaris, "polaris");
            SaveId newest = SaveInStore(f, sdx, "sdx", waiting, 2);
            f.Registry.ReleasePartition(waiting, sdx, f.Row(sdx).Epoch, newest.Generation);
            f.Silent.Add(sdx);
            for (int second = 0; second < 16; second += 4)
            {
                f.Report(sdx, SpawnState.Ready);
                f.Advance(4);
            }
            Check(f.Row(sdx).State == NodeState.Active && f.Registry.HostStoreStateOf("sdx") == HostStoreState.Held,
                $"sdx's frozen node is held and so is its store (node {f.Row(sdx).State}, store {f.Registry.HostStoreStateOf("sdx")})");
            Check(f.Registry.ClaimRecoveries(onPolaris, f.Row(onPolaris).Epoch, [waiting]).Length == 0,
                "recovery waits for the newest save in the held store instead of restoring the older one");

            // The node never wakes: past the grace it is Dead, the store Dark, and the older save is the best there is.
            for (int second = 16; second < 64; second += 4)
            {
                f.Report(sdx, SpawnState.Ready);
                f.Advance(4);
            }
            Check(f.Row(sdx).State == NodeState.Dead && f.Registry.HostStoreStateOf("sdx") == HostStoreState.Dark,
                $"past the grace the node is Dead and the store Dark (node {f.Row(sdx).State}, store {f.Registry.HostStoreStateOf("sdx")})");
            RecoveryClaim[] claims = f.Registry.ClaimRecoveries(onPolaris, f.Row(onPolaris).Epoch, [waiting]);
            Check(claims.Length == 1 && claims[0].ResolveCover.Single().Save.SaveId == older,
                "past the hold the recovery proceeds with the older save: the hold is bounded");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
    }
}
