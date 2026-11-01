using System.Text.Json;
using ClusterPlugin;

internal static class NativeObservedCheck
{
    internal static void RunShort(string reference, string output)
    {
        using var final = JsonDocument.Parse(File.ReadAllBytes(reference));
        var rows = final.RootElement.GetProperty("results").EnumerateArray().ToArray();
        OfflineProfile Profile(JsonElement r, string key) => r.GetProperty(key).Deserialize<OfflineProfile>(OfflineProfile.Json)!;
        decimal Fuel(OfflineProfile p) => p.Inventories.SelectMany(i => i.Stacks).Where(s => s.Item.EndsWith("/Uranium")).Sum(s => s.Amount);
        var row = rows.Single(r => r.GetProperty("native").GetProperty("name").GetString() == "observed-fuel-policy");
        var initial = Profile(row, "initial");
        decimal nativeDebit = Fuel(initial) - Fuel(Profile(row, "actual")), modelDebit = Fuel(initial) - Fuel(Profile(row, "modeled"));
        var opaque = rows.Single(r => r.GetProperty("native").GetProperty("name").GetString() == "observed-inventory-policy");
        bool knownFuel = initial.Confidence == "Approximate" && initial.Unknowns.Count == 0 && initial.Observations.Count == 1;
        bool frozenEdit = Profile(opaque, "initial") is {} unknown && unknown.Confidence == "Frozen"
            && unknown.Unknowns.Any(u => u.Contains("inventory changes")) && Fuel(unknown) == Fuel(Profile(opaque, "modeled"));
        bool passed = final.RootElement.GetProperty("nativeFrames").GetInt64() == 3600
            && final.RootElement.GetProperty("observationFrames").GetInt64() >= 7200 && knownFuel && frozenEdit
            && nativeDebit > 0 && modelDebit > 0 && Math.Abs(nativeDebit - modelDebit) <= .00001m;
        File.WriteAllText(output, JsonSerializer.Serialize(new { reference, knownFuel, frozenEdit, nativeDebit, modelDebit, passed,
            limitation = "short native eligibility/stock regression; no full-rotation acceptance" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native observed short: passed={passed}, native/model fuel={nativeDebit}/{modelDebit}");
        if (!passed) throw new InvalidOperationException("Native observed short regression failed");
    }

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
        var fuelResult = fuel.GetProperty("result").Deserialize<OfflineResult>(OfflineProfile.Json)!;
        bool commonBudget = Math.Abs(fuelResult.ProducerEnergyMWh.Values.Sum() - fuelResult.ConsumerEnergyMWh.Values.Sum()
            - fuelResult.StorageInputEnergyMWh.Values.Sum()) < 1e-9
            && fuelResult.ProducerFuelConsumed.Values.Sum(v => v.Where(p => p.Key.EndsWith("/Uranium")).Sum(p => p.Value)) == modelDebit;
        var opaque = rows.SingleOrDefault(r => r.GetProperty("native").GetProperty("name").GetString() == "observed-inventory-policy");
        decimal Iron(OfflineProfile p) => p.Inventories.SelectMany(i => i.Stacks).Where(s => s.Item.EndsWith("/Iron")).Sum(s => s.Amount);
        bool conservativePolicy = opaque.ValueKind != JsonValueKind.Undefined && Profile(opaque, "initial") is {} oi
            && oi.Confidence == "Frozen" && oi.Unknowns.Any(u => u.Contains("inventory changes"))
            && Fuel(oi) == Fuel(Profile(opaque, "modeled"))
            // Script iron creation during learning proves the unexpected edit.
            // Its bounded native inventory may be full before the reference;
            // ordinary fuel still burns there while Frozen preserves model stock.
            && Iron(oi) > 0 && Iron(Profile(opaque, "actual")) >= Iron(oi)
            && Fuel(Profile(opaque, "actual")) < Fuel(oi);
        bool passed = fullRotation && scheduledInPlace && varyingCaptured && observedWithinVariance && fuelScriptDifferentialPassed && commonBudget && conservativePolicy;
        File.WriteAllText(output, JsonSerializer.Serialize(new { directory, fullRotation, scheduledInPlace, varyingCaptured, observedWithinVariance,
            energyErrorMWh = energyError, varianceBoundMWh, observation.Samples, observation.Mean, observation.Variance,
            conservativePolicy, knownNativeFuel, nativeFuelDebit = nativeDebit, modeledFuelDebit = modelDebit, fuelTolerance, fuelScriptDifferentialPassed, commonBudget, passed,
            applicableObservedPassed = fullRotation && varyingCaptured && observedWithinVariance,
            acceptanceComplete = false,
            limitation = "ordinary Prepare InPlace admission across a full learning rotation then an independent full native reference; no initialized cluster scheduler or real-world observed census" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Observed rotation: varying={fullRotation && varyingCaptured && observedWithinVariance}, conservative fuel policy={conservativePolicy}, fuel fidelity={fuelScriptDifferentialPassed}");
        if (!passed) throw new InvalidOperationException("Observed native policy/reference failed");
    }
}
