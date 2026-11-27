using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModCompatScan;

// SE-1840: every public member of the game's mod API has exactly one row in Docs/ModApiPolicy.tsv, every row is
// well formed, and no row names a member the game no longer has. Exit 0 = pass, 1 = fail, 2 = no game found.
// See "Member policy table" in Docs/ModCompat.md for the other modes and for regenerating the table.

string dir =
    args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("DEDICATED64")
    ?? Environment.GetEnvironmentVariable("SPACE_ENGINEERS_BIN64")
    ?? Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? ".",
        ".steam/steam/steamapps/common/SpaceEngineersDedicatedServer/DedicatedServer64"
    );
if (!File.Exists(Path.Combine(dir, "Sandbox.Game.dll")))
{
    Console.Error.WriteLine($"no Sandbox.Game.dll in {dir}: pass the DedicatedServer64 folder");
    return 2;
}

string mode = args.Length == 3 ? args[1] : null;
if (!(args.Length <= 1 || mode is "--scan" or "--check-corpus" or "--facts"))
{
    Console.Error.WriteLine(
        "usage: ModApiPolicyChecks [DEDICATED64 [--scan|--check-corpus WORKSHOP_CONTENT | --facts DECOMPILED]]"
    );
    return 2;
}
var empty = CSharpCompilation.Create(
    "WorkshopMod",
    references: ScriptReferences.References(dir),
    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
);
if (mode == "--scan")
{
    foreach (
        var entry in CorpusUsage
            .Read(args[2], empty)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
    )
        Console.WriteLine($"{entry.Key}\t{entry.Value}");
    return 0;
}

var corpus = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "CorpusUsage.tsv"))
    .Select(line => line.Split('\t'))
    .ToDictionary(f => f[0], f => int.Parse(f[1]), StringComparer.Ordinal);
if (mode == "--facts")
{
    Facts.Write(empty, corpus.Keys.ToHashSet(StringComparer.Ordinal), args[2], Console.Out);
    return 0;
}

var errors = new List<string>();
var policies = new HashSet<string>
{
    "local",
    "replicated",
    "node-scoped",
    "per-node",
    "affinity",
    "client",
};
var harmless = new HashSet<string> { "local", "replicated", "client" };
var breakage = new Regex(@"^(-|B[1-8](,B[1-8])*)$");
var fileLine = new Regex(@"\w+\.cs:\d+");

var table = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ModApiPolicy.tsv"));
if (table[0] != "member\tpolicy\tbreakage\tshim\tchannel\ton\tmods\treason")
    errors.Add($"unexpected header: {table[0]}");
var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
foreach (string line in table.Skip(1))
{
    string[] f = line.Split('\t');
    if (f.Length != 8)
    {
        errors.Add($"not 8 fields: {line}");
        continue;
    }
    string member = f[0];
    if (!rows.TryAdd(member, f))
        errors.Add($"duplicate row: {member}");
    if (!policies.Contains(f[1]))
        errors.Add($"unknown policy {f[1]}: {member}");
    bool unverified = f[7].Contains("UNCERTAIN");
    bool pendingReview = unverified && f[1] == "node-scoped" && f[2] == "?" && f[3] == "?";
    if (!pendingReview && !breakage.IsMatch(f[2]))
        errors.Add($"bad breakage {f[2]}: {member}");
    if (f[6] != corpus.GetValueOrDefault(member).ToString())
        errors.Add($"mods count {f[6]} is not the corpus count: {member}");
    if (f[5] is not ("any" or "write"))
        errors.Add($"bad on {f[5]}: {member}");
    if (harmless.Contains(f[1]))
    {
        if (f[2] != "-" || f[3] != "-" || f[4] != "-")
            errors.Add($"{f[1]} row with breakage, shim or channel: {member}");
        if (unverified)
            errors.Add($"UNCERTAIN row must be node-scoped or stricter: {member}");
    }
    else if (!pendingReview && (f[2] == "-" || (f[3] != "yes" && f[3] != "no")))
        errors.Add($"{f[1]} row needs a breakage class and shim yes/no: {member}");
    if (!member.EndsWith(".*") && !fileLine.IsMatch(f[7]))
        errors.Add($"reason without file:line: {member}");
}

