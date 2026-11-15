using System.Net;
using System.Text.Json;
using ClusterPlugin;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.SessionComponents;
using Shared.Patches;
using VRage.Utils;

Environment.SetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY", "http://gateway.test");
const ulong user = 76561198000000001;
var harmony = new Harmony("cluster-player-skin-regression");
harmony.PatchAll(typeof(PlayerEntitlementsPatch).Assembly);
var inventory = new MySessionComponentGameInventory();
var battered = MyStringHash.GetOrCompute("Battered_Armor");
var silver = MyStringHash.GetOrCompute("Silver_Armor");
PlayerEntitlements.Attach(new(user));

// Exercise the actual Harmony entry point: the old patch swallowed this before the ownership service.
MySessionComponentGameInventory.RequestUpdateClientGameInventory([1, 2, 3]);
var request = await NextRequest();
Check(MySessionComponentGameInventory.VanillaRequests == 0, "cluster requests never enter vanilla's retry loop");
Check(request.Path == PlayerEntitlements.SkinCheckRoute, "the inventory reaches the existing Gateway endpoint");
var body = JsonSerializer.Deserialize<PlayerEntitlements.SkinCheckRequest>(request.Body);
Check(body.UserId == user && body.Data == "AQID", "sender and signed bytes survive forwarding");
MySessionComponentGameInventory.RequestUpdateClientGameInventory([1, 2, 3]);
Check(ClusterHttp.Handler.Count == 1, "duplicate requests share the attached session's pending check");
request.Reply(HttpStatusCode.OK, "{\"skins\":[\"Battered_Armor\"]}");
await Pump();
Check(inventory.HasItem(battered, user) && !inventory.HasItem(silver, user), "only Steam-confirmed skins pass the real HasItem prefix");
Check(MyMultiplayer.Answers.SequenceEqual(new[] { (user, true) }), "the client receives one successful acknowledgement");

// A handover carries the Gateway's cached skins in the next attach binding.
PlayerEntitlements.Detach(user);
PlayerEntitlements.Attach(new(user, Skins: ["Battered_Armor"]));
Check(inventory.HasItem(battered, user), "an attach binding preserves verified skins without another client refresh");

// An old HTTP response cannot grant skins to a different attachment of the same Steam id.
MySessionComponentGameInventory.RequestUpdateClientGameInventory([4]);
var old = await NextRequest();
PlayerEntitlements.Detach(user);
PlayerEntitlements.Attach(new(user, Skins: []));
MySessionComponentGameInventory.RequestUpdateClientGameInventory([5]);
var fresh = await NextRequest();
old.Reply(HttpStatusCode.OK, "{\"skins\":[\"Battered_Armor\"]}");
await Pump();
Check(!inventory.HasItem(battered, user) && MyMultiplayer.Answers.Count == 1, "a previous attachment's answer is ignored");
fresh.Reply(HttpStatusCode.OK, "{\"skins\":[\"Silver_Armor\"]}");
await Pump();
Check(inventory.HasItem(silver, user) && !inventory.HasItem(battered, user), "the new attachment runs its own check");

// Rejection and invalid data must not acknowledge success or restart the vanilla retry machinery.
MySessionComponentGameInventory.RequestUpdateClientGameInventory([6]);
var refused = await NextRequest();
refused.Reply(HttpStatusCode.UnprocessableEntity, "{\"error\":\"Steam refused the inventory\"}");
MySessionComponentGameInventory.RequestUpdateClientGameInventory([]);
Check(MySessionComponentGameInventory.VanillaRequests == 0 && MyMultiplayer.Answers.Count == 2, "refusal and empty data cannot create a retry loop");
PlayerEntitlements.Detach(user);
Check(PlayerEntitlements.HasSkin(silver, user) == null, "detach removes verified ownership");
ClusterRuntime.IsClustered = false;
MySessionComponentGameInventory.RequestUpdateClientGameInventory([7]);
Check(MySessionComponentGameInventory.VanillaRequests == 1, "standalone servers keep vanilla inventory handling");
Console.WriteLine("PASS: skin requests, ownership, acknowledgement, handover, stale replies and retry suppression");

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task<GatewayHandler.Request> NextRequest()
    => await ClusterHttp.Handler.Requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
static async Task Pump()
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (MySandboxGame.Static.Queue.IsEmpty) await Task.Delay(1, deadline.Token);
    while (MySandboxGame.Static.Queue.TryDequeue(out var action)) action();
}
