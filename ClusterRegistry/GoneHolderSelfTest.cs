using ClusterProtocol;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>T-0323 (GoneHolders.cs): saves held only by stopped nodes stay fetchable through a proxy, and the rows of
/// stopped nodes go once nothing names them. Each property has an earned red: the pre-T-0323 behaviour, switched back
/// on through <see cref="GatewayRegistry.ProxyGoneHolders"/> and friends, fails the same check.</summary>
internal static partial class SelfTest
{
    private static NodeRegistration BootServing(NodeControlFixture f, string node, string? host = "polaris")
    {
        int n = NodeControlFixture.Number(node);
        NodeRegistration row = f.Registry.RegisterNode(Join(node, $"127.0.0.1:{27100 + n}", Guid.NewGuid())
            with { Host = host, Pid = 40000 + n, ControlEndpoint = $"http://127.0.0.1:{29500 + n}", HostStore = true });
        f.Registry.Heartbeat(row.Node, row.Epoch, [], serving: true);
        return f.Row(node);
    }

    private static string SavePathOf(string node, SaveId id) =>
        $"/run/{node}-data/cluster/saves/P-{id.Partition:X16}/g{id.Generation:X8}-s{id.Sequence:X8}.mps";

    private static SaveId SaveOn(NodeControlFixture f, string node, ulong partition, int sequence)
    {
        NodeRegistration row = f.Row(node);
        long generation = f.Registry.Partitions().Single(x => x.Partition == partition).Generation;
        var id = new SaveId(partition, generation, sequence);
        f.Registry.RegisterSave(id, node, row.Epoch, SaveKind.Final, 20_000, $"c{partition}.{sequence}", sequence, SavePathOf(node, id), row.ControlEndpoint!);
        return id;
    }

    private static void CopyTo(NodeControlFixture f, SaveId id, string node)
    {
        NodeRegistration row = f.Row(node);
        SaveRegistration save = f.Registry.Saves().Single(x => x.SaveId == id);
        f.Registry.RegisterHolder(id, node, row.Epoch, save.SizeBytes, save.Checksum, SavePathOf(node, id), row.ControlEndpoint!);
    }

    /// <summary>Whether a node could fetch the served save: a live holder, or a gone holder whose proxy is a live node.</summary>
    private static bool Fetchable(NodeControlFixture f, SaveRegistration served)
    {
        bool Live(string node) => f.Registry.Nodes().Any(x => x.Node == node && x.State is NodeState.Active or NodeState.Closed && x.LeaseExpires > f.Now);
        return served.LocationHolders.Values.Any(holder => Live(holder)
            || served.HolderProxies?.TryGetValue(holder, out HolderProxy? proxy) == true && Live(proxy.Node));
    }

    /// <summary>A whole-cluster stop (every node drains to Empty) and the next start's fresh ids.</summary>
    private static void StopAndStart(NodeControlFixture f, params (string Host, int Serving, int Spares)[] hosts)
    {
        f.Registry.BeginShutdown(emergency: false, TimeSpan.FromSeconds(1));
        foreach (NodeRegistration row in f.Registry.Nodes().Where(x => x.State is NodeState.Closed or NodeState.Active && x.Role == NodeRole.Regular))
            f.DrainToEmpty(row.Node);
        f.Registry.CompleteShutdown(allowDirty: true);
        f.Reopen();
        f.Apply(hosts);
    }

