using System;
using System.Collections.Generic;
using System.Linq;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    private static void EncounterCapRules()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        // Id 3 was just dispatched. Overlapping reports from the two nodes carry the same encounter.
        var known = new Dictionary<long, double> { [3] = 0 };
        EncounterDirectorService.RefreshEncounters(known, [1, 2, 2], 10);
        Check(known.Count == 3, "pending and reported encounters must share the cap");
        EncounterDirectorService.RefreshEncounters(known, [2, 3], 150_000);
        Check(known.Count == 3, "a partition between nodes for minutes must still count");
        EncounterDirectorService.RefreshEncounters(known, [2, 3], 200_000);
        Check(known.Count == 2, "a removed encounter must free its slot");

        // SE-1921: encounters frozen at a cluster stop count only until their removal deadline.
        var census = new Dictionary<long, long>
        {
            [10] = 0,
            [11] = 2_000,
            [12] = 999,
            [13] = 1_000,
        };
        Check(
            EncounterDirectorService
                .Counted(census, 1_000)
                .OrderBy(x => x)
                .SequenceEqual([10L, 11L]),
            "an encounter past its removal deadline no longer holds a slot; one without a timer always does"
        );
        Check(
            EncounterDirectorService.RemainingMinutes(1_000 + 90, 1_000) == 2
                && EncounterDirectorService.RemainingMinutes(1_000 + 60, 1_000) == 1
                && EncounterDirectorService.RemainingMinutes(500, 1_000) == 0,
            "a moved encounter keeps its deadline in whole minutes, overdue = 0"
        );
    }
}
