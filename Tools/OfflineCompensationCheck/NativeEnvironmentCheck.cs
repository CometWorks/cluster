using System.Text.Json;
using Concealment.Accounting;

internal static class NativeEnvironmentCheck
{
    internal static void Run(string directory, string output)
    {
        using var final = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "result.json")));
        using var trajectory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "trajectory.json")));
        var row = final.RootElement.GetProperty("results")[0];
        var initial = row.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
        var actual = row.GetProperty("actual").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
        var modeled = row.GetProperty("modeled").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
        var result = row.GetProperty("result").Deserialize<OfflineResult>(OfflineProfile.Json)!;
        var samples = trajectory.RootElement.EnumerateArray().Select(s =>
            (Frame: s.GetProperty("nativeFrames").GetInt64(), Zone: s.GetProperty("cases")[0].GetProperty("zones")[0].Clone())).ToArray();
        bool Active(JsonElement z) => z.GetProperty("active").ValueKind == JsonValueKind.True;
        var nativeZone = row.GetProperty("native").GetProperty("zones")[0];
        decimal Fuel(OfflineProfile p) => p.Inventories.SelectMany(i => i.Stacks).Where(s => s.Item.EndsWith("/Uranium")).Sum(s => s.Amount);
        double supplied = result.ProducerEnergyMWh.Values.Sum(), consumed = result.ConsumerEnergyMWh.Values.Sum() + result.StorageInputEnergyMWh.Values.Sum();
        double sunStep = initial.SunPeriodFrames / initial.Sources.Max(s => s.Curve?.Length ?? 1) * initial.ClockFrameSeconds * 60;
        double creditDifference = Math.Abs(actual.Upkeep.Single().RemainingFrames - modeled.Upkeep.Single().RemainingFrames);
        bool inactiveStart = initial.Upkeep.Count == 1 && initial.Upkeep[0].Enabled && !Active(samples[0].Zone)
            && (!initial.Upkeep[0].Exists || initial.Upkeep[0].ActivationRemainingFrames > 0);
        bool restored = samples.Any(s => Active(s.Zone)) && Active(nativeZone) && modeled.Upkeep.Single().Exists
            && modeled.Upkeep.Single().ActivationRemainingFrames == 0;
        bool supported = samples.Where(s => Active(s.Zone)).All(s => s.Zone.GetProperty("powered").GetBoolean()
            && s.Zone.GetProperty("inputMW").GetDouble() >= s.Zone.GetProperty("requiredMW").GetDouble() - 1e-6);
        bool settings = samples.All(s => s.Zone.GetProperty("enabled").GetBoolean()
            && s.Zone.GetProperty("actions").GetString() == samples[0].Zone.GetProperty("actions").GetString()
            && s.Zone.GetProperty("radius").GetDouble() == 10);
        bool activation = initial.Upkeep[0].ActivationFrames == 7200 && samples.First(s => Active(s.Zone)).Frame >= initial.Upkeep[0].ActivationRemainingFrames;
        bool budget = supplied > 0 && Math.Abs(supplied - consumed) < 1e-9 && Fuel(initial) == Fuel(actual) && Fuel(initial) == Fuel(modeled)
            && result.ProducerFuelConsumed.Values.SelectMany(v => v.Values).All(v => v == 0);
        bool paidCredit = creditDifference <= sunStep + 100 && actual.Upkeep[0].RemainingFrames < initial.Upkeep[0].RemainingFrames;
        bool passed = inactiveStart && restored && supported && settings && activation && budget && paidCredit;
        File.WriteAllText(output, JsonSerializer.Serialize(new { directory, inactiveStart, restored, supported, settings, activation, budget, paidCredit,
            nativeFirstActiveFrame = samples.FirstOrDefault(s => Active(s.Zone)).Frame, modeledRemainingFrames = modeled.Upkeep[0].RemainingFrames,
            nativeRemainingFrames = actual.Upkeep[0].RemainingFrames, creditDifferenceFrames = creditDifference,
            toleranceFrames = sunStep + 100, suppliedMWh = supplied, consumedMWh = consumed, result.Events, passed,
            method = "native inactive-to-active solar interval; one captured sun segment plus native Update100 credit bound; no shared recovery/interactive UI claim" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native environmental zone: passed={passed}, credit difference={creditDifference} frames, tolerance={sunStep + 100}");
        if (!passed) throw new InvalidOperationException("Native environmental restoration differential failed");
    }
}
