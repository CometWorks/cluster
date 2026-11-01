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
            bool ok = Math.Abs(gas - modelGas) <= 500d / 60 * 8 + .1 && Math.Abs(settledAndPendingIce - modelIce) <= 25d / 60 * 8 + .01
                && Math.Abs(nativeElectrical - modelElectrical) < .001 * 8 / 216000 + 1e-10
                && Math.Abs(result.ProducerEnergyMWh.Values.Sum() - result.ConsumerEnergyMWh.Values.Sum()) < 1e-10;
            rows.Add(new { frames, nativeGas = gas, modeledGas = modelGas, nativeIce = ice, pendingGas = pending,
                nativeSettledAndPendingIce = settledAndPendingIce, modeledIce = modelIce,
                nativeElectricalMWh = nativeElectrical, modeledElectricalMWh = modelElectrical, events = result.Events, passed = ok });
            passed &= ok;
        }
        passed &= rows.Count == 4;
        var report = new { source = Path.GetFullPath(path), productSource = Environment.GetEnvironmentVariable("TESTED_PRODUCT_SHA"),
            bound = "8 native frames (two feedback periods) plus native float stock quantum; ice debt is explicitly retained", rows, passed };
        File.WriteAllText(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        if (!passed) throw new Exception("Native Factory feedback differential failed");
    }
}
