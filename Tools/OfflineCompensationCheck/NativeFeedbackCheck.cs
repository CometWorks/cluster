using ClusterPlugin;
using System.Text.Json;

internal static class NativeFeedbackCheck
{
    internal static void Run(string path, string destination)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var profile = json.RootElement.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json);
        var trace = json.RootElement.GetProperty("trace").EnumerateArray().ToArray();
        var baseline = trace[0];
        var rows = new List<object>();
        bool passed = true;
        foreach (int horizon in new[] { 4, 100, 600, 1200 })
        {
            var actual = trace.FirstOrDefault(t => t.GetProperty("frame").GetInt64() >= horizon);
            if (actual.ValueKind == JsonValueKind.Undefined) continue;
            long frames = actual.GetProperty("frame").GetInt64();
            var model = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(profile, OfflineProfile.Json), OfflineProfile.Json);
            model.ClockFrameSeconds = 1d / 60; // native frame horizons; authority clock is unchanged
            var result = OfflineModel.Simulate(model, model.SimulatedThrough + frames, frames);
            double gas = actual.GetProperty("tank").GetDouble() - baseline.GetProperty("tank").GetDouble();
            double modelGas = model.Storage.Single(s => s.Kind == "Tank").Amount - profile.Storage.Single(s => s.Kind == "Tank").Amount;
            decimal ice = baseline.GetProperty("ice").GetDecimal() - actual.GetProperty("ice").GetDecimal();
            double pending = actual.GetProperty("pending").GetDouble() - baseline.GetProperty("pending").GetDouble();
            double settledAndPendingIce = (double)ice + pending / 20;
            double modelIce = -(double)result.ItemDelta.GetValueOrDefault("MyObjectBuilder_Ore/Ice");
            double nativeElectrical = trace.Where(t => t.GetProperty("frame").GetInt64() > 0 && t.GetProperty("frame").GetInt64() <= frames).Sum(t => t.GetProperty("input").GetDouble()) / 216000;
            long producer = model.Sources.First(s => s.Kind == "Generator").Block;
            double modelElectrical = result.ConsumerEnergyMWh.GetValueOrDefault(producer);
            double nativeBattery = (double)baseline.GetProperty("battery").GetSingle() - actual.GetProperty("battery").GetSingle();
            double modelBattery = profile.Storage.Single(s => s.Kind == "Battery").Amount - model.Storage.Single(s => s.Kind == "Battery").Amount;
            bool sampledStorage = profile.Storage.Single(s => s.Kind == "Battery").TickFrames > 0;
            // Stock is sampled on Update100, not the integral of the four-frame
            // output cycle. Keep delivered energy and settled native cost visible.
            float battery = (float)baseline.GetProperty("battery").GetDouble();
            double nativeFloatBound = (sampledStorage ? 2 : frames / 2d) * (MathF.BitIncrement(battery) - battery);
            double maximumDraw = baseline.TryGetProperty("batteryOutput", out _) ? trace.Max(t => t.GetProperty("batteryOutput").GetDouble()) : .002001;
            double nativeStorageTimerBound = maximumDraw * 100 / 216000;
            double? nativeTotalElectrical = baseline.TryGetProperty("batteryOutput", out _) ? trace.Where(t => t.GetProperty("frame").GetInt64() > 0
                && t.GetProperty("frame").GetInt64() <= frames).Sum(t => t.GetProperty("batteryOutput").GetDouble()) / 216000 : null;
            bool ok = Math.Abs(gas - modelGas) <= 500d / 60 * 8 + .1 && Math.Abs(settledAndPendingIce - modelIce) <= 25d / 60 * 8 + .01
                && Math.Abs(nativeElectrical - modelElectrical) < .001 * 8 / 216000 + 1e-10
                && Math.Abs(result.ProducerEnergyMWh.Values.Sum() - result.ConsumerEnergyMWh.Values.Sum()) < 1e-10
                && (sampledStorage || Math.Abs(modelBattery - result.ProducerEnergyMWh.Values.Sum()) < 1e-10)
                && Math.Abs(nativeBattery - modelBattery) <= nativeFloatBound + nativeStorageTimerBound
                && (!nativeTotalElectrical.HasValue || Math.Abs(nativeTotalElectrical.Value - result.ProducerEnergyMWh.Values.Sum()) <= maximumDraw * 8 / 216000 + 1e-10);
            rows.Add(new { frames, nativeGas = gas, modeledGas = modelGas, nativeIce = ice, pendingGas = pending,
                nativeSettledAndPendingIce = settledAndPendingIce, modeledIce = modelIce,
                nativeElectricalMWh = nativeElectrical, modeledElectricalMWh = modelElectrical,
                nativeTotalElectricalMWh = nativeTotalElectrical, modeledTotalElectricalMWh = result.ProducerEnergyMWh.Values.Sum(), sampledStorage,
                nativeBatteryDebitMWh = nativeBattery, modeledBatteryDebitMWh = modelBattery,
                nativeFloatStorageBoundMWh = nativeFloatBound, nativeStorageTimerBoundMWh = nativeStorageTimerBound,
                events = result.Events, passed = ok });
            passed &= ok;
        }
        passed &= rows.Count == 4;
        var report = new { source = Path.GetFullPath(path), productSource = Environment.GetEnvironmentVariable("TESTED_PRODUCT_SHA"),
            bound = "8 native frames for flows/energy; two float ULP plus one Update100 settlement for sampled battery stock; ice debt explicitly retained", rows, passed };
        File.WriteAllText(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        if (!passed) throw new Exception("Native Factory feedback differential failed");
    }
}
