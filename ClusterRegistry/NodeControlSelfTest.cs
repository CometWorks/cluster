using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>T-0320 node control v2 (NodeControl.cs): ids, states, policy, remove/recycle, forced drain, history,
/// hosts file, selectors, and the admin routes over HTTP.</summary>
internal static partial class SelfTest
{
    /// <summary>A registry on a test clock with the launcher and the nodes played by hand.</summary>
    private sealed class NodeControlFixture : IDisposable
    {
        public readonly string Directory;
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(400);
        public GatewayRegistry Registry;
        /// <summary>Nodes that stopped heartbeating (they die at the next lease expiry).</summary>
        public readonly HashSet<string> Silent = [];

        /// <summary>T-0340: the gateway host a reopen loads with (the re-key at load needs it before anything can set it).</summary>
        public string? GatewayHost;

        public NodeControlFixture(string name, string? directory = null)
        {
            Directory = directory ?? Path.Combine(Path.GetTempPath(), "cluster-node-control-" + name + "-" + Guid.NewGuid());
            Registry = Open();
        }

        private GatewayRegistry Open() => new(Directory, Admission, () => Now, TimeSpan.FromSeconds(10), gatewayHost: GatewayHost);

        public void Reopen()
        {
            Registry.Dispose();
            Registry = Open();
        }

        public void Dispose()
        {
            Registry.Dispose();
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
        }

        public static int Number(string node) => int.TryParse(node.AsSpan(node.IndexOf('-') + 1), out int number) ? number : 0;

        public NodeRegistration Boot(string node, string host = "polaris", int? pid = null, bool serving = true, bool reportHost = true)
        {
            NodeRegistration row = Registry.RegisterNode(Join(node, $"127.0.0.1:{27100 + Number(node)}", Guid.NewGuid())
                with { Host = reportHost ? host : null, Pid = reportHost ? pid ?? 40000 + Number(node) : null, HostStore = true });
            Registry.Heartbeat(row.Node, row.Epoch, [], serving: serving);
            return row;
        }

        public NodeRegistration Row(string node) => Registry.Nodes().Single(x => x.Node == node);

        public void Heartbeat(string node, bool? serving = null)
        {
            NodeRegistration row = Row(node);
            Registry.Heartbeat(row.Node, row.Epoch, [.. Registry.PartitionsOwnedBy(row.Node, row.Epoch).Select(x => new PartitionVersion(x.Partition, x.Generation))],
                serving: serving ?? row.Serving);
        }

        /// <summary>Clock forward in steps of at most 4 s; every live, non-silent node heartbeats; leases expire; the pass runs.</summary>
        public void Advance(double seconds)
        {
            double left = seconds;
            do
            {
                double step = Math.Min(4, left);
                Now += TimeSpan.FromSeconds(step);
                left -= step;
                foreach (NodeRegistration row in Registry.Nodes().Where(x => x.State is NodeState.Active or NodeState.Closed or NodeState.Empty && !Silent.Contains(x.Node)))
                    Heartbeat(row.Node);
                Registry.ExpireLeases();
                Registry.NodeControlPass();
            } while (left > 0);
        }

        public void Pass() => Registry.NodeControlPass();

        public PartitionOwnership Own(string node, ulong partition, bool save = false)
        {
            NodeRegistration row = Row(node);
            PartitionOwnership owned = Registry.ClaimPartition(partition, row.Node, row.Epoch, null);
            if (save)
                Registry.RegisterSave(new SaveId(partition, owned.Generation, 1), row.Node, row.Epoch, SaveKind.Periodic, 1, "c" + partition, 1,
                    row.Node + "/" + partition, entities: 3);
            return owned;
        }

        /// <summary>The node's own drain: every partition released, then Empty (it must be Closed by then).</summary>
        public void DrainToEmpty(string node)
        {
            NodeRegistration row = Row(node);
            foreach (PartitionOwnership owned in Registry.PartitionsOwnedBy(row.Node, row.Epoch))
                Registry.ReleasePartition(owned.Partition, row.Node, row.Epoch, owned.Generation);
            if (row.State == NodeState.Active) Registry.CloseNode(row.Node, row.Epoch, TimeSpan.FromSeconds(60));
            Registry.MarkEmpty(row.Node, row.Epoch);
        }

        /// <summary>ScaleInPass's settle half, registry-only: a retiring node whose report owns nothing is closed.</summary>
        public string[] SettleRetiring(params string[] owning)
        {
            string[] closed = Registry.SettleRetiring(new HashSet<string>(owning));
            foreach (string node in closed)
            {
                NodeRegistration row = Row(node);
                Registry.CloseNode(row.Node, row.Epoch, TimeSpan.FromSeconds(60));
            }
            return closed;
        }

        public void Report(string node, SpawnState state, int? pid = null) =>
            Registry.ExecutorHeartbeat("executor-polaris", [new(node, "attempt-" + node, state, node, null, pid)]);

        public Admin.NodeControlView View() => Registry.NodeControlView(null, null, "polaris", 1);

        public Admin.OperatorNode? Node(string node) => View().Nodes.SingleOrDefault(x => x.Node == node);

        public Admin.OperatorNodeState? State(string node) => Node(node)?.State;

        public string[] Live() => [.. View().Nodes.Select(x => x.Node).OrderBy(Number)];

        public Admin.NodeHistoryEntry? History(string node) => Registry.NodeHistoryPage(GatewayRegistry.MaxNodeHistory, null, null)
            .Records.FirstOrDefault(x => x.Node == node);

        public NodeSlotPlan? Slot(string node) => Registry.NodePlan().SingleOrDefault(x => x.SlotKey == node);

        public NodeControlChange Apply(params (string Host, int Serving, int Spares)[] hosts) =>
            Registry.ApplyHostsFile(hosts.ToDictionary(x => x.Host, x => new HostTarget(x.Serving, x.Spares)), "sha-test", "loopback");

        public NodeControlChange Set(string host, int? serving = null, int? spares = null, Func<int, string?>? capacity = null) =>
            Registry.SetHostTargets(host, serving, spares, "loopback", "viktor@polaris", "test", capacity);
    }

    private static void AssertNodeControlError(Action action, int status, string code, string what)
    {
        try { action(); }
        catch (NodeControlException error) when (error.Status == status && error.Code == code) { return; }
        catch (Exception error) { throw new InvalidOperationException($"{what}: expected {status} {code}, got {error.GetType().Name}: {error.Message}"); }
        throw new InvalidOperationException($"{what}: expected {status} {code}, nothing thrown");
    }

    private static void AssertRegistrationRefused(Action action, string prefix, string what)
    {
        try { action(); }
        catch (RegistryConflictException error) when (error.Message.StartsWith(prefix, StringComparison.Ordinal)) { return; }
        catch (Exception error) { throw new InvalidOperationException($"{what}: expected {prefix}, got {error.GetType().Name}: {error.Message}"); }
        throw new InvalidOperationException($"{what}: expected {prefix}, the registration was accepted");
    }

