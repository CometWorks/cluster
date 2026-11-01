using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using SpaceEngineers.Game.EntityComponents.Blocks;
using SpaceEngineers.Game.SessionComponents;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE1-0163 fixture seam: one small dynamic grid per vanilla functional block type, with the state a
/// player would give it (a custom name and data, inventory items, a toolbar action, a program, a
/// store offer, a projection), so the suite can compare every block before and after a handover, an
/// offline round trip and a node restart, and drive the block on the node that holds it now.
/// The block list comes from the suite (generated from the game's CubeBlocks definitions); this
/// side builds whatever it is given and reports what it could not build.
/// </summary>
internal static class BlockCoverageHarness
{
    private const string Prefix = "blk-";
    private static readonly string[] InventoryCandidates =
    {
        "MyObjectBuilder_Component/SteelPlate",
        "MyObjectBuilder_AmmoMagazine/NATO_25x184mm",
        "MyObjectBuilder_AmmoMagazine/Missile200mm",
        "MyObjectBuilder_AmmoMagazine/LargeCalibreAmmo",
        "MyObjectBuilder_AmmoMagazine/AutocannonClip",
        "MyObjectBuilder_AmmoMagazine/SmallRailgunAmmo",
        "MyObjectBuilder_AmmoMagazine/LargeRailgunAmmo",
        "MyObjectBuilder_AmmoMagazine/MediumCalibreAmmo",
        "MyObjectBuilder_AmmoMagazine/FlareClip",
        "MyObjectBuilder_Ingot/Uranium",
        "MyObjectBuilder_Ore/Ice",
    };
    private const string Program =
        "public void Main(string argument) { int n; int.TryParse(Storage, out n); Storage = (n + 1).ToString(); }";

