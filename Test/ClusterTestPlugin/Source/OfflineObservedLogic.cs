using System;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace ClusterTestPlugin;

/// <summary>External assembly game logic, in the same seat as a mod component. It runs on the
/// real entity update path; it is neither an injected profile nor a replacement compensation model.</summary>
internal sealed class OfflineObservedLogic : MyGameLogicComponent
{
    private readonly bool inventory;
    internal OfflineObservedLogic(bool inventory = false) { this.inventory = inventory; }
    public override void UpdateAfterSimulation100()
    {
        if (inventory)
        {
            if (((MyEntity)Entity).GetInventory() is MyInventory stock)
                stock.AddItems(1, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Iron"));
        }
        else if (Entity.Components.TryGet<MyResourceSourceComponent>(out var source))
        {
            float rate = (float)(.1 + .05 * Math.Sin(MySession.Static.GameplayFrameCounter * Math.PI / 300));
            foreach (var resource in source.ResourceTypes)
                source.SetMaxOutputByType(resource, resource == MyResourceDistributorComponent.ElectricityId ? rate : 200 + rate * 1000);
        }
    }

    internal static void Attach(MyCubeBlock block, bool inventory = false)
    {
        var logic = new OfflineObservedLogic(inventory) { EntityUpdate = true };
        block.GameLogic = MyCompositeGameLogicComponent.Create(new[] { block.GameLogic, logic }.Where(c => c != null).ToArray(), block);
        logic.NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
    }
}
