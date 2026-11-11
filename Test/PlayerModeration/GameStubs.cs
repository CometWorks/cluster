// Only game scheduling, broadcast, checkpoint serialization and the outer save envelope
// are inert. Tests compile the real promotion owner, command ledger and global section codec.
using System.Text.Json;
using VRage.Game.ModAPI;

namespace VRage.Game.ModAPI { public enum MyPromoteLevel { None, Scripter, Moderator, SpaceMaster, Admin, Owner } }
namespace Sandbox.Game.World
{
    public sealed class MySession
    {
        public static MySession Static = new();
        public bool Ready = true;
        public readonly Dictionary<ulong, MyPromoteLevel> PromotedUsers = new();
        public MyPromoteLevel GetUserPromoteLevel(ulong user) => PromotedUsers.GetValueOrDefault(user);
    }
}
namespace Sandbox
{
    public sealed class MySandboxGame
    {
        public static MySandboxGame Static = new();
        public Thread UpdateThread = Thread.CurrentThread;
        public void Invoke(Action action, string name) => action();
    }
}
namespace VRageMath { public struct BoundingSphereD; }
namespace VRage.Game { public sealed class MyObjectBuilder_Checkpoint; }
namespace VRage.ObjectBuilders.Private
{
    public static class MyObjectBuilderSerializerKeen
    {
        public static bool SerializeXML(Stream stream, VRage.Game.MyObjectBuilder_Checkpoint value) { stream.WriteByte(1); return true; }
        public static bool DeserializeXML(Stream stream, out VRage.Game.MyObjectBuilder_Checkpoint value) { value = new(); return stream.ReadByte() == 1; }
    }
}
namespace ClusterPlugin
{
    public static class ClusterRuntime
    {
        public static bool IsClustered = true, IsWorldAuthority = true, IsAuthorityFenced;
        public static double LocalMilliseconds = 100_000;
    }
    public static class MySessionSeams
    {
        public static Action<ulong, MyPromoteLevel> ClusterPromotionChanged;
        public static int Broadcasts;
        public static void ClusterRaisePromoteLevel(ulong user, MyPromoteLevel level)
        {
            Broadcasts++;
            if (level == MyPromoteLevel.None) Sandbox.Game.World.MySession.Static.PromotedUsers.Remove(user);
            else Sandbox.Game.World.MySession.Static.PromotedUsers[user] = level;
            ClusterPromotionChanged?.Invoke(user, level);
        }
    }
    public static class ClusterConfig
    {
        public static string DefaultRole = "None";
        public static string Get(string key) => key == "players.defaultRole" ? DefaultRole : throw new ArgumentException(key);
    }
    public static class OnlinePlayersService
    {
        public sealed record OnlinePlayerEntry(string Node, ulong SteamId, int SerialId);
        public static OnlinePlayerEntry[] Cluster = [];
    }
    public static class SecretRedactor { public static string Redact(string value) => value; }
    public static class GlobalStateChannel
    {
        public static Action<string, string> Report;
        public static Action<string> Replica;
        public static Func<string, string, string> Set, Query;
        public static bool Journaled;
        public static string Published;
        public static bool SendRequest(string subsystem, string payload) => true;
        public static void Publish(string subsystem, string payload) => Published = payload;
        public static void RegisterAuthorityHandler(string subsystem, Action<string, string> handler) => Report = handler;
        public static void RegisterReplicaHandler(string subsystem, Action<string> handler) => Replica = handler;
        public static void RegisterSnapshot(string subsystem, Func<string> provider, Action<string> handler) { }
        public static void RegisterCommandHandler(string subsystem, Func<string, string, string> handler, bool journaled) { Set = handler; Journaled = journaled; }
        public static void RegisterQueryHandler(string subsystem, Func<string, string, string> handler) => Query = handler;
    }
    internal static class PartitionSaveFile
    {
        public sealed record Parsed(ulong PartitionId, int EntityCount, byte[] Body);
        public static byte[] Write(ulong partition, uint generation, uint sequence, byte kind, VRageMath.BoundingSphereD sphere, bool anchored, int entities, byte[] body, long time) => body;
        public static Parsed Read(byte[] file) => new(0, 0, file);
        public static void ValidateEnvelope(Parsed parsed, ulong partition, uint generation, uint? sequence) { }
    }
}
