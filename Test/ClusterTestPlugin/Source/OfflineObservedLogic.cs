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
    private readonly bool sun;
    internal OfflineObservedLogic(bool inventory = false, bool sun = false) { this.inventory = inventory; this.sun = sun; }
    public override void UpdateAfterSimulation100()
    {
        if (inventory)
        {
            if (((MyEntity)Entity).GetInventory() is MyInventory stock)
                stock.AddItems(1, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Iron"));
        }
        else if (Entity.Components.TryGet<MyResourceSourceComponent>(out var source))
        {
            float rate = (float)(.1 + .05 * (sun ? MySector.DirectionToSunNormalized.X : Math.Sin(MySession.Static.GameplayFrameCounter * Math.PI / 300)));
            if (sun && Entity is MyReactor reactor)
            {
                // The supported mod API owns native reactor maximum updates;
                // direct SourceComp writes are reset by its ordinary Update100.
                // Stay above the native0.01 multiplier floor for the whole sine.
                ((Sandbox.ModAPI.IMyReactor)reactor).PowerOutputMultiplier = 4 * rate / reactor.BlockDefinition.MaxPowerOutput;
                return;
            }
            foreach (var resource in source.ResourceTypes)
                source.SetMaxOutputByType(resource, resource == MyResourceDistributorComponent.ElectricityId ? rate : 200 + rate * 1000);
        }
    }

    internal static void Attach(MyCubeBlock block, bool inventory = false, bool sun = false)
    {
        var logic = new OfflineObservedLogic(inventory, sun) { EntityUpdate = true };
        var previous = block.GameLogic;
        // Remove before composing: assigning the new component otherwise detaches the old
        // component after Create set its container, leaving vanilla solar logic Entity=null.
        block.Components.Remove<MyGameLogicComponent>();
        block.GameLogic = MyCompositeGameLogicComponent.Create(new[] { previous, logic }.Where(c => c != null).ToArray(), block);
        logic.NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
    }
}
