using System.Diagnostics;
using System.Net;
using System.Reflection;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// T-0296 round 2 (handover reconciliation): a Begun handover whose SOURCE died after the target imported (the
    /// gateway's own cleanup failed: the source thaw POST cannot reach a dead node, so the gateway never called Abort).
    /// ExpireLeases released the partition (generation + 1). DecideReconciliation reads "source not viable, target
    /// ready" as Complete, and Activate's RequireOwner then throws - and ReconcileHandovers has no per-record catch,
    /// so the throw ends the whole pass. The automatic loop runs it FIRST, before the catalogue GC, the balancer and
    /// the scale-in drains, so every later pass throws at the same record: the loop is wedged, the record stays
    /// Begun forever (PartitionMoving: joins to the partition answered "moving between nodes"), and a second record
    /// that reconciliation could settle is never reached. Forced here with a fake target and a dead source.
    /// </summary>
    private static void TestHandoverReconcileStaleSource()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("handover-reconcile-stale-source: " + what); }
        string directory = Path.Combine(Path.GetTempPath(), "cluster-reconcile-stale-" + Guid.NewGuid());
        int targetPort = FreeTcpPort(), deadPort = FreeTcpPort();
        var statusByTransfer = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var discards = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var fakeTarget = new HttpListener();
        fakeTarget.Prefixes.Add($"http://127.0.0.1:{targetPort}/cluster/");
        fakeTarget.Start();
        var targetThread = new Thread(() =>
        {
            while (fakeTarget.IsListening)
            {
                HttpListenerContext context;
                try { context = fakeTarget.GetContext(); } catch (Exception) { return; }
                string path = context.Request.Url!.AbsolutePath;
                string id = context.Request.QueryString["transferId"] ?? "";
                string? body = null;
                if (path.EndsWith("/cluster/status", StringComparison.Ordinal)) statusByTransfer.TryGetValue(id, out body);
                else if (path.EndsWith("/cluster/discard", StringComparison.Ordinal)) { discards.Add(id); body = "{}"; }
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body ?? "{}");
                // h-error's node answers its discard with a server error: that record cannot settle this pass.
                context.Response.StatusCode = body == null ? 404 : id == "h-error" && path.EndsWith("/cluster/discard", StringComparison.Ordinal) ? 500 : 200;
                context.Response.ContentType = "application/json";
                context.Response.OutputStream.Write(bytes);
                context.Response.Close();
            }
        }) { IsBackground = true, Name = "Reconcile-Fake-Target" };
        targetThread.Start();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()), null,
            false, null, null, null, directory, TimeSpan.FromSeconds(1), Admission, null, []);
        using var gateway = new DirectGateway(options);
        try
        {
            GatewayRegistry registry = (GatewayRegistry)typeof(DirectGateway).GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(gateway)!;
            Func<Task> reconcile = () => (Task)typeof(DirectGateway).GetMethod("ReconcileHandovers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(gateway, null)!;
            NodeRegistration source = registry.RegisterNode(Join("node-1", "127.0.0.1:27101", Guid.NewGuid())
                with { ControlEndpoint = $"127.0.0.1:{deadPort}" });
            NodeRegistration target = registry.RegisterNode(Join("node-2", "127.0.0.1:27102", Guid.NewGuid())
                with { ControlEndpoint = $"127.0.0.1:{targetPort}" });
            // h-error comes first in the pass: a record whose cleanup fails must not keep the pass from the others.
            PartitionOwnership three = registry.ClaimPartition(3, source.Node, source.Epoch, 0);
            registry.Begin("h-error", 3, source.Node, source.Epoch, three.Generation, target.Node, target.Epoch);
            statusByTransfer["h-error"] = """{"captured":false,"imported":true,"stateVerified":false,"adopted":false,"clientless":true,"committedGeneration":0,"released":false,"thawed":false,"discarded":false}""";
            PartitionOwnership one = registry.ClaimPartition(1, source.Node, source.Epoch, 0);
            PartitionOwnership two = registry.ClaimPartition(2, source.Node, source.Epoch, 0);
            long oneGeneration = one.Generation;
            registry.Begin("h-ready", 1, source.Node, source.Epoch, one.Generation, target.Node, target.Epoch);
            registry.Begin("h-discarded", 2, source.Node, source.Epoch, two.Generation, target.Node, target.Epoch);
            // Partition 1: the target imported it clientless and verified it (the source died before Activate).
            statusByTransfer["h-ready"] = """{"captured":false,"imported":true,"stateVerified":true,"adopted":false,"clientless":true,"committedGeneration":0,"released":false,"thawed":false,"discarded":false}""";
            // Partition 2: the gateway's cleanup did discard the target's copy; only the source thaw failed.
            statusByTransfer["h-discarded"] = """{"captured":false,"imported":true,"stateVerified":true,"adopted":false,"clientless":true,"committedGeneration":0,"released":false,"thawed":false,"discarded":true}""";
            // The source dies: its lease lapses while the target keeps heartbeating.
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(1.5))
            {
                registry.Heartbeat(target.Node, target.Epoch, []);
                Thread.Sleep(100);
            }
            registry.ExpireLeases();
            Check(!registry.HasLiveNode(source.Node, source.Epoch), "the source is dead");
            PartitionOwnership released = registry.GetPartitionOwner(1);
            long releasedGeneration = released.Generation;
            Check(released.Node == null && released.Generation > oneGeneration,
                $"partition 1 was released past the handover's source generation: {released.Node} gen {released.Generation} (source gen {oneGeneration})");
            string? failure = null;
            for (int pass = 0; pass < 2; pass++)
            {
                registry.Heartbeat(target.Node, target.Epoch, []);
                try { reconcile().GetAwaiter().GetResult(); }
                catch (Exception e) { failure = (failure == null ? "" : failure + "; ") + $"pass {pass}: {e.GetType().Name}: {e.Message}"; }
            }
            HandoverRecord ready = registry.Get("h-ready")!, discarded = registry.Get("h-discarded")!;
            Console.WriteLine($"handover-reconcile-stale-source: failure={failure ?? "none"} h-ready={ready.State}/reconciled={ready.Reconciled} "
                + $"h-discarded={discarded.State}/reconciled={discarded.Reconciled} discards=[{string.Join(",", discards)}]");
            Check(failure == null, "reconciliation threw (the automatic loop would throw here on every pass): " + failure);
            Check(registry.Get("h-error")!.State == HandoverState.Begun, "control: the record whose discard failed is retried, not closed");
            Check(discarded.State == HandoverState.Aborted && discarded.Reconciled,
                "the settleable record behind the stale one was reconciled: " + discarded.State);
            Check(ready.State == HandoverState.Aborted && ready.Reconciled,
                "a handover whose source generation is gone is recovered (aborted), not left Begun: " + ready.State);
            Check(discards.Contains("h-ready"), "the target's imported copy of the ownerless partition was discarded");
            Check(registry.GetPartitionOwner(1).Generation == releasedGeneration, "reconciliation left the released row alone");
        }
        finally
        {
            fakeTarget.Stop();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}

