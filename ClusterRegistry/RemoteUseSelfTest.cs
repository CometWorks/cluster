using System.Text.Json;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    private static void TestRemoteUse()
    {
        void Check(bool value, string why) { if (!value) throw new InvalidOperationException("remote-use: " + why); }
        string directory = Directory.CreateTempSubdirectory("gw-selftest-remote-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            var one = Register(registry, "one", "ep-one", Guid.NewGuid());
            var two = Register(registry, "two", "ep-two", Guid.NewGuid());
            registry.Heartbeat(one.Node, one.Epoch, []); registry.Heartbeat(two.Node, two.Epoch, []);
            var owned = registry.ClaimPartition(17, one.Node, one.Epoch, 0) with { };
            registry.RegisterSave(new SaveId(17, owned.Generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "a", 1, "17/1", "http://one/control");
            registry.ReportRemoteUse(one.Node, one.Epoch, new() { [17] = [81, 81, 0], [999] = [82] });
            Check(registry.HasRemoteUse(17) && !registry.HasRemoteUse(999), "only owned partitions carry heartbeat remote presence");
            bool refused = false;
            try { registry.ReportRemoteUse(one.Node, one.Epoch + 1, new()); } catch (RegistryConflictException) { refused = true; }
            Check(refused && registry.HasRemoteUse(17), "stale node epoch cannot clear presence");
            var offer = new OfflineRequest(17, one.Node, one.Epoch, owned.Generation, 1, 100, 0, 0, 10, [[100, 0, 0, 10]], [170], 100, "[]", "[]")
                { OfflineGameFrame = 100, ProfileSummary = JsonSerializer.SerializeToElement(new[] { new { Group = 170, Confidence = "Exact" } }) };
            refused = false;
            try { registry.TakeOffline(offer); } catch (RegistryConflictException) { refused = true; }
            Check(refused && !registry.IsOffline(17), "remote-use blocks offline in the registry invariant, not only its HTTP caller");
            registry.ReportRemoteUse(one.Node, one.Epoch, new());
            Check(!registry.HasRemoteUse(17), "closing UI/disconnect clears remote presence");
            var offline = registry.TakeOffline(offer);
            Check(offline.OfflineGameFrame == 100 && offline.ProfileSummary?.GetArrayLength() == 1, "profile summary survives the offline offer");
            Check(registry.RequestOfflineLoad(17, "missing", "remote") == "no-target" && offline.LoadRequestedNode == null,
                "radio call-up never redirects away from an unavailable caller");
            Check(registry.RequestOfflineLoad(17, null, "laser") == "no-target" && offline.LoadRequestedNode == null,
                "laser call-up needs its source node");
            Check(registry.RequestOfflineLoad(17, one.Node, "remote") == "requested:one", "call-up directed to caller");
            now += TimeSpan.FromSeconds(11);
            registry.Heartbeat(one.Node, one.Epoch, []); registry.Heartbeat(two.Node, two.Epoch, []);
            Check(registry.RequestOfflineLoad(17, one.Node, "approach") == "requested:one" && offline.LoadReason == "remote",
                "same-node proximity refresh cannot demote the call-up reservation");
            Check(registry.RequestOfflineLoad(17, two.Node, "remote") == "requested:one", "second caller cannot steal first target before load");
            Check(registry.ClaimRecoveries(two.Node, two.Epoch, []).Length == 0, "only reserved caller can claim");
            var claim = registry.ClaimRecoveries(one.Node, one.Epoch, []).Single();
            Check(claim.Ownership.Partition == 17 && claim.ResolveCover.Any(c => c.Save.SaveId == new SaveId(17, owned.Generation, 1)), "call-up uses ordinary fenced recovery cover");
            Check(!registry.HasRemoteUse(17), "old-generation presence is not inherited by a recovery");
        }
        finally { Directory.Delete(directory, true); }

        PartitionInfo SimAt(ulong id, double x, int pcu, long load) => new(id, 1, true, new PartitionSphere(x, 0, 0, 100), 0, [], pcu, 400L * pcu + 10000, load);
        var busy = PaceSimNode("busy", SimAt(1, 0, 10000, 60000), SimAt(2, 10000, 10000, 40000));
        var idle = PaceSimNode("idle", SimAt(3, 100000, 100, 100));
        var control = DirectGateway.ChooseRebalancePlans([busy, idle], new CostModel(), [], _ => false, fullPlan: true);
        Check(control.Length > 0, "positive control: idle remote-free content can balance");
        var pinned = busy with { Report = busy.Report with { Partitions = busy.Report.Partitions.Select(p => p with { RemoteUsers = [81] }).ToArray() } };
        var held = DirectGateway.ChooseRebalancePlans([pinned, idle], new CostModel(), [], _ => false, fullPlan: true);
        Check(!held.Any(p => p.Members.Any(m => m.Id is 1 or 2)), "remote terminal/camera presence pins balance units");
        var legacy = JsonSerializer.Deserialize<PartitionInfo>("{\"id\":1,\"memberCount\":1,\"anchored\":true,\"sphere\":{\"x\":0,\"y\":0,\"z\":0,\"radius\":1},\"maxMemberSpeed\":0,\"clients\":[]}");
        Check(legacy?.RemoteUsers == null, "old node reports remain readable");
        Console.WriteLine("remote-use: heartbeat fencing/clear, offline admission, first-caller reservation, shared recovery, balance pin and legacy reports passed");
    }
}
