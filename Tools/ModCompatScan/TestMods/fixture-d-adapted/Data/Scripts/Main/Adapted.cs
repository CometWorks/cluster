using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;

namespace Fixture
{
    // scanner adapted to the cluster mod channel, declared by its author: D, adapted
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Adapted : MySessionComponentBase
    {
        public const string ClusterModCompat = "D adapted";
        private readonly HashSet<IMyEntity> m_found = new HashSet<IMyEntity>();

        public override void LoadData()
        {
            MyAPIGateway.Utilities.RegisterMessageHandler(0x434C55534D4F4452, Receive);
        }

        public override void UpdateAfterSimulation()
        {
            m_found.Clear();
            MyAPIGateway.Entities.GetEntities(m_found);
            MyAPIGateway.Utilities.SendModMessage(0x434C55534D4F4453, new object[] { "broadcast", "fixture", m_found.Count.ToString() });
        }

        private void Receive(object message)
        {
        }
    }
}
