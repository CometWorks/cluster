namespace Sandbox.Game.World
{
    public sealed class MySession
    {
        public static MySession Static;
        public bool Ready;
        public int GameplayFrameCounter;
        public System.TimeSpan ElapsedGameTime;
        public System.Guid WorldId;
        public Settings Settings = new();
    }
    public sealed class Settings { public float SunRotationIntervalMinutes = 120; }
}
namespace Sandbox
{
    public sealed class MySandboxGame
    {
        public static MySandboxGame Static = new();
        public void Invoke(System.Action action, string label) => throw new System.Exception("Cold clock check must not invoke transport");
    }
}
namespace ClusterPlugin
{
    internal static class ClusterRuntime
    {
        internal static bool IsWorldAuthority = true, IsClustered = true;
        internal static bool IsAuthorityFenced = false;
        internal static long AuthorityGeneration = 1, RegistryNodeEpoch = 1;
        internal static void AuthorizeRegistryClient(System.Net.Http.HttpClient client) { }
    }
    internal static class GlobalStateChannel
    {
        internal static void RegisterQueryHandler(string key, System.Func<string, string, string> handler) { }
        internal static string SendCommand(string key, string payload) => throw new System.Exception("No transport in cold checks");
    }
    internal static class ClusterHttp
    {
        internal static System.Net.Http.HttpClient Client(System.TimeSpan timeout) => new();
    }
    internal static class TimeOfDayService { internal static long OffsetMilliseconds; }
    internal static class SessionClockPatch { internal static long AppliedOffsetMilliseconds; }
    internal static class ClusterConfig
    {
        internal static int GetInt(string key) => 180;
        internal static string Get(string key) => "Skip";
    }
}
