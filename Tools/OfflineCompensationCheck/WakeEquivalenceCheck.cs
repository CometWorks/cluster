using System.Diagnostics;
using System.Text.Json;
using Concealment.Accounting;

/// <summary>SE-1096: a wake's offline compensation moved off the game thread. This recomputes what a cluster wake
/// logged ("[ClusterPlugin] offline compensation {json}") with the model this tool is built with, at the same wake
/// frame, from the profiles of the Final the node read, and requires the same JSON. Built at an older Concealment pin,
/// it shows that the moved wake still gives that pin's results. With --tolerance it requires the SE-1096 tolerance
/// instead: a model that steps differently rounds differently in the last digits.</summary>
internal static class WakeEquivalenceCheck
{
    // profiles: JSON array of the Finals' profiles; sun: "<from>-<through>.json" WA sun/work paths
    // (/registry/offline-accounting-sun); logged: one logged result JSON per line, byte-exact from the node log.
    internal static int Run(string profiles, string sun, string logged, long capFrames, bool tolerance = false)
    {
        var all = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllBytes(profiles))!;
        int same = 0, within = 0, differ = 0;
        foreach (string line in File.ReadAllLines(logged).Where(l => l.Length > 0))
        {
            var expected = JsonSerializer.Deserialize<OfflineResult>(line, OfflineProfile.Json)!;
            var profile = all.Select(e => JsonSerializer.Deserialize<OfflineProfile>(e.GetRawText(), OfflineProfile.Json)!)
                .Single(p => p.Group == expected.Group && p.SimulatedThrough == expected.FromFrame);
            long through = Math.Min(expected.CompensatedTo, profile.SimulatedThrough + capFrames);
            if (profile.SunPeriodFrames > 0 && profile.Sources.Any(s => s.Curve is { Length: > 1 }) && through > profile.SimulatedThrough)
                profile.SunPath = SunWorkPath.Read(File.ReadAllBytes(Path.Combine(sun, profile.SimulatedThrough + "-" + through + ".json")),
                    profile.ClockEpoch, profile.SimulatedThrough, through);
            var watch = Stopwatch.StartNew();
            var result = OfflineModel.Simulate(profile, expected.CompensatedTo, capFrames);
            watch.Stop();
            result.WakeClockGeneration = expected.WakeClockGeneration;
            string actual = JsonSerializer.Serialize(result, OfflineProfile.Json);
            string verdict;
            List<string> off = null;
            if (actual == line) { same++; verdict = "SAME"; }
            else if (tolerance && (off = Deviations(expected, result)).Count == 0) { within++; verdict = "WITHIN"; }
            else { differ++; verdict = "DIFFER"; }
            Console.WriteLine($"{verdict} group={expected.Group} frames={result.Frames} events={expected.Events}->{result.Events} seconds={watch.Elapsed.TotalSeconds:F3}");
            if (verdict == "DIFFER")
                Console.WriteLine(off != null ? "  " + string.Join("; ", off.Take(8)) : "  logged:   " + line + "\n  computed: " + actual);
        }
        Console.WriteLine($"Wake equivalence: {same} same, {within} within tolerance, {differ} different");
        return differ == 0 && same + within > 0 ? 0 : 1;
    }

    /// <summary>The SE-1096 tolerance. Exact: Skipped, Confidence, Capped, Frames, CompensatedTo, FromFrame, the zones'
    /// flags and DisabledZones. Within 1e-6: items, production units, upkeep, fuel and zone frames (Commit floors stacks to
    /// 6 decimals). Within 1e-6 relative, or 1e-9 MWh: EnergyDelta and every *EnergyMWh.</summary>
    internal static List<string> Deviations(OfflineResult expected, OfflineResult actual)
    {
        var off = new List<string>();
        void Exact<T>(string name, T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) off.Add($"{name} {a} != {b}"); }
        void Absolute<TKey>(string name, IReadOnlyDictionary<TKey, decimal> a, IReadOnlyDictionary<TKey, decimal> b)
        {
            foreach (var key in a.Keys.Union(b.Keys))
            {
                decimal x = a.GetValueOrDefault(key), y = b.GetValueOrDefault(key);
                if (Math.Abs(x - y) > 0.000001m) off.Add($"{name}[{key}] {x} vs {y}");
            }
        }
        void Relative<TKey>(string name, IReadOnlyDictionary<TKey, double> a, IReadOnlyDictionary<TKey, double> b)
        {
            foreach (var key in a.Keys.Union(b.Keys))
            {
                double x = a.GetValueOrDefault(key), y = b.GetValueOrDefault(key), deviation = Math.Abs(x - y);
                if (deviation > 1e-9 && deviation > 1e-6 * Math.Max(Math.Abs(x), Math.Abs(y))) off.Add($"{name}[{key}] {x:R} vs {y:R}");
            }
        }
        Exact("Skipped", expected.Skipped, actual.Skipped); Exact("Confidence", expected.Confidence, actual.Confidence);
        Exact("Capped", expected.Capped, actual.Capped); Exact("Frames", expected.Frames, actual.Frames);
        Exact("CompensatedTo", expected.CompensatedTo, actual.CompensatedTo); Exact("FromFrame", expected.FromFrame, actual.FromFrame);
        Exact("DisabledZones", string.Join(",", expected.DisabledZones), string.Join(",", actual.DisabledZones));
        Exact("Zones", expected.Zones.Count, actual.Zones.Count);
        foreach (var (a, b) in expected.Zones.Zip(actual.Zones))
        {
            Exact($"Zone {a.Block}", (a.Block, a.Exists, a.Activating, a.Exhausted), (b.Block, b.Exists, b.Activating, b.Exhausted));
            Absolute($"Zone {a.Block}", new Dictionary<string, decimal> { ["Remaining"] = (decimal)a.RemainingFrames, ["Activation"] = (decimal)a.ActivationRemainingFrames },
                new Dictionary<string, decimal> { ["Remaining"] = (decimal)b.RemainingFrames, ["Activation"] = (decimal)b.ActivationRemainingFrames });
        }
        Absolute("ItemDelta", expected.ItemDelta, actual.ItemDelta);
        Absolute("ProductionUnits", expected.ProductionUnits.ToDictionary(x => x.Key, x => (decimal)x.Value), actual.ProductionUnits.ToDictionary(x => x.Key, x => (decimal)x.Value));
        Absolute("UpkeepConsumed", expected.UpkeepConsumed, actual.UpkeepConsumed);
        static Dictionary<string, decimal> Fuel(OfflineResult r) => r.ProducerFuelConsumed.SelectMany(b => b.Value.Select(i => (Key: b.Key + "/" + i.Key, i.Value))).ToDictionary(x => x.Key, x => x.Value);
        Absolute("Fuel", Fuel(expected), Fuel(actual));
        Relative("EnergyDelta", new Dictionary<string, double> { [""] = expected.EnergyDelta }, new Dictionary<string, double> { [""] = actual.EnergyDelta });
        Relative("ProducerEnergyMWh", expected.ProducerEnergyMWh, actual.ProducerEnergyMWh);
        Relative("ConsumerEnergyMWh", expected.ConsumerEnergyMWh, actual.ConsumerEnergyMWh);
        Relative("StorageInputEnergyMWh", expected.StorageInputEnergyMWh, actual.StorageInputEnergyMWh);
        Relative("SolarShadowAllowanceMWh", expected.SolarShadowAllowanceMWh, actual.SolarShadowAllowanceMWh);
        return off;
    }
}
