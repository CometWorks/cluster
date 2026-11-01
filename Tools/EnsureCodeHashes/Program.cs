using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace EnsureCodeHashes;

/// <summary>
/// T-0122. Prints EnsureCode.ReportHashes for a built plugin assembly without starting a node.
///
/// Usage:
///   EnsureCodeHashes &lt;plugin.dll&gt; &lt;DedicatedServer64 dir&gt; [more probe dirs...]
///
/// Everything is loaded into the DEFAULT load context through an AssemblyResolve hook over the
/// probe directories. That matters: Harmony's PatchProcessor resolves metadata tokens against the
/// module it is handed, and a type identity split across two load contexts turns every
/// AccessTools.DeclaredMethod lookup into a silent null - which this tool would then report as
/// UNRESOLVED, i.e. as a finding about the patch rather than about the loader. If the output is
/// mostly UNRESOLVED, suspect this file before suspecting the patches.
/// </summary>
internal static class Program
{
    private static readonly List<string> ProbeDirs = new();
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: EnsureCodeHashes <plugin.dll> <DedicatedServer64> [probe dirs...]");
            return 2;
        }

        string pluginPath = Path.GetFullPath(args[0]);
        if (!File.Exists(pluginPath))
        {
            Console.Error.WriteLine($"plugin assembly not found: {pluginPath}");
            return 2;
        }

        foreach (string dir in args.Skip(1))
        {
            if (dir.StartsWith("--")) continue;
            string full = Path.GetFullPath(dir);
            if (!Directory.Exists(full))
            {
                Console.Error.WriteLine($"probe directory not found: {full}");
                return 2;
            }
            ProbeDirs.Add(full);
        }
        ProbeDirs.Add(Path.GetDirectoryName(pluginPath));

        AssemblyLoadContext.Default.Resolving += (_, name) => Probe(name.Name);

        Assembly plugin;
        try
        {
            plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"cannot load plugin assembly: {e.GetType().Name}: {e.Message}");
            return 1;
        }

        // Reached by reflection so this tool does not have to compile against the plugin's whole
        // reference closure just to name one method.
        Type ensureCode = plugin.GetType("Shared.Tools.EnsureCode", throwOnError: false);
        if (ensureCode == null)
        {
            Console.Error.WriteLine("Shared.Tools.EnsureCode not found in the plugin assembly");
            return 1;
        }
        MethodInfo report = ensureCode.GetMethod("ReportHashes", BindingFlags.Public | BindingFlags.Static);
        if (report == null)
        {
            Console.Error.WriteLine("EnsureCode.ReportHashes(Assembly) not found");
            return 1;
        }

        // The guard's own falsification test, before any hashes are printed. A table of hashes
        // from a guard that cannot reject anything is worse than no table: it would be injected,
        // it would pass forever, and the next game update would sail straight through it.
        MethodInfo selfTest = ensureCode.GetMethod("SelfTest", BindingFlags.Public | BindingFlags.Static);
        if (selfTest == null)
            Console.Error.WriteLine("WARNING: EnsureCode.SelfTest not present; guard not falsified");
        else
        {
            var failure = (string)selfTest.Invoke(null, new object[] { plugin });
            if (failure != null)
            {
                Console.Error.WriteLine($"GUARD SELF-TEST FAILED: {failure}");
                return 1;
            }
            Console.Error.WriteLine("guard self-test ok (true hash accepted, wrong hash rejected)");
        }

        if (args.Contains("--transpilers") || Environment.GetEnvironmentVariable("ENSURECODE_TRANSPILERS") == "1")
            return DryRunTranspilers(plugin);

        int total = 0, resolved = 0;
        // Enumerated one line at a time on purpose: ReportHashes is a generator that can throw on a
        // single bad patch, and materialising it with ToList() would lose every line before the
        // throw - exactly the hashes worth having.
        IEnumerator<string> lines;
        try
        {
            lines = ((IEnumerable<string>)report.Invoke(null, new object[] { plugin })).GetEnumerator();
        }
        catch (TargetInvocationException e)
        {
            Console.Error.WriteLine($"ReportHashes failed immediately: {e.InnerException?.Message ?? e.Message}");
            return 1;
        }

        while (true)
        {
            string line;
            try
            {
                if (!lines.MoveNext()) break;
                line = lines.Current;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"enumeration aborted after {total} line(s): {e.GetType().Name}: {e.Message}");
                break;
            }
            total++;
            if (!line.Contains("????????") && !line.Contains("UNRESOLVED") && !line.Contains("FAILED"))
                resolved++;
            Console.WriteLine(line);
        }

        // WHERE THE IL ACTUALLY CAME FROM. A hash table is worthless without this: the node
        // loads Sandbox.Game/VRage/VRage.Game/VRage.Library from its Magnetar Preloader
        // directory, NOT from DedicatedServer64, and those files are byte-different. Two runs
        // that silently resolved to the same directory would produce an identical table and a
        // reassuring "0 differences" that proves nothing at all.
        foreach (var pair in Loaded.Where(x => x.Value != null).OrderBy(x => x.Key, StringComparer.Ordinal))
            Console.Error.WriteLine($"loaded {pair.Key} <- {pair.Value.Location}");
        Console.Error.WriteLine($"{resolved} of {total} patch method(s) hashed");
        // A run that resolves NOTHING is a loader failure, not an empty repo: fail loudly rather
        // than hand a caller an empty table that looks like "no patches declare a target".
        return total > 0 && resolved == 0 ? 1 : 0;
    }

    /// <summary>
    /// T-0124. Runs every [HarmonyTranspiler] in the plugin against the CURRENT IL of its target
    /// and reports whether it throws.
    ///
    /// WHY THIS IS NOT OPTIONAL. A transpiler in this repo asserts the shape it expects and throws
    /// CodeInstructionNotFound when the game moves - which is the right design, and it means a
    /// wrong assertion STOPS A NODE FROM STARTING. There are 24 of them. Discovering that at node
    /// start, on a live cluster, one node at a time, is the expensive way to find out.
    ///
    /// This runs them all in a console process in about a second. It is not a correctness proof -
    /// it proves the transpiler completes and how many instructions it emitted, not that the IL it
    /// emitted is right - but "throws on today's game build" is the failure that takes a node down,
    /// and that it does catch.
    /// </summary>
    private static int DryRunTranspilers(Assembly plugin)
    {
        int ran = 0, failed = 0;
        // GetTypes() throws ReflectionTypeLoadException outright when ANY type in the assembly
        // references something unresolvable (LiteNetLib, here, which no patch touches). Taking
        // the types it did load is what Harmony's own AccessTools.GetTypesFromAssembly does.
        Type[] types;
        try { types = plugin.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        foreach (Type patchType in types)
        {
            foreach (MethodInfo method in patchType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                         | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!method.GetCustomAttributes().Any(a => a.GetType().Name == "HarmonyTranspiler"))
                    continue;

                string label = $"{patchType.Name}.{method.Name}";
                MethodBase target;
                try
                {
                    target = ResolveTranspilerTarget(plugin, patchType, method);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"  SKIP  {label}: cannot resolve target ({e.GetType().Name}: {e.Message})");
                    continue;
                }
                if (target == null)
                {
                    Console.WriteLine($"  SKIP  {label}: target not resolved");
                    continue;
                }

                ran++;
                try
                {
                    // Harmony is loaded reflectively (this tool compiles against nothing but the
                    // BCL), so PatchProcessor is reached the same way.
                    Type processor = plugin.GetType("HarmonyLib.PatchProcessor", false)
                                     ?? AppDomain.CurrentDomain.GetAssemblies()
                                         .Select(a => a.GetType("HarmonyLib.PatchProcessor", false))
                                         .FirstOrDefault(t => t != null);
                    MethodInfo current = processor?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "GetCurrentInstructions" && m.GetParameters().Length >= 1);
                    if (current == null)
                        throw new Exception("HarmonyLib.PatchProcessor.GetCurrentInstructions not found");
                    object[] currentArgs = current.GetParameters()
                        .Select((par, i) => i == 0 ? (object)target : par.HasDefaultValue ? par.DefaultValue : null).ToArray();
                    // Must be a List<CodeInstruction>, not a List<object>: transpilers take
                    // IEnumerable<CodeInstruction> and reflection will not coerce the element type.
                    // Getting this wrong makes EVERY transpiler "fail" with an ArgumentException
                    // that has nothing to do with the transpiler - a harness fault wearing the
                    // costume of 39 findings.
                    object rawBefore = current.Invoke(null, currentArgs);
                    Type ciType = processor.Assembly.GetType("HarmonyLib.CodeInstruction", true);
                    object before = typeof(Enumerable).GetMethod("ToList").MakeGenericMethod(ciType).Invoke(
                        null, new[] { typeof(Enumerable).GetMethod("Cast").MakeGenericMethod(ciType)
                            .Invoke(null, new[] { rawBefore }) });
                    int beforeCount = (int)before.GetType().GetProperty("Count").GetValue(before);
                    // Transpiler signatures vary; bind each parameter by its type.
                    // Many transpilers here take an ILGenerator and call DefineLabel() on it.
                    // Passing null makes every one of them die with a NullReferenceException that
                    // says nothing about the transpiler - 14 invented findings. A throwaway
                    // DynamicMethod's generator satisfies them. The labels it mints are not valid
                    // for the real patch, which is fine: this run checks "does it complete on
                    // today's IL", not "is the emitted IL correct".
                    object[] argv = method.GetParameters().Select(par =>
                        par.ParameterType == typeof(MethodBase) ? (object)target
                        : typeof(System.Collections.IEnumerable).IsAssignableFrom(par.ParameterType) ? before
                        : par.ParameterType == typeof(ILGenerator)
                            ? new DynamicMethod($"dryrun_{label}", typeof(void), Type.EmptyTypes).GetILGenerator()
                            : null).ToArray();
                    var after = ((System.Collections.IEnumerable)method.Invoke(null, argv)).Cast<object>().ToList();
                    Console.WriteLine($"  ok    {label}: {beforeCount} -> {after.Count} instructions"
                                      + $"  [{target.DeclaringType?.Name}.{target.Name}]");
                }
                catch (Exception e)
                {
                    failed++;
                    Exception root = e is TargetInvocationException tie ? tie.InnerException ?? e : e;
                    Console.WriteLine($"  FAIL  {label}: {root.GetType().Name}: {root.Message}");
                }
            }
        }
        Console.Error.WriteLine($"{ran - failed} of {ran} transpiler(s) ran clean against the current game build");
        return failed > 0 ? 1 : 0;
    }

    private static MethodBase ResolveTranspilerTarget(Assembly plugin, Type patchType, MethodInfo method)
    {
        Type ensureCode = plugin.GetType("Shared.Tools.EnsureCode", throwOnError: false);
        MethodInfo effective = ensureCode?.GetMethod("EffectiveInfo", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo resolve = ensureCode?.GetMethod("ResolveTarget", BindingFlags.NonPublic | BindingFlags.Static);
        if (effective == null || resolve == null)
            return null;
        object info = effective.Invoke(null, new object[] { method });
        if (info == null)
            return null;
        Type declaring = (Type)info.GetType().GetField("declaringType").GetValue(info);
        if (declaring == null)
        {
            MethodInfo computed = ensureCode.GetMethod("ResolveComputedTargets", BindingFlags.NonPublic | BindingFlags.Static);
            var targets = ((System.Collections.IEnumerable)computed.Invoke(null, new object[] { patchType }))
                .Cast<MethodBase>().ToList();
            return targets.FirstOrDefault();     // dry-run the first; the rest share the transpiler
        }
        object[] argv = { declaring, info, null, null };
        resolve.Invoke(null, argv);
        return (MethodBase)(argv[2] ?? argv[3]);
    }

    private static Assembly Probe(string simpleName)
    {
        if (simpleName == null) return null;
        if (Loaded.TryGetValue(simpleName, out Assembly cached)) return cached;
        foreach (string dir in ProbeDirs)
        {
            string candidate = Path.Combine(dir, simpleName + ".dll");
            if (!File.Exists(candidate)) continue;
            try
            {
                Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                Loaded[simpleName] = assembly;
                return assembly;
            }
            catch (BadImageFormatException)
            {
                // A native DLL sitting under the same name (HavokWrapper and friends ship both).
                // Keep probing the remaining directories instead of failing the whole load.
            }
        }
        Loaded[simpleName] = null;
        return null;
    }
}
