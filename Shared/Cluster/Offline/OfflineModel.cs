using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClusterPlugin;

/// <summary>Piecewise constant, stock-limited resource and production model. Only detached profiles
/// are mutated. Applying the result is a separate validated, transactional object-builder operation.</summary>
internal static class OfflineModel
{
	private const double Epsilon = 1e-8;
	internal const string Electricity = "MyObjectBuilder_GasProperties/Electricity";

	internal static OfflineResult Simulate(OfflineProfile profile, long wakeFrame, long maxFrames)
	{
		Validate(profile);
		if (wakeFrame < profile.SimulatedThrough || maxFrames < 0) throw new InvalidDataException("Offline clock moved backwards");
		long requested = wakeFrame - profile.SimulatedThrough;
		long frames = Math.Min(requested, maxFrames);
		double engineFramesPerClockFrame = profile.ClockFrameSeconds * 60;
		double duration = frames * engineFramesPerClockFrame;
		var result = new OfflineResult { Group = profile.Group, FromFrame = profile.SimulatedThrough, Frames = frames,
			CompensatedTo = wakeFrame, Confidence = profile.Confidence, Capped = requested > frames };
		var before = Totals(profile);
		double energy = profile.Storage.Where(s => s.Kind is "Battery" or "Jump").Sum(s => s.Amount);
		if (profile.Creative)
		{
			foreach (var store in profile.Storage) store.Amount = store.Capacity;
			profile.SimulatedThrough = wakeFrame;
			result.EnergyDelta = profile.Storage.Where(s => s.Kind is "Battery" or "Jump").Sum(s => s.Amount) - energy;
			return result;
		}
		double elapsed = 0;
		var inventories = profile.Inventories;
		while (true)
		{
			if (++result.Events > 2_000_000) throw new InvalidDataException("Offline event budget exceeded; no partial result may be applied");
			var choice = profile.Production.Select(p => Select(profile, p)).ToArray();
			double[] required = profile.Sinks.Select(s => s.Required).ToArray();
			foreach (var zone in profile.Upkeep.Where(z => z.PowerSink >= 0))
				required[zone.PowerSink] = zone.Enabled && zone.Exists && zone.ActivationRemainingFrames <= Epsilon ? zone.OperationalPower : zone.IdlePower;
			for (int i = 0; i < choice.Length; i++)
			{
				var production = profile.Production[i];
				if (production.PowerSink >= 0) required[production.PowerSink] = choice[i] == null ? production.IdlePower : production.OperationalPower;
			}
			double angleFrame = profile.SunFrame + (profile.SimulatedThrough - profile.FrozenFrame) + elapsed / engineFramesPerClockFrame;
			double[] maximum = profile.Sources.Select(s => Maximum(profile, s, angleFrame)).ToArray();
			double[] input = new double[required.Length], output = new double[maximum.Length], charging = new double[profile.Storage.Count];
			bool[] workingStorage = profile.Storage.Select(s => s.Enabled).ToArray();
			// Electricity and gas are coupled through generators/engines/farms. Iterate their power gate;
			// monotone disabling prevents an unpowered generator from supplying its own bootstrap energy.
			for (int pass = 0; pass <= profile.Sources.Count + profile.Storage.Count; pass++)
			{
				Allocate(profile, required, maximum, input, output, charging, workingStorage);
				bool changed = false;
				for (int i = 0; i < maximum.Length; i++)
				{
					int sink = profile.Sources[i].PowerSink;
					if (maximum[i] > 0 && sink >= 0 && input[sink] + Epsilon < required[sink]) { maximum[i] = 0; changed = true; }
				}
				for (int i = 0; i < workingStorage.Length; i++)
				{
					int sink = profile.Storage[i].PowerSink;
					if (workingStorage[i] && sink >= 0 && input[sink] + Epsilon < required[sink]) { workingStorage[i] = false; changed = true; }
				}
				if (!changed) break;
			}
			// Working state and chip renewal use the SAME allocated electricity as production.
			// Losing power removes the bubble and pauses its paid credit; recovery recreates it
			// with vanilla's activation delay. Neither transition switches the functional block off.
			if (UpdateZones(profile, required, input, result)) continue;
			if (elapsed + Epsilon >= duration) break;
			var changes = new Dictionary<(int Inventory, string Item), decimal>();
			var storageRate = new double[profile.Storage.Count];
			for (int i = 0; i < output.Length; i++)
			{
				var source = profile.Sources[i];
				if (source.Storage >= 0) storageRate[source.Storage] -= output[i] * source.StorageCost;
				foreach (var fuel in source.Fuel) Change(profile, changes, fuel, -(decimal)output[i] * fuel.Amount);
			}
			for (int i = 0; i < storageRate.Length; i++)
			{
				var store = profile.Storage[i];
				storageRate[i] += charging[i] * store.InputConversion - store.LeakPerFrame;
				if (store.Kind == "Battery" && storageRate[i] > 0) storageRate[i] *= store.RechargeEfficiency;
			}
			double step = duration - elapsed;
			if (profile.SunPeriodFrames > 0 && profile.Sources.Any(s => s.Curve is { Length: > 1 }))
			{
				int count = profile.Sources.Where(s => s.Curve is { Length: > 1 }).Max(s => s.Curve.Length);
				double width = profile.SunPeriodFrames / count;
				step = Math.Min(step, (width - Mod(angleFrame, width)) * engineFramesPerClockFrame);
			}
			for (int i = 0; i < choice.Length; i++)
			{
				var production = profile.Production[i];
				var recipe = choice[i];
				if (recipe == null || production.PowerSink >= 0 && input[production.PowerSink] + Epsilon < required[production.PowerSink]) { choice[i] = null; continue; }
				if (production.Refinery)
				{
					foreach (var material in recipe.Inputs) Change(profile, changes, material, -(decimal)recipe.UnitsPerFrame * material.Amount);
					foreach (var material in recipe.Outputs) Change(profile, changes, material, (decimal)recipe.UnitsPerFrame * material.Amount);
				}
				else step = Math.Min(step, Math.Max(Epsilon, (1 - production.Progress) / recipe.UnitsPerFrame));
			}
			// Revisit an infeasible recipe when upstream flow supplies ingredients or frees output
			// room. Exact single-material thresholds are events; shared/atomic capacity interactions
			// are rechecked within one captured engine production tick, rather than a whole offline span.
			foreach (var production in profile.Production.Where(p => p.Enabled))
			foreach (var recipe in production.Recipes.Where(r => !CanMake(profile, r, production.Refinery ? 0.000001m : 1m)))
			{
				decimal units = production.Refinery ? 0.000001m : 1m;
				bool improving = false;
				foreach (var material in recipe.Inputs)
				{
					decimal rate = material.Inventories.Sum(i => changes.GetValueOrDefault((i, material.Item)));
					decimal missing = material.Amount * units - Available(profile, material);
					if (missing > 0 && rate > 0) { step = Math.Min(step, (double)(missing / rate)); improving = true; }
				}
				if (recipe.Outputs.Any(m => changes.Any(c => m.Inventories.Contains(c.Key.Inventory) && c.Value < 0))) improving = true;
				if (improving) step = Math.Min(step, Math.Max(1, production.TickFrames));
			}
			foreach (var change in changes)
				if (change.Value < 0)
					step = Math.Min(step, (double)(Amount(inventories[change.Key.Inventory], change.Key.Item) / -change.Value));
			for (int i = 0; i < inventories.Count; i++)
			{
				decimal volumeRate = 0, massRate = 0;
				foreach (var change in changes.Where(c => c.Key.Inventory == i))
				{
					var item = profile.Items[change.Key.Item];
					volumeRate += change.Value * item.Volume; massRate += change.Value * item.Mass;
				}
				if (volumeRate > 0) step = Math.Min(step, (double)(Math.Max(0, inventories[i].MaxVolume - Volume(profile, i)) / volumeRate));
				if (massRate > 0) step = Math.Min(step, (double)(Math.Max(0, inventories[i].MaxMass - Mass(profile, i)) / massRate));
			}
			for (int i = 0; i < storageRate.Length; i++)
			{
				var store = profile.Storage[i];
				if (storageRate[i] > Epsilon) step = Math.Min(step, Math.Max(0, store.Capacity - store.Amount) / storageRate[i]);
				if (storageRate[i] < -Epsilon && store.Amount > Epsilon) step = Math.Min(step, store.Amount / -storageRate[i]);
				// An empty engine may fill from a powered generator/tank before it can produce power.
				if (store.Kind == "Engine" && store.Amount <= Epsilon && storageRate[i] > Epsilon) step = Math.Min(step, 1);
				// ConsumePower clears m_isFull on Update100; near full, required input also varies
				// with the remaining gap. Neither state can be held for an hour-long segment.
				if (store.Kind == "Battery" && store.Mode != 2 && (store.Full && storageRate[i] < -Epsilon
					|| !store.Full && store.Capacity - store.Amount < store.MaxInput / 2160)) step = Math.Min(step, 100);
			}
			foreach (var zone in profile.Upkeep.Where(z => z.Enabled && z.Exists))
				step = Math.Min(step, Math.Max(Epsilon, zone.ActivationRemainingFrames > Epsilon ? zone.ActivationRemainingFrames : zone.RemainingFrames));
			if (!double.IsFinite(step) || step <= 0) throw new InvalidDataException("Offline model made no progress; refusing partial compensation");
			for (int i = 0; i < output.Length; i++)
			{
				var source = profile.Sources[i];
				if (source.Resource == Electricity) result.ProducerEnergyMWh[source.Block] = result.ProducerEnergyMWh.GetValueOrDefault(source.Block) + output[i] * step / 216000;
				foreach (var fuel in source.Fuel)
				{
					if (!result.ProducerFuelConsumed.TryGetValue(source.Block, out var consumed)) result.ProducerFuelConsumed[source.Block] = consumed = [];
					consumed[fuel.Item] = consumed.GetValueOrDefault(fuel.Item) + (decimal)(output[i] * step) * fuel.Amount;
				}
			}
			for (int i = 0; i < input.Length; i++)
				if (profile.Sinks[i].Resource == Electricity) result.ConsumerEnergyMWh[profile.Sinks[i].Block] = result.ConsumerEnergyMWh.GetValueOrDefault(profile.Sinks[i].Block) + input[i] * step / 216000;
			for (int i = 0; i < charging.Length; i++)
				if (profile.Storage[i].Resource == Electricity) result.StorageInputEnergyMWh[profile.Storage[i].Block] = result.StorageInputEnergyMWh.GetValueOrDefault(profile.Storage[i].Block) + charging[i] * step / 216000;
			foreach (var change in changes) Add(inventories[change.Key.Inventory], change.Key.Item, change.Value * (decimal)step);
			for (int i = 0; i < storageRate.Length; i++)
			{
				var store = profile.Storage[i];
				store.Amount = Math.Clamp(store.Amount + storageRate[i] * step, 0, store.Capacity);
				if (store.Kind == "Battery") { if (store.Amount >= store.Capacity - Epsilon) store.Full = true; else if (store.Amount < store.Capacity - Epsilon) store.Full = false; }
			}
			for (int i = 0; i < choice.Length; i++)
			{
				var production = profile.Production[i];
				var recipe = choice[i];
				if (recipe == null) continue;
				if (production.Refinery) { result.ProductionUnits[production.Block] = result.ProductionUnits.GetValueOrDefault(production.Block) + recipe.UnitsPerFrame * step; continue; }
				production.Progress += recipe.UnitsPerFrame * step;
				if (production.Progress < 1 - Epsilon) continue;
				if (RecipeTransaction(profile, recipe, 1, true))
				{
					result.ProductionUnits[production.Block] = result.ProductionUnits.GetValueOrDefault(production.Block) + 1;
					var queued = production.Queue.First(q => q.Recipe == recipe.Id && q.Amount >= 1);
					queued.Amount--;
					if (production.Repeat) production.Queue.Add(new() { Recipe = queued.Recipe, Amount = 1, ItemId = queued.ItemId });
					production.Queue.RemoveAll(q => q.Amount <= 0);
					production.Progress = 0;
				}
				else production.Progress = Math.Min(production.Progress, 1 - Epsilon);
			}
			foreach (var zone in profile.Upkeep.Where(z => z.Enabled && z.Exists))
				if (zone.ActivationRemainingFrames > Epsilon) zone.ActivationRemainingFrames = Math.Max(0, zone.ActivationRemainingFrames - step);
				else zone.RemainingFrames = Math.Max(0, zone.RemainingFrames - step);
			elapsed += step;
		}
		profile.SimulatedThrough = wakeFrame; // the cap is deliberately forfeited, never banked for a second wake
		result.EnergyDelta = profile.Storage.Where(s => s.Kind is "Battery" or "Jump").Sum(s => s.Amount) - energy;
		foreach (var pair in Totals(profile)) result.ItemDelta[pair.Key] = pair.Value - before.GetValueOrDefault(pair.Key);
		foreach (var pair in before) if (!result.ItemDelta.ContainsKey(pair.Key)) result.ItemDelta[pair.Key] = -pair.Value;
		result.DisabledZones = profile.Upkeep.Where(z => !z.Enabled || !z.Exists || z.ActivationRemainingFrames > Epsilon).Select(z => z.Block).ToArray();
		result.Zones = profile.Upkeep.Select(z => new OfflineZoneResult { Block = z.Block, Exists = z.Exists, Activating = z.ActivationRemainingFrames > Epsilon,
			Exhausted = !z.Enabled, RemainingFrames = z.RemainingFrames, ActivationRemainingFrames = z.ActivationRemainingFrames }).ToList();
		Validate(profile);
		return result;
	}

