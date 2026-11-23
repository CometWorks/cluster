using System;
using System.Diagnostics;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1892 bench route: where a bulk store purchase spends its frame. A station purchase of N units runs vanilla's
/// item loop of MyStoreBlock.BuyPhysicalItemInternal: N times CreateNewObject and inventory.AddItems(1, item). Live
/// (2026-11-23) the buys of 20,000 or more ingots were in 300-840 ms frames and the smaller ones never. The route
/// runs that loop, or the one AddItems(N, item) the fix makes of it, into a large cargo container of a dynamic ship,
/// and answers its game-thread time and the inventory's amount.
/// </summary>
internal static class StoreBulkBuyRoutes
{
    private static MyCargoContainer m_cargo;

    public static void Register()
    {
        // POST /test-store-bulk-add?amount=N&mode=loop|batched[&item=Iron] - one bulk add on the game thread.
        TestRoutes.Add(
            "POST",
            "/test-store-bulk-add",
            context =>
            {
                int amount = (int)TestRoutes.Long(context, "amount");
                string mode = context.Request.QueryString["mode"] ?? "loop";
                string item = context.Request.QueryString["item"] ?? "Iron";
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => Run(amount, mode, item))
                );
            }
        );
    }

    private static object Run(int amount, string mode, string item)
    {
        MyCargoContainer cargo = Cargo();
        MyInventory inventory = cargo.GetInventory();
        inventory.Clear();
        var definition = new SerializableDefinitionId(typeof(MyObjectBuilder_Ingot), item);
        Stopwatch watch = Stopwatch.StartNew();
        if (mode == "loop")
            for (int i = 0; i < amount; i++)
                inventory.AddItems(1, MyObjectBuilderSerializerKeen.CreateNewObject(definition));
        else if (mode == "batched")
            inventory.AddItems(amount, MyObjectBuilderSerializerKeen.CreateNewObject(definition));
        else
            throw new ArgumentException("mode is loop or batched");
        double ms = watch.Elapsed.TotalMilliseconds;
        Plugin.Log($"store-bulk-add mode={mode} amount={amount} item={item} ms={ms:F1} (SE-1892)");
        return new
        {
            mode,
            amount,
            item,
            ms,
            stacks = inventory.GetItems().Count,
            inInventory = (double)inventory.GetItemAmount(definition),
        };
    }

    private static MyCargoContainer Cargo()
    {
        if (m_cargo != null && !m_cargo.Closed)
            return m_cargo;
        var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
        builder.GridSizeEnum = MyCubeSize.Large;
        builder.IsStatic = false;
        builder.CreatePhysics = true;
        builder.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        builder.PositionAndOrientation = new MyPositionAndOrientation(
            new Vector3D(-30000, 20000, 0),
            Vector3D.Forward,
            Vector3D.Up
        );
        builder.DisplayName = "se1892-bulk-buy-ship";
        var container = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CargoContainer>(
            "LargeBlockLargeContainer"
        );
        container.Min = new SerializableVector3I(0, 0, 0);
        builder.CubeBlocks.Add(container);
        var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false);
        m_cargo = grid.GetFatBlocks<MyCargoContainer>().Single();
        return m_cargo;
    }
}
