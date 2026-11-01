using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;

namespace ClusterPlugin;

internal static partial class OfflineCompensation
{
	private sealed class Census
	{
		public long Group, Since, LastSample, ObservationSince;
		public ulong Partition;
		public uint Generation;
		public MyCubeGrid[] Entities;
		public long[] Members;
		public string Signature, EnvironmentSignature;
		public readonly Dictionary<long, Task<double[]>> Curves = new();
		public readonly Dictionary<long, List<double[]>> Observations = new();
		public readonly Dictionary<long, Dictionary<string, decimal>> ReactorDebits = new();
		public string State = "settling";
		public OfflineProfile Last;
	}
	private static readonly Dictionary<long, Census> CensusByGroup = new();
	private static long m_lastObservationAdmissionFrame;
	private static MySession m_profileSession;
	private static void EnsureProfilingSession()
	{
		if (ReferenceEquals(m_profileSession, MySession.Static)) return;
		m_profileSession = MySession.Static;
		CensusByGroup.Clear(); Measurements.Clear(); Attempted.Clear(); PerBlockRates.Clear(); Results.Clear();
		m_lastObservationAdmissionFrame = Frame - 100;
	}


	internal static string Readiness(MyCubeGrid grid)
	{
		if (MyEncounterGenerator.Static?.IsEncounter(grid) == true || MySession.Static.GetComponent<MyNeutralShipSpawner>()?.IsEncounter(grid.EntityId) == true
			|| grid.GameLogic?.GetAs<MyGlobalEncounterComponent>() != null) return "encounter";
		if (grid.GridSystems?.ControlSystem?.IsControlled == true) return "controlled";
		if (grid.GridSystems?.JumpSystem?.IsJumping == true) return "jumping";
		if (grid.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>()?.ControlThrust.LengthSquared() > 0.000001f
			|| grid.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>()?.AutoPilotControlThrust.LengthSquared() > 0.000001f) return "thrust";
		if (grid.Physics != null && (grid.Physics.LinearVelocity.LengthSquared() > 0.0025f || grid.Physics.AngularVelocity.LengthSquared() > 0.0025f
			|| grid.Physics.LinearAcceleration.LengthSquared() > 0.0025f || grid.Physics.AngularAcceleration.LengthSquared() > 0.0025f)) return "moving";
		foreach (var block in grid.GetFatBlocks())
		{
			if (block is MyRemoteControl remote && remote.m_autopilotComponent?.AutoPilotEnabled == true) return "autopilot";
			if (block.Components.TryGet<MyAutopilotComponent>(out var autopilot) && autopilot.AutoPilotEnabled) return "autopilot";
			if (block is MyThrust thrust && thrust.Enabled && thrust.ThrustOverride > 0) return "thrust";
			if (block is MyGyro gyro && gyro.Enabled && gyro.GyroOverride) return "gyro";
			if (block is MyMotorStator rotor && rotor.Enabled && !((Sandbox.ModAPI.Ingame.IMyMotorStator)rotor).RotorLock && Math.Abs(rotor.TargetVelocityRPM) > 0.00001f) return "rotor";
			if (block is MyPistonBase piston && piston.Enabled && ((float)piston.Velocity > 0 && piston.CurrentPosition < (float)piston.MaxLimit - 0.00001f
				|| (float)piston.Velocity < 0 && piston.CurrentPosition > (float)piston.MinLimit + 0.00001f)) return "piston";
			if (block is MyMotorSuspension wheel && wheel.Enabled && Math.Abs(wheel.PropulsionOverride) > 0.00001f) return "wheel";
		}
		return null;
	}

	private static MyCubeGrid[][] Groups(PartitionManager.Partition partition)
	{
		var grids = partition.Members.Select(id => MyEntities.GetEntityById(id)).OfType<MyCubeGrid>().OrderBy(g => g.EntityId).ToArray();
		var remaining = grids.Select(g => g.EntityId).ToHashSet();
		var groups = new List<MyCubeGrid[]>();
		foreach (var grid in grids)
		{
			if (!remaining.Contains(grid.EntityId)) continue;
			var logical = MyCubeGridGroups.Static.Logical.GetGroup(grid);
			var group = logical == null ? new[] { grid } : logical.Nodes.Select(n => n.NodeData).OrderBy(g => g.EntityId).ToArray();
			if (group.Any(g => !partition.Members.Contains(g.EntityId))) throw new InvalidOperationException("Logical group crosses partition boundary");
			foreach (var member in group) remaining.Remove(member.EntityId);
			groups.Add(group);
		}
		return groups.ToArray();
	}

