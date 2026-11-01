using Concealment.Accounting;

internal static class ReservoirChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        OfflineProfile Profile(double amount = 100.83, string kind = "Engine")
        {
            var p = new OfflineProfile { Group = 1, Members = [1], ClockFrameSeconds = 1.0 / 60 };
            p.Storage.Add(new() { Block = 10, Kind = kind, Resource = "gas", Amount = amount, Capacity = 100,
                MaxInput = 300, InputConversion = 1.0 / 60 });
            p.Sources.Add(new() { Block = 10, Kind = "Engine", Group = "Engine", Resource = OfflineModel.Electricity,
                Maximum = 1, Storage = 0, StorageCost = 1.0 / 60 });
            return p;
        }
        var idle = Profile();
        OfflineModel.Simulate(idle, 60, 60);
        Check(idle.Storage[0].Amount == 100.83, "Native intake overshoot is retained without demand");
        var draining = Profile();
        draining.Sinks.Add(new() { Block = 20, Resource = OfflineModel.Electricity, Required = 1 });
        OfflineModel.Simulate(draining, 60, 60);
        Check(Math.Abs(draining.Storage[0].Amount - 99.83) < 1e-8, "Existing overfill pays the ordinary engine debit");
        var replenished = Profile();
        replenished.Sinks.Add(new() { Block = 20, Resource = OfflineModel.Electricity, Required = 1 });
        replenished.Sources.Add(new() { Block = 30, Kind = "Constant", Resource = "gas", Maximum = 100 });
        OfflineModel.Simulate(replenished, 3600, 3600);
        Check(replenished.Storage[0].Amount <= 100.83 && replenished.Storage[0].Amount >= 100,
            "Replenishment cannot grow the original native overfill");
        foreach (var invalid in new[] { Profile(106), Profile(double.NaN), Profile(100.83, "Battery"), Profile(100.83, "Tank") })
        {
            bool refused = false;
            try { OfflineModel.Simulate(invalid, 60, 60); } catch (System.IO.InvalidDataException) { refused = true; }
            Check(refused, "Non-native/out-of-bound reservoir state stays invalid");
        }
        var ordinary = Profile(100);
        OfflineModel.Simulate(ordinary, 60, 60);
        Check(ordinary.Storage[0].Amount == 100, "Old ordinary profiles retain their capacity behavior");
        Console.WriteLine($"PASS {checks} native engine overshoot/capacity assertions");
    }
}
