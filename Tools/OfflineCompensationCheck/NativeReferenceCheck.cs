using System.Security.Cryptography;
using System.Text.Json;
using Concealment.Accounting;

// A fresh model run against immutable, engine-produced standalone reference data.
// This is a detached comparison, never a claim that the new package ran natively.
internal static class NativeReferenceCheck
{
    internal static void Run(string reference, string output)
    {
        byte[] bytes = File.ReadAllBytes(reference);
        using var document = JsonDocument.Parse(bytes);
        long frames = document.RootElement.GetProperty("nativeFrames").GetInt64();
        var rows = new List<object>();
        bool passed = true;
        foreach (var row in document.RootElement.GetProperty("results").EnumerateArray())
        {
            long caseFrames = row.TryGetProperty("nativeFrames", out var capturedFrames) ? capturedFrames.GetInt64() : frames;
            var predicted = row.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
            var actual = row.GetProperty("actual").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
            long units = checked((long)Math.Round(caseFrames / (predicted.ClockFrameSeconds * 60)));
            var result = OfflineModel.Simulate(predicted, predicted.SimulatedThrough + units, long.MaxValue);
            var stocks = actual.Storage.Select(a =>
            {
                var model = predicted.Storage.Single(m => m.Block == a.Block && m.Kind == a.Kind);
                double tolerance = Math.Max(1e-6, Math.Abs(a.Amount) * .01);
                return new { a.Block, a.Kind, actual = a.Amount, predicted = model.Amount, tolerance, passed = Math.Abs(a.Amount - model.Amount) <= tolerance };
            }).ToArray();
            decimal Fuel(OfflineProfile p) => p.Inventories.SelectMany(i => i.Stacks).Where(s => s.Item == "MyObjectBuilder_Ingot/Uranium").Sum(s => s.Amount);
            decimal fuelActual = Fuel(actual), fuelPredicted = Fuel(predicted), fuelTolerance = Math.Max(.000001m, Math.Abs(fuelActual) * .01m);
            bool fuelPassed = Math.Abs(fuelActual - fuelPredicted) <= fuelTolerance;
            var zones = actual.Upkeep.Select(a =>
            {
                var model = predicted.Upkeep.Single(m => m.Block == a.Block);
                return new { a.Block, actual = a, predicted = model, passed = a.Exists == model.Exists && a.Enabled == model.Enabled
                    && Math.Abs(a.RemainingFrames - model.RemainingFrames) <= 100 };
            }).ToArray();
            Dictionary<string, decimal> Items(OfflineProfile p) => p.Inventories.SelectMany(i => i.Stacks)
                .GroupBy(s => s.Item).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));
            var initial = row.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
            var actualItems = Items(actual); var modelItems = Items(predicted);
            var items = actualItems.Keys.Union(modelItems.Keys).Where(item => item != "MyObjectBuilder_Ingot/Uranium").Select(item =>
            {
                // T-OC-02 allows one production tick per ore switch. Preserve the
                // captured timers/upgrades rather than inventing a percent allowance.
                decimal tolerance = Math.Max(.0001m, initial.Production.Sum(p => p.Recipes.Sum(r =>
                    (decimal)r.UnitsPerFrame * p.TickFrames * (p.Refinery ? 3 : 1)
                    * r.Inputs.Concat(r.Outputs).Where(m => m.Item == item).Sum(m => m.Amount))));
                decimal a = actualItems.GetValueOrDefault(item), m = modelItems.GetValueOrDefault(item);
                return new { item, actual = a, predicted = m, tolerance, passed = Math.Abs(a - m) <= tolerance };
            }).ToArray();
            string name = row.GetProperty("native").GetProperty("name").GetString()!;
            bool zoneControl = true;
            if (name == "standalone-zone-four-hour")
            {
                var nativeZone = row.GetProperty("native").GetProperty("zones").EnumerateArray().Single();
                const string chip = "MyObjectBuilder_Component/ZoneChip";
                zoneControl = frames == 864000 && Items(initial).GetValueOrDefault(chip) == 3
                    && actualItems.GetValueOrDefault(chip) == 0 && modelItems.GetValueOrDefault(chip) == 0
                    && predicted.Upkeep.Count == 1 && !predicted.Upkeep[0].Exists && !predicted.Upkeep[0].Enabled
                    && predicted.Upkeep[0].RemainingFrames == 0 && !nativeZone.GetProperty("exists").GetBoolean()
                    && nativeZone.GetProperty("enabled").GetBoolean();
            }
            bool productionControl = true;
            if (name.StartsWith("production-"))
            {
                var initialItems = Items(initial);
                productionControl = initial.Production.Count(p => p.Refinery) == 2
                    && new[] { "Iron", "Nickel", "Cobalt" }.All(ore => initialItems.GetValueOrDefault("MyObjectBuilder_Ore/" + ore) > 0)
                    && row.GetProperty("native").GetProperty("production").EnumerateArray()
                        .Where(p => initial.Production.Any(e => e.Refinery && e.Block == p.GetProperty("block").GetInt64()))
                        .All(p => p.GetProperty("upgrades").GetProperty("Productivity").GetDouble() > 0);
                if (name.EndsWith("output-full")) productionControl &= result.ProductionUnits.Values.Sum() == 0
                    && actualItems.All(p => p.Key == "MyObjectBuilder_Ingot/Uranium" || Math.Abs(p.Value - initialItems.GetValueOrDefault(p.Key)) <= .0001m);
                else productionControl &= actualItems.GetValueOrDefault("MyObjectBuilder_Component/SteelPlate") > initialItems.GetValueOrDefault("MyObjectBuilder_Component/SteelPlate")
                    && actualItems.GetValueOrDefault("MyObjectBuilder_Component/SteelPlate") == modelItems.GetValueOrDefault("MyObjectBuilder_Component/SteelPlate");
            }
            bool casePassed = stocks.All(s => s.passed) && fuelPassed && zones.All(z => z.passed) && items.All(i => i.passed) && productionControl && zoneControl;
            passed &= casePassed;
            rows.Add(new { name, stocks, fuelActual, fuelPredicted, fuelTolerance, fuelPassed, zones, items, productionControl, zoneControl,
                native = row.GetProperty("native").Clone(), result, predicted, passed = casePassed });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { reference = Path.GetFullPath(reference), referenceSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            nativeFrames = frames, method = "current detached model versus independently advanced native reference; one percent of actual stock, zone100-frame timing bound", rows, passed },
            new JsonSerializerOptions(OfflineProfile.Json) { WriteIndented = true }));
        Console.WriteLine($"native reference comparison: {rows.Count} cases, passed={passed}, {output}");
        if (!passed) throw new InvalidOperationException("Native reference differential outside tolerance; see retained output");
    }
}
