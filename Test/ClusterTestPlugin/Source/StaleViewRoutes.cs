using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Replication;
using Sandbox.Game.Replication.StateGroups;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Replication;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1554 bench routes: what a client still holds of a grid after its view moved to another node. One fixture
/// grid (small cargo container, battery, seat, ore detector), ore put into the container, and the server's side
/// of each block: its inventory, and what the node believes the client has of it.
///
/// A change of an inventory reaches a client within a few frames, so a handover finds one undelivered only by
/// a race: the reporter was mining through two handovers. Here the two undelivered states are made to last.
/// HOLD: this node schedules no send of the block's inventories to the client, so a change waits as the unsent
/// diff (a Harmony prefix on MyReplicationServer.ScheduleStateGroupSync, installed on first arm, on this test
/// plugin only; the product has no knob for it). LOSE: what a delta lost on the way leaves behind on the node:
/// the diff is taken, gets its message id and is recorded as sent, and nothing reaches the client. It is
/// recorded under a packet id no state sync packet carries it in, so no acknowledgement ever settles it.
/// </summary>
internal static class StaleViewRoutes
{
    private const string Prefix = "stale-view-";
    private const byte LostPacketId = 255;
    private static ulong m_heldClient;
    private static long m_heldBlock;
    private static long m_heldSends;
    private static bool m_patched;

    private static readonly (string Key, Type Type, string Subtype)[] Fixture =
    {
        ("cargo", typeof(MyObjectBuilder_CargoContainer), "LargeBlockSmallContainer"),
        ("battery", typeof(MyObjectBuilder_BatteryBlock), "LargeBlockBatteryBlock"),
        ("seat", typeof(MyObjectBuilder_Cockpit), "LargeBlockCockpitSeat"),
        ("detector", typeof(MyObjectBuilder_OreDetector), "LargeOreDetector"),
    };