	private static double Maximum(OfflineProfile p, OfflineSource source, double sunFrame)
	{
		if (source.Storage >= 0 && p.Storage[source.Storage].Amount <= Epsilon) return 0;
		if (source.Fuel.Any(f => Available(p, f) < 0.000001m)) return 0;
		double curve = source.Curve is { Length: > 0 } ? source.Curve[p.SunPeriodFrames <= 0 ? 0
			: Math.Min(source.Curve.Length - 1, (int)(Mod(sunFrame, p.SunPeriodFrames) / p.SunPeriodFrames * source.Curve.Length))] : 1;
		return source.Maximum * curve;
	}

	/// <summary>Priority groups: non-adaptible blackout leaves supply for lower groups, adaptible
	/// shortage is proportional. Storage pairs charge after ordinary sinks, stockpiles first.</summary>
	internal static void Allocate(OfflineProfile p, double[] demand, double[] maximum, double[] input, double[] output, double[] charging, bool[] workingStorage = null)
	{
		Array.Clear(input); Array.Clear(output); Array.Clear(charging);
		foreach (string resource in p.Sinks.Select(s => s.Resource).Concat(p.Sources.Select(s => s.Resource)).Concat(p.Storage.Select(s => s.Resource)).Distinct())
		{
			int[] sources = Enumerable.Range(0, maximum.Length).Where(i => p.Sources[i].Resource == resource).ToArray();
			double available = sources.Sum(i => maximum[i]);
			double used = 0;
			var ordinary = Enumerable.Range(0, demand.Length).Where(i => p.Sinks[i].Resource == resource)
				.Select(i => (Index: i, Priority: p.Sinks[i].Priority, Group: p.Sinks[i].Group, Rate: demand[i], Adaptible: p.Sinks[i].Adaptible, Store: false)).ToList();
			var storageDemands = Enumerable.Range(0, p.Storage.Count).Where(i => p.Storage[i].Resource == resource)
				.Select(i => (Index: i, Priority: p.Storage[i].Priority, Group: p.Storage[i].Group, Rate: workingStorage?[i] == false ? 0 : StorageDemand(p, i, output), Adaptible: true, Store: true)).ToArray();
			ordinary.AddRange(storageDemands.Where(s => !p.Storage[s.Index].Pair));
			foreach (var group in ordinary.GroupBy(s => (s.Priority, s.Group)).OrderBy(g => g.Key.Priority))
			{
				double required = group.Sum(s => s.Rate);
				double ratio = p.Simplified ? 1 : required <= available + Epsilon ? 1 : group.All(s => s.Adaptible) && required > 0 ? available / required : 0;
				foreach (var sink in group) { if (sink.Store) charging[sink.Index] = sink.Rate * ratio; else input[sink.Index] = sink.Rate * ratio; }
				double allocated = required * ratio;
				available = Math.Max(0, available - allocated); used += allocated;
			}
			void Dispatch(double needed, bool limitStorage)
			{
				foreach (var group in sources.GroupBy(i => (p.Sources[i].Priority, p.Sources[i].Group)).OrderBy(g => g.Key.Priority))
				{
					double Capacity(int i) => limitStorage && p.Sources[i].Storage >= 0 && p.Storage[p.Sources[i].Storage].Pair ? output[i] : maximum[i];
					double capacity = group.Sum(Capacity);
					double ratio = capacity <= 0 ? 0 : Math.Min(1, needed / capacity);
					foreach (int i in group) output[i] = Capacity(i) * ratio;
					needed = Math.Max(0, needed - capacity * ratio);
				}
			}
			// Stockpiles may deliberately draw from discharging storage.
			var stockpiles = storageDemands.Where(s => p.Storage[s.Index].Pair && p.Storage[s.Index].Mode == 1).ToArray();
			double stockRequired = stockpiles.Sum(s => s.Rate);
			double stockRatio = p.Simplified ? 1 : stockRequired <= 0 ? 0 : Math.Min(1, available / stockRequired);
			foreach (var sink in stockpiles) charging[sink.Index] = sink.Rate * stockRatio;
			used += stockRequired * stockRatio;
			Dispatch(used, false);
			// Auto storage can take remaining generator supply, never extra discharge of itself or a peer.
			double external = sources.Where(i => p.Sources[i].Storage < 0 || !p.Storage[p.Sources[i].Storage].Pair).Sum(i => maximum[i] - output[i]);
			var autos = storageDemands.Where(s => p.Storage[s.Index].Pair && p.Storage[s.Index].Mode != 1)
				.Select(s => s with { Rate = workingStorage?[s.Index] == false ? 0 : StorageDemand(p, s.Index, output) }).ToArray();
			double autoRequired = autos.Sum(s => s.Rate);
			double autoRatio = p.Simplified ? 1 : autoRequired <= 0 ? 0 : Math.Min(1, Math.Max(0, external) / autoRequired);
			foreach (var sink in autos) charging[sink.Index] = sink.Rate * autoRatio;
			used += autoRequired * autoRatio;
			Dispatch(used, true);
		}
	}

