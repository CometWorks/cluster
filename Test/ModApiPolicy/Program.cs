using System.Text.RegularExpressions;
using ClusterPlugin;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModCompatScan;

// SE-1840: every public member of the game's mod API has exactly one row in ModApiPolicyTable, every row is
// well formed, and no row names a member the game no longer has. Exit 0 = pass, 1 = fail, 2 = no game found.

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

if (!(args.Length <= 1 || args.Length == 3 && (args[1] == "--scan" || args[1] == "--check-corpus")))
{
    Console.Error.WriteLine(
        "usage: ModApiPolicyChecks [DEDICATED64 [--scan|--check-corpus WORKSHOP_CONTENT]]"
    );
    return 2;
}
var empty = CSharpCompilation.Create(
    "WorkshopMod",
    references: ScriptReferences.References(dir),
    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
);
if (args.Length == 3 && args[1] == "--scan")
{
    foreach (
        var entry in CorpusUsage
            .Read(args[2], empty)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
    )
        Console.WriteLine($"{entry.Key}\t{entry.Value}");
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

var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
foreach (string line in ModApiPolicyTable.Text.Split('\n'))
{
    string[] f = line.TrimEnd('\r').Split('\t');
    if (f.Length != 7)
    {
        errors.Add($"not 7 fields: {line}");
        continue;
    }
    string member = f[0];
    if (!rows.TryAdd(member, f))
        errors.Add($"duplicate row: {member}");
    if (!policies.Contains(f[1]))
        errors.Add($"unknown policy {f[1]}: {member}");
    bool unverified = f[6].Contains("UNCERTAIN");
    bool pendingReview = unverified && f[1] == "node-scoped" && f[2] == "?" && f[3] == "?";
    if (!pendingReview && !breakage.IsMatch(f[2]))
        errors.Add($"bad breakage {f[2]}: {member}");
    if (!int.TryParse(f[5], out int modCount) || modCount < 0)
        errors.Add($"bad mods count {f[5]}: {member}");
    if (harmless.Contains(f[1]))
    {
        if (f[2] != "-" || f[3] != "-" || f[4] != "-")
            errors.Add($"{f[1]} row with breakage, shim or channel: {member}");
        if (f[6].Contains("UNCERTAIN"))
            errors.Add($"UNCERTAIN row must be node-scoped or stricter: {member}");
    }
    else if (!pendingReview && (f[2] == "-" || (f[3] != "yes" && f[3] != "no")))
        errors.Add($"{f[1]} row needs a breakage class and shim yes/no: {member}");
    if (!member.EndsWith(".*") && !fileLine.IsMatch(f[6]))
        errors.Add($"reason without file:line: {member}");
}

var corpus = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "CorpusMembers.txt"))
    .ToHashSet(StringComparer.Ordinal);
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
            if (api && GameSurface.DeclaredMember(member) || corpus.Contains(key))
                game.Add(key);
        }
    }
}
game.UnionWith(["System.*", "VRageMath.*"]);
foreach (string member in corpus.Where(member => !game.Contains(member)))
    errors.Add($"corpus member not found in the game: {member}");
foreach (string member in game.Where(member => !rows.ContainsKey(member)))
    errors.Add($"game member without a row: {member}");
foreach (string member in rows.Keys.Where(member => !game.Contains(member)))
    errors.Add($"row for a member the game does not have: {member}");

// A Workshop mod can call concrete APIs and two overloads at one site. Exercise source binding
// through the same path as a corpus scan, including the policy attached to each resolved symbol.
var probe = CSharpSyntaxTree.ParseText(
    """
    using VRage.Game.ModAPI;
    using VRageMath;
    using Sandbox.Game.Entities;
    class Probe {
        void Check(IMyPlayerCollection players, LineD line) {
            players.TryGetIdentityId(1L);
            players.TryGetIdentityId(1UL);
            MyGamePruningStructure.GetAllEntitiesInRay(ref line, null);
        }
    }
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
};
foreach (var (key, policy) in expected)
    if (!seen.Contains(key) || !rows.TryGetValue(key, out var row) || row[1] != policy)
        errors.Add($"source probe expected {key} to resolve as {policy}");

var counted = rows.Where(row => row.Value[5] != "0").Select(row => row.Key).ToHashSet();
if (!counted.SetEquals(corpus))
    errors.Add(
        "the members with positive Workshop counts do not match the pinned corpus member list"
    );
if (args.Length == 3 && args[1] == "--check-corpus")
{
    foreach (var member in CorpusUsage.Read(args[2], empty).Keys.Except(rows.Keys))
        errors.Add($"live corpus member without a row: {member}");
}
var counts = rows.Values.GroupBy(f => f[1]).OrderBy(g => g.Key);
Console.WriteLine($"game members {game.Count}, rows {rows.Count}");
foreach (var group in counts)
    Console.WriteLine(
        $"  {group.Key, -12} {group.Count(), 5}   corpus-used {group.Count(f => f[5] != "0"), 5}"
    );
Console.WriteLine(
    $"  uncertain    {rows.Values.Count(f => f[6].Contains("UNCERTAIN")), 5}   corpus-used {rows.Values.Count(f => f[6].Contains("UNCERTAIN") && f[5] != "0"), 5}"
);
foreach (string error in errors.Take(50))
    Console.WriteLine("FAIL " + error);
Console.WriteLine(errors.Count == 0 ? "PASS" : $"FAIL ({errors.Count} problems)");
return errors.Count == 0 ? 0 : 1;