    // The fresh-id restart (T-0320 + the first start on w33): every holder of every save is a stopped node, two of them
    // registered before node control (no host). The claim must name a live proxy for each; the proxied grant binds
    // exactly one catalogued location and a proxy on the right host; the node-side path check is realpath-strict.
    private static void TestGoneHolderRestart()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("gone-holder-restart: " + what); }
        using var f = new NodeControlFixture("gone-restart");
        f.Registry.GatewayHost = "polaris";
        // w33's shape: node-1/node-2 from before T-0320, no host reported, holding the only two copies.
        NodeRegistration one = BootServing(f, "node-1", host: null), two = BootServing(f, "node-2", host: null);
        ulong partition = f.Registry.AllocatePartitionIds(one.Node, one.Epoch, 1)[0];
        f.Registry.ClaimPartition(partition, one.Node, one.Epoch, null);
        SaveId save = SaveOn(f, "node-1", partition, 1);
        CopyTo(f, save, "node-2");
        StopAndStart(f, ("polaris", 2, 0), ("ai", 1, 0));
        Check(f.Live().SequenceEqual(["node-3", "node-4", "node-5"]), "fresh ids after the stop: " + string.Join(",", f.Live()));
        BootServing(f, "node-3"); BootServing(f, "node-4"); BootServing(f, "node-5", host: "ai");
        Check(f.Registry.GoneRowIds().SequenceEqual(["node-1", "node-2"]), "the legacy rows are gone holders: " + string.Join(",", f.Registry.GoneRowIds()));
        Check(f.Registry.SaveReachable(save), "the registry offers the save to recovery (Empty holders are usable)");

        // Earned red: before T-0323 nothing could serve it - both holders' processes are gone.
        GatewayRegistry.ProxyGoneHolders = false;
        try { Check(!Fetchable(f, f.Registry.ServedForTest(save)), "RED expected: without proxies the claim names only dead endpoints"); }
        finally { GatewayRegistry.ProxyGoneHolders = true; }
        SaveRegistration served = f.Registry.ServedForTest(save);
        Check(Fetchable(f, served) && served.HolderProxies!.Values.All(x => x.Node == "node-3" && x.Endpoint == "http://127.0.0.1:29503"),
            "each gone holder has a live proxy on its (gateway) host, Active first then by id");

        // A real claim, from the ai node: the proxy is on polaris, where the files are.
        RecoveryClaim claim = f.Registry.ClaimRecoveries("node-5", f.Row("node-5").Epoch, [partition]).Single();
        Check(claim.ResolveCover.Single().Save.HolderProxies?.Keys.Order().SequenceEqual(["node-1", "node-2"]) == true,
            "the recovery claim carries the proxies");

        // Proxied grants: only a proxy on the gone holder's host, only a catalogued location of that holder.
        string location = SavePathOf("node-1", save);
        GrantRequest Request(string receiver, string? at) => new("save", "", partition, save.Generation, save.Sequence, "node-5", f.Row("node-5").Epoch, receiver, at);
        TransferGrant grant = f.Registry.IssueGrant(Request("node-3", location));
        Check(grant.Location == location, "the grant binds the location");
        GrantValidation Validation(TransferGrant g, string? at) => new(g.Token, "save", "", partition, save.Generation, save.Sequence, "node-3", f.Row("node-3").Epoch, at);
        AssertConflict(() => f.Registry.ValidateGrant(Validation(grant, SavePathOf("node-2", save))), "a proxy serving an ungranted location (another holder's)");
        AssertConflict(() => f.Registry.ValidateGrant(Validation(grant, null)), "a proxied grant used as a plain one");
        f.Registry.ValidateGrant(Validation(grant, location));
        AssertConflict(() => f.Registry.IssueGrant(Request("node-5", location)), "a proxy on another host (ai) than the files (polaris)");
        AssertConflict(() => f.Registry.IssueGrant(Request("node-3", "/run/node-1-data/../../etc/shadow.mps")), "a location the catalogue does not hold");
        TransferGrant plain = f.Registry.IssueGrant(Request("node-1", null));
        AssertConflict(() => f.Registry.ValidateGrant(new(plain.Token, "save", "", partition, save.Generation, save.Sequence, "node-1", f.Row("node-1").Epoch, location)),
            "a plain grant cannot serve a location");

        // Only ai is left alive: a polaris gone holder has no proxy (the host is lost for serving; replicas cover that case).
        using var lonely = new NodeControlFixture("gone-lonely");
        lonely.Registry.GatewayHost = "polaris";
        NodeRegistration l1 = BootServing(lonely, "node-1", host: null);
        ulong lp = lonely.Registry.AllocatePartitionIds(l1.Node, l1.Epoch, 1)[0];
        lonely.Registry.ClaimPartition(lp, l1.Node, l1.Epoch, null);
        SaveId ls = SaveOn(lonely, "node-1", lp, 1);
        StopAndStart(lonely, ("ai", 1, 0));
        BootServing(lonely, "node-2", host: "ai");
        Check(lonely.Registry.ServedForTest(ls).HolderProxies == null, "no live node on the gone holder's host: no proxy");

        // The port slot of a stopped node is reused by a fresh id while the gone row is still kept for its saves
        // (r67, node-19 on node-15's slot): the endpoint resolves to the live node, or every global-state frame
        // of the new node is rejected as "Multiple nodes are registered at endpoint".
        using var slot = new NodeControlFixture("gone-endpoint");
        slot.Registry.GatewayHost = "polaris";
        NodeRegistration s1 = BootServing(slot, "node-1");
        ulong sp = slot.Registry.AllocatePartitionIds(s1.Node, s1.Epoch, 1)[0];
        slot.Registry.ClaimPartition(sp, s1.Node, s1.Epoch, null);
        SaveOn(slot, "node-1", sp, 1);
        StopAndStart(slot, ("polaris", 1, 0));
        NodeRegistration reuser = slot.Registry.RegisterNode(Join("node-2", s1.Endpoint, Guid.NewGuid())
            with { Host = "polaris", Pid = 40002, ControlEndpoint = "http://127.0.0.1:29502" });
        Check(slot.Registry.GoneRowIds().SequenceEqual(["node-1"]), "the stopped node's row is kept for its save");
        Check(slot.Registry.GetNodeByEndpoint(s1.Endpoint).Node == reuser.Node, "a reused endpoint resolves to the live node, not the gone row");
        slot.Registry.CloseNode(reuser.Node, reuser.Epoch, TimeSpan.FromSeconds(5));
        Check(slot.Registry.GetNodeByEndpoint(s1.Endpoint).Node == reuser.Node, "and still to it while it drains (Closed)");

        TestProxiedSavePath();
        Console.WriteLine("gone-holder-restart: fresh-id restart with every holder stopped (legacy rows without a host) served through a polaris proxy, also to ai; proxied grants bound to one location and host; realpath-strict node check");
    }

    private static void AssertConflict(Action action, string what)
    {
        try { action(); }
        catch (RegistryConflictException) { return; }
        throw new InvalidOperationException($"gone-holder: expected a refusal: {what}");
    }

    // The proxy's own check (node side): realpath, under the run root, a .mps file. Earned red: the naive string check a
    // proxy would do on the unresolved location accepts both the traversal and the symlink.
    private static void TestProxiedSavePath()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("gone-holder-path: " + what); }
        string top = Directory.CreateTempSubdirectory("gone-path-").FullName;
        try
        {
            string root = Path.Combine(top, "run"), data = Path.Combine(root, "node-9-data");
            string saves = Path.Combine(root, "node-1-data", "cluster", "saves", "P-1");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(saves);
            Directory.CreateDirectory(Path.Combine(top, "outside"));
            string good = Path.Combine(saves, "g1-s1.mps");
            File.WriteAllText(good, "x");
            string secret = Path.Combine(top, "outside", "secret.mps");
            File.WriteAllText(secret, "s");
            string traversal = Path.Combine(root, "node-1-data", "..", "..", "outside", "secret.mps");
            string link = Path.Combine(saves, "g1-s2.mps");
            File.CreateSymbolicLink(link, secret);
            string dirLink = Path.Combine(root, "node-2-data");
            Directory.CreateSymbolicLink(dirLink, Path.Combine(top, "outside"));
            string throughDirLink = Path.Combine(dirLink, "secret.mps");
            string notSave = Path.Combine(saves, "cluster.cfg");
            File.WriteAllText(notSave, "c");
            bool Naive(string location) => location.StartsWith(root + "/", StringComparison.Ordinal) && location.EndsWith(".mps", StringComparison.Ordinal);
            Check(ProxiedSavePath.Resolve(good, data) == good, "a save file of another node under the run root is served");
            Check(Naive(traversal) && Naive(link) && Naive(throughDirLink), "RED expected: the naive check accepts traversal and links");
            Check(ProxiedSavePath.Resolve(traversal, data) == null, "'..' out of the run root is refused");
            Check(ProxiedSavePath.Resolve(link, data) == null, "a file symlink out of the run root is refused");
            Check(ProxiedSavePath.Resolve(throughDirLink, data) == null, "a directory symlink out of the run root is refused");
            Check(ProxiedSavePath.Resolve(notSave, data) == null, "a file that is not a save is refused");
            Check(ProxiedSavePath.Resolve("run/node-1-data/x.mps", data) == null, "a relative location is refused");
            // T-0348: a catalogued file that is not there passes the path check (it is not a refusal), and its read fails as
            // "missing" - the route's 410, which the requester reports as corruption. A refused path is not "missing".
            foreach (string absent in new[] { Path.Combine(saves, "g1-s9.mps"), Path.Combine(root, "node-1-data", "cluster", "saves", "P-2", "g1-s1.mps") })
            {
                Check(ProxiedSavePath.Resolve(absent, data) == absent, "a missing save file under the run root is not refused: " + absent);
                Exception? failure = null;
                try { File.ReadAllBytes(absent); } catch (Exception error) { failure = error; }
                Check(failure != null && ProxiedSavePath.IsMissing(failure), "its read fails as missing (file or directory): " + failure?.GetType().Name);
            }
            // #1210: Quasar's managed layout keeps each slot at <runtime>/nodes/<slot>/data. A removed slot's save is in a
            // sibling slot directory, which the plain rule (the data root's parent) refuses: that is the earned red.
            string nodes = Path.Combine(top, "runtime", ProxiedSavePath.ManagedNodesDirectory), slotData = Path.Combine(nodes, "a1", "data");
            string goneSave = Path.Combine(nodes, "b2", "data", "cluster", "saves", "P-1", "g1-s1.mps");
            Directory.CreateDirectory(slotData);
            Directory.CreateDirectory(Path.GetDirectoryName(goneSave)!);
            File.WriteAllText(goneSave, "x");
            File.WriteAllText(Path.Combine(top, "runtime", "secret.mps"), "s");
            Check(ProxiedSavePath.Resolve(goneSave, slotData) == null, "RED expected: unmanaged, another slot's save is refused");
            Check(ProxiedSavePath.Resolve(goneSave, slotData, managed: true) == goneSave, "managed: another slot's save on this host is served");
            Check(ProxiedSavePath.Resolve(Path.Combine(top, "runtime", "secret.mps"), slotData, managed: true) == null,
                "managed: a save outside runtime/nodes is refused");
            Check(ProxiedSavePath.Resolve(Path.Combine(top, "outside", "secret.mps"), data, managed: true) == null,
                "managed flag on a data root outside runtime/nodes keeps the plain rule");
            Check(!ProxiedSavePath.IsMissing(new UnauthorizedAccessException()) && !ProxiedSavePath.IsMissing(new IOException("torn"))
                && ProxiedSavePath.MissingStatus is not (401 or 404 or 500), "a refusal or another I/O error is not 'missing', and the status is its own");
        }
        finally { Directory.Delete(top, true); }
    }

    // Recycles leave gone rows. On the pre-T-0323 code they accumulate (earned red: N recycles, N rows). With the release
    // pass and the evacuation copies they stay bounded; a row still holding the ONLY copy of a save stays (earned red:
    // deleting it naively makes that save unreachable), and so does a row an ARCHIVED save names (earned red: releasing
    // it without the archive check leaves the archived save unreachable once it is needed again).
    private static void TestGoneHolderRelease()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("gone-holder-release: " + what); }
        const int Recycles = 8;
        int evacuate = GatewayRegistry.EvacuateBytesPerSecond;
        GatewayRegistry.EvacuateBytesPerSecond = 1 << 30;
        try
        {
            int Run(bool release, out NodeControlFixture fixture)
            {
                GatewayRegistry.ReleaseGoneRowsEnabled = release;
                var f = fixture = new NodeControlFixture("gone-release-" + release);
                f.Apply(("polaris", 2, 0));
                foreach (string node in f.Live()) BootServing(f, node);
                int maximum = 0;
                for (int cycle = 0; cycle < Recycles; cycle++)
                {
                    string[] serving = f.Live();
                    string oldest = serving[0], other = serving[1];
                    // The oldest node owns a partition and saves it; the other holds the replica.
                    NodeRegistration owner = f.Row(oldest);
                    ulong partition = f.Registry.AllocatePartitionIds(owner.Node, owner.Epoch, 1)[0];
                    f.Registry.ClaimPartition(partition, owner.Node, owner.Epoch, null);
                    CopyTo(f, SaveOn(f, oldest, partition, 1), other);
                    NodeControlChange change = f.Registry.RecycleNode(oldest, now: false, timeout: null, "loopback", "rotator", "cycle " + cycle);
                    BootServing(f, change.Replacement!);
                    f.DrainToEmpty(oldest);
                    f.Report(oldest, SpawnState.Gone);
                    // The replica pumps of the live nodes copy what only gone holders cover; then the retention pass.
                    for (int round = 0; round < 40; round++)
                    {
                        bool any = false;
                        foreach (string node in f.Live())
                        {
                            NodeRegistration row = f.Row(node);
                            foreach (ReplicaDirective work in f.Registry.ReplicaWork(node, row.Epoch).Where(x => !x.Global))
                            {
                                Check(work.Sources.Length != 0 && work.Sources.All(x => x.Proxy == null || f.Live().Contains(x.Proxy)), "every source is live or proxied by a live node");
                                CopyTo(f, work.SaveId, node);
                                any = true;
                            }
                        }
                        if (!any) break;
                    }
                    f.Registry.PlanRetention();
                    f.Registry.ReleaseGoneRowsNow();
                    maximum = Math.Max(maximum, f.Registry.GoneRowIds().Length);
                }
                return maximum;
            }

            int accumulated = Run(false, out NodeControlFixture before);
            using (before)
                Check(accumulated == Recycles && before.Registry.GoneRowIds().Length == Recycles,
                    $"RED expected: without the release pass every recycle leaves a row ({accumulated})");
            int bounded = Run(true, out NodeControlFixture after);
            using (after)
            {
                Check(bounded <= 1 && after.Registry.GoneRowIds().Length == 0,
                    $"with the release pass and evacuation the gone rows stay bounded: max {bounded}, now {after.Registry.GoneRowIds().Length}");
                Check(Enumerable.Range(1, Recycles).All(n => after.History("node-" + n) != null), "the history (audit) keeps every released node");
                Check(after.Registry.Saves().All(x => after.Registry.SaveReachable(x.SaveId)), "every save stays reachable");
            }
        }
        finally
        {
            GatewayRegistry.ReleaseGoneRowsEnabled = true;
            GatewayRegistry.EvacuateBytesPerSecond = evacuate;
        }

        // The sole holder: only node-1 ever held the save, and no live node takes a copy (the budget is spent).
        using (var f = new NodeControlFixture("gone-keep"))
        {
            GatewayRegistry.EvacuateBytesPerSecond = 1;
            try
            {
                f.Apply(("polaris", 1, 0));
                BootServing(f, "node-1");
                NodeRegistration one = f.Row("node-1");
                ulong partition = f.Registry.AllocatePartitionIds(one.Node, one.Epoch, 1)[0];
                f.Registry.ClaimPartition(partition, one.Node, one.Epoch, null);
                SaveId only = SaveOn(f, "node-1", partition, 1);
                NodeControlChange change = f.Registry.RecycleNode("node-1", now: false, timeout: null, "loopback", "rotator", "keep");
                BootServing(f, change.Replacement!);
                f.DrainToEmpty("node-1");
                f.Report("node-1", SpawnState.Gone);
                f.Registry.PlanRetention();
                f.Registry.ReleaseGoneRowsNow();
                Check(f.Registry.GoneRowIds().SequenceEqual(["node-1"]) && f.Registry.SaveReachable(only), "the sole holder keeps its row; the save stays reachable");
                f.Registry.RemoveRowNaively("node-1");
                Check(!f.Registry.SaveReachable(only), "RED expected: deleting the sole holder's row makes its save unreachable");
            }
            finally { GatewayRegistry.EvacuateBytesPerSecond = evacuate; }
        }

        TestGoneHolderArchive(release: true);
        GatewayRegistry.ReleaseChecksArchive = false;
        try { TestGoneHolderArchive(release: false); }
        finally { GatewayRegistry.ReleaseChecksArchive = true; }
        Console.WriteLine($"gone-holder-release: {Recycles} recycles leave {Recycles} rows without the pass, 0 with it (history kept); the sole holder and an archive-named holder keep their rows");
    }

    /// <summary>A retired partition's save, held only by a node that is gone now, is archived. The archive names the node,
    /// so the row stays; <paramref name="release"/> false = the check switched off: the row goes and the archived save is
    /// unreachable when the cover falls back to it.</summary>
    private static void TestGoneHolderArchive(bool release)
    {
        void Check(bool value, string what) { if (!value) throw new InvalidOperationException($"gone-holder-archive ({(release ? "checked" : "RED")}): " + what); }
        int evacuate = GatewayRegistry.EvacuateBytesPerSecond;
        GatewayRegistry.EvacuateBytesPerSecond = 1;
        using var f = new NodeControlFixture("gone-archive-" + release);
        try
        {
            f.Apply(("polaris", 2, 0));
            BootServing(f, "node-1"); BootServing(f, "node-2");
            NodeRegistration one = f.Row("node-1");
            ulong parent = f.Registry.AllocatePartitionIds(one.Node, one.Epoch, 1)[0];
            f.Registry.ClaimPartition(parent, one.Node, one.Epoch, null);
            SaveId parentSave = SaveOn(f, "node-1", parent, 1);
            long generation = f.Registry.Partitions().Single(x => x.Partition == parent).Generation;
            ulong[] outputs = f.Registry.AllocatePartitionIds(one.Node, one.Epoch, 2);
            f.Registry.ApplyLineage("split-" + parent, LineageKind.Split, one.Node, one.Epoch, [new(parent, generation)], outputs,
                new() { [outputs[0]] = [1], [outputs[1]] = [2] }, 5);
            // The outputs live on node-2 with node-2's saves; the parent's save (node-1 only) is archived.
            foreach (ulong output in outputs)
            {
                f.Registry.ReleasePartition(output, one.Node, one.Epoch, f.Registry.Partitions().Single(x => x.Partition == output).Generation);
                NodeRegistration two = f.Row("node-2");
                f.Registry.ClaimPartition(output, two.Node, two.Epoch, null);
                SaveOn(f, "node-2", output, 1);
            }
            NodeControlChange change = f.Registry.RecycleNode("node-1", now: false, timeout: null, "loopback", "rotator", "archive");
            BootServing(f, change.Replacement!);
            f.DrainToEmpty("node-1");
            f.Report("node-1", SpawnState.Gone);
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            Check(f.Registry.ArchivedPartitionCount == 1 && f.Registry.Saves().All(x => x.SaveId.Partition != parent), "the parent's save is archived");
            f.Registry.ReleaseGoneRowsNow();
            bool kept = f.Registry.GoneRowIds().Contains("node-1");
            if (release) Check(kept, "a row the archive still names is kept");
            else Check(!kept, "without the archive check the row is released");
            // node-2's copy of outputs[0] turns out corrupt: the cover falls back to the archived parent save.
            SaveId lost = f.Registry.Saves().Single(x => x.SaveId.Partition == outputs[0]).SaveId;
            NodeRegistration twoRow = f.Row("node-2");
            f.Registry.ReportCorruption(lost, "node-2", "node-2", twoRow.Epoch);
            SaveCover[] cover = f.Registry.ResolveCover(outputs[0]);
            bool reachable = cover.Any(x => x.Save.SaveId == parentSave);
            if (release) Check(reachable && f.Registry.SaveReachable(parentSave), "the archived save is reachable again when needed");
            else Check(!reachable, "RED expected: the released row leaves the archived save unreachable");
        }
        finally { GatewayRegistry.EvacuateBytesPerSecond = evacuate; }
    }

    // The archive scan runs WITHOUT the registry lock: nodes report character homes synchronously from the game thread, so a
    // 300 ms hold is a hitch on every node. Measured with a probe that takes the lock while the scan runs. Earned red: the
    // pre-fix shape (scan under the lock) makes the probe wait the whole scan. And a partition that comes back from the
    // archive during the scan defers the release to the next pass (the scan may have missed a reference it carried).
    private static void TestGoneHolderReleaseOffLock()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("gone-holder-release-off-lock: " + what); }
        int evacuate = GatewayRegistry.EvacuateBytesPerSecond;
        GatewayRegistry.EvacuateBytesPerSecond = 1;
        using var f = new NodeControlFixture("gone-off-lock");
        try
        {
            f.Apply(("polaris", 2, 0));
            BootServing(f, "node-1"); BootServing(f, "node-2");
            // An archived partition, all node-2's (it stays live): the archive is not empty, and a rehydrate is possible.
            NodeRegistration two = f.Row("node-2");
            ulong parent = f.Registry.AllocatePartitionIds(two.Node, two.Epoch, 1)[0];
            f.Registry.ClaimPartition(parent, two.Node, two.Epoch, null);
            SaveOn(f, "node-2", parent, 1);
            long generation = f.Registry.Partitions().Single(x => x.Partition == parent).Generation;
            ulong[] outputs = f.Registry.AllocatePartitionIds(two.Node, two.Epoch, 2);
            f.Registry.ApplyLineage("split-" + parent, LineageKind.Split, two.Node, two.Epoch, [new(parent, generation)], outputs,
                new() { [outputs[0]] = [1], [outputs[1]] = [2] }, 5);
            foreach (ulong output in outputs) SaveOn(f, "node-2", output, 1);
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            Check(f.Registry.ArchivedPartitionCount == 1, "the parent is archived");

            string Recycle(string node)
            {
                NodeControlChange change = f.Registry.RecycleNode(node, now: false, timeout: null, "loopback", "rotator", "off-lock");
                BootServing(f, change.Replacement!);
                f.DrainToEmpty(node);
                f.Report(node, SpawnState.Gone);
                return change.Replacement!;
            }

            // A probe that wants the registry lock 100 ms into a 400 ms scan: how long does it wait?
            (double Waited, string[] Released) Probe()
            {
                GatewayRegistry.ArchiveScanTestDelay = TimeSpan.FromMilliseconds(400);
                try
                {
                    string[] released = [];
                    var pass = new Thread(() => released = f.Registry.ReleaseGoneRowsNow());
                    pass.Start();
                    Thread.Sleep(100);
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    f.Registry.Nodes();
                    double waited = timer.Elapsed.TotalMilliseconds;
                    pass.Join();
                    return (waited, released);
                }
                finally { GatewayRegistry.ArchiveScanTestDelay = TimeSpan.Zero; }
            }

            string first = Recycle("node-1");
            GatewayRegistry.ReleaseScanUnderLock = true;
            (double redWait, string[] redReleased) = Probe();
            double redHold = f.Registry.LastReleaseLockMs;
            GatewayRegistry.ReleaseScanUnderLock = false;
            Check(redReleased.SequenceEqual(["node-1"]) && redWait >= 200 && redHold >= 400,
                $"RED expected: a scan under the lock blocks the probe ({redWait:F0} ms waited, {redHold:F0} ms longest hold)");
            Recycle(first);
            (double wait, string[] released) = Probe();
            double hold = f.Registry.LastReleaseLockMs, scan = f.Registry.LastReleaseScanMs;
            Check(released.SequenceEqual([first]) && wait < 50 && hold < 50 && scan >= 400,
                $"the scan runs off the lock: probe waited {wait:F1} ms, longest hold {hold:F1} ms, scan {scan:F0} ms");
            Console.WriteLine($"gone-holder-release-off-lock: scan {scan:F0} ms; probe waited {wait:F1} ms (red: {redWait:F0}), longest hold {hold:F1} ms (red: {redHold:F0})");

            // A rehydrate during the scan: the release waits a pass, then goes.
            string third = f.Live().First(x => x != "node-2");
            Recycle(third);
            SaveId lost = f.Registry.Saves().Single(x => x.SaveId.Partition == outputs[0]).SaveId;
            GatewayRegistry.ArchiveScanTestHook = () =>
            {
                NodeRegistration row = f.Row("node-2");
                f.Registry.ReportCorruption(lost, "node-2", "node-2", row.Epoch);
                f.Registry.ResolveCover(outputs[0]);
            };
            string[] during;
            try { during = f.Registry.ReleaseGoneRowsNow(); }
            finally { GatewayRegistry.ArchiveScanTestHook = null; }
            Check(f.Registry.ArchivedPartitionCount == 0 && during.Length == 0 && f.Registry.GoneRowIds().Contains(third),
                "a partition rehydrated during the scan defers the release");
            Check(f.Registry.ReleaseGoneRowsNow().SequenceEqual([third]), "the next pass releases it");
        }
        finally
        {
            GatewayRegistry.EvacuateBytesPerSecond = evacuate;
            GatewayRegistry.ReleaseScanUnderLock = false;
            GatewayRegistry.ArchiveScanTestDelay = TimeSpan.Zero;
            GatewayRegistry.ArchiveScanTestHook = null;
        }
    }

    // The archive-reference cache: a gone id the archive names is scanned for once, then cached until the archive can stop
    // naming it - a rehydrate of a partition whose rows name it. A rehydrate of any OTHER partition changes nothing for it.
    // Earned red (df fc5d693): every rehydrate cleared the whole cache, so the pass after it rescanned every archive-named
    // gone id (~0.3 s each on w33, on the handover loop's thread; every recycled id ends up archive-named, so the set only
    // grows) - and a pass whose scan raced a rehydrate cached what it found anyway.
    private static void TestGoneHolderArchiveCache()
    {
        GatewayRegistry.RehydrateClearsArchiveCache = true;
        string? red = null;
        try { ArchiveCache("red"); }
        catch (InvalidOperationException error) when (error.Message.Contains("unrelated rehydrate keeps the cache")) { red = error.Message; }
        finally { GatewayRegistry.RehydrateClearsArchiveCache = false; }
        if (red == null) throw new InvalidOperationException("gone-holder-archive-cache: RED expected with the whole-cache clear, it passed");
        Console.WriteLine("gone-holder-archive-cache: RED as expected with the whole-cache clear: " + red);
        ArchiveCache("green");
    }

    private static void ArchiveCache(string arm)
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("gone-holder-archive-cache: " + what); }
        int evacuate = GatewayRegistry.EvacuateBytesPerSecond;
        GatewayRegistry.EvacuateBytesPerSecond = 1;
        using var f = new NodeControlFixture("gone-archive-cache-" + arm);
        try
        {
            f.Apply(("polaris", 2, 0));
            BootServing(f, "node-1"); BootServing(f, "node-2");
            const string Keeper = "node-2";
            // A retired parent whose only save is held by `writer`; its outputs move to the keeper. Returns (parent, outputs).
            (ulong Parent, ulong[] Outputs) Retire(string writer)
            {
                NodeRegistration w = f.Row(writer);
                ulong parent = f.Registry.AllocatePartitionIds(w.Node, w.Epoch, 1)[0];
                f.Registry.ClaimPartition(parent, w.Node, w.Epoch, null);
                SaveOn(f, writer, parent, 1);
                long generation = f.Registry.Partitions().Single(x => x.Partition == parent).Generation;
                ulong[] outputs = f.Registry.AllocatePartitionIds(w.Node, w.Epoch, 2);
                f.Registry.ApplyLineage("split-" + parent, LineageKind.Split, w.Node, w.Epoch, [new(parent, generation)], outputs,
                    new() { [outputs[0]] = [1], [outputs[1]] = [2] }, 5);
                foreach (ulong output in outputs)
                {
                    if (writer != Keeper)
                        f.Registry.ReleasePartition(output, w.Node, w.Epoch, f.Registry.Partitions().Single(x => x.Partition == output).Generation);
                    NodeRegistration k = f.Row(Keeper);
                    if (writer != Keeper) f.Registry.ClaimPartition(output, k.Node, k.Epoch, null);
                    SaveOn(f, Keeper, output, 1);
                }
                return (parent, outputs);
            }
            void Rehydrate(ulong output)
            {
                SaveId lost = f.Registry.Saves().Single(x => x.SaveId.Partition == output).SaveId;
                f.Registry.ReportCorruption(lost, Keeper, Keeper, f.Row(Keeper).Epoch);
                f.Registry.ResolveCover(output);
            }
            // Three gone writers, each named by its archived parent save; one parent that only the keeper ever held.
            var gone = new List<string>();
            var retired = new List<(ulong Parent, ulong[] Outputs)>();
            for (int i = 0; i < 3; i++)
            {
                string writer = f.Live().First(x => x != Keeper);
                retired.Add(Retire(writer));
                NodeControlChange change = f.Registry.RecycleNode(writer, now: false, timeout: null, "loopback", "rotator", "cache");
                BootServing(f, change.Replacement!);
                f.DrainToEmpty(writer);
                f.Report(writer, SpawnState.Gone);
                gone.Add(writer);
            }
            (ulong _, ulong[] unrelated) = Retire(Keeper);
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            Check(f.Registry.ArchivedPartitionCount == 4, $"four retired parents archived ({f.Registry.ArchivedPartitionCount})");
            f.Registry.Flush();   // the archive rows are committed: the scan, not the queued-write check, finds them

            f.Registry.ReleaseGoneRowsNow();
            Check(f.Registry.LastReleaseScannedIds == 3 && gone.All(f.Registry.GoneRowIds().Contains), $"first pass: three scans, three rows kept: scanned {f.Registry.LastReleaseScannedIds}, gone [{string.Join(",", gone)}], rows [{string.Join(",", f.Registry.GoneRowIds())}], cached [{string.Join(",", f.Registry.ArchiveReferencedIds())}]");
            f.Registry.ReleaseGoneRowsNow();
            Check(f.Registry.LastReleaseScannedIds == 0, "second pass: all cached");

            // A partition that names none of them comes back from the archive.
            Rehydrate(unrelated[0]);
            f.Registry.ReleaseGoneRowsNow();
            Check(f.Registry.LastReleaseScannedIds == 0 && gone.All(f.Registry.GoneRowIds().Contains),
                $"an unrelated rehydrate keeps the cache: {f.Registry.LastReleaseScannedIds} id(s) rescanned");

            // The first writer's parent comes back: only that id leaves the cache (its reference is in memory now).
            Rehydrate(retired[0].Outputs[0]);
            Check(f.Registry.ArchiveReferencedIds().SequenceEqual(gone.Skip(1)),
                "a rehydrate uncaches exactly the ids its rows name: " + string.Join(",", f.Registry.ArchiveReferencedIds()));
            f.Registry.ReleaseGoneRowsNow();
            Check(gone.All(f.Registry.GoneRowIds().Contains), "the rehydrated save names the first writer in memory: kept");

            // A rehydrate DURING the scan of a fourth writer, of that writer's own parent: the scan saw the archived row, the row is
            // in memory now. The pass defers and must not cache what it found - nothing would ever uncache it (that reference is no
            // longer in the archive): once live copies cover the save, the writer's row has to go.
            string fourth = f.Live().First(x => x != Keeper);
            (ulong fourthParent, ulong[] fourthOutputs) = Retire(fourth);
            NodeControlChange last = f.Registry.RecycleNode(fourth, now: false, timeout: null, "loopback", "rotator", "cache");
            BootServing(f, last.Replacement!);
            f.DrainToEmpty(fourth);
            f.Report(fourth, SpawnState.Gone);
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            f.Registry.Flush();
            GatewayRegistry.ArchiveScanTestHook = () => Rehydrate(fourthOutputs[0]);
            try { Check(f.Registry.ReleaseGoneRowsNow().Length == 0, "the racing rehydrate defers the pass"); }
            finally { GatewayRegistry.ArchiveScanTestHook = null; }
            Check(f.Registry.LastReleaseScannedIds == 1 && !f.Registry.ArchiveReferencedIds().Contains(fourth),
                "a deferred pass caches nothing it found: " + string.Join(",", f.Registry.ArchiveReferencedIds()));
            SaveId fourthSave = f.Registry.Saves().Single(x => x.SaveId.Partition == fourthParent).SaveId;
            foreach (string node in f.Live()) CopyTo(f, fourthSave, node);
            // While the rehydrated rows' removal is still queued the committed archive names the writer: kept, not cached.
            Check(f.Registry.ReleaseGoneRowsNow().Length == 0 && !f.Registry.ArchiveReferencedIds().Contains(fourth),
                "a scan that can see rows already rehydrated keeps the row and caches nothing");
            f.Registry.Flush();
            string[] released = f.Registry.ReleaseGoneRowsNow();
            Check(released.SequenceEqual([fourth]), "covered by live copies, the fourth writer's row goes: released " + string.Join(",", released)
                + $"; rows [{string.Join(",", f.Registry.GoneRowIds())}] cached [{string.Join(",", f.Registry.ArchiveReferencedIds())}] holders ["
                + string.Join(",", f.Registry.Saves().Single(x => x.SaveId == fourthSave).LocationHolders.Values) + "] owners ["
                + string.Join(",", f.Registry.Partitions().Where(x => x.Node == fourth).Select(x => x.Partition)) + $"] live [{string.Join(",", f.Live())}]");
            Console.WriteLine("gone-holder-archive-cache: an unrelated rehydrate rescans nothing; a related one uncaches exactly its ids");
        }
        finally { GatewayRegistry.EvacuateBytesPerSecond = evacuate; }
    }

    // Evacuation: copies that only move saves off gone holders rank after every other copy and spend a byte budget, so a
    // restart does not flood disk and LAN (ai is on 100 Mb/s).
    private static void TestGoneHolderEvacuation()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("gone-holder-evacuation: " + what); }
        int evacuate = GatewayRegistry.EvacuateBytesPerSecond;
        using var f = new NodeControlFixture("gone-evacuate");
        try
        {
            GatewayRegistry.EvacuateBytesPerSecond = 20_000;   // one 20 KB test save per second, 10 s burst
            f.Apply(("polaris", 2, 0));
            BootServing(f, "node-1"); BootServing(f, "node-2");
            var old = new List<SaveId>();
            for (int i = 0; i < 30; i++)
            {
                NodeRegistration one = f.Row("node-1");
                ulong partition = f.Registry.AllocatePartitionIds(one.Node, one.Epoch, 1)[0];
                f.Registry.ClaimPartition(partition, one.Node, one.Epoch, null);
                SaveId id = SaveOn(f, "node-1", partition, 1);
                CopyTo(f, id, "node-2");
                old.Add(id);
            }
            StopAndStart(f, ("polaris", 2, 0));
            BootServing(f, "node-3"); BootServing(f, "node-4");
            // A brand-new save of a live partition, single-copied: normal replication, ahead of every evacuation.
            NodeRegistration three = f.Row("node-3");
            ulong fresh = f.Registry.AllocatePartitionIds(three.Node, three.Epoch, 1)[0];
            f.Registry.ClaimPartition(fresh, three.Node, three.Epoch, null);
            SaveId freshSave = SaveOn(f, "node-3", fresh, 1);
            ReplicaDirective first = f.Registry.ReplicaWork("node-4", f.Row("node-4").Epoch).Single(x => !x.Global);
            Check(first.SaveId == freshSave, "a normal copy goes before any evacuation");
            CopyTo(f, freshSave, "node-4");
            int copies = 0, proxied = 0;
            for (int poll = 0; poll < 40; poll++)
                foreach (string node in new[] { "node-3", "node-4" })
                    foreach (ReplicaDirective work in f.Registry.ReplicaWork(node, f.Row(node).Epoch).Where(x => !x.Global))
                    {
                        Check(work.Sources.Length != 0 && work.Sources.All(x => x.Proxy == null ? x.Holder is "node-3" or "node-4" : x.Location != null),
                            "an evacuation copies from a live holder or from a proxy that serves one location");
                        proxied += work.Sources.Count(x => x.Proxy != null);
                        CopyTo(f, work.SaveId, node);
                        copies++;
                    }
            Check(copies is >= 9 and <= 11, $"a 10 s burst of the budget and no more without time passing: {copies} copies");
            int burst = copies;
            Check(proxied != 0, "the first copies come through the proxy");
            f.Advance(5);
            for (int poll = 0; poll < 40; poll++)
                foreach (string node in new[] { "node-3", "node-4" })
                    foreach (ReplicaDirective work in f.Registry.ReplicaWork(node, f.Row(node).Epoch).Where(x => !x.Global))
                    {
                        CopyTo(f, work.SaveId, node);
                        copies++;
                    }
            Check(copies - burst is >= 4 and <= 6, $"then the rate: {copies - burst} copies in 5 s at 20 KB/s of 20 KB saves");

        }
        finally { GatewayRegistry.EvacuateBytesPerSecond = evacuate; }
        Console.WriteLine("gone-holder-evacuation: normal copies first, evacuation from proxies at the byte budget (burst then rate)");
    }

    // T-0348: a crashed (Dead) node's registered saves are served through a same-host proxy like a drained (Empty)
    // node's. r116: after a hard stop 12 rows were Dead, 34k saves had no copy source and the queue never drained.
    private static void TestGoneHolderDead()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("gone-holder-dead: " + what); }
        static ReplicaDirective[] Work(NodeControlFixture f, string node) => [.. f.Registry.ReplicaWork(node, f.Row(node).Epoch).Where(x => !x.Global)];
        using (var f = new NodeControlFixture("gone-dead"))
        {
            f.Apply(("polaris", 2, 0), ("ai", 1, 0));
            // The plan decides which id is on which host: dead and peer on polaris, remote on ai.
            string[] onPolaris = [.. f.Live().Where(x => f.Slot(x)!.Host == "polaris")];
            string dead = onPolaris[0], peer = onPolaris[1], remote = f.Live().Single(x => f.Slot(x)!.Host == "ai");
            BootServing(f, dead); BootServing(f, peer); BootServing(f, remote, host: "ai");
            NodeRegistration one = f.Row(dead);
            ulong[] partitions = f.Registry.AllocatePartitionIds(one.Node, one.Epoch, 7);
            foreach (ulong partition in partitions) f.Registry.ClaimPartition(partition, one.Node, one.Epoch, null);
            ulong claimed = partitions[0], corrupted = partitions[1], retained = partitions[2];
            SaveId claimSave = SaveOn(f, dead, claimed, 1);
            SaveId older = SaveOn(f, dead, corrupted, 1), torn = SaveOn(f, dead, corrupted, 2);
            CopyTo(f, older, peer);
            SaveId[] history = [.. Enumerable.Range(1, 5).Select(sequence => SaveOn(f, dead, retained, sequence))];
            SaveId[] plain = [.. partitions.Skip(3).Select(partition => SaveOn(f, dead, partition, 1))];
            string location = SavePathOf(dead, claimSave);
            GrantRequest Request(SaveId save, string requester, string receiver, string? at) =>
                new("save", "", save.Partition, save.Generation, save.Sequence, requester, f.Row(requester).Epoch, receiver, at);

            // The crash: node-1 stops heartbeating. Until its replacement serves, node control still lists it (Failed).
            f.Silent.Add(dead);
            f.Advance(16);
            Check(f.Row(dead).State == NodeState.Dead && f.State(dead) == Admin.OperatorNodeState.Failed && f.Live().Length == 4,
                "the crashed node is Dead, listed as Failed, with a planned replacement: " + string.Join(",", f.Live()));
            Check(f.Registry.GoneRowIds().Length == 0 && !f.Registry.SaveReachable(claimSave) && f.Registry.ServedForTest(claimSave).HolderProxies == null,
                "a Dead row that node control still lists is not a gone holder: no proxy, not reachable");
            AssertConflict(() => f.Registry.IssueGrant(Request(claimSave, remote, peer, location)), "a proxied grant for a Dead holder that node control still lists");
            // The one save with a live copy (on the peer) is copied from that copy; nothing names the Dead holder as a source.
            foreach (ReplicaDirective work in Work(f, remote))
            {
                Check(work.SaveId == older && work.Sources.Single() is { Proxy: null } source && source.Holder == peer, "only a live holder is a source while the Dead row is still listed");
                CopyTo(f, work.SaveId, remote);
            }
            Check(Work(f, peer).Length == 0 && Work(f, remote).Length == 0, "and the saves only it holds are not handed out as replica work");

            string replacement = f.Live().Single(x => x != dead && x != peer && x != remote);
            Check(f.Slot(replacement)!.Host == "polaris", "the replacement is on the dead node's host");
            BootServing(f, replacement);
            f.Pass();
            string proxy = string.CompareOrdinal(peer, replacement) < 0 ? peer : replacement;
            Check(f.Registry.GoneRowIds().SequenceEqual([dead]) && f.Row(dead).State == NodeState.Dead, "once the replacement serves, the Dead row is a gone holder");

            // Earned red: before T-0348 only an Empty gone holder's files were servable.
            GatewayRegistry.ServeDeadGoneHolders = false;
            try
            {
                Check(!f.Registry.SaveReachable(claimSave) && f.Registry.ServedForTest(claimSave).HolderProxies == null
                    && Work(f, peer).Length == 0 && Work(f, remote).Length == 0 && Work(f, replacement).Length == 0,
                    "RED expected: Empty-only, a Dead gone holder's saves have no source and nobody copies them");
                AssertConflict(() => f.Registry.IssueGrant(Request(claimSave, remote, peer, location)), "RED expected: no proxied grant for a Dead gone holder");
            }
            finally { GatewayRegistry.ServeDeadGoneHolders = true; }

            // (b) A recovery claim from the other host names the polaris proxy (Active first, then by id).
            Check(f.Registry.SaveReachable(claimSave), "the registry offers a Dead gone holder's save to recovery");
            RecoveryClaim claim = f.Registry.ClaimRecoveries(remote, f.Row(remote).Epoch, [claimed]).Single();
            SaveRegistration cover = claim.ResolveCover.Single().Save;
            Check(cover.SaveId == claimSave && cover.HolderProxies?.GetValueOrDefault(dead) is { } named && named.Node == proxy && named.Endpoint == f.Row(proxy).ControlEndpoint,
                "the claim from ai covers the partition with the Dead holder's save and names its polaris proxy");

            // (c) The proxied grant issues and validates; the Dead holder itself still serves nothing.
            TransferGrant grant = f.Registry.IssueGrant(Request(claimSave, remote, peer, location));
            Check(grant.Location == location, "the grant binds the Dead holder's location");
            f.Registry.ValidateGrant(new(grant.Token, "save", "", claimed, claimSave.Generation, claimSave.Sequence, peer, f.Row(peer).Epoch, location));
            AssertConflict(() => f.Registry.IssueGrant(Request(claimSave, remote, dead, null)), "a plain grant to the Dead holder itself");
            AssertConflict(() => f.Registry.IssueGrant(Request(claimSave, peer, remote, location)), "a proxy on another host (ai) than the Dead holder's files (polaris)");

            // (d) A torn or missing file fails the requester's checksum; the corruption report skips that copy.
            Check(f.Registry.NewestSave(corrupted)?.SaveId == torn, "the Dead holder's newer save is the partition's newest");
            f.Registry.ReportCorruption(torn, dead, remote, f.Row(remote).Epoch);
            Check(!f.Registry.SaveReachable(torn) && f.Registry.NewestSave(corrupted)?.SaveId == older, "a corruption report against the Dead holder's copy: recovery falls back to the older save");
            AssertConflict(() => f.Registry.IssueGrant(Request(torn, remote, peer, SavePathOf(dead, torn))), "a proxied grant for the copy reported corrupt");

            // (e) Retention: the Dead holder can never acknowledge a delete, so its locations of deleted saves are dropped.
            f.Registry.PlanRetention();
            Check(f.Registry.Saves().Where(x => x.PendingDelete).Select(x => x.SaveId).Order().SequenceEqual([history[0], history[1]]), "retention deletes the two oldest saves of the 5");
            f.Registry.ReleaseGoneRowsNow();
            Check(!f.Registry.Saves().Any(x => x.SaveId == history[0] || x.SaveId == history[1]), "a PendingDelete save on a Dead gone holder: location dropped, save removed");

            // (a) The replica pumps: every other save is copied off the Dead holder through the proxy, to both hosts.
            int copies = 0, proxied = 0;
            for (int round = 0; round < 40; round++)
            {
                bool any = false;
                foreach (string node in new[] { peer, remote, replacement })
                    foreach (ReplicaDirective work in Work(f, node))
                    {
                        Check(work.SaveId != torn, "the copy reported corrupt is not a source");
                        Check(work.Sources.Length != 0 && work.Sources.All(x => x.Proxy == null ? x.Holder != dead
                            : x.Holder == dead && (x.Proxy == peer || x.Proxy == replacement) && x.Location == SavePathOf(dead, work.SaveId)),
                            "every source is a live holder, or the Dead holder's catalogued location through a live polaris proxy");
                        proxied += work.Sources.Count(x => x.Proxy != null);
                        CopyTo(f, work.SaveId, node);
                        copies++;
                        any = true;
                    }
                if (!any) break;
            }
            Check(proxied != 0 && Work(f, peer).Length == 0 && Work(f, remote).Length == 0 && Work(f, replacement).Length == 0,
                $"the saves held only by the Dead node are copied through the proxy and the queue drains: {copies} copies, {proxied} proxied sources");
            f.Registry.PlanRetention();
            f.Registry.ReleaseGoneRowsNow();
            SaveRegistration[] left = [.. f.Registry.Saves().Where(x => x.SaveId != torn)];
            Check(left.Length == 3 + 2 + plain.Length && left.All(x => !x.LocationHolders.ContainsValue(dead)
                && x.LocationHolders.Values.Distinct().Count() >= 2 && x.LocationHolders.ContainsValue(remote)),
                "every other save has live copies on both hosts and no longer names the Dead holder");
            Check(f.Registry.GoneRowIds().SequenceEqual([dead]) && f.Registry.Saves().Single(x => x.SaveId == torn).LocationHolders.Values.SequenceEqual([dead]),
                "the Dead row stays only for the copy reported corrupt");
        }

        // A host cut: the Dead holder's host has no live node, so there is no proxy. Exactly the behaviour before T-0348:
        // its sole-copy save is not offered to recovery and waits; a save with a live copy elsewhere is copied at the
        // normal rank (the evacuation budget, spent after one save here, does not hold the second one back).
        int evacuate = GatewayRegistry.EvacuateBytesPerSecond;
        using (var f = new NodeControlFixture("gone-dead-cut"))
        {
            GatewayRegistry.EvacuateBytesPerSecond = 1;
            try
            {
                f.Apply(("polaris", 1, 0), ("ai", 2, 0));
                string dead = f.Live().Single(x => f.Slot(x)!.Host == "polaris");
                string[] onAi = [.. f.Live().Where(x => f.Slot(x)!.Host == "ai")];
                string holder = onAi[0], taker = onAi[1];
                BootServing(f, dead); BootServing(f, holder, host: "ai"); BootServing(f, taker, host: "ai");
                NodeRegistration one = f.Row(dead);
                ulong[] partitions = f.Registry.AllocatePartitionIds(one.Node, one.Epoch, 3);
                foreach (ulong partition in partitions) f.Registry.ClaimPartition(partition, one.Node, one.Epoch, null);
                SaveId sole = SaveOn(f, dead, partitions[0], 1);
                SaveId[] shared = [SaveOn(f, dead, partitions[1], 1), SaveOn(f, dead, partitions[2], 1)];
                foreach (SaveId save in shared) CopyTo(f, save, holder);
                f.Silent.Add(dead);
                f.Advance(16);
                string replacement = f.Live().Single(x => x != dead && x != holder && x != taker);
                BootServing(f, replacement);
                f.Pass();
                Check(f.Registry.GoneRowIds().SequenceEqual([dead]) && f.Registry.SaveReachable(sole), "with a live node on its host the Dead gone holder's save is reachable");
                f.Silent.Add(replacement);
                f.Advance(16);
                Check(f.Row(replacement).State == NodeState.Dead && f.Registry.GoneRowIds().SequenceEqual([dead]), "polaris is cut: its replacement node is Dead too");
                Check(!f.Registry.SaveReachable(sole) && f.Registry.ServedForTest(sole).HolderProxies == null && f.Registry.NewestSave(partitions[0]) == null,
                    "no live node on the Dead holder's host: no proxy, the save is not offered to recovery");
                var handed = new List<ReplicaDirective>();
                for (int poll = 0; poll < 3; poll++)
                    foreach (ReplicaDirective work in Work(f, taker))
                    {
                        handed.Add(work);
                        CopyTo(f, work.SaveId, taker);
                    }
                Check(handed.Select(x => x.SaveId).Order().SequenceEqual(shared.Order()) && handed.All(x => x.Sources.Single() is { Proxy: null } source && source.Holder == holder),
                    "the saves with a live copy are copied from it at the normal rank; the sole copy on the cut host waits: " + string.Join(",", handed.Select(x => x.SaveId)));
                Check(Work(f, holder).Length == 0, "nobody is handed the save that nothing can serve");
            }
            finally { GatewayRegistry.EvacuateBytesPerSecond = evacuate; }
        }
        Console.WriteLine("gone-holder-dead: a Dead gone holder's saves are served through a same-host proxy (replica work, recovery claim, proxied grant), a corrupt copy is skipped, deleted saves' locations dropped; a Dead row node control still lists, or a host without a live node, serves nothing");
    }
}
