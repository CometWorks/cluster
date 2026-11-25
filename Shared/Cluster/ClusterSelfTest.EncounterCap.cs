using System;
using System.Collections.Generic;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    private static void EncounterCapRules()
    {
        // Id 3 was just dispatched. Overlapping reports from the two nodes carry the same encounter.
        var known = new Dictionary<long, double> { [3] = 0 };
        EncounterDirectorService.RefreshEncounters(known, [1, 2, 2], 10);
        if (known.Count != 3)
            throw new InvalidOperationException(
                "pending and reported encounters must share the cap"
            );
        EncounterDirectorService.RefreshEncounters(known, [2, 3], 1500);
        if (known.Count != 3)
            throw new InvalidOperationException("a partition between nodes must still count");
        EncounterDirectorService.RefreshEncounters(known, [2, 3], 65000);
        if (known.Count != 2)
            throw new InvalidOperationException("a removed encounter must free its slot");
    }
}
