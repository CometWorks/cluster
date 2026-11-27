using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// SE-2000: the World Authority's cap on GPS entries a mod keeps adding under the same name. The MES wreck encounters
/// broadcast a "Wreck Detector Ping" marker to every player in range, and nothing ever removes them, so players on
/// Octahedron piled up dozens. The WA holds every player's GPS list and publishes it to the nodes, so it is the one place
/// where the cap holds whichever node a player is on, across handovers and reconnects. Every second it counts, per
/// identity, the entries named one of gps.cappedNames (case-insensitive), and deletes the oldest ones over
/// gps.maxPerName. Age is the time the WA first saw an entry; the entries it finds at boot count in the order of the
/// player's list, which is the order they were saved in. The first sweep after a boot also cleans up the backlog.
/// </summary>
internal static class GpsNameCapSweep
{
    private const double SweepIntervalMilliseconds = 1000;
    private static readonly Dictionary<(long Identity, int Hash), double> s_firstSeen = new();
    private static double s_nextSweep;

    public static void Update()
    {
        if (
            !ClusterRuntime.IsClustered
            || !ClusterRuntime.IsWorldAuthority
            || MySession.Static?.Ready != true
        )
            return;
        double now = ClusterRuntime.LocalMilliseconds;
        if (now < s_nextSweep)
            return;
        s_nextSweep = now + SweepIntervalMilliseconds;

        var names = new HashSet<string>(
            ClusterConfig.Get("gps.cappedNames").Split(',', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase
        );
        int cap = ClusterConfig.GetInt("gps.maxPerName");
        if (names.Count == 0)
        {
            s_firstSeen.Clear();
            return;
        }

        var held = new List<(long Identity, int Hash, string Name)>();
        foreach (var identity in MySession.Static.Gpss.m_playerGpss)
        foreach (var gps in identity.Value.Values)
            if (!gps.IsLocal && gps.Name != null && names.Contains(gps.Name))
                held.Add((identity.Key, gps.Hash, gps.Name));

        var current = new HashSet<(long, int)>(held.Select(x => (x.Identity, x.Hash)));
        foreach (var gone in s_firstSeen.Keys.Where(key => !current.Contains(key)).ToArray())
            s_firstSeen.Remove(gone);
        foreach (var entry in held)
            s_firstSeen.TryAdd((entry.Identity, entry.Hash), now);
        if (cap <= 0)
            return;

        var over = Oldest(held, entry => s_firstSeen[(entry.Identity, entry.Hash)], cap);
        if (over.Count == 0)
            return;
        foreach (var entry in over)
        {
            MySession.Static.Gpss.SendDeleteGpsRequest(entry.Identity, entry.Hash);
            s_firstSeen.Remove((entry.Identity, entry.Hash));
        }
        Console.WriteLine(
            "[ClusterPlugin] gps name cap deleted="
                + over.Count
                + " identities="
                + over.Select(x => x.Identity).Distinct().Count()
                + " cap="
                + cap
                + " held="
                + (held.Count - over.Count)
                + " (SE-2000)"
        );
    }

    /// <summary>
    /// The entries to delete: per identity and name, all but the <paramref name="cap"/> newest by
    /// <paramref name="seen"/>, ties kept in list order (the earlier one is older).
    /// </summary>
    internal static List<(long Identity, int Hash, string Name)> Oldest(
        IReadOnlyList<(long Identity, int Hash, string Name)> held,
        Func<(long Identity, int Hash, string Name), double> seen,
        int cap
    ) =>
        held.Select((entry, index) => (entry, index))
            .GroupBy(x => (x.entry.Identity, Name: x.entry.Name.ToUpperInvariant()))
            .SelectMany(group =>
            {
                var ordered = group.OrderBy(x => seen(x.entry)).ThenBy(x => x.index).ToList();
                return ordered.Take(Math.Max(0, ordered.Count - cap)).Select(x => x.entry);
            })
            .ToList();
}
