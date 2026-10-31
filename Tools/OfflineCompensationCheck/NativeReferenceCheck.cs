using System.Security.Cryptography;
using System.Text.Json;
using ClusterPlugin;

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
            var predicted = row.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
            var actual = row.GetProperty("actual").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
            long units = checked((long)Math.Round(frames / (predicted.ClockFrameSeconds * 60)));
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
            bool casePassed = stocks.All(s => s.passed) && fuelPassed && zones.All(z => z.passed);
            passed &= casePassed;
            rows.Add(new { name = row.GetProperty("native").GetProperty("name").GetString(), stocks, fuelActual, fuelPredicted, fuelTolerance, fuelPassed, zones,
                native = row.GetProperty("native").Clone(), result, predicted, passed = casePassed });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { reference = Path.GetFullPath(reference), referenceSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            nativeFrames = frames, method = "current detached model versus independently advanced native reference; one percent of actual stock, zone100-frame timing bound", rows, passed },
            new JsonSerializerOptions(OfflineProfile.Json) { WriteIndented = true }));
        Console.WriteLine($"native reference comparison: {rows.Count} cases, passed={passed}, {output}");
        if (!passed) throw new InvalidOperationException("Native reference differential outside tolerance; see retained output");
    }
}
