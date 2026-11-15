using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ClusterPlugin
{
    public static class ClusterRuntime
    {
        public static bool IsClustered = true;
        public static void AuthorizeRegistryClient(HttpClient client) { }
    }
    public sealed record PlayerReady(ulong UserId, uint[] MissingDlcs = null, string[] Skins = null);
    public static class OnlinePlayersService
    {
        public sealed record OnlinePlayerEntry(ulong SteamId, uint[] MissingDlcs);
        public static OnlinePlayerEntry[] Cluster = [];
    }
    public static class ClusterHttp
    {
        public static readonly GatewayHandler Handler = new();
        public static HttpClient Client(TimeSpan timeout) => new(Handler) { Timeout = timeout };
    }
    public sealed class GatewayHandler : HttpMessageHandler
    {
        public sealed record Request(string Path, string Body, TaskCompletionSource<HttpResponseMessage> Answer)
        {
            public void Reply(HttpStatusCode status, string body) => Answer.SetResult(new(status) { Content = new StringContent(body) });
        }
        public readonly Channel<Request> Requests = Channel.CreateUnbounded<Request>();
        public int Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref Count);
            await Requests.Writer.WriteAsync(new(request.RequestUri.AbsolutePath, await request.Content.ReadAsStringAsync(cancel), answer), cancel);
            return await answer.Task.WaitAsync(cancel);
        }
    }
}
namespace Sandbox
{
    public sealed class MySandboxGame
    {
        public static readonly MySandboxGame Static = new();
        public readonly ConcurrentQueue<Action> Queue = new();
        public void Invoke(Action action, string name) => Queue.Enqueue(action);
    }
}
namespace Sandbox.Engine.Utils { public static class MyFakes { public static bool OWN_ALL_DLCS; } }
namespace Sandbox.Game.Multiplayer { public static class Sync { public static ulong MyId = 42; } }
namespace VRage.Game.Definitions { public sealed class MyDlcDefinition { public uint AppId; } }
namespace Sandbox.Game
{
    public static class MyDLCs
    {
        public static bool TryGetDLC(string name, out VRage.Game.Definitions.MyDlcDefinition dlc) { dlc = null; return false; }
    }
}
namespace VRage.Utils
{
    public readonly record struct MyStringHash(string String)
    {
        public static MyStringHash GetOrCompute(string text) => new(text);
    }
}
namespace VRage.Network
{
    public interface IMyEventOwner { }
    public readonly record struct EndpointId(ulong Value);
    public sealed class MyEventContext
    {
        public static readonly MyEventContext Current = new();
        public EndpointId Sender = new(76561198000000001);
    }
}
namespace Sandbox.Engine.Multiplayer
{
    public static class MyMultiplayer
    {
        public static readonly List<(ulong User, bool Success)> Answers = [];
        public static void RaiseStaticEvent(Func<VRage.Network.IMyEventOwner, Action<bool>> action, bool success, VRage.Network.EndpointId endpoint)
            => Answers.Add((endpoint.Value, success));
    }
}
namespace Sandbox.Game.SessionComponents
{
    public sealed class MySessionComponentDLC
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool HasDLC(string DLCName, ulong steamId) => false;
    }
    public sealed class MySessionComponentGameInventory
    {
        public static int VanillaRequests;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RequestUpdateClientGameInventory(byte[] checkData) => VanillaRequests++;
        public static void UpdateClientGameInventoryResult(bool success) { }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UpdateClientGameInventory(ulong steamId, byte[] checkData, bool retry) { }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool HasItem(VRage.Utils.MyStringHash itemSubtypeId, ulong steamId) => false;
    }
}
