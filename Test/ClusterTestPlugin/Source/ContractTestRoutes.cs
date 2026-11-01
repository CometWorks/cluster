using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities.Character;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;
using VRage.Game.ObjectBuilders.Components.Contracts;
using VRage.Network;
using VRage.ObjectBuilder;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE1-0232 bench routes: a contract block on a dynamic grid, a contract posted there through the mod API, and
/// what each node offers at it. SE1-0247 adds the accepted contract's life: accept, finish and abandon, through
/// the contract system's server API (clientless) or the contract block's vanilla request handlers on behalf of an
/// attached player, what each node and the World Authority hold, and the mod API callbacks a contract posted on
/// this node received. Only game APIs, so the routes build against a package without the fix too.
/// </summary>
internal static class ContractTestRoutes
{
	/// <summary>Mod API callbacks received on this node, per contract posted here.</summary>
	private sealed class Received
	{
		public readonly List<long> Acquired = new();
		public int Succeeded;
		public int Failed;
	}

	private static readonly Dictionary<long, Received> Callbacks = new();

	public static void Register()
	{
		// POST /contract-test-block?owner=&x=&y=&z=[&anyone=1] - a dynamic grid (an armor bed and a contract block)
		// owned by `owner`. Dynamic: a static grid anchors its partition, which then never moves to another node.
		// `anyone` lets every player use the block, as a player who posts contracts for others sets it.
		TestRoutes.Add("POST", "/contract-test-block", context =>
		{
			long owner = TestRoutes.Long(context, "owner");
			var position = new Vector3D(TestRoutes.Double(context, "x"), TestRoutes.Double(context, "y"), TestRoutes.Double(context, "z"));
			bool anyone = TestRoutes.Query(context, "anyone", "0") == "1";
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => SpawnBlock(owner, position, anyone)));
		});
		// POST /contract-test-post-custom?block=&reward=&collateral=&minutes= - a mod contract (no condition, so a
		// mod finishes it) posted at the block, with the three mod API callbacks recorded on this node.
		TestRoutes.Add("POST", "/contract-test-post-custom", context =>
		{
			long block = TestRoutes.Long(context, "block");
			int reward = Int(context, "reward", 1000), collateral = Int(context, "collateral", 0), minutes = Int(context, "minutes", 60);
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Post(block, new Sandbox.ModAPI.Contracts.MyContractCustom(
				ContractType(), block, reward, collateral, minutes, "SE1-0247 contract", "cluster bench", 0, 0, null))));
		});
		// POST /contract-test-post-hauling?block=&end=&reward=&collateral=&minutes= - a courier contract from the block
		// to the end block (both on this node, as vanilla requires), with the mod API callbacks recorded here. The
		// player who accepts it gets a package to deliver at the end block.
		TestRoutes.Add("POST", "/contract-test-post-hauling", context =>
		{
			long block = TestRoutes.Long(context, "block"), end = TestRoutes.Long(context, "end");
			int reward = Int(context, "reward", 1000), collateral = Int(context, "collateral", 0), minutes = Int(context, "minutes", 60);
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Post(block,
				new Sandbox.ModAPI.Contracts.MyContractHauling(block, reward, collateral, minutes, end))));
		});
		// POST /contract-test-trash?enabled=0|1 - this node's trash removal, which takes an unpowered test grid away from
		// the players within minutes.
		TestRoutes.Add("POST", "/contract-test-trash", context =>
		{
			bool enabled = TestRoutes.Query(context, "enabled", "1") == "1";
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
			{
				MySession.Static.Settings.TrashRemovalEnabled = enabled;
				return (object)new { enabled };
			}));
		});
		// GET /contract-test-callbacks?contract= - the mod API callbacks this node received for a contract posted here.
		TestRoutes.Add("GET", "/contract-test-callbacks", context =>
		{
			long contract = TestRoutes.Long(context, "contract");
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
			{
				Callbacks.TryGetValue(contract, out Received received);
				return (object)new { contract, posted = received != null, acquired = received?.Acquired.ToArray() ?? Array.Empty<long>(),
					succeeded = received?.Succeeded ?? 0, failed = received?.Failed ?? 0 };
			}));
		});
		// POST /contract-test-accept?contract=&identity=&block= - the contract system's activation, as the block's
		// request handler calls it, for an identity without a client.
		TestRoutes.Add("POST", "/contract-test-accept", context =>
		{
			long contract = TestRoutes.Long(context, "contract"), identity = TestRoutes.Long(context, "identity"), block = TestRoutes.Long(context, "block");
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
			{
				var result = System().ActivateContract(identity, contract, 0, block);
				return (object)new { contract, identity, result = result.ToString(), accepted = result == MyContractResults.Success };
			}));
		});
		// POST /contract-test-finish-custom?contract= - a mod finishing its contract (IMyContractSystem.TryFinishCustomContract).
		TestRoutes.Add("POST", "/contract-test-finish-custom", context =>
		{
			long contract = TestRoutes.Long(context, "contract");
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
				(object)new { contract, finished = MyAPIGateway.ContractSystem.TryFinishCustomContract(contract) }));
		});
		// POST /contract-test-abandon?contract=&identity= - the contract system's abandon, as the request handler calls it.
		TestRoutes.Add("POST", "/contract-test-abandon", context =>
		{
			long contract = TestRoutes.Long(context, "contract"), identity = TestRoutes.Long(context, "identity");
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
				(object)new { contract, identity, result = System().AbandonContract(identity, contract).ToString() }));
		});
		// GET /contract-test-active?contract=&identity= - what this node (or the World Authority) holds of the contract.
		TestRoutes.Add("GET", "/contract-test-active", context =>
		{
			long contract = TestRoutes.Long(context, "contract"), identity = TestRoutes.Long(context, "identity");
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Active(contract, identity)));
		});
		// POST /contract-test-rpc?op=accept|finish|abandon&userId=&block=&contract= - the contract block's own server
		// request handler for an attached player, as if the player's client had sent it (the client has no contract
		// screen to drive headless). Finish names the player's character as the delivering entity.
		TestRoutes.Add("POST", "/contract-test-rpc", context =>
		{
			string op = TestRoutes.Query(context, "op");
			ulong user = ulong.Parse(TestRoutes.Query(context, "userId"), CultureInfo.InvariantCulture);
			long block = TestRoutes.Long(context, "block"), contract = TestRoutes.Long(context, "contract");
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Request(op, user, block, contract)));
		});
		// POST /contract-test-post?block=&reward= - an acquisition contract posted at the block (start and end) with
		// the mod API, as P3-BLK-06 does. A player-made contract: the reward leaves the block owner's account.
		TestRoutes.Add("POST", "/contract-test-post", context =>
		{
			long block = TestRoutes.Long(context, "block");
			int reward = int.Parse(TestRoutes.Query(context, "reward", "1000"));
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
			{
				if (MyEntities.GetEntityById(block) is not MyContractBlock)
					throw new ArgumentException("No contract block " + block + " on this node");
				var contract = new Sandbox.ModAPI.Contracts.MyContractAcquisition(block, reward, 0, 3600, block,
					MyDefinitionId.Parse("MyObjectBuilder_Component/SteelPlate"), 3);
				var result = MyAPIGateway.ContractSystem.AddContract(contract);
				return (object)new { block, success = result.Success, contractId = result.ContractId };
			}));
		});
		// GET /contract-test-offers?block= - the contracts this node offers at the block, and whether it hosts it.
		TestRoutes.Add("GET", "/contract-test-offers", context =>
		{
			long block = TestRoutes.Long(context, "block");
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
			{
				var system = MySession.Static.GetComponent<MySessionComponentContractSystem>();
				long[] offered = system?.GetAvailableContractsForBlock(block).ToArray().Select(contract => contract.Id).ToArray() ?? Array.Empty<long>();
				return (object)new { block, here = MyEntities.GetEntityById(block) is MyContractBlock, contracts = offered };
			}));
		});
		// GET /contract-test-offer-copy?contract= - the offer as this node holds it, as a deflated base64url builder for
		// /contract-test-reoffer: the copy a block entry from an older save carries (#1272).
		TestRoutes.Add("GET", "/contract-test-offer-copy", context =>
		{
			long contract = TestRoutes.Long(context, "contract");
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
			{
				MyContract offer = System().GetInactiveContractById(contract) ?? throw new ArgumentException("No offer " + contract + " on this node");
				return (object)new { contract, offer = Pack(offer.GetObjectBuilder()) };
			}));
		});
		// POST /contract-test-reoffer?offer= - adds that copy back as an offer at its block on this node, as adopting a
		// stale block entry did before #1272.
		TestRoutes.Add("POST", "/contract-test-reoffer", context =>
		{
			MyObjectBuilder_Contract builder = Unpack(TestRoutes.Query(context, "offer"));
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
			{
				MySessionComponentContractSystem system = System();
				bool known = system.m_inactiveContracts.ContainsKey(builder.Id) || system.m_activeContracts.ContainsKey(builder.Id);
				if (!known) system.AddContract(MyContractFactory.CreateInstance(builder));
				return (object)new { contract = builder.Id, block = builder.StartBlock, added = !known, offered = system.IsContractInInactive(builder.Id) };
			}));
		});
		// POST /contract-test-delete-grid?grid= - close the grid the way an admin delete does: not a cluster close, so
		// vanilla's close handler runs.
		TestRoutes.Add("POST", "/contract-test-delete-grid", context =>
		{
			long grid = TestRoutes.Long(context, "grid");
			TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
			{
				if (MyEntities.GetEntityById(grid) is not MyCubeGrid entity) throw new ArgumentException("No grid " + grid + " on this node");
				entity.Close();
				return (object)new { grid, closed = true };
			}));
		});
	}

	private static int Int(System.Net.HttpListenerContext context, string name, int fallback)
		=> int.Parse(TestRoutes.Query(context, name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

	private static MySessionComponentContractSystem System()
		=> MySession.Static.GetComponent<MySessionComponentContractSystem>() ?? throw new InvalidOperationException("No contract system");

	// A mod contract names a contract type definition; any loaded one will do.
	private static MyDefinitionId ContractType() => MyDefinitionManager.Static.GetContractTypeDefinitions().Select(entry => entry.Key)
		.OrderBy(id => id.SubtypeName, StringComparer.Ordinal).First();

	private static object Post(long block, IMyContract contract)
	{
		if (MyEntities.GetEntityById(block) is not MyContractBlock)
			throw new ArgumentException("No contract block " + block + " on this node");
		var received = new Received();
		contract.OnContractAcquired = player => received.Acquired.Add(player);
		contract.OnContractSucceeded = () => received.Succeeded++;
		contract.OnContractFailed = () => received.Failed++;
		MyAddContractResultWrapper result = MyAPIGateway.ContractSystem.AddContract(contract);
		if (result.Success) Callbacks[result.ContractId] = received;
		return new { block, success = result.Success, contractId = result.ContractId };
	}

	private static string Pack(MyObjectBuilder_Contract offer)
	{
		var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SessionComponentContractSystem>();
		builder.InactiveContracts = new MySerializableList<MyObjectBuilder_Contract> { offer };
		using var xml = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(xml, builder)) throw new InvalidOperationException("The offer did not serialize");
		using var packed = new MemoryStream();
		using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true)) xml.WriteTo(deflate);
		return Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	private static MyObjectBuilder_Contract Unpack(string value)
	{
		string base64 = value.Replace('-', '+').Replace('_', '/');
		byte[] packed = Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
		using var xml = new MemoryStream();
		using (var deflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress)) deflate.CopyTo(xml);
		xml.Position = 0;
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(xml, out MyObjectBuilder_SessionComponentContractSystem builder)
			|| builder.InactiveContracts == null || builder.InactiveContracts.Count != 1)
			throw new ArgumentException("Unreadable offer copy");
		return builder.InactiveContracts[0];
	}

	private static object Active(long contractId, long identityId)
	{
		MySessionComponentContractSystem system = System();
		MyContract contract = system.GetActiveContractById(contractId);
		MyIdentity identity = MySession.Static.Players.TryGetIdentity(identityId);
		return new
		{
			contract = contractId,
			active = system.IsContractActive(contractId),
			offered = system.IsContractInInactive(contractId),
			state = contract?.State.ToString(),
			owners = contract?.Owners.ToArray() ?? Array.Empty<long>(),
			conditionFinished = contract?.ContractCondition?.IsFinished,
			remainingSeconds = contract?.RemainingTime?.Seconds,
			listed = identity?.ActiveContracts.Contains(contractId) ?? false,
			// What the player's contract screen lists on this node.
			screen = system.GetActiveContractsForPlayer_OB(identityId)?.Select(builder => builder.Id).ToArray() ?? Array.Empty<long>()
		};
	}

	private static object Request(string op, ulong user, long blockId, long contractId)
	{
		MyPlayer player = MySession.Static.Players.GetPlayerById(new MyPlayer.PlayerId(user, 0))
			?? throw new InvalidOperationException("Player " + user + " is not attached to this node");
		if (MyEntities.GetEntityById(blockId) is not MyContractBlock block)
			throw new ArgumentException("No contract block " + blockId + " on this node");
		long identity = player.Identity.IdentityId;
		MyCharacter character = player.Character;
		int Packages() => character?.GetInventoryBase()?.GetItems().Count(item => item.Content is MyObjectBuilder_Package package
			&& package.ContractId == contractId) ?? 0;
		int before = Packages();
		using (MyEventContext.Set(new EndpointId(user), null, isInvokedLocally: false))
		{
			switch (op)
			{
				case "accept": block.AcceptContract(identity, contractId); break;
				case "finish": block.FinishContract(identity, contractId, character?.EntityId ?? 0); break;
				case "abandon": block.AbandonContract(identity, contractId); break;
				default: throw new ArgumentException("Unknown op " + op);
			}
		}
		return new { op, user, identity, block = blockId, character = character?.EntityId ?? 0, packagesBefore = before,
			packagesAfter = Packages(), contract = Active(contractId, identity) };
	}

	private static object SpawnBlock(long owner, Vector3D position, bool anyone)
	{
		var grid = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
		grid.GridSizeEnum = MyCubeSize.Large;
		grid.IsStatic = false;
		grid.CreatePhysics = true;
		grid.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
		grid.PositionAndOrientation = new MyPositionAndOrientation(position, Vector3D.Forward, Vector3D.Up);
		grid.DisplayName = "se1-0232-contract-block";
		for (int x = 0; x <= 2; x++)
			grid.CubeBlocks.Add(Block(MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock"), x, -1, owner));
		var contractBlock = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ContractBlock>("ContractBlock");
		contractBlock.AnyoneCanUse = anyone;
		grid.CubeBlocks.Add(Block(contractBlock, 1, 0, owner));
		MyEntity built = MyEntities.CreateFromObjectBuilderAndAdd(grid, fadeIn: false)
			?? throw new InvalidOperationException("Could not spawn the contract block grid");
		MyContractBlock block = ((MyCubeGrid)built).GetFatBlocks<MyContractBlock>().First();
		return new { grid = built.EntityId, block = block.EntityId, owner = block.OwnerId };
	}

	private static MyObjectBuilder_CubeBlock Block(MyObjectBuilder_CubeBlock block, int x, int y, long owner)
	{
		block.Min = new SerializableVector3I(x, y, 0);
		block.BuiltBy = owner;
		block.Owner = owner;
		return block;
	}
}
