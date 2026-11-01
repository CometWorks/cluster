using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ObjectBuilders;

namespace Fixture
{
    // entity-local logic that also writes world storage: B, because WA-only would strand the game logic
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Beacon), false)]
    public class BeaconLogic : MyGameLogicComponent
    {
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage("state.txt", typeof(BeaconLogic)))
                writer.Write(Entity.EntityId);
        }
    }

    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class Session : MySessionComponentBase
    {
    }
}
