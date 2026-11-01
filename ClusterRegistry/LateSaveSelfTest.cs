using ClusterProtocol;

namespace ClusterRegistry;

/// <summary>T-0347 (LateSaves.cs): the Finals of a stop with the registry down are catalogued at the next start when -
/// and only when - they are still the partition's newest state, and recovery waits for them. Earned reds: without the
/// hold the claim hands out the older Periodic save and the Final is then refused; a stale Final is refused by name.</summary>
internal static partial class SelfTest
{
    private static void TestLateSaves()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("late-saves: " + what); }
        TimeSpan configured = GatewayRegistry.LateSaveHold;
        string journalRoot = Path.Combine(Path.GetTempPath(), "cluster-late-saves-" + Guid.NewGuid());
        try
        {
            // The run of the hard stop: two nodes on polaris, one on ai, each owning partitions with a registered Periodic save.
            (NodeControlFixture Fixture, string[] Old, string[] Fresh, Dictionary<string, ulong[]> Owned, Dictionary<ulong, long> Generation) Stopped(string name, double holdSeconds)
            {
                GatewayRegistry.LateSaveHold = TimeSpan.FromSeconds(holdSeconds);
                var f = new NodeControlFixture("late-" + name);
                f.Apply(("polaris", 2, 0), ("ai", 1, 0));
                string[] polaris = [.. f.Live().Where(x => f.Slot(x)!.Host == "polaris")];
                string ai = f.Live().Single(x => f.Slot(x)!.Host == "ai");
                BootServing(f, polaris[0]); BootServing(f, polaris[1]); BootServing(f, ai, host: "ai");
                var owned = new Dictionary<string, ulong[]>();
                var generation = new Dictionary<ulong, long>();
                foreach (string node in (string[])[polaris[0], polaris[1], ai])
                {
                    NodeRegistration row = f.Row(node);
                    owned[node] = f.Registry.AllocatePartitionIds(row.Node, row.Epoch, 4);
                    foreach (ulong partition in owned[node]) generation[partition] = f.Own(node, partition, save: true).Generation;
                }
                // The registry dies and the cluster is stopped without it: no shutdown, no release. The next start gives fresh ids.
                string[] old = [polaris[0], polaris[1], ai];
                f.Reopen();
                f.Apply(("polaris", 2, 0), ("ai", 1, 0));
                foreach (string node in old) f.Silent.Add(node);
                string[] fresh = [.. f.Live().Where(x => !old.Contains(x)).OrderBy(x => f.Slot(x)!.Host == "polaris" ? 0 : 1).ThenBy(NodeControlFixture.Number)];
                Check(fresh.Length == 3 && f.Slot(fresh[0])!.Host == "polaris" && f.Slot(fresh[2])!.Host == "ai", "fresh ids after the stop: " + string.Join(",", f.Live()));
                return (f, old, fresh, owned, generation);
            }
            LateSaveReport Final(string writer, long writerEpoch, ulong partition, long generation, long sequence = 2) =>
                new(partition, generation, sequence, 20_000, $"final{partition}.{sequence}", 99, SavePathOf(writer, new SaveId(partition, generation, sequence)), writer, writerEpoch, 5);

            // Earned red: no hold. A claim that lands before the report recovers the Periodic save, and the Final is refused.
            {
                var (f, old, fresh, owned, generation) = Stopped("red", 0);
                using NodeControlFixture _ = f;
                long writerEpoch = f.Row(old[0]).Epoch;
                BootServing(f, fresh[0]);
                f.Advance(16);
                ulong partition = owned[old[0]][0];
                RecoveryClaim early = f.Registry.ClaimRecoveries(fresh[0], f.Row(fresh[0]).Epoch, [partition]).Single();
                Check(early.ResolveCover.Single().Save is { Kind: SaveKind.Periodic } periodic && periodic.SaveId.Sequence == 1,
                    "RED expected: without the hold the claim before the report is granted on the Periodic save");
                LateSaveVerdict refused = f.Registry.RegisterLateSaves(fresh[0], f.Row(fresh[0]).Epoch, [Final(old[0], writerEpoch, partition, generation[partition])]).Single();
                Check(!refused.Accepted && refused.Reason.Contains("moved on") && f.Registry.SavesFor(partition).All(x => x.SaveId.Sequence == 1),
                    "RED expected: the Final reported after that claim is refused, not catalogued over a recovered partition: " + refused.Reason);
            }

            {
                var (f, old, fresh, owned, generation) = Stopped("green", 300);
                using NodeControlFixture _ = f;
                long[] writerEpoch = [.. old.Select(x => f.Row(x).Epoch)];
                ulong accepted = owned[old[0]][0], stale = owned[old[0]][1], differs = owned[old[0]][2], foreign = owned[old[1]][0], remote = owned[old[2]][0];
                Check(f.Registry.LateSaveHeldCount == 12, "every partition owned at load is held: " + f.Registry.LateSaveHeldCount);
                // A registry-only restart is not a stop: an owner that heartbeats again is not waited for.
                {
                    var (alive, running, _, _, _) = Stopped("alive", 300);
                    using NodeControlFixture __ = alive;
                    alive.Heartbeat(running[0]);
                    Check(alive.Registry.LateSaveHeldCount == 8, "a live owner's partitions are not held: " + alive.Registry.LateSaveHeldCount);
                }
                NodeRegistration first = BootServing(f, fresh[0]);
                BootServing(f, fresh[2], host: "ai");
                f.Advance(16);
                Check(old.All(x => f.Row(x).State == NodeState.Dead) && f.Registry.Partitions().Where(x => generation.ContainsKey(x.Partition))
                        .All(x => x.Node == null && x.Generation == generation[x.Partition] + 1),
                    "the old nodes' leases lapsed: their partitions are unowned, one generation on");
                // The hold: neither an explicit claim nor the auto-pick hands a held partition out.
                Check(f.Registry.ClaimRecoveries(first.Node, first.Epoch, [accepted]).Length == 0 && f.Registry.ClaimRecoveries(first.Node, first.Epoch, []).Length == 0,
                    "no recovery of a held partition before its host has reported");

                // The node's side: the journal as the stopped writer left it (with a torn tail), found from a sibling's data root.
                string writerRoot = Path.Combine(journalRoot, old[0] + "-data", "cluster");
                foreach (LateSaveReport report in (LateSaveReport[])[Final(old[0], writerEpoch[0], accepted, generation[accepted]),
                    Final(old[0], writerEpoch[0], differs, generation[differs], sequence: 1), Final(old[1], writerEpoch[1], foreign, generation[foreign])])
                    UnregisteredFinals.Append(writerRoot, new UnregisteredFinal { Partition = report.Partition, Generation = report.Generation, Sequence = report.Sequence,
                        SizeBytes = report.SizeBytes, Checksum = report.Checksum, ClusterTime = report.ClusterTime, Entities = report.Entities ?? 0, Location = report.Location,
                        // The third entry names the wrong writer for its partition (another node's journal cannot speak for it).
                        Writer = old[0], WriterEpoch = writerEpoch[0] });
                File.AppendAllText(Path.Combine(writerRoot, UnregisteredFinals.FileName), "{\"Partition\":7,\"Gener");
                Directory.CreateDirectory(Path.Combine(journalRoot, fresh[0] + "-data"));
                string journal = UnregisteredFinals.JournalsUnder(Path.Combine(journalRoot, fresh[0] + "-data")).Single();
                List<UnregisteredFinal> entries = UnregisteredFinals.Read(journal, out int unreadable);
                Check(entries.Count == 3 && unreadable == 1, $"the journal reads back its three saves and skips the torn line: {entries.Count}/{unreadable}");
                LateSaveVerdict[] verdicts = f.Registry.RegisterLateSaves(first.Node, first.Epoch, [.. entries.Select(x =>
                    new LateSaveReport(x.Partition, x.Generation, x.Sequence, x.SizeBytes, x.Checksum, x.ClusterTime, x.Location, x.Writer, x.WriterEpoch, x.Entities, x.Homes))]);
                UnregisteredFinals.MarkReported(journal);
                Check(UnregisteredFinals.JournalsUnder(Path.Combine(journalRoot, fresh[0] + "-data")).Length == 0
                    && File.Exists(Path.Combine(writerRoot, UnregisteredFinals.ReportedFileName)), "an answered journal is not reported again");

                // #1298: Quasar's managed layout keeps each slot at <runtime>/nodes/<slot>/data; the stopped writer's journal is
                // in a sibling slot directory and the reporter's data root's parent holds only "data", so the plain scan finds
                // nothing, not even the reporter's own journal: that is the earned red.
                string nodes = Path.Combine(journalRoot, "runtime", ProxiedSavePath.ManagedNodesDirectory);
                string slotData = Path.Combine(nodes, "a1", "data"), managedJournal = Path.Combine(nodes, "b2", "data", "cluster", UnregisteredFinals.FileName);
                string ownJournal = Path.Combine(slotData, "cluster", UnregisteredFinals.FileName);
                foreach (string file in (string[])[managedJournal, ownJournal])
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, "");
                }
                Directory.CreateDirectory(Path.Combine(nodes, "c3"));   // a slot that never ran: no data directory
                Check(UnregisteredFinals.JournalsUnder(slotData).Length == 0, "RED expected: unmanaged, a managed slot's scan finds no journal");
                Check(UnregisteredFinals.JournalsUnder(slotData, managed: true).SequenceEqual([ownJournal, managedJournal]),
                    "managed: every slot's journal on this host is reported, its own included: "
                    + string.Join(", ", UnregisteredFinals.JournalsUnder(slotData, managed: true)));
                Check(UnregisteredFinals.JournalsUnder(Path.Combine(journalRoot, fresh[0] + "-data"), managed: true).Length == 0,
                    "managed flag on a data root outside runtime/nodes keeps the plain rule");
                LateSaveVerdict Verdict(ulong partition) => verdicts.Single(x => x.Partition == partition);
                Check(Verdict(accepted) is { Accepted: true, Reason: "catalogued" }, "the Final of an untouched partition is catalogued: " + Verdict(accepted).Reason);
                Check(!Verdict(differs).Accepted && Verdict(differs).Reason.Contains("different save"), "a save id the catalogue holds with other bytes is refused: " + Verdict(differs).Reason);
                Check(!Verdict(foreign).Accepted && Verdict(foreign).Reason.Contains("written by"), "a generation another node wrote is refused: " + Verdict(foreign).Reason);
                Check(f.Registry.SavesFor(differs).Single().Kind == SaveKind.Periodic && f.Registry.SavesFor(foreign).Single().Kind == SaveKind.Periodic,
                    "a refused save changes nothing in the catalogue");

                // The report releases its own host only.
                Check(f.Registry.LateSaveHeldCount == 4 && f.Registry.ClaimRecoveries(fresh[2], f.Row(fresh[2]).Epoch, [remote]).Length == 0,
                    "polaris reported; the ai partitions stay held: " + f.Registry.LateSaveHeldCount);

                // Recovery now rides the Final, served by a proxy on the dead writer's host.
                BootServing(f, fresh[1]);
                f.Pass();
                RecoveryClaim claim = f.Registry.ClaimRecoveries(fresh[2], f.Row(fresh[2]).Epoch, [accepted]).Single();
                SaveRegistration cover = claim.ResolveCover.Single().Save;
                Check(cover.SaveId == new SaveId(accepted, generation[accepted], 2) && cover.Kind == SaveKind.Final && cover.Entities == 5,
                    "the claim after the report covers the partition with the late Final: " + cover.Id);
                Check(Fetchable(f, cover) && cover.HolderProxies?.GetValueOrDefault(old[0]) is { } proxy && f.Slot(proxy.Node)!.Host == "polaris",
                    "and names a live polaris proxy for the stopped writer's file");

                // A second report of the same save (another node of the host read the journal too) is not an error.
                Check(f.Registry.RegisterLateSaves(first.Node, first.Epoch, [Final(old[0], writerEpoch[0], accepted, generation[accepted])]).Single() is { Accepted: true, Reason: "already catalogued" },
                    "a repeated report is idempotent");

                // Falsifier: a Final for a partition somebody recovered in between must NOT win, however new it claims to be.
                RecoveryClaim taken = f.Registry.ClaimRecoveries(first.Node, first.Epoch, [stale]).Single();
                Check(taken.ResolveCover.Single().Save.SaveId.Sequence == 1, "the released partition recovers from its Periodic save");
                LateSaveVerdict late = f.Registry.RegisterLateSaves(first.Node, first.Epoch, [Final(old[0], writerEpoch[0], stale, generation[stale])]).Single();
                Check(!late.Accepted && late.Reason.Contains("moved on") && f.Registry.SavesFor(stale).All(x => x.SaveId.Sequence == 1),
                    "a Final reported after its partition was recovered is refused: " + late.Reason);
                // ... and one generation too old for a row that was released twice.
                f.Heartbeat(first.Node);
                PartitionOwnership again = f.Registry.Partitions().Single(x => x.Partition == stale);
                f.Registry.ReleasePartition(stale, first.Node, first.Epoch, again.Generation);
                late = f.Registry.RegisterLateSaves(first.Node, first.Epoch, [Final(old[0], writerEpoch[0], stale, generation[stale])]).Single();
                Check(!late.Accepted && late.Reason.Contains("moved on"), "nor after the recovered copy was released again: " + late.Reason);

                // The deadline: a host that never reports does not hold its partitions for good.
                f.Advance(300);
                Check(f.Registry.ClaimRecoveries(fresh[2], f.Row(fresh[2]).Epoch, [remote]).Single().ResolveCover.Single().Save.Kind == SaveKind.Periodic
                    && f.Registry.LateSaveHeldCount == 0, "past the deadline an unreported partition recovers from the catalogue");
            }
        }
        finally
        {
            GatewayRegistry.LateSaveHold = configured;
            try { Directory.Delete(journalRoot, true); } catch (IOException) { }
        }
        Console.WriteLine("late-saves: recovery waits for the host's report; an untouched partition's Final is catalogued and served by proxy; stale, foreign and conflicting Finals are refused");
    }
}
