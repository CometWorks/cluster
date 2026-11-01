using System.Text.Json;
using ClusterPlugin;

internal static class NativeObservedCheck
{
    internal static void Run(string directory, string output)
    {
        using var final = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "result.json")));
        var rows = final.RootElement.GetProperty("results").EnumerateArray().ToArray();
        var varying = rows.Single(r => r.GetProperty("native").GetProperty("name").GetString() == "observed-sun-varying");
        var fuel = rows.Single(r => r.GetProperty("native").GetProperty("name").GetString() == "observed-fuel-policy");
        OfflineProfile Profile(JsonElement r, string key) => r.GetProperty(key).Deserialize<OfflineProfile>(OfflineProfile.Json)!;
        var v = Profile(varying, "initial"); var va = Profile(varying, "actual"); var vm = Profile(varying, "modeled");
        var observation = v.Observations.Single();
        double energyError = va.Storage.Sum(s => s.Amount) - vm.Storage.Sum(s => s.Amount);
        double varianceBoundMWh = Math.Sqrt(observation.Variance.Max()) * 450000 / 216000d;
        bool fullRotation = final.RootElement.GetProperty("nativeFrames").GetInt64() == 450000
            && final.RootElement.GetProperty("elapsedTicks").GetInt64() == 72000000000 && observation.Samples >= 4500;
        bool scheduledInPlace = final.RootElement.TryGetProperty("scheduledInPlace", out var scheduled) && scheduled.GetBoolean()
            && final.RootElement.GetProperty("observationFrames").GetInt64() >= 450000;
        bool varyingCaptured = v.Confidence == "Approximate" && observation.Variance.Any(x => x > .0001)
            && observation.Maximum.Max() - observation.Minimum.Min() > .05 && v.Sources.Any(s => s.Kind == "Observed");
        bool observedWithinVariance = Math.Abs(energyError) < varianceBoundMWh && Math.Abs(energyError) < .001;
        var fi = Profile(fuel, "initial"); var fa = Profile(fuel, "actual"); var fm = Profile(fuel, "modeled");
        decimal Fuel(OfflineProfile p) => p.Inventories.SelectMany(i => i.Stacks).Where(s => s.Item.EndsWith("/Uranium")).Sum(s => s.Amount);
        decimal nativeDebit = Fuel(fi) - Fuel(fa), modelDebit = Fuel(fi) - Fuel(fm);
        bool knownNativeFuel = fi.Confidence == "Approximate" && fi.Observations.Single().Samples >= 4500
            && fi.Unknowns.Count == 0 && nativeDebit > .1m && modelDebit > .1m;
        decimal fuelTolerance = Math.Max(.000001m, nativeDebit * .01m);
        bool fuelScriptDifferentialPassed = knownNativeFuel && Math.Abs(nativeDebit - modelDebit) <= fuelTolerance;
        var opaque = rows.SingleOrDefault(r => r.GetProperty("native").GetProperty("name").GetString() == "observed-inventory-policy");
        bool conservativePolicy = opaque.ValueKind != JsonValueKind.Undefined && Profile(opaque, "initial") is {} oi
            && oi.Confidence == "Frozen" && oi.Unknowns.Any(u => u.Contains("inventory changes"))
            && Fuel(oi) == Fuel(Profile(opaque, "modeled"))
            && Profile(opaque, "actual").Inventories.SelectMany(i => i.Stacks).Where(s => s.Item.EndsWith("/Iron")).Sum(s => s.Amount)
                > oi.Inventories.SelectMany(i => i.Stacks).Where(s => s.Item.EndsWith("/Iron")).Sum(s => s.Amount);
        bool passed = fullRotation && scheduledInPlace && varyingCaptured && observedWithinVariance && fuelScriptDifferentialPassed && conservativePolicy;
        File.WriteAllText(output, JsonSerializer.Serialize(new { directory, fullRotation, scheduledInPlace, varyingCaptured, observedWithinVariance,
            energyErrorMWh = energyError, varianceBoundMWh, observation.Samples, observation.Mean, observation.Variance,
            conservativePolicy, knownNativeFuel, nativeFuelDebit = nativeDebit, modeledFuelDebit = modelDebit, fuelTolerance, fuelScriptDifferentialPassed, passed,
            applicableObservedPassed = fullRotation && varyingCaptured && observedWithinVariance,
            acceptanceComplete = false,
            limitation = "ordinary Prepare InPlace admission across a full learning rotation then an independent full native reference; no initialized cluster scheduler or real-world observed census" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Observed rotation: varying={fullRotation && varyingCaptured && observedWithinVariance}, conservative fuel policy={conservativePolicy}, fuel fidelity={fuelScriptDifferentialPassed}");
        if (!passed) throw new InvalidOperationException("Observed native policy/reference failed");
    }
}
