using ClusterPlugin;
using System.Text.Json;

internal static class SolarShadowChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        void Near(double a, double b, string why) => Check(Math.Abs(a - b) < 1e-10, why);
        OfflineProfile Make(double raw, double extra = 0) => new()
        {
            ClockFrameSeconds = 1.0 / 60,
            Sources = [new() { Block = 1, Kind = "Environmental", Resource = OfflineModel.Electricity,
                Maximum = 1, Curve = [raw], ShadowAllowance = [extra] }],
            Sinks = [new() { Block = 2, Resource = OfflineModel.Electricity, Required = 2, Adaptible = true }],
        };
        OfflineResult Run(OfflineProfile p, long frames = 216000) => OfflineModel.Simulate(p, frames, frames);
        foreach (int pivots in new[] { 8, 12, 100 })
        foreach (double cosine in new[] { 0.0, .1, .5, 1 })
        for (int lit = 0; lit <= pivots; lit++)
        {
            double raw = cosine * lit / pivots, extra = OfflineSunCurve.ShadowExtra(raw, lit, pivots);
            Check(extra >= 0 && extra <= .05 * raw, "relative five-percent bound");
            Check(raw + extra <= cosine + 1e-12, "geometric/nameplate cap");
            if (lit == 0 || lit == pivots || cosine == 0) Near(extra, 0, "clear/full-shadow/night unchanged");
            else Check(extra > 0, "only mixed visibility receives allowance");
            var result = Run(Make(raw, extra));
            Near(result.ProducerEnergyMWh.GetValueOrDefault(1), raw + extra, "same allocator delivers bounded supply");
            Near(result.SolarShadowAllowanceMWh.GetValueOrDefault(1), extra, "delivered allowance attribution");
            Near(result.ConsumerEnergyMWh.GetValueOrDefault(2), raw + extra, "consumer conservation");
        }
        var old = JsonSerializer.Deserialize<OfflineProfile>("{\"ClockFrameSeconds\":0.016666666666666666,\"Sources\":[{\"Block\":1,\"Resource\":\"MyObjectBuilder_GasProperties/Electricity\",\"Maximum\":1,\"Curve\":[0.5]}],\"Sinks\":[{\"Block\":2,\"Resource\":\"MyObjectBuilder_GasProperties/Electricity\",\"Required\":1,\"Adaptible\":true}]}", OfflineProfile.Json);
        Near(Run(old).ProducerEnergyMWh[1], .5, "old profile defaults to zero allowance");
        var bins = Make(.5, .025);
        bins.SunPeriodFrames = 400;
        bins.Sources[0].Curve = [.5, 0, 1, .5]; bins.Sources[0].ShadowAllowance = [.025, 0, 0, .025];
        var first = OfflineModel.Simulate(bins, 100, 400);
        Near(first.ProducerEnergyMWh[1], .525 * 100 / 216000, "exact bin boundary");
        Near(OfflineModel.Simulate(bins, 200, 400).ProducerEnergyMWh[1], 0, "next dark bin remains zero");
        Near(OfflineModel.Simulate(bins, 400, 400).ProducerEnergyMWh[1], 1.525 * 100 / 216000, "wrap endpoint uses each bin once");
        // Solar displaces paid fuel through ordinary source priorities, without a second credit.
        var shared = Make(.5, .025); shared.Sinks[0].Required = 1;
        shared.Items["fuel"] = new(); shared.Inventories.Add(new() { MaxVolume = 10, MaxMass = 10, Stacks = [new() { Item = "fuel", Amount = 2 }] });
        shared.Sources[0].Priority = 1;
        shared.Sources.Add(new() { Block = 3, Kind = "Reactor", Resource = OfflineModel.Electricity, Priority = 3, Maximum = 1,
            Fuel = [new() { Item = "fuel", Amount = 1m / 216000, Inventories = [0] }] });
        var paid = Run(shared);
        Near(paid.ProducerEnergyMWh[3], .475, "allowance shares dispatch with reactor");
        Near((double)paid.ProducerFuelConsumed[3]["fuel"], .475, "actual dispatch pays fuel");
        Near(paid.ProducerEnergyMWh.Values.Sum(), paid.ConsumerEnergyMWh.Values.Sum(), "whole budget conserves");
        var unused = Make(.5, .025); unused.Sinks.Clear();
        Near(Run(unused).SolarShadowAllowanceMWh.GetValueOrDefault(1), 0, "unused potential is not credited");
        var capped = Make(.99, OfflineSunCurve.ShadowExtra(.99, 99, 100));
        Near(Run(capped).ProducerEnergyMWh[1], 1, "nameplate enforced at high visibility");
        foreach (Action<OfflineSource> invalid in new Action<OfflineSource>[] {
            s => s.ShadowAllowance = [.026], s => s.ShadowAllowance = [double.NaN], s => s.ShadowAllowance = [-.01],
            s => s.ShadowAllowance = [], s => s.Resource = "oxygen", s => s.Kind = "Reactor", s => s.Curve = [0],
            s => { s.Curve = [1]; s.ShadowAllowance = [.01]; } })
        {
            var p = Make(.5, .025); invalid(p.Sources[0]);
            bool refused = false; try { Run(p); } catch (InvalidDataException) { refused = true; }
            Check(refused, "invalid allowance refused transactionally");
        }
        Console.WriteLine($"Solar shadow: {checks} assertions passed; raw geometry and bounded allowance remain separate.");
    }
}
