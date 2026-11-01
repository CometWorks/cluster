using static ClusterRegistry.DirectGateway;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// The velocity lookahead at a modded top speed (handover config <c>lookahead.maxSpeed</c>, CostModel.MaxGridSpeed).
    /// A closing pair has to link while it still has the relocation lead (5 s for these small fixtures) before it is
    /// closer than D: it links at a raw gap of D + m + closing x lead. With the vanilla cap of 100 m/s per member and
    /// 200 m/s closing, two grids head-on at 500 or 720 m/s (Relative Top Speed at 360) link at D + m only, 4 s and
    /// 2.8 s before D. With the cap raised to 360 they get their full lead, and receding pairs still get none (T-0141).
    /// </summary>
    private static void TestLookaheadMaxSpeed()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("lookahead-max-speed: " + what); }
        const double envelope = 4000;   // D 3000 + m 1000
        static PartitionInfo Ship(ulong id, double x, double vx, double speed) =>
            new(id, 1, false, new PartitionSphere(x, 0, 0, 100), speed, [], 1000, 410_000,
                MemberSpheres: [[x, 0, 0, 100, speed, vx, 0, 0]]);
        static PartitionReport Report(PartitionInfo partition) => new(3000, 1000, [partition], 0, 50);
        static bool Linked(PartitionInfo a, PartitionInfo b, CostModel model) =>
            ChooseMove(Report(a), Report(b), envelope, [], _ => false, model) is not null;
        // A pair head-on at `speed` each, `gap` m apart surface to surface (member radius 100).
        static (PartitionInfo, PartitionInfo) HeadOn(double gap, double speed) =>
            (Ship(1, 0, speed, speed), Ship(2, gap + 200, -speed, speed));
        CostModel vanilla = new CostModel();
        CostModel fast = vanilla with { MaxGridSpeed = 360 };
        Check(vanilla.MaxGridSpeed == 100, "the default cap is today's 100 m/s");

        // Two large grids at 250 m/s cruise, 500 m/s closing: D + m + 2500 m.
        var (a, b) = HeadOn(6000, 250);
        Check(!Linked(a, b, vanilla), "500 m/s at 6000 m: the vanilla cap links only at 5000 m, 4 s before D");
        Check(Linked(a, b, fast), "500 m/s at 6000 m links with the cap at 360 (limit 6500 m, 7 s before D)");
        (a, b) = HeadOn(6600, 250);
        Check(!Linked(a, b, fast), "500 m/s at 6600 m is outside the 6500 m it needs");

        // Two small grids at 360 m/s boost, 720 m/s closing: D + m + 3600 m.
        (a, b) = HeadOn(7000, 360);
        Check(!Linked(a, b, vanilla), "720 m/s at 7000 m: the vanilla cap links only at 5000 m, 2.8 s before D");
        Check(Linked(a, b, fast), "720 m/s at 7000 m links with the cap at 360 (limit 7600 m, 6.4 s before D)");
        (a, b) = HeadOn(7700, 360);
        Check(!Linked(a, b, fast), "720 m/s at 7700 m is outside the 7600 m it needs");

        // T-0141: the cap is on CLOSING speed, so the higher cap links no receding or parallel pair. The
        // direction-blind sum would have linked this receding pair out to 4000 + 720 x 5 = 7600 m.
        (a, b) = (Ship(1, 0, -360, 360), Ship(2, 4700, 360, 360));
        Check(!Linked(a, b, fast), "a pair receding at 360 m/s each, 4500 m apart, is not linked");
        PartitionInfo up = new(3, 1, false, new PartitionSphere(0, 0, 0, 100), 360, [], 1000, 410_000,
            MemberSpheres: [[0, 0, 0, 100, 360, 0, 360, 0]]);
        PartitionInfo alongside = new(4, 1, false, new PartitionSphere(4700, 0, 0, 100), 360, [], 1000, 410_000,
            MemberSpheres: [[4700, 0, 0, 100, 360, 0, 360, 0]]);
        Check(!Linked(up, alongside, fast), "a pair on parallel courses at 360 m/s, 4500 m apart, is not linked");

        // The sphere exclusion stays a lower bound: a ship above the cap closing on a static base reaches up to the
        // 2 x cap pair limit in the member scan, so the bound must not cap each side alone (the old bound took 100 + 0
        // and excluded this pair at 4100 m while the member gap was 3850 m).
        PartitionInfo ship = Ship(5, 0, 150, 150);
        PartitionInfo station = Ship(6, 4800, 0, 0);
        Check(Linked(ship, station, vanilla), "a ship at 150 m/s, 4600 m from a base, is linked under the vanilla cap");

        // Offline wake: a client-bearing ship boosting at 360 m/s toward an offline station. The wake threshold is
        // offline.wakeMeters (5000), the lead offline.leadSeconds (15) for a station that loads in no time.
        const double lead = 15, threshold = 5000;
        var offline = new OfflinePartition { Partition = 7, X = 9200, Radius = 100, MemberSpheres = [[9200, 0, 0, 100]] };
        PartitionInfo boosting = Ship(8, 0, 360, 360);
        // 9000 m surface to surface: the vanilla 200 m/s cap wakes it only at 8000 m, 13.9 s before D at 360 m/s.
        Check(OfflineGap(boosting, offline, lead, threshold, 100) >= threshold, "the vanilla cap does not wake it at 9000 m");
        double gap = OfflineGap(boosting, offline, lead, threshold, 360);
        Check(gap < threshold && Math.Abs(gap - (9000 - 360 * lead)) < 1e-6,
            $"the cap at 360 wakes it at 9000 m (limit 10400 m, 20.6 s before D), gap {gap:F0}");
        PartitionInfo leaving = Ship(9, 0, -360, 360);
        Check(OfflineGap(leaving, offline, lead, threshold, 360) >= threshold, "a ship flying away at 360 m/s wakes nothing");
    }
}