	private static double StorageDemand(OfflineProfile p, int index, double[] output)
	{
		var store = p.Storage[index];
		double outgoing = Enumerable.Range(0, p.Sources.Count).Where(i => p.Sources[i].Storage == index)
			.Sum(i => output[i] * p.Sources[i].StorageCost);
		if (!store.Enabled || store.Kind == "Battery" && (store.Mode == 2 || store.Full)) return 0;
		double gap = Math.Max(0, store.Capacity - store.Amount);
		if (store.Kind == "Battery") return Math.Min(store.MaxInput, gap * 2160 + outgoing * 216000);
		if (store.Kind == "Engine") return (Math.Min(gap, store.Capacity / 20) + outgoing) * 60;
		if (store.Kind == "Tank") return Math.Min(store.MaxInput, gap * 60 + outgoing * 60);
		return gap > Epsilon ? store.MaxInput : 0;
	}

	private static OfflineRecipe Select(OfflineProfile p, OfflineProduction production)
	{
		if (!production.Enabled) return null;
		if (production.Refinery)
		{
			// Recipes are ordered at capture by local input stacks, then network reachability order.
			return production.Recipes.FirstOrDefault(r => CanMake(p, r, 0.000001m));
		}
		foreach (var queued in production.Queue.Where(q => q.Amount >= 1))
		{
			var recipe = production.Recipes.FirstOrDefault(r => r.Id == queued.Recipe);
			if (recipe != null && CanMake(p, recipe, 1)) return recipe;
		}
		return null;
	}

