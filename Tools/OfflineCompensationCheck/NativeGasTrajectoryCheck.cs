using System.Security.Cryptography;
using System.Text.Json;
using ClusterPlugin;

internal static class NativeGasTrajectoryCheck
{
    internal static void Run(string directory, string output)
    {
        using var final = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "result.json")));
        using var initial = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "initial.json")));
        using var trajectory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "trajectory.json")));
        var rows = new List<object>();
        bool passed = true;
        int index = 0;
        foreach (var row in final.RootElement.GetProperty("results").EnumerateArray())
        {
            var profile = row.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
            long horizon = row.GetProperty("nativeFrames").GetInt64();
            long offset = initial.RootElement.GetProperty("frame").GetInt64() - initial.RootElement.GetProperty("capturedAt")[index].GetInt64();
            var samples = trajectory.RootElement.EnumerateArray().Select(s =>
                (Frame: s.GetProperty("nativeFrames").GetInt64() + offset, State: s.GetProperty("cases")[index].Clone())).ToArray();
            string name = row.GetProperty("native").GetProperty("name").GetString()!;
            OfflineProfile At(long frames)
            {
                var p = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(profile, OfflineProfile.Json), OfflineProfile.Json)!;
                long units = (long)Math.Round(frames / (p.ClockFrameSeconds * 60));
                OfflineModel.Simulate(p, p.SimulatedThrough + units, long.MaxValue);
                return p;
            }
            double Stock(OfflineProfile p, string kind) => kind == "Ice"
                ? (double)p.Inventories.SelectMany(i => i.Stacks).Where(s => s.Item.EndsWith("/Ice")).Sum(s => s.Amount)
                : p.Storage.Single(s => s.Kind == kind).Amount;
            double Native(JsonElement state, string kind) => kind switch
            {
                "Ice" => state.GetProperty("generators")[0].GetProperty("ice").GetDouble(),
                "Engine" => state.GetProperty("engines")[0].GetProperty("gas").GetDouble(),
                _ => state.GetProperty("tanks")[0].GetProperty("gas").GetDouble()
            };
            var transitions = new List<object>();
            var nativeTimes = new Dictionary<string, long>();
            var modelTimes = new Dictionary<string, long>();
            bool casePassed = true;
            foreach (string kind in new[] { "Tank", "Ice", "Engine" })
            {
                var peak = samples.MaxBy(s => Native(s.State, kind));
                if (Native(peak.State, kind) <= .001) continue;
                var empty = samples.FirstOrDefault(s => s.Frame >= peak.Frame && Native(s.State, kind) <= .001);
                if (empty.State.ValueKind == JsonValueKind.Undefined)
                {
                    bool retained = Stock(At(horizon), kind) > .001;
                    casePassed &= retained;
                    transitions.Add(new { kind, retained, nativeEmpty = (long?)null });
                    continue;
                }
                long low = peak.Frame, high = horizon;
                bool positive = Stock(At(low), kind) > .001, exhausted = Stock(At(high), kind) <= .001;
                if (positive && exhausted)
                    while (low + 1 < high)
                    {
                        long middle = low + (high - low) / 2;
                        if (Stock(At(middle), kind) <= .001) high = middle; else low = middle;
                    }
                // Native ice settlement is Update100, and the recorded trajectory
                // samples every60 frames. This bound follows those two clocks.
                bool close = positive && exhausted && Math.Abs(high - empty.Frame) <= 160;
                nativeTimes[kind] = empty.Frame; modelTimes[kind] = high;
                casePassed &= close;
                transitions.Add(new { kind, nativeEmpty = empty.Frame, modeledEmpty = high, positive, exhausted, toleranceFrames = 160, passed = close });
            }
            var nativeOrder = nativeTimes.OrderBy(p => p.Value).Select(p => p.Key).ToArray();
            var modeledOrder = modelTimes.OrderBy(p => p.Value).Select(p => p.Key).ToArray();
            bool ordering = nativeOrder.SequenceEqual(modeledOrder) && (name != "gas-seeded-exhaustion"
                || nativeOrder.Length == 3 && nativeOrder.SequenceEqual(new[] { "Ice", "Tank", "Engine" }));
            bool coldNegative = name != "gas-cold-negative" || samples.All(s => Native(s.State, "Tank") > 190000
                && Native(s.State, "Engine") == 0 && Native(s.State, "Ice") == 500 && s.State.GetProperty("engines")[0].GetProperty("output").GetDouble() == 0);
            bool bootstrap = name != "gas-battery-bootstrap" || profile.Storage.Single(s => s.Kind == "Engine").Amount == 0
                && profile.Storage.Single(s => s.Kind == "Battery").Amount > 0 && samples.Any(s => Native(s.State, "Engine") > 1)
                && Stock(At(samples.MaxBy(s => Native(s.State, "Engine")).Frame), "Engine") > 1;
            bool circuit = name != "gas-isolated-conveyor" || samples.All(s => Native(s.State, "Tank") > 190000 && Native(s.State, "Ice") == 500)
                && Stock(At(horizon), "Tank") > 190000 && Stock(At(horizon), "Ice") == 500;
            // Native float residuals can still feed the0.1W conveyor port after
            // Factory blackouts. Do not call that unsupported generator/tank power.
            double conveyorMW = profile.Sinks.Where(s => s.Group == "Conveyors").Sum(s => s.Required);
            double tailOutputMW = nativeTimes.ContainsKey("Engine") ? samples.Where(s => s.Frame >= nativeTimes["Engine"] + 100)
                .Select(s => s.State.GetProperty("engines")[0].GetProperty("output").GetDouble()).DefaultIfEmpty().Max() : 0;
            bool powerLoss = !nativeTimes.ContainsKey("Engine") || tailOutputMW <= conveyorMW + 1e-11
                && samples.Where(s => s.Frame >= nativeTimes["Engine"] + 100).All(s => !s.State.GetProperty("tanks")[0].GetProperty("powered").GetBoolean());
            casePassed &= ordering && coldNegative && bootstrap && circuit && powerLoss;
            passed &= casePassed;
            rows.Add(new { name, transitions, nativeOrder, modeledOrder, ordering, coldNegative, bootstrap, circuit, powerLoss, tailOutputMW, conveyorMW, passed = casePassed });
            index++;
        }
        var hashes = new[] { "initial.json", "result.json", "trajectory.json" }.ToDictionary(n => n,
            n => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, n)))));
        File.WriteAllText(output, JsonSerializer.Serialize(new { directory, hashes, rows, passed }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native gas trajectory: {rows.Count} cases, passed={passed}");
        if (!passed) throw new InvalidOperationException("Gas trajectory differential failed; see retained details");
    }
}
