using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using ClusterPlugin;

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
    if (!breakage.IsMatch(f[2]))
        errors.Add($"bad breakage {f[2]}: {member}");
    if (!int.TryParse(f[5], out _))
        errors.Add($"bad mods count {f[5]}: {member}");
    if (harmless.Contains(f[1]))
    {
        if (f[2] != "-" || f[3] != "-" || f[4] != "-")
            errors.Add($"{f[1]} row with breakage, shim or channel: {member}");
        if (f[6].Contains("UNCERTAIN"))
            errors.Add($"UNCERTAIN row must be node-scoped or stricter: {member}");
    }
    else if (f[2] == "-" || (f[3] != "yes" && f[3] != "no"))
        errors.Add($"{f[1]} row needs a breakage class and shim yes/no: {member}");
    if (!member.EndsWith(".*") && !fileLine.IsMatch(f[6]))
        errors.Add($"reason without file:line: {member}");
}

// The game's mod API, enumerated the way the table names it.
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string candidate = Path.Combine(dir, name.Name + ".dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};
string[] namespaces =
{
    "Sandbox.ModAPI",
    "VRage.Game.ModAPI",
    "SpaceEngineers.Game.ModAPI",
    "VRage.ModAPI",
};
string[] assemblies =
{
    "Sandbox.Game",
    "Sandbox.Common",
    "SpaceEngineers.Game",
    "VRage.Game",
    "VRage",
    "VRage.Library",
    "VRage.Math",
    "SpaceEngineers.ObjectBuilders",
    "VRage.Input",
    "VRage.Scripting",
};
var game = new SortedSet<string>(StringComparer.Ordinal);
foreach (string name in assemblies)
{
    string path = Path.Combine(dir, name + ".dll");
    if (!File.Exists(path))
        continue;
    Type[] types;
    try
    {
        types = AssemblyLoadContext.Default.LoadFromAssemblyPath(path).GetExportedTypes();
    }
    catch (ReflectionTypeLoadException e)
    {
        types = e.Types.Where(type => type != null).ToArray();
    }
    foreach (Type type in types)
    {
        string ns = type.Namespace ?? "";
        bool modApi = namespaces.Any(each => ns == each || ns.StartsWith(each + "."));
        if (!modApi && type.FullName != "Sandbox.Game.MyVisualScriptLogicProvider")
            continue;
        string typeName = Regex.Replace(type.FullName, @"`\d+", "").Replace('+', '.');
        if (type.IsEnum || typeof(Delegate).IsAssignableFrom(type))
        {
            game.Add(typeName + ".*");
            continue;
        }
        const BindingFlags flags =
            BindingFlags.Public
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        foreach (MemberInfo member in type.GetMembers(flags))
        {
            if (member is ConstructorInfo || member is Type)
                continue;
            if (member is MethodInfo method && method.IsSpecialName)
                continue;
            game.Add(typeName + "." + member.Name);
        }
    }
}

foreach (string member in game.Where(member => !rows.ContainsKey(member)))
    errors.Add($"game member without a row: {member}");
foreach (
    string member in rows.Keys.Where(member =>
        !game.Contains(member) && member != "System.*" && member != "VRageMath.*"
    )
)
    errors.Add($"row for a member the game does not have: {member}");

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