	private static bool CanMake(OfflineProfile p, OfflineRecipe recipe, decimal units) => recipe.UnitsPerFrame > 0 && RecipeTransaction(p, recipe, units, false);

	private static bool RecipeTransaction(OfflineProfile p, OfflineRecipe recipe, decimal units, bool apply)
	{
		var stock = new Dictionary<(int Inventory, string Item), decimal>();
		var deltas = new Dictionary<(int Inventory, string Item), decimal>();
		var freeVolume = p.Inventories.Select((inv, i) => Math.Max(0, inv.MaxVolume - Volume(p, i))).ToArray();
		var freeMass = p.Inventories.Select((inv, i) => Math.Max(0, inv.MaxMass - Mass(p, i))).ToArray();
		foreach (var leg in recipe.Inputs.Select(m => (Material: m, Sign: -1)).Concat(recipe.Outputs.Select(m => (Material: m, Sign: 1))))
		{
			var material = leg.Material; var item = p.Items[material.Item];
			decimal left = material.Amount * units;
			foreach (int i in material.Inventories)
			{
				var key = (i, material.Item);
				if (!stock.ContainsKey(key)) stock[key] = Amount(p.Inventories[i], material.Item);
				decimal fits = leg.Sign < 0 ? stock[key] : Math.Min(item.Volume > 0 ? freeVolume[i] / item.Volume : decimal.MaxValue / 4,
					item.Mass > 0 ? freeMass[i] / item.Mass : decimal.MaxValue / 4);
				decimal amount = Math.Min(left, fits), delta = amount * leg.Sign;
				stock[key] += delta; deltas[key] = deltas.GetValueOrDefault(key) + delta;
				freeVolume[i] -= delta * item.Volume; freeMass[i] -= delta * item.Mass;
				left -= amount;
				if (left <= 0) break;
			}
			if (left > 0) return false;
		}
		if (apply) foreach (var delta in deltas) Add(p.Inventories[delta.Key.Inventory], delta.Key.Item, delta.Value);
		return true;
	}

