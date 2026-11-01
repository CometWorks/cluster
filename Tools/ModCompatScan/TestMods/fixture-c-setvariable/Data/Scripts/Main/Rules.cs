using Sandbox.ModAPI;
using VRage.Game.Components;

namespace Fixture
{
    // session-only rules engine keeping world state in SetVariable: C, WA-only
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Rules : MySessionComponentBase
    {
        private int m_ticks;

        public override void UpdateAfterSimulation()
        {
            if (++m_ticks % 600 == 0)
                MyAPIGateway.Utilities.SetVariable("fixture-rules-ticks", m_ticks);
        }
    }
}
