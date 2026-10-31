using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

internal static partial class OfflineCompensation
{
	private sealed record MeasurePlan(string Key, string Recipe, double Formula, int Epoch);
	internal sealed class Measurement
	{
		public MyProductionBlock Block;
		public string Key, Recipe;
		public double Formula, BeforeAmount;
		public uint Frames;
		public int Epoch;
		public uint[] InventoryOrder, QueueOrder;
	}
	private static readonly Dictionary<long, List<MeasurePlan>> Measurements = new();
	private static readonly HashSet<(long Block, string Key)> Attempted = new();
	private static readonly Dictionary<(long Block, string Key), double> PerBlockRates = new();

	private static string RateKey(MyCubeBlock block, string blueprint, double formula) => string.Join("|", block.BlockDefinition.Id, blueprint,
		string.Join(",", block.UpgradeValues.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value.ToString("R", CultureInfo.InvariantCulture))),
		formula.ToString("R", CultureInfo.InvariantCulture), ((MyFunctionalBlock)block).m_timer?.TimerTickInFrames ?? 0,
		ContentHash(new[] { block.BlockDefinition.Id.ToString(), blueprint }));

	private static bool ScheduleMeasurements(MyCubeGrid[] grids, OfflineProfile profile)
	{
		if (!ClusterConfig.GetBool("offline.rotateProduction")) return false;
		bool waiting = false;
		foreach (var entry in profile.Production.Where(p => p.Enabled))
		{
			var block = grids.SelectMany(g => g.GetFatBlocks()).OfType<MyProductionBlock>().First(b => b.EntityId == entry.Block);
			foreach (var recipe in entry.Recipes.Where(r => !r.Cached))
			{
				string key = RateKey(block, recipe.Id, recipe.UnitsPerFrame);
				string attemptKey = key + "@" + OfflineRateCache.Epoch(key);
				if (PerBlockRates.TryGetValue((block.EntityId, attemptKey), out double rate)) { recipe.UnitsPerFrame = rate; recipe.Cached = true; continue; }
				if (Attempted.Contains((block.EntityId, attemptKey))) continue;
				uint tick = block.m_timer?.TimerTickInFrames ?? 240;
				double units = recipe.UnitsPerFrame * tick;
				var blueprint = MyDefinitionManager.Static.GetBlueprintDefinition(MyDefinitionId.Parse(recipe.Id));
				// A partial stock/output-limited tick cannot identify an unconstrained throughput rate.
				if (blueprint.Prerequisites.Any(i => (double)block.InputInventory.GetItemAmount(i.Id) < (double)i.Amount * (units + 1))) continue;
				if (blueprint.Results.Any(i => (double)block.OutputInventory.ComputeAmountThatFits(i.Id) < (double)i.Amount * (units + 1))) continue;
				if (block is MyAssembler assembler && (assembler.DisassembleEnabled || assembler.CurrentProgress > 0
					|| !assembler.Queue.Any(q => q.Blueprint.Id == blueprint.Id && (double)q.Amount > units + 1))) continue;
				if (!Measurements.TryGetValue(block.EntityId, out var plans)) Measurements[block.EntityId] = plans = [];
				if (!plans.Any(plan => plan.Key == key)) plans.Add(new(key, recipe.Id, recipe.UnitsPerFrame, OfflineRateCache.Epoch(key)));
				waiting = true;
			}
		}
		return waiting;
	}

	internal static Measurement BeginMeasurement(MyProductionBlock block, uint frames)
	{
		try { return BeginMeasurementCore(block, frames); }
		catch (Exception e) { VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] production observation not started: " + SecretRedactor.Redact(e.Message)); return null; }
	}

	private static Measurement BeginMeasurementCore(MyProductionBlock block, uint frames)
	{
		if (!ClusterConfig.GetBool("offline.compensate") || !Measurements.TryGetValue(block.EntityId, out var plans) || plans.Count == 0
			|| ClusterRuntime.IsFrozen(block.CubeGrid.EntityId) || Readiness(block.CubeGrid) != null || !ClusterRuntime.IsServing) return null;
		var logical = MyCubeGridGroups.Static.Logical.GetGroup(block.CubeGrid);
		var group = logical == null ? new[] { block.CubeGrid } : logical.Nodes.Select(n => n.NodeData).ToArray();
		if (group.Any(g => Readiness(g) != null) || RemoteUse.BlocksProfiling(group.Select(g => g.EntityId))) return null;
		double distance = ClusterConfig.GetInt("offline.quietMeters");
		Vector3D position = block.CubeGrid.PositionComp.GetPosition();
		if (PositionRegistryService.Players.Any(p => p.IsWithinDistance(position.X, position.Y, position.Z, distance * distance))
			|| MySession.Static.Players.GetOnlinePlayers().Any(p => p.Character != null && Vector3D.DistanceSquared(p.Character.PositionComp.GetPosition(), position) < distance * distance)) return null;
		MeasurePlan plan = plans[0];
		plans.RemoveAt(0);
		Attempted.Add((block.EntityId, plan.Key + "@" + plan.Epoch));
		if (RateKey(block, plan.Recipe, plan.Formula) != plan.Key) return null;
		var blueprint = MyDefinitionManager.Static.GetBlueprintDefinition(MyDefinitionId.Parse(plan.Recipe));
		if (blueprint == null || frames == 0 || block.ResourceSink.SuppliedRatioByType(MyResourceDistributorComponent.ElectricityId) < 0.999f) return null;
		double needed = plan.Formula * frames + 1;
		if (blueprint.Prerequisites.Any(i => (double)block.InputInventory.GetItemAmount(i.Id) < (double)i.Amount * needed)
			|| blueprint.Results.Any(i => (double)block.OutputInventory.ComputeAmountThatFits(i.Id) < (double)i.Amount * needed)) return null;
		var state = new Measurement { Block = block, Key = plan.Key, Recipe = plan.Recipe, Formula = plan.Formula, Frames = frames, Epoch = plan.Epoch };
		if (block is MyRefinery refinery)
		{
			var item = blueprint.Prerequisites[0];
			var inventory = block.InputInventory.GetItems();
			int index = inventory.FindIndex(s => s.Content.GetId() == item.Id);
			if (index < 0) return null;
			state.InventoryOrder = inventory.Select(s => s.ItemId).ToArray();
			state.BeforeAmount = (double)block.InputInventory.GetItemAmount(item.Id) / (double)item.Amount;
			var first = inventory[index]; for (int i = index; i > 0; i--) inventory[i] = inventory[i - 1]; inventory[0] = first;
			refinery.m_queueNeedsRebuild = true;
		}
		else if (block is MyAssembler assembler)
		{
			if (assembler.CurrentProgress != 0 || assembler.DisassembleEnabled) return null;
			int index = assembler.m_queue.FindIndex(q => q.Blueprint.Id == blueprint.Id);
			if (index < 0) return null;
			state.QueueOrder = assembler.m_queue.Select(q => q.ItemId).ToArray();
			state.BeforeAmount = assembler.m_queue.Where(q => q.Blueprint.Id == blueprint.Id).Sum(q => (double)q.Amount);
			if ((double)assembler.m_queue[index].Amount <= needed) return null;
			var first = assembler.m_queue[index]; for (int i = index; i > 0; i--) assembler.m_queue[i] = assembler.m_queue[i - 1]; assembler.m_queue[0] = first;
			assembler.m_currentQueueItem = null;
			assembler.CurrentItemIndex = 0;
		}
		return state;
	}

	internal static void FinishMeasurement(Measurement state, bool succeeded)
	{
		if (state == null) return;
		var block = state.Block;
		double units = 0;
		try
		{
			if (block is MyRefinery refinery)
			{
				var recipe = MyDefinitionManager.Static.GetBlueprintDefinition(MyDefinitionId.Parse(state.Recipe));
				var item = recipe.Prerequisites[0];
				units = state.BeforeAmount - (double)block.InputInventory.GetItemAmount(item.Id) / (double)item.Amount;
				var items = block.InputInventory.GetItems();
				items.Sort((a, b) => Order(state.InventoryOrder, a.ItemId).CompareTo(Order(state.InventoryOrder, b.ItemId)));
				refinery.m_queueNeedsRebuild = true;
			}
			else if (block is MyAssembler assembler)
			{
				units = state.BeforeAmount - assembler.m_queue.Where(q => q.Blueprint.Id.ToString() == state.Recipe).Sum(q => (double)q.Amount) + assembler.CurrentProgress;
				uint? current = assembler.m_currentQueueItem?.ItemId;
				assembler.m_queue.Sort((a, b) => Order(state.QueueOrder, a.ItemId).CompareTo(Order(state.QueueOrder, b.ItemId)));
				if (current.HasValue) assembler.CurrentItemIndex = assembler.m_queue.FindIndex(q => q.ItemId == current.Value);
			}
			if (succeeded && units > 0 && OfflineRateCache.Epoch(state.Key) == state.Epoch)
			{
				double rate = units / state.Frames;
				PerBlockRates[(block.EntityId, state.Key + "@" + state.Epoch)] = rate;
				OfflineRateCache.Measured(state.Key, state.Formula, rate, state.Epoch);
			}
		}
		catch (Exception e) { VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] production observation failed: " + SecretRedactor.Redact(e.Message)); }
	}
	private static bool PerBlockRate(MyCubeBlock block, string key, out double rate) => PerBlockRates.TryGetValue((block.EntityId, key + "@" + OfflineRateCache.Epoch(key)), out rate);

	private static int Order(uint[] order, uint item) { int index = Array.IndexOf(order, item); return index < 0 ? int.MaxValue : index; }
}
