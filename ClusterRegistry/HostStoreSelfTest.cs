using System.Text.Json;

namespace ClusterRegistry;

/// <summary>T-0340 step 1 (HostStore.cs): the catalogue re-keyed from node holders to host holders at load, and the
/// re-keyed database loaded by the package before this ticket (the same code with the switch off).</summary>
internal static partial class SelfTest
{
    /// <summary><see cref="BootServing"/> for a node on another host than the gateway's: with the gateway host known, such
    /// a node must advertise an address the gateway can reach (nothing listens on it here).</summary>
    private static NodeRegistration BootServingRemote(NodeControlFixture f, string node, string host, bool spare = false)
    {
        int n = NodeControlFixture.Number(node);
        NodeRegistration row = f.Registry.RegisterNode(Join(node, $"10.0.0.2:{27100 + n}", Guid.NewGuid())
            with { Host = host, Pid = 40000 + n, ControlEndpoint = $"http://10.0.0.2:{29500 + n}", HostStore = true });
        f.Registry.Heartbeat(row.Node, row.Epoch, [], serving: !spare);
        return f.Row(node);
    }

    private static NodeRegistration BootServingOnAi(NodeControlFixture f, string node) => BootServingRemote(f, node, "ai");

    /// <summary>The planned nodes of a host, by id.</summary>
    private static string[] PlannedOn(NodeControlFixture f, string host) =>
        [.. f.View().Nodes.Where(x => x.Host == host).Select(x => x.Node).OrderBy(NodeControlFixture.Number)];

    /// <summary>A node registers a save (the next sequence when the partition has one) the way a node that writes into the
    /// host store does: at its host's store path.</summary>
    private static SaveId SaveInStore(NodeControlFixture f, string node, string host, ulong partition, int sequence = 1)
    {
        NodeRegistration row = f.Row(node);
        var id = new SaveId(partition, f.Registry.Partitions().Single(x => x.Partition == partition).Generation, sequence);
        f.Registry.RegisterSave(id, node, row.Epoch, SaveKind.Final, 20_000, $"c{partition}.{sequence}", sequence,
            GatewayRegistry.HostStorePath(f.Registry.RunRoot, host, id), row.ControlEndpoint!);
        return id;
    }

    private static void CopyIntoStore(NodeControlFixture f, SaveId id, string node, string host)
    {
        NodeRegistration row = f.Row(node);
        SaveRegistration save = f.Registry.Saves().Single(x => x.SaveId == id);
        f.Registry.RegisterHolder(id, node, row.Epoch, save.SizeBytes, save.Checksum, GatewayRegistry.HostStorePath(f.Registry.RunRoot, host, id), row.ControlEndpoint!);
    }

    /// <summary>The host agent's file move, for the saves the registry has in memory: a file of the catalogued size at each
    /// of these hosts' store paths (the self-test's hosts share one disk, so the registry sees every store).</summary>
    private static void MoveIntoStores(NodeControlFixture f, params string[] hosts)
    {
        foreach (string host in hosts)
        {
            foreach (SaveRegistration save in f.Registry.Saves()) StoreFile(GatewayRegistry.HostStorePath(f.Registry.RunRoot, host, save.SaveId), save.SizeBytes);
            foreach (GlobalSaveRegistration global in f.Registry.GlobalSaves()) StoreFile(GatewayRegistry.HostStorePath(f.Registry.RunRoot, host, global.SaveId), global.SizeBytes);
        }
    }

    private static void StoreFile(string path, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = new(path, FileMode.Create);
        stream.SetLength(size);
    }

    /// <summary>A fixture whose registry loaded with the host store in force (the store directory exists, the switch is on).
    /// The caller switches <see cref="GatewayRegistry.HostStoreEnabled"/> off again and deletes <paramref name="top"/>.</summary>
    private static NodeControlFixture HostStoreFixture(string name, out string top, params (string Host, int Serving, int Spares)[] hosts)
    {
        top = Directory.CreateTempSubdirectory(name + "-").FullName;
        var f = new NodeControlFixture(name, Path.Combine(top, "run", "gateway-registry")) { GatewayHost = "polaris" };
        f.Apply(hosts);
        Directory.CreateDirectory(Path.Combine(f.Registry.RunRoot, "saves", "polaris"));
        GatewayRegistry.HostStoreEnabled = true;
        f.Reopen();
        foreach ((string host, _, _) in hosts)
        {
            string[] planned = PlannedOn(f, host);
            int serving = hosts.Single(x => x.Host == host).Serving;
            for (int i = 0; i < planned.Length; i++)
                if (host == "polaris") f.Registry.Heartbeat(BootServing(f, planned[i]).Node, f.Row(planned[i]).Epoch, [], serving: i < serving);
                else BootServingRemote(f, planned[i], host, spare: i >= serving);
        }
        return f;
    }

    /// <summary>A node stops for good (recycled, drained, its process gone); its replacement serves.</summary>
    private static string RecycleAndReplace(NodeControlFixture f, string node, string host)
    {
        NodeControlChange change = f.Registry.RecycleNode(node, now: false, timeout: null, "loopback", "rotator", "host-store");
        if (host == "polaris") BootServing(f, change.Replacement!); else BootServingRemote(f, change.Replacement!, host);
        f.DrainToEmpty(node);
        f.Report(node, SpawnState.Gone);
        f.Pass();
        return change.Replacement!;
    }

    /// <summary>The node's process dies: it stops heartbeating and its lease expires (outside a network window: Dead).</summary>
    private static void Kill(NodeControlFixture f, string node)
    {
        f.Silent.Add(node);
        f.Advance(15);
    }

    private static ulong OwnNewPartition(NodeControlFixture f, string node)
    {
        NodeRegistration row = f.Row(node);
        ulong partition = f.Registry.AllocatePartitionIds(row.Node, row.Epoch, 1)[0];
        f.Registry.ClaimPartition(partition, row.Node, row.Epoch, null);
        return partition;
    }

    /// <summary>A partition of <paramref name="node"/> with one save, split in two (each output saved): after two retention
    /// passes the parent's save is archived. Returns the parent's save.</summary>
    private static SaveId ArchivedSaveOn(NodeControlFixture f, string node)
    {
        NodeRegistration row = f.Row(node);
        ulong parent = OwnNewPartition(f, node);
        SaveId save = SaveOn(f, node, parent, 1);
        ulong[] outputs = f.Registry.AllocatePartitionIds(row.Node, row.Epoch, 2);
        f.Registry.ApplyLineage("split-" + parent, LineageKind.Split, row.Node, row.Epoch, [new(parent, save.Generation)], outputs,
            new() { [outputs[0]] = [1], [outputs[1]] = [2] }, 5);
        foreach (ulong output in outputs) SaveOn(f, node, output, 1);
        f.Registry.PlanRetention();
        f.Registry.PlanRetention();
        return save;
    }

    /// <summary>The save is held by exactly these hosts, each at its store path, and by nothing else.</summary>
    private static bool HeldByHosts(NodeControlFixture f, SaveRegistration save, params string[] hosts) =>
        save.Locations.SetEquals(hosts.Select(host => GatewayRegistry.HostStorePath(f.Registry.RunRoot, host, save.SaveId)))
        && save.LocationHolders.Count == hosts.Length && save.HolderEndpoints.Count == 0
        && hosts.All(host => save.LocationHolders[GatewayRegistry.HostStorePath(f.Registry.RunRoot, host, save.SaveId)] == GatewayRegistry.HostHolder(host));

    // The re-key at load. Holders of every kind the live registry has: rows from before node control (no host: the
    // gateway host), rows with a host, a released row only the node history remembers, the World Authority, copies on
    // one host (merged) and on two, corrupt copies, an archived partition (not rewritten; re-keyed through the frozen
    // alias map when it comes back). One transaction, idempotent, and not at all without node control.
    private static void TestHostStoreRekey()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-rekey: " + what); }
        Check(GatewayRegistry.HostStorePath("/run", "polaris", new SaveId(0x2A, 3, 7)) == "/run/saves/polaris/P-000000000000002A/g00000003-s00000007.mps",
            "a partition save's store path is the node's relative name under saves/<host>");
        Check(GatewayRegistry.HostStorePath("/run", "ai", new GlobalSaveId(2, 11)) == "/run/saves/ai/GLOBAL/g00000002-s0000000B.mps",
            "a global save's store path");
        Check(GatewayRegistry.HostOfHolder(GatewayRegistry.HostHolder("polaris")) == "polaris" && GatewayRegistry.HostOfHolder("node-12") == null,
            "a host holder key is told from a node id");
        foreach (string bad in new[] { "", "..", "a/b", "a:b" })
        {
            try { GatewayRegistry.HostStorePath("/run", bad, new SaveId(1, 1, 1)); }
            catch (InvalidDataException) { continue; }
            throw new InvalidOperationException($"host-store-rekey: host name '{bad}' must not name a store directory");
        }

