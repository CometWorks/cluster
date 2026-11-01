using System.Collections.Generic;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>Versioned, detached state of one closed logical group. Rates are per engine frame,
/// except resource ports (MW or gas/s). No wall-clock timestamps participate in simulation.</summary>
internal sealed class OfflineProfile
{
	public const int CurrentSchema = 1;
	public static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
	public int Schema = CurrentSchema;
	public long Group;
	public long[] Members = [];
	public string ContentHash;
	public string[] Definitions = [];
	public long FrozenFrame, SimulatedThrough;
	// Existing captured clocks use floor(authority milliseconds / 16). Resource/timer rates
	// use native 60 Hz engine frames; keep the conversion explicit at the model boundary.
	public double ClockFrameSeconds = 0.016;
	public double SunFrame, SunPeriodFrames;
	public double ResourceFrameRemaining = 1;
	public bool Creative, Simplified;
	public string Confidence = "Exact";
	public List<string> Unknowns = [];
	public List<OfflineInventory> Inventories = [];
	public Dictionary<string, OfflineItem> Items = [];
	public List<OfflineSink> Sinks = [];
	public List<OfflineSource> Sources = [];
	public List<OfflineStorage> Storage = [];
	public List<OfflineProduction> Production = [];
	public List<OfflineUpkeep> Upkeep = [];
	public List<OfflineObservation> Observations = [];
}
internal sealed class OfflineItem
{
	public decimal Volume, Mass;
	public bool Integral;
}
internal sealed class OfflineStack
{
	public string Item;
	public decimal Amount;
}
internal sealed class OfflineInventory
{
	public long Block;
	public int Index;
	public string InventoryId;
	public decimal MaxVolume, MaxMass;
	public List<OfflineStack> Stacks = [];
}
internal sealed class OfflineMaterial
{
	public string Item;
	public decimal Amount;
	public int[] Inventories = [];
}
internal sealed class OfflineSink
{
	public long Block;
	public string Resource, Group;
	public int Priority;
	public bool Adaptible, Powered;
	public double Required;
}
internal sealed class OfflineSource
{
	public long Block;
	public string Resource, Group, Kind;
	public int Priority;
	public double Maximum;
	public double[] Curve;
	// The reservoir expended by one resource-unit of output for one engine frame.
	public int Storage = -1;
	public double StorageCost;
	// Inventory material expended by one resource-unit of output for one frame.
	public List<OfflineMaterial> Fuel = [];
	// Native reactor settlement samples delivered output at its timer boundary.
	// Zero keeps the continuous behavior of profiles captured by older packages.
	public double FuelTickFrames, FuelElapsedFrames, FuelTier2TickFrames;
	public double FuelTier2AfterFrames = -1;
	public int PowerSink = -1;
	// A gas producer's one electrical sink follows delivered gas, shared by all
	// its output ports. -1 preserves the captured constant demand of older profiles.
	public double IdlePower = -1, OperationalPower;
	// Native gas source enablement follows the previous electrical update; demand follows gas output.
	public bool PowerFeedback, Producing;
}
internal sealed class OfflineStorage
{
	public long Block;
	public string Kind, Resource, Group;
	public double Amount, Capacity, MaxInput, InputConversion, LeakPerFrame;
	public int Priority, Mode; // battery: Auto=0, Recharge=1, Discharge=2; tank stockpile=1
	public int PowerSink = -1;
	public bool Pair, Enabled = true, Full;
	public double RechargeEfficiency = 1;
	public double IdlePower = -1, OperationalPower;
	public bool Transferring; // retained operational demand, reset by native flow/working callbacks
	// Native batteries sample current input/output on Update100, like reactor fuel.
	public double TickFrames, ElapsedFrames, AccountedEnergy;
	public bool Sampling;
}
internal sealed class OfflineRecipe
{
	public string Id;
	public double UnitsPerFrame;
	public bool Cached;
	public List<OfflineMaterial> Inputs = [], Outputs = [];
}
internal sealed class OfflineQueue
{
	public string Recipe;
	public decimal Amount;
	public uint ItemId;
}
internal sealed class OfflineProduction
{
	public long Block;
	public bool Refinery, Repeat, Slave, Disassemble, Enabled;
	public int PowerSink;
	public uint TickFrames = 60;
	public double IdlePower, OperationalPower, Progress;
	public List<OfflineRecipe> Recipes = [];
	public List<OfflineQueue> Queue = [];
}
internal sealed class OfflineUpkeep
{
	public long Block;
	public double RemainingFrames, PeriodFrames;
	public bool Enabled;
	public int PowerSink = -1;
	public bool Exists = true;
	public double OperationalPower, IdlePower, ActivationFrames, ActivationRemainingFrames;
	public OfflineMaterial Chips;
}
internal sealed class OfflineResult
{
	public long Group, FromFrame, CompensatedTo, Frames;
	public int Events;
	public bool Capped;
	public string Confidence, Skipped;
	public double EnergyDelta;
	public Dictionary<string, decimal> ItemDelta = [];
	public Dictionary<long, double> ProducerEnergyMWh = [], ConsumerEnergyMWh = [], StorageInputEnergyMWh = [];
	public Dictionary<long, Dictionary<string, decimal>> ProducerFuelConsumed = [];
	public Dictionary<long, double> ProductionUnits = [];
	public Dictionary<long, decimal> UpkeepConsumed = [];
	public List<OfflineZoneResult> Zones = [];
	public long[] DisabledZones = [];
}

internal sealed class OfflineZoneResult
{
	public long Block;
	public bool Exists, Activating, Exhausted;
	public double RemainingFrames, ActivationRemainingFrames;
}

internal sealed class OfflineObservation
{
	public long Block;
	public int Samples;
	public double[] Mean, Minimum, Maximum, Variance;
}
