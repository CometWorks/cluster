using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.GameServices;
using VRage.Serialization;
using VRageMath;

namespace ClusterPlugin;

public static class MyPlayerCollectionPatch
{
	private const string Subsystem = "identities";
	private const string PlayerSubsystem = "players";
	private const string PlayerRebindSubsystem = "player-rebind";
	private static readonly object SnapshotSync = new object();
	private static string m_snapshot = IdentityAuthorityCodec.WriteSnapshot(Array.Empty<IdentityAuthorityEntry>());
	private static readonly Dictionary<MyPlayer.PlayerId, long> Bindings = new Dictionary<MyPlayer.PlayerId, long>();
	private static readonly Dictionary<long, MyPlayer.PlayerId> ReverseBindings = new Dictionary<long, MyPlayer.PlayerId>();
	private static readonly Dictionary<ulong, PlayerReady> PendingAttach = new Dictionary<ulong, PlayerReady>();

	/// <summary>
	/// SE-1081: clients the Gateway admitted through Direct Transport. Unlike <see cref="PendingAttach"/> an id
	/// stays here after its detach: the player can still be in the online list for a report or two after the
	/// binding left, and the departure must still read as a Direct Transport one. A later attach of the same id
	/// over Steam takes it out again.
	/// </summary>
	private static readonly HashSet<ulong> DirectTransportClients = new HashSet<ulong>();

	/// <summary>
	/// An immutable copy of <see cref="Bindings"/> and <see cref="ReverseBindings"/>, republished under
	/// <see cref="SnapshotSync"/> after every mutation. The two hot readers take this reference and never
	/// take the lock.
	/// </summary>
	/// <remarks>
	/// <c>MyPlayerCollection.TryGetPlayerId</c> is lock-free in vanilla and the engine calls it from its
	/// PARALLEL entity update, so locking that read funnelled nine ParallelTasks workers and the game thread
	/// through one monitor. It cost more than every other plugin call on the game thread except the per-frame
	/// tick itself: 316 of 316 sampled appearances of <see cref="TryGetPlayerIdPrefix"/> were inside
	/// Monitor.Enter_Slowpath, 372 of the 2447 ms of contended lock wait in a 30 s node-3 trace - the largest
	/// single caller of any, engine or plugin (findings/player-binding-lock-blocks-ten-threads-in-a-lock-free-engine-path.md).
	/// The swap is also what makes a lock-free read CORRECT rather than merely fast. <see cref="ApplySnapshot"/>
	/// and <see cref="AuthorityChanged"/> clear both maps before refilling them, and a reader that observed
	/// that intermediate would answer "no player" for a player that has one; today it cannot, only because it
	/// blocks. A published view is never mutated afterwards, so a reader sees old-complete or new-complete and
	/// never the gap - dropping the lock WITHOUT this swap would be a bug.
	/// </remarks>
	private sealed record BindingView(Dictionary<MyPlayer.PlayerId, long> Forward,
		Dictionary<long, MyPlayer.PlayerId> Reverse);
	private static volatile BindingView m_bindingView = new BindingView(
		new Dictionary<MyPlayer.PlayerId, long>(), new Dictionary<long, MyPlayer.PlayerId>());
	private static bool m_snapshotReady;
	private static volatile MySession m_readySession;
	[ThreadStatic] private static bool m_applying;
	[ThreadStatic] private static bool m_applyNpc;
	[ThreadStatic] private static bool m_applySendToClients;
	private static double m_nextRefresh;