    public static void Register()
    {
        TestRoutes.Add(
            "POST",
            "/block-test-spawn",
            context =>
            {
                Spec spec =
                    JsonSerializer.Deserialize<Spec>(TestRoutes.Body(context), JsonOptions)
                    ?? throw new ArgumentException("Missing spawn body");
                TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Spawn(spec)));
            }
        );
        TestRoutes.Add(
            "GET",
            "/block-test-state",
            context =>
            {
                long[] grids = TestRoutes
                    .Query(context, "grids")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(long.Parse)
                    .ToArray();
                bool ob = TestRoutes.Query(context, "ob", "true") == "true";
                TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => State(grids, ob)));
            }
        );
        TestRoutes.Add(
            "POST",
            "/block-test-act",
            context =>
            {
                long block = TestRoutes.Long(context, "block");
                string action = TestRoutes.Query(context, "action");
                string value = context.Request.QueryString["value"];
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => Act(block, action, value))
                );
            }
        );
        TestRoutes.Add(
            "POST",
            "/block-test-diff",
            context =>
            {
                Pair[] pairs =
                    JsonSerializer.Deserialize<Pair[]>(TestRoutes.Body(context), JsonOptions)
                    ?? throw new ArgumentException("Missing diff body");
                TestRoutes.Json(
                    context,
                    200,
                    pairs.Select(pair => Diff(pair.Left, pair.Right)).ToArray()
                );
            }
        );
        TestRoutes.Add(
            "GET",
            "/block-test-safezones",
            context => TestRoutes.Json(context, 200, TestRoutes.OnGameThread(SafeZones))
        );
        TestRoutes.Add(
            "POST",
            "/block-test-admin-safezone",
            context =>
            {
                string op = TestRoutes.Query(context, "op");
                long id = op == "create" ? 0 : TestRoutes.Long(context, "id");
                Vector3D position =
                    op == "create"
                        ? new Vector3D(
                            TestRoutes.Double(context, "x"),
                            TestRoutes.Double(context, "y"),
                            TestRoutes.Double(context, "z")
                        )
                        : Vector3D.Zero;
                float radius = op == "update" ? (float)TestRoutes.Double(context, "radius") : 0;
                string allowed = TestRoutes.Query(context, "allowed", "None");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => AdminSafeZone(op, id, position, radius, allowed))
                );
            }
        );
        TestRoutes.Add(
            "POST",
            "/block-test-safezone-on",
            context =>
            {
                long block = TestRoutes.Long(context, "block");
                TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => SafeZoneOn(block)));
            }
        );
        TestRoutes.Add(
            "POST",
            "/block-test-store-grid",
            context =>
            {
                long terminal = TestRoutes.Long(context, "terminal");
                long grid = TestRoutes.Long(context, "grid");
                long owner = TestRoutes.Long(context, "owner");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => StoreGrid(terminal, grid, owner))
                );
            }
        );
        TestRoutes.Add(
            "GET",
            "/block-test-stored-grids",
            context => TestRoutes.Json(context, 200, TestRoutes.OnGameThread(StoredGrids))
        );
        TestRoutes.Add(
            "POST",
            "/block-test-retrieve-grid",
            context =>
            {
                long terminal = TestRoutes.Long(context, "terminal");
                Guid id = Guid.Parse(TestRoutes.Query(context, "id"));
                long owner = TestRoutes.Long(context, "owner");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => RetrieveGrid(terminal, id, owner))
                );
            }
        );
        TestRoutes.Add(
            "POST",
            "/block-test-strand-take",
            context =>
                TestRoutes.Json(
                    context,
                    200,
                    StrandTake(Guid.Parse(TestRoutes.Query(context, "id")))
                )
        );
        TestRoutes.Add(
            "POST",
            "/block-test-order-grid",
            context =>
            {
                long terminal = TestRoutes.Long(context, "terminal");
                Guid id = Guid.Parse(TestRoutes.Query(context, "id"));
                long owner = TestRoutes.Long(context, "owner");
                float minutes = (float)TestRoutes.Double(context, "maxMinutes");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => OrderGrid(terminal, id, owner, minutes))
                );
            }
        );
        TestRoutes.Add(
            "POST",
            "/block-test-auto-deploy",
            context =>
            {
                long owner = TestRoutes.Long(context, "owner");
                bool on = TestRoutes.Query(context, "on") == "true";
                TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => AutoDeploy(owner, on)));
            }
        );
        TestRoutes.Add(
            "POST",
            "/block-test-combat",
            context =>
            {
                long grid = TestRoutes.Long(context, "grid");
                bool damage = TestRoutes.Query(context, "damage", "false") == "true";
                int cooldown = int.Parse(TestRoutes.Query(context, "cooldown", "0"));
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => Combat(grid, damage, cooldown))
                );
            }
        );
        TestRoutes.Add(
            "GET",
            "/block-test-request",
            context =>
            {
                string request = TestRoutes.Query(context, "id");
                TestRoutes.Json(
                    context,
                    200,
                    Requests.TryGetValue(request, out string result)
                        ? new
                        {
                            id = request,
                            done = true,
                            result,
                        }
                        : new
                        {
                            id = request,
                            done = false,
                            result = (string)null,
                        }
                );
            }
        );
        TestRoutes.Add(
            "POST",
            "/block-test-contract",
            context =>
            {
                long block = TestRoutes.Long(context, "block");
                long owner = TestRoutes.Long(context, "owner");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => AddContract(block, owner))
                );
            }
        );
        TestRoutes.Add(
            "GET",
            "/block-test-contracts",
            context =>
            {
                long block = TestRoutes.Long(context, "block");
                TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Contracts(block)));
            }
        );
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public sealed class Spec
    {
        public long Owner { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double Spacing { get; set; } = 40;
        public bool Static { get; set; }
        public List<Item> Items { get; set; } = new();
    }

    public sealed class Pair
    {
        public string Left { get; set; }
        public string Right { get; set; }
    }

    /// <summary>The handover gate's comparison (same normalizer, same lazy block id rule, same epsilons),
    /// uncapped, between two documents taken at any two moments.</summary>
    private static List<string> Diff(string left, string right)
    {
        var a = System.Xml.Linq.XDocument.Parse(left);
        var b = System.Xml.Linq.XDocument.Parse(right);
        ClusterPlugin.ImportRoundTrip.NormalizeDocument(a);
        ClusterPlugin.ImportRoundTrip.NormalizeDocument(b);
        ClusterPlugin.ImportRoundTrip.StripLazyBlockEntityIds(a, b);
        var found = new List<string>();
        ClusterPlugin.ImportRoundTrip.Collect(
            a.Root,
            b.Root,
            a.Root?.Name.LocalName ?? "?",
            found,
            int.MaxValue
        );
        return found;
    }

    public sealed class Item
    {
        public string Key { get; set; }
        public string TypeId { get; set; }
        public string Subtype { get; set; }
    }

    // -- spawn ----------------------------------------------------------------------------------

    private static object Spawn(Spec spec)
    {
        var built = new List<object>();
        var failed = new List<object>();
        Vector3D origin = new(spec.X, spec.Y, spec.Z);
        int slot = 0;
        foreach (Item item in spec.Items)
        {
            Vector3D position =
                origin + spec.Spacing * new Vector3D(slot % 6, slot / 6 % 6, slot / 36);
            slot++;
            try
            {
                built.Add(SpawnOne(item, position, spec.Owner, spec.Static));
            }
            catch (Exception error)
            {
                failed.Add(
                    new
                    {
                        key = item.Key,
                        typeId = item.TypeId,
                        subtype = item.Subtype,
                        error = error.Message,
                    }
                );
            }
        }
        return new { built, failed };
    }

    private static MyCubeBlockDefinition Definition(string typeId, string subtype)
    {
        string type = typeId.StartsWith("MyObjectBuilder_", StringComparison.Ordinal)
            ? typeId
            : "MyObjectBuilder_" + typeId;
        if (!MyObjectBuilderType.TryParse(type, out MyObjectBuilderType objectBuilderType))
            throw new ArgumentException("Unknown block type " + typeId);
        var id = new MyDefinitionId(objectBuilderType, subtype ?? string.Empty);
        return MyDefinitionManager.Static.GetCubeBlockDefinition(id)
            ?? throw new ArgumentException("No definition " + id);
    }

    private static object SpawnOne(Item item, Vector3D position, long owner, bool isStatic)
    {
        MyCubeBlockDefinition definition = Definition(item.TypeId, item.Subtype);
        bool large = definition.CubeSize == MyCubeSize.Large;
        var grid = NewGrid(Prefix + item.Key, position, definition.CubeSize, isStatic);
        var block = (MyObjectBuilder_CubeBlock)
            MyObjectBuilderSerializerKeen.CreateNewObject(definition.Id);
        Place(block, Vector3I.Zero, owner);
        if (block is MyObjectBuilder_TerminalBlock terminal)
        {
            terminal.CustomName = Prefix + item.Key;
            terminal.ShowInTerminal = true;
        }
        if (block is MyObjectBuilder_MyProgrammableBlock pb)
            pb.Program = Program;
        if (block is MyObjectBuilder_GravityGeneratorBase gravity)
            gravity.GravityAcceleration = 0f; // its field would push the spaceball and virtual mass fixtures around
        if (block is MyObjectBuilder_ProjectorBase projector)
        {
            projector.ProjectedGrid = ProjectionBlueprint(definition.CubeSize);
            projector.KeepProjection = true;
            projector.Enabled = true;
        }
        if (block is MyObjectBuilder_JumpDrive jump)
            jump.StoredPower = 0.5f;
        grid.CubeBlocks.Add(block);

        Vector3I size = definition.Size;
        bool producer =
            block
            is MyObjectBuilder_BatteryBlock
                or MyObjectBuilder_Reactor
                or MyObjectBuilder_SolarPanel
                or MyObjectBuilder_HydrogenEngine
                or MyObjectBuilder_WindTurbine;
        if (!producer)
        {
            var battery = (MyObjectBuilder_BatteryBlock)
                MyObjectBuilderSerializerKeen.CreateNewObject(
                    new MyDefinitionId(
                        typeof(MyObjectBuilder_BatteryBlock),
                        large ? "LargeBlockBatteryBlock" : "SmallBlockBatteryBlock"
                    )
                );
            Place(battery, new Vector3I(size.X, 0, 0), owner);
            battery.CurrentStoredPower = large ? 3f : 0.05f;
            battery.CustomName = Prefix + item.Key + "-battery";
            grid.CubeBlocks.Add(battery);
        }
        // A target for the blocks that act on other blocks (timer, button panel, sensor, event controller).
        var light = (MyObjectBuilder_InteriorLight)
            MyObjectBuilderSerializerKeen.CreateNewObject(
                new MyDefinitionId(
                    typeof(MyObjectBuilder_InteriorLight),
                    large ? "SmallLight" : "SmallBlockSmallLight"
                )
            );
        Place(light, new Vector3I(size.X + 1, 0, 0), owner);
        light.CustomName = Prefix + item.Key + "-light";
        grid.CubeBlocks.Add(light);

        var built = (MyCubeGrid)(
            MyEntities.CreateFromObjectBuilderAndAdd(grid, fadeIn: false)
            ?? throw new InvalidOperationException("Could not spawn " + item.Key)
        );
        MyCubeBlock fat =
            built.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id == definition.Id)
            ?? throw new InvalidOperationException(
                "The spawned grid lost its " + definition.Id + " block"
            );
        var partners = new List<long>();
        string prepareError = null;
        try
        {
            Prepare(fat, built, owner, partners);
        }
        catch (Exception error)
        {
            prepareError = error.GetType().Name + ": " + error.Message;
        }
        return new
        {
            key = item.Key,
            typeId = item.TypeId,
            subtype = item.Subtype,
            grid = built.EntityId,
            block = fat.EntityId,
            blockType = fat.GetType().Name,
            partners,
            prepareError,
        };
    }

    /// <summary>Gives the block the state a player would: inventory content, a toolbar action on the
    /// light, a store offer, a mechanical head, a partner grid for the blocks that link to one.</summary>
    private static void Prepare(MyCubeBlock block, MyCubeGrid grid, long owner, List<long> partners)
    {
        if (block is MyTerminalBlock terminal)
            terminal.CustomData = "custom data of " + terminal.CustomName;
        MyLightingBlock light = grid.GetFatBlocks()
            .OfType<MyLightingBlock>()
            .FirstOrDefault(b => b != block);
        for (int index = 0; index < block.InventoryCount; index++)
        {
            if (block.GetInventory(index) is not Sandbox.Game.MyInventory inventory)
                continue;
            foreach (string candidate in InventoryCandidates)
            {
                MyDefinitionId id = MyDefinitionId.Parse(candidate);
                if (!inventory.CanItemsBeAdded(5, id))
                    continue;
                inventory.AddItems(5, MyObjectBuilderSerializerKeen.CreateNewObject(id));
                break;
            }
        }
        switch (block)
        {
            case MyTimerBlock timer when light != null:
                SetToolbarAction(timer.Toolbar, light);
                break;
            case MyButtonPanel panel when light != null:
                SetToolbarAction(panel.Toolbar, light);
                break;
            case MySensorBlock sensor when light != null:
                SetToolbarAction(sensor.Toolbar, light);
                break;
            case MyMechanicalConnectionBlockBase:
                // The head is built a frame later by the suite (/block-test-act action=top): a piston
                // base is not ready for RecreateTop in the frame it was added.
                break;
            case MyStoreBlock store when !store.IsDispenser:
            {
                // Above the economy's minimum price; the owner needs a bank account (the suite funds it).
                var inserted = store.InsertOffer(
                    new VRage.Game.ModAPI.Ingame.MyStoreItemDataSimple(
                        MyDefinitionId.Parse("MyObjectBuilder_Component/SteelPlate"),
                        2,
                        5000
                    ),
                    out _
                );
                if (inserted != Sandbox.ModAPI.Ingame.MyStoreInsertResults.Success)
                    throw new InvalidOperationException("Store offer refused: " + inserted);
                break;
            }
            case MyAssembler assembler:
                var blueprint = MyDefinitionManager.Static.GetBlueprintDefinition(
                    MyDefinitionId.Parse("MyObjectBuilder_BlueprintDefinition/SteelPlate")
                );
                if (blueprint != null && assembler.CanUseBlueprint(blueprint))
                    assembler.InsertQueueItem(-1, blueprint, 7);
                break;
            case MyRemoteControl remote:
                ((Sandbox.ModAPI.IMyRemoteControl)remote).AddWaypoint(
                    grid.PositionComp.GetPosition() + new Vector3D(0, 0, 500),
                    "blk-waypoint"
                );
                break;
            case MyDoor door:
                door.Open = true;
                break;
            case MyTextPanel panel:
                ((Sandbox.ModAPI.IMyTextPanel)panel).WriteText("text of " + panel.CustomName);
                break;
            case MyShipConnector:
                partners.Add(SpawnPartner(grid, block, owner, "connector"));
                break;
            case MyLaserAntenna:
                partners.Add(SpawnPartner(grid, block, owner, "laser"));
                break;
            case SpaceEngineers.Game.ModAPI.IMyLandingGear:
                partners.Add(SpawnPartner(grid, block, owner, "slab"));
                break;
        }
    }

    private static void SetToolbarAction(MyToolbar toolbar, MyLightingBlock target)
    {
        var data = new MyObjectBuilder_ToolbarItemTerminalBlock
        {
            BlockEntityId = target.EntityId,
            _Action = "OnOff",
        };
        toolbar.SetItemAtIndex(0, MyToolbarItemFactory.CreateToolbarItem(data));
    }

    /// <summary>The second grid of a linked block: a connector facing this one, a slab under a landing
    /// gear, a laser antenna 300 m above.</summary>
    private static long SpawnPartner(MyCubeGrid grid, MyCubeBlock block, long owner, string kind)
    {
        MyCubeSize size = grid.GridSizeEnum;
        bool large = size == MyCubeSize.Large;
        float cell = grid.GridSize;
        MatrixD world = block.PositionComp.WorldMatrixRef;
        MyObjectBuilder_CubeGrid partner;
        switch (kind)
        {
            case "connector":
            {
                // The connector's connect face is its Forward; mirror it one block length plus a small gap ahead.
                Vector3D at =
                    world.Translation + world.Forward * (cell * block.BlockDefinition.Size.Z + 0.3);
                partner = NewGrid(grid.DisplayName + "-partner", at, size, false);
                var connector = (MyObjectBuilder_CubeBlock)
                    MyObjectBuilderSerializerKeen.CreateNewObject(block.BlockDefinition.Id);
                Place(connector, Vector3I.Zero, owner);
                connector.BlockOrientation = new SerializableBlockOrientation(
                    Base6Directions.Direction.Backward,
                    Base6Directions.Direction.Up
                );
                partner.CubeBlocks.Add(connector);
                var battery = (MyObjectBuilder_BatteryBlock)
                    MyObjectBuilderSerializerKeen.CreateNewObject(
                        new MyDefinitionId(
                            typeof(MyObjectBuilder_BatteryBlock),
                            large ? "LargeBlockBatteryBlock" : "SmallBlockBatteryBlock"
                        )
                    );
                Place(battery, new Vector3I(1, 0, 0), owner);
                battery.CurrentStoredPower = large ? 3f : 0.05f;
                partner.CubeBlocks.Add(battery);
                break;
            }
            case "laser":
            {
                partner = NewGrid(
                    grid.DisplayName + "-partner",
                    world.Translation + new Vector3D(0, 300, 0),
                    size,
                    false
                );
                var laser = (MyObjectBuilder_CubeBlock)
                    MyObjectBuilderSerializerKeen.CreateNewObject(block.BlockDefinition.Id);
                Place(laser, Vector3I.Zero, owner);
                partner.CubeBlocks.Add(laser);
                var battery = (MyObjectBuilder_BatteryBlock)
                    MyObjectBuilderSerializerKeen.CreateNewObject(
                        new MyDefinitionId(
                            typeof(MyObjectBuilder_BatteryBlock),
                            large ? "LargeBlockBatteryBlock" : "SmallBlockBatteryBlock"
                        )
                    );
                Place(battery, new Vector3I(block.BlockDefinition.Size.X, 0, 0), owner);
                battery.CurrentStoredPower = large ? 3f : 0.05f;
                partner.CubeBlocks.Add(battery);
                break;
            }
            default:
            {
                // A slab just under the gear's pad: inside its detection box, clear of a collision.
                Vector3D at =
                    world.Translation
                    + world.Down * (cell * (block.BlockDefinition.Size.Y * 0.5 + 0.5) + 0.2);
                partner = NewGrid(grid.DisplayName + "-slab", at, size, false);
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    var armor = (MyObjectBuilder_CubeBlock)
                        MyObjectBuilderSerializerKeen.CreateNewObject(
                            new MyDefinitionId(
                                typeof(MyObjectBuilder_CubeBlock),
                                large ? "LargeBlockArmorBlock" : "SmallBlockArmorBlock"
                            )
                        );
                    Place(armor, new Vector3I(x, 0, z), owner);
                    partner.CubeBlocks.Add(armor);
                }
                break;
            }
        }
        MyEntity spawned =
            MyEntities.CreateFromObjectBuilderAndAdd(partner, fadeIn: false)
            ?? throw new InvalidOperationException(
                "Could not spawn the partner of " + grid.DisplayName
            );
        return spawned.EntityId;
    }

    private static MyObjectBuilder_CubeGrid ProjectionBlueprint(MyCubeSize size)
    {
        var blueprint = NewGrid("blk-projection", Vector3D.Zero, size, false);
        for (int x = 0; x < 3; x++)
        {
            var armor = (MyObjectBuilder_CubeBlock)
                MyObjectBuilderSerializerKeen.CreateNewObject(
                    new MyDefinitionId(
                        typeof(MyObjectBuilder_CubeBlock),
                        size == MyCubeSize.Large ? "LargeBlockArmorBlock" : "SmallBlockArmorBlock"
                    )
                );
            armor.Min = new SerializableVector3I(x, 0, 0);
            blueprint.CubeBlocks.Add(armor);
        }
        return blueprint;
    }

    private static MyObjectBuilder_CubeGrid NewGrid(
        string name,
        Vector3D position,
        MyCubeSize size,
        bool isStatic
    )
    {
        var grid = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
        grid.GridSizeEnum = size;
        grid.IsStatic = isStatic;
        grid.CreatePhysics = true;
        grid.PersistentFlags =
            MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        grid.PositionAndOrientation = new MyPositionAndOrientation(
            position,
            Vector3D.Forward,
            Vector3D.Up
        );
        grid.DisplayName = name;
        return grid;
    }

    private static void Place(MyObjectBuilder_CubeBlock block, Vector3I min, long owner)
    {
        block.Min = min;
        if (owner != 0)
        {
            block.Owner = owner;
            block.BuiltBy = owner;
            block.ShareMode = MyOwnershipShareModeEnum.All;
        }
    }

    // -- state ----------------------------------------------------------------------------------

    private static object State(long[] gridIds, bool withObjectBuilders)
    {
        var grids = new List<object>();
        foreach (long id in gridIds)
        {
            if (
                !MyEntities.TryGetEntityById(id, out MyEntity entity)
                || entity is not MyCubeGrid grid
                || grid.MarkedForClose
            )
                continue;
            grids.Add(
                new
                {
                    grid = grid.EntityId,
                    name = grid.DisplayName,
                    isStatic = grid.IsStatic,
                    blocks = grid.BlocksCount,
                    position = new[]
                    {
                        grid.PositionComp.GetPosition().X,
                        grid.PositionComp.GetPosition().Y,
                        grid.PositionComp.GetPosition().Z,
                    },
                    fat = grid.GetFatBlocks().Select(Describe).ToList(),
                    // The grid as a handover carries it: the gate's own document shape, compared by /block-test-diff.
                    sector = withObjectBuilders
                        ? ClusterPlugin
                            .ImportRoundTrip.ToDocument(
                                new MyObjectBuilder_EntityBase[] { grid.GetObjectBuilder() }
                            )
                            .ToString()
                        : null,
                }
            );
        }
        return new { grids };
    }

    private static object Describe(MyCubeBlock block)
    {
        var facts = new Dictionary<string, object>();
        var functional = block as MyFunctionalBlock;
        if (functional != null)
            facts["enabled"] = functional.Enabled;
        var inventories = new List<string>();
        for (int index = 0; index < block.InventoryCount; index++)
            if (block.GetInventory(index) is Sandbox.Game.MyInventory inventory)
                inventories.Add(
                    string.Join(
                        ",",
                        inventory
                            .GetItems()
                            .Select(item =>
                                item.Content.GetId().SubtypeName + "=" + item.Amount.ToString()
                            )
                    )
                );
        if (inventories.Count > 0)
            facts["inventories"] = inventories;
        Facts(block, facts);
        return new
        {
            entityId = block.EntityId,
            typeId = block.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", ""),
            subtype = block.BlockDefinition.Id.SubtypeName,
            name = (block as MyTerminalBlock)?.CustomName?.ToString(),
            functional = block.IsFunctional,
            working = block.IsWorking,
            owner = block.OwnerId,
            facts,
        };
    }

    /// <summary>The runtime state that matters for each family, beside its object builder.</summary>
    private static void Facts(MyCubeBlock block, Dictionary<string, object> facts)
    {
        switch (block)
        {
            case MyMechanicalConnectionBlockBase mechanical:
                facts["topBlock"] = mechanical.TopBlock?.EntityId ?? 0;
                facts["topGrid"] = mechanical.TopBlock?.CubeGrid?.EntityId ?? 0;
                break;
            case MyShipConnector connector:
                facts["status"] = ((IMyShipConnector)connector).Status.ToString();
                facts["other"] = connector.Other?.EntityId ?? 0;
                break;
            case SpaceEngineers.Game.ModAPI.IMyLandingGear gear:
                facts["lockMode"] = gear.LockMode.ToString();
                break;
            case MyLaserAntenna laser:
                facts["laserState"] = laser.State.ToString();
                facts["laserTarget"] = laser.TargetId ?? 0;
                break;
            case MyProjectorBase projector:
                facts["projecting"] = projector.ProjectedGrid != null;
                facts["remaining"] = ((IMyProjector)projector).RemainingBlocks;
                break;
            case MyTimerBlock timer:
                facts["countingDown"] = (
                    (SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock)timer
                ).IsCountingDown;
                facts["toolbar0"] = ToolbarSlot(timer.Toolbar);
                break;
            case MyButtonPanel panel:
                facts["toolbar0"] = ToolbarSlot(panel.Toolbar);
                break;
            case MySensorBlock sensor:
                facts["toolbar0"] = ToolbarSlot(sensor.Toolbar);
                facts["active"] = sensor.IsActive;
                break;
            case MyProgrammableBlock pb:
                facts["storage"] = pb.m_instance?.Storage ?? pb.m_storageData;
                facts["compiled"] = pb.m_instance != null;
                break;
            case MyStoreBlock store:
                facts["offers"] = store
                    .PlayerItems.Select(item =>
                        item.Item?.SubtypeName + "x" + item.Amount + "@" + item.PricePerUnit
                    )
                    .ToList();
                break;
            case MySafeZoneBlock safeZone:
            {
                long zone = safeZone.Components.Get<MySafeZoneComponent>()?.SafeZoneEntityId ?? 0;
                facts["safeZone"] = zone;
                facts["safeZoneHere"] =
                    zone != 0 && MyEntities.TryGetEntityById(zone, out MyEntity _);
                facts["zoneChips"] = (double)(
                    safeZone.GetInventory()?.GetItemAmount(ZoneChip) ?? 0
                );
                break;
            }
            case MyBatteryBlock battery:
                facts["storedRatio"] = Math.Round(
                    battery.CurrentStoredPower / Math.Max(battery.MaxStoredPower, 1e-6f),
                    3
                );
                break;
            case MyJumpDrive jump:
                facts["storedRatio"] = Math.Round(
                    ((Sandbox.ModAPI.Ingame.IMyJumpDrive)jump).CurrentStoredPower
                        / Math.Max(
                            ((Sandbox.ModAPI.Ingame.IMyJumpDrive)jump).MaxStoredPower,
                            1e-6f
                        ),
                    3
                );
                break;
            case MyGasTank tank:
                facts["filledRatio"] = Math.Round(tank.FilledRatio, 4);
                break;
            case MyProductionBlock production:
                facts["queue"] = production
                    .Queue.Select(item => item.Blueprint.Id.SubtypeName + "x" + item.Amount)
                    .ToList();
                break;
            case MyRemoteControl remote:
                var waypoints = new List<Sandbox.ModAPI.Ingame.MyWaypointInfo>();
                ((Sandbox.ModAPI.Ingame.IMyRemoteControl)remote).GetWaypointInfo(waypoints);
                facts["waypoints"] = waypoints.Count;
                break;
            case MyDoor door:
                facts["open"] = door.Open;
                break;
            case MyLightingBlock lighting:
                facts["lightEnabled"] = lighting.Enabled;
                break;
        }
        if (block.Components.TryGet(out MyGridsStorageEntityComponent _))
            facts["shipStorage"] = true;
    }

    /// <summary>Slot 0 as saved (target entity id and action): the display name is only filled in by a
    /// toolbar update, so it is empty after any load until the block next refreshes its toolbar.</summary>
    private static string ToolbarSlot(MyToolbar toolbar) =>
        toolbar.GetItemAtIndex(0)?.GetObjectBuilder()
            is MyObjectBuilder_ToolbarItemTerminalBlock item
            ? item.BlockEntityId + ":" + item._Action
            : null;

    // -- acting on a block -----------------------------------------------------------------------

    private static object Act(long blockId, string action, string value)
    {
        if (
            !MyEntities.TryGetEntityById(blockId, out MyEntity entity)
            || entity is not MyCubeBlock block
        )
            throw new KeyNotFoundException("No block " + blockId + " on this node");
        switch (action)
        {
            case "toggle":
            {
                var functional = (MyFunctionalBlock)block;
                functional.Enabled = !functional.Enabled;
                return new
                {
                    block = blockId,
                    action,
                    enabled = functional.Enabled,
                };
            }
            case "trigger":
                switch (block)
                {
                    case MyTimerBlock timer:
                        ((SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock)timer).Trigger();
                        break;
                    case MyButtonPanel panel:
                        // ActivateButton minus the network context: refresh the slot for the owner, then fire it.
                        panel.Toolbar.UpdateItemForIdentity(0, panel.OwnerId, panel.AnyoneCanUse);
                        panel.Toolbar.ActivateItemAtIndex(0);
                        break;
                    case MySensorBlock sensor:
                        // What the sensor does on a detection (MySensorBlock), minus the detection.
                        sensor.Toolbar.UpdateAllItemsAtSlot(0);
                        sensor.Toolbar.ActivateAllItemsAtSlot(0, false, false, false, false);
                        break;
                    default:
                        throw new ArgumentException(
                            "Nothing to trigger on " + block.GetType().Name
                        );
                }
                return new { block = blockId, action };
            case "run":
            {
                var pb = (MyProgrammableBlock)block;
                pb.Run(string.Empty, Sandbox.ModAPI.Ingame.UpdateType.Terminal);
                return new
                {
                    block = blockId,
                    action,
                    storage = pb.m_instance?.Storage,
                    compiled = pb.m_instance != null,
                };
            }
            case "top":
            {
                var mechanical = (MyMechanicalConnectionBlockBase)block;
                if (mechanical.TopBlock == null)
                    mechanical.RecreateTop(
                        null,
                        MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal,
                        true
                    );
                return new
                {
                    block = blockId,
                    action,
                    topGrid = mechanical.TopBlock?.CubeGrid?.EntityId ?? 0,
                };
            }
            case "connect":
            {
                var connector = (MyShipConnector)block;
                connector.TryConnect();
                return new
                {
                    block = blockId,
                    action,
                    status = ((IMyShipConnector)connector).Status.ToString(),
                };
            }
            case "lock":
            {
                var gear = (SpaceEngineers.Game.ModAPI.IMyLandingGear)block;
                gear.Lock();
                return new
                {
                    block = blockId,
                    action,
                    lockMode = gear.LockMode.ToString(),
                };
            }
            case "laser":
            {
                var laser = (MyLaserAntenna)block;
                bool requested = laser.ConnectTo(long.Parse(value));
                return new
                {
                    block = blockId,
                    action,
                    requested,
                    state = laser.State.ToString(),
                };
            }
            default:
                throw new ArgumentException("Unknown action " + action);
        }
    }

    // -- safe zones -------------------------------------------------------------------------------

    private static object SafeZones()
    {
        return new
        {
            zones = MyEntities
                .GetEntities()
                .OfType<MySafeZone>()
                .Where(zone => !zone.MarkedForClose)
                .Select(zone => new
                {
                    entityId = zone.EntityId,
                    block = zone.SafeZoneBlockId,
                    enabled = zone.Enabled,
                    radius = zone.Radius,
                    allowed = zone.AllowedActions.ToString(),
                    access = zone.AccessTypePlayers.ToString(),
                    position = new[]
                    {
                        zone.PositionComp.GetPosition().X,
                        zone.PositionComp.GetPosition().Y,
                        zone.PositionComp.GetPosition().Z,
                    },
                })
                .ToList(),
        };
    }

    private static readonly MyDefinitionId ZoneChip = MyDefinitionId.Parse(
        "MyObjectBuilder_Component/ZoneChip"
    );

    /// <summary>The server half of switching a safe zone block on: zone chips loaded, upkeep paid, the
    /// zone created and activated without the countdown, then two settings changed from their defaults
    /// so the legs can tell a zone that kept them from a fresh one.</summary>
    private static object SafeZoneOn(long blockId)
    {
        if (
            !MyEntities.TryGetEntityById(blockId, out MyEntity entity)
            || entity is not MySafeZoneBlock block
        )
            throw new KeyNotFoundException("No safe zone block " + blockId + " on this node");
        MySafeZoneComponent component =
            block.Components.Get<MySafeZoneComponent>()
            ?? throw new InvalidOperationException("The block has no safe zone component");
        // Before the zone exists: with a zone, a chip arriving restarts its activation countdown.
        block.GetInventory()?.AddItems(5, MyObjectBuilderSerializerKeen.CreateNewObject(ZoneChip));
        component.m_upkeepTime = TimeSpan.FromMilliseconds(
            Sandbox.MySandboxGame.TotalGamePlayTimeInMilliseconds + 3600000.0
        );
        component.m_timeLeft = TimeSpan.FromHours(1);
        component.SafeZoneCreate_Server(skipActivationTimer: true);
        if (MyEntities.TryGetEntityById(component.SafeZoneEntityId, out MySafeZone zone))
        {
            var settings = (MyObjectBuilder_SafeZone)zone.GetObjectBuilder();
            settings.Radius = 25;
            settings.AllowedActions ^= MySafeZoneAction.Welding;
            MySessionComponentSafeZones.UpdateSafeZone(settings, sync: true);
        }
        return new
        {
            block = blockId,
            zone = component.SafeZoneEntityId,
            enabled = component.IsSafeZoneEnabled(),
        };
    }

    /// <summary>SE1-0252: what an admin's client asks the server for in the admin menu's safe zone page, run
    /// through the same server methods its requests land in.</summary>
    private static object AdminSafeZone(
        string op,
        long id,
        Vector3D position,
        float radius,
        string allowed
    )
    {
        switch (op)
        {
            case "create":
                var before = AdminZoneIds();
                MySessionComponentSafeZones.CreateSafeZone_Implementation(position);
                return new { zone = AdminZoneIds().Except(before).Single() };
            case "update":
                if (!MyEntities.TryGetEntityById(id, out MySafeZone zone))
                    throw new KeyNotFoundException("No safe zone " + id + " on this node");
                var settings = (MyObjectBuilder_SafeZone)zone.GetObjectBuilder();
                settings.Shape = MySafeZoneShape.Sphere;
                settings.Radius = radius;
                settings.Enabled = true;
                settings.AllowedActions = Enum.Parse<MySafeZoneAction>(allowed);
                MySessionComponentSafeZones.UpdateSafeZone_Implementation(settings);
                return new { zone = id, radius = zone.Radius };
            case "delete":
                MySessionComponentSafeZones.DeleteSafeZone_Implementation(id);
                return new { zone = id };
            default:
                throw new ArgumentException("Unknown op " + op);
        }
    }

    private static long[] AdminZoneIds() =>
        MyEntities
            .GetEntities()
            .OfType<MySafeZone>()
            .Where(zone => zone.SafeZoneBlockId == 0 && !zone.MarkedForClose)
            .Select(zone => zone.EntityId)
            .ToArray();

    // -- ship storage (Services terminal) ---------------------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        string
    > Requests = new();

    /// <summary>A Steam id no bench client uses (slot ids are 76561199500010000 + 10n + i, n < 100).</summary>
    private const ulong OfflinePlayerSteamId = 76561199500009999;

    private static (MyCubeBlock, MyGridsStorageEntityComponent) Terminal(long terminalId)
    {
        if (
            !MyEntities.TryGetEntityById(terminalId, out MyEntity entity)
            || entity is not MyCubeBlock block
        )
            throw new KeyNotFoundException("No terminal " + terminalId + " on this node");
        return (
            block,
            block.Components.Get<MyGridsStorageEntityComponent>()
                ?? throw new InvalidOperationException(
                    "Block " + terminalId + " has no ship storage component"
                )
        );
    }

    private static MyGridsStorageSessionComponent Storage() =>
        MySession.Static.GetComponent<MyGridsStorageSessionComponent>()
        ?? throw new InvalidOperationException("No ship storage session component on this node");

    /// <summary>What StoreGridTrusted does for a player at the terminal, minus the network context.</summary>
    private static object StoreGrid(long terminalId, long gridId, long owner)
    {
        (_, MyGridsStorageEntityComponent component) = Terminal(terminalId);
        if (
            !MyEntities.TryGetEntityById(gridId, out MyEntity entity)
            || entity is not MyCubeGrid grid
        )
            throw new KeyNotFoundException("No grid " + gridId + " on this node");
        string request = Guid.NewGuid().ToString("N");
        Storage()
            .StoreGrid(
                grid,
                component,
                owner,
                null,
                null,
                result => Requests[request] = result.ToString()
            );
        return new { request };
    }

    /// <summary>
    /// LoadGrid refuses an owner with no player id (MyGridStorageHelper.LoadGrid: TryGetPlayerId).
    /// The suite's owner is an identity without a client; give it the player id a player who has
    /// played on this world before keeps while offline.
    /// </summary>
    private static void EnsurePlayerId(long owner)
    {
        var players = MySession.Static.Players;
        if (!players.TryGetPlayerId(owner, out _))
        {
            var playerId = new MyPlayer.PlayerId(OfflinePlayerSteamId, 0);
            players.m_playerIdentityIds[playerId] = owner;
            players.m_identityPlayerIds[owner] = playerId;
        }
    }

    private static object RetrieveGrid(long terminalId, Guid id, long owner)
    {
        EnsurePlayerId(owner);
        (_, MyGridsStorageEntityComponent component) = Terminal(terminalId);
        string request = Guid.NewGuid().ToString("N");
        Storage()
            .RetrieveGrid(
                id,
                component,
                owner,
                null,
                null,
                result => Requests[request] = result.ToString()
            );
        return new { request };
    }

    /// <summary>
    /// #1316: a retrieval whose node died after the blueprint fetch. The real Take and fetch at the
    /// World Authority, then no paste, Consume or Release, so the WA holds a fetched take of this node
    /// (stranded once held ShipStorageService.FetchedTakeReport). Off the game thread, as both block.
    /// </summary>
    private static object StrandTake(Guid id)
    {
        Guid take = Guid.NewGuid();
        ClusterPlugin.GlobalStateChannel.SendCommand(
            "ship-storage",
            JsonSerializer.Serialize(
                new ClusterPlugin.ShipStorageService.Request("Take", id, Take: take)
            ),
            10000
        );
        string blueprint = ClusterPlugin.GlobalStateChannel.SendCommand(
            "ship-storage-blueprint",
            id.ToString(),
            10000
        );
        return new { take = take.ToString(), blueprintChars = blueprint.Length };
    }

    /// <summary>
    /// What RetrieveGridTrusted does for a delayed retrieval, minus the network context. The
    /// countdown is capped at maxMinutes (GridStorageRetrievalTimeMaxMinutes on this node).
    /// </summary>
    private static object OrderGrid(long terminalId, Guid id, long owner, float maxMinutes)
    {
        Terminal(terminalId);
        MySession.Static.Settings.GridStorageRetrievalTimeMaxMinutes = maxMinutes;
        Storage().EnqueueOrder(owner, id, terminalId);
        return new { queued = Storage().GetCurrentClientQueueLength(owner) };
    }

    /// <summary>The player's auto-deploy switch (SetAutoDeploySwitchEndpoint).</summary>
    private static object AutoDeploy(long owner, bool on)
    {
        EnsurePlayerId(owner);
        Storage().SetAutoDeploySwitch(owner, on);
        return new { autoDeploy = Storage().GetAutoDeploySwitchState(owner) };
    }

    /// <summary>
    /// The grid's storage combat cooldown. damage=true first reports a hit on one of its blocks; a
    /// cooldown (seconds) sets GridStorageCombatCooldown on this node first.
    /// </summary>
    private static object Combat(long gridId, bool damage, int cooldown)
    {
        if (cooldown > 0)
            MySession.Static.Settings.GridStorageCombatCooldown = cooldown;
        if (damage)
        {
            if (
                !MyEntities.TryGetEntityById(gridId, out MyEntity entity)
                || entity is not MyCubeGrid grid
            )
                throw new KeyNotFoundException("No grid " + gridId + " on this node");
            grid.TriggerOnBlockDamaged(grid.CubeBlocks.First(), 1f, null, 0);
        }
        return new { grid = gridId, remainingMs = Storage().GetCombatCooldownRemainingMs(gridId) };
    }

    private static object StoredGrids()
    {
        MyGridsStorageSessionComponent storage = Storage();
        string folder = Path.Combine(MySession.Static.CurrentPath, "Grids");
        var files = new List<string>();
        foreach (string root in new[] { MySession.Static.CurrentPath, folder })
            if (Directory.Exists(root))
                files.AddRange(
                    Directory
                        .EnumerateFiles(root, "*.sbc", SearchOption.AllDirectories)
                        .Where(path =>
                            path.Contains("gs-")
                            || path.StartsWith(folder, StringComparison.Ordinal)
                        )
                        .Select(path => Path.GetRelativePath(MySession.Static.CurrentPath, path))
                );
        return new
        {
            sessionPath = MySession.Static.CurrentPath,
            grids = storage
                .GetStoredGridsData()
                .Select(pair => new
                {
                    id = pair.Key.ToString(),
                    owner = pair.Value.OwnerId,
                    name = pair.Value.SavedGridDetails?.DisplayName,
                    inTransition = pair.Value.InTransition,
                })
                .ToList(),
            files = files.Distinct().OrderBy(path => path).ToList(),
            users = storage
                .m_clientOrders.Select(pair => new
                {
                    identity = pair.Key,
                    autoDeploy = pair.Value.AutoDeploy,
                    orders = pair
                        .Value.Orders.Select(order => new
                        {
                            id = order.Id.ToString(),
                            name = order.DisplayName,
                            countdownMs = order.CountdownMs,
                            inProgress = order.InProgress,
                            terminal = order.CallerEntityId,
                        })
                        .ToList(),
                })
                .ToList(),
        };
    }

    // -- contracts --------------------------------------------------------------------------------

    private static object AddContract(long blockId, long owner)
    {
        if (
            !MyEntities.TryGetEntityById(blockId, out MyEntity entity)
            || entity is not MyContractBlock block
        )
            throw new KeyNotFoundException("No contract block " + blockId + " on this node");
        var contract = new Sandbox.ModAPI.Contracts.MyContractAcquisition(
            blockId,
            1000,
            0,
            3600,
            blockId,
            MyDefinitionId.Parse("MyObjectBuilder_Component/SteelPlate"),
            3
        );
        var result = Sandbox.ModAPI.MyAPIGateway.ContractSystem.AddContract(contract);
        return new
        {
            block = blockId,
            success = result.Success,
            contractId = result.ContractId,
            owner,
        };
    }

    private static object Contracts(long blockId)
    {
        var system =
            MySession.Static.GetComponent<Sandbox.Game.SessionComponents.MySessionComponentContractSystem>();
        var ids = new List<long>();
        if (system != null)
        {
            if (system.m_inactiveContractsForBlock.TryGetValue(blockId, out var available))
                foreach (var contract in available)
                    ids.Add(contract.Id);
        }
        return new
        {
            block = blockId,
            contracts = ids,
            here = MyEntities.EntityExists(blockId),
        };
    }
}
