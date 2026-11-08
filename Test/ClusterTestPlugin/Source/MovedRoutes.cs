using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Replication;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;
using VRage.Voxels;
using VRageMath;
using ClusterPlugin;
using static ClusterPlugin.ClusterControlServer;
using static ClusterTestPlugin.TestSeams;

namespace ClusterTestPlugin;

/// <summary>Test-only routes moved verbatim out of ClusterControlServer.Handle by move_routes.py.
/// The bodies use the control server's own helpers (Invoke, Write, QueryLong, ...) through the
/// static import; keep them as they were so a diff against the product history stays readable.</summary>
internal static class MovedRoutes
{
    public static void Register()
    {
        // -- moved routes begin --
        TestRoutes.Add("POST", "/respawn-test-delay", (context, path) =>
		{
					int respawnDelayMs = int.Parse(context.Request.QueryString["ms"]
						?? throw new ArgumentException("Missing ms query parameter"), CultureInfo.InvariantCulture);
					Write(context, 200, "application/json",
						Encoding.UTF8.GetBytes(JsonSerializer.Serialize(RespawnRegistryService.ConfigureTestDelay(respawnDelayMs))));
					return;
		});
        // SE-1150: this node's next reports carry these respawn points (a JSON array of ClusterRespawnPoint) as they are,
        // past the capture's checks, for `seconds`; an empty array clears them.
        TestRoutes.Add("POST", "/respawn-test-raw-points", (context, path) =>
		{
					ClusterRespawnPoint[] points = JsonSerializer.Deserialize<ClusterRespawnPoint[]>(TestRoutes.Query(context, "points"));
					double seconds = TestRoutes.Double(context, "seconds");
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
						RespawnRegistryService.ConfigureTestRawPoints(points, seconds))));
					return;
		});
        TestRoutes.Add("GET", "/respawn-test-delay", (context, path) =>
		{
					Write(context, 200, "application/json",
						Encoding.UTF8.GetBytes(JsonSerializer.Serialize(RespawnRegistryService.TestDelayStatus())));
					return;
		});
        TestRoutes.Add("POST", "/respawn-test-arbitrate", (context, path) =>
		{
					ulong userId = ulong.Parse(context.Request.QueryString["userId"] ?? throw new ArgumentException("Missing userId query parameter"), CultureInfo.InvariantCulture);
					int serialId = int.Parse(context.Request.QueryString["serialId"] ?? "0", CultureInfo.InvariantCulture);
					long identityId = long.Parse(context.Request.QueryString["identityId"] ?? throw new ArgumentException("Missing identityId query parameter"), CultureInfo.InvariantCulture);
					long respawnEntityId = long.Parse(context.Request.QueryString["respawnEntityId"] ?? throw new ArgumentException("Missing respawnEntityId query parameter"), CultureInfo.InvariantCulture);
					Guid operationId = Guid.TryParse(context.Request.QueryString["operationId"], out Guid parsed) ? parsed : Guid.NewGuid();
					ClusterRespawnDecision decision = RespawnRegistryService.Arbitrate(
						new ClusterRespawnRequest(userId, serialId, identityId, respawnEntityId), operationId);
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(RespawnRegistryCodec.WriteDecision(decision)));
					return;
		});
        TestRoutes.Add("POST", "/respawn-test-enable", (context, path) =>
		{
					long entity = QueryLong(context, "entity");
					bool enabled = context.Request.QueryString["enabled"] == "true";
					string result = Invoke(() =>
					{
						bool found = MyEntities.TryGetEntityById(entity, out Sandbox.Game.Entities.Cube.MyFunctionalBlock block);
						if (found) block.Enabled = enabled;
						return JsonSerializer.Serialize(new { entity, found, enabled });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/respawn-test-kill", (context, path) =>
		{
					ulong userId = ulong.Parse(context.Request.QueryString["userId"] ?? throw new ArgumentException("Missing userId query parameter"), CultureInfo.InvariantCulture);
					int serialId = int.Parse(context.Request.QueryString["serialId"] ?? "0", CultureInfo.InvariantCulture);
					string result = Invoke(() =>
					{
						MyPlayer player = MySession.Static.Players.GetPlayerById(new MyPlayer.PlayerId(userId, serialId))
							?? throw new InvalidOperationException("Player is not attached");
						MyCharacter character = player.Character ?? throw new InvalidOperationException("Player has no character");
						bool killed = character.DoDamage(1000f, MyDamageType.Suicide, updateSync: true, character.EntityId);
						return JsonSerializer.Serialize(new { killed, identityId = player.Identity.IdentityId });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/respawn-test-request", (context, path) =>
		{
					ulong userId = ulong.Parse(context.Request.QueryString["userId"] ?? throw new ArgumentException("Missing userId query parameter"), CultureInfo.InvariantCulture);
					int serialId = int.Parse(context.Request.QueryString["serialId"] ?? "0", CultureInfo.InvariantCulture);
					long respawnEntityId = long.Parse(context.Request.QueryString["respawnEntityId"] ?? throw new ArgumentException("Missing respawnEntityId query parameter"), CultureInfo.InvariantCulture);
					string result = Invoke(() =>
					{
						var message = new MyPlayerCollection.RespawnMsg { RespawnEntityId = respawnEntityId, PlayerSerialId = serialId, Color = Color.Red };
						using (MyEventContext.Set(new EndpointId(userId), null, isInvokedLocally: false))
						{
							bool runOriginal = MyPlayerCollectionPatch.RespawnRequestPrefix(message);
							if (runOriginal)
								MyPlayerCollection.OnRespawnRequest(false, false, respawnEntityId, null, null, null, null, null,
									realPlayer: true, serialId, null, Color.Red);
							return JsonSerializer.Serialize(new { routed = !runOriginal });
						}
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        // #1287: a cross-node respawn detaches a dead client's view, and a spawn preview queued for the same client runs
        // a frame later. Detach the view here, queue the preview, then disconnect the client as the switch would move it.
        TestRoutes.Add("POST", "/respawn-test-preview-race", (context, path) =>
		{
					ulong userId = ulong.Parse(context.Request.QueryString["userId"] ?? throw new ArgumentException("Missing userId query parameter"), CultureInfo.InvariantCulture);
					long respawnEntityId = long.Parse(context.Request.QueryString["respawnEntityId"] ?? throw new ArgumentException("Missing respawnEntityId query parameter"), CultureInfo.InvariantCulture);
					string result = Invoke(() =>
					{
						ClusterRespawnPoint point = RespawnRegistryService.Points.FirstOrDefault(x => x.RespawnEntityId == respawnEntityId)
							?? throw new InvalidOperationException("Respawn point is not in this node's registry");
						DetachClientView(userId);
						RespawnRegistryService.SwitchForPreview(userId, respawnEntityId, point);
						MySandboxGame.Static.Invoke(() => MyMultiplayer.Static?.DisconnectClient(userId), "ClusterTestPlugin preview race cleanup");
						return JsonSerializer.Serialize(new { detached = true, origin = point.Origin, partition = point.PartitionId });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/respawn-test-player", (context, path) =>
		{
					ulong userId = ulong.Parse(context.Request.QueryString["userId"] ?? throw new ArgumentException("Missing userId query parameter"), CultureInfo.InvariantCulture);
					int serialId = int.Parse(context.Request.QueryString["serialId"] ?? "0", CultureInfo.InvariantCulture);
					string result = Invoke(() =>
					{
						MyPlayer player = MySession.Static.Players.GetPlayerById(new MyPlayer.PlayerId(userId, serialId));
						long controlled = player?.Controller?.ControlledEntity?.Entity?.EntityId ?? player?.Character?.EntityId ?? 0;
						return JsonSerializer.Serialize(new { attached = player != null, dead = player?.Character?.IsDead ?? player?.Identity?.IsDead,
							identityId = player?.Identity?.IdentityId ?? 0, character = player?.Character?.EntityId ?? 0, controlled });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/test-wa-partition", (context, path) =>
		{
					int partitionMs = int.Parse(context.Request.QueryString["ms"]
						?? throw new ArgumentException("Missing ms query parameter"), CultureInfo.InvariantCulture);
					Write(context, 200, "application/json",
						Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GlobalStateChannel.ConfigureTestPartition(partitionMs))));
					return;
		});
        TestRoutes.Add("GET", "/test-wa-partition", (context, path) =>
		{
					Write(context, 200, "application/json",
						Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GlobalStateChannel.TestPartitionStatus())));
					return;
		});
        // T-0243: every entity id in a grid's logical group - the grids, their fat blocks and those blocks'
        // subparts - with the connectors' partners and this node's current EntityId range, so a scenario can
        // show that a paste landed wholly inside the node's registry-issued range with its docking intact.
        TestRoutes.Add("GET", "/entity-id-audit", (context, path) =>
		{
					long gridId = long.Parse(context.Request.QueryString["grid"] ?? "0", CultureInfo.InvariantCulture);
					string name = context.Request.QueryString["name"];
					string result = Invoke(() =>
					{
						// ?name= picks the newest grid of that display name: a big paste outlives the client's HTTP
						// timeout, so the scenario may not learn the pasted grid's id from the paste call itself.
						MyCubeGrid root = null;
						if (gridId != 0) MyEntities.TryGetEntityById(gridId, out root);
						else if (!string.IsNullOrEmpty(name))
							root = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(grid => grid.DisplayName == name)
								.OrderByDescending(grid => grid.EntityId & EntityIdAllocator.UniqueMask).FirstOrDefault();
						if (root == null)
							throw new KeyNotFoundException("no grid " + (gridId != 0 ? gridId.ToString(CultureInfo.InvariantCulture) : name));
						gridId = root.EntityId;
						List<MyCubeGrid> grids = MyCubeGridGroups.Static.Logical.GetGroup(root)?.Nodes.Select(node => node.NodeData).ToList()
							?? new List<MyCubeGrid> { root };
						var ids = new List<object>();
						var connectors = new List<object>();
						void AddSubparts(MyEntity entity, long owner)
						{
							if (entity.Subparts == null) return;
							foreach (MyEntitySubpart subpart in entity.Subparts.Values)
							{
								ids.Add(new { id = subpart.EntityId, kind = "subpart", grid = owner });
								AddSubparts(subpart, owner);
							}
						}
						foreach (MyCubeGrid grid in grids)
						{
							ids.Add(new { id = grid.EntityId, kind = "grid", grid = grid.EntityId });
							foreach (MyCubeBlock block in grid.GetFatBlocks())
							{
								ids.Add(new { id = block.EntityId, kind = "block", grid = grid.EntityId });
								AddSubparts(block, grid.EntityId);
								if (block is Sandbox.Game.Entities.Cube.MyShipConnector connector)
									connectors.Add(new { id = connector.EntityId, grid = grid.EntityId, connected = connector.Connected,
										other = connector.Other?.EntityId ?? 0, otherGrid = connector.Other?.CubeGrid.EntityId ?? 0 });
							}
						}
						return JsonSerializer.Serialize(new { root = gridId, grids = grids.Select(grid => grid.EntityId).ToArray(), ids, connectors,
							allocator = EntityIdAllocator.Snapshot() });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
		});
        // T-0229: a GPS table the size of a live world's (2.6 MB on w32), so the gps subsystem's
        // full-state publishes and snapshots exceed the relay IPC's fragment size. The WA only: GPS
        // entries are authority state, and a node would forward each add as a request.
        TestRoutes.Add("POST", "/gps-test-bulk", (context, path) =>
		{
					int count = int.Parse(context.Request.QueryString["count"] ?? "500", CultureInfo.InvariantCulture);
					int bytes = int.Parse(context.Request.QueryString["bytes"] ?? "2000", CultureInfo.InvariantCulture);
					if (!ClusterRuntime.IsWorldAuthority)
						throw new InvalidOperationException("gps-test-bulk runs on the World Authority only");
					if (bytes > 65536) // GpsAuthorityModel.Valid: a longer description invalidates the whole table
						throw new ArgumentException("bytes must be at most 65536");
					string result = Invoke(() =>
					{
						long identityId = MySession.Static.Players.GetAllIdentities().Select(x => x.IdentityId).DefaultIfEmpty(0).First();
						if (identityId == 0) throw new InvalidOperationException("the WA knows no identity to hold the GPS entries");
						string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
						for (int i = 0; i < count; i++)
						{
							var gps = new Sandbox.Game.Screens.Helpers.MyGps
							{
								Name = $"t0229-bulk-{tag}-{i}",
								Description = new string((char)('a' + i % 26), bytes),
								Coords = new Vector3D(1000 + i, 2000, 3000),
								ShowOnHud = false,
								DiscardAt = null,
							};
							gps.UpdateHash();
							MySession.Static.Gpss.SendAddGpsRequest(identityId, ref gps, 0, playSoundOnCreation: false);
						}
						var entries = new List<VRage.Game.ModAPI.IMyGps>();
						MySession.Static.Gpss.GetGpsList(identityId, entries);
						return JsonSerializer.Serialize(new { identityId, tag, added = count, bytes, entries = entries.Count });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/chat-test-request", (context, path) =>
		{
					ulong sender = ulong.Parse(context.Request.QueryString["sender"] ?? throw new ArgumentException("Missing sender query parameter"), CultureInfo.InvariantCulture);
					string text = context.Request.QueryString["text"] ?? throw new ArgumentException("Missing text query parameter");
					string result = Invoke(() =>
					{
						using (MyEventContext.Set(new EndpointId(sender), null, isInvokedLocally: false))
						{
							bool runOriginal = ChatAuthorityPatch.RequestPrefix(new ChatMsg
							{
								Text = text, Author = sender, Channel = (byte)ChatChannel.Global, CrossChatEnabled = true
							});
							return JsonSerializer.Serialize(new { forwarded = !runOriginal });
						}
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/identity-test-create", (context, path) =>
		{
					string name = context.Request.QueryString["name"] ?? throw new ArgumentException("Missing name query parameter");
					string result = Invoke(() =>
					{
						MyIdentity identity = MySession.Static.Players.CreateNewIdentity(name, initialPlayer: true);
						return JsonSerializer.Serialize(new { identityId = identity.IdentityId, identity.DisplayPlatformName });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/identity-trash-test", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["identityId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long trashIdentity) || trashIdentity <= 0)
						throw new ArgumentException("Invalid identityId query parameter");
					string result = Invoke(() =>
					{
						var trashComponent = MySession.Static.GetComponent<Sandbox.Game.SessionComponents.MySessionComponentTrash>()
							?? throw new InvalidOperationException("Trash component unavailable");
						return JsonSerializer.Serialize(new { identityId = trashIdentity, removed = trashComponent.ClusterRemoveAbandonedIdentity(trashIdentity) });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/identity-test-status", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["identityId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long identityId) || identityId <= 0)
						throw new ArgumentException("Invalid identityId query parameter");
					string result = Invoke(() =>
					{
						MyIdentity identity = MySession.Static.Players.TryGetIdentity(identityId);
						return JsonSerializer.Serialize(new { identityId, exists = identity != null, name = identity?.DisplayPlatformName,
							npc = MySession.Static.Players.IdentityIsNpc(identityId) });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/banking-test-change", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["ownerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ownerId) || ownerId <= 0
						|| !long.TryParse(context.Request.QueryString["amount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
						throw new ArgumentException("Invalid banking query parameters");
					Guid operationId = context.Request.QueryString["operationId"] == null ? Guid.Empty
						: Guid.Parse(context.Request.QueryString["operationId"]);
					string result = Invoke(() => JsonSerializer.Serialize(new
					{
						success = operationId == Guid.Empty ? MyBankingSystem.ChangeBalance(ownerId, amount)
							: MyBankingSystemPatch.ChangeBalanceWithOperation(ownerId, amount, operationId),
						operationId
					}));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/banking-test-delay", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["ownerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long delayOwner) || delayOwner <= 0
						|| !int.TryParse(context.Request.QueryString["ms"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int delayMs) || delayMs < 0 || delayMs > 60000)
						throw new ArgumentException("Invalid banking delay query parameters");
					// SE-1226, on the World Authority: the next balance change of this owner waits on its game thread.
					MyBankingSystemPatch.DelayNextChange(delayOwner, delayMs);
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ownerId = delayOwner, ms = delayMs })));
					return;
		});
        TestRoutes.Add("POST", "/banking-test-transfer", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["from"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long transferFrom) || transferFrom <= 0
						|| !long.TryParse(context.Request.QueryString["to"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long transferTo) || transferTo <= 0
						|| !long.TryParse(context.Request.QueryString["amount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long transferAmount) || transferAmount <= 0)
						throw new ArgumentException("Invalid banking query parameters");
					// Drives vanilla's own trade/deposit settlement path - Transfer_Server - not the cluster seam
					// directly, so the leg proves the wiring a real player trade would take.
					string result = Invoke(() =>
					{
						MyBankingSystem.Static.Transfer_Server(transferFrom, transferTo, transferAmount);
						MyBankingSystem.Static.TryGetAccountInfo(transferFrom, out MyAccountInfo fromInfo);
						MyBankingSystem.Static.TryGetAccountInfo(transferTo, out MyAccountInfo toInfo);
						return JsonSerializer.Serialize(new
						{
							from = transferFrom,
							to = transferTo,
							amount = transferAmount,
							fromBalance = fromInfo.Balance,
							toBalance = toInfo.Balance
						});
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        // Desynchronizes this node's clients from an account the way a missed event does, leaving the node's own copy alone.
        TestRoutes.Add("POST", "/banking-test-client-desync", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["ownerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ownerId) || ownerId <= 0)
						throw new ArgumentException("Invalid ownerId query parameter");
					bool forget = context.Request.QueryString["kind"] == "forget";
					string result = Invoke(() =>
					{
						if (forget) MyMultiplayer.RaiseStaticEvent(x => MyBankingSystem.RemoveAccount_Clients, ownerId);
						else MyMultiplayer.RaiseStaticEvent(x => MyBankingSystem.ChangeBalanceBroadcastToClients, ownerId, 0L, 0L);
						return JsonSerializer.Serialize(new { ownerId, forget });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/banking-test-create", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["ownerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ownerId) || ownerId <= 0
						|| !long.TryParse(context.Request.QueryString["balance"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long balance) || balance < 0)
						throw new ArgumentException("Invalid banking query parameters");
					string result = Invoke(() =>
					{
						MyBankingSystem.Static.CreateAccount(ownerId, balance);
						return JsonSerializer.Serialize(new { ownerId, requested = true });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/banking-test-command", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["from"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long commandFrom) || commandFrom <= 0
						|| !long.TryParse(context.Request.QueryString["to"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long commandTo) || commandTo <= 0
						|| !long.TryParse(context.Request.QueryString["amount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long commandAmount) || commandAmount <= 0)
						throw new ArgumentException("Invalid banking command query parameters");
					Guid commandOperation = Guid.Parse(context.Request.QueryString["operationId"]
						?? throw new ArgumentException("Missing operationId query parameter"));
					int commandDelay = int.Parse(context.Request.QueryString["delayMs"] ?? "0", CultureInfo.InvariantCulture);
					int commandTimeout = int.Parse(context.Request.QueryString["timeoutMs"] ?? "5000", CultureInfo.InvariantCulture);
					// Chaos-lane path: a WA transfer command with a caller-chosen operation id (so a retry
					// after an uncertain outcome dedupes) and an optional in-flight delay at the authority
					// (so the lane can kill the WA mid-mutation). Deliberately no local apply: the replica
					// converges through the banking snapshot, and the lane asserts authority-side balances.
					// A refusal (link down, timeout, fenced generation) surfaces as the HTTP error text.
					bool commandSuccess = BankingAuthorityCodec.ReadResult(GlobalStateChannel.SendCommand("banking",
						BankingAuthorityCodec.WriteRequest(new BankingAuthorityRequest("Transfer", commandFrom, commandAmount, commandTo, commandDelay)),
						commandOperation, commandTimeout)).Success;
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
					{
						operationId = commandOperation,
						success = commandSuccess
					})));
					return;
		});
        TestRoutes.Add("POST", "/faction-test-command", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["identityId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long repIdentity) || repIdentity <= 0
						|| !long.TryParse(context.Request.QueryString["factionId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long repFaction) || repFaction <= 0
						|| !int.TryParse(context.Request.QueryString["delta"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int repDelta) || repDelta == 0)
						throw new ArgumentException("Invalid faction command query parameters");
					Guid repOperation = Guid.Parse(context.Request.QueryString["operationId"]
						?? throw new ArgumentException("Missing operationId query parameter"));
					int repDelay = int.Parse(context.Request.QueryString["delayMs"] ?? "0", CultureInfo.InvariantCulture);
					int repTimeout = int.Parse(context.Request.QueryString["timeoutMs"] ?? "5000", CultureInfo.InvariantCulture);
					// Chaos-lane path mirroring banking-test-command: a WA AddReputation command with a
					// caller-chosen operation id (an uncertain-outcome retry dedupes) and an optional
					// in-flight delay at the authority (the lane kills the WA mid-mutation). No local
					// apply: replicas converge through the faction snapshot.
					bool repSuccess = JsonSerializer.Deserialize<bool>(GlobalStateChannel.SendCommand("factions",
						FactionAuthorityCodec.Write(new FactionAuthorityMessage
						{
							Operation = "AddReputation",
							First = repIdentity,
							Second = repFaction,
							Number = repDelta,
							Reason = VRage.Game.ModAPI.ReputationChangeReason.Admin,
							// No propagation: the lane asserts one deterministic reputation value.
							Propagate = false,
							AdminChange = true,
							DelayMilliseconds = repDelay
						}), repOperation, repTimeout));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
					{
						operationId = repOperation,
						success = repSuccess
					})));
					return;
		});
        TestRoutes.Add("POST", "/banking-test-store-payment", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["payerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long payerId) || payerId <= 0
						|| !long.TryParse(context.Request.QueryString["payeeId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long payeeId) || payeeId <= 0
						|| !long.TryParse(context.Request.QueryString["amount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount) || amount <= 0)
						throw new ArgumentException("Invalid store payment query parameters");
					bool commit = bool.Parse(context.Request.QueryString["commit"] ?? "true");
					int storeHoldMs = int.TryParse(context.Request.QueryString["holdMs"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStoreHold)
						? Math.Clamp(parsedStoreHold, 0, 60000) : 0;
					string result = Invoke(() =>
					{
						using MyStoreBlockPatch.StorePaymentScope payment = MyStoreBlockPatch.BeginPayment(payerId, amount);
						// Chaos knob, zero in production traffic: holds the reservation open on the game
						// thread so a lane can kill the node between the reserve and its settlement.
						if (payment.Approved && storeHoldMs > 0) Thread.Sleep(storeHoldMs);
						bool debit = false;
						bool credit = false;
						if (payment.Approved && commit)
						{
							debit = MyBankingSystem.ChangeBalance(payerId, -amount);
							credit = MyBankingSystem.ChangeBalance(payeeId, amount);
							payment.Commit();
						}
						return JsonSerializer.Serialize(new { approved = payment.Approved, commit, debit, credit });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/banking-test-store-inventory-full", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["payerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fullPayerId) || fullPayerId <= 0
						|| !long.TryParse(context.Request.QueryString["payeeId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fullPayeeId) || fullPayeeId <= 0
						|| !long.TryParse(context.Request.QueryString["amount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fullAmount) || fullAmount <= 0
						|| !long.TryParse(context.Request.QueryString["identityId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fullIdentityId) || fullIdentityId <= 0)
						throw new ArgumentException("Invalid inventory-full payment query parameters");
					// The vanilla store body's inventory-full branch: reserve the payer debit, ask the buyer's REAL
					// inventory whether the goods fit, and on refusal early-return - the disposing scope must refund.
					// The refusal is genuine (a Survival character inventory versus an impossible ore volume), not a
					// commit=false flag like the abort probe.
					string result = Invoke(() =>
					{
						MyIdentity buyer = MySession.Static.Players.TryGetIdentity(fullIdentityId)
							?? throw new InvalidOperationException("No identity " + fullIdentityId);
						MyInventory buyerInventory = buyer.Character?.GetInventory()
							?? throw new InvalidOperationException("Identity " + fullIdentityId + " has no character inventory here");
						MyDefinitionId goods = MyDefinitionId.Parse("MyObjectBuilder_Ore/Stone");
						using MyStoreBlockPatch.StorePaymentScope payment = MyStoreBlockPatch.BeginPayment(fullPayerId, fullAmount);
						bool debit = false;
						bool credit = false;
						bool inventoryFull = false;
						if (payment.Approved)
						{
							inventoryFull = !buyerInventory.CanItemsBeAdded(1000000, goods);
							if (!inventoryFull)
							{
								debit = MyBankingSystem.ChangeBalance(fullPayerId, -fullAmount);
								credit = MyBankingSystem.ChangeBalance(fullPayeeId, fullAmount);
								payment.Commit();
							}
						}
						return JsonSerializer.Serialize(new { approved = payment.Approved, inventoryFull, debit, credit });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/banking-test-deferred-payment", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["payerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long payerId) || payerId <= 0
						|| !long.TryParse(context.Request.QueryString["payeeId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long payeeId) || payeeId <= 0
						|| !long.TryParse(context.Request.QueryString["amount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount) || amount <= 0)
						throw new ArgumentException("Invalid deferred payment query parameters");
					// Mirrors the asynchronous prefab purchase: reserve now, settle (or abandon) in a later callback.
					bool spawned = bool.Parse(context.Request.QueryString["spawned"] ?? "true");
					string result = Invoke(() =>
					{
						MyStoreBlockPatch.StorePaymentScope payment = MyStoreBlockPatch.BeginDeferredPayment(payerId, amount);
						bool settled = false;
						try
						{
							if (payment.Approved && spawned)
							{
								payment.Settle(() =>
								{
									MyBankingSystem.ChangeBalance(payerId, -amount);
									MyBankingSystem.ChangeBalance(payeeId, amount);
								});
								settled = true;
							}
						}
						finally { payment.Dispose(); }
						return JsonSerializer.Serialize(new { approved = payment.Approved, spawned, settled });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/pcu-test-status", (context, path) =>
		{
					long pcuOwner = long.TryParse(context.Request.QueryString["owner"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedOwner) ? parsedOwner : 0;
					int pcuProbe = int.TryParse(context.Request.QueryString["probe"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedProbe) ? parsedProbe : 0;
					string result = Invoke(() =>
					{
						long owner = pcuOwner != 0 ? pcuOwner
							: MySession.Static.Players.GetAllIdentities()
								.Where(identity => !MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
								.Select(identity => identity.IdentityId).FirstOrDefault();
						MyBlockLimits limits = MySession.Static.Players.TryGetIdentity(owner)?.BlockLimits;
						string verdict = pcuProbe > 0
							? MySession.Static.IsWithinWorldLimits(out _, owner, null, pcuProbe).ToString() : null;
						return JsonSerializer.Serialize(new
						{
							owner,
							localPcuBuilt = limits?.PCUBuilt,
							localRemaining = limits?.PCU,
							probe = pcuProbe,
							verdict,
							ledger = PcuLedgerService.Status(owner)
						});
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/pcu-test-build", (context, path) =>
		{
					long buildOwner = long.TryParse(context.Request.QueryString["owner"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedBuildOwner) ? parsedBuildOwner : 0;
					if (!int.TryParse(context.Request.QueryString["pcu"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int buildPcu) || buildPcu == 0)
						throw new ArgumentException("Invalid pcu query parameter");
					// Commits usage accounting exactly the way a finished build does, so the ledger sees what a real
					// construction would produce without the lane having to weld thousands of blocks.
					string result = Invoke(() =>
					{
						long owner = buildOwner != 0 ? buildOwner
							: MySession.Static.Players.GetAllIdentities()
								.Where(identity => !MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
								.Select(identity => identity.IdentityId).FirstOrDefault();
						MyBlockLimits limits = MySession.Static.Players.TryGetIdentity(owner)?.BlockLimits
							?? throw new InvalidOperationException("No block limits for identity " + owner);
						MyCubeGrid host = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault()
							?? throw new InvalidOperationException("No grid to attribute the probe build to");
						if (buildPcu > 0) limits.IncreaseBlocksBuilt("pcu-probe", buildPcu, host);
						else limits.DecreaseBlocksBuilt("pcu-probe", -buildPcu, host);
						return JsonSerializer.Serialize(new { owner, pcu = buildPcu, localPcuBuilt = limits.PCUBuilt });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/contract-test-settle", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["contractId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long settleContract) || settleContract >= 0
						|| !long.TryParse(context.Request.QueryString["payerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long settlePayer) || settlePayer <= 0
						|| !long.TryParse(context.Request.QueryString["deposit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long settleDeposit) || settleDeposit < 0)
						throw new ArgumentException("Invalid contract settlement query parameters");
					string settleKind = context.Request.QueryString["kind"];
					if (settleKind is not ("Finish" or "Fail")) throw new ArgumentException("Invalid contract settlement kind");
					string result = Invoke(() => JsonSerializer.Serialize(
						ContractDepositProbe.Settle(settleContract, settlePayer, settleDeposit, settleKind)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/contract-test-settle-split", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["contractId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long splitContract) || splitContract >= 0
						|| !long.TryParse(context.Request.QueryString["ownerA"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long splitOwnerA) || splitOwnerA <= 0
						|| !long.TryParse(context.Request.QueryString["ownerB"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long splitOwnerB) || splitOwnerB <= 0
						|| !int.TryParse(context.Request.QueryString["reward"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int splitReward) || splitReward <= 0)
						throw new ArgumentException("Invalid split settlement query parameters");
					string result = Invoke(() => JsonSerializer.Serialize(
						ContractDepositProbe.SettleSplit(splitContract, splitOwnerA, splitOwnerB, splitReward)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/contract-test-activate", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["payerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long contractPayer) || contractPayer <= 0
						|| !long.TryParse(context.Request.QueryString["deposit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long contractDeposit) || contractDeposit < 0)
						throw new ArgumentException("Invalid contract activation query parameters");
					string result = Invoke(() => JsonSerializer.Serialize(ContractDepositProbe.Activate(contractPayer, contractDeposit)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/mod-test-send", (context, path) =>
		{
					string modKind = context.Request.QueryString["kind"] ?? throw new ArgumentException("Missing kind query parameter");
					string modChannel = context.Request.QueryString["channel"] ?? throw new ArgumentException("Missing channel query parameter");
					string modPayload = context.Request.QueryString["payload"] ?? string.Empty;
					long modCorrelation = long.Parse(context.Request.QueryString["correlation"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					string modReplyTo = context.Request.QueryString["replyTo"];
					string result = Invoke(() => JsonSerializer.Serialize(
						ModChannelTestHarness.Send(modKind, modChannel, modPayload, modCorrelation, modReplyTo)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/mod-test-received", (context, path) =>
		{
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ModChannelTestHarness.Drain())));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-ensure-grid", (context, path) =>
		{
					long antennaOwner = QueryLong(context, "owner");
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.EnsureAntennaGrid(antennaOwner)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-ensure-named-grid", (context, path) =>
		{
					long namedOwner = QueryLong(context, "owner");
					string namedGrid = context.Request.QueryString["name"] ?? throw new ArgumentException("Missing name query parameter");
					double namedLateral = double.Parse(context.Request.QueryString["lateral"] ?? throw new ArgumentException("Missing lateral query parameter"), CultureInfo.InvariantCulture);
					float namedRadius = float.Parse(context.Request.QueryString["radius"] ?? throw new ArgumentException("Missing radius query parameter"), CultureInfo.InvariantCulture);
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.EnsureNamedAntennaGrid(namedOwner, namedGrid, namedLateral, namedRadius)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-ensure-laser", (context, path) =>
		{
					long laserOwner = QueryLong(context, "owner");
					string laserName = context.Request.QueryString["name"] ?? throw new ArgumentException("Missing name query parameter");
					double laserLateral = double.Parse(context.Request.QueryString["lateral"] ?? throw new ArgumentException("Missing lateral query parameter"), CultureInfo.InvariantCulture);
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.EnsureLaserGrid(laserOwner, laserName, laserLateral)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-ensure-laser-at", (context, path) =>
		{
					long laserAtOwner = long.TryParse(context.Request.QueryString["owner"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLaserAtOwner) ? parsedLaserAtOwner : 0;
					string laserAtName = context.Request.QueryString["name"] ?? throw new ArgumentException("Missing name query parameter");
					VRageMath.Vector3D laserAt = QueryPosition(context);
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.EnsureLaserGridAt(laserAtOwner, laserAtName, laserAt)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-laser-gps", (context, path) =>
		{
					long laserGpsGrid = QueryLong(context, "grid");
					VRageMath.Vector3D laserGps = QueryPosition(context);
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.LaserGps(laserGpsGrid, laserGps)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-laser-idle", (context, path) =>
		{
					long laserIdleGrid = QueryLong(context, "grid");
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.LaserIdle(laserIdleGrid)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-laser-connect", (context, path) =>
		{
					long laserGrid = QueryLong(context, "grid");
					long laserTarget = QueryLong(context, "target");
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.LaserConnect(laserGrid, laserTarget)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/igc-test-laser-status", (context, path) =>
		{
					long laserGrid = QueryLong(context, "grid");
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.LaserStatus(laserGrid)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-add-pb", (context, path) =>
		{
					long igcGrid = QueryLong(context, "grid");
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.AddProgrammableBlock(igcGrid)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/igc-test-reachability", (context, path) =>
		{
					long igcReachSource = QueryLong(context, "source");
					long igcReachAddressee = QueryLong(context, "addressee");
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.Reachability(igcReachSource, igcReachAddressee)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-unicast", (context, path) =>
		{
					long igcSource = QueryLong(context, "source");
					long igcAddressee = QueryLong(context, "addressee");
					string igcTag = context.Request.QueryString["tag"] ?? throw new ArgumentException("Missing tag query parameter");
					string igcData = context.Request.QueryString["data"] ?? string.Empty;
					// The send blocks on the addressee's node, so it must not hold the game thread.
					string result = JsonSerializer.Serialize(IgcTestHarness.SendUnicast(igcSource, igcAddressee, igcTag, igcData));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-broadcast", (context, path) =>
		{
					long igcSource = QueryLong(context, "source");
					string igcTag = context.Request.QueryString["tag"] ?? throw new ArgumentException("Missing tag query parameter");
					string igcData = context.Request.QueryString["data"] ?? string.Empty;
					string result = JsonSerializer.Serialize(IgcTestHarness.SendBroadcast(igcSource, igcTag, igcData));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/igc-test-listen", (context, path) =>
		{
					long igcAddress = QueryLong(context, "address");
					string igcTag = context.Request.QueryString["tag"] ?? throw new ArgumentException("Missing tag query parameter");
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.Listen(igcAddress, igcTag)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/igc-test-received", (context, path) =>
		{
					long igcAddress = QueryLong(context, "address");
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.Received(igcAddress)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/rc-test-ensure", (context, path) =>
		{
					long rcOwner = QueryLong(context, "owner");
					bool dynamic = context.Request.QueryString["dynamic"] == "true";
					string result = Invoke(() => JsonSerializer.Serialize(RcTestHarness.EnsureRemoteControlGrid(rcOwner, dynamic)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/rc-test-use", (context, path) =>
		{
					long rcBlock = QueryLong(context, "block");
					string rcClient = context.Request.QueryString["client"];
					long rcUsedBy = string.IsNullOrEmpty(rcClient) ? QueryLong(context, "usedBy") : 0;
					ulong rcSteam = string.IsNullOrEmpty(rcClient) ? 0UL : ulong.Parse(rcClient, CultureInfo.InvariantCulture);
					string result = Invoke(() => JsonSerializer.Serialize(RcTestHarness.Use(rcBlock, rcUsedBy, rcSteam)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/rc-test-release", (context, path) =>
		{
					long rcReleaseBlock = QueryLong(context, "block");
					string result = Invoke(() => JsonSerializer.Serialize(RcTestHarness.Release(rcReleaseBlock)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/rc-test-status", (context, path) =>
		{
					long rcStatusBlock = QueryLong(context, "block");
					string result = Invoke(() => JsonSerializer.Serialize(RcTestHarness.Status(rcStatusBlock)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/ai-route-test-spawn", (context, path) =>
		{
					string aiRouteBody = TestRoutes.Body(context);
					string result = Invoke(() => JsonSerializer.Serialize(AiRouteTestHarness.Spawn(aiRouteBody)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/camera-test-spawn", (context, path) =>
		{
					double cameraX = double.Parse(context.Request.QueryString["x"] ?? throw new ArgumentException("Missing x query parameter"), CultureInfo.InvariantCulture);
					double cameraY = double.Parse(context.Request.QueryString["y"] ?? throw new ArgumentException("Missing y query parameter"), CultureInfo.InvariantCulture);
					double cameraZ = double.Parse(context.Request.QueryString["z"] ?? throw new ArgumentException("Missing z query parameter"), CultureInfo.InvariantCulture);
					string result = Invoke(() => JsonSerializer.Serialize(CameraTestHarness.Spawn(cameraX, cameraY, cameraZ)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/camera-test-view", (context, path) =>
		{
					long cameraEntity = long.Parse(context.Request.QueryString["entityId"] ?? throw new ArgumentException("Missing entityId query parameter"), CultureInfo.InvariantCulture);
					bool cameraActive = bool.TryParse(context.Request.QueryString["active"], out bool parsedActive) && parsedActive;
					string result = Invoke(() => JsonSerializer.Serialize(CameraTestHarness.SetView(cameraEntity, cameraActive)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/transfer-test-offer", (context, path) =>
		{
					long transferGrid = QueryLong(context, "grid");
					long transferOld = QueryLong(context, "oldOwner");
					long transferNew = QueryLong(context, "newOwner");
					string result = Invoke(() => JsonSerializer.Serialize(TestSeams.OfferGridTransfer(transferGrid, transferOld, transferNew)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/transfer-test-state", (context, path) =>
		{
					long transferStateGrid = QueryLong(context, "grid");
					string result = Invoke(() => JsonSerializer.Serialize(TestSeams.GridTransferState(transferStateGrid)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        // P3-HND-01/02: content-rich and interaction-state fixtures for the §6.10 round-trip gate.
        TestRoutes.Add("POST", "/rich-test-spawn", (context, path) =>
		{
					string richKind = context.Request.QueryString["kind"] ?? throw new ArgumentException("Missing kind query parameter");
					double richX = double.Parse(context.Request.QueryString["x"] ?? throw new ArgumentException("Missing x query parameter"), CultureInfo.InvariantCulture);
					double richY = double.Parse(context.Request.QueryString["y"] ?? throw new ArgumentException("Missing y query parameter"), CultureInfo.InvariantCulture);
					double richZ = double.Parse(context.Request.QueryString["z"] ?? throw new ArgumentException("Missing z query parameter"), CultureInfo.InvariantCulture);
					long richBuiltBy = long.TryParse(context.Request.QueryString["builtBy"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedRichBuiltBy) ? parsedRichBuiltBy : 0;
					string result = Invoke(() => JsonSerializer.Serialize(RichTestHarness.Spawn(richKind, richX, richY, richZ, richBuiltBy)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/rich-test-act", (context, path) =>
		{
					long richGrid = QueryLong(context, "grid");
					string richAction = context.Request.QueryString["action"] ?? throw new ArgumentException("Missing action query parameter");
					long richTarget = long.TryParse(context.Request.QueryString["target"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedRichTarget) ? parsedRichTarget : 0;
					string result = Invoke(() => JsonSerializer.Serialize(RichTestHarness.Act(richGrid, richAction, richTarget)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/rich-test-state", (context, path) =>
		{
					long richStateGrid = QueryLong(context, "grid");
					string result = Invoke(() => JsonSerializer.Serialize(RichTestHarness.State(richStateGrid)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/weather-test-set", (context, path) =>
		{
					string weatherName = context.Request.QueryString["weather"]
						?? throw new ArgumentException("Missing weather query parameter");
					int.TryParse(context.Request.QueryString["length"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int weatherLength);
					string result = Invoke(() => JsonSerializer.Serialize(WeatherAuthorityService.TestSet(weatherName, weatherLength)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/weather-test-lightning", (context, path) =>
		{
					double? lightningX = double.TryParse(context.Request.QueryString["x"], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedX) ? parsedX : null;
					double? lightningY = double.TryParse(context.Request.QueryString["y"], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedY) ? parsedY : null;
					double? lightningZ = double.TryParse(context.Request.QueryString["z"], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedZ) ? parsedZ : null;
					string result = Invoke(() => JsonSerializer.Serialize(WeatherAuthorityService.TestLightning(lightningX, lightningY, lightningZ)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/encounter-test-place-fail", (context, path) =>
		{
					if (!int.TryParse(context.Request.QueryString["count"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int placeFailCount))
						throw new ArgumentException("Invalid count query parameter");
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(
						JsonSerializer.Serialize(EncounterDirectorService.ConfigureTestPlacementFailures(placeFailCount))));
					return;
		});
        TestRoutes.Add("POST", "/encounter-test-interval", (context, path) =>
		{
					int encounterIntervalMs = int.Parse(context.Request.QueryString["ms"] ?? throw new ArgumentException("Missing ms query parameter"), CultureInfo.InvariantCulture);
					string encounterIntervalResult = JsonSerializer.Serialize(EncounterDirectorService.ConfigureTestInterval(encounterIntervalMs));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(encounterIntervalResult));
					return;
		});
        TestRoutes.Add("GET", "/encounter-test-interval", (context, path) =>
		{
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(EncounterDirectorService.TestIntervalStatus())));
					return;
		});
        TestRoutes.Add("POST", "/drop-test-interval", (context, path) =>
		{
					int dropIntervalMs = int.Parse(context.Request.QueryString["ms"] ?? throw new ArgumentException("Missing ms query parameter"), CultureInfo.InvariantCulture);
					string result = JsonSerializer.Serialize(DropContainerDirectorService.ConfigureTestInterval(dropIntervalMs));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/drop-test-deadline", (context, path) =>
		{
					long dropGrid = long.Parse(context.Request.QueryString["grid"] ?? throw new ArgumentException("Missing grid query parameter"), CultureInfo.InvariantCulture);
					int dropSeconds = int.Parse(context.Request.QueryString["seconds"] ?? throw new ArgumentException("Missing seconds query parameter"), CultureInfo.InvariantCulture);
					string result = Invoke(() => JsonSerializer.Serialize(DropContainerRemovalService.SetTestDeadline(dropGrid, dropSeconds)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/drop-test-interval", (context, path) =>
		{
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(DropContainerDirectorService.TestIntervalStatus())));
					return;
		});
        TestRoutes.Add("POST", "/spawn-test-dispatch", (context, path) =>
		{
					string prefab = context.Request.QueryString["prefab"] ?? throw new ArgumentException("Missing prefab query parameter");
					Guid operationId = Guid.TryParse(context.Request.QueryString["operationId"], out Guid parsedSpawn) ? parsedSpawn : Guid.NewGuid();
					ClusterPlayerPosition target = SpawnDirectorService.SelectTarget()
						?? throw new InvalidOperationException("No cluster-wide player is online to target");
					double offset = double.TryParse(context.Request.QueryString["offset"], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedOffset) ? parsedOffset : 150;
					var dispatch = new ClusterSpawnRequest(operationId, string.Empty, prefab, target.ClientId,
						target.X + offset, target.Y, target.Z, target.IdentityId, null);
					ClusterSpawnResult spawned = SpawnDirectorService.Dispatch(dispatch);
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
					{
						operationId, target = target.IdentityId, client = target.ClientId,
						spawned.Success, spawned.GridEntityIds, spawned.Error, spawned.Pending
					})));
					return;
		});
        TestRoutes.Add("GET", "/banking-test-status", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["ownerId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ownerId) || ownerId <= 0)
						throw new ArgumentException("Invalid ownerId query parameter");
					string result = Invoke(() =>
					{
						bool exists = MyBankingSystem.Static.TryGetAccountInfo(ownerId, out MyAccountInfo account);
						return JsonSerializer.Serialize(new { ownerId, exists, balance = exists ? account.Balance : 0 });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/faction-test-create", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["founderId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long founderId) || founderId <= 0)
						throw new ArgumentException("Invalid founderId query parameter");
					string tag = context.Request.QueryString["tag"] ?? throw new ArgumentException("Missing tag query parameter");
					string name = context.Request.QueryString["name"] ?? throw new ArgumentException("Missing name query parameter");
					string result = Invoke(() =>
					{
						bool accepted = JsonSerializer.Deserialize<bool>(GlobalStateChannel.SendCommand("factions", FactionAuthorityCodec.Write(new FactionAuthorityMessage
						{
							Operation = "Create", IsLocallyInvoked = true,
							Faction = new MyFactionCollection.AddFactionMsg { FounderId = founderId, FactionTag = tag, FactionName = name, Type = "PlayerMade" }
						})));
						return JsonSerializer.Serialize(new { founderId, tag, requested = true, accepted });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/faction-test-remove", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["factionId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long removeFactionId) || removeFactionId <= 0)
						throw new ArgumentException("Invalid factionId query parameter");
					ulong.TryParse(context.Request.QueryString["sender"], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong removeSender);
					string result = Invoke(() =>
					{
						// Mirrors the lane-proven war/peace path exactly: a leader's steam id as the event
						// sender, not locally invoked. Vanilla's removal shape is
						// SendFactionChange(RemoveFaction, id, id, 0).
						bool accepted = JsonSerializer.Deserialize<bool>(GlobalStateChannel.SendCommand("factions", FactionAuthorityCodec.Write(new FactionAuthorityMessage
						{
							Operation = "StateChange", Sender = removeSender, IsLocallyInvoked = removeSender == 0,
							StateChange = VRage.Game.ModAPI.MyFactionStateChange.RemoveFaction,
							First = removeFactionId, Second = removeFactionId, Third = 0
						})));
						return JsonSerializer.Serialize(new { factionId = removeFactionId, sender = removeSender, requested = true, accepted });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/faction-test-reputation", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["identityId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long identityId) || identityId <= 0
						|| !long.TryParse(context.Request.QueryString["factionId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long factionId) || factionId <= 0
						|| !int.TryParse(context.Request.QueryString["reputation"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int reputation))
						throw new ArgumentException("Invalid faction reputation query parameters");
					string result = Invoke(() =>
					{
						MySession.Static.Factions.SetReputationBetweenPlayerAndFaction(identityId, factionId, reputation, ReputationChangeReason.Admin);
						return JsonSerializer.Serialize(new { identityId, factionId, reputation, requested = true });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/faction-test-relation", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["fromFactionId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fromFactionId) || fromFactionId <= 0
						|| !long.TryParse(context.Request.QueryString["toFactionId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long toFactionId) || toFactionId <= 0
						|| !int.TryParse(context.Request.QueryString["reputation"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int reputation))
						throw new ArgumentException("Invalid faction relation query parameters");
					string result = Invoke(() =>
					{
						MySession.Static.Factions.SetReputationBetweenFactions(fromFactionId, toFactionId, reputation);
						return JsonSerializer.Serialize(new { fromFactionId, toFactionId, reputation, requested = true });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/faction-test-accept-humans", (context, path) =>
		{
					if (!long.TryParse(context.Request.QueryString["factionId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long factionId) || factionId <= 0
						|| !bool.TryParse(context.Request.QueryString["accept"], out bool accept))
						throw new ArgumentException("Invalid faction accept-humans query parameters");
					string result = Invoke(() =>
					{
						IMyFaction faction = MySession.Static.Factions.TryGetFactionById(factionId) ?? throw new KeyNotFoundException("Faction " + factionId);
						faction.AcceptHumans = accept;
						return JsonSerializer.Serialize(new { factionId, accept, requested = true });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/faction-test-damage-reputation", (context, path) =>
		{
					// Drives the vanilla combat-reputation entry point (the same one weapon/tool damage calls);
					// on a regular node the faction authority prefix forwards it to the World Authority.
					long repAttacker = QueryLong(context, "identityId");
					long repAttacked = QueryLong(context, "attackedIdentityId");
					string repType = context.Request.QueryString["type"] ?? "Damaging";
					float repAmount = float.Parse(context.Request.QueryString["amount"] ?? "1000", CultureInfo.InvariantCulture);
					string result = Invoke(() =>
					{
						MySession.Static.Factions.DamageFactionPlayerReputation(repAttacker, repAttacked,
							(MyReputationDamageType)Enum.Parse(typeof(MyReputationDamageType), repType, ignoreCase: true), repAmount);
						return JsonSerializer.Serialize(new { requested = true, identityId = repAttacker, attackedIdentityId = repAttacked, type = repType, amount = repAmount });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("GET", "/faction-test-status", (context, path) =>
		{
					string tag = context.Request.QueryString["tag"] ?? throw new ArgumentException("Missing tag query parameter");
					string otherTag = context.Request.QueryString["otherTag"];
					long.TryParse(context.Request.QueryString["identityId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long identityId);
					string result = Invoke(() =>
					{
						MyFaction faction = MySession.Static.Factions.TryGetFactionByTag(tag) as MyFaction;
						MyFaction other = otherTag == null ? null : MySession.Static.Factions.TryGetFactionByTag(otherTag) as MyFaction;
						Tuple<MyRelationsBetweenFactions, int> relation = faction == null || identityId <= 0 ? null
							: MySession.Static.Factions.GetRelationBetweenPlayerAndFaction(identityId, faction.FactionId);
						Tuple<MyRelationsBetweenFactions, int> factionRelation = faction == null || other == null ? null
							: MySession.Static.Factions.GetRelationBetweenFactions(faction.FactionId, other.FactionId);
						float damage = 0;
						if (faction != null && identityId > 0) faction.DamageInflicted.Dictionary.TryGetValue(identityId, out damage);
						return JsonSerializer.Serialize(new
						{
							tag, exists = faction != null, factionId = faction?.FactionId ?? 0, founderId = faction?.FounderId ?? 0,
							acceptHumans = faction?.AcceptHumans, autoAcceptMember = faction?.AutoAcceptMember, autoAcceptPeace = faction?.AutoAcceptPeace,
							members = faction?.Members.Keys.OrderBy(x => x).ToArray() ?? Array.Empty<long>(), reputation = relation?.Item2, damageInflicted = damage,
							otherTag, relation = factionRelation?.Item1.ToString(), relationReputation = factionRelation?.Item2,
							request = faction == null || other == null ? null : MySession.Static.Factions.GetRequestState(faction.FactionId, other.FactionId).ToString()
						});
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/voxel-test-spawn-asteroid", (context, path) =>
		{
					double x = double.Parse(context.Request.QueryString["x"] ?? throw new ArgumentException("Missing x query parameter"), CultureInfo.InvariantCulture);
					double y = double.Parse(context.Request.QueryString["y"] ?? throw new ArgumentException("Missing y query parameter"), CultureInfo.InvariantCulture);
					double z = double.Parse(context.Request.QueryString["z"] ?? throw new ArgumentException("Missing z query parameter"), CultureInfo.InvariantCulture);
					int radius = int.Parse(context.Request.QueryString["radius"] ?? "60", CultureInfo.InvariantCulture);
					int seed = int.Parse(context.Request.QueryString["seed"] ?? "7", CultureInfo.InvariantCulture);
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(AsteroidSpawnService.TestSpawn(x, y, z, radius, seed)));
					return;
		});
        TestRoutes.Add("POST", "/voxel-test-place-player", (context, path) =>
		{
					ulong client = ulong.Parse(context.Request.QueryString["client"] ?? throw new ArgumentException("Missing client query parameter"), CultureInfo.InvariantCulture);
					string storage = context.Request.QueryString["storage"] ?? throw new ArgumentException("Missing storage query parameter");
					Vector3D direction = new Vector3D(
						double.Parse(context.Request.QueryString["x"] ?? throw new ArgumentException("Missing x query parameter"), CultureInfo.InvariantCulture),
						double.Parse(context.Request.QueryString["y"] ?? throw new ArgumentException("Missing y query parameter"), CultureInfo.InvariantCulture),
						double.Parse(context.Request.QueryString["z"] ?? throw new ArgumentException("Missing z query parameter"), CultureInfo.InvariantCulture));
					bool align = bool.TryParse(context.Request.QueryString["align"], out bool value) && value;
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(Invoke(() => VoxelTestPlacePlayer(client, storage, direction, align))));
					return;
		});
        TestRoutes.Add("GET", "/voxel-test-player-state", (context, path) =>
		{
					ulong client = ulong.Parse(context.Request.QueryString["client"] ?? throw new ArgumentException("Missing client query parameter"), CultureInfo.InvariantCulture);
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(Invoke(() => VoxelTestPlayerState(client))));
					return;
		});
        TestRoutes.Add("POST", "/voxel-test-append-delay", (context, path) =>
		{
					int voxelDelayMs = int.Parse(context.Request.QueryString["ms"]
						?? throw new ArgumentException("Missing ms query parameter"), CultureInfo.InvariantCulture);
					Write(context, 200, "application/json",
						Encoding.UTF8.GetBytes(JsonSerializer.Serialize(VoxelJournalService.ConfigureTestDelay(voxelDelayMs))));
					return;
		});
        TestRoutes.Add("GET", "/voxel-test-append-delay", (context, path) =>
		{
					Write(context, 200, "application/json",
						Encoding.UTF8.GetBytes(JsonSerializer.Serialize(VoxelJournalService.TestDelayStatus())));
					return;
		});
        TestRoutes.Add("POST", "/voxel-test-cutout", (context, path) =>
		{
					string storage = context.Request.QueryString["storage"] ?? throw new ArgumentException("Missing storage query parameter");
					Vector3I center = QueryVector(context);
					float radius = float.Parse(context.Request.QueryString["radius"] ?? "1.8", CultureInfo.InvariantCulture);
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(Invoke(() => VoxelTestCutout(storage, center, radius))));
					return;
		});
        TestRoutes.Add("POST", "/voxel-test-write", (context, path) =>
		{
					string storage = context.Request.QueryString["storage"] ?? throw new ArgumentException("Missing storage query parameter");
					Vector3I min = QueryVector(context);
					int size = int.Parse(context.Request.QueryString["size"] ?? "4", CultureInfo.InvariantCulture);
					byte content = byte.Parse(context.Request.QueryString["content"] ?? "0", CultureInfo.InvariantCulture);
					byte material = byte.Parse(context.Request.QueryString["material"] ?? "1", CultureInfo.InvariantCulture);
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(Invoke(() => VoxelTestWrite(storage, min, size, content, material))));
					return;
		});
        TestRoutes.Add("POST", "/conceal-test-teleport", (context, path) =>
		{
					ulong teleportClient = ulong.Parse(context.Request.QueryString["client"]
						?? throw new ArgumentException("Missing client query parameter"), System.Globalization.CultureInfo.InvariantCulture);
					double teleportX = double.Parse(context.Request.QueryString["x"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					double teleportY = double.Parse(context.Request.QueryString["y"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					double teleportZ = double.Parse(context.Request.QueryString["z"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					// Moves the client's character server-side so the position registry carries the new location.
					// This is what stands in for a real approach in the concealment lane. Same repositioning
					// mechanism as VoxelTestPlacePlayer, which is proven to stick with a live client attached.
					string result = Invoke(() =>
					{
						MyPlayer player = MySession.Static.Players.GetOnlinePlayers()
							.SingleOrDefault(candidate => candidate.Id.SteamId == teleportClient)
							?? throw new KeyNotFoundException("Online player " + teleportClient);
						MyCharacter character = player.Character
							?? throw new InvalidOperationException("Player has no character");
						character.PositionComp.SetPosition(new Vector3D(teleportX, teleportY, teleportZ));
						if (character.Physics != null)
						{
							character.Physics.LinearVelocity = Vector3.Zero;
							character.Physics.AngularVelocity = Vector3.Zero;
						}
						return JsonSerializer.Serialize(new
						{
							client = teleportClient,
							character = character.EntityId,
							position = new[] { teleportX, teleportY, teleportZ }
						});
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/conceal-test-wake", (context, path) =>
		{
					ulong wakePartition = ulong.Parse(context.Request.QueryString["partition"]
						?? throw new ArgumentException("Missing partition query parameter"), System.Globalization.CultureInfo.InvariantCulture);
					string wakeCause = context.Request.QueryString["cause"] ?? "explicit";
					string result = Invoke(() => JsonSerializer.Serialize(new
					{
						partition = wakePartition,
						woken = m_persistence.WakePartition(wakePartition, wakeCause)
					}));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/save-test", (context, path) =>
		{
					string ratioValue = context.Request.QueryString["simRatio"];
					double? ratio = ratioValue == null || ratioValue.Equals("actual", StringComparison.OrdinalIgnoreCase)
						? null : double.Parse(ratioValue, System.Globalization.CultureInfo.InvariantCulture);
					int delay = int.Parse(context.Request.QueryString["writeDelayMs"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					double sample = double.Parse(context.Request.QueryString["sampleSeconds"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					int drop = int.Parse(context.Request.QueryString["dropTransferAfterBytes"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					if (drop < 0) throw new ArgumentOutOfRangeException(nameof(drop));
					Interlocked.Exchange(ref m_dropTransferAfterBytes, drop);
					bool suppressRecovery = bool.TryParse(context.Request.QueryString["suppressRecovery"], out bool suppress) && suppress;
					Invoke(() =>
					{
						m_persistence.ConfigureSaveTest(ratio, delay, sample);
						if (suppressRecovery) TestSeams.SuppressRecovery(m_persistence);
						return 0;
					});
					WriteText(context, 200, "configured\n");
					return;
		});
        TestRoutes.Add("POST", "/test-position", (context, path) =>
		{
					long entityId = long.Parse(context.Request.QueryString["entity"] ?? throw new ArgumentException("Missing entity query parameter"));
					long relativeId = long.Parse(context.Request.QueryString["relativeTo"] ?? throw new ArgumentException("Missing relativeTo query parameter"));
					double x = double.Parse(context.Request.QueryString["x"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					double y = double.Parse(context.Request.QueryString["y"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					double z = double.Parse(context.Request.QueryString["z"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
					Invoke(() =>
					{
						if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || !MyEntities.TryGetEntityById(relativeId, out MyEntity relative))
							throw new ArgumentException("Unknown entity");
						entity.PositionComp.SetPosition(relative.PositionComp.GetPosition() + new VRageMath.Vector3D(x, y, z));
						if (entity.Physics != null)
						{
							entity.Physics.LinearVelocity = VRageMath.Vector3.Zero;
							entity.Physics.AngularVelocity = VRageMath.Vector3.Zero;
						}
						m_partitions.Evaluate();
						return 0;
					});
					WriteText(context, 200, "positioned\n");
					return;
		});
        TestRoutes.Add("POST", "/test-track-procedural", (context, path) =>
		{
					// T-0266 harness: have the vanilla procedural generator track an entity at a character's range
					// (ViewDistance), as it tracks the character in a live partition. A grid alone is tracked at its
					// own half extent, which generates next to nothing; the live crash partition held a character
					// and its offline close unloaded 25 asteroids at once.
					long entityId = long.Parse(context.Request.QueryString["entity"] ?? throw new ArgumentException("Missing entity query parameter"));
					double radius = double.Parse(context.Request.QueryString["radius"] ?? "5000", CultureInfo.InvariantCulture);
					Invoke(() =>
					{
						if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity))
							throw new ArgumentException("Unknown entity");
						var generator = Sandbox.Game.World.Generator.MyProceduralWorldGenerator.Static
							?? throw new InvalidOperationException("No procedural generator (ProceduralDensity 0?)");
						HarmonyLib.AccessTools.Method(generator.GetType(), "TrackEntity", new[] { typeof(MyEntity), typeof(double) })
							.Invoke(generator, new object[] { entity, radius });
						return 0;
					});
					WriteText(context, 200, "tracked\n");
					return;
		});
        TestRoutes.Add("POST", "/test-cluster-reorder", (context, path) =>
		{
					// T-0266 harness: vanilla MyClusterTree.ReorderClusters around an entity - the batch path
					// (DeactivateBatch -> RemoveRigidBodyBatch -> FinishBatch, then re-add) that moving ships
					// trigger all day live and a bench of parked fixtures never does (batchRemoves=0 in every run).
					long entityId = long.Parse(context.Request.QueryString["entity"] ?? throw new ArgumentException("Missing entity query parameter"));
					double inflate = double.Parse(context.Request.QueryString["inflate"] ?? "30000", CultureInfo.InvariantCulture);
					int clusters = Invoke(() =>
					{
						if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity.MarkedForClose)
							throw new ArgumentException("Unknown entity");
						MyPhysics.Clusters.ReorderClusters(entity.PositionComp.WorldAABB.GetInflated(inflate));
						return MyPhysics.Clusters.GetClusters().Count;
					});
					WriteText(context, 200, $"reordered clusters={clusters}\n");
					return;
		});
        TestRoutes.Add("GET", "/test-voxel-census", (context, path) =>
		{
					// T-0266 harness coverage: asteroids (not planets) loaded on this node, and how many hold a rigid body.
					string result = Invoke(() =>
					{
						var maps = MyEntities.GetEntities().OfType<MyVoxelMap>().ToList();
						return JsonSerializer.Serialize(new
						{
							asteroids = maps.Count,
							withBody = maps.Count(map => map.Physics?.RigidBody != null),
							procedural = maps.Count(map => map.StorageName != null && map.StorageName.StartsWith("Asteroid_", StringComparison.Ordinal)),
						});
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("POST", "/test-havok-release", (context, path) =>
		{
					// T-0266 known positive: take an entity's rigid body out of the world, then drop every native reference to it
					// outside the managed wrapper (the state both crash dumps show: native refcount 0, wrapper alive), then close
					// the entity. Without HavokReleasedBodyGuard the close is the HkEntity.Dispose SIGSEGV; with it the node logs
					// havok_body_released_early and keeps running.
					long entityId = long.Parse(context.Request.QueryString["entity"] ?? throw new ArgumentException("Missing entity query parameter"));
					bool close = context.Request.QueryString["close"] != "0";  // close=0: leave the dead body in place (HavokBodyAudit known positive)
					string result = Invoke(() =>
					{
						if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity))
							throw new ArgumentException("Unknown entity");
						MyPhysicsBody physics = entity.Physics as MyPhysicsBody ?? throw new ArgumentException("Entity has no MyPhysicsBody");
						Havok.HkRigidBody body = physics.m_rigidBody ?? throw new ArgumentException("Entity has no own rigid body");
						physics.Enabled = false;
						if (body.InWorld) throw new InvalidOperationException("Body still in a world after Enabled=false");
						IntPtr handle = body.NativeObject;
						int refs = body.ReferenceCount;
						for (int i = 0; i < refs; i++)
							Havok.HkReferenceObject.HkReferenceObject_RemoveReference(handle);
						Console.WriteLine($"[ClusterTest] test havok_release entity={entity.GetType().Name}:{entityId} refsDropped={refs} close={close}");
						if (close) entity.Close();
						return JsonSerializer.Serialize(new { entity = entityId, type = entity.GetType().Name, refsDropped = refs, closed = close });
					});
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        TestRoutes.Add("*", "/promote-test", (context, path) =>
		{
					ulong userId = ulong.Parse(context.Request.QueryString["userId"] ?? throw new ArgumentException("Missing userId query parameter"), CultureInfo.InvariantCulture);
					string result = context.Request.HttpMethod == "POST"
						? Invoke(() => JsonSerializer.Serialize(TestSeams.SetPromoteLevel(userId,
							int.Parse(context.Request.QueryString["level"] ?? throw new ArgumentException("Missing level query parameter"), CultureInfo.InvariantCulture))))
						: Invoke(() => JsonSerializer.Serialize(PromotionService.Status(userId)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        // Partitioning.md 6.10 / P3-HND-03: force the import round-trip gate to fail by perturbing one
        // named field of the target's re-serialized object builders. POST ?field=Name arms it, POST with
        // an empty/absent field clears it, GET reads it. The perturbation itself runs inside the gate,
        // through ClusterSeams.CorruptImportedDocument; the armed field lives here, not in the product.
        TestRoutes.Add("*", "/debug/corrupt-import", (context, path) =>
		{
					if (context.Request.HttpMethod == "POST")
					{
						string field = context.Request.QueryString["field"];
						SetCorruptImportField(string.IsNullOrEmpty(field) ? null : field);
						Log("corrupt-import hook " + (m_corruptImportField == null ? "cleared" : "armed field=" + m_corruptImportField));
					}
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { field = m_corruptImportField })));
					return;
		});
        // SE-1167: keep a failing import's entities in the world for N seconds before the discard, so a test sees
        // what plugins do with them. Acts only while /debug/corrupt-import is armed, inside its perturbation, so
        // a passing import is never delayed. POST ?seconds=N arms it (0 clears), GET reads it. The import answers
        // the gateway only after the hold, and the gateway gives it 10 s (DirectGateway._http.Timeout).
        TestRoutes.Add("*", "/debug/hold-import", (context, path) =>
		{
					if (context.Request.HttpMethod == "POST")
					{
						double seconds = double.Parse(context.Request.QueryString["seconds"] ?? "0", CultureInfo.InvariantCulture);
						if (seconds is < 0 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
						m_holdImportMs = (int)(seconds * 1000);
						Log("hold-import " + (m_holdImportMs == 0 ? "cleared" : "armed ms=" + m_holdImportMs));
					}
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { seconds = m_holdImportMs / 1000.0, held = Volatile.Read(ref m_heldImports) })));
					return;
		});
        // Names the IGC addresses this node hosts and the grids that could carry one. Moved out of the
        // product with IgcTestHarness (T-0171); only Test/scenarios_wa.py calls it.
        TestRoutes.Add("GET", "/igc-endpoints", (context, path) =>
		{
					string result = Invoke(() => JsonSerializer.Serialize(IgcTestHarness.Endpoints()));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        // P1-CHR-03: break controller rebinding for one client, deliberately, so the 6.6 fallback can be
        // observed converting a stuck character into a forced reconnect.
        TestRoutes.Add("POST", "/debug/break-control-rebind", (context, path) =>
		{
					ulong breakClient = ulong.Parse(context.Request.QueryString["client"]
						?? throw new ArgumentException("Missing client query parameter"), CultureInfo.InvariantCulture);
					double breakSeconds = double.TryParse(context.Request.QueryString["seconds"], NumberStyles.Float,
						CultureInfo.InvariantCulture, out double parsedSeconds) ? parsedSeconds : 30;
					string result = Invoke(() => JsonSerializer.Serialize(BreakControlRebind(breakClient, breakSeconds)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
					return;
		});
        // -- moved routes end --
    }

    /// <summary>The x, y and z query parameters as a world position (invariant culture).</summary>
    private static VRageMath.Vector3D QueryPosition(HttpListenerContext context)
        => new(QueryDouble(context, "x"), QueryDouble(context, "y"), QueryDouble(context, "z"));

    private static double QueryDouble(HttpListenerContext context, string name)
        => double.Parse(context.Request.QueryString[name] ?? throw new ArgumentException($"Missing {name} query parameter"),
            CultureInfo.InvariantCulture);
}
