// Exercise production protocol, feed and client lifecycle without starting the game or loading native libraries.
namespace VRage.Game.ModAPI
{
    public interface IMySession { }
    public interface IMyPlayer { ulong SteamUserId { get; } }
    public interface IMyMultiplayer
    {
        bool IsServer { get; }
        bool MultiplayerActive { get; }
        ulong MyId { get; }
        void RegisterSecureMessageHandler(ushort channel, Action<ushort, byte[], ulong, bool> handler);
        void UnregisterSecureMessageHandler(ushort channel, Action<ushort, byte[], ulong, bool> handler);
        bool SendMessageTo(ushort channel, byte[] data, ulong user);
        bool SendMessageToServer(ushort channel, byte[] data);
    }
}

namespace Sandbox.Game.World
{
    public class MySession : VRage.Game.ModAPI.IMySession
    {
        public static MySession Static;
        public readonly HashSet<ulong> Admins = new();
        public bool IsUserAdmin(ulong user) => Admins.Contains(user);
    }
}

namespace Sandbox.ModAPI
{
    public static class MyAPIGateway
    {
        public static VRage.Game.ModAPI.IMySession Session;
        public static VRage.Game.ModAPI.IMyMultiplayer Multiplayer;
        public static FakePlayers Players = new();
        public static FakeUtilities Utilities = new();
    }
    public class FakePlayers
    {
        public readonly List<VRage.Game.ModAPI.IMyPlayer> Online = new();
        public void GetPlayers(List<VRage.Game.ModAPI.IMyPlayer> output) => output.AddRange(Online);
    }
    public class FakeUtilities
    {
        public readonly List<string> Notifications = new();
        public void ShowNotification(string text, int milliseconds, string font) => Notifications.Add(text);
    }
}

namespace ClusterPlugin
{
    internal static class ClusterRuntime
    {
        internal static long RegistryNodeEpoch = 8;
        internal static void AuthorizeRegistryClient(System.Net.Http.HttpClient http) { }
    }
    internal static class ClusterHttp
    {
        internal static System.Net.Http.HttpClient Client(TimeSpan timeout) => new() { Timeout = timeout };
    }
}
namespace VRage.Utils
{
    public class MyLog
    {
        public static MyLog Default = new();
        public void WriteLine(string text) { }
    }
}