    public static void Register()
    {
        // POST /stale-view-test-spawn?x=&y=&z=&static=true - the fixture grid, its blocks in a row along +X.
        TestRoutes.Add(
            "POST",
            "/stale-view-test-spawn",
            context =>
            {
                var position = new Vector3D(
                    Double(context, "x"),
                    Double(context, "y"),
                    Double(context, "z")
                );
                bool isStatic = TestRoutes.Query(context, "static", "true") == "true";
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => Spawn(position, isStatic))
                );
            }
        );
        // POST /stale-view-test-items?block=&subtype=Iron&amount=100 - ore into the block's first inventory.
        TestRoutes.Add(
            "POST",
            "/stale-view-test-items",
            context =>
            {
                long block = TestRoutes.Long(context, "block");
                string subtype = TestRoutes.Query(context, "subtype", "Iron");
                double amount = double.Parse(
                    TestRoutes.Query(context, "amount", "100"),
                    CultureInfo.InvariantCulture
                );
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        MyInventory inventory =
                            Block(block).GetInventory(0)
                            ?? throw new ArgumentException("No inventory on " + block);
                        var ore = (MyObjectBuilder_PhysicalObject)
                            MyObjectBuilderSerializerKeen.CreateNewObject(
                                new MyDefinitionId(typeof(MyObjectBuilder_Ore), subtype)
                            );
                        bool added = inventory.AddItems((MyFixedPoint)amount, ore);
                        return State(block, 0, added);
                    })
                );
            }
        );
        // POST /stale-view-test-hold?block=&client=&armed=true|false - hold, or let go of, the sends of the block's
        // inventories to the client.
        TestRoutes.Add(
            "POST",
            "/stale-view-test-hold",
            context =>
            {
                long block = TestRoutes.Long(context, "block");
                ulong client = ulong.Parse(
                    TestRoutes.Query(context, "client"),
                    CultureInfo.InvariantCulture
                );
                bool armed = TestRoutes.Query(context, "armed", "true") == "true";
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        EnsurePatched();
                        m_heldClient = client;
                        Interlocked.Exchange(ref m_heldBlock, armed ? block : 0);
                        if (
                            !armed
                            && MyMultiplayer.Static?.ReplicationLayer is MyReplicationServer server
                        )
                            foreach (MyEntityInventoryStateGroup group in Groups(Block(block)))
                                server.AddToDirtyGroups(group);
                        Plugin.Log(
                            $"stale-view-test-hold armed={armed} client={client} block={block} heldSends={Interlocked.Read(ref m_heldSends)} (SE-1554)"
                        );
                        return (object)
                            new
                            {
                                armed,
                                client,
                                block,
                                heldSends = Interlocked.Read(ref m_heldSends),
                            };
                    })
                );
            }
        );
        // POST /stale-view-test-lose?block=&client= - the block's waiting inventory change as a delta that was sent
        // and never arrived.
        TestRoutes.Add(
            "POST",
            "/stale-view-test-lose",
            context =>
            {
                long block = TestRoutes.Long(context, "block");
                ulong client = ulong.Parse(
                    TestRoutes.Query(context, "client"),
                    CultureInfo.InvariantCulture
                );
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        var lost = new List<uint>();
                        var endpoint = new Endpoint(new EndpointId(client), 0);
                        foreach (MyEntityInventoryStateGroup group in Groups(Block(block)))
                        {
                            if (
                                group.m_clientInventoryUpdate == null
                                || !group.m_clientInventoryUpdate.TryGetValue(
                                    endpoint,
                                    out var data
                                )
                                || !data.Dirty
                            )
                                continue;
                            if (data.SendPackets.ContainsKey(LostPacketId))
                                throw new InvalidOperationException("One lost delta per inventory");
                            MyEntityInventoryStateGroup.InventoryDeltaInformation delta =
                                group.CalculateInventoryDiff(ref data);
                            delta.MessageId = data.CurrentMessageId++;
                            data.SendPackets[LostPacketId] = delta;
                            data.Dirty = false;
                            lost.Add(delta.MessageId);
                        }
                        Plugin.Log(
                            $"stale-view-test-lose client={client} block={block} messageIds=[{string.Join(",", lost)}] (SE-1554)"
                        );
                        return (object)
                            new
                            {
                                client,
                                block,
                                lost,
                            };
                    })
                );
            }
        );
        // GET /stale-view-test-cell?grid=&x=&y=&z= - the block at a grid cell on this node: its entity id (0 for a
        // block without one, as armor) and type, or found=false.
        TestRoutes.Add(
            "GET",
            "/stale-view-test-cell",
            context =>
            {
                long gridId = TestRoutes.Long(context, "grid");
                var cell = new Vector3I(
                    (int)Double(context, "x"),
                    (int)Double(context, "y"),
                    (int)Double(context, "z")
                );
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        if (
                            !MyEntities.TryGetEntityById(gridId, out MyEntity entity)
                            || entity is not MyCubeGrid grid
                            || grid.MarkedForClose
                        )
                            throw new KeyNotFoundException("No grid " + gridId + " on this node");
                        MySlimBlock slim = grid.GetCubeBlock(cell);
                        return (object)
                            new
                            {
                                grid = gridId,
                                found = slim != null,
                                block = slim?.FatBlock?.EntityId ?? 0,
                                type = slim?.BlockDefinition.Id.ToString(),
                                buildRatio = slim?.BuildLevelRatio,
                                blocks = grid.BlocksCount,
                            };
                    })
                );
            }
        );
        // GET /stale-view-test-state?block=&client= - the block on this node, and this node's view of the client.
        TestRoutes.Add(
            "GET",
            "/stale-view-test-state",
            context =>
            {
                long block = TestRoutes.Long(context, "block");
                ulong client = ulong.Parse(
                    TestRoutes.Query(context, "client", "0"),
                    CultureInfo.InvariantCulture
                );
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => State(block, client, null))
                );
            }
        );
    }

    private static IEnumerable<MyEntityInventoryStateGroup> Groups(MyCubeBlock block)
    {
        for (int i = 0; i < block.InventoryCount; i++)
            if (
                MyExternalReplicable.FindByObject(block.GetInventory(i))
                    is MyInventoryReplicable replicable
                && replicable.m_stateGroup != null
            )
                yield return replicable.m_stateGroup;
    }

    private static void EnsurePatched()
    {
        if (m_patched)
            return;
        var original =
            AccessTools.Method(typeof(MyReplicationServer), "ScheduleStateGroupSync")
            ?? throw new MissingMethodException("MyReplicationServer.ScheduleStateGroupSync");
        new Harmony("ClusterTestPlugin.StaleView").Patch(
            original,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(StaleViewRoutes), nameof(ScheduleStateGroupSyncPrefix))
            )
        );
        m_patched = true;
    }

    private static bool ScheduleStateGroupSyncPrefix(MyClient client, MyStateDataEntry groupEntry)
    {
        long block = Interlocked.Read(ref m_heldBlock);
        if (block == 0 || client?.State == null || client.State.EndpointId.Id.Value != m_heldClient)
            return true;
        if (
            groupEntry?.Group is not MyEntityInventoryStateGroup group
            || group.Inventory?.Owner?.EntityId != block
        )
            return true;
        Interlocked.Increment(ref m_heldSends);
        return false;
    }

    private static double Double(System.Net.HttpListenerContext context, string name) =>
        double.Parse(TestRoutes.Query(context, name), CultureInfo.InvariantCulture);

    private static MyCubeBlock Block(long id) =>
        MyEntities.TryGetEntityById(id, out MyEntity entity)
        && entity is MyCubeBlock block
        && !block.MarkedForClose
            ? block
            : throw new KeyNotFoundException("No block " + id + " on this node");

    private static object Spawn(Vector3D position, bool isStatic)
    {
        var grid = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
        grid.GridSizeEnum = MyCubeSize.Large;
        grid.IsStatic = isStatic;
        grid.CreatePhysics = true;
        grid.PersistentFlags =
            MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        grid.PositionAndOrientation = new MyPositionAndOrientation(
            position,
            Vector3D.Forward,
            Vector3D.Up
        );
        grid.DisplayName = Prefix + "grid";
        int x = 0;
        foreach ((string key, Type type, string subtype) in Fixture)
        {
            var id = new MyDefinitionId(type, subtype);
            MyCubeBlockDefinition definition =
                MyDefinitionManager.Static.GetCubeBlockDefinition(id)
                ?? throw new ArgumentException("No definition " + id);
            var block = (MyObjectBuilder_CubeBlock)
                MyObjectBuilderSerializerKeen.CreateNewObject(id);
            block.Min = new Vector3I(x, 0, 0);
            x += definition.Size.X;
            if (block is MyObjectBuilder_TerminalBlock terminal)
            {
                terminal.CustomName = Prefix + key;
                terminal.ShowInTerminal = true;
            }
            if (block is MyObjectBuilder_BatteryBlock battery)
                battery.CurrentStoredPower = 3f;
            grid.CubeBlocks.Add(block);
        }
        var built = (MyCubeGrid)(
            MyEntities.CreateFromObjectBuilderAndAdd(grid, fadeIn: false)
            ?? throw new InvalidOperationException("Could not spawn the fixture grid")
        );
        return new
        {
            grid = built.EntityId,
            blocks = built
                .GetFatBlocks()
                .OfType<MyTerminalBlock>()
                .ToDictionary(
                    block => block.CustomName.ToString().Substring(Prefix.Length),
                    block =>
                        (object)
                            new
                            {
                                block = block.EntityId,
                                min = new[] { block.Min.X, block.Min.Y, block.Min.Z },
                                position = Xyz(block.PositionComp.WorldAABB.Center),
                            }
                ),
        };
    }

    private static double[] Xyz(Vector3D v) => new[] { v.X, v.Y, v.Z };

    private static object State(long blockId, ulong clientId, bool? added)
    {
        MyCubeBlock block = Block(blockId);
        MyReplicationServer server = MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer;
        MyClient client =
            clientId == 0
                ? null
                : server?.m_clientStates.Values.FirstOrDefault(c =>
                    c.State.EndpointId.Id.Value == clientId
                );
        var inventories = new List<object>();
        for (int i = 0; i < block.InventoryCount; i++)
        {
            MyInventory inventory = block.GetInventory(i);
            MyEntityInventoryStateGroup group = null;
            bool? held = null;
            uint networkId = 0;
            if (
                MyExternalReplicable.FindByObject(inventory) is MyInventoryReplicable replicable
                && server != null
            )
            {
                try
                {
                    networkId = server.GetNetworkIdByObject(replicable).Value;
                }
                catch (Exception)
                {
                    networkId = 0;
                }
                group = replicable.m_stateGroup;
                if (client != null)
                    held = client.Replicables.ContainsKey(replicable);
            }
            MyEntityInventoryStateGroup.InventoryClientData data = null;
            if (client != null)
                group?.m_clientInventoryUpdate?.TryGetValue(client.State.EndpointId, out data);
            inventories.Add(
                new
                {
                    index = i,
                    mass = (double)inventory.CurrentMass,
                    volume = (double)inventory.CurrentVolume,
                    items = inventory
                        .GetItems()
                        .Select(item => new
                        {
                            itemId = item.ItemId,
                            type = item.Content.TypeId.ToString(),
                            subtype = item.Content.SubtypeName,
                            amount = (double)item.Amount,
                        })
                        .ToArray(),
                    networkId,
                    clientHolds = held,
                    clientData = data == null
                        ? null
                        : new
                        {
                            dirty = data.Dirty,
                            messageId = data.CurrentMessageId,
                            failed = data.FailedIncompletePackets.Count,
                            unacked = data.SendPackets.Count,
                            items = data
                                .ClientItems.Select(item => new
                                {
                                    itemId = item.Item.ItemId,
                                    amount = (double)item.Amount,
                                })
                                .ToArray(),
                        },
                }
            );
        }
        uint blockNetworkId = 0;
        bool? blockHeld = null;
        IMyReplicable blockReplicable = MyExternalReplicable.FindByObject(block);
        if (blockReplicable != null && server != null)
        {
            try
            {
                blockNetworkId = server.GetNetworkIdByObject(blockReplicable).Value;
            }
            catch (Exception)
            {
                blockNetworkId = 0;
            }
            if (client != null)
                blockHeld = client.Replicables.ContainsKey(blockReplicable);
        }
        return new
        {
            block = blockId,
            grid = block.CubeGrid.EntityId,
            type = block.GetType().Name,
            added,
            functional = block.IsFunctional,
            working = block.IsWorking,
            enabled = (block as MyFunctionalBlock)?.Enabled,
            name = (block as MyTerminalBlock)?.CustomName.ToString(),
            replicable = blockReplicable?.GetType().Name,
            networkId = blockNetworkId,
            clientHolds = blockHeld,
            clientContext = client == null
                ? null
                : ((MyClientState)client.State).Context.ToString(),
            inventories,
        };
    }
}
