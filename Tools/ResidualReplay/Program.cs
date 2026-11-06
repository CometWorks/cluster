// Replays a retained handover-diff dump pair through the REAL ImportRoundTrip.ResidualsOfDocuments
// in the BUILT plugin assembly, by reflection. The point is to avoid reimplementing
// NormalizeDocument: it runs ~25 passes and an offline approximation of it gave me a composition
// that was wrong twice. ResidualsOfDocuments exists for exactly this ("a retained handover-diff
// dump pair replays through here unchanged").
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;

class Program
{
    static string gameDir;

    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: residual-replay <ClusterNode.dll> <source.xml[.gz]> <target.xml[.gz]> [gameDir]\n       residual-replay <ClusterNode.dll> --self-test <section> [gameDir]");
            return 2;
        }
        gameDir = args.Length > 3 ? args[3]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".steam/steam/steamapps/common/SpaceEngineersDedicatedServer/DedicatedServer64");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string candidate = Path.Combine(gameDir, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };

        Assembly plugin = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        // --self-test SECTION runs one node self-test section that needs no game session (handover-drift,
        // round-trip-normalize) the same way, so a gate rule is checked without starting a node.
        if (args[1] == "--self-test") return SelfTest(plugin, args[2]);
        Type type = plugin.GetType("ClusterPlugin.ImportRoundTrip")
            ?? throw new InvalidOperationException("ImportRoundTrip not found in " + args[0]);
        MethodInfo method = type.GetMethod("ResidualsOfDocuments", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ResidualsOfDocuments not found");

        // Timing mode: ITERATIONS=<n> re-runs the walk on freshly loaded documents and reports the
        // distribution. The walk NORMALIZES IN PLACE, so each iteration must reload from disk -
        // reusing the XDocument would time an already-normalized input and understate the cost.
        // Compare two assemblies on the same input to measure whether a change removes WORK or only
        // removes warnings; the node's own "import warm-up done ... compareMs=" is the same
        // measurement in-process, so this is its offline equivalent with no deploy.
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("ITERATIONS"), out int n) ? n : 1;
        List<string> residuals = null;
        List<double> times = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            XDocument a = Load(args[1]), b = Load(args[2]);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            residuals = (List<string>)method.Invoke(null, new object[] { a, b, null });
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        if (iterations > 1)
        {
            times.Sort();
            // Report the MEDIAN and the minimum: the first iterations carry JIT and the median is
            // robust to a GC pause landing in one sample.
            Console.WriteLine($"compare ms over {iterations} runs: median {times[times.Count / 2]:F2}"
                + $"  min {times[0]:F2}  max {times[times.Count - 1]:F2}");
        }

        Console.WriteLine($"residuals: {residuals.Count}");
        MethodInfo signature = type.GetMethod("Signature", BindingFlags.NonPublic | BindingFlags.Static);
        if (signature != null)
            Console.WriteLine($"signature: {signature.Invoke(null, new object[] { residuals })}");
        Console.WriteLine();
        foreach (string entry in residuals) Console.WriteLine("  " + entry);
        return 0;
    }

    static int SelfTest(Assembly plugin, string section)
    {
        MethodInfo run = plugin.GetType("ClusterPlugin.ClusterSelfTest")?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("ClusterSelfTest.Run not found");
        try
        {
            run.Invoke(null, new object[] { section });
            Console.WriteLine($"PASS self-test {section}");
            return 0;
        }
        catch (TargetInvocationException error)
        {
            Console.WriteLine($"FAIL self-test {section}: {error.InnerException?.Message}");
            return 1;
        }
    }

    // Nodes write the dumps gzipped since #1397 (*.xml.gz); older plain *.xml dumps load as they are.
    static XDocument Load(string path)
    {
        using Stream file = File.OpenRead(path);
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return XDocument.Load(file);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return XDocument.Load(gzip);
    }
}