	internal static decimal Amount(OfflineInventory inventory, string item) => inventory.Stacks.Where(s => s.Item == item).Sum(s => s.Amount);
	private static decimal Available(OfflineProfile p, OfflineMaterial material) => material.Inventories.Sum(i => Amount(p.Inventories[i], material.Item));
	private static decimal Volume(OfflineProfile p, int i) => p.Inventories[i].Stacks.Sum(s => s.Amount * p.Items[s.Item].Volume);
	private static decimal Mass(OfflineProfile p, int i) => p.Inventories[i].Stacks.Sum(s => s.Amount * p.Items[s.Item].Mass);
	private static decimal Fits(OfflineProfile p, OfflineMaterial m) => m.Inventories.Sum(i => Free(p, i, m.Item));
	private static decimal Free(OfflineProfile p, int i, string item)
	{
		var spec = p.Items[item]; var inventory = p.Inventories[i];
		decimal volume = spec.Volume > 0 ? Math.Max(0, inventory.MaxVolume - Volume(p, i)) / spec.Volume : decimal.MaxValue / 4;
		decimal mass = spec.Mass > 0 ? Math.Max(0, inventory.MaxMass - Mass(p, i)) / spec.Mass : decimal.MaxValue / 4;
		return Math.Min(volume, mass);
	}

