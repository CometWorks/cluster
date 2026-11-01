using Concealment.Accounting;
using System.Text.Json;

internal static class PowerBudgetChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        void Near(double actual, double expected, string why, double tolerance = 1e-8) => Check(Math.Abs(actual - expected) <= tolerance, $"{why}: {actual:R} != {expected:R}");
        OfflineProfile Clone(OfflineProfile p) => JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(p, OfflineProfile.Json), OfflineProfile.Json);
        OfflineMaterial M(string item, decimal amount = 1) => new() { Item = item, Amount = amount, Inventories = [0] };
        OfflineProfile Base(decimal chips = 3, double credit = 60, double draw = 5)
        {
            var p = new OfflineProfile { Group = 1, Members = [1], ClockFrameSeconds = 1.0 / 60 };
            foreach (string item in new[] { "chips", "fuel", "ore", "ingot", "component" }) p.Items[item] = new() { Mass = 1, Volume = 1 };
            p.Inventories.Add(new() { MaxMass = 1000, MaxVolume = 1000, Stacks = [new() { Item = "chips", Amount = chips }, new() { Item = "fuel", Amount = 10 }] });
            p.Sinks.Add(new() { Block = 10, Resource = OfflineModel.Electricity, Group = "Zone", Required = draw });
            p.Upkeep.Add(new() { Block = 10, Enabled = true, RemainingFrames = credit, PeriodFrames = 60, PowerSink = 0,
                OperationalPower = draw, IdlePower = .000001, ActivationFrames = 100, Chips = M("chips") });
            return p;
        }
        void Reactor(OfflineProfile p, double capacity = 15, long block = 20) => p.Sources.Add(new() { Block = block, Resource = OfflineModel.Electricity,
            Group = "Reactor", Priority = 2, Maximum = capacity, Fuel = [M("fuel", 1m / 216000)] });
        void Production(OfflineProfile p, bool refinery, decimal stock = 10)
        {
            p.Inventories[0].Stacks.Add(new() { Item = "ore", Amount = stock });
            p.Sinks.Add(new() { Block = 30, Resource = OfflineModel.Electricity, Group = "Production", Priority = 1 });
            p.Production.Add(new() { Block = 30, Enabled = true, Refinery = refinery, PowerSink = 1, IdlePower = .2, OperationalPower = 2,
                Recipes = [new() { Id = "make", UnitsPerFrame = refinery ? 1 : .1, Inputs = [M("ore")], Outputs = [M(refinery ? "ingot" : "component")] }],
                Queue = [new() { Recipe = "make", Amount = 100 }] });
        }
        OfflineResult Simulate(OfflineProfile p, long frames = 600) => OfflineModel.Simulate(p, p.SimulatedThrough + frames, 1000000);
        double Stock(OfflineProfile p, string item) => (double)OfflineModel.Amount(p.Inventories[0], item);

        // Actual engine semantics: power loss removes the bubble and freezes paid credit;
        // it does not consume chips or permanently flip the functional block off.
        {
            var p = Base(); var r = Simulate(p);
            Near(p.Upkeep[0].RemainingFrames, 60, "unpowered paid credit pauses");
            Near(Stock(p, "chips"), 3, "unpowered zone consumes no chip");
            Check(p.Upkeep[0].Enabled && !p.Upkeep[0].Exists && r.DisabledZones.SequenceEqual(new long[] { 10 }), "unpowered bubble absent, block remains eligible for recovery");
        }
        // Recovery waits through activation before charging paid upkeep again.
        {
            var p = Base(chips: 5); p.SunPeriodFrames = 600;
            p.Sources.Add(new() { Block = 40, Resource = OfflineModel.Electricity, Maximum = 10, Curve = [0, 1] });
            var r = Simulate(p);
            Near(Stock(p, "chips"), 2, "only 200 powered active frames consume three renewal chips");
            Near(p.Upkeep[0].RemainingFrames, 40, "darkness and activation do not spend paid credit");
            Check(!p.Upkeep[0].Exists && !r.Zones.Single().Exhausted, "renewable loss removes bubble without exhausting its remaining credit");
        }
        // Refinery input exhaustion selects standby; an assembler's remaining queue
        // retains working draw even though missing input stops item completion.
        foreach (bool refinery in new[] { true, false })
        {
            var p = Base(chips: 0); Reactor(p); Production(p, refinery, stock: refinery ? 10 : 2);
            var original = Clone(p); var r = Simulate(p);
            double working = refinery ? 10 : 600;
            double expected = (5 * 60 + 2 * working + .2 * (600 - working) + .000001 * 540) / 216000;
            Near(10 - Stock(p, "fuel"), expected, "delivered demand debits reactor fuel, never maximum capacity");
            Near(r.ProducerEnergyMWh.Values.Sum(), r.ConsumerEnergyMWh.Values.Sum() + r.StorageInputEnergyMWh.Values.Sum(), "shared delivered energy conserved");
            Near((double)r.ProducerFuelConsumed[20]["fuel"], expected, "per-producer fuel diagnostics match the one stock debit");
            Near(Stock(p, refinery ? "ingot" : "component"), refinery ? 10 : 2, "production stops at empty input");
            Check(!p.Upkeep[0].Enabled && !p.Upkeep[0].Exists, "chip exhaustion removes bubble with power available");
            var retry = Clone(original); var second = Simulate(retry);
            Near(Stock(retry, "fuel"), Stock(p, "fuel"), "immutable original retry does not stack fuel debits");
            Near(second.ConsumerEnergyMWh[10], r.ConsumerEnergyMWh[10], "retry zone energy is recomputed, not accumulated");
        }
        // Finite fuel blacks out both zone and production; no free product or chip upkeep.
        {
            var p = Base(credit: 10000); Reactor(p); Production(p, true);
            p.Inventories.Add(new() { MaxMass = 0, MaxVolume = 0 });
            p.Production[0].Recipes[0].Outputs[0].Inventories = [1];
            var r = Simulate(p);
            Near(10 - Stock(p, "fuel"), 5.2 * 600 / 216000, "full output reduces production to standby in the same grid budget");
            Check(r.ProductionUnits.GetValueOrDefault(30) == 0, "blocked output never creates products");
        }
        {
            var p = Base(credit: 600); Reactor(p); Production(p, true, stock: 500);
            p.Inventories[0].Stacks.Single(s => s.Item == "fuel").Amount = 700m / 216000;
            var r = Simulate(p);
            Near(Stock(p, "ingot"), 100, "production ends at common fuel exhaustion", .001);
            Near(p.Upkeep[0].RemainingFrames, 500, "upkeep stops with delivered power", .001);
            Near(Stock(p, "chips"), 3, "outage does not consume future chips");
            Check(!p.Upkeep[0].Exists, "no powered bubble survives fuel exhaustion");
        }
        // Renewables and charging share the allocator too; battery recharge loss remains local.
        {
            var p = Base(credit: 10000); Reactor(p); Production(p, true, stock: 900);
            p.Sources.Add(new() { Block = 40, Resource = OfflineModel.Electricity, Priority = 0, Group = "Solar", Maximum = 8 });
            p.Storage.Add(new() { Block = 50, Resource = OfflineModel.Electricity, Kind = "Battery", Capacity = 1, Pair = true,
                MaxInput = 2, Mode = 1, InputConversion = 1.0 / 216000, RechargeEfficiency = .8 });
            var r = Simulate(p);
            Near(r.ProducerEnergyMWh[40], 8 * 600.0 / 216000, "renewable source serves before reactor");
            Near(r.ProducerEnergyMWh[20], 600.0 / 216000, "reactor supplies only the remaining one MW");
            Near(p.Storage[0].Amount, 2 * 600.0 / 216000 * .8, "battery receives its actual charge with recharge loss");
            Near(r.ProducerEnergyMWh.Values.Sum(), r.ConsumerEnergyMWh.Values.Sum() + r.StorageInputEnergyMWh.Values.Sum(), "production, zone and battery use the same budget");
        }
        // Both gas outputs share the generator's electrical sink. Empty/full gas
        // demand costs only standby; working gas pays operational draw once.
        foreach (bool demand in new[] { false, true })
        {
            var p = Base(); p.Upkeep.Clear(); p.Sinks.Clear(); Reactor(p);
            p.Sinks.Add(new() { Block = 60, Resource = OfflineModel.Electricity, Required = .5 });
            foreach (string gas in new[] { "oxygen", "hydrogen" })
            {
                p.Sources.Add(new() { Block = 60, Resource = gas, Maximum = 10, PowerSink = 0, IdlePower = .001, OperationalPower = .5 });
                if (demand) p.Sinks.Add(new() { Block = 70, Resource = gas, Required = 1 });
            }
            var r = Simulate(p);
            Near(r.ConsumerEnergyMWh[60], (demand ? .5 : .001) * 600 / 216000, "gas producer uses shared working/standby power");
            Near(10 - Stock(p, "fuel"), (demand ? .5 : .001) * 600 / 216000, "reactor fuel pays the one actual gas producer sink");
        }
        {
            var p = Base(); p.Upkeep.Clear(); p.Sinks.Clear();
            p.Sources.Add(new() { Block = 20, Resource = OfflineModel.Electricity, Maximum = .1 });
            p.Sinks.Add(new() { Block = 60, Resource = OfflineModel.Electricity, Required = .5 });
            p.Sinks.Add(new() { Block = 70, Resource = "hydrogen", Required = 1 });
            p.Inventories[0].Stacks.Add(new() { Item = "ore", Amount = 10 });
            p.Sources.Add(new() { Block = 60, Resource = "hydrogen", Maximum = 10, PowerSink = 0, IdlePower = .001, OperationalPower = .5,
                Fuel = [M("ore", 1m / 60)] });
            var r = Simulate(p);
            Near(r.ConsumerEnergyMWh[60], .001 * 600 / 216000, "insufficient working power falls back to standby");
            Near(Stock(p, "ore"), 10, "gas cannot bootstrap itself from insufficient electrical supply");
        }
        // One elapsed world hour contains 225000 actual native frames.
        {
            var p = Base(credit: 1000000); Reactor(p);
            var r = Simulate(p, 225000);
            Near(r.ConsumerEnergyMWh[10], 5 * 25d / 24, "world hour advances native per-frame delivered energy");
            Near(10 - Stock(p, "fuel"), 5 * 25d / 24, "world/native clock preserves actual fuel cost");
            Check(r.Frames == 225000, "diagnostic interval retains original authority frame units");
        }
        Console.WriteLine($"Shared zone/production power budget: {checks} assertions passed; detached model, not native proof.");
    }
}
