using ClusterPlugin;
using System.Text.Json;

if (args.Length == 2 && args[0] == "--review") { ReviewChecks.Run(args[1]); return; }
if (args.Length == 1 && args[0] == "--power-budget") { PowerBudgetChecks.Run(); return; }

if (args.Length == 3 && args[0] is "--save-container" or "--solar-pivots")
{
    string plugin = Path.GetFullPath(args[1]);
    string[] probes = [Path.GetDirectoryName(plugin), Path.GetFullPath(args[2])];
    var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    using (var deps = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(plugin, ".deps.json"))))
    {
        string nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        foreach (var target in deps.RootElement.GetProperty("targets").EnumerateObject())
        foreach (var library in target.Value.EnumerateObject())
            if (library.Value.TryGetProperty("runtime", out var runtime) && deps.RootElement.GetProperty("libraries").TryGetProperty(library.Name, out var metadata)
                && metadata.TryGetProperty("path", out var packagePath))
                foreach (var asset in runtime.EnumerateObject()) packages[Path.GetFileName(asset.Name)] = Path.Combine(nuget, packagePath.GetString(), asset.Name);
    }
    AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
    {
        string name = new System.Reflection.AssemblyName(request.Name).Name + ".dll";
        string candidate = probes.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
        candidate ??= packages.GetValueOrDefault(name);
        return candidate == null || !File.Exists(candidate) ? null : System.Reflection.Assembly.LoadFrom(candidate);
    };
    var assembly = System.Reflection.Assembly.LoadFrom(plugin);
    var method = assembly.GetType("ClusterPlugin.ClusterSelfTest", throwOnError: true).GetMethod(args[0] == "--solar-pivots" ? "OfflineSolarPivots" : "OfflineSaveSections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    try { method.Invoke(null, null); }
    catch (System.Reflection.TargetInvocationException e) { throw e.InnerException; }
    Console.WriteLine(args[0] == "--solar-pivots" ? "Solar pivots: identity/rotated engine-basis checks passed; cold math only, no raycasts." : "Offline save container: 10 structural/compatibility assertions passed against the built plugin; no game process started.");
    return;
}

int checks = 0;
void Check(bool condition, string why) { if (!condition) throw new Exception(why); checks++; }
void Near(double actual, double expected, string why, double tolerance = 1e-6) => Check(Math.Abs(actual - expected) <= tolerance, $"{why}: {actual} != {expected}");
OfflineProfile Clone(OfflineProfile p) => JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(p, OfflineProfile.Json), OfflineProfile.Json);
const string E = OfflineModel.Electricity;
// These detached cases express their durations in engine frames; captured game profiles use
// 16 ms authority-clock units. The native live differential checks that conversion separately.
OfflineProfile Base() => new() { Group = 1, Members = [1], FrozenFrame = 100, SimulatedThrough = 100, ClockFrameSeconds = 1.0 / 60 };
OfflineSource Reservoir(OfflineProfile p, double amount, double capacity, double output, string kind = "Battery", string resource = E, int priority = 2)
{
    int index = p.Storage.Count;
    p.Storage.Add(new() { Block = index + 1, Kind = kind, Resource = resource, Capacity = capacity, Amount = amount, Pair = true, MaxInput = output, InputConversion = kind == "Battery" ? 1.0 / 216000 : 1.0 / 60, Mode = 2 });
    var source = new OfflineSource { Block = index + 1, Resource = resource, Priority = priority, Group = kind, Maximum = output, Storage = index, StorageCost = kind == "Battery" ? 1.0 / 216000 : 1.0 / 60 };
    p.Sources.Add(source); return source;
}
void Item(OfflineProfile p, string item, decimal amount, decimal volume = 1)
{
    p.Items[item] = new() { Volume = volume, Mass = 1 };
    if (p.Inventories.Count == 0) p.Inventories.Add(new() { Block = 1, MaxVolume = 1000, MaxMass = 1000 });
    p.Inventories[0].Stacks.Add(new() { Item = item, Amount = amount });
}
OfflineMaterial M(string item, decimal amount, params int[] inventories) => new() { Item = item, Amount = amount, Inventories = inventories.Length == 0 ? [0] : inventories };
OfflineResult Run(OfflineProfile p, long frames) => OfflineModel.Simulate(p, p.SimulatedThrough + frames, 168L * 216000);