internal static partial class SelfTest
{
    /// <summary>
    /// T-0296 round 2: a Begun handover whose TARGET re-registered under a new epoch (a restarted process under the same
    /// id) while the source still owns the partition. ReconcileHandovers looked both nodes up by (id, recorded epoch) and
    /// skipped the record when either was missing - forever, since that epoch never comes back. The record then stays
    /// Begun: PartitionMoving answers every join to the partition "moving between nodes", Begin refuses every new
    /// handover of it, and ExpireLeases exempts its row from time expiry while the source lives. The source's frozen
    /// copy is never thawed. Expected: the gone side is probed as unreachable and dead, so the live source decides Abort.
    /// </summary>
    private static void TestHandoverReconcileReregistered()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("handover-reconcile-reregistered: " + what); }
        string directory = Path.Combine(Path.GetTempPath(), "cluster-reconcile-rereg-" + Guid.NewGuid());
        int nodePort = FreeTcpPort();
        var thaws = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var fakeNodes = new HttpListener();
        fakeNodes.Prefixes.Add($"http://127.0.0.1:{nodePort}/cluster/");
        fakeNodes.Start();
        new Thread(() =>
        {
            while (fakeNodes.IsListening)
            {
                HttpListenerContext context;
                try { context = fakeNodes.GetContext(); } catch (Exception) { return; }
                string path = context.Request.Url!.AbsolutePath;
                string? body = null;
                // Both nodes share this endpoint; the source's half of the status is what matters (captured, frozen).
                if (path.EndsWith("/cluster/status", StringComparison.Ordinal))
                    body = """{"captured":true,"imported":false,"stateVerified":false,"adopted":false,"clientless":true,"committedGeneration":0,"released":false,"thawed":false,"discarded":false}""";
                else if (path.EndsWith("/cluster/thaw", StringComparison.Ordinal)) { thaws.Add(context.Request.QueryString["transferId"] ?? ""); body = "{}"; }
                else if (path.EndsWith("/cluster/discard", StringComparison.Ordinal)) body = "{}";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body ?? "{}");
                context.Response.StatusCode = body == null ? 404 : 200;
                context.Response.OutputStream.Write(bytes);
                context.Response.Close();
            }
        }) { IsBackground = true, Name = "Reconcile-Fake-Nodes" }.Start();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()), null,
            false, null, null, null, directory, TimeSpan.FromSeconds(30), Admission, null, []);
        using var gateway = new DirectGateway(options);
        try
        {
            GatewayRegistry registry = (GatewayRegistry)typeof(DirectGateway).GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(gateway)!;
            Func<Task> reconcile = () => (Task)typeof(DirectGateway).GetMethod("ReconcileHandovers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(gateway, null)!;
            NodeRegistration source = registry.RegisterNode(Join("node-1", "127.0.0.1:27101", Guid.NewGuid()) with { ControlEndpoint = $"127.0.0.1:{nodePort}" });
            NodeRegistration target = registry.RegisterNode(Join("node-2", "127.0.0.1:27102", Guid.NewGuid()) with { ControlEndpoint = $"127.0.0.1:{nodePort}" });
            PartitionOwnership owned = registry.ClaimPartition(1, source.Node, source.Epoch, 0);
            long generation = owned.Generation;
            registry.Begin("h-orphan", 1, source.Node, source.Epoch, generation, target.Node, target.Epoch);
            // The target process restarts under the same id: a new instance, a new epoch.
            NodeRegistration restarted = registry.RegisterNode(Join("node-2", "127.0.0.1:27102", Guid.NewGuid()) with { ControlEndpoint = $"127.0.0.1:{nodePort}" });
            Check(restarted.Epoch == target.Epoch + 1, "the target re-registered under a new epoch");
            for (int pass = 0; pass < 2; pass++) reconcile().GetAwaiter().GetResult();
            HandoverRecord record = registry.Get("h-orphan")!;
            Console.WriteLine($"handover-reconcile-reregistered: h-orphan={record.State}/reconciled={record.Reconciled} thaws=[{string.Join(",", thaws)}]");
            Check(record.State == HandoverState.Aborted, "the orphaned handover is aborted, not left Begun forever: " + record.State);
            Check(thaws.Contains("h-orphan"), "the live source's frozen copy was thawed");
            Check(registry.OwnedAt(1, source.Node, source.Epoch, generation), "the live source keeps the partition");
        }
        finally
        {
            fakeNodes.Stop();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}

internal static partial class SelfTest
{
    /// <summary>
    /// T-0296 round 2 x T-0321 P4: inside a network window a remote node's lapsed lease is HELD - ExpireLeases does not
    /// declare it Dead and its partitions keep their owner - but reconciliation's HasLiveNode still read the raw lease and
    /// called the node dead. A handover to a remote target that failed at the window's start (the gateway could not reach
    /// it to discard, so it left the record Begun) was then decided Abort without the target: the record closed with no
    /// discard sent, and the still-running target keeps its uncommitted import - frozen entities that the stale-ownership
    /// purge deliberately leaves alone (HasLiveInboundTransfer) - for the rest of its life. Expected: Wait until the
    /// target answers or the registry really declares it Dead.
    /// </summary>
    private static void TestHandoverReconcileHeldLease()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("handover-reconcile-held-lease: " + what); }
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            var windows = new NetworkWindows([(TimeSpan.Parse("03:50"), TimeSpan.Parse("04:08"))], TimeSpan.FromSeconds(30), "polaris", TimeZoneInfo.Utc);
            DateTimeOffset now = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(500) + TimeSpan.Parse("03:49:50");
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)) { LeaseHold = windows };
            NodeRegistration source = registry.RegisterNode(Join("local", "local-ep", Guid.NewGuid()) with { Host = "polaris" });
            NodeRegistration target = registry.RegisterNode(Join("remote", "remote-ep", Guid.NewGuid()) with { Host = "ai" });
            PartitionOwnership owned = registry.ClaimPartition(7, source.Node, source.Epoch, 0);
            long generation = owned.Generation;
            registry.Begin("h-window", 7, source.Node, source.Epoch, generation, target.Node, target.Epoch);
            // The window opens; the remote target goes silent for 60 s while the local source keeps heartbeating.
            for (int i = 0; i < 6; i++)
            {
                now += TimeSpan.FromSeconds(10);
                registry.Heartbeat(source.Node, source.Epoch, [new(7, generation)]);
                registry.ExpireLeases();
            }
            Check(registry.Nodes().Single(x => x.Node == "remote").State == NodeState.Active, "control: the held remote target is not Dead");
            bool targetLive = registry.HasLiveNode(target.Node, target.Epoch);
            var sourceProbe = new DirectGateway.TransferProbe(true, new(true, false, false, false, false, 0, false, false, false));
            var targetProbe = new DirectGateway.TransferProbe(false, null);
            DirectGateway.ReconciliationAction action = DirectGateway.DecideReconciliation(registry.Get("h-window")!, sourceProbe, targetProbe,
                registry.HasLiveOwnership(7, source.Node, source.Epoch, generation), false,
                registry.HasLiveNode(source.Node, source.Epoch), targetLive,
                registry.OwnedAt(7, source.Node, source.Epoch, generation));
            Console.WriteLine($"handover-reconcile-held-lease: targetLive={targetLive} action={action}");
            Check(targetLive, "a remote node whose lease the network window holds is live to reconciliation");
            Check(action == DirectGateway.ReconciliationAction.Wait, "the unreachable held target is waited for, not aborted around: " + action);
            // After the window and its grace the registry declares it Dead; then reconciliation decides without it.
            now = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(500) + TimeSpan.Parse("04:09:00");
            registry.Heartbeat(source.Node, source.Epoch, [new(7, generation)]);
            registry.ExpireLeases();
            Check(!registry.HasLiveNode(target.Node, target.Epoch), "after the window the silent target is dead");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}