	private static void Change(OfflineProfile p, Dictionary<(int, string), decimal> changes, OfflineMaterial material, decimal rate)
	{
		if (rate == 0) return;
		bool Accepts(int i)
		{
			if (rate < 0) return Amount(p.Inventories[i], material.Item) > 0;
			if (Free(p, i, material.Item) >= 0.000001m) return true;
			// A conveyor pull can free the same full cargo that receives production this segment.
			var spec = p.Items[material.Item];
			decimal volumeRate = rate * spec.Volume, massRate = rate * spec.Mass;
			foreach (var change in changes.Where(c => c.Key.Item1 == i))
			{ volumeRate += change.Value * p.Items[change.Key.Item2].Volume; massRate += change.Value * p.Items[change.Key.Item2].Mass; }
			return (Volume(p, i) < p.Inventories[i].MaxVolume || volumeRate <= 0)
				&& (Mass(p, i) < p.Inventories[i].MaxMass || massRate <= 0);
		}
		int chosen = material.Inventories.FirstOrDefault(Accepts, -1);
		if (chosen < 0) throw new InvalidDataException("Resource flow has no inventory destination");
		changes[(chosen, material.Item)] = changes.GetValueOrDefault((chosen, material.Item)) + rate;
	}

	private static void Transfer(OfflineProfile p, OfflineMaterial material, decimal amount)
	{
		foreach (int i in material.Inventories)
		{
			decimal delta = amount < 0 ? -Math.Min(-amount, Amount(p.Inventories[i], material.Item)) : Math.Min(amount, Free(p, i, material.Item));
			Add(p.Inventories[i], material.Item, delta);
			amount -= delta;
			if (amount == 0) return;
		}
		if (Math.Abs(amount) > 0.000001m) throw new InvalidDataException("Inventory transaction exceeds its stock or capacity");
	}