// Priority and blackout/adaptible allocation. Lower non-adaptible groups still get unused supply.
{
    var p = Base();
    p.Sources.Add(new() { Resource = E, Group = "solar", Priority = 1, Maximum = 4 });
    p.Sinks.AddRange([new() { Resource = E, Group = "too-large", Priority = 1, Required = 5 }, new() { Resource = E, Group = "fits", Priority = 2, Required = 1 },
        new() { Resource = E, Group = "adaptive", Priority = 3, Required = 4, Adaptible = true }, new() { Resource = E, Group = "adaptive", Priority = 3, Required = 2, Adaptible = true }]);
    double[] input = new double[4], output = new double[1], charging = [];
    OfflineModel.Allocate(p, [5, 1, 4, 2], [4], input, output, charging);
    Near(input[0], 0, "blackout"); Near(input[1], 1, "lower group"); Near(input[2], 2, "adaptive 2/3"); Near(input[3], 1, "adaptive 1/3"); Near(output[0], 4, "conservation");
}
// T-OC-01 model proof: two batteries are used before reactor fuel; shared stock cannot go negative.
{
    var p = Base(); Reservoir(p, 1, 3, 12); Reservoir(p, 1, 3, 12);
    Item(p, "uranium", 10);
    p.Sources.Add(new() { Resource = E, Group = "reactor", Priority = 3, Maximum = 15, Fuel = [M("uranium", 1m / 216000)] });
    p.Sinks.Add(new() { Resource = E, Group = "lights", Required = 1 });
    Run(p, 6 * 216000);
    Near(p.Storage.Sum(s => s.Amount), 0, "batteries exhausted");
    Near((double)OfflineModel.Amount(p.Inventories[0], "uranium"), 6, "reactor supplies remaining four hours");
}
// Empty/full events and charge loss, solar-first source order, no self-charging.
{
    var p = Base(); var battery = Reservoir(p, 0, 1, 12); p.Storage[0].Mode = 1; p.Sources[0].Maximum = 0; p.Storage[0].RechargeEfficiency = 0.8;
    battery.Maximum = 0;
    p.Sources.Add(new() { Resource = E, Group = "solar", Priority = 1, Maximum = 2 });
    p.Sinks.Add(new() { Resource = E, Group = "lights", Required = 1 });
    Run(p, 216000); Near(p.Storage[0].Amount, 0.8, "recharge loss");
    Run(p, 216000); Near(p.Storage[0].Amount, 1, "capacity clamp");
}
// T-OC-02 model proof: sequential ores, input/output capacity events, no invented products at full.
{
    var p = Base(); Item(p, "ore1", 10); Item(p, "ore2", 5); Item(p, "ingot", 0);
    p.Inventories.Add(new() { Block = 2, MaxVolume = 12, MaxMass = 12 });
    p.Production.Add(new() { Block = 1, Refinery = true, Enabled = true, PowerSink = -1, Recipes = [
        new() { Id = "first", UnitsPerFrame = 1, Inputs = [M("ore1", 1)], Outputs = [M("ingot", 1, 1)] },
        new() { Id = "second", UnitsPerFrame = 1, Inputs = [M("ore2", 1)], Outputs = [M("ingot", 1, 1)] }] });
    Run(p, 100);
    Near((double)OfflineModel.Amount(p.Inventories[1], "ingot"), 12, "output stalls at capacity");
    Near((double)OfflineModel.Amount(p.Inventories[0], "ore2"), 3, "second ore remaining");
}
// Whole-unit assembler completion, missing queue entries skipped, repeat preserves ingredients.
{
    var p = Base(); Item(p, "iron", 3); Item(p, "component", 0);
    p.Production.Add(new() { Enabled = true, PowerSink = -1, Repeat = true,
        Recipes = [new() { Id = "component", UnitsPerFrame = 0.1, Inputs = [M("iron", 1)], Outputs = [M("component", 1)] }],
        Queue = [new() { Recipe = "unknown", Amount = 100 }, new() { Recipe = "component", Amount = 1 }] });
    Run(p, 100); Near((double)OfflineModel.Amount(p.Inventories[0], "component"), 3, "repeat conserves input"); Near(p.Production[0].Progress, 0, "completed progress");
}
// T-OC-03 model proof: environmental breakpoint integration and disabled rotation.
{
    var p = Base(); Reservoir(p, 0, 10, 12); p.Storage[0].Mode = 1; p.Sources[0].Maximum = 0;
    p.Sources.Add(new() { Resource = E, Priority = 1, Group = "sun", Maximum = 1, Curve = [1, 0] });
    p.SunPeriodFrames = 120; Run(p, 240); Near(p.Storage[0].Amount, 120.0 / 216000, "two cycles, half lit");
}
// T-OC-04 model proof: coupled ice -> gas -> engine stocks, shared consumption and exhaustion.
{
    var p = Base(); Item(p, "ice", 2);
    p.Storage.Add(new() { Block = 1, Kind = "Engine", Resource = "h2", Group = "engine", Amount = 100, Capacity = 100, MaxInput = 300, InputConversion = 1.0 / 60 });
    p.Sources.Add(new() { Resource = E, Group = "engine", Maximum = 2, Storage = 0, StorageCost = 1.0 / 6 });
    p.Sources.Add(new() { Resource = "h2", Group = "generator", Maximum = 20, PowerSink = 0, Fuel = [M("ice", 1m / 1200)] });
    p.Sinks.Add(new() { Resource = E, Group = "generators", Required = 0.1 }); p.Sinks.Add(new() { Resource = E, Group = "lights", Priority = 1, Required = 1 });
    Run(p, 6000); Near((double)OfflineModel.Amount(p.Inventories[0], "ice"), 0, "ice exhausted"); Near(p.Storage[0].Amount, 0, "engine exhausted");
}
// T-OC-11: three chips, one hour already paid, fourth-hour deadline disables the zone.
{
    var p = Base(); Item(p, "chip", 3);
    p.Upkeep.Add(new() { Block = 1, Enabled = true, RemainingFrames = 216000, PeriodFrames = 216000, Chips = M("chip", 1) });
    var r = Run(p, 4 * 216000); Check(!p.Upkeep[0].Enabled && r.DisabledZones.SequenceEqual([1L]), "safe zone disabled"); Near((double)OfflineModel.Amount(p.Inventories[0], "chip"), 0, "chips exhausted");
}
// T-OC-09 model proof: independent retries give identical state; applied intervals are not credited twice.
{
    var original = Base(); Reservoir(original, 1, 3, 12); original.Sinks.Add(new() { Resource = E, Required = 1, Group = "lights" });
    var a = Clone(original); var b = Clone(original); Run(a, 10000); Run(b, 10000);
    Check(JsonSerializer.Serialize(a, OfflineProfile.Json) == JsonSerializer.Serialize(b, OfflineProfile.Json), "crash retry deterministic");
    var once = JsonSerializer.Serialize(a, OfflineProfile.Json); Run(a, 0); Check(once == JsonSerializer.Serialize(a, OfflineProfile.Json), "same interval idempotent");
    var capped = Clone(original); var r = OfflineModel.Simulate(capped, capped.SimulatedThrough + 10000, 100);
    Check(r.Capped && r.Frames == 100 && r.CompensatedTo == 10100, "cap forfeits excess");
}
// Auto batteries cannot amplify energy by feeding their own recharge sinks.
{
    var p = Base(); Reservoir(p, 1, 3, 12); p.Storage[0].Mode = 0; p.Storage[0].RechargeEfficiency = 0.8;
    Run(p, 216000); Near(p.Storage[0].Amount, 1, "auto battery cannot charge itself");
    p.Sources.Add(new() { Resource = E, Group = "solar", Priority = 1, Maximum = 1 });
    p.Sinks.Add(new() { Resource = E, Group = "lights", Required = 1 });
    Run(p, 216000); Near(p.Storage[0].Amount, 1, "solar covers load before battery");
}
// Multiple products share capacity; checking each product separately would overfill this output.
{
    var p = Base(); Item(p, "ore", 10); Item(p, "a", 0); Item(p, "b", 0);
    p.Inventories.Add(new() { MaxVolume = 1, MaxMass = 1 });
    p.Production.Add(new() { Enabled = true, PowerSink = -1, Recipes = [new() { Id = "pair", UnitsPerFrame = 1,
        Inputs = [M("ore", 1)], Outputs = [M("a", 1, 1), M("b", 1, 1)] }], Queue = [new() { Recipe = "pair", Amount = 1 }] });
    Run(p, 10); Near((double)OfflineModel.Amount(p.Inventories[0], "ore"), 10, "multi-product batch stalls atomically");
    Check(p.Inventories[1].Stacks.Count == 0, "no partial products at full output");
}
// Conveyor input withdrawal can free the exact full cargo that accepts the resulting output.
{
    var p = Base(); Item(p, "ore", 10); Item(p, "ingot", 0);
    p.Inventories[0].MaxVolume = 10; p.Inventories[0].MaxMass = 10;
    p.Production.Add(new() { Refinery = true, Enabled = true, PowerSink = -1,
        Recipes = [new() { Id = "refine", UnitsPerFrame = 1, Inputs = [M("ore", 1)], Outputs = [M("ingot", 1)] }] });
    Run(p, 100); Near((double)OfflineModel.Amount(p.Inventories[0], "ore"), 0, "shared full cargo inputs consumed");
    Near((double)OfflineModel.Amount(p.Inventories[0], "ingot"), 10, "shared full cargo output conserved");
}
// Invalid profiles fail before their state can become a builder patch.
{
    var p = Base(); Reservoir(p, 1, 3, 12); p.Storage[0].Amount = double.NaN;
    bool rejected = false; try { Run(p, 1); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "non-finite stock rejected");
}
Console.WriteLine($"Offline model: {checks} assertions passed (synthetic deterministic proofs; no game differential/GUI/performance claim).");
ReviewChecks.Run("all");
PowerBudgetChecks.Run();

ReviewRound2Checks.Run();