        string top = Directory.CreateTempSubdirectory("host-rekey-").FullName;
        var f = new NodeControlFixture("host-rekey", Path.Combine(top, "run", "gateway-registry")) { GatewayHost = "polaris" };
        try
        {
            // Before node control: no host reported, both copies on the gateway host.
            BootServing(f, "node-1", host: null); BootServing(f, "node-2", host: null);
            SaveId legacy = SaveOn(f, "node-1", OwnNewPartition(f, "node-1"), 1);
            CopyTo(f, legacy, "node-2");
            StopAndStart(f, ("polaris", 2, 0), ("ai", 3, 0));
            // The plan decides which id runs where (hosts in name order): ai gets the first three ids, polaris the next two.
            string[] ai = [.. f.View().Nodes.Where(x => x.Host == "ai").Select(x => x.Node)], polaris = [.. f.View().Nodes.Where(x => x.Host == "polaris").Select(x => x.Node)];
            Check(ai.Length == 3 && polaris.Length == 2, "fresh ids on both hosts: " + string.Join(",", f.Live()));
            foreach (string node in polaris) BootServing(f, node);
            BootServingOnAi(f, ai[0]); BootServingOnAi(f, ai[1]);
            // The third ai node reports the wrong host about itself: the plan knows where it was launched.
            BootServing(f, ai[2], host: "polaris");
            SaveId misreported = SaveOn(f, ai[2], OwnNewPartition(f, ai[2]), 1);
            NodeRegistration onPolaris = f.Row(polaris[0]), onAi = f.Row(ai[0]);
            NodeRegistration wa = f.Registry.RegisterNode(Join("wa-1", "127.0.0.1:28500", Guid.NewGuid())
                with { Role = NodeRole.WA, Host = "polaris", Pid = 77, ControlEndpoint = "http://127.0.0.1:29419" });
            GlobalSaveRegistration global = f.Registry.RegisterGlobalSave(new(wa.AuthorityGeneration, 1), wa.Node, wa.Epoch, SaveKind.Periodic, 42, "global", 123,
                "/run/wa-1-data/cluster/saves/GLOBAL/g00000001-s00000001.mps", wa.ControlEndpoint!);
            f.Registry.RegisterGlobalHolder(global.SaveId, onAi.Node, onAi.Epoch, 42, "global", $"/run/{onAi.Node}-data/cluster/saves/GLOBAL/g00000001-s00000001.mps", onAi.ControlEndpoint!);

            SaveId twoHosts = SaveOn(f, polaris[0], OwnNewPartition(f, polaris[0]), 1);
            CopyTo(f, twoHosts, ai[0]);
            SaveId oneHostTwice = SaveOn(f, polaris[0], OwnNewPartition(f, polaris[0]), 1);
            CopyTo(f, oneHostTwice, polaris[1]);
            f.Registry.ReportCorruption(oneHostTwice, polaris[1], onPolaris.Node, onPolaris.Epoch);
            SaveId corruptOnAi = SaveOn(f, ai[0], OwnNewPartition(f, ai[0]), 1);
            CopyTo(f, corruptOnAi, polaris[0]);
            f.Registry.ReportCorruption(corruptOnAi, ai[0], onPolaris.Node, onPolaris.Epoch);
            SaveId archived = ArchivedSaveOn(f, ai[0]);
            Check(f.Registry.ArchivedPartitionCount == 1, "the split parent's save is archived");
            // The other ai node stops for good and its row is released: only the node history knows it ran on ai.
            SaveId released = SaveOn(f, ai[1], OwnNewPartition(f, ai[1]), 1);
            NodeControlChange change = f.Registry.RecycleNode(ai[1], now: false, timeout: null, "loopback", "rotator", "rekey");
            BootServingOnAi(f, change.Replacement!);
            f.DrainToEmpty(ai[1]);
            f.Report(ai[1], SpawnState.Gone);
            f.Registry.RemoveRowNaively(ai[1]);
            Check(f.History(ai[1])?.Host == "ai" && f.Registry.Nodes().All(x => x.Node != ai[1]), ai[1] + " is in the history only");

            f.Reopen();
            long idleCommits = f.Registry.StoreCommits;
            string before = JsonSerializer.Serialize(f.Registry.Saves());
            Dictionary<string, byte[]> archive = f.Registry.ArchivedRowBytes();
            Check(f.Registry.Saves().All(x => x.LocationHolders.Values.All(holder => GatewayRegistry.HostOfHolder(holder) == null))
                && f.Registry.HostStoreAliases().Count == 0, "switch off: a load changes nothing");

            // The store directory is the sign that the files were moved: without it the switch alone re-keys nothing.
            GatewayRegistry.HostStoreEnabled = true;
            f.Reopen();
            Check(JsonSerializer.Serialize(f.Registry.Saves()) == before && f.Registry.StoreCommits == idleCommits && f.Registry.HostStoreAliases().Count == 0,
                "no store directory on the gateway host: the catalogue is left as it is");
            Directory.CreateDirectory(Path.Combine(f.Registry.RunRoot, "saves", "polaris"));
            // The files are moved (every save's, the archived one's too); a location is re-keyed only on that evidence
            // (host-store-unmoved has the rest of that rule).
            MoveIntoStores(f, "polaris", "ai");
            StoreFile(GatewayRegistry.HostStorePath(f.Registry.RunRoot, "ai", archived), 20_000);
            f.Reopen();
            SaveRegistration Save(SaveId id) => f.Registry.Saves().Single(x => x.SaveId == id);
            Check(JsonSerializer.Serialize(f.Registry.Saves()) != before && f.Registry.Saves().All(x => x.LocationHolders.Values.All(holder => GatewayRegistry.HostOfHolder(holder) != null)),
                "every in-memory save is held by hosts after the load");
            Check(HeldByHosts(f, Save(legacy), "polaris"), "holders without a host are on the gateway host; two copies on it are one location");
            Check(HeldByHosts(f, Save(twoHosts), "polaris", "ai"), "copies on two hosts stay two locations");
            Check(HeldByHosts(f, Save(oneHostTwice), "polaris") && Save(oneHostTwice).CorruptHolders.Count == 0,
                "a host with one intact and one corrupt copy holds an intact one");
            Check(HeldByHosts(f, Save(corruptOnAi), "polaris", "ai") && Save(corruptOnAi).CorruptHolders.SetEquals([GatewayRegistry.HostHolder("ai")]),
                "a host whose only copy is corrupt is a corrupt holder");
            Check(HeldByHosts(f, Save(released), "ai"), "a released row's host comes from the node history, not the gateway host");
            Check(f.Row(ai[2]).Host == "polaris" && HeldByHosts(f, Save(misreported), "ai") && f.Registry.HostStoreAliases()[ai[2]] == "ai",
                "the plan's host beats the host a node reported about itself");
            GlobalSaveRegistration rekeyed = f.Registry.GlobalSaves().Single();
            Check(rekeyed.Locations.SetEquals(new[] { "polaris", "ai" }.Select(host => GatewayRegistry.HostStorePath(f.Registry.RunRoot, host, rekeyed.SaveId)))
                && rekeyed.LocationHolders.Values.Order().SequenceEqual(["host:ai", "host:polaris"]) && rekeyed.HolderEndpoints.Count == 0,
                "the global save is held by both hosts at their GLOBAL store paths");
            Check(f.Registry.Saves().All(x => f.Registry.SaveReachable(x.SaveId)), "every save stays reachable");
            foreach (string host in new[] { "polaris", "ai" })
                Check(f.Registry.Nodes().Single(x => x.Node == GatewayRegistry.HostHolder(host)) is { Role: NodeRole.Regular, State: NodeState.Empty } row && row.Host == host,
                    $"the rollback row of {host}: Regular, Empty, with its host");
            Dictionary<string, string> aliases = f.Registry.HostStoreAliases();
            Check(aliases["node-1"] == "polaris" && aliases["node-2"] == "polaris" && aliases[polaris[0]] == "polaris" && aliases[ai[0]] == "ai"
                && aliases[ai[1]] == "ai" && aliases["wa-1"] == "polaris" && aliases.Keys.All(x => GatewayRegistry.HostOfHolder(x) == null),
                "every id of a row or of the history is frozen to its host: " + string.Join(",", aliases.Select(x => x.Key + "=" + x.Value)));
            Check(f.Registry.StoreCommits == idleCommits + 1, $"the whole re-key is one transaction ({f.Registry.StoreCommits - idleCommits})");
            Dictionary<string, byte[]> archiveAfter = f.Registry.ArchivedRowBytes();
            Check(archiveAfter.Count == archive.Count && archive.All(x => archiveAfter[x.Key].AsSpan().SequenceEqual(x.Value)), "archived rows are not rewritten");

            // Idempotent: a second load writes nothing. A save a node registers meanwhile (the nodes still write into their
            // own dirs until step 4) is re-keyed by the next load.
            string once = JsonSerializer.Serialize(f.Registry.Saves()) + JsonSerializer.Serialize(f.Registry.GlobalSaves()) + JsonSerializer.Serialize(aliases);
            f.Reopen();
            Check(f.Registry.StoreCommits == idleCommits && once == JsonSerializer.Serialize(f.Registry.Saves()) + JsonSerializer.Serialize(f.Registry.GlobalSaves())
                + JsonSerializer.Serialize(f.Registry.HostStoreAliases()), "a second load re-keys nothing and writes nothing");
            // A host holder whose row is gone (nothing re-keys that save any more) gets its row back.
            f.Registry.RemoveRowNaively("host:ai");
            f.Reopen();
            Check(f.Registry.Nodes().Any(x => x.Node == "host:ai" && x.State == NodeState.Empty && x.Host == "ai") && f.Registry.StoreCommits == idleCommits + 1,
                "a missing host row is repaired by the load");
            f.Reopen();
            Check(f.Registry.StoreCommits == idleCommits, "and the repair is idempotent");
            SaveId later = SaveOn(f, polaris[0], twoHosts.Partition, 2);
            MoveIntoStores(f, "polaris");
            f.Reopen();
            Check(HeldByHosts(f, Save(later), "polaris") && HeldByHosts(f, Save(twoHosts), "polaris", "ai"), "the next load re-keys what nodes registered since");

            // The archived save names the first ai node. Its row is released and it never was in the history, so only the
            // frozen alias still says ai (the fallback would be the gateway host, polaris).
            f.Registry.RemoveRowNaively(ai[0]);
            Check(f.History(ai[0]) == null, ai[0] + " is in neither the rows nor the history");
            Check(f.Registry.SaveReachable(archived) && f.Registry.ArchivedPartitionCount == 0 && HeldByHosts(f, Save(archived), "ai"),
                "an archived save is re-keyed through the alias map when its partition comes back");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }

