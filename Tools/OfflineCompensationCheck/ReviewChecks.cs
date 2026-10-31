using ClusterPlugin;

internal static class ReviewChecks
{
    internal static void Run(string selected)
    {
        const string E = OfflineModel.Electricity;
        OfflineProfile P() => new() { Group = 1, Members = [1], FrozenFrame = 100, SimulatedThrough = 100, ClockFrameSeconds = 1.0 / 60 };
        OfflineMaterial M(string item, int inventory) => new() { Item = item, Amount = 1, Inventories = [inventory] };
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine(message); }
        if (selected is "R1" or "all")
        {
            var p = P();
            p.Storage.Add(new() { Kind = "Battery", Resource = E, Amount = 3, Capacity = 3, Full = true,
                Pair = true, Mode = 0, MaxInput = 12, InputConversion = 1.0 / 216000, RechargeEfficiency = .8 });
            p.Sources.Add(new() { Resource = E, Group = "Battery", Priority = 2, Maximum = 12, Storage = 0, StorageCost = 1.0 / 216000 });
            p.Sources.Add(new() { Resource = E, Group = "Reactor", Priority = 3, Maximum = 15 });
            p.Sinks.Add(new() { Resource = E, Group = "Utility", Required = 1 });
            var result = OfflineModel.Simulate(p, 216100, 1000000);
            Check(p.Storage[0].Amount > 2.999 && p.Storage[0].Amount <= 3 && result.Events > 1,
                $"R1 full Auto battery resumes charging: {p.Storage[0].Amount:R} MWh, {result.Events} events");
        }
        if (selected is "R2" or "all")
        {
            var p = P();
            foreach (string item in new[] { "ore", "iron", "component" }) p.Items[item] = new() { Mass = 1, Volume = 1 };
            p.Inventories.Add(new() { MaxVolume = 1000, MaxMass = 1000, Stacks = [new() { Item = "ore", Amount = 100 }] });
            p.Inventories.Add(new() { MaxVolume = 1000, MaxMass = 1000 });
            p.Inventories.Add(new() { MaxVolume = 1000, MaxMass = 1000 });
            p.Production.Add(new() { Refinery = true, Enabled = true, PowerSink = -1,
                Recipes = [new() { Id = "refine", UnitsPerFrame = 1, Inputs = [M("ore", 0)], Outputs = [M("iron", 1)] }] });
            p.Production.Add(new() { Enabled = true, PowerSink = -1,
                Recipes = [new() { Id = "assemble", UnitsPerFrame = .1, Inputs = [M("iron", 1)], Outputs = [M("component", 2)] }],
                Queue = [new() { Recipe = "assemble", Amount = 10 }] });
            OfflineModel.Simulate(p, 200, 1000);
            decimal made = OfflineModel.Amount(p.Inventories[2], "component"), iron = OfflineModel.Amount(p.Inventories[1], "iron");
            Check(made > 0 && made + iron == 100 && OfflineModel.Amount(p.Inventories[0], "ore") == 0,
                $"R2 upstream availability starts downstream production and conserves stock: {made} components, {iron} iron");
        }
        if (selected is "R3" or "all")
        {
            var p = P();
            p.Storage.Add(new() { Kind = "Tank", Resource = "h2", Amount = 100, Capacity = 100, Pair = true, MaxInput = 5, InputConversion = 1.0 / 60, PowerSink = 0 });
            p.Sources.Add(new() { Resource = "h2", Group = "Tank", Maximum = 5, Storage = 0, StorageCost = 1.0 / 60, PowerSink = 0 });
            p.Sinks.Add(new() { Resource = E, Group = "Utility", Required = .001 });
            p.Sinks.Add(new() { Resource = "h2", Group = "Thrust", Required = 1 });
            OfflineModel.Simulate(p, 160, 1000);
            Check(p.Storage[0].Amount == 100, "R3 unpowered tank cannot discharge");
            p.Storage[0].Amount = 0; p.Sources.Add(new() { Resource = "h2", Group = "Generator", Maximum = 10 });
            OfflineModel.Simulate(p, 220, 1000);
            Check(p.Storage[0].Amount == 0, "R3 unpowered tank cannot charge");
            p.Sources.Add(new() { Resource = E, Group = "Reactor", Maximum = 1 });
            OfflineModel.Simulate(p, 280, 1000);
            Check(p.Storage[0].Amount > 0, "R3 positive control: powered tank can charge");
        }
    }
}
