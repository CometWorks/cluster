using System.Diagnostics;
using System.Text.Json;
using Concealment.Accounting;

/// <summary>SE-1096: a wake's offline compensation moved off the game thread. This recomputes what a cluster wake
/// logged ("[ClusterPlugin] offline compensation {json}") with the model this tool is built with, at the same wake
/// frame, from the profiles of the Final the node read, and requires the same JSON. Built at an older Concealment pin,
/// it shows that the moved wake still gives that pin's results.</summary>
internal static class WakeEquivalenceCheck
{
    // profiles: JSON array of the Finals' profiles; sun: "<from>-<through>.json" WA sun/work paths
    // (/registry/offline-accounting-sun); logged: one logged result JSON per line, byte-exact from the node log.
    internal static int Run(string profiles, string sun, string logged, long capFrames)
    {
        var all = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllBytes(profiles))!;
        int same = 0, differ = 0;
        foreach (string line in File.ReadAllLines(logged).Where(l => l.Length > 0))
        {
            var expected = JsonSerializer.Deserialize<OfflineResult>(line, OfflineProfile.Json)!;
            var profile = all.Select(e => JsonSerializer.Deserialize<OfflineProfile>(e.GetRawText(), OfflineProfile.Json)!)
                .Single(p => p.Group == expected.Group && p.SimulatedThrough == expected.FromFrame);
            long through = Math.Min(expected.CompensatedTo, profile.SimulatedThrough + capFrames);
            if (profile.SunPeriodFrames > 0 && profile.Sources.Any(s => s.Curve is { Length: > 1 }) && through > profile.SimulatedThrough)
                profile.SunPath = SunWorkPath.Read(File.ReadAllBytes(Path.Combine(sun, profile.SimulatedThrough + "-" + through + ".json")),
                    profile.ClockEpoch, profile.SimulatedThrough, through);
            var watch = Stopwatch.StartNew();
            var result = OfflineModel.Simulate(profile, expected.CompensatedTo, capFrames);
            watch.Stop();
            result.WakeClockGeneration = expected.WakeClockGeneration;
            string actual = JsonSerializer.Serialize(result, OfflineProfile.Json);
            bool equal = actual == line;
            if (equal) same++; else differ++;
            Console.WriteLine($"{(equal ? "SAME" : "DIFFER")} group={expected.Group} frames={result.Frames} events={result.Events} seconds={watch.Elapsed.TotalSeconds:F3}");
            if (!equal) Console.WriteLine("  logged:   " + line + "\n  computed: " + actual);
        }
        Console.WriteLine($"Wake equivalence: {same} same, {differ} different");
        return differ == 0 && same > 0 ? 0 : 1;
    }
}
