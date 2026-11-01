using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ClusterPlugin;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-855 test seam: programmable blocks that really run scripts on a node, for testing the Performance plugin's
/// parallel PB phase on a cluster (scenarios_pb_worker.py). A station is a line of armor with a battery on each end,
/// an antenna, an LCD, lights and the PBs, so that razing the middle of the line splits it in two working grids.
/// </summary>
internal static class PbTestHarness
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record PbSpec(string Name, string Program, string Storage);

    private sealed record StationSpec(
        string Name,
        long Owner,
        double Lateral,
        int Length,
        PbSpec[] Pbs
    );

    private static readonly FieldInfo Instance = Field("m_instance");
    private static readonly FieldInfo Termination = Field("m_terminationReason");
    private static readonly FieldInfo ProgramData = Field("m_programData");
    private static readonly MethodInfo Recompile = typeof(MyProgrammableBlock).GetMethod(
        "Recompile",
        BindingFlags.NonPublic | BindingFlags.Instance
    );

    private static FieldInfo Field(string name) =>
        typeof(MyProgrammableBlock).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

    public static void Register()
    {
        TestRoutes.Add(
            "POST",
            "/pb-test-spawn",
            context =>
            {
                StationSpec spec =
                    JsonSerializer.Deserialize<StationSpec>(TestRoutes.Body(context), JsonOptions)
                    ?? throw new ArgumentException("Missing spawn body");
                TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Spawn(spec)));
            }
        );
        TestRoutes.Add(
            "GET",
            "/pb-test-state",
            context => TestRoutes.Json(context, 200, TestRoutes.OnGameThread(State))
        );
        TestRoutes.Add(
            "GET",
            "/pb-test-scheduler",
            context => TestRoutes.Json(context, 200, TestRoutes.OnGameThread(Scheduler))
        );
        TestRoutes.Add(
            "POST",
            "/pb-test-program",
            context =>
            {
                long block = TestRoutes.Long(context, "block");
                string program = JsonSerializer.Deserialize<string>(TestRoutes.Body(context));
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => SetProgram(block, program))
                );
            }
        );
        TestRoutes.Add(
            "POST",
            "/pb-test-op",
            context =>
            {
                long grid = TestRoutes.Long(context, "grid");
                string op = TestRoutes.Query(context, "op");
                long other = long.Parse(context.Request.QueryString["other"] ?? "0");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => Operate(grid, op, other))
                );
            }
        );
    }

    private static T Block<T>(string subtype, int x, int y, long owner, string name = null)
        where T : MyObjectBuilder_CubeBlock, new()
    {
        T block = MyObjectBuilderSerializerKeen.CreateNewObject<T>(subtype);
        block.Min = new SerializableVector3I(x, y, 0);
        block.Owner = owner;
        block.BuiltBy = owner;
        block.ShareMode = MyOwnershipShareModeEnum.Faction;
        if (block is MyObjectBuilder_TerminalBlock terminal && name != null)
            terminal.CustomName = name;
        return block;
    }

    private static object Spawn(StationSpec spec)
    {
        // As IgcTestHarness: 20 km above the players, a lateral offset apart; an isolated position is a partition
        ClusterPlayerPosition anyPlayer =
            PositionRegistryService.Players.FirstOrDefault(player => player.ClientId != 0)
            ?? throw new InvalidOperationException(
                "No cluster player position to anchor the station"
            );
        Vector3D reference = new(anyPlayer.X, anyPlayer.Y, anyPlayer.Z);
        MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(reference);
        Vector3D up =
            planet == null
                ? Vector3D.Up
                : Vector3D.Normalize(reference - planet.PositionComp.GetPosition());
        Vector3D position =
            reference + up * 20000.0 + Vector3D.CalculatePerpendicularVector(up) * spec.Lateral;

        var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
        builder.GridSizeEnum = MyCubeSize.Large;
        builder.IsStatic = true;
        builder.CreatePhysics = true;
        builder.PersistentFlags =
            MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        builder.PositionAndOrientation = new MyPositionAndOrientation(
            position,
            Vector3D.Forward,
            Vector3D.Up
        );
        builder.DisplayName = spec.Name;

        int length = Math.Max(spec.Length, spec.Pbs.Length * 2 + 8);
        for (int x = 0; x < length; x++)
            builder.CubeBlocks.Add(
                Block<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock", x, 0, spec.Owner)
            );

        foreach (int x in new[] { 0, length - 1 })
        {
            var battery = Block<MyObjectBuilder_BatteryBlock>(
                "LargeBlockBatteryBlock",
                x,
                1,
                spec.Owner,
                $"pbw-battery-{x}"
            );
            battery.CurrentStoredPower = 3f;
            battery.Enabled = true;
            builder.CubeBlocks.Add(battery);
        }

        var antenna = Block<MyObjectBuilder_RadioAntenna>(
            "LargeBlockRadioAntenna",
            1,
            1,
            spec.Owner,
            spec.Name + "-antenna"
        );
        antenna.Enabled = true;
        antenna.BroadcastRadius = 50000f;
        antenna.EnableBroadcasting = true;
        builder.CubeBlocks.Add(antenna);

        for (int x = 0; x < length; x += 3)
        {
            var light = Block<MyObjectBuilder_InteriorLight>(
                "SmallLight",
                x,
                -1,
                spec.Owner,
                $"pbw-light-{x}"
            );
            light.Enabled = true;
            builder.CubeBlocks.Add(light);
        }

        int at = 2;
        foreach (PbSpec pb in spec.Pbs)
        {
            at += 1;
            var block = Block<MyObjectBuilder_MyProgrammableBlock>(
                "LargeProgrammableBlock",
                at,
                1,
                spec.Owner,
                pb.Name
            );
            block.Enabled = true;
            block.Program = pb.Program;
            block.Storage = pb.Storage ?? "";
            builder.CubeBlocks.Add(block);
        }

        var panel = Block<MyObjectBuilder_TextPanel>(
            "LargeTextPanel",
            length - 2,
            1,
            spec.Owner,
            "pbw-lcd"
        );
        panel.Enabled = true;
        builder.CubeBlocks.Add(panel);

        MyEntity spawned =
            MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false)
            ?? throw new InvalidOperationException("Could not spawn the station");
        return new { grid = spawned.EntityId, length };
    }

    private static object State() =>
        new
        {
            pbs = MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .SelectMany(grid => grid.GetFatBlocks<MyProgrammableBlock>())
                .Where(pb => pb.CustomName.ToString().StartsWith("pbw-"))
                .Select(pb => new
                {
                    id = pb.EntityId,
                    grid = pb.CubeGrid.EntityId,
                    gridName = pb.CubeGrid.DisplayName,
                    name = pb.CustomName.ToString(),
                    working = pb.IsWorking,
                    instance = Instance.GetValue(pb) != null,
                    termination = Termination.GetValue(pb)?.ToString(),
                    customData = pb.CustomData,
                    info = pb.DetailedInfo?.ToString(),
                    storage = ((IMyProgrammableBlock)pb).StorageData,
                })
                .ToArray(),
            grids = MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .Where(grid =>
                    grid.GetFatBlocks<MyBatteryBlock>()
                        .Any(b => b.CustomName.ToString().StartsWith("pbw-"))
                )
                .Select(grid => new { id = grid.EntityId, blocks = grid.BlocksCount })
                .ToArray(),
            simRatio = Sync.ServerSimulationRatio,
        };

    private static object Scheduler()
    {
        Type type = AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Shared.Patches.ScriptScheduler"))
            .FirstOrDefault(found => found != null);
        if (type == null)
            return new { found = false };
        const BindingFlags flags =
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        object Read(string name) =>
            type.GetField(name, flags)?.GetValue(null)
            ?? type.GetProperty(name, flags)?.GetValue(null);
        Type runner = type.Assembly.GetType("Shared.Patches.ScriptRunner");
        return new
        {
            found = true,
            perThread = runner?.GetField("CounterPerThread", flags)?.GetValue(null),
            enabled = Read("Enabled"),
            parallel = Read("Parallel"),
            threads = Read("Threads"),
            frames = Read("frames"),
            dueRuns = Read("dueRuns"),
            ranRuns = Read("ranRuns"),
            parallelRuns = Read("parallelRuns"),
            overloadFrames = Read("overloadFrames"),
            failedWrites = Read("failedWrites"),
        };
    }

    private static object SetProgram(long block, string program)
    {
        if (
            !MyEntities.TryGetEntityById(block, out MyEntity entity)
            || entity is not MyProgrammableBlock pb
        )
            throw new KeyNotFoundException("No programmable block " + block);
        ProgramData.SetValue(pb, program);
        Recompile.Invoke(pb, new object[] { true });
        return new { block };
    }

    private static object Operate(long gridId, string op, long other)
    {
        if (
            !MyEntities.TryGetEntityById(gridId, out MyEntity entity)
            || entity is not MyCubeGrid grid
        )
            throw new KeyNotFoundException("No grid " + gridId);
        switch (op)
        {
            case "split":
                // The middle of the armor line
                int max = grid.GetBlocks().Where(b => b.Position.Y == 0).Max(b => b.Position.X);
                grid.RazeBlock(new Vector3I(max / 2, 0, 0));
                return new { op, at = max / 2 };
            case "merge":
                if (
                    !MyEntities.TryGetEntityById(other, out MyEntity otherEntity)
                    || otherEntity is not MyCubeGrid otherGrid
                )
                    throw new KeyNotFoundException("No grid " + other);
                // Back where it was split off: both grids keep the station's coordinates
                MyCubeGrid merged = grid.MergeGrid_MergeBlock(
                    otherGrid,
                    Vector3I.Zero,
                    checkMergeOrder: false
                );
                return new { op, grid = merged?.EntityId ?? 0 };
            case "close":
                grid.Close();
                return new { op };
            case "owner":
                foreach (MyProgrammableBlock pb in grid.GetFatBlocks<MyProgrammableBlock>())
                    pb.ChangeOwner(other, MyOwnershipShareModeEnum.Faction);
                return new { op };
            default:
                throw new ArgumentException("Unknown op " + op);
        }
    }
}
