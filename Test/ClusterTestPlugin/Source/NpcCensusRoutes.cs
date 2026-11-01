using System;
using System.Globalization;

namespace ClusterTestPlugin;

/// <summary>
/// #1354: the census as mod-mes (#1321) reads it - through the AppDomain slot, with no reference to the cluster
/// assembly - and a stand-in for mod-mes's gap-band slot, so the sweep's gap boost can be tested without MES.
/// </summary>
internal static class NpcCensusRoutes
{
    private const string CountSlot = "CometWorks.Cluster.NpcCensus.v1";
    private const string AgeSlot = "CometWorks.Cluster.NpcCensus.Age.v1";
    private const string InGapSlot = "CometWorks.ModMes.InGap.v1";

    public static void Register()
    {
        // GET /npc-census-count?x=&y=&z=&radius= - Count(x, y, z, radius) and the age, from the published slots.
        TestRoutes.Add("GET", "/npc-census-count", context =>
        {
            double x = TestRoutes.Double(context, "x"), y = TestRoutes.Double(context, "y"), z = TestRoutes.Double(context, "z");
            double radius = TestRoutes.Double(context, "radius");
            var count = AppDomain.CurrentDomain.GetData(CountSlot) as Func<double, double, double, double, int>;
            var age = AppDomain.CurrentDomain.GetData(AgeSlot) as Func<double>;
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => new
            {
                published = count != null && age != null,
                count = count?.Invoke(x, y, z, radius),
                ageSeconds = age?.Invoke(),
            }));
        });
        // POST /npc-test-gap?minX= - a stand-in InGap: every point with x >= minX is in a gap band. No minX clears the slot.
        TestRoutes.Add("POST", "/npc-test-gap", context =>
        {
            string raw = context.Request.QueryString["minX"];
            if (raw == null) AppDomain.CurrentDomain.SetData(InGapSlot, null);
            else
            {
                double minX = double.Parse(raw, CultureInfo.InvariantCulture);
                AppDomain.CurrentDomain.SetData(InGapSlot, (Func<double, double, double, bool>)((x, y, z) => x >= minX));
            }
            TestRoutes.Json(context, 200, new { slot = InGapSlot, minX = raw });
        });
    }
}