	private static string Signature(MyCubeGrid[] grids)
	{
		var text = new StringBuilder();
		text.Append(MyFinalBuildConstants.APP_VERSION_STRING).Append('|').Append(ClusterConfig.GetInt("offline.solarSteps"))
			.Append('|').Append(MySector.SunProperties.BaseSunDirectionNormalized).Append('|').Append(MySector.SunProperties.SunRotationAxis)
			.Append('|').Append(MySession.Static.Settings.EnableSunRotation)
			.Append('|').Append(string.Join(",", MySession.Static.Mods.Select(m => m.PublishedFileId + ":" + m.PublishedServiceName + ":" + m.Name)));
		foreach (var grid in grids)
		{
			text.Append(grid.EntityId).Append(':').Append(grid.WorldMatrix.ToString());
			// Armor and partially built blocks occlude too; a fat-block-only census misses these edits.
			foreach (var slim in grid.GetBlocks().OrderBy(b => b.Position.ToString(), StringComparer.Ordinal))
				text.Append(';').Append(slim.Position).Append(':').Append(slim.BlockDefinition.Id).Append(':').Append(slim.Orientation).Append(':').Append(slim.BuildLevelRatio);
			foreach (var block in grid.GetFatBlocks().OrderBy(b => b.EntityId))
			{
				text.Append('|').Append(block.EntityId).Append(':').Append(block.BlockDefinition.Id).Append(':').Append(block.IsFunctional);
				if (block is MyFunctionalBlock functional) text.Append(':').Append(functional.Enabled);
				foreach (var upgrade in block.UpgradeValues.OrderBy(v => v.Key)) text.Append(':').Append(upgrade.Key).Append('=').Append(upgrade.Value.ToString("R", CultureInfo.InvariantCulture));
				if (block is MyGyro gyro) text.Append(':').Append(gyro.RequiredPowerInput.ToString("R", CultureInfo.InvariantCulture));
				if (block is MyBatteryBlock battery) text.Append(':').Append(battery.ChargeMode);
				if (block is MyGasTank tank) text.Append(':').Append(((Sandbox.ModAPI.Ingame.IMyGasTank)tank).Stockpile);
				if (block is MyProductionBlock production)
				{
					text.Append(':').Append(production.UseConveyorSystem);
					foreach (var recipe in production.Queue.Select(q => q.Blueprint.Id).Distinct().OrderBy(id => id.ToString())) text.Append(':').Append(recipe);
					if (block is MyAssembler assembler) text.Append(':').Append(assembler.DisassembleEnabled).Append(':').Append(assembler.RepeatEnabled).Append(':').Append(assembler.IsSlave);
				}
			}
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
	}

	private static string OccluderSignature(PartitionManager.Partition partition)
	{
		var text = new StringBuilder();
		foreach (var grid in partition.Members.Select(id => MyEntities.GetEntityById(id)).OfType<MyCubeGrid>().OrderBy(g => g.EntityId))
		{
			text.Append(grid.EntityId).Append(':').Append(grid.WorldMatrix);
			foreach (var slim in grid.GetBlocks().OrderBy(b => b.Position.ToString(), StringComparer.Ordinal))
				text.Append(';').Append(slim.Position).Append(':').Append(slim.BlockDefinition.Id).Append(':').Append(slim.Orientation).Append(':').Append(slim.BuildLevelRatio);
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
	}

	/// <summary>Called during grace, independently per quiet group. A moving sibling never discards a
	/// completed group's curve or throughput measurements. No grid is frozen during sampling.</summary>
	internal static bool Prepare(PartitionManager.Partition partition, bool quick = false)
	{
		EnsureProfilingSession();
		if (!ClusterConfig.GetBool("offline.compensate")) return true;
		if (!ContentReady || ClusterRuntime.IsClustered && !ClusterRuntime.CanProfile(partition.Id)) return false;
		bool complete = true;
		string environment = OccluderSignature(partition);
		foreach (var grids in Groups(partition))
		{
			long group = grids.Min(g => g.EntityId);
			if (grids.Any(g => Readiness(g) != null)) { complete = false; continue; }
			string signature = Signature(grids);
			if (!CensusByGroup.TryGetValue(group, out var census) || census.Signature != signature
				|| census.Entities == null || !census.Entities.SequenceEqual(grids)
				|| census.Partition != partition.Id || ClusterRuntime.IsClustered && !ClusterRuntime.CanProfile(partition.Id, census.Generation))
			{
				census = new() { Group = group, Members = grids.Select(g => g.EntityId).ToArray(), Since = Frame, Signature = signature,
					Entities = grids, Partition = partition.Id, Generation = ClusterRuntime.PluginOwnershipSnapshot().TryGetValue(partition.Id, out long generation) ? (uint)generation : 0 };
				CensusByGroup[group] = census;
			}
			if (census.EnvironmentSignature != environment) { census.EnvironmentSignature = environment; census.Curves.Clear(); }
			if (MySession.Static.CreativeMode)
			{
				census.Last = Capture(grids, census, quick: true); census.Last.Confidence = "Creative"; census.State = "complete"; continue;
			}
			if (!quick && Frame - census.Since < ClusterConfig.GetInt("offline.settleFrames")) { complete = false; continue; }
			foreach (var block in grids.SelectMany(g => g.GetFatBlocks()))
			{
				if (IsEnvironmental(block) && !Scripted(block) && !census.Curves.ContainsKey(block.EntityId)) census.Curves[block.EntityId] = SampleSun(block);
				if ((Scripted(block) || ClusterConfig.Get("offline.unknownBlocks") == "Mean" && block is MyProgrammableBlock or MyTimerBlock or MyShipConnector) && !census.Observations.ContainsKey(block.EntityId))
				{ census.Observations[block.EntityId] = []; census.ObservationSince = Frame; }
			}
			if (Frame - census.LastSample >= 100)
			{
				census.LastSample = Frame;
				foreach (var block in grids.SelectMany(g => g.GetFatBlocks()).Where(b => census.Observations.ContainsKey(b.EntityId)))
					census.Observations[block.EntityId].Add(ReadRates(block));
			}
			if (census.Curves.Values.Any(t => !t.IsCompletedSuccessfully)) { census.State = "environmental"; complete = false; continue; }
			if (!quick && census.Observations.Count > 0 && Frame - census.ObservationSince < RequiredObservationFrames)
			{ census.State = "observing"; complete = false; continue; }
			census.Last = Capture(grids, census, quick);
			if (!quick && ScheduleMeasurements(grids, census.Last)) { census.State = "production"; complete = false; continue; }
			if (census.Last.Unknowns.Count > 0 && ClusterConfig.Get("offline.unknownBlocks") == "Block") { census.State = "unknown-blocked"; complete = false; continue; }
			census.State = "complete";
		}
		// Closed groups must not pin entity references or retain profiles forever.
		foreach (long closed in CensusByGroup.Keys.Where(id => MyEntities.GetEntityById(id) == null).ToArray()) CensusByGroup.Remove(closed);
		return complete;
	}

	internal static OfflineProfile[] CaptureForDrain(PartitionManager.Partition partition)
	{
		try { return CaptureFor(partition, quick: true); }
		catch (Exception e)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain compensation profile omitted for " + partition.Id + ": " + SecretRedactor.Redact(e.Message));
			return null; // profiling must never prevent the drain's durable Final
		}
	}

	internal static OfflineProfile[] CaptureFor(PartitionManager.Partition partition, bool quick = false)
	{
		if (!ClusterConfig.GetBool("offline.compensate")) return null;
		if (quick && !ContentReady) { MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain compensation profile omitted: mod content hash not ready"); return null; }
		var profiles = new List<OfflineProfile>();
		string environment = OccluderSignature(partition);
		foreach (var grids in Groups(partition))
		{
			long group = grids.Min(g => g.EntityId);
			if (grids.Any(g => Readiness(g) != null)) continue; // a cluster stop may also save ships in flight
			if (!CensusByGroup.TryGetValue(group, out var census) || census.Signature != Signature(grids) || census.EnvironmentSignature != environment)
			{
				if (!quick) throw new InvalidOperationException("Profile invalidated before freeze");
				// A stop's unprofiled environmental/scripted blocks freeze. Other stocks still compensate.
				census = new() { Group = group, Members = grids.Select(g => g.EntityId).ToArray(), Since = Frame, Signature = Signature(grids) };
			}
			var profile = Capture(grids, census, quick);
			if (!quick && census.State != "complete") throw new InvalidOperationException("Profile is not complete before freeze");
			profiles.Add(profile);
		}
		return profiles.ToArray();
	}

	private static long RequiredObservationFrames => Math.Max(ClusterConfig.GetInt("offline.observeSeconds") * 60L,
		ClusterConfig.Get("offline.longObservation") == "InPlace" ? (long)(MySession.Static.Settings.SunRotationIntervalMinutes * 60000.0 / 16) : 0);

	private static object ProfilingStatus() => CensusByGroup.Values.Select(c => new { group = c.Group, members = c.Members, state = c.State,
		frames = Frame - c.Since, observationFrames = Frame - c.ObservationSince, requiredObservationFrames = RequiredObservationFrames, samples = c.Observations.Values.Select(s => s.Count).ToArray(), observed = c.Observations.Count, confidence = c.Last?.Confidence, unknowns = c.Last?.Unknowns }).ToArray();
	internal static object Profiles(ulong partition, PartitionManager manager) => CensusByGroup.Values.Where(c => c.Members.Any(id => manager.Peek(partition).Members.Contains(id))).Select(c => c.Last).Where(p => p != null).ToArray();

	// Operator reads consume existing census/Final state. They never Evaluate, Capture or profile.
	internal static object AdminRead(ulong partition, PartitionManager manager, OfflineProfile[] offline = null)
	{
		var members = offline == null ? manager.Peek(partition).Members.ToHashSet() : offline.SelectMany(p => p.Members).ToHashSet();
		var census = CensusByGroup.Values.Where(c => c.Members.Any(members.Contains)).ToArray();
		var profiles = offline ?? census.Select(c => c.Last).Where(p => p != null).ToArray();
		return new
		{
			capability = "v1",
			groups = profiles.Select(p => new { group = p.Group, members = p.Members,
				state = offline == null ? census.FirstOrDefault(c => c.Group == p.Group)?.State : "offline",
				confidence = p.Confidence, unknowns = p.Unknowns, frozenFrame = p.FrozenFrame, simulatedThrough = p.SimulatedThrough,
				observations = p.Observations }).Concat(census.Where(c => offline == null && c.Last == null).Select(c => new
				{ group = c.Group, members = c.Members, state = c.State, confidence = (string)null, unknowns = new List<string>(),
					frozenFrame = 0L, simulatedThrough = 0L, observations = new List<OfflineObservation>() })).ToArray(),
			results = Results.Values.Where(r => members.Contains(r.Group)).ToArray(),
		};
	}

	private static bool Scripted(MyCubeBlock block)
	{
		if (block.GameLogic == null) return false;
		return HasModLogic(block.GameLogic);
	}
	private static bool HasModLogic(MyGameLogicComponent logic)
	{
		if (logic == null) return false;
		string assembly = logic.GetType().Assembly.GetName().Name;
		if (assembly is not ("Sandbox.Game" or "SpaceEngineers.Game" or "VRage.Game")) return true;
		if (logic is MyCompositeGameLogicComponent composite) return composite.GetComponents().Any(HasModLogic);
		return false;
	}
	internal static void UpdateObservations()
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority) return;
		EnsureProfilingSession();
		if (MySession.Static?.Ready != true) return;
		long frame = Frame;
		if (frame - m_lastObservationAdmissionFrame < 100) return;
		m_lastObservationAdmissionFrame = frame;
		foreach (var census in CensusByGroup.Values.ToArray())
		{
			if (frame - census.LastSample < 100) continue;
			if (!ClusterConfig.GetBool("offline.compensate") || census.Entities == null
				|| !ClusterRuntime.CanProfile(census.Partition, census.Generation)
				|| census.Entities.Any(g => g.MarkedForClose || !ReferenceEquals(MyEntities.GetEntityById(g.EntityId), g) || Readiness(g) != null))
			{
				CensusByGroup.Remove(census.Group);
				foreach (long block in census.Entities?.SelectMany(g => g.GetFatBlocks()).Select(b => b.EntityId) ?? Enumerable.Empty<long>())
					Measurements.Remove(block);
				continue;
			}
			if (census.State == "complete" || census.Observations.Count == 0) continue;
			census.LastSample = frame;
			foreach (long id in census.Observations.Keys)
				if (MyEntities.GetEntityById(id) is MyCubeBlock block && !block.MarkedForClose) census.Observations[id].Add(ReadRates(block));
		}
	}

