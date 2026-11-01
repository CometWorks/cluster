using System;
using System.Collections.Generic;
using System.Reflection;
using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// T-0392 v2 slices bench: make THIS node's ShipAntennaService treat a ship as recently piloted - the state a
/// pilot's ship is in on the live SOURCE of a handover (the 7 'Visitor Ion' ships of 2026-10-05 00:22:56 were
/// seated, so ON) - without a character. Writes the service's private LastOccupied[grid] = now (the `grace`
/// term of rule=assigned), so the source keeps the antenna ON until capture while the target, a different
/// process with no such entry, reads the arriving ship as unoccupied and stopped.
/// </summary>
internal static class ShipAntennaTestRoutes
{
    public static void Register()
    {
        // POST /ship-antenna-test-grace?grid=
        TestRoutes.Add("POST", "/ship-antenna-test-grace", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Grace(grid)));
        });
    }

    private static object Grace(long grid)
    {
        FieldInfo field = typeof(ShipAntennaService).GetField("LastOccupied", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(nameof(ShipAntennaService), "LastOccupied");
        var lastOccupied = (Dictionary<long, double>)field.GetValue(null);
        double now = ClusterRuntime.LocalMilliseconds;
        lastOccupied[grid] = now;
        return new { grid, lastOccupied = now };
    }
}