	private static void Add(OfflineInventory inventory, string item, decimal amount)
	{
		if (amount > 0)
		{
			var stack = inventory.Stacks.FirstOrDefault(s => s.Item == item);
			if (stack == null) inventory.Stacks.Add(new() { Item = item, Amount = amount }); else stack.Amount += amount;
			return;
		}
		foreach (var stack in inventory.Stacks.Where(s => s.Item == item))
		{
			decimal removed = Math.Min(stack.Amount, -amount);
			stack.Amount -= removed; amount += removed;
			if (amount >= 0) break;
		}
		if (amount < -0.000001m) throw new InvalidDataException("Offline stock underflow");
	}

	private static bool UpdateZones(OfflineProfile p, double[] required, double[] input, OfflineResult result)
	{
		bool changed = false;
		foreach (var zone in p.Upkeep.Where(z => z.Enabled))
		{
			bool powered = zone.PowerSink < 0 || input[zone.PowerSink] + Epsilon >= required[zone.PowerSink];
			if (!powered && zone.Exists) { zone.Exists = false; zone.ActivationRemainingFrames = 0; changed = true; }
			else if (powered && !zone.Exists && zone.RemainingFrames > Epsilon)
			{
				zone.Exists = true; zone.ActivationRemainingFrames = Math.Max(100, zone.ActivationFrames); changed = true;
			}
			else if (powered && zone.Exists && zone.ActivationRemainingFrames <= Epsilon && zone.RemainingFrames <= Epsilon)
			{
				if (Available(p, zone.Chips) >= zone.Chips.Amount)
				{
					Transfer(p, zone.Chips, -zone.Chips.Amount); zone.RemainingFrames = zone.PeriodFrames;
					result.UpkeepConsumed[zone.Block] = result.UpkeepConsumed.GetValueOrDefault(zone.Block) + zone.Chips.Amount;
				}
				else { zone.Enabled = false; zone.Exists = false; zone.RemainingFrames = 0; }
				changed = true;
			}
		}
		return changed;
	}