	private static double[] ReadRates(MyCubeBlock block)
	{
		var values = new List<double>();
		if (block.Components.TryGet<MyResourceSinkComponent>(out var sink)) foreach (var type in sink.AcceptedResources) values.Add(sink.RequiredInputByType(type));
		if (block.Components.TryGet<MyResourceSourceComponent>(out var source)) foreach (var type in source.ResourceTypes) values.Add(source.MaxOutputByType(type));
		if (source != null) foreach (var type in source.ResourceTypes) values.Add(source.CurrentOutputByType(type));
		for (int i = 0; i < block.InventoryCount; i++)
		{
			var census = block is MyReactor && i == 0 && !block.GetInventory(i).GetItems().Any(item => item.Content is MyObjectBuilder_GasContainerObject)
				? CensusByGroup.Values.FirstOrDefault(c => c.Observations.ContainsKey(block.EntityId)) : null;
			string stock;
			if (census != null)
			{
				// Only debits observed at the guarded native ConsumeFuel boundary
				// are normalized. Conveyor pulls and arbitrary mod edits stay visible.
				var amounts = block.GetInventory(i).GetItems().GroupBy(item => item.Content.GetId().ToString())
					.ToDictionary(g => g.Key, g => g.Sum(item => (decimal)item.Amount));
				if (census.ReactorDebits.TryGetValue(block.EntityId, out var debits))
					foreach (var debit in debits) amounts[debit.Key] = amounts.GetValueOrDefault(debit.Key) + debit.Value;
				stock = string.Join("|", amounts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value.ToString("G29", CultureInfo.InvariantCulture)));
			}
			else stock = string.Join("|", block.GetInventory(i).GetItems().Select(item => item.ItemId + ":" + item.Content.GetId() + ":" + ((decimal)item.Amount).ToString(CultureInfo.InvariantCulture)
				+ (item.Content is MyObjectBuilder_GasContainerObject gas ? ":" + gas.GasLevel.ToString("R", CultureInfo.InvariantCulture) : "")));
			values.Add(BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(stock))) & ((1UL << 53) - 1));
		}
		return values.ToArray();
	}

	internal static Action ObserveReactorFuel(MyReactor reactor, float milliseconds)
	{
		// Observation must not interfere with the engine if it is unavailable.
		try
		{
			var census = CensusByGroup.Values.FirstOrDefault(c => c.Observations.ContainsKey(reactor.EntityId));
			if (census == null || Sandbox.Engine.Utils.MyFakes.ENABLE_INFINITE_REACTOR_FUEL
				|| !reactor.SourceComp.HasCapacityRemaining || reactor.SourceComp.CurrentOutput == 0) return null;
			var inventory = reactor.GetInventory();
			float ratio = reactor.SourceComp.CurrentOutput / reactor.BlockDefinition.MaxPowerOutput;
			var debits = reactor.BlockDefinition.FuelInfos.Select(f =>
			{
				// Exact guarded native fixed-point debit, including its minimum quantum.
				float rate = ratio * f.ConsumptionPerSecond_Items / 1000f;
				var amount = (VRage.MyFixedPoint)(milliseconds * rate);
				if (amount == 0) amount = VRage.MyFixedPoint.SmallestPossibleValue;
				return (Item: f.FuelId.ToString(), Amount: (decimal)VRage.MyFixedPoint.Min(inventory.GetItemAmount(f.FuelId), amount));
			}).ToArray();
			return () =>
			{
				if (!census.ReactorDebits.TryGetValue(reactor.EntityId, out var totals)) census.ReactorDebits[reactor.EntityId] = totals = new();
				foreach (var debit in debits) totals[debit.Item] = totals.GetValueOrDefault(debit.Item) + debit.Amount;
			};
		}
		catch { return null; } // The unchanged inventory fingerprint then freezes this block.
	}
}
