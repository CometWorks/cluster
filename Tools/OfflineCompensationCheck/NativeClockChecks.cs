using Concealment.Accounting;
using System.Text.Json;

// Actual host capture -> saved raw elapsed ticks -> same production DLL, with no
// discriminator replacement or inverse-normalized horizon. Native stocks advance
// independently; the fixture's stored modeled result is not the oracle.
internal static class NativeClockChecks
{
    internal static void Run(string path, string output)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var rows = new List<object>();
        bool passed = document.RootElement.GetProperty("complete").GetBoolean();
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var initial = row.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
            bool local = initial.Schema == OfflineProfile.ClusterSchema && initial.ClockDomain == "ClusterLocal";
            long frames = row.GetProperty("nativeFrames").GetInt64(), sessionTicks = row.GetProperty("elapsedTicks").GetInt64();
            // Cluster schema3 names a native-local callback, separate from the
            // replicated display/sun session. Measure its ACTUAL raw readings;
            // never replace the duration by the expected native update count.
            long ticks = local ? row.GetProperty("hostElapsedTicks").GetInt64() : sessionTicks;
            long units = local
                ? checked((long)Math.Floor(row.GetProperty("hostEndMilliseconds").GetDouble() / 16)) - checked((long)Math.Floor(row.GetProperty("hostStartMilliseconds").GetDouble() / 16))
                : row.TryGetProperty("elapsedStartTicks", out var startTicks)
                    ? row.GetProperty("elapsedEndTicks").GetInt64() / 160000 - startTicks.GetInt64() / 160000 : ticks / 160000;
            var actual = row.GetProperty("actual").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
            var model = row.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
            var result = OfflineModel.Simulate(model, initial.SimulatedThrough + units, 72L * 225000);
            long clockDrift = units - frames;
            bool clock = ticks == frames * 160000 && clockDrift == 0 && units == row.GetProperty("hostUnits").GetInt64()
                && (initial.Schema == 2 || local) && initial.ClockFrameSeconds == 1d / 60 && result.Frames == units;
            Dictionary<string, decimal> Items(OfflineProfile p) => p.Inventories.SelectMany(i => i.Stacks).GroupBy(s => s.Item).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));
            var before = Items(initial); var nativeItems = Items(actual); var modeledItems = Items(model);
            var items = before.Keys.Union(nativeItems.Keys).Union(modeledItems.Keys).Select(item =>
            {
                decimal nativeDebit = before.GetValueOrDefault(item) - nativeItems.GetValueOrDefault(item);
                decimal modeledDebit = before.GetValueOrDefault(item) - modeledItems.GetValueOrDefault(item);
                decimal tolerance;
                if (item.EndsWith("/Uranium")) tolerance = Math.Max(.000001m, Math.Abs(nativeDebit) * .01m);
                else if (item.EndsWith("/Ice"))
                {
                    double Pending(JsonElement state) => state.GetProperty("environment").GetProperty("generators").EnumerateArray().Sum(g => g.GetProperty("pendingIce").GetDouble());
                    nativeDebit += (decimal)(Pending(row.GetProperty("native")) - Pending(row.GetProperty("initialNative")));
                    // Engine-fill changes use the retained native gas reference's
                    // Update100 + 60-frame bound; Factory cycles have an 8-frame bound.
                    int phaseBound = initial.Storage.Any(s => s.Kind == "Engine") ? 160 : 8;
                    tolerance = .001m + (decimal)initial.Sources.Where(s => s.Kind == "Generator").Sum(s => s.Maximum * s.Fuel.Where(m => m.Item == item).Sum(m => (double)m.Amount) * phaseBound);
                }
                else tolerance = Math.Max(.0001m, initial.Production.Sum(p => p.Recipes.Sum(r => (decimal)r.UnitsPerFrame * p.TickFrames * (p.Refinery ? 3 : 1)
                    * r.Inputs.Concat(r.Outputs).Where(m => m.Item == item).Sum(m => m.Amount))));
                return new { item, nativeDebit, modeledDebit, tolerance, passed = Math.Abs(nativeDebit - modeledDebit) <= tolerance };
            }).ToArray();
            var storage = model.Storage.Select(s =>
            {
                var a = actual.Storage.Single(a => a.Block == s.Block && a.Kind == s.Kind);
                double bound = s.Kind is "Tank" or "Engine" ? s.MaxInput * s.InputConversion * 160 + 1
                    : Math.Max(s.MaxInput, initial.Sources.Where(p => p.Storage >= 0 && initial.Storage[p.Storage].Block == s.Block).Sum(p => p.Maximum)) * 100 / 216000d
                        + (frames / 100d + 2) * Math.Abs(MathF.BitIncrement((float)a.Amount) - (float)a.Amount);
                return new { s.Block, s.Kind, native = a.Amount, modeled = s.Amount, bound, passed = Math.Abs(s.Amount - a.Amount) <= bound };
            }).ToArray();
            var zones = model.Upkeep.Select(z =>
            {
                var native = actual.Upkeep.SingleOrDefault(a => a.Block == z.Block);
                // Native capture omits exhausted/deleted bubbles. Read the live
                // component's serialized credit/entity id as the negative control.
                var state = row.GetProperty("native").GetProperty("environment").GetProperty("zones").EnumerateArray().Single(s => s.GetProperty("block").GetInt64() == z.Block);
                double nativeCredit = native?.RemainingFrames ?? state.GetProperty("creditMs").GetDouble() * 60 / 1000;
                bool nativeExists = native?.Exists ?? state.GetProperty("zone").GetInt64() != 0;
                bool ok = Math.Abs(z.RemainingFrames - nativeCredit) <= 100 && z.Exists == nativeExists;
                return new { z.Block, modeledCredit = z.RemainingFrames, nativeCredit, z.Exists, nativeExists, passed = ok };
            }).ToArray();
            bool ok = clock && items.All(i => i.passed) && storage.All(s => s.passed) && zones.All(z => z.passed);
            passed &= ok;
            rows.Add(new { name = row.GetProperty("name").GetString(), frames, ticks, sessionTicks, initial.ClockDomain, units, clockDrift, clock, items, storage, zones, passed = ok });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { source = Path.GetFullPath(path), rows, passed,
            method = "actual raw host clock in its explicit schema domain; replicated session elapsed retained separately; unchanged native production/fuel/pending-ice/gas/Update100 bounds" },
            new JsonSerializerOptions(OfflineProfile.Json) { WriteIndented = true }));
        Console.WriteLine($"Raw-host native prefix differential: {rows.Count} cases, passed={passed}, {output}");
        if (!passed) throw new InvalidOperationException("Raw-host native differential failed; see retained comparisons");
    }
}