    // Global ids are never reused: after the highest node is removed and the store reopened, the next id is new;
    // the ids of a run before node control (T-0312's node-5..7) and a planned id that never started are skipped too.
    private static void TestNodeControlIds()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("node-control-ids: " + what); }
        using var f = new NodeControlFixture("ids");
        // T-0312 leftovers registered before node control was active (legacy registration still works then).
        f.Boot("node-5"); f.Boot("node-6"); f.Boot("node-7");
        NodeRegistration wa = f.Registry.RegisterNode(Join("wa-1", "127.0.0.1:28500", Guid.NewGuid()) with { Role = NodeRole.WA, Host = "polaris", Pid = 77 });
        f.Apply(("polaris", 3, 0));
        Check(f.Live().SequenceEqual(["node-8", "node-9", "node-10"]), "ids continue after every id the registry knows: " + string.Join(",", f.Live()));
        Check(f.View().NotCounted.Any(x => x.Node == "wa-1" && x.Role == "WorldAuthority" && x.Pid == 77) && f.Node("wa-1") == null,
            "the World Authority is listed as not counted, never as a node");
        AssertRegistrationRefused(() => f.Boot("node-5"), "node_not_planned", "an id outside the plan does not register while node control is active");
        foreach (string node in f.Live()) f.Boot(node);
        f.Set("polaris", serving: 4);
        Check(f.Live().Last() == "node-11", "the increase plans node-11");
        f.Boot("node-11");
        f.Registry.RemoveNode("node-11", now: true, timeout: null, "loopback", "viktor@polaris", "ids test");
        f.DrainToEmpty("node-11");
        f.Report("node-11", SpawnState.Gone);
        Check(f.History("node-11") != null && f.Node("node-11") == null, "the removed highest node is in the history, not live");
        AssertRegistrationRefused(() => f.Boot("node-11"), "node_id_retired", "a history id that registers again is refused");
        f.Reopen();
        f.Set("polaris", serving: 4);
        Check(f.Live().Last() == "node-12", "after a reopen the next id is node-12, never the removed node-11: " + string.Join(",", f.Live()));
        f.Set("polaris", spares: 1);
        Check(f.Live().Last() == "node-13" && f.State("node-13") == Admin.OperatorNodeState.Starting, "a spare is planned as node-13");
        f.Set("polaris", spares: 0);
        f.Pass();
        Check(f.Node("node-13") == null && f.History("node-13")?.Why is not null, "a spare that never started is cancelled into the history");
        f.Reopen();
        f.Set("polaris", spares: 1);
        Check(f.Live().Last() == "node-14", "a cancelled, never-registered id is not reused either: " + string.Join(",", f.Live()));
        Check(f.Registry.Nodes().Single(x => x.Node == "wa-1").Epoch == wa.Epoch, "the World Authority is untouched");
        Console.WriteLine("node-control-ids: node-8.. after T-0312's node-7, node-12 after the removed node-11 across a reopen, node-14 after a cancelled node-13");
    }

    // The operator states hide Active/Closed/Empty/Dead: the full mapping table, and a removed node stays listed as
    // Stopping (not hidden) until the launcher reports it gone.
    private static void TestNodeControlStates()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("node-control-states: " + what); }
        using (var f = new NodeControlFixture("states"))
        {
            f.Apply(("polaris", 2, 1));
            Check(f.Live().SequenceEqual(["node-1", "node-2", "node-3"]) && f.View().Nodes.All(x => x.State == Admin.OperatorNodeState.Starting),
                "planned, not registered: Starting");
            Check(f.Slot("node-1") is { Desired: NodeDesiredState.Wanted, Host: "polaris" } && f.Slot("node-3") != null, "every planned node is a Wanted slot keyed by its id");
            f.Boot("node-1", serving: false);
            Check(f.State("node-1") == Admin.OperatorNodeState.Starting, "registered, not serving yet: Starting");
            f.Heartbeat("node-1", serving: true);
            Check(f.State("node-1") == Admin.OperatorNodeState.Serving, "Active + serving: Serving");
            f.Boot("node-3", serving: false);
            Check(f.State("node-3") == Admin.OperatorNodeState.Starting, "a spare still loading: Starting");
            f.Heartbeat("node-3", serving: true);
            Check(f.State("node-3") == Admin.OperatorNodeState.Spare, "a warm spare: Spare");
            f.Boot("node-2");
            Check(f.State("node-2") == Admin.OperatorNodeState.Serving && f.Node("node-2")!.Partitions == 0,
                "a serving node with 0 partitions is still Serving");
            f.Own("node-2", 21);
            Check(f.Node("node-2")!.Partitions == 1 && f.Node("node-2")!.Pid == 40002 && f.Node("node-2")!.AgeSeconds == 0, "partitions, pid and age are listed");
            // Closed by the registry without a stop request (a direct close): Draining; the planner replaces it.
            NodeRegistration two = f.Row("node-2");
            f.Registry.CloseNode(two.Node, two.Epoch, TimeSpan.FromSeconds(60));
            f.Pass();
            Check(f.State("node-2") == Admin.OperatorNodeState.Draining, "Closed without a stop: Draining");
            Check(f.State("node-3") == Admin.OperatorNodeState.Serving && f.Live().Contains("node-4") && f.State("node-4") == Admin.OperatorNodeState.Starting,
                "a closing node no longer counts: the spare node-3 takes over and node-4 refills the spare");
            // Remove node-1: Draining while it retires, Stopping once forced or Empty, listed until gone.
            f.Registry.RemoveNode("node-1", now: false, timeout: TimeSpan.FromSeconds(300), "loopback", "viktor", "states");
            Check(f.State("node-1") == Admin.OperatorNodeState.Draining && f.Registry.IsRetiring("node-1"), "a removed serving node drains (retiring)");
            Check(f.Node("node-1")!.Stop is { Kind: "remove", Actor: "loopback", RequestedBy: "viktor", Why: "states", Forced: false },
                "the stop says who, why and that it is not forced");
            f.SettleRetiring();
            Check(f.State("node-1") == Admin.OperatorNodeState.Draining, "retiring and closed by the drain: still Draining (Closed is internal)");
            f.DrainToEmpty("node-1");
            f.Pass();
            Check(f.State("node-1") == Admin.OperatorNodeState.Stopping, "Empty: Stopping, and still listed until the launcher stops it");
            Check(f.Slot("node-1") is { IncumbentAction: NodeIncumbentAction.Kill }, "the launcher is told to kill the empty node");
            f.Report("node-1", SpawnState.Gone);
            Check(f.Node("node-1") == null && f.History("node-1") is { Outcome: "remove", Host: "polaris" }, "gone: out of the list, into the history");
            Check(f.Slot("node-1") == null, "the gone node's slot is dropped");
            // A registry-closed node that leaves.
            f.DrainToEmpty("node-2");
            f.Pass();
            Check(f.State("node-2") == Admin.OperatorNodeState.Stopping, "Empty without a stop: Stopping");
            f.Report("node-2", SpawnState.Gone);
            Check(f.History("node-2") is { Outcome: "closed" }, "a node that closed unasked and left is in the history as closed");
        }
        using (var f = new NodeControlFixture("states-failed"))
        {
            f.Apply(("polaris", 2, 0));
            f.Boot("node-1"); f.Boot("node-2");
            f.Report("node-2", SpawnState.Ready);
            f.Silent.Add("node-2");
            // #1218: past its lease AND past the freshness of its host's last Ready report (20 s) - a lapsed lease is held
            // only while the host keeps reporting the process running.
            f.Advance(21);
            Check(f.Row("node-2").State == NodeState.Dead && f.State("node-2") == Admin.OperatorNodeState.Failed, "a dead node: Failed");
            Check(f.Live().Contains("node-3") && f.Node("node-2")!.ReplacedBy == "node-3" && f.Node("node-3")!.Replaces == "node-2",
                "a failed node is replaced by a NEW id: " + string.Join(",", f.Live()));
            Check(f.Slot("node-2") is { Desired: NodeDesiredState.Kill }, "the dead id's slot says Kill: the launcher never restarts it");
            Check(f.Slot("node-3") is { SpawnAllowed: false, RetryAt: not null } && f.View().Hosts.Single().Warnings.Any(x => x.Contains("failed in the last")),
                "a replacement after a death backs off (a crash loop must not relaunch at once under ever new ids)");
            AssertRegistrationRefused(() => f.Boot("node-2"), "node_id_retired", "a dead planned id does not come back");
            f.Boot("node-3", serving: false);
            f.Advance(1);
            Check(f.State("node-2") == Admin.OperatorNodeState.Failed, "Failed while the replacement is still starting");
            f.Heartbeat("node-3", serving: true);
            f.Pass();
            Check(f.Node("node-2") == null && f.History("node-2") is { Outcome: "failed", ReplacedBy: "node-3" }, "once the replacement serves, the failed node is history");
        }
        Console.WriteLine("node-control-states: Starting/Serving/Spare/Draining/Stopping/Failed from the internal states; Empty stays listed as Stopping until gone");
    }

    // Policy: an increase activates a warm spare ON THE SAME HOST, refills the spare with a new id, and launches fresh
    // nodes only for the remainder; a decrease drains the OLDEST serving node (a fixture where the oldest is not the
    // least loaded), a spare decrease stops the oldest spare, and an overloading decrease is obeyed with a warning.
    private static void TestNodeControlPolicy()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("node-control-policy: " + what); }
        using var f = new NodeControlFixture("policy");
        f.Apply(("ai", 1, 1), ("polaris", 3, 1));
        // ai: node-1 serving, node-2 spare; polaris: node-3..5 serving, node-6 spare (serving first per host).
        Check(f.View().Nodes.Where(x => x.Host == "polaris").Select(x => x.Node).SequenceEqual(["node-3", "node-4", "node-5", "node-6"]),
            "the hosts file plans serving then spare per host: " + string.Join(",", f.Live()));
        f.Boot("node-1", "ai"); f.Boot("node-2", "ai");
        // Oldest is not least loaded: node-3 starts first and owns the most, node-4 owns nothing.
        f.Boot("node-3"); f.Advance(60);
        f.Boot("node-4"); f.Advance(60);
        f.Boot("node-5"); f.Advance(60);
        f.Boot("node-6");
        f.Advance(1);
        for (ulong partition = 31; partition <= 35; partition++) f.Own("node-3", partition);
        f.Own("node-5", 51); f.Own("node-5", 52);
        Check(f.State("node-6") == Admin.OperatorNodeState.Spare && f.State("node-2") == Admin.OperatorNodeState.Spare, "both hosts have a warm spare");
        // Increase polaris by one: its spare activates at once, a new spare (max + 1) refills, nothing fresh serves.
        NodeControlChange up = f.Set("polaris", serving: 4);
        Check(f.State("node-6") == Admin.OperatorNodeState.Serving && f.Row("node-6").Activated == f.Now, "the polaris spare is activated synchronously");
        Check(f.State("node-2") == Admin.OperatorNodeState.Spare, "the spare on ai is not touched");
        Check(f.Live().Last() == "node-7" && f.State("node-7") == Admin.OperatorNodeState.Starting && f.Slot("node-7")!.Host == "polaris",
            "a new spare node-7 refills polaris");
        Check(up.Actions.Any(x => x.Contains("activated spare node-6")) && up.Actions.Any(x => x.Contains("planned spare node-7"))
            && !up.Actions.Any(x => x.Contains("planned serving")), "actions: activate, refill, no fresh serving node: " + string.Join(" | ", up.Actions));
        // Increase by two while the new spare is still starting: fresh serving nodes for the remainder, the spare count holds.
        NodeControlChange more = f.Set("polaris", serving: 6);
        Check(more.Actions.Count(x => x.Contains("planned serving")) == 2 && !more.Actions.Any(x => x.Contains("planned spare")),
            "no warm spare left: two fresh serving nodes, no extra spare: " + string.Join(" | ", more.Actions));
        Check(f.Live().SequenceEqual(["node-1", "node-2", "node-3", "node-4", "node-5", "node-6", "node-7", "node-8", "node-9"]), "node-8 and node-9 are fresh");
        f.Boot("node-7"); f.Boot("node-8"); f.Boot("node-9");
        f.Advance(1);
        // Decrease by one: the OLDEST serving node drains (node-3, 5 partitions), not the least loaded (node-4, 0).
        // An overloading decrease is obeyed with the projected load as a warning.
        NodeControlChange down = f.Set("polaris", serving: 5, capacity: remaining => $"projected load too high over {remaining} node(s)");
        Check(f.Registry.RetiringNodes().SequenceEqual(["node-3"]) && f.State("node-3") == Admin.OperatorNodeState.Draining,
            "the oldest serving node drains: " + string.Join(",", f.Registry.RetiringNodes()));
        Check(down.Warnings.SequenceEqual(["projected load too high over 6 node(s)"]) && f.Node("node-3")!.Stop is { Kind: "decrease" },
            "the capacity warning comes back and nothing is refused: " + string.Join(" | ", down.Warnings));
        // The operator's reason and identity reach the decrease stop (bench run 2: the set's --reason was dropped from the
        // history's why, and the reason was passed on as requestedBy).
        Check(f.Node("node-3")!.Stop is { Why: "test (serving on polaris lowered to 5)", RequestedBy: "viktor@polaris" },
            "the set's reason is the decrease stop's why: " + f.Node("node-3")!.Stop?.Why + " / " + f.Node("node-3")!.Stop?.RequestedBy);
        Check(DirectGateway.CapacityWarning(3_000_000, 2, true, 0.7) is { } warning && warning.Contains("1.50 game threads")
            && DirectGateway.CapacityWarning(1_000_000, 2, true, 0.7) == null && DirectGateway.CapacityWarning(9e9, 2, false, 0.7) == null
            && DirectGateway.CapacityWarning(1, 0, true, 0.7) != null, "capacity warning: over the share, under it, unmeasured, none left");
        // Spare decrease: the oldest spare stops (node-7 is polaris's only spare; add one and drop back).
        f.Set("polaris", spares: 2);
        f.Boot(f.Live().Last());
        f.Advance(1);
        f.Set("polaris", spares: 1);
        Check(f.Node("node-7")!.Stop is { Kind: "decrease" } && f.Node(f.Live().Last())!.Stop == null, "a spare decrease stops the oldest spare (node-7)");
        f.Pass();
        Check(f.State("node-7") == Admin.OperatorNodeState.Draining || f.State("node-7") == Admin.OperatorNodeState.Stopping,
            "the stopped spare closes (it owns nothing to drain)");
        Check(f.Row("node-7").State == NodeState.Closed, "a spare is closed directly, not retired through the pinned drain");
        Console.WriteLine("node-control-policy: spare on the host first, refill with a new id, fresh only for the rest; oldest serving and oldest spare go first; capacity warns, never refuses");
    }

    // remove = target -1 (serving or spare), recycle = count unchanged with the activated spare as the preferred drain
    // receiver (or a fresh replacement); the last serving node cannot be removed.
    private static void TestNodeControlRemoveRecycle()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("node-control-remove-recycle: " + what); }
        using var f = new NodeControlFixture("remove-recycle");
        f.Apply(("polaris", 3, 1));
        foreach (string node in f.Live()) { f.Boot(node); f.Advance(10); }
        NodeControlChange removed = f.Registry.RemoveNode("node-2", now: false, timeout: null, "loopback", "viktor", "less");
        Admin.HostView host = f.View().Hosts.Single();
        Check(removed.Node == "node-2" && host.Serving.Target == 2 && host.Serving.File == 3 && f.View().DiffersFromFile,
            "remove lowers the serving target by one (runtime differs from the file)");
        Check(f.Live().Length == 4 && f.State("node-4") == Admin.OperatorNodeState.Spare && removed.Replacement == null,
            "a remove plans no replacement and leaves the spare alone");
        NodeControlChange spare = f.Registry.RemoveNode("node-4", now: false, timeout: null, "loopback", "viktor", "no spare");
        Check(f.View().Hosts.Single().Spares.Target == 0 && f.Row("node-4").State == NodeState.Closed && spare.Node == "node-4",
            "removing the spare lowers the spare target and closes it");
        // Recycle with a warm spare: the count holds, the spare takes over and receives the whole drain.
        f.Set("polaris", spares: 1);
        f.Boot("node-5");
        f.Advance(1);
        NodeControlChange recycled = f.Registry.RecycleNode("node-1", now: false, timeout: null, "loopback", "rotator", "lifetime 1h02m > 1h00m");
        Check(recycled.Node == "node-1" && recycled.Replacement == "node-5" && f.State("node-5") == Admin.OperatorNodeState.Serving,
            "recycle activates the spare as the replacement");
        Check(f.View().Hosts.Single().Serving.Target == 2 && f.Registry.RetiringNodes().Contains("node-1"), "recycle keeps the count");
        Check(f.Registry.PreferredDrainReceiver("node-1") == "node-5", "the activated spare is the preferred drain receiver");
        Check(f.Live().Last() == "node-6" && f.State("node-6") == Admin.OperatorNodeState.Starting, "a new spare refills after the activation");
        Check(DirectGateway.DrainTarget([new PartitionInfo(1, 1, false, new PartitionSphere(0, 0, 0, 10), 0, [])],
            [("node-3", new PartitionReport(3000, 1000, [new PartitionInfo(2, 1, false, new PartitionSphere(1, 0, 0, 10), 0, [])], 0)),
             ("node-5", new PartitionReport(3000, 1000, [], 0))], 4000,
            new Dictionary<string, double> { ["node-3"] = 0, ["node-5"] = 9 }, "node-5") == "node-5",
            "DrainTarget sends the component to the preferred receiver even past a neighbour and a lighter node");
        // Recycle with no warm spare (node-6 still starting): a fresh node replaces it; preferred once it serves.
        NodeControlChange fresh = f.Registry.RecycleNode("node-3", now: false, timeout: null, "loopback", "viktor", "cruft");
        Check(fresh.Replacement == "node-7" && f.State("node-7") == Admin.OperatorNodeState.Starting && f.Registry.PreferredDrainReceiver("node-3") == null,
            "no warm spare: a fresh node replaces the recycled one: " + fresh.Replacement);
        f.Boot("node-7");
        Check(f.Registry.PreferredDrainReceiver("node-3") == "node-7", "the fresh replacement receives the drain once it serves");
        AssertNodeControlError(() => f.Registry.RemoveNode("node-3", false, null, "loopback", null, null), 409, "already_stopping", "a node already stopping");
        // The last serving node is not removable.
        using var last = new NodeControlFixture("remove-last");
        last.Apply(("polaris", 1, 0));
        last.Boot("node-1");
        AssertNodeControlError(() => last.Registry.RemoveNode("node-1", false, null, "loopback", null, null), 409, "last_serving_node", "the last serving node");
        AssertNodeControlError(() => last.Set("polaris", serving: 0), 409, "last_serving_node", "a target of zero serving nodes cluster-wide");
        Console.WriteLine("node-control-remove-recycle: remove -1 (serving/spare), recycle keeps the count with the spare as drain receiver or a fresh replacement");
    }

    // The drain deadline forces the node's OWN close drain (Closed, ForceDrainAt now) - never a kill; --now forces at
    // once; a forced node that still owns rows stays Stopping; the deadline clock stops while partitions recover; and
    // the memberless-row fixture (12f361b) finishes through a forced stop.
    private static void TestNodeControlForce()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("node-control-force: " + what); }
        using var f = new NodeControlFixture("force");
        f.Apply(("polaris", 4, 0));
        foreach (string node in f.Live()) f.Boot(node);
        f.Advance(1);
        f.Own("node-1", 11);
        f.Own("node-2", 21);
        f.Registry.RemoveNode("node-1", now: false, timeout: TimeSpan.FromSeconds(60), "loopback", "viktor", "deadline");
        f.Advance(56);
        Check(f.State("node-1") == Admin.OperatorNodeState.Draining && !f.Node("node-1")!.Stop!.Forced && f.Row("node-1").State == NodeState.Active,
            "before the deadline: Draining, not forced");
        f.Advance(8);
        NodeRegistration forced = f.Row("node-1");
        Check(f.Node("node-1")!.Stop!.Forced && f.State("node-1") == Admin.OperatorNodeState.Stopping, "past the deadline: forced, Stopping");
        Check(forced.State == NodeState.Closed && forced.ForceDrainAt <= f.Now, "forcing = the node's own close drain with ForceDrainAt now");
        // A stuck forced node is never killed: it owns partition 11 and keeps it for minutes.
        f.Advance(240);
        Check(f.Row("node-1").State == NodeState.Closed && f.Node("node-1") != null && f.Slot("node-1")!.IncumbentAction != NodeIncumbentAction.Kill,
            "a forced node that still owns a row is not killed and stays listed");
        Check(f.Node("node-1")!.Stop!.Detail?.Contains("still owns 1 partition") == true, "Stopping says why: " + f.Node("node-1")!.Stop!.Detail);
        f.DrainToEmpty("node-1");
        f.Pass();
        Check(f.Slot("node-1")!.IncumbentAction == NodeIncumbentAction.Kill, "once its drain empties it, the launcher is told to stop it");
        f.Report("node-1", SpawnState.Gone);
        Check(f.History("node-1") is { Forced: true, Outcome: "remove" }, "the history records the forced stop");
        // --now: forced in the same call.
        f.Registry.RemoveNode("node-2", now: true, timeout: null, "loopback", "viktor", "now");
        Check(f.Node("node-2")!.Stop is { Forced: true, Now: true } && f.Row("node-2").State == NodeState.Closed && f.Row("node-2").ForceDrainAt == f.Now,
            "--now forces at once");
        // The deadline pauses while partitions recover: node-3 has an unowned, recoverable partition around it.
        f.Own("node-4", 41, save: true);
        f.Registry.RecycleNode("node-3", now: false, timeout: TimeSpan.FromSeconds(60), "loopback", "viktor", "pause");
        NodeRegistration four = f.Row("node-4");
        f.Registry.ReleasePartition(41, four.Node, four.Epoch, f.Registry.GetPartitionOwner(41).Generation);
        Check(f.Registry.RecoveryPending(), "partition 41 is unowned and recoverable");
        f.Advance(120);
        Check(!f.Node("node-3")!.Stop!.Forced && f.Node("node-3")!.Stop!.Detail?.Contains("paused") == true, "no force while partitions recover");
        f.Registry.ClaimPartition(41, four.Node, four.Epoch, null);
        f.Heartbeat("node-4");
        f.Advance(40);
        Check(!f.Node("node-3")!.Stop!.Forced, "the paused time is added: 40 s after recovery ends it is still 20 s early");
        f.Advance(24);
        Check(f.Node("node-3")!.Stop!.Forced, "forced once the unpaused 60 s have run");
        Console.WriteLine("node-control-force: deadline and --now force the node's own drain, a stuck node is not killed, the deadline pauses during recovery");

        // 12f361b through a forced stop: a retiring node whose report owns nothing but whose registry row still names a
        // memberless partition. The report-driven settle closes it; the force leaves the release to the node's drain.
        using var m = new NodeControlFixture("force-memberless");
        m.Apply(("polaris", 2, 0));
        m.Boot("node-1"); m.Boot("node-2");
        PartitionOwnership memberless = m.Own("node-2", 286401);
        m.Registry.RemoveNode("node-2", now: false, timeout: TimeSpan.FromSeconds(30), "loopback", "viktor", "memberless");
        Check(m.SettleRetiring().SequenceEqual(["node-2"]), "the report owns nothing: closed although a memberless row names it");
        m.Advance(32);
        Check(m.Node("node-2")!.Stop!.Forced && m.Row("node-2").State == NodeState.Closed && m.Row("node-2").ForceDrainAt <= m.Now,
            "the deadline forces the closed node's drain");
        Check(m.Slot("node-2")!.IncumbentAction == NodeIncumbentAction.Draining, "still Draining for the launcher: never killed while the row exists");
        NodeRegistration two = m.Row("node-2");
        m.Registry.ReleasePartition(286401, two.Node, two.Epoch, memberless.Generation);
        m.Registry.MarkEmpty(two.Node, two.Epoch);
        m.Report("node-2", SpawnState.Gone);
        Check(m.History("node-2") is { Forced: true } && m.Node("node-2") == null, "the memberless node finishes through the forced stop");
        Console.WriteLine("node-control-force: the memberless-row fixture finishes through a forced stop");
    }

    // History: who, when, why, age, host, forced, replacement; newest first with cursor and host filter; bounded;
    // survives a reopen.
    private static void TestNodeControlHistory()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("node-control-history: " + what); }
        int limit = GatewayRegistry.NodeHistoryLimit;
        try
        {
            using var f = new NodeControlFixture("history");
            f.Apply(("ai", 1, 0), ("polaris", 2, 0));
            foreach (Admin.OperatorNode node in f.View().Nodes) f.Boot(node.Node, node.Host);
            DateTimeOffset started = f.Now;
            f.Advance(3600);
            f.Registry.RecycleNode("polaris#1", now: true, timeout: null, "quasar", "viktor@polaris", "cruft");
            string recycled = f.View().Nodes.Single(x => x.Stop != null).Node;
            f.DrainToEmpty(recycled);
            f.Now += TimeSpan.FromSeconds(5);
            f.Report(recycled, SpawnState.Gone);
            Admin.NodeHistoryEntry entry = f.History(recycled)!;
            Check(entry is { Outcome: "recycle", Actor: "quasar", RequestedBy: "viktor@polaris", Why: "cruft", Forced: true, Host: "polaris" }
                && entry.StartedAt == started && entry.EndedAt == f.Now && Math.Abs(entry.AgeSeconds - (f.Now - started).TotalSeconds) < 0.001
                && entry.ReplacedBy != null && entry.Pid == 40000 + NodeControlFixture.Number(recycled),
                $"who/when/why/age/host/forced/replacement: {entry}");
            f.Reopen();
            Check(f.History(recycled) == entry, "the history survives a reopen");
            // Bounded: with the limit at 20, 30 more stops keep at most 20, newest last, the oldest pruned.
            GatewayRegistry.NodeHistoryLimit = 20;
            for (int i = 0; i < 15; i++)
            {
                f.Set("polaris", spares: 1);
                f.Set("polaris", spares: 0);
                f.Set("ai", spares: 1);
                f.Set("ai", spares: 0);
                f.Pass();
            }
            Admin.NodeHistoryPage all = f.Registry.NodeHistoryPage(1000, null, null);
            Check(all.Records.Length <= 20 && all.Records.Length >= 19 && f.History(recycled) == null, $"bounded: {all.Records.Length} records, the oldest pruned");
            Check(all.Records.Zip(all.Records.Skip(1)).All(x => x.First.Seq > x.Second.Seq), "newest first");
            Admin.NodeHistoryPage first = f.Registry.NodeHistoryPage(5, null, "ai");
            Admin.NodeHistoryPage second = f.Registry.NodeHistoryPage(5, first.NextCursor, "ai");
            Check(first.Records.Length == 5 && first.Records.All(x => x.Host == "ai") && first.NextCursor == first.Records[^1].Seq
                && second.Records.All(x => x.Seq < first.Records[^1].Seq && x.Host == "ai"), "cursor pages and the host filter");
            f.Reopen();
            Check(f.Registry.NodeHistoryPage(1000, null, null).Records.SequenceEqual(all.Records), "the pruned history survives a reopen");
        }
        finally { GatewayRegistry.NodeHistoryLimit = limit; }
        Console.WriteLine("node-control-history: who/when/why/age/host/forced per stopped node, newest first with cursor and host filter, bounded, durable");
    }

    // The hosts file: applied at start with FRESH ids (every live record to the history as restart), resets the runtime
    // targets, differsFromFile after a runtime set, a reopen does not reapply, invalid files are refused.
    private static void TestNodeControlHostsFile()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("node-control-hosts-file: " + what); }
        using var f = new NodeControlFixture("hosts-file");
        Check(!f.View().Active && !f.View().DiffersFromFile, "before any apply: inactive");
        f.Apply(("polaris", 2, 1));
        Admin.NodeControlView view = f.View();
        Check(view.Active && !view.DiffersFromFile && view.HostsFile.Sha256 == "sha-test" && view.HostsFile.AppliedAt == f.Now
            && view.Hosts.Single() is { Serving: { Target: 2, File: 2 }, Spares: { Target: 1, File: 1 } }, "the apply sets runtime and file targets");
        foreach (string node in f.Live()) f.Boot(node);
        f.Set("polaris", serving: 3);
        Check(f.View().DiffersFromFile && f.View().Hosts.Single() is { Serving: { Target: 3, File: 2 } }, "a runtime set differs from the file");
        f.Reopen();
        Check(f.View().DiffersFromFile && f.Live().SequenceEqual(["node-1", "node-2", "node-3", "node-4"]),
            "a reopen (gateway restart) keeps the runtime targets and the live ids, nothing is reapplied: " + string.Join(",", f.Live()));
        // A stop, then the next start applies the file: every live record to the history, fresh ids for everything.
        f.Registry.BeginShutdown(emergency: false, TimeSpan.FromSeconds(1));
        foreach (NodeRegistration row in f.Registry.Nodes().Where(x => x.State == NodeState.Closed)) f.DrainToEmpty(row.Node);
        f.Registry.CompleteShutdown(allowDirty: true);
        f.Reopen();
        // Between the stop and the start's apply the stopped nodes are Empty; the planner must not replace them (bench run 2:
        // the reopened registry planned node-7/node-8 0.2 s before the apply, which sent them straight to the history, and an
        // always-on executor of another host would have launched them). Also across a crash of the new process before the apply.
        f.Pass();
        f.Advance(30);
        f.Reopen();
        f.Pass();
        Check(f.Live().SequenceEqual(["node-1", "node-2", "node-3", "node-4"]) && f.Registry.NodePlan().All(x => NodeControlFixture.Number(x.SlotKey) <= 4),
            "a reopen after a stop plans nothing before the start's apply: " + string.Join(",", f.Live()));
        f.Apply(("polaris", 2, 1));
        Check(f.Live().SequenceEqual(["node-5", "node-6", "node-7"]) && !f.View().DiffersFromFile && f.View().Hosts.Single().Serving.Target == 2,
            "the start's apply plans fresh ids and resets the targets: " + string.Join(",", f.Live()));
        Check(Enumerable.Range(1, 4).All(n => f.History("node-" + n) is { Outcome: "restart", Actor: "start" }), "every node live at the stop is history (restart)");
        Check(f.Registry.NodePlan().Select(x => x.SlotKey).Order().SequenceEqual(["node-5", "node-6", "node-7"]), "the old slots are dropped");
        AssertRegistrationRefused(() => f.Boot("node-1"), "node_id_retired", "an id of the previous run does not come back");
        AssertNodeControlError(() => f.Apply(("polaris", 0, 1)), 400, "invalid_arguments", "a hosts file with no serving node");
        AssertNodeControlError(() => f.Apply(("polaris", 17, 0)), 400, "invalid_arguments", "a count above 16");
        AssertNodeControlError(() => f.Set("ai", serving: 1), 404, "host_not_found", "a host outside the hosts file");
        Console.WriteLine("node-control-hosts-file: fresh ids at every start, targets reset, differsFromFile, a reopen does not reapply");
    }

    // Selectors, resolved on the server: node-N, PID@host (the registration's pid, or the launcher's observation when
    // the node sent none), host#position (oldest process #1, Draining/Stopping included); ambiguous and unknown.
    private static void TestNodeControlSelectors()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("node-control-selectors: " + what); }
        using var f = new NodeControlFixture("selectors");
        AssertNodeControlError(() => f.Registry.ResolveSelector("node-1"), 409, "node_control_inactive", "before node control is active");
        f.Apply(("ai", 1, 0), ("polaris", 3, 0));
        f.Boot("node-2"); f.Advance(30);
        f.Boot("node-3", pid: 5555); f.Advance(30);
        f.Boot("node-4", reportHost: false); f.Boot("node-1", "ai", pid: 5555);
        Check(f.Registry.ResolveSelector("node-3") == "node-3", "by id");
        Check(f.Registry.ResolveSelector("5555@polaris") == "node-3" && f.Registry.ResolveSelector("5555@ai") == "node-1", "PID@host is per host");
        Check(f.Registry.ResolveSelector("5555@POLARIS") == "node-3", "the host is case-insensitive");
        AssertNodeControlError(() => f.Registry.ResolveSelector("7777@polaris"), 404, "selector_not_found", "an unknown pid");
        f.Report("node-4", SpawnState.Ready, pid: 7777);
        Check(f.Node("node-4")!.Pid == 7777 && f.Registry.ResolveSelector("7777@polaris") == "node-4", "the launcher's observation fills a missing pid");
        Check(f.Registry.ResolveSelector("polaris#1") == "node-2" && f.Registry.ResolveSelector("polaris#2") == "node-3"
            && f.Registry.ResolveSelector("polaris#3") == "node-4" && f.Registry.ResolveSelector("ai#1") == "node-1", "host#position: oldest process #1");
        Check(f.View().Nodes.Where(x => x.Host == "polaris").Select(x => x.Position).SequenceEqual([1, 2, 3]), "the list shows the same positions");
        f.Registry.RemoveNode("polaris#1", now: false, timeout: null, "loopback", "viktor", "selectors");
        Check(f.Registry.ResolveSelector("polaris#1") == "node-2" && f.Node("node-2")!.Position == 1, "a draining node keeps its position");
        AssertNodeControlError(() => f.Registry.ResolveSelector("polaris#9"), 404, "selector_not_found", "a position past the end");
        AssertNodeControlError(() => f.Registry.ResolveSelector("polaris#0"), 400, "invalid_arguments", "position 0");
        AssertNodeControlError(() => f.Registry.ResolveSelector("wa-1"), 404, "selector_not_found", "the World Authority is not selectable");
        f.Registry.RegisterNode(Join("wa-1", "127.0.0.1:28500", Guid.NewGuid()) with { Role = NodeRole.WA, Host = "polaris", Pid = 5555 });
        Check(f.Registry.ResolveSelector("5555@polaris") == "node-3", "the World Authority's pid does not make PID@host ambiguous");
        f.Report("node-2", SpawnState.Ready, pid: 5555);
        Check(f.Node("node-2")!.Pid == 40002, "an observation does not override the pid the node reported itself");
        f.Set("polaris", serving: 3);
        f.Boot(f.Live().Last(), pid: 5555);
        AssertNodeControlError(() => f.Registry.ResolveSelector("5555@polaris"), 409, "selector_ambiguous", "two live nodes with one pid on a host");
        AssertNodeControlError(() => f.Registry.ResolveSelector(""), 400, "invalid_arguments", "an empty selector");
        Console.WriteLine("node-control-selectors: node-N, PID@host, host#position; ambiguous 409, unknown 404");
    }

    // Managed deployments: list works read-only from the approved slots, every mutation is 409 managed_inventory_fixed.
    private static void TestNodeControlManaged()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-node-control-managed-" + Guid.NewGuid());
        var deployment = new ManagedDeployment("deployment-1", Guid.NewGuid(),
            [new("regular", "host-1", "regular", "127.0.0.1:28417", NodeRole.Regular),
             new("wa", "host-2", "wa", "127.0.0.1:28418", NodeRole.WA)]);
        try
        {
            using var registry = new GatewayRegistry(root, Admission, managedDeployment: deployment);
            Admin.NodeControlView view = registry.NodeControlView(null, null, "host-0", 1);
            Assert(view.Managed && !view.Active && view.Nodes.Single() is { Node: "regular", Host: "host-1", State: Admin.OperatorNodeState.Starting }
                && view.Hosts.Single(x => x.Host == "host-1").Serving.Target == 1);
            AssertNodeControlError(() => registry.SetHostTargets("host-1", 2, null, "quasar", null, null), 409, "managed_inventory_fixed", "managed set");
            AssertNodeControlError(() => registry.RemoveNode("regular", false, null, "quasar", null, null), 409, "managed_inventory_fixed", "managed remove");
            AssertNodeControlError(() => registry.RecycleNode("regular", false, null, "quasar", null, null), 409, "managed_inventory_fixed", "managed recycle");
            AssertNodeControlError(() => registry.ApplyHostsFile(new Dictionary<string, HostTarget> { ["host-1"] = new(1, 0) }, null, "quasar"),
                409, "managed_inventory_fixed", "managed hosts file");
            Assert(registry.NodeHistoryPage(10, null, null).Records.Length == 0);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>The node-control DTOs on the wire (another agent's Python client reads these names) and the contract lists.</summary>
    private static void TestNodeControlContract()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("admin-contract (node control): " + what); }
        foreach (string operation in new[] { "GET /admin/v1/node-control", "PUT /admin/v1/node-control/hosts/{host}", "POST /admin/v1/node-control/remove",
            "POST /admin/v1/node-control/recycle", "GET /admin/v1/node-control/history", "POST /admin/v1/node-control/hosts-file" })
            Check(Admin.AdminProtocol.Operations.Contains(operation), operation + " is in AdminProtocol.Operations");
        using var f = new NodeControlFixture("contract");
        f.Apply(("polaris", 2, 0));
        f.Boot("node-1"); f.Boot("node-2");
        f.Registry.RemoveNode("node-2", false, null, "loopback", "viktor", "wire");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(f.View(), options));
        JsonElement root = document.RootElement;
        Check(root.GetProperty("revision").ValueKind == JsonValueKind.Number && root.GetProperty("differsFromFile").GetBoolean()
            && root.GetProperty("hostsFile").GetProperty("sha256").GetString() == "sha-test" && root.GetProperty("hostsFile").TryGetProperty("appliedAt", out _),
            "revision, differsFromFile, hostsFile{sha256, appliedAt}");
        JsonElement host = root.GetProperty("hosts")[0];
        Check(host.GetProperty("host").GetString() == "polaris" && host.GetProperty("serving").GetProperty("target").GetInt32() == 1
            && host.GetProperty("serving").GetProperty("file").GetInt32() == 2 && host.GetProperty("serving").GetProperty("actual").GetInt32() == 1
            && host.GetProperty("spares").TryGetProperty("actual", out _) && host.TryGetProperty("ramFloorMb", out _)
            && host.TryGetProperty("diskFloorMb", out _) && host.GetProperty("warnings").ValueKind == JsonValueKind.Array, "HostView");
        JsonElement node = root.GetProperty("nodes").EnumerateArray().Single(x => x.GetProperty("node").GetString() == "node-2");
        foreach (string name in new[] { "node", "host", "position", "pid", "startedAt", "servingSince", "ageSeconds", "state", "partitions", "clients", "stop", "replaces", "replacedBy" })
            Check(node.TryGetProperty(name, out _), "OperatorNode." + name);
        Check(node.GetProperty("state").GetString() == "Draining" && node.GetProperty("stop").GetProperty("kind").GetString() == "remove"
            && node.GetProperty("stop").GetProperty("actor").GetString() == "loopback" && node.GetProperty("stop").GetProperty("why").GetString() == "wire"
            && node.GetProperty("stop").TryGetProperty("deadline", out _) && !node.GetProperty("stop").GetProperty("forced").GetBoolean(),
            "state as a string, stop{kind, actor, why, deadline, forced}");
        JsonElement gateway = root.GetProperty("notCounted").EnumerateArray().Single(x => x.GetProperty("node").GetString() == "gateway");
        Check(gateway.GetProperty("role").GetString() == "Gateway" && gateway.GetProperty("host").GetString() == "polaris" && gateway.TryGetProperty("pid", out _),
            "notCounted[{node, role, host, pid}]");
        Check(JsonSerializer.Deserialize<Admin.NodeStopRequest>("""{"selector":"polaris#2","now":true}""", options) is { Selector: "polaris#2", Now: true, TimeoutSeconds: null },
            "NodeStopRequest: timeoutSeconds is optional (server default)");
        Check(JsonSerializer.Deserialize<Admin.HostsFileApply>("""{"hosts":{"polaris":{"serving":4,"spares":1}},"sha256":"ab","resume":true}""", options)
            is { Sha256: "ab" } apply && apply.Hosts["polaris"] == new Admin.HostTargetSpec(4, 1), "HostsFileApply{hosts{name: HostTarget}, sha256, resume}");
        using JsonDocument history = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(f.Registry.NodeHistoryPage(10, null, null), options));
        Check(history.RootElement.TryGetProperty("records", out _) && history.RootElement.TryGetProperty("nextCursor", out _), "NodeHistoryPage{records, nextCursor}");
    }

    /// <summary>
    /// The gateway's diag fans out to the nodes' own /cluster/diag. It must not query the row of a node that is gone
    /// (Empty or Dead): node ids are never reused but the launcher reuses port slots, so that row's control endpoint can
    /// serve ANOTHER node now, whose section would be reported twice, once under the stopped node's name (bench run 3:
    /// every entity "duplicated on node-1 and node-5" after node-5 took node-1's slot; cluster-status printed the
    /// phantom). A fake node answers on the endpoint as node-5; the positive control proves the endpoint is queried
    /// while the row is Active.
    /// </summary>
    private static void TestDiagSkipsStoppedRows()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("diag-stopped-rows: " + what); }
        string directory = Path.Combine(Path.GetTempPath(), "cluster-diag-stopped-" + Guid.NewGuid());
        int controlPort = FreeTcpPort(), nodePort = FreeTcpPort();
        int diagHits = 0;
        using var fakeNode = new HttpListener();
        fakeNode.Prefixes.Add($"http://127.0.0.1:{nodePort}/cluster/");
        fakeNode.Start();
        var nodeThread = new Thread(() =>
        {
            while (fakeNode.IsListening)
            {
                HttpListenerContext context;
                try { context = fakeNode.GetContext(); } catch (Exception) { return; }
                bool diag = context.Request.Url!.AbsolutePath.EndsWith("/cluster/diag", StringComparison.Ordinal);
                if (diag) Interlocked.Increment(ref diagHits);
                byte[] body = System.Text.Encoding.UTF8.GetBytes(diag ? """{"nodeId":"node-5","partitions":[{"partitionId":7}]}""" : "{}");
                context.Response.StatusCode = diag ? 200 : 404;
                context.Response.ContentType = "application/json";
                context.Response.OutputStream.Write(body);
                context.Response.Close();
            }
        }) { IsBackground = true, Name = "Diag-Fake-Node" };
        nodeThread.Start();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, controlPort),
            false, null, null, null, directory, TimeSpan.FromSeconds(10), Admission, null, []);
        var gateway = new DirectGateway(options);
        var thread = new Thread(() =>
        {
            try { gateway.Run(); }
            catch (Exception e) { Console.Error.WriteLine("diag gateway failed: " + e); }
        }) { IsBackground = true, Name = "Gateway-Diag-Self-Test" };
        thread.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{controlPort}"), Timeout = TimeSpan.FromSeconds(10) };
            Stopwatch waited = Stopwatch.StartNew();
            while (true)
            {
                try { using HttpResponseMessage probe = http.GetAsync("/admin/v1/health").GetAwaiter().GetResult(); if (probe.IsSuccessStatusCode) break; }
                catch (HttpRequestException) when (waited.Elapsed < TimeSpan.FromSeconds(10)) { }
                Thread.Sleep(50);
            }
            string Post(string path, string? body = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path);
                if (body != null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                using HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult();
                string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(response.IsSuccessStatusCode, $"POST {path}: {(int)response.StatusCode} {text}");
                return text;
            }
            JsonElement Node1()
            {
                using JsonDocument diag = JsonDocument.Parse(http.GetStringAsync("/diag?depth=partitions").GetAwaiter().GetResult());
                return diag.RootElement.GetProperty("nodes").EnumerateArray().Single(x => x.GetProperty("nodeId").GetString() == "node-1").Clone();
            }
            NodeJoinRequest join = Join("node-1", "127.0.0.1:27101", Guid.NewGuid()) with { ControlEndpoint = $"127.0.0.1:{nodePort}" };
            using JsonDocument registered = JsonDocument.Parse(Post("/registry/nodes", JsonSerializer.Serialize(join)));
            long epoch = registered.RootElement.GetProperty("Epoch").GetInt64();
            JsonElement active = Node1();
            Check(Volatile.Read(ref diagHits) == 1 && active.GetProperty("section").ValueKind == JsonValueKind.Object,
                "control: an Active row's endpoint is queried: hits " + diagHits + ", " + active);
            Post($"/registry/nodes/close?node=node-1&epoch={epoch}&forceAfterSeconds=60");
            Post($"/registry/nodes/empty?node=node-1&epoch={epoch}");
            JsonElement empty = Node1();
            Check(empty.GetProperty("state").GetString() == "Empty", "node-1 is Empty: " + empty);
            Check(empty.GetProperty("section").ValueKind == JsonValueKind.Null && empty.GetProperty("stale").GetBoolean()
                && Volatile.Read(ref diagHits) == 1,
                "an Empty row is listed stale and its endpoint (now another node's) is not queried: hits " + diagHits + ", " + empty);
            Console.WriteLine("diag-stopped-rows: an Active row is queried, an Empty row is listed stale without a query");
        }
        finally
        {
            gateway.Stop();
            fakeNode.Stop();
            try { System.IO.Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    /// <summary>The routes over HTTP: Idempotency-Key required, idempotent replay (no second effect), key reuse 409,
    /// selector 404 with no operation recorded, the feature flag, and the history page.</summary>
    private static void TestNodeControlAdminRoutes()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("admin-operations (node control): " + what); }
        string directory = Path.Combine(Path.GetTempPath(), "cluster-node-control-http-" + Guid.NewGuid());
        int controlPort = FreeTcpPort();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, controlPort),
            false, null, null, null, directory, TimeSpan.FromSeconds(10), Admission, null, []);
        var gateway = new DirectGateway(options);
        var thread = new Thread(() =>
        {
            try { gateway.Run(); }
            catch (Exception e) { Console.Error.WriteLine("node-control gateway failed: " + e); }
        }) { IsBackground = true, Name = "Gateway-NodeControl-Self-Test" };
        thread.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{controlPort}"), Timeout = TimeSpan.FromSeconds(10) };
            Stopwatch waited = Stopwatch.StartNew();
            while (true)
            {
                try { using HttpResponseMessage probe = http.GetAsync("/admin/v1/health").GetAwaiter().GetResult(); if (probe.IsSuccessStatusCode) break; }
                catch (HttpRequestException) when (waited.Elapsed < TimeSpan.FromSeconds(10)) { }
                Thread.Sleep(50);
            }
            (int Status, JsonElement Body) Send(HttpMethod method, string path, string? body = null, string? key = null)
            {
                using var request = new HttpRequestMessage(method, path);
                if (body != null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                if (key != null) request.Headers.Add("Idempotency-Key", key);
                using HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult();
                using JsonDocument parsed = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                return ((int)response.StatusCode, parsed.RootElement.Clone());
            }
            JsonElement capabilities = Send(HttpMethod.Get, "/admin/v1/capabilities").Body.GetProperty("data");
            Check(capabilities.GetProperty("features").GetProperty("nodeControl").GetString() == "v1", "features.nodeControl = v1");
            Check(capabilities.GetProperty("features").GetProperty("clientDiagnostics").GetString() == "v1", "client diagnostics capability");
            string publication = JsonSerializer.Serialize(new Admin.RotationPublication(0, "manager", DateTimeOffset.UtcNow.AddMinutes(1), false, []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Check(Send(HttpMethod.Put, "/admin/v1/rotation", publication).Status == 400, "rotation needs an idempotency key");
            var published = Send(HttpMethod.Put, "/admin/v1/rotation", publication, "rotation-1");
            Check(published.Status == 200 && published.Body.GetProperty("data").GetProperty("state").GetString() == "Succeeded", "rotation publication over HTTP");
            var republished = Send(HttpMethod.Put, "/admin/v1/rotation", publication, "rotation-1");
            Check(republished.Body.GetProperty("data").GetProperty("operationId").GetString() == published.Body.GetProperty("data").GetProperty("operationId").GetString(), "rotation replay returns same operation");
            Check(Send(HttpMethod.Get, "/admin/v1/rotation").Body.GetProperty("data").GetProperty("revision").GetInt64() == 1, "rotation publication applied once");

            Check(Send(HttpMethod.Get, "/admin/v1/node-control").Body.GetProperty("data").GetProperty("active").GetBoolean() == false, "inactive before any apply");
            string apply = """{"hosts":{"polaris":{"serving":2,"spares":1}},"sha256":"f00d"}""";
            Check(Send(HttpMethod.Post, "/admin/v1/node-control/hosts-file", apply).Status == 400, "a mutation without Idempotency-Key is 400");
            (int status, JsonElement applied) = Send(HttpMethod.Post, "/admin/v1/node-control/hosts-file", apply, "k-apply");
            Check(status == 200 && applied.GetProperty("data").GetProperty("state").GetString() == "Succeeded"
                && applied.GetProperty("data").GetProperty("result").GetProperty("revision").GetString() == "1", "the apply finishes at once: " + applied);
            string[] actions = JsonSerializer.Deserialize<string[]>(applied.GetProperty("data").GetProperty("result").GetProperty("actions").GetString()!)!;
            Check(actions.Length == 3 && actions[0].Contains("planned serving node-1"), "result.actions is a JSON array: " + string.Join(" | ", actions));
            (int replayStatus, JsonElement replay) = Send(HttpMethod.Post, "/admin/v1/node-control/hosts-file", apply, "k-apply");
            JsonElement view = Send(HttpMethod.Get, "/admin/v1/node-control").Body.GetProperty("data");
            Check(replayStatus == 200 && replay.GetProperty("data").GetProperty("operationId").GetString() == applied.GetProperty("data").GetProperty("operationId").GetString()
                && view.GetProperty("revision").GetInt64() == 1 && view.GetProperty("nodes").GetArrayLength() == 3, "the replay returns the same operation and applies nothing twice");
            Check(Send(HttpMethod.Post, "/admin/v1/node-control/hosts-file", """{"hosts":{"polaris":{"serving":3,"spares":1}}}""", "k-apply").Body
                .GetProperty("error").GetProperty("code").GetString() == "idempotency_key_reuse", "a reused key with another body is 409 idempotency_key_reuse");
            (int setStatus, JsonElement set) = Send(HttpMethod.Put, "/admin/v1/node-control/hosts/polaris", """{"serving":3,"requestedBy":"viktor@polaris"}""", "k-set");
            Check(setStatus == 200 && set.GetProperty("data").GetProperty("result").GetProperty("revision").GetString() == "2", "PUT hosts/{host}: " + set);
            (int missing, JsonElement notFound) = Send(HttpMethod.Post, "/admin/v1/node-control/remove", """{"selector":"polaris#9"}""", "k-missing");
            Check(missing == 404 && notFound.GetProperty("error").GetProperty("code").GetString() == "selector_not_found", "an unknown selector is 404 selector_not_found");
            Check(!Send(HttpMethod.Get, "/admin/v1/operations").Body.GetProperty("data").GetProperty("operations").EnumerateArray()
                .Any(x => x.GetProperty("idempotencyKey").GetString() == "k-missing"), "a refused request records no operation");
            Check(Send(HttpMethod.Put, "/admin/v1/node-control/hosts/ai", """{"serving":1}""", "k-ai").Status == 404, "a host outside the file is 404");
            (int removeStatus, JsonElement removed) = Send(HttpMethod.Post, "/admin/v1/node-control/remove", """{"selector":"node-3","now":true,"reason":"http"}""", "k-remove");
            Check(removeStatus == 200 && removed.GetProperty("data").GetProperty("result").GetProperty("node").GetString() == "node-3"
                && removed.GetProperty("data").GetProperty("target").GetString() == "node-3", "remove answers the resolved node: " + removed);
            JsonElement page = Send(HttpMethod.Get, "/admin/v1/node-control/history?limit=5&host=polaris").Body.GetProperty("data");
            Check(page.GetProperty("records").EnumerateArray().Any(x => x.GetProperty("node").GetString() == "node-3" && x.GetProperty("why").GetString() == "http"),
                "the never-started node-3 is in the history page: " + page);
        }
        finally
        {
            gateway.Stop();
            thread.Join(2000);
            gateway.Dispose();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
