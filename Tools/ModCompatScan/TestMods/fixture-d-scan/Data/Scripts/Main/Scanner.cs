using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;

namespace Fixture
{
    // world scanner without the channel: D, needs adaptation
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Scanner : MySessionComponentBase
    {
        private readonly HashSet<IMyEntity> m_found = new HashSet<IMyEntity>();

        public override void UpdateAfterSimulation()
        {
            m_found.Clear();
            MyAPIGateway.Entities.GetEntities(m_found, entity => entity is IMyCubeGrid);
        }
    }
}
