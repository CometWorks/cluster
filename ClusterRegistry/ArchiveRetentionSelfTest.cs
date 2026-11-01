using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClusterRegistry;

/// <summary>T-0400 archive retention (ArchiveRetention.cs): the files of the saves of a partition archived more than
/// retention.archiveFileDays days ago are deleted through the delete-turn directive/ack path; the rows stay.</summary>
internal static partial class SelfTest
{
    /// <summary>A retired, archived partition whose one save is in polaris' store with a copy in ai's; its two outputs live
    /// on <paramref name="node"/> with node-held saves (so a corruption report can make the parent needed again).</summary>
    private static (SaveId Save, ulong[] Outputs) ArchivedInStores(NodeControlFixture f, string node, string ai, bool legacyHolder = false)
    {
        NodeRegistration row = f.Row(node);
        ulong parent = OwnNewPartition(f, node);
        SaveId save;
        if (legacyHolder) save = SaveOn(f, node, parent, 1);
        else
        {
            save = SaveInStore(f, node, "polaris", parent, 1);
            CopyIntoStore(f, save, ai, "ai");
        }
        ulong[] outputs = f.Registry.AllocatePartitionIds(row.Node, row.Epoch, 2);
        f.Registry.ApplyLineage("split-" + parent, LineageKind.Split, row.Node, row.Epoch, [new(parent, save.Generation)], outputs,
            new() { [outputs[0]] = [(long)parent * 10 + 1], [outputs[1]] = [(long)parent * 10 + 2] }, 5);
        foreach (ulong output in outputs) SaveOn(f, node, output, 1);
        return (save, outputs);
    }

    /// <summary>Clock forward by <paramref name="span"/> in one step; every live node then heartbeats (its lease is renewed
    /// before anything expires it), so the stores stay Serving.</summary>
    private static void Jump(NodeControlFixture f, TimeSpan span)
    {
        f.Now += span;
        foreach (NodeRegistration row in f.Registry.Nodes().Where(x => x.State is NodeState.Active or NodeState.Closed && !x.Node.StartsWith("host:", StringComparison.Ordinal)))
            f.Heartbeat(row.Node);
    }

    private static JsonElement RetentionStatus(NodeControlFixture f) => JsonSerializer.SerializeToElement(f.Registry.ArchiveRetentionStatusForTest());

    private static string Capture(Action action)
    {
        TextWriter console = Console.Out;
        var log = new StringWriter();
        Console.SetOut(log);
        try { action(); }
        finally { Console.SetOut(console); }
        Console.Write(log.ToString());
        return log.ToString();
    }