var game = new HashSet<string>(StringComparer.Ordinal);
foreach (var assembly in empty.SourceModule.ReferencedAssemblySymbols.Where(GameSurface.IsGame))
{
    foreach (var type in GameSurface.Types(assembly.GlobalNamespace))
    {
        if (type.DeclaredAccessibility != Accessibility.Public)
            continue;
        bool api = GameSurface.IsInterfaceSurface(type);
        foreach (var member in type.GetMembers())
        {
            if (member is not (IPropertySymbol or IFieldSymbol or IEventSymbol or IMethodSymbol))
                continue;
            string key = GameSurface.Key(member);
            if (api && GameSurface.DeclaredMember(member) || corpus.ContainsKey(key))
                game.Add(key);
        }
    }
}
game.UnionWith(["System.*", "VRageMath.*"]);
foreach (string member in corpus.Keys.Where(member => !game.Contains(member)))
    errors.Add($"corpus member not found in the game: {member}");
foreach (string member in game.Where(member => !rows.ContainsKey(member)))
    errors.Add($"game member without a row: {member}");
foreach (string member in rows.Keys.Where(member => !game.Contains(member)))
    errors.Add($"row for a member the game does not have: {member}");

// A Workshop mod can call concrete APIs and two overloads at one site, and the compiler adds calls the source
// does not spell out. Exercise source binding through the same path as a corpus scan, including the policy
// attached to each resolved symbol.
var probe = CSharpSyntaxTree.ParseText(
    """
    using VRage;
    using VRage.Collections;
    using VRage.Game.Components;
    using VRage.Game.ModAPI;
    using VRageMath;
    using Sandbox.Game.Entities;
    class Probe {
        void Check(IMyPlayerCollection players, LineD line, ListReader<int> list) {
            players.TryGetIdentityId(1L);
            players.TryGetIdentityId(1UL);
            MyGamePruningStructure.GetAllEntitiesInRay(ref line, null);
            MyFixedPoint amount = 5;
            foreach (int item in list) { }
        }
    }
    class Session : MySessionComponentBase { }
    class Logic : MyGameLogicComponent { public Logic() { } }
    """
);
var sample = empty.AddSyntaxTrees(probe);
var diagnostics = sample.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
errors.AddRange(diagnostics.Select(d => d.ToString()));
var seen = CorpusUsage.Members(sample.GetSemanticModel(probe)).ToHashSet();
var expected = new Dictionary<string, string>
{
    ["VRage.Game.ModAPI.IMyPlayerCollection.TryGetIdentityId(System.Int64)"] = "node-scoped",
    ["VRage.Game.ModAPI.IMyPlayerCollection.TryGetIdentityId(System.UInt64)"] = "replicated",
    [
        "Sandbox.Game.Entities.MyGamePruningStructure.GetAllEntitiesInRay(VRageMath.LineD@,System.Collections.Generic.List{VRageMath.MyLineSegmentOverlapResult{VRage.Game.Entity.MyEntity}},Sandbox.Game.Entities.MyEntityQueryType)"
    ] = "node-scoped",
    ["VRage.MyFixedPoint.op_Implicit(System.Int32)~VRage.MyFixedPoint"] = "local",
    ["VRage.Collections.ListReader`1.GetEnumerator()"] = "local",
    ["VRage.Game.Components.MySessionComponentBase.#ctor()"] = "local",
    ["VRage.Game.Components.MyGameLogicComponent.#ctor()"] = "local",
};
foreach (var (key, policy) in expected)
{
    if (!seen.Contains(key))
        errors.Add($"source probe did not find {key}");
    else if (!rows.TryGetValue(key, out var row) || row[1] != policy)
        errors.Add($"source probe expected {key} to resolve as {policy}");
}

if (mode == "--check-corpus")
{
    foreach (var member in CorpusUsage.Read(args[2], empty).Keys.Except(rows.Keys))
        errors.Add($"live corpus member without a row: {member}");
}
var counts = rows.Values.GroupBy(f => f[1]).OrderBy(g => g.Key, StringComparer.Ordinal);
Console.WriteLine($"game members {game.Count}, rows {rows.Count}");
foreach (var group in counts)
    Console.WriteLine(
        $"  {group.Key, -12} {group.Count(), 5}   corpus-used {group.Count(f => f[6] != "0"), 5}"
    );
Console.WriteLine(
    $"  uncertain    {rows.Values.Count(f => f[7].Contains("UNCERTAIN")), 5}   corpus-used {rows.Values.Count(f => f[7].Contains("UNCERTAIN") && f[6] != "0"), 5}"
);
foreach (string error in errors.Take(50))
    Console.WriteLine("FAIL " + error);
Console.WriteLine(errors.Count == 0 ? "PASS" : $"FAIL ({errors.Count} problems)");
return errors.Count == 0 ? 0 : 1;
