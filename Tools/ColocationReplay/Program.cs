using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

// Reconstructs PartitionReport objects from the `post-commit partitions` JSON that every node
// writes, then calls the BUILT gateway's ChooseMove on each node PAIR at two engage fractions.
// Nothing here re-implements any part of the placement rule; every decision comes out of the
// assembly under test.
internal static class Program
{
    private static Assembly _asm;
    private static Type T(string n) => _asm.GetType("ClusterRegistry." + n, true);

    private static object Make(Type t, params object[] args)
    {
        ConstructorInfo best = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length).First();
        ParameterInfo[] ps = best.GetParameters();
        object[] full = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
            full[i] = i < args.Length && args[i] != null ? args[i]
                : ps[i].HasDefaultValue ? ps[i].DefaultValue
                : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
        return best.Invoke(full);
    }

    private static object Info(JsonElement p)
    {
        JsonElement s = p.GetProperty("sphere");
        object sphere = Make(T("PartitionSphere"), s.GetProperty("x").GetDouble(), s.GetProperty("y").GetDouble(),
            s.GetProperty("z").GetDouble(), s.GetProperty("radius").GetDouble());
        ulong[] clients = p.TryGetProperty("clients", out JsonElement c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetUInt64()).ToArray() : Array.Empty<ulong>();
        double[][] spheres = null;
        if (p.TryGetProperty("memberSpheres", out JsonElement ms) && ms.ValueKind == JsonValueKind.Array)
            spheres = ms.EnumerateArray().Select(r => r.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray();
        return Make(T("PartitionInfo"), p.GetProperty("id").GetUInt64(), p.GetProperty("memberCount").GetInt32(),
            p.GetProperty("anchored").GetBoolean(), sphere,
            p.TryGetProperty("maxMemberSpeed", out JsonElement v) ? v.GetDouble() : 0.0, clients,
            p.TryGetProperty("pcu", out JsonElement q) ? q.GetInt32() : 0,
            p.TryGetProperty("estimatedSnapshotBytes", out JsonElement b) ? b.GetInt64() : 0L,
            p.TryGetProperty("simLoadUs", out JsonElement u) ? u.GetInt64() : 0L, spheres);
    }

    private static object Report(JsonElement d, out int n, out double sync, out double margin)
    {
        JsonElement parts = d.GetProperty("partitions");
        Array arr = Array.CreateInstance(T("PartitionInfo"), parts.GetArrayLength());
        int i = 0;
        foreach (JsonElement p in parts.EnumerateArray()) arr.SetValue(Info(p), i++);
        n = i;
        sync = d.GetProperty("syncDistance").GetDouble();
        margin = d.GetProperty("margin").GetDouble();
        return Make(T("PartitionReport"), sync, margin, arr, 0.0,
            d.TryGetProperty("threadLoad", out JsonElement tl) ? tl.GetDouble() : 0.0);
    }

    public static int Main(string[] argv)
    {
        if (argv.Length < 2) { Console.Error.WriteLine("usage: ColocationReplay <ClusterRegistry.dll> <node-log>..."); return 2; }
        _asm = Assembly.LoadFrom(Path.GetFullPath(argv[0]));

        // EVERY snapshot in each node log, so the replay covers the transient approaches that the
        // marginal-gap population is made of. A single instant cannot show them: they are by
        // definition brief.
        string since = Environment.GetEnvironmentVariable("SINCE") ?? "00:00:00";
        var byNode = new Dictionary<string, List<(int Sec, JsonElement Doc)>>();
        foreach (string path in argv.Skip(1))
        {
            var list = new List<(int, JsonElement)>();
            foreach (string line in File.ReadLines(path))
            {
                int k = line.IndexOf("post-commit partitions", StringComparison.Ordinal);
                if (k < 0 || line.Length < 20) continue;
                string stamp = line.Substring(11, 8);
                if (string.CompareOrdinal(stamp, since) < 0) continue;
                int sec = int.Parse(stamp.Substring(0, 2)) * 3600 + int.Parse(stamp.Substring(3, 2)) * 60
                          + int.Parse(stamp.Substring(6, 2));
                try { list.Add((sec, JsonDocument.Parse(line.Substring(k + 22).Trim()).RootElement.Clone())); }
                catch (JsonException) { }
            }
            if (list.Count == 0) { Console.Error.WriteLine($"  {path}: no usable post-commit line"); continue; }
            byNode[Path.GetFileName(path)] = list;
            Console.Error.WriteLine($"  {Path.GetFileName(path)}: {list.Count} snapshots");
        }
        string[] nodes = byNode.Keys.OrderBy(x => x).ToArray();

        MethodInfo choose = T("DirectGateway").GetMethod("ChooseMove", BindingFlags.Static | BindingFlags.NonPublic);
        Type costType = T("CostModel");
        PropertyInfo fracProp = costType.GetProperty("ColocationEngageFraction");
        if (fracProp == null) { Console.Error.WriteLine("assembly has no ColocationEngageFraction - wrong build"); return 3; }

        double[] fractions = (Environment.GetEnvironmentVariable("FRACTIONS") ?? "1.0,0.875")
            .Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();

        // Align nodes on a common second grid: for each node take the snapshot in force at that
        // second (the most recent at or before it), which is what the gateway would have polled.
        int lo = byNode.Values.Max(v => v[0].Sec), hi = byNode.Values.Min(v => v[^1].Sec);
        var grid = new List<int>();
        for (int t = lo; t <= hi; t += 10) grid.Add(t);
        Console.WriteLine($"replaying {grid.Count} aligned instants over {(hi - lo) / 60} min across {nodes.Length} nodes");
        Console.WriteLine();
        JsonElement InForce(string node, int t)
        {
            List<(int Sec, JsonElement Doc)> v = byNode[node];
            int best = -1;
            for (int i = 0; i < v.Count && v[i].Sec <= t; i++) best = i;
            return v[best < 0 ? 0 : best].Doc;
        }
        foreach (double f in fractions)
        {
            object model = Make(costType);
            model = costType.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) != null
                ? model : model;
            // records: use the generated copy ctor path via `with` is not reachable by reflection,
            // so construct directly with the fraction in its positional slot.
            ParameterInfo[] ps = costType.GetConstructors().OrderByDescending(c => c.GetParameters().Length)
                .First().GetParameters();
            object[] args = ps.Select(p => p.HasDefaultValue ? p.DefaultValue
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            int slot = Array.FindIndex(ps, p => p.Name == "ColocationEngageFraction");
            args[slot] = f;
            model = costType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First().Invoke(args);

            // Count DISTINCT partitions co-location wants to move, not raw returns. A static replay
            // never executes anything, so an unresolved urgent pair is re-proposed at every instant
            // and would dominate a raw count - a confound that is LARGE and shared, so it does not
            // cancel in a ratio either. Deduplicating by moving-partition id measures the structure
            // instead: how much of the fleet co-location is trying to gather.
            var movers = new HashSet<ulong>(); var gaps = new List<double>();
            var marginal = new HashSet<ulong>();
            foreach (int t in grid)
            for (int a = 0; a < nodes.Length; a++)
                for (int b = a + 1; b < nodes.Length; b++)   // i<j, as the production pass does
                {
                    object src = Report(InForce(nodes[a], t), out int na, out double sync, out double margin);
                    object tgt = Report(InForce(nodes[b], t), out int nb, out _, out _);
                    if (na == 0 || nb == 0) continue;
                    object move = choose.Invoke(null, new object[] {
                        src, tgt, sync + margin, new HashSet<ulong>(),
                        (Func<ulong, bool>)(_ => false), model, null, null, null, 0.0, 0.0 });
                    if (move == null) continue;
                    Type mt = move.GetType();
                    string reason = (string)mt.GetProperty("Reason").GetValue(move);
                    if (reason != "co-location") continue;
                    double gap = (double)mt.GetProperty("Gap").GetValue(move);
                    ulong id = (ulong)mt.GetProperty("Moving").GetValue(move).GetType()
                        .GetProperty("Id").GetValue(mt.GetProperty("Moving").GetValue(move));
                    if (movers.Add(id)) gaps.Add(gap);
                    if (gap >= 3000) marginal.Add(id);
                }
            gaps.Sort();
            string dist = gaps.Count == 0 ? "-"
                : $"min {gaps[0],6:F0}  median {gaps[gaps.Count / 2],6:F0}  max {gaps[gaps.Count - 1],6:F0}";
            Console.WriteLine($"  fraction {f:F3}  engage {(3000 + 1000) * f,4:F0} m   distinct partitions co-location wants to gather: {movers.Count,3}"
                + $"   of which first seen at gap >= 3000 m: {marginal.Count,3}   gap {dist}");
        }
        return 0;
    }
}
