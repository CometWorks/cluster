using System;
using System.Linq;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// <summary>
    /// SE-2000 GPS name cap, the pure parts: which entries go over the cap (per identity and name, oldest first, ties in
    /// list order, names case-insensitive, other names and identities untouched), and the gps.* knobs.
    /// </summary>
    private static void GpsNameCapRules()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("gps-name-cap: " + message);
        }
        const string Ping = "Wreck Detector Ping";
        var held = new (long Identity, int Hash, string Name)[]
        {
            (1, 10, Ping),
            (1, 11, Ping),
            (1, 12, "wreck detector ping"),
            (1, 13, Ping),
            (2, 20, Ping),
            (2, 21, Ping),
            (1, 30, "Other"),
            (1, 31, "Other"),
            (1, 32, "Other"),
        };
        // Hash 12 was seen last, 13 at the same time as 10 and 11 (a boot: list order decides).
        double Seen((long Identity, int Hash, string Name) entry) => entry.Hash == 12 ? 2 : 1;

        var over = GpsNameCapSweep.Oldest(held, Seen, 2);
        Check(
            over.Select(x => x.Hash).OrderBy(x => x).SequenceEqual(new[] { 10, 11, 30 }),
            "cap 2 deletes the two oldest pings of identity 1 and its oldest 'Other', got "
                + string.Join(",", over.Select(x => x.Hash))
        );
        Check(
            GpsNameCapSweep.Oldest(held, Seen, 5).Count == 0,
            "nothing goes while every identity is at or under the cap"
        );
        Check(
            GpsNameCapSweep.Oldest(held, Seen, 1).Select(x => x.Hash).Contains(20)
                && !GpsNameCapSweep.Oldest(held, Seen, 1).Select(x => x.Hash).Contains(21),
            "cap 1 keeps the later of two entries seen at the same time"
        );

        Check(
            ClusterConfig.DefaultOf("gps.cappedNames") == Ping,
            "gps.cappedNames defaults to the MES wreck ping"
        );
        Check(ClusterConfig.DefaultOf("gps.maxPerName") == "5", "gps.maxPerName defaults to 5");
        Check(
            ClusterConfig.Normalize("gps.cappedNames", " A b , ,C ") == "A b,C",
            "gps.cappedNames trims names and drops blanks"
        );
        Check(ClusterConfig.Normalize("gps.cappedNames", "") == "", "gps.cappedNames may be empty");
        bool refused = false;
        try
        {
            ClusterConfig.Normalize("gps.cappedNames", new string('x', 33));
        }
        catch (ArgumentException)
        {
            refused = true;
        }
        Check(refused, "gps.cappedNames refuses a name longer than the game's 32 characters");
    }
}
