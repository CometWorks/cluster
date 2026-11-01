using ClusterPlugin;
using System.Text.Json;

internal static class FeedbackChecks
{
    internal static OfflineProfile Fixture(double supply = .1, bool adaptible = false, double ice = 1000)
    {
        var p = new OfflineProfile { Group = 1, Members = [1], ClockFrameSeconds = 1d / 60 };
        const string e = OfflineModel.Electricity;
        p.Items["ice"] = new() { Volume = 1, Mass = 1 };
        p.Inventories.Add(new() { MaxMass = 10000, MaxVolume = 10000, Stacks = [new() { Item = "ice", Amount = (decimal)ice }] });
        p.Sinks.Add(new() { Block = 2, Resource = e, Group = "Factory", Priority = 30, Required = .001, Powered = true, Adaptible = adaptible });
        p.Sinks.Add(new() { Block = 3, Resource = e, Group = "Factory", Priority = 30, Required = .000001, Powered = true });
        p.Sinks.Add(new() { Block = 4, Resource = e, Group = "Utility", Priority = 50, Required = .01 });
        p.Storage.Add(new() { Block = 5, Kind = "Battery", Resource = e, Amount = 3, Capacity = 3, Mode = 2, Pair = true });
        p.Sources.Add(new() { Block = 5, Resource = e, Maximum = supply, Storage = 0, StorageCost = 1d / 216000 });
        p.Storage.Add(new() { Block = 3, Kind = "Tank", Resource = "h2", PowerSink = 1, Amount = 0, Capacity = 10000000, MaxInput = 500000, InputConversion = 1d / 60, Pair = true, Mode = 1 });
        p.Sources.Add(new() { Block = 2, Resource = "h2", Kind = "Generator", Maximum = 500, PowerSink = 0, IdlePower = .001, OperationalPower = .5,
            PowerFeedback = true, Fuel = [new() { Item = "ice", Amount = 1m / 20 / 60, Inventories = [0] }] });
        return p;
    }
    internal static void Run()
    {
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
        foreach (long duration in new long[] { 1, 2, 3, 4, 13, 1200, 36000, 36_288_000 })
        {
            var p = Fixture(ice: 100000000);
            p.Inventories[0].MaxMass = p.Inventories[0].MaxVolume = 1000000000;
            p.Storage[1].Capacity = 1000000000;
            p.Storage[0].Amount = p.Storage[0].Capacity = 10000;
            var result = OfflineModel.Simulate(p, duration, duration);
            double poweredFrames = duration / 4 * 2 + Math.Min(duration % 4, 2);
            double gas = poweredFrames * 500 / 60;
            Check(Math.Abs(p.Storage[1].Amount - gas) < .00001, $"native two-on/two-off gas at finite horizon {duration}");
            Check(Math.Abs((double)result.ItemDelta["ice"] + gas / 20) < .00001, "gas/ice conservation");
            Check(Math.Abs(result.ProducerEnergyMWh.Values.Sum() - result.ConsumerEnergyMWh.Values.Sum()) < 1e-9, "complete shared electrical conservation");
            Check(result.Events < 30, "closed cycle compressed without frame replay");
        }
        foreach (double supply in new[] { 0d, .0005, .1, 1d })
        {
            var p = Fixture(supply);
            if (supply < .001001) foreach (var sink in p.Sinks) sink.Powered = false;
            OfflineModel.Simulate(p, 1200, 1200);
            Check((p.Storage[1].Amount > 0) == (supply >= .001001), "Factory zero/standby/working availability");
        }
        var sharedProduction = Fixture();
        sharedProduction.Sinks.Add(new() { Block = 6, Resource = OfflineModel.Electricity, Group = "Factory", Priority = 30, Required = .02 });
        sharedProduction.Items["ore"] = new() { Volume = 1, Mass = 1 };
        sharedProduction.Items["iron"] = new() { Volume = 1, Mass = 1 };
        sharedProduction.Inventories.Add(new() { MaxVolume = 100000, MaxMass = 100000, Stacks = [new() { Item = "ore", Amount = 10000 }] });
        sharedProduction.Production.Add(new() { Block = 6, Refinery = true, Enabled = true, PowerSink = 3, IdlePower = .001, OperationalPower = .02,
            Recipes = [new() { Id = "iron", UnitsPerFrame = 1, Inputs = [new() { Item = "ore", Amount = 1, Inventories = [1] }], Outputs = [new() { Item = "iron", Amount = 1, Inventories = [1] }] }] });
        var shared = OfflineModel.Simulate(sharedProduction, 1200, 1200);
        Check(Math.Abs(OfflineModel.Amount(sharedProduction.Inventories[1], "iron") - 600m) < .00001m, "production follows endogenous powered phases in shared Factory group");
        Check(Math.Abs(shared.ProducerEnergyMWh.Values.Sum() - shared.ConsumerEnergyMWh.Values.Sum()) < 1e-9, "shared production and gas use one conserved producer debit");
        var finite = Fixture(ice: 1);
        OfflineModel.Simulate(finite, 1200, 1200);
        Check(Math.Abs(finite.Storage[1].Amount - 20) < 1e-5 && OfflineModel.Amount(finite.Inventories[0], "ice") < .000001m, "finite ice ends cycle without free output");
        var disconnected = Fixture(); disconnected.Storage[1].Resource = "h2#isolated";
        OfflineModel.Simulate(disconnected, 1200, 1200);
        Check(disconnected.Storage[1].Amount == 0 && OfflineModel.Amount(disconnected.Inventories[0], "ice") == 1000, "disconnected circuit consumes no ice");
        var sampled = Fixture();
        sampled.Sinks[2].Required = 0;
        sampled.Storage[0].TickFrames = 100;
        var sampleResult = OfflineModel.Simulate(sampled, 400, 400);
        Check(3 - sampled.Storage[0].Amount > sampleResult.ProducerEnergyMWh.Values.Sum() * 1.5,
            "native Update100 battery cost samples a phase rather than averaging fast feedback");
        Check(Math.Abs(sampleResult.ProducerEnergyMWh.Values.Sum() - sampleResult.ConsumerEnergyMWh.Values.Sum()) < 1e-10,
            "sampled stock cost does not change conserved delivered electricity");
        var tankFeedback = Fixture();
        tankFeedback.Sinks[1].Required = .001; // capture during a native gas-transfer phase
        tankFeedback.Storage[1].IdlePower = .000001;
        tankFeedback.Storage[1].OperationalPower = .001;
        var tankResult = OfflineModel.Simulate(tankFeedback, 400, 400);
        Check(Math.Abs(tankResult.ConsumerEnergyMWh[3] - 100 * (.001 + .000001) / 216000) < 1e-12,
            "tank standby and transfer draw follow endogenous gas flow");
        var transition = Fixture(supply: 1);
        transition.Storage[0].TickFrames = 100;
        OfflineModel.Simulate(transition, 25, 25);
        transition.Sources[0].Maximum = .1;
        OfflineModel.Simulate(transition, 75, 50);
        Check(transition.Storage[0].Sampling, "cycle retains its unpaid native timer interval");
        transition.Sources[0].Maximum = 1;
        OfflineModel.Simulate(transition, 100, 25);
        float settled = 3f - (100f * (1f / 60f) * 1000f) * (.510001f / 3600000f);
        Check(Math.Abs(transition.Storage[0].Amount - settled) < 1e-7, "steady prefix is not debited twice when a cycle enters and leaves one timer interval");
        Check(!transition.Storage[0].Sampling && transition.Storage[0].AccountedEnergy == 0,
            "native settlement releases the temporary sampling interval");
    }
}