	public static void Install()
	{
		MyPlayerCollectionSeams.ClusterCreateIdentity = SeamCreateIdentity;
		MyPlayerCollectionSeams.ClusterCreateNpcIdentity = SeamCreateNpcIdentity;
		MyPlayerCollectionSeams.ClusterAfterCreateIdentity = AfterCreateIdentityPrefix;
		MyPlayerCollectionSeams.ClusterBeforeNewPlayerRequest = BeforeNewPlayerRequest;
		MyPlayerCollectionSeams.ClusterInitialPlayerIdentityIsNew = InitialPlayerIdentityIsNew;
		MyPlayerCollectionSeams.ClusterTryGetPlayerId = TryGetPlayerIdPrefix;
		MyPlayerCollectionSeams.ClusterTryGetIdentityId = TryGetIdentityIdPrefix;
		MyPlayerCollectionSeams.ClusterLoadPlayer = LoadPlayerPrefix;
		MyPlayerCollectionSeams.ClusterCreatePlayer = CreateNewPlayerPrefix;
		MyPlayerCollectionSeams.ClusterRespawnRequestPrefix = RespawnRequestPrefix;
		DirectTransportNodeLinkPatch.AttachBindingAccepted = AcceptAttachBinding;
		DirectTransportNodeLinkPatch.AttachBindingRemoved = RemoveAttachBinding;
		MyPlayerPatch.Install();
		GlobalStateChannel.RegisterCommandHandler(Subsystem, HandleCommand, journaled: true, replay: ReplayIdentity);
		GlobalStateChannel.RegisterCommandHandler(PlayerSubsystem, HandlePlayerCommand, journaled: true, replay: ReplayPlayer);
		GlobalStateChannel.RegisterCommandHandler(PlayerRebindSubsystem, HandleRebindCommand, journaled: true);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, CurrentSnapshot, ApplySnapshot);
	}

	public static bool RespawnRequestPrefix(MyPlayerCollection.RespawnMsg msg)
		=> RespawnRegistryService.RouteExactRespawn(msg);

	public static void Update()
	{
		MySession session = MySession.Static;
		if (session?.Ready != true) return;
		m_readySession = session;
		if (!ClusterRuntime.IsWorldAuthority || ClusterRuntime.LocalMilliseconds < m_nextRefresh) return;
		m_nextRefresh = ClusterRuntime.LocalMilliseconds + 1000;
		PublishSnapshot();
	}

	public static bool CreateIdentityPrefix(MyPlayerCollection __instance, string name, string model, Vector3? colorMask,
		bool initialPlayer, bool addToNpcs, ref MyIdentity __result)
	{
		if (m_readySession != MySession.Static || m_applying || !ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority) return true;
		var request = new IdentityAuthorityRequest("Player", name, model, colorMask.HasValue,
			colorMask?.X ?? 0, colorMask?.Y ?? 0, colorMask?.Z ?? 0, initialPlayer, addToNpcs);
		IdentityAuthorityEntry entry = IdentityAuthorityCodec.ReadEntry(GlobalStateChannel.SendCommand(Subsystem, IdentityAuthorityCodec.WriteRequest(request)));
		__result = ApplyEntry(__instance, entry, !initialPlayer);
		return false;
	}

	public static bool CreateNpcIdentityPrefix(MyPlayerCollection __instance, string name, long identityId, ref MyIdentity __result)
	{
		if (identityId != 0 || m_readySession != MySession.Static || m_applying || !ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority) return true;
		var request = new IdentityAuthorityRequest("Npc", name, null, false, 0, 0, 0, false, true);
		IdentityAuthorityEntry entry = IdentityAuthorityCodec.ReadEntry(GlobalStateChannel.SendCommand(Subsystem, IdentityAuthorityCodec.WriteRequest(request)));
		__result = ApplyEntry(__instance, entry, true);
		return false;
	}

	public static void AfterCreateIdentityPrefix(MyIdentity identity, ref bool addToNpcs, ref bool sendToClients)
	{
		if (!m_applying) return;
		addToNpcs = m_applyNpc;
		sendToClients = m_applySendToClients;
	}

	public static bool AcceptAttachBinding(ulong clientId, byte[] payload)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.AuthorityGeneration <= 0) return payload == null || payload.Length == 0;
		if (ClusterRuntime.IsWorldAuthority || payload == null || payload.Length == 0) return false;
		try
		{
			PlayerReady ready = IdentityAuthorityCodec.ReadReady(Encoding.UTF8.GetString(payload));
			if (ready.UserId != clientId || ready.AuthorityGeneration != ClusterRuntime.AuthorityGeneration) return false;
			lock (SnapshotSync)
			{
				var playerId = new MyPlayer.PlayerId(ready.UserId, ready.SerialId);
				if (Bindings.TryGetValue(playerId, out long existing) && existing != ready.Identity.IdentityId)
					ReplaceBinding(playerId, existing, ready.Identity.IdentityId);
				else
					SetBinding(playerId, ready.Identity.IdentityId);
				PendingAttach[clientId] = ready;
				if (ready.ServiceName == DirectTransportServiceName) DirectTransportClients.Add(clientId);
				else DirectTransportClients.Remove(clientId);
			}
			PlayerEntitlements.Attach(ready);
			return true;
		}
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] player_attach rejected=true client=" + clientId
				+ " error=" + SecretRedactor.Redact(exception.Message));
			return false;
		}
	}

	public static bool BeforeNewPlayerRequest(MyPlayerCollection.NewPlayerRequestParameters parameters)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.AuthorityGeneration <= 0 || parameters.IsWildlifeEntity || !parameters.RealPlayer) return true;
		PlayerReady ready;
		lock (SnapshotSync)
		{
			if (!PendingAttach.TryGetValue(parameters.ClientSteamId, out ready) || ready.SerialId != parameters.PlayerSerialId
				|| ready.AuthorityGeneration != ClusterRuntime.AuthorityGeneration)
			{
				// A client asks for its player exactly once per world load, so a refusal here is permanent: the
				// client keeps its network connection and every replicated identity but never gets a MyPlayer,
				// which leaves the respawn table empty and every identity-dependent screen inert. Always say why.
				Console.WriteLine("[ClusterPlugin] player_request refused=true client=" + parameters.ClientSteamId
					+ " serial=" + parameters.PlayerSerialId + " pending=" + (ready != null)
					+ " pendingSerial=" + (ready?.SerialId ?? -1)
					+ " generation=" + ClusterRuntime.AuthorityGeneration
					+ " bindingGeneration=" + (ready?.AuthorityGeneration ?? 0));
				return false;
			}
		}
		bool identityExisted = MySession.Static.Players.TryGetIdentity(ready.Identity.IdentityId) != null;
		Console.WriteLine("[ClusterPlugin] player_request accepted=true client=" + parameters.ClientSteamId
			+ " serial=" + parameters.PlayerSerialId + " identity=" + ready.Identity.IdentityId
			+ " identityExisted=" + identityExisted);
		ApplyEntry(MySession.Static.Players, ready.Identity, sendToClients: true);
		// ApplyEntry announces nothing when this node already holds the identity, which is the common case:
		// identities are allocated at the World Authority and replicated between nodes, never to clients. The
		// OnPlayerCreated broadcast that vanilla is about to send would then name an identity this client has
		// never heard of, and its handler returns at the TryGetIdentity guard with nothing to retry it. Announce
		// unconditionally; a client that already knows the identity ignores the repeat.
		MySession.Static.Players.ClusterAnnounceIdentity(ready.Identity.IdentityId);
		return true;
	}

	/// <summary>
	/// Tells a joining cluster client that its identity is new, so it materializes the identity from the same
	/// message that carries its player. The World Authority allocates identities and replicates them between
	/// nodes, so by admission time this node already holds the identity and vanilla would report
	/// <c>newIdentity=false</c> - but the client has never seen it, and <c>OnPlayerCreated</c> silently returns
	/// when the identity is unknown. Announcing separately is not enough: that announcement is an RPC while the
	/// initial player arrives as a raw PLAYER_DATA packet, so the two can be delivered out of order.
	/// </summary>
	public static bool InitialPlayerIdentityIsNew(ulong clientSteamId, bool vanilla)
	{
		if (vanilla || !ClusterRuntime.IsClustered || ClusterRuntime.AuthorityGeneration <= 0) return vanilla;
		lock (SnapshotSync) return PendingAttach.ContainsKey(clientSteamId);
	}

	public static void AuthorityChanged(long generation)
	{
		lock (SnapshotSync)
		{
			Bindings.Clear();
			ReverseBindings.Clear();
			PublishBindingView();
			PendingAttach.Clear();
			m_snapshotReady = false;
			m_snapshot = IdentityAuthorityCodec.WriteSnapshot(Array.Empty<IdentityAuthorityEntry>());
		}
	}

	public static void Rebind(MyPlayer player, long previousIdentityId, MyIdentity identity)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.AuthorityGeneration <= 0 || m_applying || !player.IsRealPlayer) return;
		MyPlayer.PlayerId playerId = player.Id;
		long identityId = identity.IdentityId;
		lock (SnapshotSync)
			if (!Bindings.TryGetValue(playerId, out long existing))
				throw new InvalidOperationException("World Authority player bindings are not synchronized");
			else if (existing != previousIdentityId) throw new InvalidOperationException("Player identity changed from a non-canonical binding");
		if (!ClusterRuntime.IsWorldAuthority)
			GlobalStateChannel.SendCommand(PlayerRebindSubsystem, IdentityAuthorityCodec.WriteRebindRequest(
				new PlayerRebindRequest(playerId.SteamId, playerId.SerialId, previousIdentityId, identityId)));
		lock (SnapshotSync)
		{
			ReplaceBinding(playerId, previousIdentityId, identityId);
			if (PendingAttach.TryGetValue(playerId.SteamId, out PlayerReady ready) && ready.SerialId == playerId.SerialId)
				PendingAttach[playerId.SteamId] = ready with
				{
					Identity = From(identity, MySession.Static.Players.IdentityIsNpc(identityId))
				};
		}
		if (ClusterRuntime.IsWorldAuthority) PublishSnapshot();
	}

	public static bool TryGetPlayerIdPrefix(MyPlayerCollection __instance, long identityId,
		ref MyPlayer.PlayerId result, ref bool __result)
	{
		if (!ClusterRuntime.IsClustered) return true;
		// No lock: see BindingView. The engine calls this from its parallel entity update.
		if (!m_bindingView.Reverse.TryGetValue(identityId, out result)) return true;
		__result = true;
		return false;
	}

	public static bool TryGetIdentityIdPrefix(MyPlayerCollection __instance, ulong steamId, int serialId, ref long __result)
	{
		if (!ClusterRuntime.IsClustered) return true;
		// No lock: see BindingView.
		if (!m_bindingView.Forward.TryGetValue(new MyPlayer.PlayerId(steamId, serialId), out __result)) return true;
		return false;
	}

	private static readonly FieldInfo VanillaPlayerIdentityIds = AccessTools.Field(typeof(MyPlayerCollection), "m_playerIdentityIds");
	private static readonly FieldInfo VanillaIdentityPlayerIds = AccessTools.Field(typeof(MyPlayerCollection), "m_identityPlayerIds");

	/// <summary>
	/// T-0165: the World Authority hosts no MyPlayer for a remote client, so vanilla's player maps do not
	/// know it. The binding prefix on TryGetIdentityId covers that for our own calls, but the JIT inlines
	/// that four-line lookup into vanilla request handlers (CreateFactionRequest, EditFactionRequest,
	/// FactionStateChangeRequest, ...), which then see identity 0 and drop the request as a validation
	/// failure. While the authority executes a forwarded request, put the sender's canonical binding into
	/// the vanilla maps, and take it out again afterwards. Game thread only (the maps are not synchronized
	/// for writers).
	/// </summary>
	internal static IDisposable ExposeBindingToVanilla(ulong steamId)
	{
		MyPlayerCollection players = MySession.Static?.Players;
		if (!ClusterRuntime.IsWorldAuthority || players == null || steamId == 0) return null;
		var playerId = new MyPlayer.PlayerId(steamId, 0);
		if (!m_bindingView.Forward.TryGetValue(playerId, out long identityId)) return null;
		var forward = (ConcurrentDictionary<MyPlayer.PlayerId, long>)VanillaPlayerIdentityIds.GetValue(players);
		var reverse = (Dictionary<long, MyPlayer.PlayerId>)VanillaIdentityPlayerIds.GetValue(players);
		bool addForward = forward.TryAdd(playerId, identityId);
		bool addReverse = reverse.TryAdd(identityId, playerId);
		return addForward || addReverse ? new VanillaBindingScope(forward, reverse, playerId, identityId, addForward, addReverse) : null;
	}

	private sealed class VanillaBindingScope(ConcurrentDictionary<MyPlayer.PlayerId, long> forward,
		Dictionary<long, MyPlayer.PlayerId> reverse, MyPlayer.PlayerId playerId, long identityId, bool addedForward, bool addedReverse)
		: IDisposable
	{
		public void Dispose()
		{
			// A request that created the player for real (it cannot here, but vanilla might one day) now
			// owns the entry; only remove what is still exactly ours.
			if (addedForward && forward.TryGetValue(playerId, out long current) && current == identityId
				&& !MySession.Static.Players.TryGetPlayerById(playerId, out _))
				forward.TryRemove(playerId, out _);
			if (addedReverse && reverse.TryGetValue(identityId, out MyPlayer.PlayerId owner) && owner.Equals(playerId)
				&& !MySession.Static.Players.TryGetPlayerById(playerId, out _))
				reverse.Remove(identityId);
		}
	}

	internal static bool IsCanonicalBinding(ulong userId, int serialId, long identityId)
	{
		lock (SnapshotSync)
			return Bindings.TryGetValue(new MyPlayer.PlayerId(userId, serialId), out long canonical) && canonical == identityId;
	}

	public static void LoadPlayerPrefix(MyPlayerCollection __instance, ref MyPlayer.PlayerId playerId, MyObjectBuilder_Player playerOb, bool obsolete)
	{
		if (!ClusterRuntime.IsWorldAuthority || playerOb?.IdentityId <= 0 || playerOb.IsWildlifeAgent || playerId.SteamId == Sync.MyId) return;
		MyIdentity identity = __instance.TryGetIdentity(playerOb.IdentityId);
		if (identity == null || obsolete && identity.IsDead) return;
		lock (SnapshotSync) SetBinding(playerId, playerOb.IdentityId);
	}

	public static void CreateNewPlayerPrefix(MyPlayer.PlayerId playerId, MyObjectBuilder_Player playerBuilder,
		ConcurrentDictionary<MyPlayer.PlayerId, long> ___m_playerIdentityIds,
		Dictionary<long, MyPlayer.PlayerId> ___m_identityPlayerIds)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.AuthorityGeneration <= 0 || playerBuilder == null) return;
		long identityId;
		lock (SnapshotSync)
			if (!Bindings.TryGetValue(playerId, out identityId)) return;
		if (___m_playerIdentityIds.TryGetValue(playerId, out long previousIdentityId))
			___m_identityPlayerIds.Remove(previousIdentityId);
		___m_playerIdentityIds[playerId] = identityId;
		___m_identityPlayerIds[identityId] = playerId;
		playerBuilder.IdentityId = identityId;
	}

	private static MyIdentity SeamCreateIdentity(MyPlayerCollection instance, string name, string model, Vector3? colorMask,
		bool initialPlayer, bool addToNpcs)
	{
		MyIdentity result = null;
		return CreateIdentityPrefix(instance, name, model, colorMask, initialPlayer, addToNpcs, ref result) ? null : result;
	}

	private static MyIdentity SeamCreateNpcIdentity(MyPlayerCollection instance, string name, long identityId)
	{
		MyIdentity result = null;
		return CreateNpcIdentityPrefix(instance, name, identityId, ref result) ? null : result;
	}

	private static string HandleCommand(string origin, string payload)
	{
		IdentityAuthorityRequest request = IdentityAuthorityCodec.ReadRequest(payload);
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		MySandboxGame game = MySandboxGame.Static ?? throw new InvalidOperationException("The World Authority game is unavailable");
		Action execute = () =>
		{
			try
			{
				MyIdentity identity = request.Kind == "Npc"
					? MySession.Static.Players.CreateNewNpcIdentity(request.Name)
					: MySession.Static.Players.CreateNewIdentity(request.Name, request.Model,
						request.HasColor ? new Vector3(request.ColorX, request.ColorY, request.ColorZ) : null,
						request.InitialPlayer, request.AddToNpcs);
				if (MyBankingSystem.Static != null && !MyBankingSystem.Static.TryGetAccountInfo(identity.IdentityId, out var _))
					MyBankingSystem.Static.CreateAccount(identity.IdentityId);
				IdentityAuthorityEntry entry = From(identity, request.Kind == "Npc" || request.AddToNpcs);
				PublishSnapshot();
				completion.SetResult(IdentityAuthorityCodec.WriteEntry(entry));
			}
			catch (Exception exception) { completion.SetException(exception); }
		};
		if (Thread.CurrentThread == game.UpdateThread) execute();
		else game.Invoke(execute, "ClusterPlugin identity allocation");
		return completion.Task.GetAwaiter().GetResult();
	}

	private static string HandlePlayerCommand(string origin, string payload)
	{
		if (origin != "$gateway") throw new InvalidOperationException("Player bindings must originate at the Gateway");
		PlayerBindingRequest request = IdentityAuthorityCodec.ReadBindingRequest(payload);
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		MySandboxGame game = MySandboxGame.Static ?? throw new InvalidOperationException("The World Authority game is unavailable");
		Action bind = () =>
		{
			try
			{
				var playerId = new MyPlayer.PlayerId(request.UserId, request.SerialId);
				MyIdentity identity = MySession.Static.Players.TryGetPlayerIdentity(playerId);
				if (identity == null)
				{
					// A replacement World Authority restores identities and cluster bindings from the
					// journal, but vanilla's player->identity map comes from its own checkpoint, which
					// never saw a player hosted on another node. The restored cluster binding is
					// canonical here: minting a fresh identity instead would change the player's
					// identity across authority generations and fail every gateway admission refresh.
					long bound;
					lock (SnapshotSync) Bindings.TryGetValue(playerId, out bound);
					if (bound != 0) identity = MySession.Static.Players.TryGetIdentity(bound);
				}
				if (identity == null && request.ExpectedIdentityId > 0)
				{
					// The journaled save a replacement World Authority restored may predate this player's
					// admission entirely, leaving no binding to recover above. The gateway session is the
					// durable holder of the identity granted under the previous generation, so honor its
					// expected id — recreate the identity under the SAME id when it is gone — unless that
					// id is already bound to a different player. Node-side characters and ownership
					// reference the id, so minting a fresh one would orphan them.
					bool taken;
					lock (SnapshotSync) taken = ReverseBindings.TryGetValue(request.ExpectedIdentityId, out MyPlayer.PlayerId holder)
						&& holder != playerId;
					if (!taken && MySession.Static.Players.TryGetPlayerId(request.ExpectedIdentityId, out MyPlayer.PlayerId vanillaHolder)
						&& vanillaHolder != playerId)
						taken = true;
					if (!taken)
						identity = MySession.Static.Players.TryGetIdentity(request.ExpectedIdentityId)
							?? ApplyEntry(MySession.Static.Players, new IdentityAuthorityEntry(request.ExpectedIdentityId,
								request.Name, null, false, 0f, 0f, 0f, false), sendToClients: true);
				}
				if (identity == null)
					identity = MySession.Static.Players.CreateNewIdentity(request.Name, null, null, initialPlayer: true, addToNpcs: false);
				else if (JoinedName(identity.DisplayPlatformName, request.Name, request.UserId) is string renamed)
					identity.SetDisplayName(renamed);
				lock (SnapshotSync) SetBinding(playerId, identity.IdentityId);
				if (MyBankingSystem.Static != null && !MyBankingSystem.Static.TryGetAccountInfo(identity.IdentityId, out var _))
					MyBankingSystem.Static.CreateAccount(identity.IdentityId);
				IdentityAuthorityEntry entry = From(identity, isNpc: false);
				PublishSnapshot();
				completion.SetResult(IdentityAuthorityCodec.WriteReady(new PlayerReady(ClusterRuntime.AuthorityGeneration,
					request.ServiceName, request.UserId, request.OwnerId, request.SerialId, request.IsAdmin, entry)));
			}
			catch (Exception exception) { completion.SetException(exception); }
		};
		if (Thread.CurrentThread == game.UpdateThread) bind();
		else game.Invoke(bind, "ClusterPlugin player binding");
		return completion.Task.GetAwaiter().GetResult();
	}

	private static string HandleRebindCommand(string origin, string payload)
	{
		if (origin == "$gateway") throw new InvalidOperationException("Player identity replacement must originate at a regular node");
		PlayerRebindRequest request = IdentityAuthorityCodec.ReadRebindRequest(payload);
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		MySandboxGame game = MySandboxGame.Static ?? throw new InvalidOperationException("The World Authority game is unavailable");
		Action rebind = () =>
		{
			try
			{
				var playerId = new MyPlayer.PlayerId(request.UserId, request.SerialId);
				if (MySession.Static.Players.TryGetIdentity(request.IdentityId) == null)
					throw new InvalidOperationException("Replacement player identity does not exist at the World Authority");
				lock (SnapshotSync)
				{
					if (!Bindings.TryGetValue(playerId, out long existing))
						throw new InvalidOperationException("Player binding does not exist at the World Authority");
					if (existing != request.IdentityId) ReplaceBinding(playerId, request.PreviousIdentityId, request.IdentityId);
				}
				PublishSnapshot();
				completion.SetResult(string.Empty);
			}
			catch (Exception exception) { completion.SetException(exception); }
		};
		if (Thread.CurrentThread == game.UpdateThread) rebind();
		else game.Invoke(rebind, "ClusterPlugin player identity replacement");
		return completion.Task.GetAwaiter().GetResult();
	}

	/// <summary>
	/// #1318: a replacement World Authority replays a journaled allocation with the identity id the node was given (the
	/// handler would allocate a new one), on the World Authority's game thread.
	/// </summary>
	private static string ReplayIdentity(string origin, string payload, string result)
	{
		MyIdentity identity = ApplyEntry(MySession.Static.Players, IdentityAuthorityCodec.ReadEntry(result), sendToClients: false);
		if (MyBankingSystem.Static != null && !MyBankingSystem.Static.TryGetAccountInfo(identity.IdentityId, out var _))
			MyBankingSystem.Static.CreateAccount(identity.IdentityId);
		PublishSnapshot();
		return result;
	}

	/// <summary>#1318: a journaled player binding, replayed with the identity the Gateway was given.</summary>
	private static string ReplayPlayer(string origin, string payload, string result)
	{
		PlayerReady ready = IdentityAuthorityCodec.ReadReady(result);
		MyIdentity identity = ApplyEntry(MySession.Static.Players, ready.Identity, sendToClients: false);
		lock (SnapshotSync) SetBinding(new MyPlayer.PlayerId(ready.UserId, ready.SerialId), identity.IdentityId);
		if (MyBankingSystem.Static != null && !MyBankingSystem.Static.TryGetAccountInfo(identity.IdentityId, out var _))
			MyBankingSystem.Static.CreateAccount(identity.IdentityId);
		PublishSnapshot();
		return result;
	}

	private static void ApplySnapshot(string payload)
	{
		IdentityAuthoritySnapshot snapshot = IdentityAuthorityCodec.ReadSnapshot(payload);
		MyPlayerCollection players = MySession.Static.Players;
		var wanted = snapshot.Identities.ToDictionary(x => x.IdentityId);
		lock (SnapshotSync)
		{
			Bindings.Clear();
			ReverseBindings.Clear();
			// SetBinding republishes per entry, but an empty snapshot has no entry to do it.
			PublishBindingView();
			foreach (PlayerBindingEntry binding in snapshot.Bindings)
				SetBinding(new MyPlayer.PlayerId(binding.UserId, binding.SerialId), binding.IdentityId);
			foreach (PlayerReady ready in PendingAttach.Values)
				if (ready.AuthorityGeneration == ClusterRuntime.AuthorityGeneration)
					SetBinding(new MyPlayer.PlayerId(ready.UserId, ready.SerialId), ready.Identity.IdentityId);
		}
		bool removed = false;
		bool previousApplying = m_applying;
		m_applying = true;
		try
		{
			foreach (MyIdentity identity in players.GetAllIdentities().ToArray())
			{
				if (wanted.ContainsKey(identity.IdentityId) || players.TryGetPlayerId(identity.IdentityId, out _)) continue;
				MyBankingSystemPatch.ApplyLocally(() => players.RemoveIdentity(identity.IdentityId));
				removed = true;
			}
			foreach (IdentityAuthorityEntry entry in snapshot.Identities)
			{
				MyIdentity identity = players.TryGetIdentity(entry.IdentityId);
				if (identity == null)
				{
					ApplyEntry(players, entry, true);
					continue;
				}
				if (From(identity, players.IdentityIsNpc(identity.IdentityId)) != entry
					&& !players.TryGetPlayerId(identity.IdentityId, out _))
				{
					MyBankingSystemPatch.ApplyLocally(() => players.RemoveIdentity(identity.IdentityId));
					ApplyEntry(players, entry, true);
					removed = true;
					continue;
				}
				identity.SetDisplayName(entry.Name);
				if (entry.HasColor) identity.SetColorMask(new Vector3(entry.ColorX, entry.ColorY, entry.ColorZ));
				if (entry.IsNpc) players.MarkIdentityAsNPC(entry.IdentityId);
				else players.UnmarkIdentityAsNPC(entry.IdentityId);
			}
			foreach (MyPlayer player in players.GetOnlinePlayers().ToArray())
			{
				long identityId;
				lock (SnapshotSync) Bindings.TryGetValue(player.Id, out identityId);
				MyIdentity identity = identityId == 0 ? null : players.TryGetIdentity(identityId);
				if (identity != null && player.Identity?.IdentityId != identityId) player.Identity = identity;
			}
		}
		finally { m_applying = previousApplying; }
		if (removed)
		{
			GlobalStateChannel.RequestResync("banking");
			GlobalStateChannel.RequestResync("gps");
		}
	}

	private static MyIdentity ApplyEntry(MyPlayerCollection players, IdentityAuthorityEntry entry, bool sendToClients)
	{
		MyIdentity existing = players.TryGetIdentity(entry.IdentityId);
		if (existing != null) return existing;
		bool previousApplying = m_applying;
		bool previousNpc = m_applyNpc;
		bool previousSendToClients = m_applySendToClients;
		m_applying = true;
		m_applyNpc = entry.IsNpc;
		m_applySendToClients = sendToClients;
		try
		{
			return players.CreateNewIdentity(entry.Name, entry.IdentityId, entry.Model,
				entry.HasColor ? new Vector3(entry.ColorX, entry.ColorY, entry.ColorZ) : null);
		}
		finally
		{
			m_applying = previousApplying;
			m_applyNpc = previousNpc;
			m_applySendToClients = previousSendToClients;
		}
	}

	private static void PublishSnapshot()
	{
		string snapshot;
		lock (SnapshotSync)
		{
			snapshot = IdentityAuthorityCodec.WriteSnapshot(MySession.Static.Players.GetAllIdentities().Select(identity =>
				From(identity, MySession.Static.Players.IdentityIsNpc(identity.IdentityId))), Bindings.Select(x =>
					new PlayerBindingEntry(x.Key.SteamId, x.Key.SerialId, x.Value)));
			if (m_snapshotReady && m_snapshot == snapshot) return;
			m_snapshot = snapshot;
			m_snapshotReady = true;
		}
		GlobalStateChannel.Publish(Subsystem, snapshot);
	}

	private static string CurrentSnapshot() { lock (SnapshotSync) return m_snapshotReady ? m_snapshot : null; }

	public static void AddBindingsToCheckpoint(MyObjectBuilder_Checkpoint checkpoint)
	{
		if (!ClusterRuntime.IsWorldAuthority) return;
		checkpoint.AllPlayersData ??= new SerializableDictionary<MyObjectBuilder_Checkpoint.PlayerId, MyObjectBuilder_Player>();
		lock (SnapshotSync)
			foreach ((MyPlayer.PlayerId playerId, long identityId) in Bindings)
			{
				var key = new MyObjectBuilder_Checkpoint.PlayerId(playerId.SteamId, playerId.SerialId);
				if (!checkpoint.AllPlayersData.Dictionary.TryGetValue(key, out MyObjectBuilder_Player player))
				{
					player = new MyObjectBuilder_Player { Connected = false };
					checkpoint.AllPlayersData.Dictionary.Add(key, player);
				}
				player.DisplayName = MySession.Static.Players.TryGetIdentity(identityId)?.DisplayPlatformName;
				player.IdentityId = identityId;
			}
	}

	private static void SetBinding(MyPlayer.PlayerId playerId, long identityId)
	{
		if (Bindings.TryGetValue(playerId, out long existing) && existing != identityId)
			throw new InvalidOperationException("Player already has another identity");
		if (ReverseBindings.TryGetValue(identityId, out MyPlayer.PlayerId owner) && owner != playerId)
			throw new InvalidOperationException("Identity already has another player");
		Bindings[playerId] = identityId;
		ReverseBindings[identityId] = playerId;
		PublishBindingView();
	}

	private static void ReplaceBinding(MyPlayer.PlayerId playerId, long previousIdentityId, long identityId)
	{
		if (!Bindings.TryGetValue(playerId, out long existing) || existing != previousIdentityId)
			throw new InvalidOperationException("Player binding changed before identity replacement");
		if (ReverseBindings.TryGetValue(identityId, out MyPlayer.PlayerId owner) && owner != playerId)
			throw new InvalidOperationException("Replacement identity already has another player");
		ReverseBindings.Remove(previousIdentityId);
		Bindings[playerId] = identityId;
		ReverseBindings[identityId] = playerId;
		PublishBindingView();
	}

	/// <summary>
	/// Republishes the lock-free reader view. Call under <see cref="SnapshotSync"/>, after the last mutation
	/// of a critical section - a clear that is about to be refilled still publishes, so that a section which
	/// ends up empty leaves no stale view behind.
	/// </summary>
	private static void PublishBindingView()
		=> m_bindingView = new BindingView(new Dictionary<MyPlayer.PlayerId, long>(Bindings),
			new Dictionary<long, MyPlayer.PlayerId>(ReverseBindings));

	/// <summary>The ServiceName the Gateway gives a client it admitted through Direct Transport.</summary>
	private const string DirectTransportServiceName = "DirectTransport";

	/// <summary>Whether the Gateway admitted this client through Direct Transport (SE-1081).</summary>
	internal static bool IsDirectTransport(ulong clientId)
	{
		lock (SnapshotSync) return DirectTransportClients.Contains(clientId);
	}

	private static void RemoveAttachBinding(ulong clientId)
	{
		lock (SnapshotSync) PendingAttach.Remove(clientId);
		PlayerEntitlements.Detach(clientId);
	}

	/// <summary>The identities of every client attached to this node, joined or still joining. The attach
	/// binding arrives with the gateway link, seconds before the client asks for its MyPlayer, and leaves only
	/// on detach - so unlike GetOnlinePlayers it already names a client that is mid-join (T-0233).</summary>
	internal static HashSet<long> AttachedIdentities()
	{
		lock (SnapshotSync) return PendingAttach.Values.Select(ready => ready.Identity.IdentityId).ToHashSet();
	}

	/// <summary>
	/// SE-1100: the name an identity takes when its player logs in as <paramref name="joined"/>, or null to keep
	/// <paramref name="current"/>. Vanilla renames the identity on every world request, but on a cluster that
	/// node-local rename lasts only until the World Authority's next identity snapshot, so the WA renames it
	/// here. A stored platform icon (a Steam player's \ue030) stays when the joining name has none, and a client
	/// without a name, which the Gateway names after its id, keeps the old one.
	/// </summary>
	internal static string JoinedName(string current, string joined, ulong userId)
	{
		if (string.IsNullOrEmpty(joined) || joined == userId.ToString(CultureInfo.InvariantCulture)) return null;
		string name = PlatformIcon.TrimIcon(joined), had = PlatformIcon.TrimIcon(current ?? string.Empty);
		if (name.Length == 0 || name == had) return null;
		if (name != joined) return joined;
		return (current ?? string.Empty).Substring(0, (current ?? string.Empty).Length - had.Length) + name;
	}

	private static IdentityAuthorityEntry From(MyIdentity identity, bool isNpc) => new(identity.IdentityId, identity.DisplayPlatformName,
		identity.Model, identity.ColorMask.HasValue, identity.ColorMask?.X ?? 0, identity.ColorMask?.Y ?? 0, identity.ColorMask?.Z ?? 0, isNpc);
}
