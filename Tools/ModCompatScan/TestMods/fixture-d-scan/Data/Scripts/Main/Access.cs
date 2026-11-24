using Sandbox.Definitions;
using Sandbox.Game;

namespace Fixture
{
    public static class Access
    {
        public static int ReadPCU(MyCubeBlockDefinition definition) => definition.PCU;

        public static void ChangeIntegrity(MyCubeBlockDefinition definition) =>
            definition.MaxIntegrity = 100;

        public static void ReadByReference(MyCubeBlockDefinition definition) =>
            Inspect(ref definition.IntegrityPointsPerSec);

        public static int ReadReference(MyCubeBlockDefinition definition) =>
            definition.MountPoints.Length;

        public static void ToggleProperty(MyCubeBlockDefinition definition) =>
            definition.GuiVisible = !definition.GuiVisible;

        private static void Inspect(ref float value) { }

        public static bool HasHandler() => MyVisualScriptLogicProvider.PlayerConnected != null;

        public static void Subscribe() =>
            MyVisualScriptLogicProvider.PlayerDisconnected += OnDisconnect;

        private static void OnDisconnect(long identity) { }
    }
}