	private static Dictionary<string, decimal> Totals(OfflineProfile p) => p.Inventories.SelectMany(i => i.Stacks).GroupBy(s => s.Item).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));
	private static double Mod(double value, double period) => (value % period + period) % period;

	internal static void Validate(OfflineProfile p)
	{
		if (p.Schema != OfflineProfile.CurrentSchema || p.SimulatedThrough < p.FrozenFrame || p.FrozenFrame < 0
			|| !double.IsFinite(p.ClockFrameSeconds) || p.ClockFrameSeconds <= 0 || p.ClockFrameSeconds > 1
			|| !double.IsFinite(p.SunFrame) || !double.IsFinite(p.SunPeriodFrames) || p.SunPeriodFrames < 0) throw new InvalidDataException("Invalid offline profile clock/schema");
		foreach (var item in p.Items.Values) if (item.Volume < 0 || item.Mass < 0) throw new InvalidDataException("Invalid item dimensions");
		for (int i = 0; i < p.Inventories.Count; i++)
		{
			var inventory = p.Inventories[i];
			if (inventory.Stacks.Any(s => s.Amount < 0 || !p.Items.ContainsKey(s.Item))) throw new InvalidDataException("Invalid offline inventory stock");
			// Existing overfilled inventories are allowed at capture; the model must never add to them.
			if (inventory.MaxVolume < 0 || inventory.MaxMass < 0) throw new InvalidDataException("Invalid offline inventory limits");
		}
		foreach (var s in p.Storage)
			if (!double.IsFinite(s.Amount) || !double.IsFinite(s.Capacity) || s.Amount < -Epsilon || s.Amount > s.Capacity + Epsilon
				|| s.PowerSink < -1 || s.PowerSink >= p.Sinks.Count
				|| s.Capacity < 0 || !double.IsFinite(s.MaxInput) || s.MaxInput < 0 || !double.IsFinite(s.InputConversion) || s.InputConversion < 0)
				throw new InvalidDataException("Invalid offline reservoir");
		foreach (var source in p.Sources)
			if (!double.IsFinite(source.Maximum) || source.Maximum < 0 || source.Storage < -1 || source.Storage >= p.Storage.Count
				|| source.PowerSink < -1 || source.PowerSink >= p.Sinks.Count || !double.IsFinite(source.StorageCost) || source.StorageCost < 0
				|| source.Curve?.Any(v => !double.IsFinite(v) || v < 0 || v > 1) == true) throw new InvalidDataException("Invalid offline source");
		foreach (var sink in p.Sinks) if (!double.IsFinite(sink.Required) || sink.Required < 0) throw new InvalidDataException("Invalid offline sink");
		foreach (var material in p.Sources.SelectMany(s => s.Fuel).Concat(p.Production.SelectMany(b => b.Recipes).SelectMany(r => r.Inputs.Concat(r.Outputs)))
			.Concat(p.Upkeep.Select(z => z.Chips)))
			if (material == null || material.Amount <= 0 || !p.Items.ContainsKey(material.Item)
				|| material.Inventories.Any(i => i < 0 || i >= p.Inventories.Count) || material.Inventories.Distinct().Count() != material.Inventories.Length)
				throw new InvalidDataException("Invalid offline material path");
		foreach (var production in p.Production)
		{
			if (production.PowerSink < -1 || production.PowerSink >= p.Sinks.Count || !double.IsFinite(production.Progress) || production.Progress < 0 || production.Progress > 1)
				throw new InvalidDataException("Invalid production state");
			foreach (var recipe in production.Recipes) if (!double.IsFinite(recipe.UnitsPerFrame) || recipe.UnitsPerFrame <= 0) throw new InvalidDataException("Invalid production throughput");
			if (production.Queue.Any(q => q.Amount < 0)) throw new InvalidDataException("Invalid production queue");
		}
		foreach (var zone in p.Upkeep)
			if (!double.IsFinite(zone.PeriodFrames) || zone.PeriodFrames <= 0 || !double.IsFinite(zone.RemainingFrames) || zone.RemainingFrames < 0
				|| zone.PowerSink < -1 || zone.PowerSink >= p.Sinks.Count || !double.IsFinite(zone.ActivationFrames) || zone.ActivationFrames < 0
				|| !double.IsFinite(zone.ActivationRemainingFrames) || zone.ActivationRemainingFrames < 0 || !double.IsFinite(zone.OperationalPower) || zone.OperationalPower < 0
				|| !double.IsFinite(zone.IdlePower) || zone.IdlePower < 0) throw new InvalidDataException("Invalid upkeep state");
	}
}
