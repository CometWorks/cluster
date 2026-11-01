using System;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// T-0401 bench: paste a ship blueprint (bp.sbc on the node's host) as the live pilots' ships are, parked: dynamic
/// unless static=true, zero velocity, every block owned by <c>owner</c> (0 = unowned), no AI switched on, nobody
/// seated. The live cluster's ships are 'Visitor Ion' pastes (40 blocks); the bench handover-imports K of them to
/// time MyEntities.Add on a realistic grid. /ai-route-test-spawn reads the same file format but puts its ship on an
/// active AI route; this route only pastes.
/// </summary>
internal static class BlueprintTestRoutes
{
    public static void Register()
    {
        // POST /blueprint-test-spawn?path=&x=&y=&z=[&owner=][&static=][&name=] - every grid of the blueprint's first
        // ship, moved together so its first grid sits at (x, y, z) in the blueprint's own orientation.
        TestRoutes.Add("POST", "/blueprint-test-spawn", context =>
        {
            string path = TestRoutes.Query(context, "path");
            Vector3D position = new(TestRoutes.Double(context, "x"), TestRoutes.Double(context, "y"), TestRoutes.Double(context, "z"));
            long owner = long.TryParse(context.Request.QueryString["owner"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedOwner) ? parsedOwner : 0;
            bool isStatic = bool.TryParse(context.Request.QueryString["static"], out bool parsedStatic) && parsedStatic;
            string name = context.Request.QueryString["name"];
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Spawn(path, position, owner, isStatic, name)));
        });
    }

    private static object Spawn(string path, Vector3D position, long owner, bool isStatic, string name)
    {
        if (!MyObjectBuilderSerializerKeen.DeserializeXML(path, out MyObjectBuilder_Definitions definitions)
            || definitions?.ShipBlueprints == null || definitions.ShipBlueprints.Length == 0)
            throw new InvalidOperationException("Could not read blueprint " + path);
        MyObjectBuilder_CubeGrid[] grids = definitions.ShipBlueprints[0].CubeGrids;
        if (grids == null || grids.Length == 0) throw new InvalidOperationException("Blueprint " + path + " has no grid");
        Vector3D origin = grids[0].PositionAndOrientation?.Position ?? Vector3D.Zero;
        MyEntities.RemapObjectBuilderCollection(grids);
        foreach (MyObjectBuilder_CubeGrid grid in grids)
        {
            MyPositionAndOrientation placed = grid.PositionAndOrientation ?? new MyPositionAndOrientation(origin, Vector3.Forward, Vector3.Up);
            grid.PositionAndOrientation = new MyPositionAndOrientation(position + ((Vector3D)placed.Position - origin), placed.Forward, placed.Up);
            grid.IsStatic = isStatic;
            grid.CreatePhysics = true;
            grid.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
            grid.LinearVelocity = new SerializableVector3(0, 0, 0);
            grid.AngularVelocity = new SerializableVector3(0, 0, 0);
            if (!string.IsNullOrEmpty(name)) grid.DisplayName = name;
            foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
            }
        }
        MyCubeGrid[] spawned = grids.Select(grid => MyEntities.CreateFromObjectBuilderAndAdd(grid, fadeIn: false) as MyCubeGrid
            ?? throw new InvalidOperationException("Could not spawn a grid of " + path)).ToArray();
        // blocks = the runtime count (what GridAddTrace's >= 10 floor reads), builderBlocks = the file's.
        return new
        {
            grids = spawned.Select(grid => grid.EntityId).ToArray(),
            blocks = spawned.Select(grid => grid.BlocksCount).ToArray(),
            builderBlocks = grids.Select(grid => grid.CubeBlocks.Count).ToArray(),
            name = spawned[0].DisplayName,
            owner,
            isStatic,
        };
    }
}