internal static partial class SelfTest
{
    /// <summary>
    /// T-0296 round 2: a COMMITTED handover left unreconciled (its source release failed: the source died right after the
    /// commit) whose target has long since committed, reported and kept the partition. Once the target's finished
    /// transfer record is swept (CLUSTER_TRANSFER_RECORD_SECONDS, 1 h) its status answers 404, and DecideReconciliation
    /// read "target not ready" as Recover - AbandonReconciliation then RELEASED the live target's row at the committed
    /// generation, and the target discarded the partition it was simulating on its next heartbeat (recovery from the last
    /// save: everything since lost). A record reaches this when reconciliation could not run for an hour (the wedge fixed
    /// above) or, with the re-registered-node fix, for every legacy record that used to be skipped. Expected: a target that
    /// still owns the committed generation past the finalization lease (so it has reported it) completes the record.
    /// </summary>
    private static void TestHandoverReconcileCommittedPruned()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("handover-reconcile-committed-pruned: " + what); }
        string directory = Path.Combine(Path.GetTempPath(), "cluster-reconcile-pruned-" + Guid.NewGuid());
        int targetPort = FreeTcpPort(), deadPort = FreeTcpPort();
        using var fakeTarget = new HttpListener();
        fakeTarget.Prefixes.Add($"http://127.0.0.1:{targetPort}/cluster/");
        fakeTarget.Start();
        new Thread(() =>
        {
            while (fakeTarget.IsListening)
            {
                HttpListenerContext context;
                try { context = fakeTarget.GetContext(); } catch (Exception) { return; }
                // The target swept its finished transfer record: every status asks for an unknown transfer.
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }) { IsBackground = true, Name = "Reconcile-Fake-Pruned-Target" }.Start();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()), null,
            false, null, null, null, directory, TimeSpan.FromSeconds(1), Admission, null, []);
        using var gateway = new DirectGateway(options);
        try
        {
            GatewayRegistry registry = (GatewayRegistry)typeof(DirectGateway).GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(gateway)!;
            Func<Task> reconcile = () => (Task)typeof(DirectGateway).GetMethod("ReconcileHandovers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(gateway, null)!;
            NodeRegistration source = registry.RegisterNode(Join("node-1", "127.0.0.1:27101", Guid.NewGuid()) with { ControlEndpoint = $"127.0.0.1:{deadPort}" });
            NodeRegistration target = registry.RegisterNode(Join("node-2", "127.0.0.1:27102", Guid.NewGuid()) with { ControlEndpoint = $"127.0.0.1:{targetPort}" });
            PartitionOwnership owned = registry.ClaimPartition(1, source.Node, source.Epoch, 0);
            registry.Begin("h-old", 1, source.Node, source.Epoch, owned.Generation, target.Node, target.Epoch);
            registry.Activate("h-old");
            long committedGeneration = registry.Commit("h-old").CommittedGeneration!.Value;
            // The source dies before its release; the target heartbeats the committed generation (it committed it). The
            // decision does not depend on the finalization lease having ended, so only the source's 1 s TTL is waited out.
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(1.5))
            {
                registry.Heartbeat(target.Node, target.Epoch, [new(1, committedGeneration)]);
                Thread.Sleep(200);
            }
            registry.ExpireLeases();
            Check(!registry.HasLiveNode(source.Node, source.Epoch), "control: the source is dead");
            Check(registry.HasLiveOwnership(1, target.Node, target.Epoch, committedGeneration), "control: the target owns the committed generation");
            registry.Heartbeat(target.Node, target.Epoch, [new(1, committedGeneration)]);
            reconcile().GetAwaiter().GetResult();
            HandoverRecord record = registry.Get("h-old")!;
            PartitionOwnership after = registry.GetPartitionOwner(1);
            Console.WriteLine($"handover-reconcile-committed-pruned: record={record.State}/reconciled={record.Reconciled} owner={after.Node ?? "none"} gen={after.Generation}");
            Check(after.Node == target.Node && after.Generation == committedGeneration,
                $"the live target keeps the partition it committed: owner {after.Node ?? "none"} gen {after.Generation}");
            Check(record.Reconciled, "the record is finalized");
        }
        finally
        {
            fakeTarget.Stop();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