        // Without node control nothing is a gone holder, so the rollback form would not serve: the re-key does not run.
        using var plain = new NodeControlFixture("host-rekey-plain") { GatewayHost = "polaris" };
        try
        {
            BootServing(plain, "node-1", host: null);
            SaveId only = SaveOn(plain, "node-1", OwnNewPartition(plain, "node-1"), 1);
            GatewayRegistry.HostStoreEnabled = true;
            plain.Reopen();
            Check(plain.Registry.Saves().Single().LocationHolders.Single() is { Key: var path, Value: "node-1" } && path == SavePathOf("node-1", only)
                && plain.Registry.Nodes().All(x => GatewayRegistry.HostOfHolder(x.Node) == null), "no node control: the catalogue is left as it is");
        }
        finally { GatewayRegistry.HostStoreEnabled = false; }
        Console.WriteLine("host-store-rekey: node holders (no host, row host, history host, WA) re-keyed to host holders at their store paths in one transaction; "
            + "same-host copies merged, corruption kept per host; idempotent; archived rows untouched and re-keyed through the frozen alias map on rehydrate; "
            + "off without the store directory and without node control");
    }

    // T-0340 step 1's gate: the package BEFORE this ticket loads a re-keyed registry.db with every save reachable. That
    // package is this code with the switch off (off, nothing of HostStore.cs runs). A host holder's synthetic row is a
    // gone holder to it: usable, proxied by a live node of that host at the exact catalogued store path, which the
    // node-side check accepts (a .mps under the run root). It copies the saves back to node holders at the evacuation
    // rate, drops a host location only once live holders cover the save, deletes nothing, and the next load with the
    // switch on re-keys again. Earned reds: without the gone-holder proxies, and without the synthetic row, nothing
    // serves a host holder's file. Limits found: a host-held global save has no replica source (no proxy for globals).
    private static void TestHostStorePreviousPackage()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-previous-package: " + what); }
        string top = Directory.CreateTempSubdirectory("host-store-").FullName;
        try
        {
            string root = Path.Combine(top, "run");
            (string, int, int)[] hosts = [("polaris", 2, 0), ("ai", 1, 0)];
            using var f = new NodeControlFixture("host-previous", Path.Combine(root, "gateway-registry")) { GatewayHost = "polaris" };
            f.Registry.GatewayHost = "polaris";
            Directory.CreateDirectory(Path.Combine(root, "saves", "polaris"));
            string[] On(string host) => [.. f.View().Nodes.Where(x => x.Host == host).Select(x => x.Node).OrderBy(NodeControlFixture.Number)];
            void BootAll()
            {
                foreach (string node in On("polaris")) BootServing(f, node);
                foreach (string node in On("ai")) BootServingOnAi(f, node);
            }
            NodeRegistration BootAuthority() => f.Registry.RegisterNode(Join("wa-1", "127.0.0.1:28500", Guid.NewGuid())
                with { Role = NodeRole.WA, Host = "polaris", Pid = 77, ControlEndpoint = "http://127.0.0.1:29419" });
            string Store(string host, SaveId id) => GatewayRegistry.HostStorePath(root, host, id);
            SaveRegistration Save(SaveId id) => f.Registry.Saves().Single(x => x.SaveId == id);

            // The world before the upgrade: saves on polaris only, on both hosts, on ai only; a global save; two archived partitions.
            f.Apply(hosts);
            BootAll();
            string polaris = On("polaris")[0], ai = On("ai")[0];
            NodeRegistration wa = BootAuthority();
            GlobalSaveRegistration global = f.Registry.RegisterGlobalSave(new(wa.AuthorityGeneration, 1), wa.Node, wa.Epoch, SaveKind.Periodic, 42, "global", 123,
                Path.Combine(root, "wa-1-data", "cluster", "saves", "GLOBAL", "g00000001-s00000001.mps"), wa.ControlEndpoint!);
            SaveId onPolaris = SaveOn(f, polaris, OwnNewPartition(f, polaris), 1);
            SaveId onBoth = SaveOn(f, polaris, OwnNewPartition(f, polaris), 1);
            CopyTo(f, onBoth, ai);
            SaveId onAi = SaveOn(f, ai, OwnNewPartition(f, ai), 1);
            SaveId archivedNew = ArchivedSaveOn(f, polaris), archivedLegacy = ArchivedSaveOn(f, ai);
            Check(f.Registry.ArchivedPartitionCount == 2, "two partitions archived");
            SaveId[] inMemory = [.. f.Registry.Saves().Select(x => x.SaveId)];
            Check(inMemory.All(f.Registry.SaveReachable), "before: every save is reachable");

            // Upgrade: stop, load with the switch on. One archived partition comes back (re-keyed) and is archived again in
            // the new form; the other stays in the archive in the old form.
            MoveIntoStores(f, "polaris", "ai");
            StoreFile(GatewayRegistry.HostStorePath(f.Registry.RunRoot, "polaris", archivedNew), 20_000);
            GatewayRegistry.HostStoreEnabled = true;
            StopAndStart(f, hosts);
            Check(f.Registry.Saves().All(x => x.LocationHolders.Values.All(holder => GatewayRegistry.HostOfHolder(holder) != null)), "upgraded: every in-memory save is held by hosts");
            Check(f.Registry.PlanRetention().Length == 0 && !f.Registry.SaveReachable(onPolaris), "upgraded: no node runs yet - the stores are dark, retention does not plan");
            BootAll();
            BootAuthority();
            Check(f.Registry.SaveReachable(archivedNew) && HeldByHosts(f, Save(archivedNew), "polaris"), "upgraded: the rehydrated save is re-keyed");
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            Check(f.Registry.ArchivedPartitionCount == 2, "upgraded: it is archived again, as a host-held row");

            // Rollback: the previous package opens the same database; the nodes carry on.
            GatewayRegistry.HostStoreEnabled = false;
            f.Reopen();
            foreach (string node in f.Live()) f.Heartbeat(node, serving: true);
            string proxy = On("polaris")[0], remote = On("ai")[0];
            NodeRegistration authority = f.Row("wa-1");
            Check(f.Registry.Saves().Select(x => x.SaveId).Order().SequenceEqual(inMemory.Order()) && f.Registry.ArchivedPartitionCount == 2,
                "previous package: the same saves are catalogued");
            Check(inMemory.All(f.Registry.SaveReachable), "previous package: every save is reachable");
            Check(new[] { "host:ai", "host:polaris" }.All(f.Registry.GoneRowIds().Contains), "previous package: the host rows are gone holders: " + string.Join(",", f.Registry.GoneRowIds()));
            foreach (SaveId id in inMemory)
            {
                SaveRegistration served = f.Registry.ServedForTest(id);
                Check(Fetchable(f, served) && served.HolderProxies!.All(x => x.Value.Node == (x.Key == "host:ai" ? remote : proxy)),
                    $"previous package: {id} is served by a live node of each holding host");
            }
            GatewayRegistry.ProxyGoneHolders = false;
            try { Check(!Fetchable(f, f.Registry.ServedForTest(onPolaris)), "RED expected: without the gone-holder proxies nothing serves a host holder's file"); }
            finally { GatewayRegistry.ProxyGoneHolders = true; }

            // Recovery claims across hosts carry the proxy; the proxied grant binds the store path and a proxy on that host.
            RecoveryClaim claim = f.Registry.ClaimRecoveries(remote, f.Row(remote).Epoch, [onPolaris.Partition]).Single();
            Check(claim.ResolveCover.Single().Save is { } cover && cover.HolderProxies!.Single() is { Key: "host:polaris" } source && source.Value.Node == proxy
                && cover.Locations.SetEquals([Store("polaris", onPolaris)]), "previous package: ai's recovery claim names the polaris proxy and the store path");
            RecoveryClaim back = f.Registry.ClaimRecoveries(proxy, f.Row(proxy).Epoch, [onAi.Partition]).Single();
            Check(back.ResolveCover.Single().Save.HolderProxies!.Single() is { Key: "host:ai" } aiSource && aiSource.Value.Node == remote,
                "previous package: polaris' recovery claim names the ai proxy");
            string location = Store("polaris", onPolaris);
            GrantRequest Request(string receiver, string? at) => new("save", "", onPolaris.Partition, onPolaris.Generation, onPolaris.Sequence, remote, f.Row(remote).Epoch, receiver, at);
            TransferGrant grant = f.Registry.IssueGrant(Request(proxy, location));
            f.Registry.ValidateGrant(new(grant.Token, "save", "", onPolaris.Partition, onPolaris.Generation, onPolaris.Sequence, proxy, f.Row(proxy).Epoch, location));
            AssertConflict(() => f.Registry.IssueGrant(Request(remote, location)), "a proxy on ai for the polaris store");
            AssertConflict(() => f.Registry.IssueGrant(Request(proxy, Store("ai", onPolaris))), "a store path the catalogue does not hold");
            TransferGrant plain = f.Registry.IssueGrant(Request("host:polaris", null));
            AssertConflict(() => f.Registry.ValidateGrant(new(plain.Token, "save", "", onPolaris.Partition, onPolaris.Generation, onPolaris.Sequence, proxy, f.Row(proxy).Epoch, null)),
                "a grant naming the host row cannot be used by a node");
            // The proxy's own check (node side) accepts the store path: a .mps under the run root of its data dir.
            string data = Path.Combine(root, proxy + "-data");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(Path.GetDirectoryName(location)!);
            File.WriteAllText(location, "x");
            Check(ClusterProtocol.ProxiedSavePath.Resolve(location, data) == location, "previous package: a proxy may read the store path");

            // The global save: offered to the World Authority at its store path (read from that host's disk), but it has
            // no replica source - the previous package proxies partition saves only. The next global save replicates.
            Check(f.Registry.NewestGlobalSaveForRecovery() is { } offered && offered.SaveId == global.SaveId
                && offered.Locations.SetEquals([GatewayRegistry.HostStorePath(root, "polaris", global.SaveId)]), "previous package: the global save is offered at its store path");

            // The replica pumps copy the saves back to node holders (evacuation); retention deletes nothing; the release
            // pass drops a host location only where live holders on both hosts cover the save.
            int copies = 0;
            var globals = new List<ReplicaDirective>();
            void Pump()
            {
                for (int round = 0, was = -1; round < 40 && copies != was; round++)
                {
                    was = copies;
                    foreach (string node in f.Live())
                        foreach (ReplicaDirective work in f.Registry.ReplicaWork(node, f.Row(node).Epoch))
                        {
                            if (work.Global) { globals.Add(work); continue; }
                            Check(work.Sources.Length != 0 && work.Sources.All(x => x.Proxy == null ? f.Live().Contains(x.Holder)
                                : f.Live().Contains(x.Proxy) && x.Location == (GatewayRegistry.HostOfHolder(x.Holder) is { } host ? Store(host, work.SaveId) : SavePathOf(x.Holder, work.SaveId))),
                                "previous package: every replica source is a live holder, or a live proxy at the store path (at the node path for a stopped node)");
                            CopyTo(f, work.SaveId, node);
                            copies++;
                        }
                }
            }
            Pump();
            Check(globals.Count == 0, "LIMIT: the previous package finds no replica source for a host-held global save");
            Check(f.Registry.PlanRetention().Length == 0, "previous package: retention deletes nothing");
            string[] releasedRows = f.Registry.ReleaseGoneRowsNow();
            Check(f.Registry.Saves().Select(x => x.SaveId).Order().SequenceEqual(inMemory.Order()) && inMemory.All(f.Registry.SaveReachable),
                "previous package: after evacuation every save is still catalogued and reachable");
            Check(f.Registry.Saves().All(x => x.LocationHolders.Values.All(holder => f.Live().Contains(holder))
                && x.LocationHolders.All(held => held.Key == SavePathOf(held.Value, x.SaveId)) && x.LocationHolders.Count >= 2),
                $"previous package: {copies} copies moved every save back to live node holders at their own paths");
            Check(releasedRows.Contains("host:ai") && f.Registry.GoneRowIds().Contains("host:polaris"),
                "previous package: a host row nothing names is released, one the archive names is kept: released " + string.Join(",", releasedRows));
            // Both archive forms come back reachable: the host-held row through the polaris proxy, the old row through its node's.
            Check(f.Registry.SaveReachable(archivedNew) && Fetchable(f, f.Registry.ServedForTest(archivedNew))
                && f.Registry.ServedForTest(archivedNew).HolderProxies!.Single() is { Key: "host:polaris" },
                "previous package: an archived host-held save comes back served by its host's proxy");
            Check(f.Registry.SaveReachable(archivedLegacy) && Fetchable(f, f.Registry.ServedForTest(archivedLegacy)),
                "previous package: an archived save in the old form comes back as before");
            // New work registers as it always did.
            SaveId fresh = SaveOn(f, proxy, OwnNewPartition(f, proxy), 1);
            Check(Save(fresh).LocationHolders.Single() is { Value: var holder } && holder == proxy, "previous package: a new save is held by its node");
            GlobalSaveRegistration next = f.Registry.RegisterGlobalSave(new(authority.AuthorityGeneration, authority.AuthorityGeneration == wa.AuthorityGeneration ? 2 : 1),
                authority.Node, authority.Epoch, SaveKind.Periodic, 43, "global2", 124, Path.Combine(root, "wa-1-data", "cluster", "saves", "GLOBAL", "next.mps"), authority.ControlEndpoint!);
            Pump();
            Check(globals.Count != 0 && globals.All(x => x.SaveId.Generation == next.SaveId.Generation && x.SaveId.Sequence == next.SaveId.Sequence && x.Sources.Single().Holder == "wa-1"),
                "previous package: the next global save replicates from the World Authority");

            // Roll forward again: the load re-keys what the previous package moved back (the files it wrote are moved first).
            MoveIntoStores(f, "polaris", "ai");
            GatewayRegistry.HostStoreEnabled = true;
            f.Reopen();
            Check(f.Registry.Saves().All(x => x.LocationHolders.Values.All(h => GatewayRegistry.HostOfHolder(h) != null) && f.Registry.SaveReachable(x.SaveId))
                && HeldByHosts(f, Save(onPolaris), "polaris", "ai") && f.Registry.Nodes().Any(x => x.Node == "host:ai"),
                "roll forward: the catalogue is re-keyed again and the released host row is back");
            // Earned red for the synthetic row: to the previous package a host holder without it has no proxy.
            GatewayRegistry.HostStoreEnabled = false;
            f.Reopen();
            foreach (string node in f.Live()) f.Heartbeat(node, serving: true);
            Check(Fetchable(f, f.Registry.ServedForTest(fresh)), "previous package again: a host-held save is served through its host's proxy");
            f.Registry.RemoveRowNaively("host:polaris");
            f.Registry.RemoveRowNaively("host:ai");
            Check(!Fetchable(f, f.Registry.ServedForTest(fresh)), "RED expected: without the host rows nothing serves a host holder's file");
            Console.WriteLine($"host-store-previous-package: PROVED - the switch-off code loads the re-keyed store with all {inMemory.Length} in-memory and 2 archived saves reachable; "
                + $"host rows are gone holders proxied at the store path across hosts; {copies} evacuation copies back to node holders, nothing deleted; "
                + "re-keyed again on the next load. LIMIT: a host-held global save has no replica source until the next global save");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            Directory.Delete(top, true);
        }
    }

    // T-0340 step 2. With node holders every stopped node returns the saves it held to the replica queue (earned red, the
    // control arm: S saves on two nodes of two hosts, one node recycled, its saves queued). With host holders a stop
    // queues nothing, a crashed node's registered files stay usable, and what the queue wants follows the host store
    // states: Dark and Held copies count but are not offered, a Lost host's copies stop counting and a node of a host
    // without a copy is handed the save, never a node of a host that has one. The copy target is the plan's.
    private static void TestHostStoreNodeStop()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-node-stop: " + what); }
        const int Saves = 12;
        static int WorkFor(NodeControlFixture f, string node) => f.Registry.ReplicaWork(node, f.Row(node).Epoch).Count(x => !x.Global);

        // Control arm, the catalogue as it is today: node holders at node paths.
        int queuedByNodeStop;
        using (var legacy = new NodeControlFixture("host-stop-legacy"))
        {
            legacy.Apply(("polaris", 2, 0), ("ai", 2, 0));
            string[] polaris = PlannedOn(legacy, "polaris"), ai = PlannedOn(legacy, "ai");
            foreach (string node in polaris) BootServing(legacy, node);
            foreach (string node in ai) BootServing(legacy, node, host: "ai");
            for (int i = 0; i < Saves; i++)
                CopyTo(legacy, SaveOn(legacy, polaris[i % 2], OwnNewPartition(legacy, polaris[i % 2]), 1), ai[i % 2]);
            Check(legacy.Registry.ReplicaQueueDepth() == 0, "node holders: every save has its two copies before the stop");
            RecycleAndReplace(legacy, polaris[0], "polaris");
            queuedByNodeStop = legacy.Registry.ReplicaQueueDepth();
            Check(queuedByNodeStop == Saves / 2, $"RED expected: with node holders a stop queues every save the node held ({queuedByNodeStop} of {Saves})");
        }

        NodeControlFixture f = HostStoreFixture("host-stop", out string top, ("polaris", 2, 0), ("ai", 2, 0), ("sdx", 1, 0));
        try
        {
            string[] polaris = PlannedOn(f, "polaris"), ai = PlannedOn(f, "ai");
            string sdx = PlannedOn(f, "sdx").Single();
            var saves = new List<SaveId>();
            for (int i = 0; i < Saves; i++)
            {
                SaveId id = SaveInStore(f, polaris[i % 2], "polaris", OwnNewPartition(f, polaris[i % 2]));
                CopyIntoStore(f, id, ai[i % 2], "ai");
                saves.Add(id);
            }
            Check(f.Registry.Saves().All(x => HeldByHosts(f, x, "polaris", "ai")), "a node that writes into the store registers its host as the holder");
            Check(f.Registry.ReplicaQueueDepth() == 0, "two planned hosts hold every save: nothing waits (a third host changes nothing, the target is two)");

            // A stop.
            string replacement = RecycleAndReplace(f, polaris[0], "polaris");
            Check(f.Registry.ReplicaQueueDepth() == 0 && f.Live().All(node => WorkFor(f, node) == 0),
                $"a node stop queues 0 saves ({f.Registry.ReplicaQueueDepth()}; {queuedByNodeStop} with node holders)");
            Check(f.Registry.Saves().All(x => f.Registry.SaveReachable(x.SaveId)), "every save stays reachable after the stop");

            // A crash: the files a Dead node registered stay usable, served by another node of its host.
            Kill(f, polaris[1]);
            Check(f.Row(polaris[1]).State == NodeState.Dead && f.Registry.HostStoreStateOf("polaris") == HostStoreState.Serving, "the crashed node is Dead, its host still serves");
            SaveRegistration served = f.Registry.ServedForTest(saves[1]);
            Check(f.Registry.ReplicaQueueDepth() == 0 && served.HolderProxies!["host:polaris"].Node == replacement && Fetchable(f, served),
                "a Dead node's registered files stay usable through its host's store");

            // Dark: no live node on polaris. The copies count, nothing is re-replicated, the store is not offered.
            Kill(f, replacement);
            served = f.Registry.ServedForTest(saves[0]);
            Check(f.Registry.HostStoreStateOf("polaris") == HostStoreState.Dark && f.Registry.ReplicaQueueDepth() == 0 && WorkFor(f, sdx) == 0,
                "a dark store's copies count: nothing is queued");
            Check(served.HolderProxies!.Keys.SequenceEqual(["host:ai"]) && f.Registry.SaveReachable(saves[0]), "a dark store is not offered; the other host's is");
            foreach (string node in PlannedOn(f, "polaris").Where(node => f.Registry.Nodes().All(x => x.Node != node))) BootServing(f, node);
            Check(f.Registry.HostStoreStateOf("polaris") == HostStoreState.Serving, "polaris serves again once a node runs there");
            string onPolaris = PlannedOn(f, "polaris").First(node => f.Row(node).State == NodeState.Active);

            // Held: sdx goes silent inside a network window. Its copy counts and is not offered; a recovery that needs it waits.
            f.Registry.LeaseHold = new NetworkWindows([(f.Now.TimeOfDay, f.Now.TimeOfDay + TimeSpan.FromMinutes(30))], TimeSpan.FromSeconds(30), "polaris", TimeZoneInfo.Utc);
            ulong waiting = OwnNewPartition(f, sdx);
            SaveId older = SaveInStore(f, sdx, "sdx", waiting);
            CopyIntoStore(f, older, onPolaris, "polaris");
            SaveId newest = SaveInStore(f, sdx, "sdx", waiting, 2);
            Check(f.Registry.ReplicaQueueDepth() == 1, "a save on one host waits for its second copy");
            f.Registry.ReleasePartition(waiting, sdx, f.Row(sdx).Epoch, newest.Generation);
            Kill(f, sdx);
            Check(f.Row(sdx).State == NodeState.Active && f.Registry.HostStoreStateOf("sdx") == HostStoreState.Held, "sdx is held by the window");
            Check(f.Registry.ServedForTest(older).HolderProxies!.Keys.SequenceEqual(["host:polaris"]) && WorkFor(f, onPolaris) == 0,
                "a held store is not offered as a source");
            Check(f.Registry.ClaimRecoveries(onPolaris, f.Row(onPolaris).Epoch, [waiting]).Length == 0,
                "recovery waits for the newest save in the held store instead of restoring the older one");
            f.Silent.Remove(sdx);
            f.Advance(1);
            Check(f.Registry.HostStoreStateOf("sdx") == HostStoreState.Serving, "sdx is back");
            CopyIntoStore(f, f.Registry.ReplicaWork(onPolaris, f.Row(onPolaris).Epoch).Single(x => !x.Global && x.SaveId == newest).SaveId, onPolaris, "polaris");
            f.Registry.LeaseHold = null;

            // Lost: the next start's hosts file has no ai. Its copies stop counting; only a node of a host without a copy
            // is handed the save.
            StopAndStart(f, ("polaris", 2, 0), ("sdx", 1, 0));
            foreach (string node in PlannedOn(f, "polaris")) BootServing(f, node);
            onPolaris = PlannedOn(f, "polaris")[0];
            sdx = PlannedOn(f, "sdx").Single();
            BootServingRemote(f, sdx, "sdx");
            Check(f.Registry.HostStoreStateOf("ai") == HostStoreState.Lost && f.Registry.ReplicaQueueDepth() == Saves,
                $"a lost host's copies stop counting: {f.Registry.ReplicaQueueDepth()} of {Saves} saves wait");
            Check(WorkFor(f, onPolaris) == 0, "a node of a host that holds the save is not handed it");
            int copied = 0;
            for (int round = 0; round <= Saves; round++)
                foreach (ReplicaDirective work in f.Registry.ReplicaWork(sdx, f.Row(sdx).Epoch).Where(x => !x.Global))
                {
                    Check(work.Sources.Single() is { Holder: "host:polaris" } source && source.Proxy != null && f.Row(source.Proxy).State == NodeState.Active
                        && source.Location == GatewayRegistry.HostStorePath(f.Registry.RunRoot, "polaris", work.SaveId), "the source is a live polaris node at the polaris store path");
                    CopyIntoStore(f, work.SaveId, sdx, "sdx");
                    copied++;
                }
            Check(copied == Saves && f.Registry.ReplicaQueueDepth() == 0, $"the sdx node copies them ({copied}) and the queue is empty");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }

        // One planned host: the target is one copy (the old second copy on the same disk guarded against a per-file fault only).
        NodeControlFixture single = HostStoreFixture("host-stop-single", out string singleTop, ("polaris", 2, 0));
        try
        {
            string node = PlannedOn(single, "polaris")[0];
            SaveInStore(single, node, "polaris", OwnNewPartition(single, node));
            Check(single.Registry.ReplicaQueueDepth() == 0, "single host: one copy is the target");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            single.Dispose();
            Directory.Delete(singleTop, true);
        }
        Console.WriteLine($"host-store-node-stop: a node stop queues {queuedByNodeStop} of {Saves} saves with node holders, 0 with host holders; a Dead node's files stay usable; "
            + "dark and held stores count but are not offered (recovery waits for a held one); a lost host's saves go to a host without a copy; single host = one copy");
    }

    // T-0340 step 3. A save grant for a host-held save is bound to (save id, host), not to one receiver: it lists the
    // host's servers in the order to try them (spares first, Active rotated per grant, Closed last) and any registered,
    // non-Dead node the plan runs on that host may consume it, once. Refused: a node of another host, a stale epoch, a
    // stopped node whose port a new node reused, a Dead node, a replay, an expired token. A refusal does not burn the
    // token, so the requester retries the next server. The current node plugin's request (a receiver and the catalogued
    // location) gets such a grant unchanged on the wire but for the two added fields.
    private static void TestHostStoreGrant()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-grant: " + what); }
        NodeControlFixture f = HostStoreFixture("host-grant", out string top, ("polaris", 3, 1), ("ai", 1, 0));
        try
        {
            string[] polaris = PlannedOn(f, "polaris");
            string spare = polaris.Single(node => f.Row(node).Spare), ai = PlannedOn(f, "ai").Single();
            string[] active = [.. polaris.Where(node => node != spare)];
            SaveId save = SaveInStore(f, active[0], "polaris", OwnNewPartition(f, active[0]));
            string location = GatewayRegistry.HostStorePath(f.Registry.RunRoot, "polaris", save);
            GrantRequest Request(string receiver, string? at) => new("save", "", save.Partition, save.Generation, save.Sequence, ai, f.Row(ai).Epoch, receiver, at);
            GrantValidation By(TransferGrant grant, string node, long? epoch = null, string? at = null) =>
                new(grant.Token, "save", "", save.Partition, save.Generation, save.Sequence, node, epoch ?? f.Row(node).Epoch, at);
            static string Order(TransferGrant grant) => string.Join(",", grant.Servers!.Select(x => x.Node));

            // The server list.
            Check(f.Registry.ServedForTest(save).HolderProxies!["host:polaris"].Node == spare, "a recovery claim names the host's first server, a spare");
            TransferGrant first = f.Registry.IssueGrant(Request(spare, location));
            Check(first.Host == "polaris" && first.Location == location && Order(first) == string.Join(",", [spare, .. active]),
                "the grant is bound to the host and lists its servers, spares first: " + Order(first));
            Check(first.Servers!.All(x => x.Endpoint == f.Row(x.Node).ControlEndpoint), "each server with its control endpoint");
            TransferGrant second = f.Registry.IssueGrant(Request(spare, location));
            Check(Order(second) == string.Join(",", [spare, active[1], active[2], active[0]]), "the Active servers rotate per grant: " + Order(second));
            f.Registry.CloseNode(active[2], f.Row(active[2]).Epoch, TimeSpan.FromSeconds(60));
            TransferGrant third = f.Registry.IssueGrant(Request(spare, location));
            Check(third.Servers![0].Node == spare && third.Servers[^1].Node == active[2] && third.Servers.Length == 4, "a Closed (draining) server goes last: " + Order(third));

            // Who may consume it. None of the refusals burns the token.
            AssertConflict(() => f.Registry.ValidateGrant(By(first, ai, at: location)), "a node of another host");
            AssertConflict(() => f.Registry.ValidateGrant(By(first, active[0], f.Row(active[0]).Epoch + 1, location)), "a stale epoch");
            AssertConflict(() => f.Registry.ValidateGrant(By(first, spare, at: GatewayRegistry.HostStorePath(f.Registry.RunRoot, "ai", save))), "another location than the granted one");
            // active[1] stops for good; a new node comes up on its ports. The stopped incarnation is not a server any more.
            NodeRegistration stopped = f.Row(active[1]);
            NodeControlChange change = f.Registry.RecycleNode(active[1], now: false, timeout: null, "loopback", "rotator", "grant");
            f.DrainToEmpty(active[1]);
            f.Report(active[1], SpawnState.Gone);
            f.Pass();
            NodeRegistration reuser = f.Registry.RegisterNode(Join(change.Replacement!, stopped.Endpoint, Guid.NewGuid())
                with { Host = "polaris", Pid = 41000, ControlEndpoint = stopped.ControlEndpoint });
            f.Registry.Heartbeat(reuser.Node, reuser.Epoch, [], serving: true);
            AssertConflict(() => f.Registry.ValidateGrant(By(first, stopped.Node, stopped.Epoch, location)), "a stopped node whose port was reused");
            Kill(f, active[0]);
            Check(f.Row(active[0]).State == NodeState.Dead, "active[0] is Dead");
            AssertConflict(() => f.Registry.ValidateGrant(By(first, active[0], at: location)), "a Dead node");
            // The requester moves on to the next server: the same token, never validated so far, is good there - once.
            first = f.Registry.IssueGrant(Request(spare, location));
            AssertConflict(() => f.Registry.ValidateGrant(By(first, ai, at: location)), "the first try, at a wrong server");
            f.Registry.ValidateGrant(By(first, reuser.Node, at: location));
            AssertConflict(() => f.Registry.ValidateGrant(By(first, spare, at: location)), "a replay of a consumed token");
            // A server that computes the path itself validates without a location; a request may name the host.
            TransferGrant byHost = f.Registry.IssueGrant(Request("host:polaris", null));
            Check(byHost.Host == "polaris" && byHost.Servers!.Length != 0, "a request naming the host holder gets a host grant");
            f.Registry.ValidateGrant(By(byHost, spare));
            TransferGrant expired = f.Registry.IssueGrant(Request(spare, location));
            f.Advance(61);
            AssertConflict(() => f.Registry.ValidateGrant(By(expired, spare, at: location)), "an expired token");

            // Not granted: a store that is not catalogued for the save, a corrupt copy. A node-held save keeps the old grant.
            AssertConflict(() => f.Registry.IssueGrant(Request("host:ai", null)), "a host that does not hold the save");
            f.Registry.ReportCorruption(save, "host:polaris", ai, f.Row(ai).Epoch);
            AssertConflict(() => f.Registry.IssueGrant(Request(spare, location)), "a corrupt copy");
            SaveId nodeHeld = SaveOn(f, reuser.Node, OwnNewPartition(f, reuser.Node), 1);
            TransferGrant plain = f.Registry.IssueGrant(new("save", "", nodeHeld.Partition, nodeHeld.Generation, nodeHeld.Sequence, ai, f.Row(ai).Epoch, reuser.Node));
            Check(plain.Host == null && plain.Servers == null && plain.Receiver == reuser.Node && !JsonSerializer.Serialize(plain).Contains("\"Host\"")
                && !JsonSerializer.Serialize(plain).Contains("\"Servers\""), "a node-held save keeps the receiver-bound grant, unchanged on the wire");
            AssertConflict(() => f.Registry.ValidateGrant(new(plain.Token, "save", "", nodeHeld.Partition, nodeHeld.Generation, nodeHeld.Sequence, spare, f.Row(spare).Epoch)),
                "and only its receiver consumes it");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("host-store-grant: grants bound to (save, host) with an ordered server list (spares, rotated Active, Closed); "
            + "wrong host, stale epoch, reused port, Dead node, replay and expiry refused; an unconsumed token retried on the next server");
    }

    // T-0340 step 4, the shared file rules (ClusterProtocol.HostSaveStore, the code both plugins run): the node's path is
    // the registry's; an install never replaces a file that is already the right one, under contention exactly one
    // writer puts it there; a second save with other content under the same id is refused; a fetched copy replaces a
    // damaged file. No temporary file is left behind.
    private static void TestHostStoreInstall()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-install: " + what); }
        var id = new SaveId(0x2A, 3, 7);
        Check(ClusterProtocol.HostSaveStore.PartitionSave(ClusterProtocol.HostSaveStore.NodeSaves("/run/node-7-data", "polaris", store: false), id.Partition, id.Generation, id.Sequence)
            == SavePathOf("node-7", id), "not told to use the store: a node's save path is the one in its data dir");
        string saves = ClusterProtocol.HostSaveStore.NodeSaves("/run/node-7-data/", "polaris", store: true);
        Check(ClusterProtocol.HostSaveStore.PartitionSave(saves, id.Partition, id.Generation, id.Sequence) == GatewayRegistry.HostStorePath("/run", "polaris", id)
            && ClusterProtocol.HostSaveStore.GlobalSave(saves, 2, 11) == GatewayRegistry.HostStorePath("/run", "polaris", new GlobalSaveId(2, 11)),
            "told to use the store: a node computes the store paths the registry computes");
        // A store-mode node asked for a save that was never moved reads it where it still is.
        string mixed = Directory.CreateTempSubdirectory("host-held-").FullName;
        try
        {
            string store = Path.Combine(mixed, "store.mps"), legacy = Path.Combine(mixed, "legacy.mps");
            Check(ClusterProtocol.HostSaveStore.Held(store, legacy) == store, "neither file: the store path (the read fails there, as it did)");
            File.WriteAllText(legacy, "old");
            Check(ClusterProtocol.HostSaveStore.Held(store, legacy) == legacy, "only the node-dir file exists: the node reads the legacy location");
            File.WriteAllText(store, "new");
            Check(ClusterProtocol.HostSaveStore.Held(store, legacy) == store, "the store file wins once it is there");
        }
        finally { Directory.Delete(mixed, true); }

        string top = Directory.CreateTempSubdirectory("host-install-").FullName;
        try
        {
            const int Writers = 8;
            string Final(string name) => Path.Combine(top, "P-1", name);
            Directory.CreateDirectory(Path.Combine(top, "P-1"));
            byte[] Bytes(int seed) => [.. Enumerable.Range(0, 20_000).Select(i => (byte)(i * 31 + seed))];
            bool? Judge(string path, byte[] wanted) => File.ReadAllBytes(path) is var found && found.AsSpan().SequenceEqual(wanted) ? true : found.Length == wanted.Length ? false : null;
            // Each writer: its own temporary file, then the install; all released together.
            (int Installed, int Already, int Refused) Race(string final, Func<int, byte[]> content, bool replaceOther)
            {
                int installed = 0, already = 0, refused = 0;
                using var start = new ManualResetEventSlim();
                Thread[] threads = [.. Enumerable.Range(0, Writers).Select(writer => new Thread(() =>
                {
                    byte[] bytes = content(writer);
                    string temporary = ClusterProtocol.HostSaveStore.Temporary(final, "node-" + writer);
                    File.WriteAllBytes(temporary, bytes);
                    start.Wait();
                    try
                    {
                        if (ClusterProtocol.HostSaveStore.Install(temporary, final, path => Judge(path, bytes), replaceOther)) Interlocked.Increment(ref installed);
                        else Interlocked.Increment(ref already);
                    }
                    catch (IOException) { Interlocked.Increment(ref refused); }
                }))];
                foreach (Thread thread in threads) thread.Start();
                start.Set();
                foreach (Thread thread in threads) thread.Join();
                return (installed, already, refused);
            }

            for (int round = 0; round < 25; round++)
            {
                // Two downloaders (and six more) of one save: the same bytes.
                string same = Final($"same-{round}.mps");
                (int installed, int already, int refused) = Race(same, _ => Bytes(1), replaceOther: true);
                Check(installed == 1 && already == Writers - 1 && refused == 0 && File.ReadAllBytes(same).AsSpan().SequenceEqual(Bytes(1)),
                    $"the same file from {Writers} nodes: one installs it, the others find it there ({installed}/{already}/{refused})");
                // Two writers of one save id with different content: the first stays, the others are refused.
                string differ = Final($"differ-{round}.mps");
                (installed, already, refused) = Race(differ, writer => Bytes(writer), replaceOther: false);
                byte[] kept = File.ReadAllBytes(differ);
                Check(installed == 1 && already == 0 && refused == Writers - 1 && Enumerable.Range(0, Writers).Any(writer => kept.AsSpan().SequenceEqual(Bytes(writer))),
                    $"different content under one id: one writer wins whole, the others are refused ({installed}/{already}/{refused})");
            }
            // A fetched copy, verified against the catalogue, replaces a torn file and a wrong one; a writer does not replace a wrong one.
            string torn = Final("torn.mps");
            File.WriteAllBytes(torn, [1, 2, 3]);
            string fetched = ClusterProtocol.HostSaveStore.Temporary(torn, "node-1");
            File.WriteAllBytes(fetched, Bytes(5));
            Check(ClusterProtocol.HostSaveStore.Install(fetched, torn, path => Judge(path, Bytes(5)), replaceOther: false) && File.ReadAllBytes(torn).AsSpan().SequenceEqual(Bytes(5)),
                "a damaged file is replaced, by a writer too");
            fetched = ClusterProtocol.HostSaveStore.Partial(torn, "node-2");
            File.WriteAllBytes(fetched, Bytes(6));
            Check(ClusterProtocol.HostSaveStore.Install(fetched, torn, path => Judge(path, Bytes(6)), replaceOther: true) && File.ReadAllBytes(torn).AsSpan().SequenceEqual(Bytes(6)),
                "a fetched copy replaces a readable file with other content");
            Check(ClusterProtocol.HostSaveStore.Partial(torn, "node-1") != ClusterProtocol.HostSaveStore.Partial(torn, "node-2")
                && Path.GetFileName(ClusterProtocol.HostSaveStore.Temporary(torn, "node-9")).Contains(".node-9."), "partial and temporary names carry the node id");
            string[] left = [.. Directory.EnumerateFiles(Path.Combine(top, "P-1")).Where(path => !path.EndsWith(".mps", StringComparison.Ordinal))];
            Check(left.Length == 0, "no temporary file is left: " + string.Join(",", left.Select(Path.GetFileName)));
        }
        finally { Directory.Delete(top, true); }
        Console.WriteLine("host-store-install: node and registry compute one store path; 25 rounds of 8 concurrent installs - one installs, the rest find it (same bytes) or are refused (other bytes); damaged files replaced; no temporaries left");
    }

    // T-0340 step 4. A host-held save's delete goes to the HOST: one live node of it at a time is handed the host's whole
    // batch (any node can unlink a file in the store; two doing it at once is only wasted work), the next one when that
    // node is gone or its turn has lapsed. The acknowledgement is the old one and idempotent.
    private static void TestHostStoreDeletes()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-deletes: " + what); }
        NodeControlFixture f = HostStoreFixture("host-deletes", out string top, ("polaris", 2, 0), ("ai", 1, 0));
        try
        {
            string[] polaris = PlannedOn(f, "polaris");
            string ai = PlannedOn(f, "ai").Single();
            ulong partition = OwnNewPartition(f, polaris[0]);
            for (int sequence = 1; sequence <= 5; sequence++)
                CopyIntoStore(f, SaveInStore(f, polaris[0], "polaris", partition, sequence), ai, "ai");
            DeleteDirective[] planned = f.Registry.PlanRetention();
            Check(planned.Length == 4 && planned.All(x => GatewayRegistry.HostOfHolder(x.Node) != null), $"retention plans the two oldest saves' copies on both hosts ({planned.Length})");
            DeleteDirective[] Pending(string node) => f.Registry.PendingDeletes(node, f.Row(node).Epoch);
            string Store(string host, DeleteDirective x) => GatewayRegistry.HostStorePath(f.Registry.RunRoot, host, x.SaveId);

            DeleteDirective[] first = Pending(polaris[0]);
            Check(first.Length == 2 && first.All(x => x.Location == Store("polaris", x) && x.Node == polaris[0]), $"a polaris node is handed polaris' batch at the store paths ({first.Length})");
            Check(Pending(polaris[1]).Length == 0, "the other polaris node is not handed it meanwhile");
            Check(Pending(polaris[0]).Length == 2, "the node whose turn it is gets it again (a retry)");
            DeleteDirective[] remote = Pending(ai);
            Check(remote.Length == 2 && remote.All(x => x.Location == Store("ai", x)), "the ai node is handed ai's batch, not polaris'");
            Check(f.Registry.AcknowledgeDelete(first[0].SaveId, first[0].Location) && !f.Registry.AcknowledgeDelete(first[0].SaveId, first[0].Location),
                "the acknowledgement removes the location, once");
            // The node dies with one delete to go: the other node of the host takes over at once.
            Kill(f, polaris[0]);
            DeleteDirective[] second = Pending(polaris[1]);
            Check(second.Length == 1 && second[0].SaveId == first[1].SaveId, "a dead node's turn passes to another node of the host");
            f.Registry.AcknowledgeDelete(second[0].SaveId, second[0].Location);
            foreach (DeleteDirective directive in remote) f.Registry.AcknowledgeDelete(directive.SaveId, directive.Location);
            Check(f.Registry.Saves().Count(x => x.SaveId.Partition == partition) == 3 && f.Registry.Saves().All(x => !x.PendingDelete)
                && Pending(polaris[1]).Length == 0 && Pending(ai).Length == 0, "the deleted saves leave the catalogue, the newest three stay");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("host-store-deletes: a host's delete batch goes to one live node of that host at a time, at the store paths; acks remove the locations; a dead node's turn passes on");
    }

    // T-0340 step 4. Global saves in the host store: the World Authority (it runs on the gateway host, outside node
    // control) registers its host as the holder when it wrote the store path; a node of a host without a copy is handed
    // the save with a live server of a holding host as its source; the recovery answer names such a server too.
    private static void TestHostStoreGlobals()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-globals: " + what); }
        NodeControlFixture f = HostStoreFixture("host-globals", out string top, ("polaris", 2, 0), ("ai", 1, 0));
        try
        {
            string[] polaris = PlannedOn(f, "polaris");
            string ai = PlannedOn(f, "ai").Single();
            NodeRegistration wa = f.Registry.RegisterNode(Join("wa-1", "127.0.0.1:28500", Guid.NewGuid())
                with { Role = NodeRole.WA, Host = "polaris", Pid = 77, ControlEndpoint = "http://127.0.0.1:29419" });
            string Store(string host, GlobalSaveId id) => GatewayRegistry.HostStorePath(f.Registry.RunRoot, host, id);
            var id = new GlobalSaveId(wa.AuthorityGeneration, 1);
            f.Registry.RegisterGlobalSave(id, wa.Node, wa.Epoch, SaveKind.Periodic, 42, "global", 123, Store("polaris", id), wa.ControlEndpoint!);
            GlobalSaveRegistration Global(GlobalSaveId which) => f.Registry.GlobalSaves().Single(x => x.SaveId == which);
            Check(Global(id).LocationHolders.Single() is { Value: "host:polaris" } held && held.Key == Store("polaris", id) && Global(id).HolderEndpoints.Count == 0,
                "the World Authority's global save in the store is held by the gateway host");
            Check(f.Registry.ReplicaWork(polaris[0], f.Row(polaris[0]).Epoch).All(x => !x.Global), "a node of the holding host is not handed it");
            ReplicaDirective work = f.Registry.ReplicaWork(ai, f.Row(ai).Epoch).Single();
            Check(work.Global && work.SaveId.Generation == id.Generation && work.SaveId.Sequence == id.Sequence
                && work.Sources.Single() is { Holder: "host:polaris" } source && polaris.Any(node => f.Row(node).ControlEndpoint == source.Endpoint),
                "the ai node is handed it, from a live polaris node");
            GlobalSaveRegistration offered = f.Registry.NewestGlobalSaveForRecovery()!;
            Check(offered.SaveId == id && offered.HolderEndpoints.Single() is { Key: "host:polaris" } server && polaris.Any(node => f.Row(node).ControlEndpoint == server.Value),
                "the recovery answer names a live server of the holding host");
            NodeRegistration remote = f.Row(ai);
            f.Registry.RegisterGlobalHolder(id, ai, remote.Epoch, 42, "global", Store("ai", id), remote.ControlEndpoint!);
            Check(Global(id).LocationHolders.Values.Order().SequenceEqual(["host:ai", "host:polaris"]) && Global(id).HolderEndpoints.Count == 0
                && f.Live().All(node => f.Registry.ReplicaWork(node, f.Row(node).Epoch).Length == 0), "with a copy on both planned hosts nobody is handed it");
            // T-0399: the ai store's copy tore and the ai node's scrub withdraws it (node + its store path, as it registered).
            // A polaris node naming the ai store path is not that holder: no-op. The ai host is then handed a fresh copy.
            Check(!f.Registry.WithdrawGlobalHolder(id, polaris[0], f.Row(polaris[0]).Epoch, Store("ai", id))
                && Global(id).LocationHolders.Count == 2, "another host's node cannot withdraw a host's copy");
            Check(f.Registry.WithdrawGlobalHolder(id, ai, remote.Epoch, Store("ai", id))
                && Global(id).LocationHolders.Values.SequenceEqual(["host:polaris"]), "the host's own node withdraws the host's copy");
            Check(f.Registry.ReplicaWork(ai, f.Row(ai).Epoch).Single() is { Global: true } rework && rework.Sources.Single().Holder == "host:polaris",
                "a withdrawn host copy is replicated again, from the other host");
            f.Registry.RegisterGlobalHolder(id, ai, remote.Epoch, 42, "global", Store("ai", id), remote.ControlEndpoint!);
            // A location that is not the store path stays node-held, as before.
            var next = new GlobalSaveId(wa.AuthorityGeneration, 2);
            f.Registry.RegisterGlobalSave(next, wa.Node, wa.Epoch, SaveKind.Periodic, 43, "global2", 124, "/run/wa-1-data/cluster/saves/GLOBAL/other.mps", wa.ControlEndpoint!);
            Check(Global(next).LocationHolders.Single().Value == "wa-1" && Global(next).HolderEndpoints.ContainsKey("wa-1"), "a global save outside the store is held by its node");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("host-store-globals: a global save in the store is host-held (the World Authority's host is the gateway's), replicated to the host without a copy from a live server, offered to recovery with one");
    }

    // T-0340: the registry is the single authority for store mode. A node offers (it could use the store); the
    // registration answer says whether it does. Both directions: a registry not in store mode answers no to a node
    // that offers; a registry in store mode answers no - and logs a WARNING - to a node that cannot (it does not
    // offer, or it does not report the host the plan runs it on). The status field shows the mode, each store and the
    // live nodes left legacy, with the save counts walked at most once a minute.
    private static void TestHostStoreMode()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-mode: " + what); }
        static JsonElement Status(NodeControlFixture f) => JsonSerializer.SerializeToElement(f.Registry.Status()).GetProperty("hostStore");
        string top = Directory.CreateTempSubdirectory("host-mode-").FullName;
        var f = new NodeControlFixture("host-mode", Path.Combine(top, "run", "gateway-registry")) { GatewayHost = "polaris" };
        TextWriter console = Console.Out;
        try
        {
            f.Apply([("polaris", 3, 0), ("ai", 2, 0)]);
            string[] polaris = PlannedOn(f, "polaris"), ai = PlannedOn(f, "ai");
            // The registry is not in store mode: a node that offers is told no, and nothing is logged about it.
            var log = new StringWriter();
            Console.SetOut(log);
            Check(!BootServing(f, polaris[0]).HostStore, "a registry that is not in store mode tells an offering node no");
            Console.SetOut(console);
            Check(!log.ToString().Contains("host store"), "and logs nothing about the host store");
            JsonElement status = Status(f);
            Check(!status.GetProperty("enabled").GetBoolean() && status.GetProperty("hosts").EnumerateObject().Count() == 0
                && status.GetProperty("legacyNodes").GetArrayLength() == 0, "status: not in store mode");

            Directory.CreateDirectory(Path.Combine(f.Registry.RunRoot, "saves", "polaris"));
            GatewayRegistry.HostStoreEnabled = true;
            f.Reopen();
            f.Heartbeat(polaris[0]);
            log = new StringWriter();
            Console.SetOut(log);
            NodeRegistration told = BootServing(f, polaris[1]);
            Check(told.HostStore && JsonSerializer.Serialize(told).Contains("\"HostStore\":true"),
                "store mode: a node that offers and reports its plan host is told yes, in the registration answer");
            Check(BootServingRemote(f, ai[0], "ai").HostStore, "on every host of the plan");
            Check(!log.ToString().Contains("WARNING"), "a node that is told yes is not warned about");
            // A node that cannot: the plugin before this ticket never offers.
            NodeRegistration old = f.Registry.RegisterNode(Join(polaris[2], $"127.0.0.1:{27100 + NodeControlFixture.Number(polaris[2])}", Guid.NewGuid())
                with { Host = "polaris", Pid = 41000, ControlEndpoint = "http://127.0.0.1:29990" });
            f.Registry.Heartbeat(old.Node, old.Epoch, [], serving: true);
            Check(!old.HostStore && log.ToString().Contains($"WARNING: host store: {polaris[2]} keeps its saves in its own data dir"),
                "store mode: a node that does not offer is told no and the registry logs a WARNING naming it");
            // A node that does not know its host: no host name, so the store directory cannot be agreed on.
            log.GetStringBuilder().Clear();
            NodeRegistration hostless = f.Registry.RegisterNode(Join(ai[1], $"10.0.0.2:{27100 + NodeControlFixture.Number(ai[1])}", Guid.NewGuid())
                with { Pid = 41001, ControlEndpoint = "http://10.0.0.2:29991", HostStore = true });
            f.Registry.Heartbeat(hostless.Node, hostless.Epoch, [], serving: true);
            Check(!hostless.HostStore && log.ToString().Contains($"WARNING: host store: {ai[1]} keeps its saves in its own data dir"),
                "store mode: a node that reports no host is told no, with a WARNING");
            Console.SetOut(console);

            status = Status(f);
            string[] legacy = [.. status.GetProperty("legacyNodes").EnumerateArray().Select(x => x.GetString()!)];
            Check(status.GetProperty("enabled").GetBoolean() && legacy.Order().SequenceEqual(new[] { polaris[0], polaris[2], ai[1] }.Order()),
                "status: store mode, and the live nodes that keep node-held saves (one registered before the mode, two refused): " + string.Join(",", legacy));
            JsonElement Host(string name) => Status(f).GetProperty("hosts").GetProperty(name);
            Check(Host("polaris").GetProperty("state").GetString() == "Serving" && Host("ai").GetProperty("state").GetString() == "Serving"
                && Host("polaris").GetProperty("saves").GetInt32() == 0, "status: each planned host's store state and save count");
            SaveId save = SaveInStore(f, polaris[1], "polaris", OwnNewPartition(f, polaris[1]));
            CopyIntoStore(f, save, ai[0], "ai");
            Check(Host("polaris").GetProperty("saves").GetInt32() == 0, "the save counts are not walked on every poll");
            f.Advance(61);
            Check(Host("polaris").GetProperty("saves").GetInt32() == 1 && Host("ai").GetProperty("saves").GetInt32() == 1,
                "they are a minute old at most");
        }
        finally
        {
            Console.SetOut(console);
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("host-store-mode: the registration answer carries store mode (yes only from a store-mode registry to a node that offers and "
            + "reports its plan host; a refusal is a WARNING); status hostStore = enabled, per-host state and saves, legacy nodes");
    }

    // T-0340: a node that finds its own host's copy damaged reports the HOST holder corrupt (the same
    // /registry/saves/corruption route). The copy then stops counting and stops being served, the host is offered the
    // save again, and the mark clears when a node of the host registers the good copy at the store path.
    private static void TestHostStoreCorruption()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-corruption: " + what); }
        static bool Offered(NodeControlFixture f, string node, SaveId id) => f.Registry.ReplicaWork(node, f.Row(node).Epoch).Any(x => !x.Global && x.SaveId == id);
        NodeControlFixture f = HostStoreFixture("host-corrupt", out string top, ("polaris", 2, 0), ("ai", 1, 0));
        try
        {
            string[] polaris = PlannedOn(f, "polaris");
            string ai = PlannedOn(f, "ai")[0];
            SaveId id = SaveInStore(f, polaris[0], "polaris", OwnNewPartition(f, polaris[0]));
            CopyIntoStore(f, id, ai, "ai");
            SaveRegistration Save() => f.Registry.Saves().Single(x => x.SaveId == id);
            Check(f.Registry.ReplicaQueueDepth() == 0 && !Offered(f, polaris[1], id), "two hosts hold the save: nothing to copy");

            f.Registry.ReportCorruption(id, GatewayRegistry.HostHolder("polaris"), polaris[1], f.Row(polaris[1]).Epoch);
            Check(Save().CorruptHolders.SetEquals([GatewayRegistry.HostHolder("polaris")]), "the host holder is marked corrupt");
            Check(f.Registry.ReplicaQueueDepth() == 1 && Offered(f, polaris[1], id) && !Offered(f, ai, id),
                "the corrupt copy stops counting: a node of that host is offered the save again, the host with the good copy is not");
            AssertConflict(() => f.Registry.IssueGrant(new("save", "", id.Partition, id.Generation, id.Sequence, ai, f.Row(ai).Epoch, GatewayRegistry.HostHolder("polaris"))),
                "the corrupt copy is not served");

            CopyIntoStore(f, id, polaris[1], "polaris");
            Check(Save().CorruptHolders.Count == 0 && HeldByHosts(f, Save(), "polaris", "ai") && f.Registry.ReplicaQueueDepth() == 0,
                "the good copy registered at the store path clears the host holder's corrupt mark");
            f.Registry.IssueGrant(new("save", "", id.Partition, id.Generation, id.Sequence, ai, f.Row(ai).Epoch, GatewayRegistry.HostHolder("polaris")));
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("host-store-corruption: a corrupt host holder stops counting and serving, its host is offered the save, the mark clears when the good copy registers");
    }

    // T-0340, bench defect A: at the FIRST start of a run root the registry is constructed before the start applies the
    // hosts file, so node control is not active at load. Store mode is evaluated again when the hosts file is applied -
    // before any node registers - and an empty catalogue is in store mode from the first save.
    private static void TestHostStoreFreshStart()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-fresh-start: " + what); }
        string top = Directory.CreateTempSubdirectory("host-fresh-").FullName;
        Directory.CreateDirectory(Path.Combine(top, "run", "saves", "polaris"));
        GatewayRegistry.HostStoreEnabled = true;
        var f = new NodeControlFixture("host-fresh", Path.Combine(top, "run", "gateway-registry")) { GatewayHost = "polaris" };
        try
        {
            f.Reopen();   // the gateway host is known, as in the gateway process; still no hosts file
            bool Enabled() => JsonSerializer.SerializeToElement(f.Registry.Status()).GetProperty("hostStore").GetProperty("enabled").GetBoolean();
            Check(!Enabled(), "before the hosts file is applied there is no node control and no store mode");
            f.Apply(("polaris", 2, 0));
            Check(Enabled(), "applying the hosts file puts an empty catalogue into store mode");
            string[] polaris = PlannedOn(f, "polaris");
            Check(BootServing(f, polaris[0]).HostStore, "the first node to register is told to use the store");
            SaveId first = SaveInStore(f, polaris[0], "polaris", OwnNewPartition(f, polaris[0]));
            Check(HeldByHosts(f, f.Registry.Saves().Single(x => x.SaveId == first), "polaris"), "the first save is host-held");
            f.Reopen();
            Check(Enabled(), "and the next load is in store mode from the constructor");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("host-store-fresh-start: store mode is entered when the hosts file is applied, before any node registers; the first save is host-held");
    }

    // T-0340, bench defect B: the re-key must never turn a readable catalogue into an unreadable one. A location is
    // re-keyed only when the registry SEES the store file with the catalogued size; one whose store file is missing (or
    // has another size) stays node-held at its old path, is reported (one WARNING, status hostStore.unmoved) and is
    // re-keyed by the load after the file is there. A host whose store the registry cannot see stays legacy as a whole.
    private static void TestHostStoreUnmoved()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-unmoved: " + what); }
        string top = Directory.CreateTempSubdirectory("host-unmoved-").FullName;
        var f = new NodeControlFixture("host-unmoved", Path.Combine(top, "run", "gateway-registry")) { GatewayHost = "polaris" };
        TextWriter console = Console.Out;
        try
        {
            f.Apply(("polaris", 2, 0), ("ai", 1, 0));
            string[] polaris = PlannedOn(f, "polaris");
            string ai = PlannedOn(f, "ai")[0];
            foreach (string node in polaris) BootServing(f, node);
            BootServingOnAi(f, ai);
            // Three saves written into node dirs, each with a copy on ai, and two global saves of the World Authority.
            SaveId moved = SaveOn(f, polaris[0], OwnNewPartition(f, polaris[0]), 1), unmoved = SaveOn(f, polaris[0], OwnNewPartition(f, polaris[0]), 1),
                wrongSize = SaveOn(f, polaris[1], OwnNewPartition(f, polaris[1]), 1);
            foreach (SaveId id in new[] { moved, unmoved, wrongSize }) CopyTo(f, id, ai);
            NodeRegistration wa = f.Registry.RegisterNode(Join("wa-1", "127.0.0.1:28500", Guid.NewGuid())
                with { Role = NodeRole.WA, Host = "polaris", Pid = 77, ControlEndpoint = "http://127.0.0.1:29419" });
            string GlobalPath(int sequence) => $"/run/wa-1-data/cluster/saves/GLOBAL/g{wa.AuthorityGeneration:X8}-s{sequence:X8}.mps";
            GlobalSaveId globalMoved = f.Registry.RegisterGlobalSave(new(wa.AuthorityGeneration, 1), wa.Node, wa.Epoch, SaveKind.Periodic, 42, "g1", 1, GlobalPath(1), wa.ControlEndpoint!).SaveId;
            GlobalSaveId globalUnmoved = f.Registry.RegisterGlobalSave(new(wa.AuthorityGeneration, 2), wa.Node, wa.Epoch, SaveKind.Periodic, 43, "g2", 2, GlobalPath(2), wa.ControlEndpoint!).SaveId;
            SaveRegistration Save(SaveId id) => f.Registry.Saves().Single(x => x.SaveId == id);
            GlobalSaveRegistration Global(GlobalSaveId id) => f.Registry.GlobalSaves().Single(x => x.SaveId == id);
            string Shape(SaveId id) => JsonSerializer.Serialize(new { Save(id).Locations, Save(id).LocationHolders, Save(id).HolderEndpoints });
            string unmovedBefore = Shape(unmoved), wrongSizeBefore = Shape(wrongSize);
            string globalBefore = JsonSerializer.Serialize(Global(globalUnmoved));

            // The store exists, but only one save and one global save were moved into it; a third file has another size.
            string root = f.Registry.RunRoot;
            StoreFile(GatewayRegistry.HostStorePath(root, "polaris", moved), Save(moved).SizeBytes);
            StoreFile(GatewayRegistry.HostStorePath(root, "polaris", wrongSize), Save(wrongSize).SizeBytes - 1);
            StoreFile(GatewayRegistry.HostStorePath(root, "polaris", globalMoved), 42);
            GatewayRegistry.HostStoreEnabled = true;
            var log = new StringWriter();
            Console.SetOut(log);
            f.Reopen();
            Console.SetOut(console);
            foreach (string node in polaris.Append(ai)) f.Heartbeat(node);

            Check(Save(moved).LocationHolders[GatewayRegistry.HostStorePath(root, "polaris", moved)] == "host:polaris"
                && !Save(moved).Locations.Contains(SavePathOf(polaris[0], moved)), "a location whose store file is there with the catalogued size is re-keyed");
            Check(Save(moved).LocationHolders.GetValueOrDefault(SavePathOf(ai, moved)) == ai && Save(moved).HolderEndpoints.ContainsKey(ai)
                && !Save(moved).HolderEndpoints.ContainsKey(polaris[0]),
                "the copy on a host whose store the registry does not see stays node-held at its old path, with its endpoint");
            Check(Shape(unmoved) == unmovedBefore, "a save whose store file is missing is left exactly as it was: " + Shape(unmoved));
            Check(Shape(wrongSize) == wrongSizeBefore, "a store file of another size is no evidence");
            Check(Global(globalMoved).LocationHolders.Single() is { Value: "host:polaris" } held && held.Key == GatewayRegistry.HostStorePath(root, "polaris", globalMoved),
                "a moved global save is re-keyed");
            Check(JsonSerializer.Serialize(Global(globalUnmoved)) == globalBefore, "an unmoved global save keeps its legacy location");
            Check(f.Registry.Saves().All(x => f.Registry.SaveReachable(x.SaveId) && Fetchable(f, f.Registry.ServedForTest(x.SaveId))),
                "every save stays reachable and fetchable through a location that exists");
            Check(f.Registry.NewestGlobalSaveForRecovery()!.Locations.Contains(GlobalPath(2)), "the World Authority is told the newest global save at the path its file is at");
            // 2 partition saves on polaris, 3 copies on ai, 1 global save: six legacy locations are left.
            string warning = log.ToString().Split('\n').SingleOrDefault(x => x.Contains("WARNING: host store:") && x.Contains("not in the store")) ?? "";
            Check(warning.Contains("6 location(s)") && warning.Contains(SavePathOf(polaris[0], unmoved)), "one WARNING counts the unmoved locations and names the first few: " + log);
            JsonElement status = JsonSerializer.SerializeToElement(f.Registry.Status()).GetProperty("hostStore");
            Check(status.GetProperty("enabled").GetBoolean() && status.GetProperty("unmoved").GetInt32() == 6, "status hostStore.unmoved: " + status);

            // The files arrive (cluster stopped): the next load re-keys them and says nothing about polaris any more.
            StoreFile(GatewayRegistry.HostStorePath(root, "polaris", unmoved), Save(unmoved).SizeBytes);
            StoreFile(GatewayRegistry.HostStorePath(root, "polaris", wrongSize), Save(wrongSize).SizeBytes);
            StoreFile(GatewayRegistry.HostStorePath(root, "polaris", globalUnmoved), 43);
            f.Reopen();
            Check(new[] { moved, unmoved, wrongSize }.All(id => Save(id).LocationHolders.GetValueOrDefault(GatewayRegistry.HostStorePath(root, "polaris", id)) == "host:polaris"
                    && Save(id).LocationHolders.Count == 2)
                && Global(globalUnmoved).LocationHolders.Values.Single() == "host:polaris", "with the files present the next load re-keys them");
            // ai's files become visible too (a shared disk, or the evidence the host agent will bring): then they re-key.
            MoveIntoStores(f, "ai");
            f.Reopen();
            Check(new[] { moved, unmoved, wrongSize }.All(id => HeldByHosts(f, Save(id), "polaris", "ai")), "a store the registry sees is re-keyed for any host");
            f.Advance(61);
            Check(JsonSerializer.SerializeToElement(f.Registry.Status()).GetProperty("hostStore").GetProperty("unmoved").GetInt32() == 0, "nothing is unmoved then");
        }
        finally
        {
            Console.SetOut(console);
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("host-store-unmoved: a location is re-keyed only when its store file is there with the catalogued size; the rest stays legacy, "
            + "reachable, reported in one WARNING and in status; the load after the move re-keys it");
    }

    // T-0340 step 6: in store mode a stopped node's row is released without asking the archive. On r113 every stopped row
    // was kept for an archived save and never went. Here: a legacy node whose archived saves name it (one file moved into
    // the store, two not), a save it wrote that is now host-held (the writer field) and a finished handover out of it.
    // Earned red: with the archive still asked (the pass before this step) the row stays. Released, nothing is lost: the
    // moved save comes back host-held, the unmoved ones are served by a live node of the released holder's host under
    // a grant bound to the catalogued location (refused for a node of another host), copied into a store like any
    // other save, and not offered while no node of that host lives. Falsifier: without the released-holder rule the
    // same save is unreachable. Then eight recycles of store-mode nodes leave no stopped row.
    private static void TestHostStoreRelease()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-release: " + what); }
        string top = Directory.CreateTempSubdirectory("host-release-").FullName;
        var f = new NodeControlFixture("host-release", Path.Combine(top, "run", "gateway-registry")) { GatewayHost = "polaris" };
        try
        {
            f.Apply(("polaris", 2, 0), ("ai", 1, 0));
            string[] polaris = PlannedOn(f, "polaris");
            string old = polaris[0], keeper = polaris[1], ai = PlannedOn(f, "ai")[0];
            foreach (string node in polaris) BootServing(f, node);
            BootServingOnAi(f, ai);
            // A retired parent whose only save is old's, at old's node path; its outputs live on the keeper.
            (SaveId Parent, ulong Output) Retire()
            {
                NodeRegistration row = f.Row(old);
                ulong parent = OwnNewPartition(f, old);
                SaveId save = SaveOn(f, old, parent, 1);
                ulong[] outputs = f.Registry.AllocatePartitionIds(row.Node, row.Epoch, 2);
                f.Registry.ApplyLineage("split-" + parent, LineageKind.Split, row.Node, row.Epoch, [new(parent, save.Generation)], outputs,
                    new() { [outputs[0]] = [1], [outputs[1]] = [2] }, 5);
                foreach (ulong output in outputs)
                {
                    f.Registry.ReleasePartition(output, row.Node, row.Epoch, f.Registry.Partitions().Single(x => x.Partition == output).Generation);
                    f.Registry.ClaimPartition(output, keeper, f.Row(keeper).Epoch, null);
                    SaveOn(f, keeper, output, 1);
                }
                return (save, outputs[0]);
            }
            (SaveId Parent, ulong Output) moved = Retire(), unmoved = Retire(), cut = Retire();
            // A live partition old wrote a save for, with a copy on ai, handed to the keeper by a finished handover.
            ulong live = OwnNewPartition(f, old);
            SaveId liveSave = SaveOn(f, old, live, 1);
            CopyTo(f, liveSave, ai);
            string handover = Guid.NewGuid().ToString("D");
            f.Registry.Begin(handover, live, old, f.Row(old).Epoch, liveSave.Generation, keeper, f.Row(keeper).Epoch);
            f.Registry.Activate(handover);
            f.Registry.Commit(handover);
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            Check(f.Registry.ArchivedPartitionCount == 3 && f.Registry.Saves().All(x => x.SaveId.Partition != moved.Parent.Partition), "the three parents' saves are archived");

            // The file move: every save in memory, and one of the three archived saves. Then store mode.
            string root = f.Registry.RunRoot;
            Directory.CreateDirectory(Path.Combine(root, "saves", "polaris"));
            MoveIntoStores(f, "polaris");
            StoreFile(GatewayRegistry.HostStorePath(root, "polaris", moved.Parent), 20_000);
            GatewayRegistry.HostStoreEnabled = true;
            f.Reopen();
            foreach (string node in polaris.Append(ai)) f.Heartbeat(node);
            SaveRegistration Save(SaveId id) => f.Registry.Saves().Single(x => x.SaveId == id);
            Check(Save(liveSave).Node == old && Save(liveSave).LocationHolders.Values.Order().SequenceEqual(new[] { "host:polaris", ai }.Order()),
                "old's live save is host-held on polaris now; old is only its writer");

            // old stops for good.
            string replacement = RecycleAndReplace(f, old, "polaris");
            f.Registry.PlanRetention();
            Check(f.Registry.GoneRowIds().SequenceEqual([old]), "the stopped node's row is a gone row: " + string.Join(",", f.Registry.GoneRowIds()));
            GatewayRegistry.HostStoreReleaseChecksArchive = true;
            Check(f.Registry.ReleaseGoneRowsNow().Length == 0 && f.Registry.GoneRowIds().Contains(old),
                "RED expected: while the release pass asks the archive, the archived saves keep the stopped row");
            GatewayRegistry.HostStoreReleaseChecksArchive = false;
            f.Reopen();   // the red arm's archive cache is memory only
            foreach (string node in f.Live()) f.Heartbeat(node);
            string[] released = f.Registry.ReleaseGoneRowsNow();
            Check(released.SequenceEqual([old]) && f.Registry.Nodes().All(x => x.Node != old) && f.Registry.LastReleaseScannedIds == 0,
                "in store mode the row is released without an archive scan: " + string.Join(",", released));
            Check(f.Registry.HostStoreAliases().GetValueOrDefault(old) == "polaris", "the released row leaves its host in the alias map");
            Check(JsonSerializer.SerializeToElement(f.Registry.Status()).GetProperty("hostStore").GetProperty("aliases").GetInt32() >= 1,
                "status hostStore.aliases tells the prune that rows may have been released without the archive");
            Check(f.History(old) != null && f.Registry.Get(handover) is { SourceNode: var source } && source == old && Save(liveSave).Node == old
                && f.Registry.SaveReachable(liveSave), "the node history, the finished handover and the writer field still name it; none needs the row");
            Check(f.Registry.ArchivedPartitionCount == 3, "nothing was read back from the archive for the release");
            f.Reopen();
            foreach (string node in f.Live()) f.Heartbeat(node);
            Check(f.Registry.Nodes().All(x => x.Node != old) && f.Registry.HostStoreAliases().GetValueOrDefault(old) == "polaris", "the release and the alias survive a restart");

            // The keeper's copy of an output turns out corrupt: its cover falls back to the archived parent save.
            bool CoverFallsBackTo((SaveId Parent, ulong Output) retired)
            {
                SaveId lost = f.Registry.Saves().Single(x => x.SaveId.Partition == retired.Output).SaveId;
                if (!Save(lost).CorruptHolders.Contains("host:polaris")) f.Registry.ReportCorruption(lost, "host:polaris", ai, f.Row(ai).Epoch);
                return f.Registry.ResolveCover(retired.Output).Any(x => x.Save.SaveId == retired.Parent) && f.Registry.SaveReachable(retired.Parent);
            }
            Check(CoverFallsBackTo(moved) && HeldByHosts(f, Save(moved.Parent), "polaris"),
                "an archived save whose file is in the store comes back host-held and reachable");
            GatewayRegistry.ServeReleasedHolders = false;
            Check(!CoverFallsBackTo(unmoved), "RED expected: released without the released-holder rule, the unmoved archived save is unreachable");
            GatewayRegistry.ServeReleasedHolders = true;
            Check(CoverFallsBackTo(unmoved), "an archived save whose file was NOT moved stays reachable after its holder's row is gone");
            string legacy = SavePathOf(old, unmoved.Parent);
            Check(Save(unmoved.Parent).LocationHolders.Single() is { Value: var holder } at && holder == old && at.Key == legacy,
                "it keeps its catalogued location and holder: " + JsonSerializer.Serialize(Save(unmoved.Parent).LocationHolders));
            SaveRegistration served = f.Registry.ServedForTest(unmoved.Parent);
            string[] onPolaris = [.. PlannedOn(f, "polaris")];
            Check(served.HolderProxies?.GetValueOrDefault(old) is { } proxy && onPolaris.Contains(proxy.Node) && Fetchable(f, served),
                "a recovery claim names a live node of the released holder's host as its proxy");
            string server = served.HolderProxies![old].Node;
            GrantRequest Request(string receiver, string? location) =>
                new("save", "", unmoved.Parent.Partition, unmoved.Parent.Generation, unmoved.Parent.Sequence, ai, f.Row(ai).Epoch, receiver, location);
            TransferGrant grant = f.Registry.IssueGrant(Request(server, legacy));
            Check(grant.Host == null && grant.Location == legacy && grant.Receiver == server, "the grant binds the catalogued location to that proxy");
            AssertConflict(() => f.Registry.IssueGrant(Request(ai, legacy)), "a node of another host is no proxy for it");
            AssertConflict(() => f.Registry.IssueGrant(Request(server, SavePathOf(old, cut.Parent))), "another save's location");
            AssertConflict(() => f.Registry.IssueGrant(Request(old, null)), "the released node itself");
            f.Registry.ValidateGrant(new(grant.Token, "save", "", unmoved.Parent.Partition, unmoved.Parent.Generation, unmoved.Parent.Sequence, server, f.Row(server).Epoch, legacy));

            // It is copied off like any other save that lacks its copies: the source is the proxy at the catalogued location.
            ReplicaDirective? work = null;
            for (int round = 0; round < 20 && work == null; round++)
                foreach (ReplicaDirective directive in f.Registry.ReplicaWork(ai, f.Row(ai).Epoch).Where(x => !x.Global))
                {
                    if (directive.SaveId == unmoved.Parent) { work = directive; break; }
                    CopyIntoStore(f, directive.SaveId, ai, "ai");
                }
            Check(work?.Sources.Single() is { } from && from.Holder == old && from.Proxy != null && onPolaris.Contains(from.Proxy) && from.Location == legacy,
                "replica work for it names the released holder, a live proxy on its host and the catalogued location");
            CopyIntoStore(f, unmoved.Parent, ai, "ai");
            Check(Save(unmoved.Parent).LocationHolders.Values.Order().SequenceEqual(new[] { "host:ai", old }.Order()), "the copy is host-held on ai");

            // Eight recycles of store-mode nodes, each with a save of its own: no stopped row is left.
            for (int i = 0; i < 8; i++)
            {
                string node = replacement;
                ulong partition = OwnNewPartition(f, node);
                SaveId id = SaveInStore(f, node, "polaris", partition);
                CopyIntoStore(f, id, ai, "ai");
                f.Registry.ReleasePartition(partition, node, f.Row(node).Epoch, id.Generation);
                f.Registry.ClaimPartition(partition, keeper, f.Row(keeper).Epoch, null);
                replacement = RecycleAndReplace(f, node, "polaris");
                f.Registry.PlanRetention();
                f.Registry.ReleaseGoneRowsNow();
                Check(f.Registry.SaveReachable(id), $"recycle {i + 1}: the stopped node's save stays reachable");
            }
            string[] rows = [.. f.Registry.Nodes().Select(x => x.Node).Where(x => GatewayRegistry.HostOfHolder(x) == null).Order()];
            Check(f.Registry.GoneRowIds().Length == 0 && rows.SequenceEqual(f.Live().Order()),
                "eight recycles leave no stopped row, with archived saves present: " + string.Join(",", rows));
            Check(f.Registry.ArchivedPartitionCount >= 1 && f.Registry.Saves().All(x => f.Registry.SaveReachable(x.SaveId)
                    || x.LocationHolders.Values.All(x.CorruptHolders.Contains)), "and every save in memory with an intact copy is reachable");

            // No live node on polaris (a host cut): the released holder's file cannot be read, so it is not offered.
            Check(f.Registry.SaveReachable(cut.Parent), "while a polaris node lives, the third parent's unmoved save is reachable");
            foreach (string node in PlannedOn(f, "polaris")) Kill(f, node);
            Check(f.Registry.HostStoreStateOf("polaris") != HostStoreState.Serving && !CoverFallsBackTo(cut) && !f.Registry.SaveReachable(cut.Parent)
                && f.Registry.ServedForTest(cut.Parent).HolderProxies?.ContainsKey(old) != true,
                "with no live node on its host a released holder is not offered");
            Check(f.Registry.SaveReachable(unmoved.Parent), "the save that was copied to ai meanwhile still is");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            GatewayRegistry.HostStoreReleaseChecksArchive = false;
            GatewayRegistry.ServeReleasedHolders = true;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("host-store-release: in store mode a stopped row is released without the archive (kept while the archive is asked: red); "
            + "an archived save comes back host-held when its file is in the store, else served through its released holder's host under a location-bound grant; "
            + "8 recycles leave 0 stopped rows");
    }

    // T-0340 step 7: the host agent's evidence for legacy files it moved (HostStoreMove.cs). A legacy world in store mode:
    // a stopped ai node's copy of a save (in memory), a stopped polaris node's ARCHIVED save, a live node's file, and
    // files that must not be re-keyed (another size or checksum, the wrong host, a save nobody catalogues). The query
    // changes nothing; the commit re-keys exactly the node's location - an archived row in place, without reading the
    // partition back - and is durable when it answers; a second commit is idempotent (hostHeld); the stopped rows are
    // then released and the archived save comes back host-held and reachable.
    private static void TestHostStoreAdopt()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-adopt: " + what); }
        string top = Directory.CreateTempSubdirectory("host-adopt-").FullName;
        var f = new NodeControlFixture("host-adopt", Path.Combine(top, "run", "gateway-registry")) { GatewayHost = "polaris" };
        try
        {
            f.Apply(("polaris", 2, 0), ("ai", 1, 0));
            string[] polaris = PlannedOn(f, "polaris");
            string old = polaris[0], keeper = polaris[1], aiOld = PlannedOn(f, "ai")[0];
            foreach (string node in polaris) BootServing(f, node);
            BootServingOnAi(f, aiOld);
            SaveId shared = SaveOn(f, aiOld, OwnNewPartition(f, aiOld), 1);
            SaveId archived = ArchivedSaveOn(f, old);
            Check(f.Registry.ArchivedPartitionCount == 1, "the split parent's save is archived");
            SaveId liveSave = SaveOn(f, keeper, OwnNewPartition(f, keeper), 1);
            SaveRegistration Save(SaveId id) => f.Registry.Saves().Single(x => x.SaveId == id);
            HostStoreAdoptFile File(string node, SaveId id, string? checksum = null, long size = 20_000) =>
                new(node, id.Partition, id.Generation, id.Sequence, false, size, checksum ?? $"c{id.Partition}.{id.Sequence}");
            object Ask(string host, bool commit, params HostStoreAdoptFile[] files) => f.Registry.HostStoreAdopt(new HostStoreAdoptRequest(host, commit, files));
            string[] Statuses(object answer) =>
                [.. JsonSerializer.SerializeToElement(answer).GetProperty("files").EnumerateArray().Select(x => x.GetProperty("Status").GetString()!)];

            AssertConflict(() => Ask("ai", true, File(aiOld, shared)), "no commit while the catalogue is not in store mode");
            // Store mode with nothing moved: every location stays legacy.
            Directory.CreateDirectory(Path.Combine(f.Registry.RunRoot, "saves", "polaris"));
            GatewayRegistry.HostStoreEnabled = true;
            f.Reopen();
            foreach (string node in polaris.Append(aiOld)) f.Heartbeat(node);
            // Both nodes stop for good; the rows stay (their files are catalogued).
            string aiNew = RecycleAndReplace(f, aiOld, "ai");
            string polarisNew = RecycleAndReplace(f, old, "polaris");
            f.Registry.PlanRetention();
            Check(f.Registry.GoneRowIds().Order().SequenceEqual(new[] { aiOld, old }.Order()), "the legacy locations keep both stopped rows: "
                + string.Join(",", f.Registry.GoneRowIds()));

            string shape = JsonSerializer.Serialize(f.Registry.Saves());
            Dictionary<string, byte[]> archive = f.Registry.ArchivedRowBytes();
            long rehydrations = f.Registry.ArchiveRehydrations;
            int aliases = f.Registry.HostStoreAliases().Count;
            HostStoreAdoptFile[] asked =
            [
                File(aiOld, shared), File(old, archived), File(keeper, liveSave), File(aiOld, shared, checksum: "other"),
                File(aiOld, shared, size: 1), File(aiOld, new SaveId(999, 1, 1)), File(aiOld, liveSave),
            ];
            string[] query = Statuses(Ask("ai", false, asked));
            Check(query.SequenceEqual(["adopt", "wrongHost", "live", "mismatch", "mismatch", "uncatalogued", "unnamed"]),
                "the query on ai: " + string.Join(",", query));
            string[] onPolaris = Statuses(Ask("polaris", false, File(old, archived), File(aiOld, shared)));
            Check(onPolaris.SequenceEqual(["adopt", "wrongHost"]), "the query on polaris: " + string.Join(",", onPolaris));
            Check(JsonSerializer.Serialize(f.Registry.Saves()) == shape && ArchiveSame(archive, f.Registry.ArchivedRowBytes())
                && f.Registry.ArchiveRehydrations == rehydrations && f.Registry.HostStoreAliases().Count == aliases,
                "a query changes nothing: catalogue, archive, aliases");

            string[] committed = Statuses(Ask("ai", true, File(aiOld, shared, checksum: "other"), File(aiOld, shared), File(keeper, liveSave)));
            Check(committed.SequenceEqual(["mismatch", "rekeyed", "live"]), "the commit on ai: " + string.Join(",", committed));
            Check(!f.Registry.StatePendingWrite, "the re-key is written durably before the answer");
            string aiStore = GatewayRegistry.HostStorePath(f.Registry.RunRoot, "ai", shared);
            Check(Save(shared).LocationHolders.GetValueOrDefault(aiStore) == "host:ai" && Save(shared).LocationHolders.Count == 1
                && Save(shared).HolderEndpoints.Count == 0 && Save(liveSave).LocationHolders.Values.Single() == keeper,
                "exactly the stopped node's location became ai's store location; the live node's save is untouched: "
                + JsonSerializer.Serialize(Save(shared).LocationHolders));
            Check(Statuses(Ask("ai", true, File(aiOld, shared))).Single() == "hostHeld", "a second commit is idempotent: hostHeld");

            string[] archivedCommit = Statuses(Ask("polaris", true, File(old, archived)));
            Check(archivedCommit.Single() == "rekeyed" && !f.Registry.StatePendingWrite, "the archived save is re-keyed durably: " + archivedCommit.Single());
            Check(f.Registry.ArchivedPartitionCount == 1 && f.Registry.ArchiveRehydrations == rehydrations, "without reading the partition back");
            SaveRegistration row = f.Registry.ArchivedRowBytes().Values.Select(x => JsonSerializer.Deserialize<SaveRegistration>(x)!).Single(x => x.SaveId == archived);
            string polarisStore = GatewayRegistry.HostStorePath(f.Registry.RunRoot, "polaris", archived);
            Check(row.Locations.SetEquals([polarisStore]) && row.LocationHolders.GetValueOrDefault(polarisStore) == "host:polaris"
                && row.HolderEndpoints.Count == 0, "the committed archive row names polaris' store: " + JsonSerializer.Serialize(row));
            Check(Statuses(Ask("polaris", true, File(old, archived))).Single() == "hostHeld", "the archived save: a second commit is hostHeld");
            // The rest of old's dir: the split outputs' saves it wrote (in memory).
            HostStoreAdoptFile[] outputs = [.. f.Registry.Saves().Where(x => x.LocationHolders.ContainsValue(old)).Select(x => File(old, x.SaveId))];
            Check(outputs.Length == 2 && Statuses(Ask("polaris", true, outputs)).All(x => x == "rekeyed"), "old's other files are re-keyed");

            f.Registry.PlanRetention();
            string[] released = f.Registry.ReleaseGoneRowsNow();
            Check(released.Order().SequenceEqual(new[] { aiOld, old }.Order()), "nothing pins the stopped rows any more: " + string.Join(",", released));
            Check(f.Registry.HostStoreAliases().GetValueOrDefault(aiOld) == "ai" && f.Registry.HostStoreAliases().GetValueOrDefault(old) == "polaris",
                "their hosts are frozen in the alias map");
            f.Reopen();
            foreach (string node in f.Live()) f.Heartbeat(node);
            Check(f.Registry.SaveReachable(archived) && HeldByHosts(f, Save(archived), "polaris"), "the archived save comes back host-held and reachable");
            Check(f.Registry.SaveReachable(shared) && Save(shared).LocationHolders.GetValueOrDefault(aiStore) == "host:ai", "the re-key survives a restart");
            _ = aiNew; _ = polarisNew;
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("host-store-adopt: the host agent's evidence re-keys exactly a stopped node's legacy location (in memory, and an archived row in "
            + "place without reading it back), durably before it answers, idempotently; live, mismatching, wrong-host and uncatalogued files are left; "
            + "the stopped rows then release");
    }

    // #1276 (T-0296 F33): a recovery claim hands out the newest save at a stopped node's legacy path L, served through the
    // gone-holder proxy under a grant bound to L; the host agent's sweep then commits the move of that file and unlinks L
    // before the proxy opens it. The node's fetch fails and it loads the older fallback (PartitionPersistence: "recovery
    // fell back from"). The forced interleaving: claim, grant, commit the adopt, unlink L if the answer was rekeyed,
    // validate the grant and fetch. Earned red: without the hold the commit answers rekeyed and the node loads the older
    // save. Green: the adopt answers busy while the claim or an unexpired grant names L, and moves it afterwards.
    private static void TestHostStoreAdoptDuringClaim()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-adopt-during-claim: " + what); }
        try
        {
            GatewayRegistry.AdoptHoldsClaimedSaves = false;
            Check(AdoptDuringClaim(hold: false) == "fell back", "RED expected: without the hold the sweep unlinks the claimed file and the node loads the older save");
            GatewayRegistry.AdoptHoldsClaimedSaves = true;
            string green = AdoptDuringClaim(hold: true);
            Check(green == "newest", "with the hold the node loads the newest save: " + green);
        }
        finally
        {
            GatewayRegistry.AdoptHoldsClaimedSaves = true;
        }
        Console.WriteLine("host-store-adopt-during-claim: a host-store adopt answers busy while a recovery claim or an unexpired proxied grant names "
            + "the legacy file (the node then loads the newest save, not the older fallback) and moves it once the claim settled and the grant "
            + "was used or expired; RED without the hold");
    }

    /// <summary>One run of the interleaving; returns "newest" or "fell back" (or throws on a failed green check).</summary>
    private static string AdoptDuringClaim(bool hold)
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("host-store-adopt-during-claim: " + what); }
        string top = Directory.CreateTempSubdirectory("host-adopt-claim-").FullName;
        var f = new NodeControlFixture("host-adopt-claim", Path.Combine(top, "run", "gateway-registry")) { GatewayHost = "polaris" };
        try
        {
            f.Apply(("polaris", 2, 0));
            string[] polaris = PlannedOn(f, "polaris");
            string old = polaris[0], keeper = polaris[1];
            foreach (string node in polaris) BootServing(f, node);
            ulong partition = OwnNewPartition(f, old);
            SaveId older = SaveOn(f, old, partition, 1), newest = SaveOn(f, old, partition, 2);
            Directory.CreateDirectory(Path.Combine(f.Registry.RunRoot, "saves", "polaris"));
            GatewayRegistry.HostStoreEnabled = true;
            f.Reopen();
            foreach (string node in polaris) f.Heartbeat(node);
            RecycleAndReplace(f, old, "polaris");

            HostStoreAdoptFile File(SaveId id) => new(old, id.Partition, id.Generation, id.Sequence, false, 20_000, $"c{id.Partition}.{id.Sequence}");
            string Adopt(bool commit, SaveId id) => JsonSerializer.SerializeToElement(f.Registry.HostStoreAdopt(new HostStoreAdoptRequest("polaris", commit, [File(id)])))
                .GetProperty("files").EnumerateArray().Single().GetProperty("Status").GetString()!;
            // cluster-hostd: unlink the legacy file only on rekeyed (Cli/cluster_hostd.py _move_batch).
            HashSet<string> onDisk = [SavePathOf(old, older), SavePathOf(old, newest)];
            string Sweep(SaveId id)
            {
                string status = Adopt(true, id);
                if (status == "rekeyed") onDisk.Remove(SavePathOf(old, id));
                return status;
            }
            SaveRegistration Save(SaveId id) => f.Registry.Saves().Single(x => x.SaveId == id);

            // 1. The claim names the newest save at its legacy path, served by a live polaris proxy; the older one is the fallback.
            RecoveryClaim claim = f.Registry.ClaimRecoveries(keeper, f.Row(keeper).Epoch, [partition]).Single();
            string legacy = SavePathOf(old, newest);
            SaveRegistration cover = claim.ResolveCover.Single().Save;
            Check(cover.SaveId == newest && cover.Locations.SetEquals([legacy]) && cover.HolderProxies?.GetValueOrDefault(old) is not null
                && claim.Fallbacks.Single().Save.SaveId == older, "the claim names the newest save at its legacy path, a proxy, and the older fallback");
            string proxy = cover.HolderProxies![old].Node;
            // 2. The node asks for the proxied grant (bound to L).
            GrantRequest Request(SaveId id) => new("save", "", id.Partition, id.Generation, id.Sequence, keeper, f.Row(keeper).Epoch, proxy, SavePathOf(old, id));
            TransferGrant grant = f.Registry.IssueGrant(Request(newest));
            // 3 + 4. The host agent's sweep lands inside the window.
            if (hold) Check(Adopt(false, newest) == "busy", "the query answers busy while the claim names the save");
            string committed = Sweep(newest);
            // 5. The proxy validates the grant and opens L; a missing L sends the node to the fallback.
            f.Registry.ValidateGrant(new(grant.Token, "save", "", newest.Partition, newest.Generation, newest.Sequence, proxy, f.Row(proxy).Epoch, legacy));
            if (!onDisk.Contains(legacy))
            {
                Check(!hold && committed == "rekeyed" && onDisk.Contains(SavePathOf(old, older)), "only the unguarded sweep may unlink the claimed file");
                return "fell back";
            }
            Check(committed == "busy" && Save(newest).Locations.SetEquals([legacy]), "the commit is refused (busy) and the location is unchanged: " + committed);

            // The grant is used up but the claim is still Recovering (the node may read its fallbacks): still busy.
            Check(Sweep(newest) == "busy" && Sweep(older) == "busy", "busy until the claim settles");
            // The node's heartbeat reports the loaded partition: Recovering clears and the move goes through.
            f.Heartbeat(keeper);
            Check(f.Registry.Partitions().Single(x => x.Partition == partition) is { Recovering: false, Node: var owner } && owner == keeper, "the claim settled");
            // A replica fetch's grant alone (no claim) holds the file too, until it is used or expires.
            TransferGrant replica = f.Registry.IssueGrant(Request(older));
            Check(Sweep(older) == "busy", "an unexpired grant bound to the location holds it");
            f.Registry.ValidateGrant(new(replica.Token, "save", "", older.Partition, older.Generation, older.Sequence, proxy, f.Row(proxy).Epoch, SavePathOf(old, older)));
            Check(Sweep(older) == "rekeyed", "a used grant no longer holds it");
            f.Registry.IssueGrant(Request(newest));
            Check(Sweep(newest) == "busy", "a fresh grant holds the newest save");
            f.Advance(61);
            Check(Sweep(newest) == "rekeyed" && onDisk.Count == 0, "an expired grant no longer holds it; both files moved");
            Check(HeldByHosts(f, Save(newest), "polaris") && HeldByHosts(f, Save(older), "polaris"), "both saves are now held by polaris' store");
            return "newest";
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
    }

    private static bool ArchiveSame(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out byte[]? other) && other.AsSpan().SequenceEqual(x.Value));
}
