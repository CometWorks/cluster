using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components.Beacon;
using VRage.Utils;

namespace ClusterPlugin;

internal static partial class OfflineCompensation
{
	private static OfflineProfile Capture(MyCubeGrid[] grids, Census census, bool quick)
	{
		var blocks = grids.SelectMany(g => g.GetFatBlocks()).OrderBy(b => b.EntityId).ToArray();
		var p = new OfflineProfile { Group = census.Group, Members = grids.Select(g => g.EntityId).ToArray(), FrozenFrame = Frame, SimulatedThrough = Frame,
			Creative = MySession.Static.CreativeMode, Simplified = MySession.Static.SimplifiedSimulation,
			SunFrame = MySession.Static.ElapsedGameTime.TotalMilliseconds / 16,
			SunPeriodFrames = MySession.Static.Settings.EnableSunRotation ? MySession.Static.Settings.SunRotationIntervalMinutes * 60000.0 / 16 : 0 };
		var definitions = new HashSet<string>(blocks.Select(b => b.BlockDefinition.Id.ToString()));
		var inventoryIndices = new Dictionary<MyInventory, int>();
		foreach (var block in blocks)
			for (int i = 0; i < block.InventoryCount; i++)
			{
				var inventory = block.GetInventory(i);
				if (inventory == null) continue;
				inventoryIndices[inventory] = p.Inventories.Count;
				p.Inventories.Add(new() { Block = block.EntityId, Index = i, InventoryId = inventory.InventoryId.ToString(),
					MaxVolume = Limit((double)inventory.MaxVolume), MaxMass = Limit((double)inventory.MaxMass),
					Stacks = inventory.GetItems().Select(item => new OfflineStack { Item = item.Content.GetId().ToString(), Amount = (decimal)item.Amount }).ToList() });
			}
		void Item(MyDefinitionId id)
		{
			string key = id.ToString();
			if (p.Items.ContainsKey(key)) return;
			var definition = MyDefinitionManager.Static.GetPhysicalItemDefinition(id) ?? throw new InvalidOperationException("Unknown physical item " + id);
			p.Items[key] = new() { Volume = (decimal)definition.Volume, Mass = (decimal)definition.Mass, Integral = definition.HasIntegralAmounts };
			definitions.Add(key);
		}
		foreach (var stack in p.Inventories.SelectMany(i => i.Stacks)) Item(MyDefinitionId.Parse(stack.Item));
		var distribution = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyResourceDistributionGroupDefinition>().ToArray();
		foreach (var definition in distribution) definitions.Add(definition.Id.ToString());
		(int Priority, bool Adaptible) Group(MyStringHash name, bool source)
		{
			var definition = distribution.FirstOrDefault(d => d.IsSource == source && d.Id.SubtypeName == name.String);
			return (definition?.Priority ?? int.MaxValue, definition?.IsAdaptible ?? false);
		}
		var gasCircuits = new Dictionary<long, long>();
		string Resource(MyDefinitionId id, MyCubeBlock block)
		{
			if (id != MyResourceDistributorComponent.ElectricityId) definitions.Add(id.ToString());
			if (id == MyResourceDistributorComponent.ElectricityId) return id.ToString();
			if (!gasCircuits.TryGetValue(block.EntityId, out long circuit))
			{
				circuit = block.EntityId;
				if (block is IMyConveyorEndpointBlock endpoint)
					foreach (var other in blocks.OfType<IMyConveyorEndpointBlock>())
						if (MyGridConveyorSystem.Reachable(endpoint.ConveyorEndpoint, other.ConveyorEndpoint)) circuit = Math.Min(circuit, other.ConveyorEndpoint.CubeBlock.EntityId);
				gasCircuits[block.EntityId] = circuit;
			}
			return id + ":" + circuit;
		}
		OfflineMaterial Material(MyCubeBlock block, MyDefinitionId id, decimal amount, MyInventory own, bool push)
		{
			Item(id);
			var paths = new List<int>();
			if (own != null && inventoryIndices.TryGetValue(own, out int local) && (!push || own.CheckConstraint(id))) paths.Add(local);
			bool conveyor = block switch { MyProductionBlock production => production.UseConveyorSystem, MyReactor reactor => reactor.UseConveyorSystem,
				MyGasGenerator generator => generator.UseConveyorSystem, _ => true };
			if (conveyor && block is IMyConveyorEndpointBlock endpoint)
			{
				var mapping = block.CubeGrid.GridSystems.ConveyorSystem.GetConveyorEndpointMapping(endpoint);
				foreach (var remote in (push ? mapping.pushElements : mapping.pullElements) ?? [])
				{
					var owner = remote?.ConveyorEndpoint.CubeBlock;
					if (owner == null || !grids.Contains(owner.CubeGrid) || !MyGridConveyorSystem.CanTransfer(endpoint, remote, id, push, calcImmediately: true)) continue;
					for (int i = 0; i < owner.InventoryCount; i++)
					{
						var inventory = owner.GetInventory(i);
						if (inventory == null || !inventoryIndices.TryGetValue(inventory, out int index) || paths.Contains(index)) continue;
						if ((inventory.GetFlags() & (push ? MyInventoryFlags.CanReceive : MyInventoryFlags.CanSend)) == 0 || push && !inventory.CheckConstraint(id)) continue;
						paths.Add(index);
					}
				}
			}
			return new() { Item = id.ToString(), Amount = amount, Inventories = paths.ToArray() };
		}
		foreach (var grid in grids) grid.GridSystems.ResourceDistributor.ResourceStateByType(MyResourceDistributorComponent.ElectricityId, withRecompute: true, grid);
		foreach (var block in blocks)
		{
			bool enabled = block.IsFunctional && (block is not MyFunctionalBlock functional || functional.Enabled);
			bool scripted = Scripted(block);
			string unknown = block switch { MyProgrammableBlock => "programmable block", MyTimerBlock timer when timer.IsCountingDown => "countdown timer",
				MyShipConnector connector when connector.ThrowOut => "ejecting connector", _ => null };
			if (unknown != null) p.Unknowns.Add(block.EntityId + ":" + unknown);
			bool inventoryUnknown = scripted && census.Observations.TryGetValue(block.EntityId, out var observedSamples) && observedSamples.Count > 1
				&& block.InventoryCount > 0 && Enumerable.Range(observedSamples[0].Length - block.InventoryCount, block.InventoryCount)
					.Any(i => observedSamples.Any(sample => Math.Abs(sample[i] - observedSamples[0][i]) > 0.000001));
			if (inventoryUnknown) { unknown = "script inventory changes cannot be separated from conveyor transfers"; p.Unknowns.Add(block.EntityId + ":" + unknown); }
			bool opaqueStorage = scripted && block is MyBatteryBlock or MyGasTank or MyGasFueledPowerProducer or MyJumpDrive;
			if (opaqueStorage) { unknown = "script-driven storage has no trustworthy rate contract"; p.Unknowns.Add(block.EntityId + ":" + unknown); }
			bool observe = !inventoryUnknown && !opaqueStorage && (scripted && unknown == null || unknown != null && ClusterConfig.Get("offline.unknownBlocks") == "Mean");
			if (scripted && !observe) p.Unknowns.Add(block.EntityId + ":mod logic frozen");
			if (quick && census.Observations.ContainsKey(block.EntityId)) p.Confidence = "Approximate";
			block.Components.TryGet<MyResourceSinkComponent>(out var sink);
			block.Components.TryGet<MyResourceSourceComponent>(out var source);
			int powerSink = -1;
			var sources = new List<OfflineSource>();
			if (source != null)
				foreach (var type in source.ResourceTypes)
				{
					var group = Group(source.Group, true);
					double maximum = enabled && source.Enabled && source.ProductionEnabledByType(type) ? source.MaxOutputUnlimitedByCapacity(type) : 0;
					var port = new OfflineSource { Block = block.EntityId, Resource = Resource(type, block), Group = source.Group.String,
						Priority = group.Priority, Kind = "Constant", Maximum = maximum };
					sources.Add(port); p.Sources.Add(port);
				}
			if (sink != null)
				foreach (var type in sink.AcceptedResources)
				{
					if (block is MyBatteryBlock || block is MyGasTank && type != MyResourceDistributorComponent.ElectricityId
						|| block is MyJumpDrive && type == MyResourceDistributorComponent.ElectricityId
						|| block is MyGasFueledPowerProducer engineSink && type == engineSink.BlockDefinition.Fuel.FuelId) continue;
					var group = Group(sink.Group, false);
					if (type == MyResourceDistributorComponent.ElectricityId) powerSink = p.Sinks.Count;
					p.Sinks.Add(new() { Block = block.EntityId, Resource = Resource(type, block), Group = sink.Group.String, Priority = group.Priority,
						Adaptible = group.Adaptible, Required = sink.RequiredInputByType(type) });
				}
			if (unknown != null && !observe || scripted && !observe)
			{
				// Its constant idle requirement stays modeled. Arbitrary scripts/items never acquire invented output.
				foreach (var port in sources) port.Maximum = 0;
				continue;
			}
			if (observe)
			{
				if (!census.Observations.TryGetValue(block.EntityId, out var samples) || samples.Count < 2)
				{
					p.Unknowns.Add(block.EntityId + ":insufficient observation"); foreach (var port in sources) port.Maximum = 0; continue;
				}
				if (samples.Any(s => s.Length != samples[0].Length))
				{ p.Unknowns.Add(block.EntityId + ":resource topology varied"); foreach (var port in sources) port.Maximum = 0; continue; }
				int rateCount = samples[0].Length - block.InventoryCount;
				var means = Enumerable.Range(0, rateCount).Select(i => samples.Average(s => s[i])).ToArray();
				p.Observations.Add(new() { Block = block.EntityId, Samples = samples.Count, Mean = means,
					Minimum = Enumerable.Range(0, rateCount).Select(i => samples.Min(s => s[i])).ToArray(),
					Maximum = Enumerable.Range(0, rateCount).Select(i => samples.Max(s => s[i])).ToArray(),
					Variance = Enumerable.Range(0, rateCount).Select(i => samples.Average(s => (s[i] - means[i]) * (s[i] - means[i]))).ToArray() });
				double tolerance = double.Parse(ClusterConfig.Get("offline.cacheTolerance"), CultureInfo.InvariantCulture);
				bool varying = Enumerable.Range(0, samples[0].Length).Any(i => samples.Max(s => s[i]) - samples.Min(s => s[i]) > tolerance * Math.Max(0.000001, samples.Average(s => Math.Abs(s[i]))));
				int offset = 0;
				foreach (var port in p.Sinks.Where(s => s.Block == block.EntityId))
				{
					port.Required = samples.Average(s => s[offset]); offset++;
				}
				foreach (var port in sources) { port.Maximum = samples.Average(s => s[offset]); port.Kind = varying ? "Observed" : "Constant"; offset++; }
				if (!varying)
				{
					string constantKey = "observed|" + block.BlockDefinition.Id + "|" + ContentHash(new[] { block.BlockDefinition.Id.ToString() });
					void Cache(string key, double rate)
					{
						if (rate > 0 && (!OfflineRateCache.TryGet(key, out var old) || Math.Abs(old.Rate - rate) > tolerance * rate)) OfflineRateCache.Measured(key, rate, rate);
					}
					foreach (var port in p.Sinks.Where(s => s.Block == block.EntityId)) Cache(constantKey + "|in|" + port.Resource, port.Required);
					foreach (var port in sources) Cache(constantKey + "|out|" + port.Resource, port.Maximum);
				}
				// Observable inventories are retained as stocks. Arbitrary inventory creation/motion is unknown.
				if (varying) p.Confidence = "Approximate";
			}
			if (block is MyBatteryBlock battery)
			{
				var d = battery.BlockDefinition; int index = p.Storage.Count;
				p.Storage.Add(new() { Block = block.EntityId, Kind = "Battery", Resource = Resource(MyResourceDistributorComponent.ElectricityId, block),
					Amount = battery.CurrentStoredPower, Capacity = battery.MaxStoredPower, MaxInput = d.RequiredPowerInput, InputConversion = 1.0 / 216000,
					Mode = (int)battery.ChargeMode, Pair = true, Enabled = enabled, Full = battery.m_isFull, RechargeEfficiency = d.RechargeMultiplier });
				foreach (var port in sources) { port.Storage = index; port.StorageCost = 1.0 / 216000; port.Kind = "Battery"; port.Maximum = enabled && battery.ChargeMode != Sandbox.ModAPI.Ingame.ChargeMode.Recharge ? d.MaxPowerOutput : 0; }
			}
			else if (block is MyGasTank tank)
			{
				var d = (MyGasTankDefinition)block.BlockDefinition; int index = p.Storage.Count;
				bool gasEnabled = enabled && (MySession.Static.Settings.EnableOxygen || d.StoredGasId != MyResourceDistributorComponent.OxygenId);
				p.Storage.Add(new() { Block = block.EntityId, Kind = "Tank", Resource = Resource(d.StoredGasId, block), Amount = tank.FilledRatio * d.Capacity,
					Capacity = d.Capacity, MaxInput = d.Capacity * 0.05, InputConversion = 1.0 / 60, Mode = ((Sandbox.ModAPI.Ingame.IMyGasTank)tank).Stockpile ? 1 : 0,
					Pair = true, Enabled = gasEnabled, PowerSink = powerSink, LeakPerFrame = block.IsFunctional ? 0 : d.Capacity * d.LeakPercent / 100.0 });
				foreach (var port in sources) { port.Storage = index; port.StorageCost = 1.0 / 60; port.Kind = "Tank"; port.PowerSink = powerSink; port.Maximum = gasEnabled && !((Sandbox.ModAPI.Ingame.IMyGasTank)tank).Stockpile ? d.Capacity * 0.05 : 0; }
			}
			else if (block is MyGasFueledPowerProducer engine)
			{
				var d = engine.BlockDefinition; int index = p.Storage.Count;
				var group = Group(d.ResourceSinkGroup, false);
				p.Storage.Add(new() { Block = block.EntityId, Kind = "Engine", Resource = Resource(d.Fuel.FuelId, block), Group = d.ResourceSinkGroup.String,
					Priority = group.Priority, Amount = engine.Capacity, Capacity = d.FuelCapacity, MaxInput = d.FuelCapacity * 3, InputConversion = 1.0 / 60, Enabled = enabled });
				foreach (var port in sources) { port.Storage = index; port.StorageCost = 1.0 / d.FuelProductionToCapacityMultiplier / 60; port.Kind = "Engine"; port.Maximum = enabled ? engine.MaxOutput : 0; }
			}
			else if (block is MyReactor reactor)
			{
				foreach (var port in sources)
				{
					port.Kind = "Reactor"; port.Maximum = enabled ? reactor.MaxOutput : 0;
					foreach (var fuel in reactor.BlockDefinition.FuelInfos)
						port.Fuel.Add(Material(block, fuel.FuelId, (decimal)(fuel.ConsumptionPerSecond_Items / Math.Max(0.000001, reactor.BlockDefinition.MaxPowerOutput) / 60), reactor.GetInventory(), false));
				}
			}
			else if (block is MyGasGenerator generator)
			{
				var d = (MyOxygenGeneratorDefinition)block.BlockDefinition;
				var ice = new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Ice");
				foreach (var port in sources)
				{
					var gas = d.ProducedGases.First(g => Resource(g.Id, block) == port.Resource);
					port.Kind = "Generator"; port.PowerSink = powerSink; port.Maximum = enabled ? d.IceConsumptionPerSecond * gas.IceToGasRatio : 0;
					port.Fuel.Add(Material(block, ice, (decimal)(1.0 / gas.IceToGasRatio / 60), generator.GetInventory(), false));
				}
				if (powerSink >= 0 && enabled) p.Sinks[powerSink].Required = d.OperationalPowerConsumption;
			}
			else if (block is MyJumpDrive jump)
			{
				var d = jump.BlockDefinition; var group = Group(sink.Group, false);
				var ob = (MyObjectBuilder_JumpDrive)jump.GetObjectBuilderCubeBlock();
				p.Storage.Add(new() { Block = block.EntityId, Kind = "Jump", Resource = Resource(MyResourceDistributorComponent.ElectricityId, block),
					Group = sink.Group.String, Priority = group.Priority, Amount = ob.StoredPower, Capacity = d.PowerNeededForJump,
					MaxInput = d.RequiredPowerInput, InputConversion = d.PowerEfficiency / 216000.0, Enabled = enabled && ob.Recharging });
				p.Sinks.Add(new() { Block = block.EntityId, Resource = Resource(MyResourceDistributorComponent.ElectricityId, block), Group = sink.Group.String,
					Priority = group.Priority, Adaptible = true, Required = 0.001 });
			}
			if (block is MyWindTurbine wind && !scripted)
			{
				var shared = wind.GetOrCreateSharedComponent();
				foreach (var port in sources) port.Maximum = enabled && shared.IsEnabled && wind.IsInAtmosphere
					? wind.BlockDefinition.MaxPowerOutput * wind.PlacementEffectivity * Math.Min(1, shared.WindSpeed / wind.BlockDefinition.OptimalWindSpeed) : 0;
			}
			if (IsEnvironmental(block) && !scripted)
			{
				if (!census.Curves.TryGetValue(block.EntityId, out var curve) || !curve.IsCompletedSuccessfully)
				{ foreach (var port in sources) port.Maximum = 0; p.Unknowns.Add(block.EntityId + ":environmental curve unavailable at stop"); }
				else foreach (var port in sources)
				{
					port.Curve = curve.Result; port.Kind = "Environmental";
					if (block is MySolarPanel solar) port.Maximum = enabled ? solar.SolarPanelDefinition.MaxPowerOutput : 0;
					if (block is MyOxygenFarm farm) { port.Maximum = enabled ? farm.BlockDefinition.MaxGasOutput : 0; port.PowerSink = powerSink; }
				}
			}
			if (block is MyProductionBlock production && block is not (MyGasGenerator or MyGasTank))
			{
				var d = (MyProductionBlockDefinition)block.BlockDefinition;
				var entry = new OfflineProduction { Block = block.EntityId, Enabled = enabled, PowerSink = powerSink, Refinery = block is MyRefinery,
					TickFrames = production.m_timer?.TimerTickInFrames ?? 60,
					IdlePower = d.StandbyPowerConsumption, OperationalPower = d.OperationalPowerConsumption * (1 + block.UpgradeValues.GetValueOrDefault("Productivity")) / Math.Max(0.000001, block.UpgradeValues.GetValueOrDefault("PowerEfficiency", 1)) };
				var assembler = block as MyAssembler;
				if (assembler != null)
				{
					entry.Progress = assembler.CurrentProgress; entry.Repeat = assembler.RepeatEnabled; entry.Slave = assembler.IsSlave; entry.Disassemble = assembler.DisassembleEnabled;
					entry.Queue = production.Queue.Select(q => new OfflineQueue { Recipe = q.Blueprint.Id.ToString(), Amount = (decimal)q.Amount, ItemId = q.ItemId }).ToList();
					if (entry.Slave && entry.Queue.Count == 0) p.Confidence = "Approximate";
				}
				var blueprints = entry.Refinery ? d.BlueprintClasses.SelectMany(c => c).Distinct().ToArray() : production.Queue.Select(q => q.Blueprint).Distinct().ToArray();
				foreach (var blueprint in blueprints)
				{
					definitions.Add(blueprint.Id.ToString());
					double speed = entry.Refinery ? (((MyRefineryDefinition)d).RefineSpeed + block.UpgradeValues.GetValueOrDefault("Productivity")) * MySession.Static.RefinerySpeedMultiplier
						: (((MyAssemblerDefinition)d).AssemblySpeed + block.UpgradeValues.GetValueOrDefault("Productivity")) * MySession.Static.AssemblerSpeedMultiplier;
					double formula = entry.Refinery ? speed * 0.016 / blueprint.BaseProductionTimeInSeconds : 1000.0 / Math.Max(1, Math.Round(blueprint.BaseProductionTimeInSeconds * 1000 / speed)) / 60;
					var recipe = new OfflineRecipe { Id = blueprint.Id.ToString(), UnitsPerFrame = formula };
					double efficiency = assembler?.GetEfficiencyMultiplierForBlueprint(blueprint) ?? 1;
					foreach (var material in blueprint.Prerequisites)
						recipe.Inputs.Add(Material(block, material.Id, (decimal)material.Amount / (decimal)Math.Max(0.000001, efficiency), production.InputInventory, entry.Disassemble));
					foreach (var material in blueprint.Results)
						recipe.Outputs.Add(Material(block, material.Id, (decimal)material.Amount * (entry.Refinery ? (decimal)(((MyRefineryDefinition)d).MaterialEfficiency * block.UpgradeValues.GetValueOrDefault("Effectiveness", 1)) : 1), production.OutputInventory, !entry.Disassemble));
					if (entry.Disassemble) (recipe.Inputs, recipe.Outputs) = (recipe.Outputs, recipe.Inputs);
					string key = RateKey(block, blueprint.Id.ToString(), formula);
					if (OfflineRateCache.TryGet(key, out var cached)) { recipe.UnitsPerFrame = cached.Rate; recipe.Cached = true; }
					else if (PerBlockRate(block, key, out double localRate)) { recipe.UnitsPerFrame = localRate; recipe.Cached = true; p.Confidence = "Approximate"; }
					else p.Confidence = "Approximate";
					entry.Recipes.Add(recipe);
				}
				if (entry.Refinery)
				{
					if (entry.Recipes.SelectMany(r => r.Inputs).Select(m => m.Item).Distinct().Count() > 1 && entry.Recipes.SelectMany(r => r.Inputs).Any(m => m.Inventories.Length > 1)) p.Confidence = "Approximate";
					var order = p.Inventories.Where(i => i.Block == block.EntityId).SelectMany(i => i.Stacks).Select(s => s.Item)
						.Concat(entry.Recipes.SelectMany(r => r.Inputs).SelectMany(m => m.Inventories).Distinct().SelectMany(i => p.Inventories[i].Stacks).Select(s => s.Item)).Distinct().ToList();
					entry.Recipes = entry.Recipes.OrderBy(r => r.Inputs.Count == 0 ? int.MaxValue : order.IndexOf(r.Inputs[0].Item) is int index && index >= 0 ? index : int.MaxValue).ToList();
				}
				p.Production.Add(entry);
			}
			if (block is MySafeZoneBlock zone)
			{
				var builder = zone.GetObjectBuilderCubeBlock();
				var component = builder.ComponentContainer?.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().FirstOrDefault();
				if (component != null && enabled && ((ObjectBuilders.SafeZone.MyObjectBuilder_SafeZoneBlock)builder).SafeZoneId != 0 && !component.Activating) p.Upkeep.Add(new() { Block = block.EntityId, Enabled = enabled, RemainingFrames = Math.Max(0, component.UpkeepTime * 60 / 1000),
					PeriodFrames = zone.Definition.SafeZoneUpkeepTimeM * 3600, Chips = Material(block, new MyDefinitionId(typeof(MyObjectBuilder_Component), "ZoneChip"), zone.Definition.SafeZoneUpkeep, zone.GetInventory(), false) });
			}
			// Known-class capture above supplies stock/fuel/recipe semantics. It must not replace
			// an observed mod rate with the final instantaneous sample or the vanilla definition.
			if (observe && p.Observations.LastOrDefault(o => o.Block == block.EntityId) is { } observation)
			{
				int offset = 0;
				foreach (var port in p.Sinks.Where(s => s.Block == block.EntityId)) port.Required = observation.Mean[offset++];
				foreach (var port in sources) port.Maximum = enabled ? observation.Mean[offset++] : 0;
				foreach (var entry in p.Production.Where(p => p.Block == block.EntityId))
					entry.IdlePower = entry.OperationalPower = powerSink >= 0 ? p.Sinks[powerSink].Required : 0;
			}
		}
		if (p.Unknowns.Count > 0) p.Confidence = ClusterConfig.Get("offline.unknownBlocks") == "Freeze" || p.Unknowns.Any(x => x.Contains("cannot be separated", StringComparison.Ordinal) || x.Contains("unavailable", StringComparison.Ordinal) || x.Contains("insufficient", StringComparison.Ordinal)
			|| x.Contains("mod logic frozen", StringComparison.Ordinal) || x.Contains("topology varied", StringComparison.Ordinal)) ? "Frozen" : "Approximate";
		if (p.Creative) p.Confidence = "Creative";
		p.Definitions = definitions.OrderBy(x => x, StringComparer.Ordinal).ToArray();
		p.ContentHash = ContentHash(p.Definitions);
		OfflineModel.Validate(p);
		return p;
	}
	private static decimal Limit(double value) => !double.IsFinite(value) || value > 1e20 ? 100000000000000000000m : Math.Max(0, (decimal)value);
}
