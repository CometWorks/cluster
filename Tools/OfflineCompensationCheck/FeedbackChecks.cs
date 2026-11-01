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
        var finite = Fixture(ice: 1);
        OfflineModel.Simulate(finite, 1200, 1200);
        Check(Math.Abs(finite.Storage[1].Amount - 20) < 1e-5 && OfflineModel.Amount(finite.Inventories[0], "ice") < .000001m, "finite ice ends cycle without free output");
        var disconnected = Fixture(); disconnected.Storage[1].Resource = "h2#isolated";
        OfflineModel.Simulate(disconnected, 1200, 1200);
        Check(disconnected.Storage[1].Amount == 0 && OfflineModel.Amount(disconnected.Inventories[0], "ice") == 1000, "disconnected circuit consumes no ice");
    }
}
