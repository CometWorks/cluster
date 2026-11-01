using Concealment.Accounting;
using System.Text.Json;

internal static class CapChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool condition, string why) { if (!condition) throw new Exception(why); checks++; }
        OfflineProfile Clone(OfflineProfile p) => JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(p, OfflineProfile.Json), OfflineProfile.Json);
        string Stocks(OfflineProfile p) { var copy = Clone(p); copy.SimulatedThrough = 0; return JsonSerializer.Serialize(copy, OfflineProfile.Json); }
        foreach (double seconds in new[] { 0.016, 1.0 / 60 })
        {
            const string E = OfflineModel.Electricity, G = "MyObjectBuilder_GasProperties/Hydrogen:1";
            var basis = new OfflineProfile { Group = 1, Members = [1], FrozenFrame = 10000, SimulatedThrough = 10000, ClockFrameSeconds = seconds };
            basis.Items["fuel"] = new() { Volume = .0001m, Mass = 1 };
            basis.Items["ore"] = new() { Volume = .0001m, Mass = 1 };
            basis.Items["product"] = new() { Volume = .0001m, Mass = 1 };
            basis.Items["ice"] = new() { Volume = .0001m, Mass = 1 };
            basis.Items["chip"] = new() { Volume = .0001m, Mass = 1, Integral = true };
            basis.Inventories.Add(new() { Block = 1, MaxVolume = 1e12m, MaxMass = 1e12m, Stacks = [new() { Item = "fuel", Amount = 1e9m }, new() { Item = "ore", Amount = 1e9m }, new() { Item = "ice", Amount = 1e9m }, new() { Item = "chip", Amount = 1000 }] });
            OfflineMaterial M(string item, decimal amount) => new() { Item = item, Amount = amount, Inventories = [0] };
            basis.Sinks.Add(new() { Block = 10, Resource = E, Group = "Factory", Required = 1, Priority = 30 });
            basis.Sinks.Add(new() { Block = 11, Resource = E, Group = "Zone", Required = 5, Priority = 1 });
            basis.Sinks.Add(new() { Block = 12, Resource = E, Group = "Generators", Required = .5, Priority = 75, Adaptible = true });
            basis.Sources.Add(new() { Block = 1, Resource = E, Group = "Reactor", Kind = "Reactor", Maximum = 100, Priority = 3, Fuel = [M("fuel", 1m/216000)] });
            basis.Sources.Add(new() { Block = 2, Resource = E, Group = "Solar", Kind = "Environmental", Maximum = 1, Priority = 1, Curve = [.4], ShadowAllowance = [.02] });
            basis.Sources.Add(new() { Block = 12, Resource = G, Group = "Generator", Kind = "Generator", Maximum = 60, Priority = 1, PowerSink = 2, Fuel = [M("ice", 1m/1200)] });
            basis.Storage.Add(new() { Block = 20, Kind = "Battery", Resource = E, Group = "Battery", Pair = true, Mode = 1, Enabled = true, Capacity = 1e6, MaxInput = 2, InputConversion = 1.0 / 216000, RechargeEfficiency = .8, Priority = 95 });
            basis.Storage.Add(new() { Block = 21, Kind = "Tank", Resource = G, Group = "Tank", Pair = true, Mode = 1, Enabled = true, Capacity = 1e9, MaxInput = 60, InputConversion = 1.0 / 60, Priority = 10 });
            basis.Storage.Add(new() { Block = 22, Kind = "Jump", Resource = E, Group = "Charging", Enabled = true, Capacity = 1000, MaxInput = 1, InputConversion = 1.0 / 216000, Priority = 85 });
            basis.Production.Add(new() { Block = 10, Enabled = true, Refinery = true, PowerSink = 0, IdlePower = .001, OperationalPower = 1, Recipes = [new() { Id = "recipe", UnitsPerFrame = .001, Cached = true, Inputs = [M("ore", 1)], Outputs = [M("product", 1)] }] });
            basis.Upkeep.Add(new() { Block = 11, Enabled = true, Exists = true, PowerSink = 1, RemainingFrames = 3600, PeriodFrames = 216000, OperationalPower = 5, IdlePower = .000001, Chips = M("chip", 1), ActivationFrames = 7200 });
            long cap = 72L * (3600000 / 16); // actual saved world time in BOTH hosts
            long wake = basis.SimulatedThrough + cap;
            var exact = Clone(basis); var result = OfflineModel.Simulate(exact, wake, cap);
            Check(!result.Capped && result.Frames == cap && exact.SimulatedThrough == wake, "exact 72h boundary/clock");
            Check(Math.Abs(result.ProductionUnits[10] - .001 * cap * seconds * 60) < 1e-5, "production uses native resource phases for exactly 72 elapsed world hours");
            Check(exact.Storage[0].Amount > 100 && exact.Storage[1].Amount > 1e7 && exact.Storage[2].Amount > 70, "electricity/gas/storage share capped time");
            Check(result.ProducerFuelConsumed[1]["fuel"] > 500 && result.ProducerFuelConsumed[12]["ice"] > 700000 && result.UpkeepConsumed[11] == (long)Math.Round(cap * seconds / 3600), "fuel/ice/zone costs stop at capped time");
            foreach (long requested in new[] { cap - 1, cap, cap + 1, 168L * (3600000 / 16), long.MaxValue - basis.SimulatedThrough })
            {
                var model = Clone(basis); long end = checked(basis.SimulatedThrough + requested);
                var r = OfflineModel.Simulate(model, end, cap);
                Check(r.Frames == Math.Min(requested, cap) && r.Capped == (requested > cap), "below/exact/above72 clip");
                Check(r.CompensatedTo == end && model.SimulatedThrough == end, "all excess is forfeited at actual wake");
                if (requested >= cap) Check(Stocks(model) == Stocks(exact), "ALL detached outcomes stop at the same cutoff");
                string stock = Stocks(model);
                var repeat = OfflineModel.Simulate(model, end, cap);
                Check(repeat.Frames == 0 && Stocks(model) == stock, "retry cannot collect another capped chunk");
                var persisted = Clone(model);
                var afterReload = OfflineModel.Simulate(persisted, end, cap);
                Check(afterReload.Frames == 0 && Stocks(persisted) == stock, "profile serialization/reload cannot bank excess");
            }
            // Immutable originals recompute the same first capped interval at a
            // later retry; never combine the new result with previously applied stock.
            var retry = Clone(basis); var retryResult = OfflineModel.Simulate(retry, wake + cap * 3, cap);
            Check(Stocks(retry) == Stocks(exact) && retryResult.Capped, "immutable original retry recomputes first72h only");
            var nextInterval = Clone(exact); nextInterval.FrozenFrame = nextInterval.SimulatedThrough;
            var future = OfflineModel.Simulate(nextInterval, nextInterval.SimulatedThrough + cap, cap);
            Check(future.Frames == cap && !future.Capped, "a genuinely new future conceal interval can earn its own cap");
            foreach (long overrideCap in new[] { 0L, cap / 2, cap * 2 })
            {
                var changed = Clone(basis); var r = OfflineModel.Simulate(changed, wake + cap, overrideCap);
                Check(r.Frames == overrideCap && changed.SimulatedThrough == wake + cap, "explicit host cap overrides preserved without banking");
            }
        }
        Console.WriteLine($"PASS {checks} cap/accounting/clock/forfeiture assertions (both host clock units)");
    }
}
