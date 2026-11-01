using ClusterPlugin;
using System.Security.Cryptography;
using System.Text.Json;

internal static class NativeSolarShadowCheck
{
    internal static void Run(string directory, string output)
    {
        using var initial = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "initial.json")));
        using var live = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "solar-samples.json")));
        var profiles = initial.RootElement.GetProperty("profiles").Deserialize<OfflineProfile[]>(OfflineProfile.Json);
        var names = new Dictionary<long, (string Name, bool Panel)>();
        foreach (var grid in live.RootElement[0].GetProperty("grids").EnumerateArray())
        foreach (string kind in new[] { "panels", "farms" })
        foreach (var port in grid.GetProperty(kind).EnumerateArray())
            names[port.GetProperty("block").GetInt64()] = (grid.GetProperty("name").GetString(), kind == "panels");
        double[] open = profiles.SelectMany(p => p.Sources).Single(s => names.TryGetValue(s.Block, out var n) && n == ("planet-open", true)).Curve;
        var rows = new List<object>();
        int mixed = 0, zero = 0, clear = 0, farm = 0;
        foreach (var source in profiles.SelectMany(p => p.Sources).Where(s => s.Curve is { Length: > 0 }))
        {
            var name = names[source.Block];
            var extra = new double[source.Curve.Length];
            for (int bin = 0; bin < source.Curve.Length; bin++)
            {
                double raw = source.Curve[bin], envelope = open[bin];
                // All four retained fixtures have the same panel orientation. The
                // unobstructed panel is their exact cosine envelope. Recover the
                // discrete lit count, and refuse data that cannot support it.
                int lit = !name.Panel || envelope == 0 ? 0 : (int)Math.Round(raw / envelope * 12);
                if (name.Panel && (lit < 0 || lit > 12 || Math.Abs(raw - envelope * lit / 12) > 2e-6))
                    throw new Exception("Retained geometry cannot identify pivot visibility");
                if (name.Panel) extra[bin] = OfflineSunCurve.ShadowExtra(raw, lit, 12);
                if (!name.Panel) farm++;
                else if (raw == 0) zero++;
                else if (lit == 12) clear++;
                else mixed++;
                if (extra[bin] < 0 || extra[bin] > raw * .05 || raw + extra[bin] > 1
                    || (raw == 0 || lit == 12 || !name.Panel) && extra[bin] != 0)
                    throw new Exception("Shadow allowance bound/control failed");
                rows.Add(new { grid = name.Name, panel = name.Panel, source.Block, bin, raw, lit, pivots = name.Panel ? 12 : 8,
                    extra = extra[bin], allowed = raw + extra[bin] });
            }
            if (name.Panel) source.ShadowAllowance = extra;
        }
        if (mixed == 0 || zero == 0 || clear == 0 || farm == 0) throw new Exception("Missing retained positive/negative controls");
        var budgets = new List<object>();
        foreach (var profile in profiles)
        {
            var rawProfile = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(profile, OfflineProfile.Json), OfflineProfile.Json);
            foreach (var source in rawProfile.Sources) source.ShadowAllowance = null;
            long duration = (long)Math.Ceiling(profile.SunPeriodFrames);
            var raw = OfflineModel.Simulate(rawProfile, rawProfile.SimulatedThrough + duration, duration);
            var allowed = OfflineModel.Simulate(profile, profile.SimulatedThrough + duration, duration);
            if (Math.Abs(allowed.ProducerEnergyMWh.Values.Sum() - allowed.ConsumerEnergyMWh.Values.Sum()
                - allowed.StorageInputEnergyMWh.Values.Sum()) > 1e-8) throw new Exception("Shared electrical budget failed");
            budgets.Add(new { profile.Group, duration, raw, allowed });
        }
        var hashes = new[] { "initial.json", "solar-samples.json", "solar-native-points.json", "solar-geometry.json", "result.json" }
            .ToDictionary(n => n, n => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, n)))).ToLowerInvariant());
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, historicalNativeReplayNotFreshRun = true,
            directory = Path.GetFullPath(directory), hashes, criterion = "0 < lit < pivots in the same ray pass; panels only",
            formula = "extra = min(0.05 * raw, raw * (pivots - lit) / lit); zero otherwise",
            rawGeometryCriterion = .02, allowanceMaximumFraction = .05, mixedBins = mixed, darkOrBlockedBins = zero,
            clearBins = clear, unchangedFarmBins = farm, rows, budgets }, new JsonSerializerOptions(OfflineProfile.Json) { WriteIndented = true }));
        Console.WriteLine($"Retained native solar: {rows.Count} bins, {mixed} mixed, {zero} dark/blocked, {clear} clear, {farm} farm; bounds and shared budgets pass.");
    }
}
