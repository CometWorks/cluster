using Sandbox.ModAPI;
using VRage.Game.Components;

namespace Fixture
{
    // client half that only talks to the server, saves its settings in global storage and ships an author
    // manifest claiming C: automatic B, author C
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class Hud : MySessionComponentBase
    {
        public override void LoadData()
        {
            using (var writer = MyAPIGateway.Utilities.WriteFileInGlobalStorage("hud.cfg"))
                writer.Write("on");
            MyAPIGateway.Multiplayer.SendMessageToServer(4711, new byte[] { 1 });
        }
    }
}
