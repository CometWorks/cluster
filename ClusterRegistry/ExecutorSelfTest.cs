using PluginSdk.Clustering;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    private static void TestExecutorContract()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-executor-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            Guid lease;
            var request = new Admin.ExecutorPollRequest("host-1", "executor-1", Guid.NewGuid(), null, 1, []);
            using (var registry = new GatewayRegistry(root, Admission, () => now))
            {
                registry.PutNodeSlot("slot-1", "host-1", NodeRole.Regular, NodeDesiredState.Wanted);
                registry.PutNodeSlot("slot-2", "host-2", NodeRole.Regular, NodeDesiredState.Wanted);
                NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
                var acquired = registry.PollExecutor(request);
                lease = acquired.Lease;
                Assert(acquired.Plan.Length == 1 && acquired.Plan[0].SlotKey == "slot-1");
                Assert(registry.PollExecutor(request).Lease == lease);
                AssertConflict(() => registry.PollExecutor(request with { Session = Guid.NewGuid() }));
                request = request with { Lease = lease, Sequence = 2,
                    Reports = [new("slot-1", "attempt-1", Admin.NodeObservation.Ready, node.Node, node.Epoch, null)] };
                registry.PollExecutor(request);
                long before = registry.StoreCommits;
                registry.ClaimPartition(1, node.Node, node.Epoch, 0);
                ReportCharacterHome(registry, 1, 1, 1, 1, node.Node); // Unrelated coalesced mutation.
                registry.PollExecutor(request with { Sequence = 3 });
                if (GatewayRegistry.PersistInterval > TimeSpan.Zero)
                    Assert(registry.StoreCommits == before);
                request = request with { Sequence = 3 };
                registry.PollExecutor(request); // Exact response-loss replay.
                AssertConflict(() => registry.PollExecutor(request with { Reports = [] }));
                AssertConflict(() => registry.PollExecutor(request with { Sequence = 4,
                    Reports = [request.Reports[0] with { SlotKey = "slot-2" }] }));
                registry.AdminKillNode("slot-1", node.Node, node.Epoch);
                AssertConflict(() => registry.PollExecutor(request with { Sequence = 4,
                    Reports = [request.Reports[0] with { State = Admin.NodeObservation.Gone, Epoch = node.Epoch + 1 }] }));
                Assert(registry.AdminNodePlan().Single(slot => slot.SlotKey == "slot-1").IncumbentAction == Admin.IncumbentAction.Kill);
                registry.PollExecutor(request with { Sequence = 4,
                    Reports = [request.Reports[0] with { State = Admin.NodeObservation.Gone }] });
                now = now.AddSeconds(Admin.ExecutorProtocol.LeaseSeconds + 1);
                AssertConflict(() => registry.PollExecutor(request with { Sequence = 5, Reports = [] }));
            }
            using var recovered = new GatewayRegistry(root, Admission, () => now);
            AssertConflict(() => recovered.PollExecutor(request with { Sequence = 5, Reports = [] }));
            var next = recovered.PollExecutor(request with { Lease = null, Sequence = 5, Reports = [] });
            Assert(next.Lease != lease && next.Plan.Single().IncumbentAction == Admin.IncumbentAction.None);
        }
        finally { Directory.Delete(root, true); }
    }
    // #1452: the Host reads a new process's receipt during its reconcile and sends the report one poll later. A node
    // that registers in between is still reported as Spawning without an epoch. That report repeats what the slot
    // already observes, so it must not 409 (which drops the Host's lease and batch); an epoch-less report that would
    // change what the slot observes of a live incarnation is still refused.
    private static void TestExecutorSpawningBeforeReceipt()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-executor-receipt-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            using var registry = new GatewayRegistry(root, Admission, () => now);
            registry.PutNodeSlot("slot-1", "host-1", NodeRole.Regular, NodeDesiredState.Wanted);
            var request = new Admin.ExecutorPollRequest("host-1", "executor-1", Guid.NewGuid(), null, 1, []);
            request = request with { Lease = registry.PollExecutor(request).Lease, Sequence = 2,
                Reports = [new("slot-1", "attempt-1", Admin.NodeObservation.Spawning, "node-1", 0, null)] };
            registry.PollExecutor(request);
            NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
            registry.PollExecutor(request with { Sequence = 3 });
            Assert(registry.NodePlan().Single().Observed == SpawnState.Spawning);
            AssertConflict(() => registry.PollExecutor(request with { Sequence = 4,
                Reports = [request.Reports[0] with { State = Admin.NodeObservation.Failed, Failure = "x" }] }));
            request = request with { Lease = registry.PollExecutor(request with { Lease = null, Sequence = 4, Reports = [] }).Lease,
                Sequence = 5, Reports = [request.Reports[0] with { State = Admin.NodeObservation.Ready, Epoch = node.Epoch }] };
            registry.PollExecutor(request);
            AssertConflict(() => registry.PollExecutor(request with { Sequence = 6,
                Reports = [request.Reports[0] with { State = Admin.NodeObservation.Spawning, Epoch = 0 }] }));
        }
        finally { Directory.Delete(root, true); }
    }
    private static void TestManagedDeployment()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-managed-" + Guid.NewGuid());
        var deployment = new ManagedDeployment("deployment-1", Guid.NewGuid(),
            [new("regular", "host-1", "regular", "127.0.0.1:28417", NodeRole.Regular),
             new("wa", "host-2", "wa", "127.0.0.1:28418", NodeRole.WA)]);
        try
        {
            using (var registry = new GatewayRegistry(root, Admission, managedDeployment: deployment))
            {
                Assert(!registry.AdmissionOpen && registry.NodePlan().Length == 2);
                AssertConflict(registry.EnsureLegacyExecutorAllowed);
                AssertConflict(() => registry.ExecutorHeartbeat("legacy", []));
                var regular = Register(registry, "regular", "127.0.0.1:28417", Guid.NewGuid());
                Assert(registry.Lifecycle().Phase == ClusterPhase.Bootstrapping);
                var wa = registry.RegisterNode(Join("wa", "127.0.0.1:28418", Guid.NewGuid()) with { Role = NodeRole.WA });
                foreach (var (host, node) in new[] { ("host-1", regular), ("host-2", wa) })
                {
                    var request = new Admin.ExecutorPollRequest(host, host, Guid.NewGuid(), null, 1, [], deployment.Revision);
                    var acquired = registry.PollExecutor(request);
                    request = request with { Lease = acquired.Lease, Sequence = 2,
                        Reports = [new(node.Node, "attempt", Admin.NodeObservation.Ready, node.Node, node.Epoch, null)] };
                    AssertConflict(() => registry.PollExecutor(request with { DeploymentRevision = "wrong" }));
                    registry.PollExecutor(request);
                    Assert(!registry.AdmissionOpen); // Host observations cannot grant runtime admission.
                    registry.Heartbeat(node.Node, node.Epoch, [], serving: true,
                        deploymentRevision: deployment.Revision, configurationVerified: true);
                    if (host == "host-1") Assert(!registry.AdmissionOpen);
                }
                Assert(registry.AdmissionOpen && registry.Lifecycle().Phase == ClusterPhase.Serving);
                registry.Flush();
            }
            using (var registry = new GatewayRegistry(root, Admission, managedDeployment: deployment))
            {
                Assert(!registry.AdmissionOpen && registry.Nodes().All(n => !n.Serving));
                // Surviving runtime nodes restore admission without an executor or Agent.
                foreach (var node in registry.Nodes())
                    registry.Heartbeat(node.Node, node.Epoch, [], serving: true,
                        deploymentRevision: deployment.Revision, configurationVerified: true);
                Assert(registry.AdmissionOpen);
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                registry.Flush();
            }
            using (var recovered = new GatewayRegistry(root, Admission, managedDeployment: deployment))
            {
                Assert(!recovered.AdmissionOpen && recovered.Lifecycle().Phase == ClusterPhase.Draining);
                Assert(recovered.NodePlan().All(slot => slot.Desired == NodeDesiredState.Draining));
                var authority = recovered.Nodes().Single(node => node.Role == NodeRole.WA);
                recovered.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch,
                    SaveKind.Final, 1, "final", 1, "wa/final");
                foreach (var node in recovered.Nodes()) recovered.MarkEmpty(node.Node, node.Epoch);
                recovered.CompleteShutdown(false);
            }
            using (var down = new GatewayRegistry(root, Admission, managedDeployment: deployment))
                Assert(down.Lifecycle().Phase == ClusterPhase.Down && !down.AdmissionOpen);
            using var warm = new GatewayRegistry(root, Admission,
                managedDeployment: deployment with { StartGeneration = Guid.NewGuid() });
            Assert(warm.Lifecycle().Phase == ClusterPhase.Bootstrapping && !warm.AdmissionOpen);
            Assert(warm.NodePlan().All(slot => slot.Desired == NodeDesiredState.Wanted));
        }
        finally { Directory.Delete(root, true); }
    }
    // #1195: a managed spec designates hot spare slots. They register holding nothing, a spare replaces a dead
    // node and recovers its partitions, the dead node's slot comes back as the spare (the slot pool is fixed),
    // node control reports the split, and every new generation starts from the approved designation again.
    private static void TestManagedHotSpares()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-managed-spares-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var deployment = new ManagedDeployment("deployment-1", Guid.NewGuid(),
            [new("r1", "host-1", "r1", "127.0.0.1:28417", NodeRole.Regular),
             new("r2", "host-1", "r2", "127.0.0.1:28418", NodeRole.Regular),
             new("s1", "host-1", "s1", "127.0.0.1:28419", NodeRole.Regular, Spare: true),
             new("wa", "host-1", "wa", "127.0.0.1:28420", NodeRole.WA)]);
        try
        {
            foreach (var invalid in new[] {
                deployment with { Slots = [.. deployment.Slots.Select(slot => slot with { Spare = true })] },
                deployment with { Slots = [.. deployment.Slots.Select(slot => slot with { Spare = slot.Role == NodeRole.Regular })] } })
            {
                string bad = Path.Combine(root, "invalid-" + Guid.NewGuid());
                try { using var refused = new GatewayRegistry(bad, Admission, managedDeployment: invalid); Assert(false); }
                catch (InvalidDataException) { }
            }
            using (var registry = new GatewayRegistry(root, Admission, () => now, TimeSpan.FromSeconds(10), managedDeployment: deployment))
            {
                NodeRegistration r1 = Register(registry, "r1", "127.0.0.1:28417", Guid.NewGuid());
                NodeRegistration r2 = Register(registry, "r2", "127.0.0.1:28418", Guid.NewGuid());
                NodeRegistration s1 = Register(registry, "s1", "127.0.0.1:28419", Guid.NewGuid());
                var wa = registry.RegisterNode(Join("wa", "127.0.0.1:28420", Guid.NewGuid()) with { Role = NodeRole.WA });
                Assert(!r1.Spare && !r2.Spare && s1.Spare && s1.Activated == null);
                foreach (var node in new[] { r1, r2, s1, wa })
                    registry.Heartbeat(node.Node, node.Epoch, [], serving: true,
                        deploymentRevision: deployment.Revision, configurationVerified: true);
                Assert(registry.AdmissionOpen);
                Assert(registry.AdminNodes().Single(x => x.Node == "s1").Spare && !registry.AdminNodes().Single(x => x.Node == "r1").Spare);
                Admin.NodeControlView view = registry.NodeControlView(null, null, "host-1", 1);
                Admin.HostView host = view.Hosts.Single(x => x.Host == "host-1");
                Assert(view.Managed && host.Serving.Target == 2 && host.Spares.Target == 1 && host.Spares.Actual == 1
                    && view.Nodes.Single(x => x.Node == "s1").State == Admin.OperatorNodeState.Spare);
                Assert(registry.ClaimRecoveries(s1.Node, s1.Epoch, []).Length == 0);
                foreach (ulong partition in new ulong[] { 1, 2 })
                {
                    PartitionOwnership owned = registry.ClaimPartition(partition, r1.Node, r1.Epoch, 0);
                    registry.RegisterSave(new SaveId(partition, owned.Generation, 1), r1.Node, r1.Epoch, SaveKind.Periodic, 1,
                        "c" + partition, 1, "r1/" + partition, entities: 3);
                }
                // r1 dies; the others keep heartbeating.
                now += TimeSpan.FromSeconds(6);
                foreach (var node in new[] { r2, s1, wa }) registry.Heartbeat(node.Node, node.Epoch, [], serving: true,
                    deploymentRevision: deployment.Revision, configurationVerified: true);
                now += TimeSpan.FromSeconds(5);
                Assert(registry.ExpireLeases() == 2);
                NodeRegistration promoted = registry.Nodes().Single(x => x.Node == "s1");
                Assert(!promoted.Spare && promoted.Activated == now);
                Assert(registry.ClaimRecoveries(r2.Node, r2.Epoch, []).Length == 0);
                Assert(registry.ClaimRecoveries(s1.Node, s1.Epoch, []).Select(x => x.Ownership.Partition).Order().SequenceEqual([1UL, 2UL]));
                // The dead slot is the spare now: its next incarnation registers holding nothing.
                NodeRegistration reborn = Register(registry, "r1", "127.0.0.1:28417", Guid.NewGuid());
                Assert(reborn.Spare && reborn.Activated == null && reborn.Epoch == r1.Epoch + 1);
                Assert(registry.ClaimRecoveries(reborn.Node, reborn.Epoch, []).Length == 0);
                host = registry.NodeControlView(null, null, "host-1", 1).Hosts.Single(x => x.Host == "host-1");
                Assert(host.Serving.Target == 2 && host.Spares.Target == 1);
                // A crashed slot that Host relaunches before its lease ran out: the new process registers while the old
                // incarnation still owns its partitions. The spare (r1 now) takes them over and r2 comes back as the spare.
                foreach (ulong partition in new ulong[] { 5, 6 })
                {
                    PartitionOwnership owned = registry.ClaimPartition(partition, r2.Node, r2.Epoch, 0);
                    registry.RegisterSave(new SaveId(partition, owned.Generation, 1), r2.Node, r2.Epoch, SaveKind.Periodic, 1,
                        "c" + partition, 1, "r2/" + partition, entities: 3);
                }
                NodeRegistration restarted = Register(registry, "r2", "127.0.0.1:28418", Guid.NewGuid());
                Assert(restarted.Spare && restarted.Epoch == r2.Epoch + 1 && !registry.Nodes().Single(x => x.Node == "r1").Spare);
                // #1206: a survivor's claim poll lands before the next lease-expiry pass. Its reservation prune must not
                // read the dead incarnation's rows as recovered and drop them: on sirius the survivor took one of two.
                Assert(registry.ClaimRecoveries(s1.Node, s1.Epoch, []).Length == 0);
                registry.ExpireLeases();
                Assert(registry.ClaimRecoveries(s1.Node, s1.Epoch, []).Length == 0);
                Assert(registry.ClaimRecoveries(reborn.Node, reborn.Epoch, []).Select(x => x.Ownership.Partition).Order().SequenceEqual([5UL, 6UL]));
                host = registry.NodeControlView(null, null, "host-1", 1).Hosts.Single(x => x.Host == "host-1");
                Assert(host.Serving.Target == 2 && host.Spares.Target == 1);
                foreach (ulong partition in new ulong[] { 5, 6 })
                    registry.ReleasePartition(partition, reborn.Node, reborn.Epoch, registry.GetPartitionOwner(partition).Generation);
                foreach (ulong partition in new ulong[] { 1, 2 })
                    registry.ReleasePartition(partition, s1.Node, s1.Epoch, registry.GetPartitionOwner(partition).Generation);
                registry.Flush();
            }
            using (var replay = new GatewayRegistry(root, Admission, () => now, TimeSpan.FromSeconds(10), managedDeployment: deployment))
                Assert(replay.Nodes().Single(x => x.Node == "r2").Spare && !replay.Nodes().Single(x => x.Node == "r1").Spare
                    && !replay.Nodes().Single(x => x.Node == "s1").Spare);
            // A new generation after a clean Down starts from the approved designation again.
            using (var registry = new GatewayRegistry(root, Admission, () => now, TimeSpan.FromSeconds(10), managedDeployment: deployment))
            {
                foreach (var node in registry.Nodes().Where(x => x.State == NodeState.Active))
                    registry.Heartbeat(node.Node, node.Epoch, [], serving: true,
                        deploymentRevision: deployment.Revision, configurationVerified: true);
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                var authority = registry.Nodes().Single(node => node.Role == NodeRole.WA);
                registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch,
                    SaveKind.Final, 1, "final", 1, "wa/final");
                foreach (var node in registry.Nodes().Where(x => x.State is NodeState.Active or NodeState.Closed))
                    registry.MarkEmpty(node.Node, node.Epoch);
                registry.CompleteShutdown(false);
            }
            using var warm = new GatewayRegistry(root, Admission, () => now, TimeSpan.FromSeconds(10),
                managedDeployment: deployment with { StartGeneration = Guid.NewGuid() });
            Assert(warm.Lifecycle().Phase == ClusterPhase.Bootstrapping);
            Assert(Register(warm, "s1", "127.0.0.1:28419", Guid.NewGuid()).Spare && !Register(warm, "r1", "127.0.0.1:28417", Guid.NewGuid()).Spare);
        }
        finally { Directory.Delete(root, true); }
    }
    /// <summary>
    /// SE-1168: a managed deployment rotates its regular nodes on rotation.lifetimeSeconds (400 s here: one recycle per
    /// 100 s over four serving nodes). Each recycle activates the spare, drains the oldest node into it (scale-in retiring,
    /// the spare as its drain receiver), and restarts the drained slot as the new spare: Empty plans the kill, Gone lets
    /// the still-wanted slot respawn, and the new incarnation registers holding nothing. Every regular node is recycled in
    /// turn, the counts never change, one recycle runs at a time, a stuck drain is closed after the drain timeout, 0
    /// switches it off, and a gateway restart keeps the recycle in flight. Red before SE-1168: nothing ever recycles.
    /// </summary>
    private static void TestManagedRotation()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("managed-rotation: " + what); }
        string root = Path.Combine(Path.GetTempPath(), "cluster-managed-rotation-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        string[] regulars = ["r1", "r2", "r3", "r4", "s1"];
        var deployment = new ManagedDeployment("deployment-1", Guid.NewGuid(),
            [.. regulars.Select((node, i) => new ManagedSlot(node, "host-1", node, "127.0.0.1:" + (28430 + i), NodeRole.Regular, Spare: node == "s1")),
             new("wa", "host-1", "wa", "127.0.0.1:28439", NodeRole.WA)]);
        GatewayRegistry? registry = null;
        try
        {
            registry = new GatewayRegistry(root, Admission, () => now, TimeSpan.FromSeconds(10), managedDeployment: deployment);
            var request = new Admin.ExecutorPollRequest("host-1", "executor-1", Guid.NewGuid(), null, 1, []) { DeploymentRevision = deployment.Revision };
            request = request with { Lease = registry.PollExecutor(request).Lease };
            foreach (string node in regulars) Register(registry, node, "127.0.0.1:" + (28430 + Array.IndexOf(regulars, node)), Guid.NewGuid());
            registry.RegisterNode(Join("wa", "127.0.0.1:28439", Guid.NewGuid()) with { Role = NodeRole.WA });
            NodeRegistration Live(string node) => registry!.Nodes().Single(x => x.Node == node);
            void Beat()
            {
                foreach (NodeRegistration node in registry!.Nodes().Where(x => x.State is NodeState.Active or NodeState.Closed))
                    registry.Heartbeat(node.Node, node.Epoch, [], serving: true, deploymentRevision: deployment.Revision, configurationVerified: true);
            }
            // One second at a time, as the lease thread runs the pass; stops at the second a recycle starts.
            string? Tick(int seconds)
            {
                for (int second = 0; second < seconds; second++)
                {
                    now = now.AddSeconds(1);
                    Beat();
                    if (second % 5 == 0)
                    {
                        request = request with { Sequence = request.Sequence + 1, Reports = [] };
                        registry!.PollExecutor(request);
                    }
                    bool idle = registry!.RetiringNodes().Length == 0;
                    registry.NodeControlPass();
                    if (idle && registry.RetiringNodes() is [string started]) return started;
                }
                return null;
            }
            void Own(ulong partition, string node)
            {
                NodeRegistration owner = Live(node);
                PartitionOwnership owned = registry!.ClaimPartition(partition, node, owner.Epoch,
                    registry.Partitions().FirstOrDefault(x => x.Partition == partition)?.Generation ?? 0);
                registry.RegisterSave(new SaveId(partition, owned.Generation, 1), node, owner.Epoch, SaveKind.Periodic, 1,
                    "c" + partition, 1, node + "/" + partition, entities: 3);
            }
            (int Serving, int Spares) Counts() => (registry!.Nodes().Count(x => x.Role == NodeRole.Regular && x.State == NodeState.Active && !x.Spare),
                registry.Nodes().Count(x => x.Role == NodeRole.Regular && x.State == NodeState.Active && x.Spare));
            // The drain of a recycled node as ScaleInPass and the node carry it out: its partitions move to the receiver,
            // it is closed and reports Empty; Host kills it, reports Gone, and the respawned slot registers.
            void Drain(string node, string receiver, bool closed = false)
            {
                NodeRegistration from = Live(node), to = Live(receiver);
                foreach (ulong partition in registry!.Partitions().Where(x => x.Node == node).Select(x => x.Partition).ToArray())
                {
                    registry.ReleasePartition(partition, node, from.Epoch, registry.GetPartitionOwner(partition).Generation);
                    Own(partition, receiver);
                }
                if (!closed) registry.CloseNode(node, from.Epoch, TimeSpan.FromSeconds(60));
                registry.MarkEmpty(node, from.Epoch);
                Check(registry.AdminNodePlan().Single(x => x.SlotKey == node).IncumbentAction == Admin.IncumbentAction.Kill, node + ": Empty plans the kill");
                Check(Tick(1) == null && Live(node).Spare, node + ": the drained slot is the spare now");
                request = request with { Sequence = request.Sequence + 1,
                    Reports = [new(node, "attempt-" + node + from.Epoch, Admin.NodeObservation.Gone, node, from.Epoch, null)] };
                Admin.NodePlan slot = registry.PollExecutor(request).Plan.Single(x => x.SlotKey == node);
                Check(slot.IncumbentAction == Admin.IncumbentAction.None && slot.SpawnAllowed, node + ": Gone lets the slot respawn");
                NodeRegistration reborn = Register(registry, node, from.Endpoint, Guid.NewGuid());
                Check(reborn.Spare && reborn.Epoch == from.Epoch + 1, node + ": the new incarnation registers as the spare");
                Check(Tick(1) == null && registry.RetiringNodes().Length == 0, node + ": the recycle is done");
            }

            Beat();
            Check(registry.Lifecycle().Phase == ClusterPhase.Serving, "the cluster serves");
            foreach ((ulong partition, string node) in new[] { (1UL, "r1"), (2UL, "r1"), (3UL, "r2"), (4UL, "r3"), (5UL, "r4") })
                Own(partition, node);
            registry.SetHandoverConfig(new Dictionary<string, string?> { [GatewayRegistry.RotationLifetimeKey] = "0" });
            Check(Tick(500) == null, "lifetime 0 never recycles");
            Check(registry.AdminNodes().All(x => x.RotationDue == null), "lifetime 0 estimates no rotation");
            registry.SetHandoverConfig(new Dictionary<string, string?>
                { [GatewayRegistry.RotationLifetimeKey] = "400", [GatewayRegistry.RotationDrainTimeoutKey] = "120" });

            List<(string Node, DateTimeOffset At)> recycled = [];
            string spare = "s1";
            for (int round = 0; round < 5; round++)
            {
                // SE-1241: the operator view's estimate is when the recycle happens, oldest first; the spare has no age.
                Admin.NodeStatus[] estimate = registry.AdminNodes();
                Admin.NodeStatus first = estimate.Where(x => x.RotationDue != null).MinBy(x => x.RotationDue)!;
                Check(estimate.Count(x => x.RotationDue != null && x.ServingSince != null) == 4
                    && estimate.Single(x => x.Node == spare) is { ServingSince: null, RotationDue: null },
                    $"round {round}: the four serving nodes have an age and a rotation estimate, the spare neither");
                string? node = Tick(200);
                Check(node != null, $"round {round}: a node is recycled within 200 s");
                Check(node == first.Node && now >= first.RotationDue && now - first.RotationDue <= TimeSpan.FromSeconds(1),
                    $"round {round}: {node} recycled at {now}, estimated {first.Node} at {first.RotationDue}");
                recycled.Add((node!, now));
                Check(!Live(spare).Spare && Live(spare).Activated == now, $"{node}: the spare {spare} is activated");
                Check(registry.PreferredDrainReceiver(node!) == spare, $"{node}: drains into {spare}");
                Check(Counts() == (5, 0) && registry.AdminNodePlan().Single(x => x.SlotKey == node).IncumbentAction == Admin.IncumbentAction.Draining,
                    $"{node}: the spare serves beside the draining node, whose slot drains its incumbent");
                Check(Tick(110) == null && registry.RetiringNodes().SequenceEqual([node!]) && Live(node!).State == NodeState.Active,
                    $"{node}: one recycle at a time, still draining before the timeout");
                if (round == 3)
                {
                    // Past the 120 s drain timeout: closed, its own drain hands out the rest.
                    Check(Tick(10) == null && Live(node!).State == NodeState.Closed && Live(node!).ForceDrainAt == now, $"{node}: a stuck drain is closed");
                }
                else if (round == 4)
                {
                    // A gateway restart in the middle of a recycle keeps it.
                    registry.Flush();
                    registry.Dispose();
                    registry = new GatewayRegistry(root, Admission, () => now, TimeSpan.FromSeconds(10), managedDeployment: deployment);
                    request = request with { Lease = null, Sequence = request.Sequence + 1, Reports = [] };
                    request = request with { Lease = registry.PollExecutor(request).Lease };
                    Check(registry.RetiringNodes().SequenceEqual([node!]) && registry.PreferredDrainReceiver(node!) == spare,
                        "a gateway restart keeps the recycle in flight");
                }
                Drain(node!, spare, closed: round == 3);
                Check(Counts() == (4, 1), $"{node}: four serve and one spare after the recycle");
                spare = node!;
            }
            Check(recycled.Select(x => x.Node).SequenceEqual(["r1", "r2", "r3", "r4", "s1"]),
                "every node is recycled in turn, oldest first: " + string.Join(",", recycled.Select(x => x.Node)));
            Check(recycled[0].At == DateTimeOffset.UnixEpoch.AddSeconds(501), "the first as soon as rotation is on (r1 has served 500 s): " + recycled[0].At);
            Check(recycled.Zip(recycled.Skip(1)).All(x => x.Second.At - x.First.At >= TimeSpan.FromSeconds(100)),
                "at most one recycle per lifetime / 4");
            Check(registry.Partitions().Count(x => x.Node != null) == 5, "no partition is lost");
            registry.Flush();
        }
        finally
        {
            registry?.Dispose();
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// SE-214: a managed node the Registry declares Dead while its host still reports the process running (hung, not
    /// crashed) gets a fenced Kill of exactly that incarnation; Gone clears it and the slot respawns after the backoff.
    /// A crash, a node the host knows no epoch of, and a held lease plan no kill, and a second hang backs off longer.
    /// </summary>
    private static void TestManagedHungNodeKill()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("managed-hung-node-kill: " + what); }
        string root = Path.Combine(Path.GetTempPath(), "cluster-managed-hung-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var deployment = new ManagedDeployment("deployment-1", Guid.NewGuid(),
            [new("hung", "host-1", "hung", "127.0.0.1:28421", NodeRole.Regular),
             new("crashed", "host-1", "crashed", "127.0.0.1:28422", NodeRole.Regular),
             new("loading", "host-1", "loading", "127.0.0.1:28423", NodeRole.Regular),
             new("wa", "host-1", "wa", "127.0.0.1:28424", NodeRole.WA)]);
        try
        {
            using var registry = new GatewayRegistry(root, Admission, () => now, TimeSpan.FromSeconds(10), managedDeployment: deployment)
                { LiveProcessGrace = TimeSpan.FromSeconds(60) };
            var request = new Admin.ExecutorPollRequest("host-1", "executor-1", Guid.NewGuid(), null, 1, []) { DeploymentRevision = deployment.Revision };
            request = request with { Lease = registry.PollExecutor(request).Lease };
            Admin.NodePlan[] Poll(params Admin.ExecutorNodeReport[] reports)
            {
                request = request with { Sequence = request.Sequence + 1, Reports = reports };
                return registry.PollExecutor(request).Plan;
            }
            Admin.ExecutorNodeReport Report(NodeRegistration node, Admin.NodeObservation state, long? epoch = null) =>
                new(node.Node, "attempt-" + node.Node + node.Epoch, state, node.Node, epoch ?? node.Epoch, null);
            NodeRegistration Live(string node) => registry.Nodes().Single(x => x.Node == node);
            void Advance(int seconds, Func<Admin.ExecutorNodeReport[]> reports)
            {
                for (int second = 1; second <= seconds; second++)
                {
                    now = now.AddSeconds(1);
                    registry.ExpireLeases();
                    if (second % 2 == 0) Poll(reports());
                }
            }

            NodeRegistration hung = Register(registry, "hung", "127.0.0.1:28421", Guid.NewGuid());
            NodeRegistration crashed = Register(registry, "crashed", "127.0.0.1:28422", Guid.NewGuid());
            NodeRegistration loading = Register(registry, "loading", "127.0.0.1:28423", Guid.NewGuid());
            NodeRegistration wa = registry.RegisterNode(Join("wa", "127.0.0.1:28424", Guid.NewGuid()) with { Role = NodeRole.WA });
            void Beat() => registry.Heartbeat(wa.Node, wa.Epoch, [], serving: true,
                deploymentRevision: deployment.Revision, configurationVerified: true);
            Admin.ExecutorNodeReport[] Steady() => [Report(hung, Admin.NodeObservation.Ready), Report(wa, Admin.NodeObservation.Ready),
                Report(crashed, Admin.NodeObservation.Gone), Report(loading, Admin.NodeObservation.Spawning, epoch: 0)];
            Poll(Report(hung, Admin.NodeObservation.Ready), Report(crashed, Admin.NodeObservation.Ready),
                Report(wa, Admin.NodeObservation.Ready), Report(loading, Admin.NodeObservation.Spawning, epoch: 0));

            // Only the WA keeps renewing. hung freezes (its host reports it running), crashed really crashed, and loading
            // hangs before its ready receipt, so its host can't name an epoch and its ready timeout stays in charge.
            Advance(20, () => { Beat(); return Steady(); });
            Check(Live("crashed").State == NodeState.Dead && Live("loading").State == NodeState.Dead, "the crash and the loader die at their leases");
            Check(Live("hung").State == NodeState.Active, "the frozen node is held while its host reports it running");
            Admin.NodePlan Slot(string key) => registry.AdminNodePlan().Single(x => x.SlotKey == key);
            Check(Slot("crashed").IncumbentAction == Admin.IncumbentAction.None, "a crashed process gets no kill");
            Check(Slot("loading").IncumbentAction == Admin.IncumbentAction.None, "a process the host knows no epoch of gets no kill");
            Check(Slot("hung").IncumbentAction == Admin.IncumbentAction.None, "a held lease gets no kill");

            Advance(41, () => { Beat(); return Steady(); });
            Check(Live("hung").State == NodeState.Dead, "past the hold the frozen node is Dead");
            Admin.NodePlan killed = Poll(Report(hung, Admin.NodeObservation.Ready), Report(wa, Admin.NodeObservation.Ready))
                .Single(x => x.SlotKey == "hung");
            Check(killed.IncumbentAction == Admin.IncumbentAction.Kill && killed.IncumbentNode == "hung" && killed.IncumbentEpoch == hung.Epoch,
                "the plan kills exactly the hung incarnation");
            Check(!killed.SpawnAllowed, "nothing respawns over the incumbent");
            Check(registry.AdminNodePlan().Single(x => x.SlotKey == "wa").IncumbentAction == Admin.IncumbentAction.None, "the live WA is left alone");

            // A Gone naming another incarnation does not release the slot; the one naming this one does, and the backoff runs.
            try { Poll(Report(hung, Admin.NodeObservation.Gone, epoch: hung.Epoch + 1)); Check(false, "a stale Gone is refused"); }
            catch (RegistryConflictException) { request = request with { Sequence = request.Sequence + 1 }; }
            Admin.NodePlan gone = Poll(Report(hung, Admin.NodeObservation.Gone)).Single(x => x.SlotKey == "hung");
            Check(gone.IncumbentAction == Admin.IncumbentAction.None && gone.IncumbentNode == null, "Gone clears the incumbent");
            Check(!gone.SpawnAllowed, "the respawn waits for the backoff");
            now = now.AddSeconds(6);
            Beat();
            Check(Poll(Report(hung, Admin.NodeObservation.Gone)).Single(x => x.SlotKey == "hung").SpawnAllowed, "after 5 s the slot may respawn");

            // The next incarnation serves (Ready ends the retry state), hangs again, and backs off for twice as long.
            NodeRegistration second = Register(registry, "hung", "127.0.0.1:28421", Guid.NewGuid());
            Check(second.Epoch > hung.Epoch, "a new incarnation");
            Poll(Report(second, Admin.NodeObservation.Ready));
            Check(Slot("hung").RetryAt == null, "Ready ends the backoff");
            Advance(62, () => { Beat(); return [Report(second, Admin.NodeObservation.Ready)]; });
            Check(Live("hung").State == NodeState.Dead && Slot("hung").IncumbentEpoch == second.Epoch, "the second hang kills the second incarnation");
            Poll(Report(second, Admin.NodeObservation.Gone));
            now = now.AddSeconds(6);
            Beat();
            Check(!Poll(Report(second, Admin.NodeObservation.Gone)).Single(x => x.SlotKey == "hung").SpawnAllowed, "the second kill backs off 10 s");
            now = now.AddSeconds(5);
            Beat();
            Check(Poll(Report(second, Admin.NodeObservation.Gone)).Single(x => x.SlotKey == "hung").SpawnAllowed, "and then respawns");

            // Everything hanging at once is not one failure cause: GlobalSpawnHalted would stop the very respawns that end it.
            Check(Slot("hung").LastFailure == "node_dead:hung", "each slot names its own node");
            registry.Flush();
        }
        finally { Directory.Delete(root, true); }
    }

    // #1210: a new generation whose inventory drops slots (2 hot spares -> 0). Their rows are gone holders: the release
    // pass drops a row once nothing names it, a proxy serves a dropped slot's files until then, and every row of a slot
    // still in the inventory stays. Red: without the managed rule nothing is gone, the dropped rows stay Empty forever.
    // An archived save that names a dropped slot does not keep its row (the live 2 -> 0 check: the archive is never
    // rewritten, so it would forever): the row goes as in store mode, its host frozen in the alias map, and a live node
    // of that host serves the archived copy when it comes back. Red: asking the archive keeps the row.
    private static void TestManagedRemovedSlots()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("managed-removed-slots: " + what); }
        var deployment = new ManagedDeployment("deployment-1", Guid.NewGuid(),
            [new("r1", "host-1", "r1", "127.0.0.1:28417", NodeRole.Regular),
             new("r2", "host-1", "r2", "127.0.0.1:28418", NodeRole.Regular),
             new("s1", "host-1", "s1", "127.0.0.1:28419", NodeRole.Regular, Spare: true),
             new("s2", "host-1", "s2", "127.0.0.1:28420", NodeRole.Regular, Spare: true),
             new("wa", "host-1", "wa", "127.0.0.1:28421", NodeRole.WA)]);
        var smaller = new ManagedDeployment("deployment-2", Guid.NewGuid(), [.. deployment.Slots.Where(slot => !slot.Spare)]);
        static string Rows(GatewayRegistry registry) => string.Join(",", registry.AdminNodes().Select(x => x.Node).Order(StringComparer.Ordinal));
        NodeRegistration Start(GatewayRegistry registry, ManagedDeployment managed, ManagedSlot slot)
        {
            NodeRegistration node = registry.RegisterNode(Join(slot.Node, slot.Endpoint, Guid.NewGuid())
                with { Role = slot.Role, Host = slot.Host, ControlEndpoint = "http://" + slot.Endpoint.Replace(":284", ":294") });
            registry.Heartbeat(node.Node, node.Epoch, [], serving: true, deploymentRevision: managed.Revision, configurationVerified: true);
            return node;
        }

        // The first generation: r1 saves partition 1 and spare s2 holds the only other copy; a retired partition's save,
        // with a copy on s2 too, is archived; then a clean Down.
        (SaveId Save, SaveId Archived, ulong Output) FirstGeneration(string root)
        {
            using var registry = new GatewayRegistry(root, Admission, managedDeployment: deployment);
            NodeRegistration[] nodes = [.. deployment.Slots.Select(slot => Start(registry, deployment, slot))];
            Check(registry.AdmissionOpen, "the first generation serves");
            NodeRegistration r1 = nodes[0], s2 = nodes[3], authority = nodes[4];
            PartitionOwnership owned = registry.ClaimPartition(1, r1.Node, r1.Epoch, 0);
            var save = new SaveId(1, owned.Generation, 1);
            registry.RegisterSave(save, r1.Node, r1.Epoch, SaveKind.Periodic, 1, "c1", 1, "r1/1", r1.ControlEndpoint!, entities: 3);
            registry.RegisterHolder(save, s2.Node, s2.Epoch, 1, "c1", "s2/1", s2.ControlEndpoint!);
            registry.ReleasePartition(1, r1.Node, r1.Epoch, registry.GetPartitionOwner(1).Generation);
            ulong parent = registry.AllocatePartitionIds(r1.Node, r1.Epoch, 1)[0];
            registry.ClaimPartition(parent, r1.Node, r1.Epoch, null);
            var archived = new SaveId(parent, registry.GetPartitionOwner(parent).Generation, 1);
            registry.RegisterSave(archived, r1.Node, r1.Epoch, SaveKind.Final, 1, "c2", 1, "r1/2", r1.ControlEndpoint!, entities: 3);
            registry.RegisterHolder(archived, s2.Node, s2.Epoch, 1, "c2", "s2/2", s2.ControlEndpoint!);
            ulong[] outputs = registry.AllocatePartitionIds(r1.Node, r1.Epoch, 2);
            registry.ApplyLineage("split-" + parent, LineageKind.Split, r1.Node, r1.Epoch, [new(parent, archived.Generation)], outputs,
                new() { [outputs[0]] = [1], [outputs[1]] = [2] }, 5);
            foreach (ulong output in outputs)
            {
                long generation = registry.GetPartitionOwner(output).Generation;
                registry.RegisterSave(new SaveId(output, generation, 1), r1.Node, r1.Epoch, SaveKind.Final, 1, "c" + output, 1, "r1/o" + output, r1.ControlEndpoint!);
                registry.ReleasePartition(output, r1.Node, r1.Epoch, generation);
            }
            registry.PlanRetention();
            registry.PlanRetention();
            Check(registry.ArchivedPartitionCount == 1 && registry.Saves().All(x => x.SaveId != archived), "the retired partition's save is archived");
            registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
            registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch,
                SaveKind.Final, 1, "final", 1, "wa/final");
            foreach (var node in registry.Nodes()) registry.MarkEmpty(node.Node, node.Epoch);
            registry.CompleteShutdown(false);
            return (save, archived, outputs[0]);
        }

        bool rule = GatewayRegistry.ManagedRemovedSlotsGone;
        string redRoot = Path.Combine(Path.GetTempPath(), "cluster-managed-removed-red-" + Guid.NewGuid());
        string root = Path.Combine(Path.GetTempPath(), "cluster-managed-removed-" + Guid.NewGuid());
        try
        {
            GatewayRegistry.ManagedRemovedSlotsGone = false;
            FirstGeneration(redRoot);
            using (var red = new GatewayRegistry(redRoot, Admission, managedDeployment: smaller))
            {
                Check(red.ReleaseGoneRowsNow().Length == 0 && Rows(red) == "r1,r2,s1,s2,wa",
                    "RED expected: without the managed rule the dropped slots' rows stay: " + Rows(red));
            }
            GatewayRegistry.ManagedRemovedSlotsGone = rule;

            (SaveId save, SaveId archived, ulong output) = FirstGeneration(root);
            using (var registry = new GatewayRegistry(root, Admission, managedDeployment: smaller))
            {
                // Before any node of the new generation registers: s1 goes, s2 stays for the copy only it and r1 hold.
                Check(registry.ReleaseGoneRowsNow().SequenceEqual(["s1"]), "the pass releases the unnamed dropped slot");
                Check(Rows(registry) == "r1,r2,s2,wa", "the rows of slots still in the inventory stay: " + Rows(registry));
                NodeRegistration[] nodes = [.. smaller.Slots.Select(slot => Start(registry, smaller, slot))];
                Check(registry.AdmissionOpen, "the new generation serves");
                Check(registry.ServedForTest(save).HolderProxies?.GetValueOrDefault("s2") is { } proxy && proxy.Node is "r1" or "r2",
                    "a live node of s2's host serves its copy");
                Check(registry.ReleaseGoneRowsNow().Length == 0 && Rows(registry) == "r1,r2,s2,wa", "s2 stays while its copy is needed");
                // r2 takes a copy: r1 and r2 cover the save, s2's location is dropped and its row released.
                NodeRegistration r2 = nodes[1];
                registry.RegisterHolder(save, r2.Node, r2.Epoch, 1, "c1", "r2/1", r2.ControlEndpoint!);
                GatewayRegistry.ManagedReleaseChecksArchive = true;
                Check(registry.ReleaseGoneRowsNow().Length == 0 && Rows(registry) == "r1,r2,s2,wa",
                    "RED expected: while the pass asks the archive, the archived copy keeps s2: " + Rows(registry));
                GatewayRegistry.ManagedReleaseChecksArchive = false;
                Check(registry.ReleaseGoneRowsNow().SequenceEqual(["s2"]) && Rows(registry) == "r1,r2,wa" && registry.LastReleaseScannedIds == 0,
                    "s2 goes once nothing in memory needs it, without an archive scan: " + Rows(registry));
                Check(registry.HostStoreAliases() is var aliases && aliases.GetValueOrDefault("s1") == "host-1" && aliases.GetValueOrDefault("s2") == "host-1",
                    "the released rows leave their host in the alias map");
                Check(registry.SaveReachable(save) && registry.Saves().Single(x => x.SaveId == save).LocationHolders.Values.Order().SequenceEqual(["r1", "r2"]),
                    "the save keeps its live copies");
                // An output's only copy turns out corrupt: its cover falls back to the archived parent, and s2's copy of
                // it is served through a live node of s2's host.
                SaveId lost = registry.Saves().Single(x => x.SaveId.Partition == output).SaveId;
                registry.ReportCorruption(lost, "r1", r2.Node, r2.Epoch);
                Check(registry.ResolveCover(output).Any(x => x.Save.SaveId == archived) && registry.SaveReachable(archived),
                    "the archived parent comes back as the cover");
                Check(registry.ServedForTest(archived).HolderProxies?.GetValueOrDefault("s2") is { Node: "r1" or "r2" },
                    "a live node of the released slot's host serves its archived copy");
                GatewayRegistry.ServeReleasedHolders = false;
                Check(registry.ServedForTest(archived).HolderProxies?.ContainsKey("s2") != true,
                    "RED expected: without the released-holder rule the released slot's copy has no proxy");
                GatewayRegistry.ServeReleasedHolders = true;
                registry.Flush();
            }
            using (var replay = new GatewayRegistry(root, Admission, managedDeployment: smaller))
                Check(Rows(replay) == "r1,r2,wa", "the release is persisted: " + Rows(replay));
            Console.WriteLine("managed-removed-slots: a generation without the 2 spare slots releases their rows (s1 at once, s2 once r2 "
                + "holds its copy, served through a live node until then; an archived copy on s2 does not keep it, and is served through "
                + "its host when it comes back); the inventory's rows stay; without the rule both stay");
        }
        finally
        {
            GatewayRegistry.ManagedRemovedSlotsGone = rule;
            GatewayRegistry.ManagedReleaseChecksArchive = false;
            GatewayRegistry.ServeReleasedHolders = true;
            foreach (string directory in new[] { root, redRoot })
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static void TestManagedConfigurationDrift()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-drift-" + Guid.NewGuid());
        var deployment = new ManagedDeployment("revision", Guid.NewGuid(),
            [new("regular", "host", "regular", "127.0.0.1:28417", NodeRole.Regular),
             new("wa", "host", "wa", "127.0.0.1:28418", NodeRole.WA)]);
        try
        {
            using (var registry = new GatewayRegistry(root, Admission, managedDeployment: deployment))
            {
                var node = Register(registry, "regular", "127.0.0.1:28417", Guid.NewGuid());
                registry.Heartbeat(node.Node, node.Epoch, [], serving: true,
                    deploymentRevision: deployment.Revision, configurationVerified: true);
                registry.Heartbeat(node.Node, node.Epoch, [], serving: true,
                    deploymentRevision: deployment.Revision, configurationVerified: false,
                    configurationFailure: "configuration_drift:example");
                var slot = registry.NodePlan().Single(s => s.SlotKey == "regular");
                Assert(slot.Desired == NodeDesiredState.Draining && slot.IncumbentAction == NodeIncumbentAction.Draining);
                Assert(slot.LastFailure == "configuration_drift:example" && !registry.AdmissionOpen);
                registry.MarkEmpty(node.Node, node.Epoch);
                registry.Flush();
            }
            using var replay = new GatewayRegistry(root, Admission, managedDeployment: deployment);
            Assert(replay.NodePlan().Single(s => s.SlotKey == "regular").Desired == NodeDesiredState.Draining);
            Assert(replay.NodePlan().Single(s => s.SlotKey == "regular").LastFailure == "configuration_drift:example");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestManagedRecovery()
    {
        foreach (bool dirtyDown in new[] { false, true })
        {
            string root = Path.Combine(Path.GetTempPath(), "cluster-recovery-" + Guid.NewGuid());
            var deployment = new ManagedDeployment("revision", Guid.NewGuid(),
                [new("regular", "host", "regular", "127.0.0.1:28417", NodeRole.Regular),
                 new("wa", "host", "wa", "127.0.0.1:28418", NodeRole.WA)]);
            RecordVersion version = default!;
            long oldEpoch = 0;
            try
            {
                using (var registry = new GatewayRegistry(root, Admission, managedDeployment: deployment))
                {
                    var regular = Register(registry, "regular", "127.0.0.1:28417", Guid.NewGuid());
                    oldEpoch = regular.Epoch;
                    var wa = registry.RegisterNode(Join("wa", "127.0.0.1:28418", Guid.NewGuid()) with { Role = NodeRole.WA });
                    foreach (var node in new[] { regular, wa })
                        registry.Heartbeat(node.Node, node.Epoch, [], serving: true,
                            deploymentRevision: deployment.Revision, configurationVerified: true);
                    Assert(registry.AdmissionOpen && registry.Lifecycle().Phase is ClusterPhase.Serving or ClusterPhase.Degraded);
                    registry.ClaimPartition(42, regular.Node, regular.Epoch, 0);
                    ReportCharacterHome(registry, 1, 2, 3, 42, regular.Node);
                    var read = new PluginRequest { Plugin = "example", Action = "read", Key = "count", Node = regular.Node, Incarnation = regular.Epoch };
                    var missing = registry.PluginOperation(read);
                    var written = registry.PluginOperation(new PluginRequest { Plugin = "example", Action = "cas", Key = "count",
                        Node = regular.Node, Incarnation = regular.Epoch, Expected = missing.Record.Version,
                        Payload = [42], SchemaVersion = 1, OperationId = Guid.NewGuid() });
                    Assert(written.Code == PluginResultCode.Success);
                    version = written.Record.Version;
                    if (dirtyDown) { registry.BeginShutdown(false, TimeSpan.FromMinutes(1)); registry.CompleteShutdown(true); }
                    registry.Flush();
                }
                var recovery = deployment with { StartGeneration = Guid.NewGuid(), Recover = true };
                AssertConflict(() => { using var denied = new GatewayRegistry(root, Admission, managedDeployment: recovery with { Recover = false }); });
                AssertConflict(() => { using var denied = new GatewayRegistry(root, Admission, managedDeployment: recovery with { Revision = "changed" }); });
                AssertConflict(() => { using var denied = new GatewayRegistry(root, Admission, managedDeployment: recovery with {
                    Slots = recovery.Slots.Select(slot => slot with { Host = "changed-host" }).ToArray() }); });
                using (var registry = new GatewayRegistry(root, Admission, managedDeployment: recovery))
                {
                    Assert(registry.Lifecycle().Startup == StartupKind.Recovery && registry.Lifecycle().Phase == ClusterPhase.Bootstrapping);
                    Assert(!registry.AdmissionOpen && registry.Nodes().All(n => n.State == NodeState.Dead));
                    Assert(registry.GetCharacterHome(1)?.Partition == 42);
                    Assert(registry.NodePlan().All(s => s.Desired == NodeDesiredState.Wanted));
                    var old = new PluginRequest { Plugin = "example", Action = "read", Key = "count", Node = "regular", Incarnation = oldEpoch };
                    Assert(registry.PluginOperation(old).Code == PluginResultCode.Fenced);
                    var regular = Register(registry, "regular", "127.0.0.1:28417", Guid.NewGuid());
                    registry.Heartbeat(regular.Node, regular.Epoch, [], serving: true,
                        deploymentRevision: deployment.Revision, configurationVerified: true);
                    old.Incarnation = regular.Epoch;
                    var retained = registry.PluginOperation(old);
                    Assert(retained.Code == PluginResultCode.Success && retained.Record.Payload.SequenceEqual(new byte[] { 42 }));
                    Assert(retained.Record.Version.StoreId == version.StoreId && retained.Record.Version.Revision == version.Revision);
                    registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                    registry.Flush();
                }
                using (var replay = new GatewayRegistry(root, Admission, managedDeployment: recovery))
                    Assert(replay.Lifecycle().Phase == ClusterPhase.Draining);
                AssertConflict(() => { using var denied = new GatewayRegistry(root, Admission, managedDeployment: deployment); });
            }
            finally { Directory.Delete(root, true); }
        }
    }

    // #1296: a cluster whose every stop ends dirty must still take the build that fixes it. CLUSTER_START_RECOVERY=update
    // is a recovery start that may also change the revision and the slots; plain recovery still may not.
    private static void TestManagedRecoveryUpdate()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-recovery-update-" + Guid.NewGuid());
        var deployment = new ManagedDeployment("before", Guid.NewGuid(),
            [new("regular", "host", "regular", "127.0.0.1:28417", NodeRole.Regular),
             new("wa", "host", "wa", "127.0.0.1:28418", NodeRole.WA)]);
        try
        {
            AssertConflict(() => { using var denied = new GatewayRegistry(root, Admission,
                managedDeployment: deployment with { Recover = true, RecoveryUpdate = true }); });
            if (Directory.Exists(root)) Directory.Delete(root, true);
            using (var registry = new GatewayRegistry(root, Admission, managedDeployment: deployment))
            {
                var regular = Register(registry, "regular", "127.0.0.1:28417", Guid.NewGuid());
                var wa = registry.RegisterNode(Join("wa", "127.0.0.1:28418", Guid.NewGuid()) with { Role = NodeRole.WA });
                foreach (var node in new[] { regular, wa })
                    registry.Heartbeat(node.Node, node.Epoch, [], serving: true,
                        deploymentRevision: deployment.Revision, configurationVerified: true);
                registry.ClaimPartition(42, regular.Node, regular.Epoch, 0);
                ReportCharacterHome(registry, 1, 2, 3, 42, regular.Node);
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                registry.CompleteShutdown(true);
                registry.Flush();
            }
            var update = deployment with { Revision = "after", StartGeneration = Guid.NewGuid(),
                Slots = [.. deployment.Slots, new("regular-2", "host", "regular-2", "127.0.0.1:28419", NodeRole.Regular, Spare: true)] };
            AssertConflict(() => { using var denied = new GatewayRegistry(root, Admission, managedDeployment: update); });
            AssertConflict(() => { using var denied = new GatewayRegistry(root, Admission, managedDeployment: update with { Recover = true }); });
            using (var registry = new GatewayRegistry(root, Admission, managedDeployment: update with { Recover = true, RecoveryUpdate = true }))
            {
                Assert(registry.Lifecycle().Startup == StartupKind.Recovery && registry.Lifecycle().Phase == ClusterPhase.Bootstrapping);
                Assert(!registry.AdmissionOpen && registry.Nodes().All(n => n.State == NodeState.Dead));
                Assert(registry.GetCharacterHome(1)?.Partition == 42);
                Assert(registry.NodePlan().Select(s => s.SlotKey).Order().SequenceEqual(["regular", "regular-2", "wa"]));
                var regular = Register(registry, "regular", "127.0.0.1:28417", Guid.NewGuid());
                // The new revision's node registers and proves itself against the new inventory.
                registry.Heartbeat(regular.Node, regular.Epoch, [], serving: true, deploymentRevision: "after", configurationVerified: true);
                Assert(registry.Nodes().Single(n => n.Node == "regular").State != NodeState.Dead);
                registry.Flush();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void TestManagedRestore()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-restore-" + Guid.NewGuid());
        var deployment = new ManagedDeployment("before", Guid.NewGuid(),
            [new("regular", "host", "regular", "127.0.0.1:28417", NodeRole.Regular),
             new("wa", "host", "wa", "127.0.0.1:28418", NodeRole.WA)]);
        try
        {
            using (var registry = new GatewayRegistry(root, Admission, managedDeployment: deployment))
            {
                var node = Register(registry, "regular", "127.0.0.1:28417", Guid.NewGuid());
                registry.ClaimPartition(42, node.Node, node.Epoch, 0);
                registry.Flush();
            }
            Guid restoreId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(root, "managed-restore.json"),
                System.Text.Json.JsonSerializer.Serialize(new { restoreId, revision = "restored" }));
            var restored = deployment with { Revision = "restored", StartGeneration = Guid.NewGuid() };
            using (var registry = new GatewayRegistry(root, Admission, managedDeployment: restored))
            {
                Assert(!registry.AdmissionOpen && registry.Nodes().All(n => n.State == NodeState.Dead));
                Assert(registry.Lifecycle().Startup == StartupKind.Recovery);
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                registry.Flush();
            }
            using (var registry = new GatewayRegistry(root, Admission, managedDeployment: restored))
                Assert(registry.Lifecycle().Phase == ClusterPhase.Draining); // Replay cannot undo a later shutdown.
        }
        finally { Directory.Delete(root, true); }
    }

}