    // The main flow: stamped with the archive move; knob 0 and an age under N do nothing; past N the host-held files go to
    // the delete-turn node of their host, the ack rewrites the ARCHIVED row (kept; location in DeletedLocations, not in
    // Saves); a restart keeps the stamp and re-queues what is still catalogued; the last ack marks the partition swept;
    // and a recovery that needs the deleted save afterwards logs the named line instead of falling back silently.
    private static void TestArchiveRetention()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("archive-retention: " + what); }
        NodeControlFixture f = HostStoreFixture("archive-retention", out string top, ("polaris", 2, 0), ("ai", 1, 0));
        try
        {
            string[] polaris = PlannedOn(f, "polaris");
            string ai = PlannedOn(f, "ai").Single();
            (SaveId save, ulong[] outputs) = ArchivedInStores(f, polaris[0], ai);
            ulong parent = save.Partition;
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            Check(f.Registry.ArchivedPartitionCount == 1 && f.Registry.Saves().All(x => x.SaveId.Partition != parent), "the parent is archived");
            f.Registry.Checkpoint();
            DateTimeOffset archivedAt = f.Now;
            Check(f.Registry.ArchiveStampForTest(parent) is { Swept: false } stamp && stamp.ArchivedMs == archivedAt.ToUnixTimeMilliseconds(),
                "the archive move stamps the partition with the time it was archived");
            DeleteDirective[] Pending(string node) => f.Registry.PendingDeletes(node, f.Row(node).Epoch);
            string polarisFile = GatewayRegistry.HostStorePath(f.Registry.RunRoot, "polaris", save), aiFile = GatewayRegistry.HostStorePath(f.Registry.RunRoot, "ai", save);

            // Under N days (default 7): untouched.
            Jump(f, TimeSpan.FromDays(6));
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 0 && Pending(polaris[0]).Length == 0 && Pending(ai).Length == 0, "a partition archived 6 days ago is untouched");
            // Knob 0 = off, even past the age.
            f.Registry.SetHandoverConfig(new Dictionary<string, string?> { [GatewayRegistry.ArchiveFileDaysKey] = "0" });
            Jump(f, TimeSpan.FromDays(2));
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 0 && Pending(polaris[0]).Length == 0 && RetentionStatus(f).GetProperty("days").GetInt32() == 0,
                "retention.archiveFileDays=0 deletes nothing");
            // Back to the default: due.
            f.Registry.SetHandoverConfig(new Dictionary<string, string?> { [GatewayRegistry.ArchiveFileDaysKey] = null });
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 2, $"past 7 days both host files are queued ({f.Registry.ArchiveFilesOutstanding})");
            // Switched off with files queued but not handed out: the queue empties at once, nothing is handed out.
            f.Registry.SetHandoverConfig(new Dictionary<string, string?> { [GatewayRegistry.ArchiveFileDaysKey] = "0" });
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 0 && Pending(polaris[0]).Length == 0 && Pending(ai).Length == 0, "switched off, the queue empties");
            f.Registry.SetHandoverConfig(new Dictionary<string, string?> { [GatewayRegistry.ArchiveFileDaysKey] = null });
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 2, "switched back on, queued again");
            Check(f.Registry.ArchivedPartitionCount == 1 && f.Registry.ArchiveRehydrations == 0, "queueing reads the archive without bringing the partition back");
            DeleteDirective[] first = Pending(polaris[0]);
            Check(first.Length == 1 && first[0].Location == polarisFile && first[0].Node == polaris[0] && first[0].SaveId == save,
                "the polaris file goes to the node with polaris' delete turn, at the store path");
            Check(Pending(polaris[1]).Length == 0, "the other polaris node is not handed it meanwhile");
            DeleteDirective[] remote = Pending(ai);
            Check(remote.Length == 1 && remote[0].Location == aiFile, "the ai file goes to ai's node");
            Check(f.Registry.AcknowledgeDelete(first[0].SaveId, first[0].Location) && !f.Registry.AcknowledgeDelete(first[0].SaveId, first[0].Location),
                "the ack lands once");
            SaveRegistration? row = f.Registry.ArchivedSaveForTest(save);
            Check(row != null && row.Locations.SetEquals([aiFile]) && row.DeletedLocations?.Keys.SequenceEqual([polarisFile]) == true
                && !row.LocationHolders.ContainsKey(polarisFile), "the archived row is kept, without the deleted location, which DeletedLocations records");
            Check(f.Registry.Saves().All(x => x.SaveId.Partition != parent) && f.Registry.ArchivedPartitionCount == 1, "the row stays archived, not in Saves");
            Check(RetentionStatus(f).GetProperty("deletedLocations").GetInt64() == 1, "status counts the deleted location");

            // A restart before ai acks: the stamp and the deletion survive; the outstanding directive is rebuilt from the row.
            f.Reopen();
            foreach (string node in polaris.Append(ai)) f.Heartbeat(node);
            Check(f.Registry.ArchiveStampForTest(parent) is { Swept: false } kept && kept.ArchivedMs == archivedAt.ToUnixTimeMilliseconds(),
                "the restart keeps the stamp");
            Check(f.Registry.ArchiveFilesOutstanding == 0 && f.Registry.AcknowledgeDelete(remote[0].SaveId, remote[0].Location) == false,
                "the outstanding directives are memory only: an ack the restart lost is refused, the location stays catalogued");
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 1 && Pending(ai).Single().Location == aiFile && Pending(polaris[0]).Length == 0,
                "the next pass re-queues exactly what the row still names");
            Check(f.Registry.AcknowledgeDelete(save, aiFile), "ai's ack lands");
            row = f.Registry.ArchivedSaveForTest(save);
            Check(row != null && row.Locations.Count == 0 && row.DeletedLocations?.Count == 2, "both locations deleted, the row kept");
            Check(f.Registry.ArchiveStampForTest(parent) is { Swept: true, Legacy: 0 }, "the last ack marks the partition swept");
            Check(RetentionStatus(f).GetProperty("deletedLocations").GetInt64() == 2 && f.Registry.ArchiveFilesOutstanding == 0, "status: 2 deleted, none outstanding");
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveRetentionLastRows == 0, "a swept partition is not read again");

            // A live cover needs the parent again (its output's only save is reported corrupt): the rows come back with their
            // deletions and the named line, and the cover does not offer the deleted save.
            SaveId lost = f.Registry.Saves().Single(x => x.SaveId.Partition == outputs[0]).SaveId;
            string log = Capture(() =>
            {
                f.Registry.ReportCorruption(lost, polaris[0], polaris[1], f.Row(polaris[1]).Epoch);
                SaveCover[] cover = f.Registry.ResolveCover(outputs[0]);
                Check(cover.All(x => x.Save.SaveId != save || x.Save.Locations.Count == 0), "the cover offers no deleted file");
            });
            Check(log.Contains($"registry: archived save {parent}:{save.Generation}:{save.Sequence} files deleted at ") && log.Contains("(archive retention) - not recoverable"),
                "the recovery that needed the deleted save logs the named line");
            Check(f.Registry.ArchivedPartitionCount == 0 && f.Registry.Saves().Single(x => x.SaveId == save).DeletedLocations?.Count == 2,
                "the rehydrated row keeps its deletions");
            Check(f.Registry.ArchiveStampForTest(parent) == null, "the stamp leaves with the archive");
            f.Registry.Checkpoint();
            Check(f.Registry.ArchiveStampsForTest().Count == 0, "and is gone from the store with the next write");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("archive-retention: stamped at the move; off at 0 and under N days; past N the host files go through the delete turn; acks keep the row; restart re-queues; swept; recovery fails loudly");
    }

    // What retention must never touch, whatever the age: a partition a snapshot references (brought back by the snapshot,
    // as before) or a live cover needs (brought back by the pass's own walk), a recovering or owned row. A node id holder's
    // file goes to that node while it is live (T-0412; a stopped holder's: archive-retention-node-held).
    private static void TestArchiveRetentionGuards()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("archive-retention-guards: " + what); }
        NodeControlFixture f = HostStoreFixture("archive-guards", out string top, ("polaris", 2, 0), ("ai", 1, 0));
        try
        {
            string[] polaris = PlannedOn(f, "polaris");
            string ai = PlannedOn(f, "ai").Single();
            (SaveId pinned, _) = ArchivedInStores(f, polaris[0], ai);
            (SaveId covered, ulong[] coveredOutputs) = ArchivedInStores(f, polaris[0], ai);
            (SaveId owned, _) = ArchivedInStores(f, polaris[0], ai);
            (SaveId legacy, _) = ArchivedInStores(f, polaris[0], ai, legacyHolder: true);
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            Check(f.Registry.ArchivedPartitionCount == 4, $"four parents archived ({f.Registry.ArchivedPartitionCount})");
            f.Registry.Checkpoint();
            Jump(f, TimeSpan.FromDays(8));
            DeleteDirective[] Pending(string node) => f.Registry.PendingDeletes(node, f.Row(node).Epoch);
            bool Untouched(SaveId id) => Pending(polaris[0]).Concat(Pending(ai)).All(x => x.SaveId != id);

            // A snapshot naming the save brings it back (as today) and pins it.
            f.Registry.CreateSnapshot("pin", SnapshotKind.Online, 100, [pinned], []);
            Check(f.Registry.Saves().Any(x => x.SaveId == pinned), "the snapshot brings the save back");
            // A live cover falls back on the other one: the pass's own needed walk brings it back before retention runs.
            SaveId lost = f.Registry.Saves().Single(x => x.SaveId.Partition == coveredOutputs[0]).SaveId;
            f.Registry.ReportCorruption(lost, polaris[0], polaris[1], f.Row(polaris[1]).Epoch);
            // Recovering, then owned: the guard itself (no API reaches these states without a rehydrate).
            f.Registry.TestSetOwnerRow(owned.Partition, null, recovering: true);
            f.Registry.PlanRetention();
            Check(f.Registry.Saves().Any(x => x.SaveId == covered) && f.Registry.Saves().Single(x => x.SaveId == covered).Locations.Count == 2,
                "the live cover's save came back whole");
            DeleteDirective own = Pending(polaris[0]).Single();
            Check(own.SaveId == legacy && own.Node == polaris[0] && Pending(ai).Length == 0, "T-0412: a live node's own copy goes to that node only");
            Check(f.Registry.AcknowledgeDelete(own.SaveId, own.Location), "and its ack lands");
            Check(Untouched(pinned) && Untouched(covered) && Untouched(owned) && f.Registry.ArchiveFilesOutstanding == 0,
                "nothing else is handed out: pinned, covered, recovering");
            Check(RetentionStatus(f).GetProperty("skippedPartitions").GetInt64() == 1, "the recovering partition is counted as skipped");
            f.Registry.TestSetOwnerRow(owned.Partition, polaris[0], recovering: false);
            f.Registry.PlanRetention();
            Check(Untouched(owned) && f.Registry.ArchiveFilesOutstanding == 0, "an owned row is never touched");
            Check(f.Registry.ArchiveStampForTest(legacy.Partition) is { Swept: true, Legacy: 0 } && f.Registry.ArchivedSaveForTest(legacy)?.Locations.Count == 0,
                "the node copy's ack sweeps its partition");
            // The control: released, the same partition is queued at once.
            f.Registry.TestSetOwnerRow(owned.Partition, null, recovering: false);
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 2 && Pending(polaris[0]).Single().SaveId == owned, "the control: the released partition is queued");
            // Recovering again while in flight: the file not handed out yet (ai's) leaves the queue at the next pass; the one
            // polaris was handed stays, so its ack still lands.
            f.Registry.TestSetOwnerRow(owned.Partition, null, recovering: true);
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 1 && Pending(ai).Length == 0, "an in-flight partition that turns recovering hands out nothing more");
            DeleteDirective handed = Pending(polaris[0]).Single();
            Check(f.Registry.AcknowledgeDelete(handed.SaveId, handed.Location) && f.Registry.ArchiveStampForTest(owned.Partition) is { Swept: false },
                "the handed-out file's ack lands; the partition is not swept (ai's file is still there)");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("archive-retention-guards: snapshot, live cover, recovering, owned never touched; a live node's own copy goes to it");
    }

    // T-0412: without a host store (a Quasar-managed cluster: every copy is node-held, in a node dir that lives as long as
    // the node) retention runs all the same. A live holder is handed its own copy, and only its own; a Dead holder's copy
    // is never handed out and keeps its partition unswept until the node comes back under the same id (as Quasar restarts
    // it) and deletes it; a directive the holder does not acknowledge within the grace is recorded as deleted, so it cannot
    // hold the batch for ever. Red on the pre-T-0412 code: nothing is queued.
    private static void TestArchiveRetentionNodeHeld()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("archive-retention-node-held: " + what); }
        using var f = new NodeControlFixture("archive-node-held");
        foreach (string node in new[] { "node-1", "node-2", "node-3" }) BootServing(f, node);
        SaveId ArchivedOn(string node, string? copy)
        {
            NodeRegistration row = f.Row(node);
            ulong parent = OwnNewPartition(f, node);
            SaveId save = SaveOn(f, node, parent, 1);
            if (copy != null) CopyTo(f, save, copy);
            ulong[] outputs = f.Registry.AllocatePartitionIds(row.Node, row.Epoch, 2);
            f.Registry.ApplyLineage("split-" + parent, LineageKind.Split, row.Node, row.Epoch, [new(parent, save.Generation)], outputs,
                new() { [outputs[0]] = [1], [outputs[1]] = [2] }, 5);
            foreach (ulong output in outputs) SaveOn(f, node, output, 1);
            return save;
        }
        SaveId held = ArchivedOn("node-1", copy: "node-2");
        SaveId stopped = ArchivedOn("node-3", copy: null);
        f.Registry.PlanRetention();
        f.Registry.PlanRetention();
        Check(f.Registry.ArchivedPartitionCount == 2, $"both parents archived ({f.Registry.ArchivedPartitionCount})");
        f.Registry.Checkpoint();
        // node-3's partitions go elsewhere (their saves copied to node-1, so no live cover needs the parent) and it dies:
        // its copy of the parent is in a stopped node's dir.
        foreach (SaveRegistration output in f.Registry.Saves().Where(x => x.Node == "node-3").ToArray()) CopyTo(f, output.SaveId, "node-1");
        foreach (PartitionOwnership owned in f.Registry.PartitionsOwnedBy("node-3", f.Row("node-3").Epoch))
            f.Registry.ReleasePartition(owned.Partition, "node-3", f.Row("node-3").Epoch, owned.Generation);
        Kill(f, "node-3");
        Check(f.Row("node-3").State == NodeState.Dead, "node-3 is Dead");
        DeleteDirective[] Pending(string node) => f.Registry.PendingDeletes(node, f.Row(node).Epoch);

        Jump(f, TimeSpan.FromDays(6));
        f.Registry.PlanRetention();
        Check(f.Registry.ArchiveFilesOutstanding == 0 && Pending("node-1").Length == 0, "under 7 days nothing is queued");
        Jump(f, TimeSpan.FromDays(2));
        f.Registry.PlanRetention();
        Check(f.Registry.ArchiveFilesOutstanding == 2, $"no host store: both live holders' copies are queued ({f.Registry.ArchiveFilesOutstanding})");
        DeleteDirective[] one = Pending("node-1"), two = Pending("node-2");
        Check(one.Length == 1 && one[0].SaveId == held && one[0].Node == "node-1" && one[0].Location == SavePathOf("node-1", held),
            "node-1 is handed its own copy only");
        Check(two.Length == 1 && two[0].Location == SavePathOf("node-2", held), "node-2 is handed its own copy only");
        Check(f.Registry.ArchiveStampForTest(stopped.Partition) is { Swept: false }
            && f.Registry.ArchivedSaveForTest(stopped)?.Locations.SequenceEqual([SavePathOf("node-3", stopped)]) == true
            && RetentionStatus(f).GetProperty("legacyHolderLocations").GetInt64() == 0, "a Dead holder's copy stays catalogued, its partition unswept, nothing handed out");
        Check(f.Registry.AcknowledgeDelete(held, one[0].Location), "node-1's ack lands");
        SaveRegistration? row = f.Registry.ArchivedSaveForTest(held);
        Check(row != null && row.Locations.SequenceEqual([SavePathOf("node-2", held)]) && row.DeletedLocations?.Keys.SequenceEqual([SavePathOf("node-1", held)]) == true,
            "the archived row is kept, node-1's location in DeletedLocations");
        Check(f.Registry.ArchivedPartitionCount == 2 && f.Registry.ArchiveRehydrations == 0, "nothing is brought back");

        // node-2 never acknowledges: within the grace its directive stays; past it, it is recorded as deleted.
        Jump(f, GatewayRegistry.ArchiveHandOutGrace / 2);
        f.Registry.PlanRetention();
        Check(f.Registry.ArchiveFilesOutstanding == 1 && Pending("node-2").Length == 1, "within the grace the unacknowledged directive stays");
        Jump(f, GatewayRegistry.ArchiveHandOutGrace);
        string log = Capture(() => f.Registry.PlanRetention());
        row = f.Registry.ArchivedSaveForTest(held);
        Check(f.Registry.ArchiveFilesOutstanding == 0 && Pending("node-2").Length == 0 && row?.Locations.Count == 0 && row.DeletedLocations?.Count == 2,
            "past the grace it is recorded as deleted");
        Check(log.Contains("1 node-held archived file(s) handed out more than 10 min ago and not acknowledged by their holder; recorded as deleted"),
            "one WARNING line says so");
        JsonElement status = RetentionStatus(f);
        Check(status.GetProperty("unackedRecordedDeleted").GetInt64() == 1 && status.GetProperty("deletedLocations").GetInt64() == 1,
            "status: 1 acknowledged, 1 recorded unacknowledged");
        Check(f.Registry.ArchiveStampForTest(held.Partition) is { Swept: true, Legacy: 0 }, "the partition is swept");

        // node-3 comes back under the same id (a Quasar restart): its copy goes to it, and its partition is swept.
        f.Silent.Remove("node-3");
        BootServing(f, "node-3");
        f.Registry.PlanRetention();
        DeleteDirective[] three = Pending("node-3");
        Check(three.Length == 1 && three[0].SaveId == stopped && three[0].Location == SavePathOf("node-3", stopped), "the restarted node-3 is handed its copy");
        Check(f.Registry.AcknowledgeDelete(stopped, three[0].Location) && f.Registry.ArchiveStampForTest(stopped.Partition) is { Swept: true, Legacy: 0 },
            "its ack sweeps the partition");
        Console.WriteLine("archive-retention-node-held: no host store: live holders get their own copies; a Dead holder's waits for it to come back; an unacknowledged directive is recorded deleted after the grace");
    }

    // An archive written before this build has no stamps: the first load stamps every partition at that moment (its files
    // go N days later, not by save age), and logs one line with the read-only proxy count.
    private static void TestArchiveRetentionFirstSight()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("archive-retention-first-sight: " + what); }
        NodeControlFixture f = HostStoreFixture("archive-first-sight", out string top, ("polaris", 2, 0), ("ai", 1, 0));
        try
        {
            string[] polaris = PlannedOn(f, "polaris");
            string ai = PlannedOn(f, "ai").Single();
            (SaveId save, _) = ArchivedInStores(f, polaris[0], ai);
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            f.Registry.Checkpoint();
            // The previous package: no stamp table rows (it never wrote any).
            f.Registry.Dispose();
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(f.Directory, RegistryStore.FileName), Pooling = false }.ToString()))
            {
                db.Open();
                using SqliteCommand command = db.CreateCommand();
                command.CommandText = $"DELETE FROM \"{RegistryStore.StampTable}\"";
                Check(command.ExecuteNonQuery() == 1, "the fixture had one stamp to remove");
            }
            f.Now += TimeSpan.FromDays(30);
            DateTimeOffset firstSight = f.Now;
            string log = Capture(() => f.Reopen());
            foreach (string node in polaris.Append(ai)) f.Heartbeat(node);
            Check(log.Contains("registry: archive retention: 1 archived partition(s) had no archive stamp and were stamped at first sight")
                && log.Contains("1 would already be past 7 day(s), 0 without a proxy; not used to delete"), "one load line with the stamp count and the proxy count");
            Check(f.Registry.ArchiveStampForTest(save.Partition) is { Swept: false } stamp && stamp.ArchivedMs == firstSight.ToUnixTimeMilliseconds(),
                "stamped with the first load's time, not the save's age");
            Check(RetentionStatus(f).GetProperty("firstSightStamped").GetInt64() == 1, "status reports the first-sight stamps");
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 0, "30-day-old saves are not due: the age counts from first sight");
            Jump(f, TimeSpan.FromDays(7.5));
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 2, "due 7 days after first sight");
            // A second load stamps nothing and logs nothing.
            log = Capture(() => f.Reopen());
            Check(!log.Contains("stamped at first sight"), "a stamped archive is not stamped again");
        }
        finally
        {
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
        Console.WriteLine("archive-retention-first-sight: an unstamped archive is stamped at its first load, logged once with the read-only proxy count");
    }

    // Pace: one pass reads at most CLUSTER_ARCHIVE_FILE_BATCH archived rows and keeps at most about that many files
    // outstanding (a partition is read whole, so it may overshoot by one partition's files); the rest waits for the acks.
    // Then the lock time of a full-size pass, measured.
    private static void TestArchiveRetentionPace()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("archive-retention-pace: " + what); }
        int batch = GatewayRegistry.ArchiveFileBatch;
        bool verify = GatewayRegistry.VerifyStoreAfterWrite;
        NodeControlFixture f = HostStoreFixture("archive-pace", out string top, ("polaris", 2, 0), ("ai", 1, 0));
        try
        {
            string[] polaris = PlannedOn(f, "polaris");
            string ai = PlannedOn(f, "ai").Single();
            for (int i = 0; i < 4; i++) ArchivedInStores(f, polaris[0], ai);
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            Check(f.Registry.ArchivedPartitionCount == 4, "four parents archived");
            f.Registry.Checkpoint();
            Jump(f, TimeSpan.FromDays(8));
            GatewayRegistry.ArchiveFileBatch = 3;
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveRetentionLastRows <= 3 && f.Registry.ArchiveFilesOutstanding == 4,
                $"batch 3: two partitions' files queued ({f.Registry.ArchiveFilesOutstanding}), {f.Registry.ArchiveRetentionLastRows} row(s) read");
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveRetentionLastRows == 0 && f.Registry.ArchiveFilesOutstanding == 4, "a full queue reads nothing more");
            foreach (string node in new[] { polaris[0], ai })
                foreach (DeleteDirective directive in f.Registry.PendingDeletes(node, f.Row(node).Epoch))
                    Check(f.Registry.AcknowledgeDelete(directive.SaveId, directive.Location), "ack");
            f.Registry.PlanRetention();
            Check(f.Registry.ArchiveFilesOutstanding == 4, "after the acks, the next two partitions");
            GatewayRegistry.ArchiveFileBatch = batch;

            // The measurement: a pass at the shipped batch over enough archived partitions to fill it.
            GatewayRegistry.VerifyStoreAfterWrite = false;
            int count = batch / 2;
            for (int i = 0; i < count; i++) ArchivedInStores(f, polaris[0], ai);
            f.Registry.PlanRetention();
            f.Registry.PlanRetention();
            f.Registry.Checkpoint();
            Jump(f, TimeSpan.FromDays(8));
            foreach (string node in new[] { polaris[0], ai })
                foreach (DeleteDirective directive in f.Registry.PendingDeletes(node, f.Row(node).Epoch))
                    f.Registry.AcknowledgeDelete(directive.SaveId, directive.Location);
            f.Registry.PlanRetention();
            double ms = f.Registry.ArchiveRetentionLastMs;
            int rows = f.Registry.ArchiveRetentionLastRows, queued = f.Registry.ArchiveFilesOutstanding;
            Check(rows > 0 && rows <= batch + 1 && queued <= batch + 2, $"the shipped batch bounds the pass ({rows} rows, {queued} queued)");
            f.Registry.PlanRetention();
            double idle = f.Registry.ArchiveRetentionLastMs;
            Console.WriteLine($"archive-retention-pace: one pass at CLUSTER_ARCHIVE_FILE_BATCH={batch}: {rows} archived row(s) read, {queued} file(s) queued, "
                + $"{ms:F1} ms under the lock; next pass with the queue full: {idle:F2} ms");
        }
        finally
        {
            GatewayRegistry.ArchiveFileBatch = batch;
            GatewayRegistry.VerifyStoreAfterWrite = verify;
            GatewayRegistry.HostStoreEnabled = false;
            f.Dispose();
            Directory.Delete(top, true);
        }
    }

    // The knob: default, normalisation, refusal before anything changes, and an unreadable stored value reads as OFF.
    private static void TestArchiveRetentionKnob()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("archive-retention-knob: " + what); }
        Check(GatewayRegistry.ArchiveFileDaysOf(null) == 7, "absent = 7 days");
        Check(GatewayRegistry.ArchiveFileDaysOf("0") == 0 && GatewayRegistry.ArchiveFileDaysOf(" 14 ") == 14, "0 = off, 14 = 14");
        Check(GatewayRegistry.ArchiveFileDaysOf("-1") == 0 && GatewayRegistry.ArchiveFileDaysOf("7.5") == 0 && GatewayRegistry.ArchiveFileDaysOf("x") == 0,
            "an unreadable value deletes nothing");
        Check(GatewayRegistry.NormalizeArchiveFileDays(" 3 ") == "3", "normalised");
        foreach (string bad in new[] { "-1", "7.5", "off", "" })
        {
            bool refused = false;
            try { GatewayRegistry.NormalizeArchiveFileDays(bad); } catch (ArgumentException) { refused = true; }
            Check(refused, $"'{bad}' refused");
        }
        // The sweep's slim row read sees exactly what a full deserialization sees, whatever else the row carries.
        var full = new SaveRegistration(new SaveId(ulong.MaxValue - 3, 7, 9), "node-4", 3, SaveKind.Final, 20_000, "c", 12, ["/a/x.mps", "/b/y.mps", "/c/z.mps"],
            DateTimeOffset.UnixEpoch)
        {
            HomeStamps = new() { [1] = 2 }, Entities = 4, PendingDelete = true,
            LocationHolders = new() { ["/a/x.mps"] = "host:polaris", ["/b/y.mps"] = "node-9" },
            HolderEndpoints = new() { ["node-9"] = "http://n9" }, CorruptHolders = ["node-9"],
            DeletedLocations = new() { ["/d/w.mps"] = DateTimeOffset.UnixEpoch },
        };
        (SaveId scannedId, string scannedWriter, List<string> scannedLocations, List<(string, string)> scannedHolders) =
            GatewayRegistry.ScanArchivedRowForTest(JsonSerializer.SerializeToUtf8Bytes(full));
        Check(scannedId == full.SaveId && scannedWriter == full.Node && scannedLocations.SequenceEqual(full.Locations)
            && scannedHolders.OrderBy(x => x.Item1).SequenceEqual(full.LocationHolders.Select(x => (x.Key, x.Value)).OrderBy(x => x.Item1)),
            "the slim row read matches the full deserialization");
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(10));
            bool refused = false;
            try { registry.SetHandoverConfig(new Dictionary<string, string?> { ["tee.async"] = "true", [GatewayRegistry.ArchiveFileDaysKey] = "-3" }); }
            catch (ArgumentException) { refused = true; }
            Check(refused && registry.HandoverConfigRevision == 0 && registry.HandoverConfig().Values.Count == 0, "a bad value refuses the whole update");
            HandoverConfigDocument set = registry.SetHandoverConfig(new Dictionary<string, string?> { [GatewayRegistry.ArchiveFileDaysKey] = " 10 " });
            Check(set.Values[GatewayRegistry.ArchiveFileDaysKey] == "10" && set.Revision == 1, "stored normalised");
            Check(JsonSerializer.SerializeToElement(registry.Status()).GetProperty("archiveRetention").GetProperty("days").GetInt32() == 10, "status shows the live value");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("archive-retention-knob: default 7, 0 = off, refused before any change, unreadable reads as off");
    }
}
