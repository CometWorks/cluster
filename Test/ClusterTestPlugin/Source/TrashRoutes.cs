using System;
using System.Globalization;

namespace ClusterTestPlugin;

/// <summary>Routes of <see cref="TrashTestHarness"/>: trash probes and fixture grids.</summary>
internal static class TrashRoutes
{
    public static void Register()
    {
        // POST /trash-test-spawn?x=&y=&z=[&builtBy=][&static=][&blocks=][&spin=rad/s][&npc=true][&owner=identity id][&mesKey=true][&batteries=N][&charge=MWh][&batteriesOff=true]
        TestRoutes.Add("POST", "/trash-test-spawn", context =>
        {
            double x = TestRoutes.Double(context, "x");
            double y = TestRoutes.Double(context, "y");
            double z = TestRoutes.Double(context, "z");
            long builtBy = long.TryParse(context.Request.QueryString["builtBy"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedBuiltBy) ? parsedBuiltBy : 0;
            bool isStatic = bool.TryParse(context.Request.QueryString["static"], out bool parsedStatic) && parsedStatic;
            int blocks = int.TryParse(context.Request.QueryString["blocks"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedBlocks) ? parsedBlocks : 1;
            double spin = double.TryParse(context.Request.QueryString["spin"], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedSpin) ? parsedSpin : 0;
            bool npc = bool.TryParse(context.Request.QueryString["npc"], out bool parsedNpc) && parsedNpc;
            long owner = long.TryParse(context.Request.QueryString["owner"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedOwner) ? parsedOwner : 0;
            bool mesKey = bool.TryParse(context.Request.QueryString["mesKey"], out bool parsedMesKey) && parsedMesKey;
            int batteries = int.TryParse(context.Request.QueryString["batteries"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedBatteries) ? parsedBatteries : 0;
            double charge = double.TryParse(context.Request.QueryString["charge"], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedCharge) ? parsedCharge : -1;
            bool batteriesOff = bool.TryParse(context.Request.QueryString["batteriesOff"], out bool parsedOff) && parsedOff;
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => TrashTestHarness.Spawn(x, y, z, builtBy, isStatic, blocks, spin, npc, owner, mesKey, batteries, charge, batteriesOff)));
        });
        // POST /test-close-entity?entity= - P1-MEM-05: destroy a bridging entity so a partition becomes
        // splittable with no member moving.
        TestRoutes.Add("POST", "/test-close-entity", context =>
        {
            long entity = long.Parse(TestRoutes.Query(context, "entity"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => TrashTestHarness.CloseEntity(entity)));
        });
        // POST /test-remove-block?entity= - SE1-0086: remove a block from its grid (Trade Lanes registry removal)
        TestRoutes.Add("POST", "/test-remove-block", context =>
        {
            long entity = long.Parse(TestRoutes.Query(context, "entity"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => TrashTestHarness.RemoveBlock(entity)));
        });
        // GET /trash-test-close?x=&y=&z=
        TestRoutes.Add("GET", "/trash-test-close", context =>
        {
            double x = TestRoutes.Double(context, "x");
            double y = TestRoutes.Double(context, "y");
            double z = TestRoutes.Double(context, "z");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => TrashTestHarness.Close(x, y, z)));
        });
        // GET /trash-test-state?entityId=
        TestRoutes.Add("GET", "/trash-test-state", context =>
        {
            long entity = TestRoutes.Long(context, "entityId");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => TrashTestHarness.State(entity)));
        });
    }
}
