using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Network;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyPlayerCollection"/>.
/// Shared/Patches/MyPlayerCollectionPatch.cs invokes the static delegates through Harmony. The cluster instance
/// members of the game type are extension methods here, written against the publicized assembly (no reflection).
/// </summary>
public static class MyPlayerCollectionSeams
{
	// ClusterPlugin seam adapters for MyPlayerCollectionPatch prefixes.
	public delegate void ClusterAfterCreateIdentityDelegate(MyIdentity identity, ref bool addToNpcs, ref bool sendToClients);
	public delegate bool ClusterTryGetPlayerIdDelegate(MyPlayerCollection instance, long identityId, ref MyPlayer.PlayerId result, ref bool found);
	public delegate bool ClusterTryGetIdentityIdDelegate(MyPlayerCollection instance, ulong steamId, int serialId, ref long result);
	public delegate void ClusterLoadPlayerDelegate(MyPlayerCollection instance, ref MyPlayer.PlayerId playerId, MyObjectBuilder_Player playerOb, bool obsolete);
	public delegate void ClusterCreatePlayerDelegate(MyPlayer.PlayerId playerId, MyObjectBuilder_Player playerBuilder,
		ConcurrentDictionary<MyPlayer.PlayerId, long> playerIdentityIds, Dictionary<long, MyPlayer.PlayerId> identityPlayerIds);

	public static Func<MyPlayerCollection, string, string, Vector3?, bool, bool, MyIdentity> ClusterCreateIdentity;
	public static Func<MyPlayerCollection, string, long, MyIdentity> ClusterCreateNpcIdentity;
	public static ClusterAfterCreateIdentityDelegate ClusterAfterCreateIdentity;
	public static Func<MyPlayerCollection.NewPlayerRequestParameters, bool> ClusterBeforeNewPlayerRequest;

	/// <summary>
	/// Diagnostic trace hook of MyControlAssignmentTracePatch. It is scaffolding for a fixed
	/// defect: the field exists so the installer compiles, but no patch invokes it.
	/// </summary>
	public static Action<string, ulong, int, long, bool> ClusterControlAssignment;

	// ClusterPlugin seam adapter for MyPlayerCollectionPatch.InitialPlayerIdentityIsNew
	public static Func<ulong, bool, bool> ClusterInitialPlayerIdentityIsNew;
	public static ClusterTryGetPlayerIdDelegate ClusterTryGetPlayerId;
	public static ClusterTryGetIdentityIdDelegate ClusterTryGetIdentityId;
	public static ClusterLoadPlayerDelegate ClusterLoadPlayer;
	public static ClusterCreatePlayerDelegate ClusterCreatePlayer;
	public static Func<MyPlayerCollection.RespawnMsg, bool> ClusterRespawnRequestPrefix;

	/// <summary>
	/// Transpiler helper (MyPlayerCollectionPatch.BeforeNewPlayerRequest): runs the seam after vanilla's
	/// sender-spoof guard. A refusal sends vanilla <c>OnNewPlayerFailure</c> to the original requester the same
	/// way vanilla does for a cancelled request, and the caller returns; true continues the original path.
	/// </summary>
	public static bool BeforeNewPlayerRequestOrFail(MyPlayerCollection.NewPlayerRequestParameters parameters)
	{
		if (ClusterBeforeNewPlayerRequest?.Invoke(parameters) != false) return true;
		if (MyEventContext.Current.IsLocallyInvoked)
			MyPlayerCollection.OnNewPlayerFailure(parameters.ClientSteamId, parameters.PlayerSerialId);
		else
			MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyPlayerCollection.OnNewPlayerFailure,
				parameters.ClientSteamId, parameters.PlayerSerialId, MyEventContext.Current.Sender);
		return false;
	}

	/// <summary>
	/// Transpiler helper (MyPlayerCollectionPatch.InitialPlayerIdentityIsNew): replaces the value vanilla stores
	/// into <c>PlayerDataMsg.NewIdentity</c>. The cluster installs the World Authority's identity on this node
	/// before vanilla's lookup, so vanilla sees an existing identity and reports newIdentity=false. The joining
	/// client has never seen it, and OnInitialPlayerCreated only creates the identity when NewIdentity is true,
	/// so its OnPlayerCreated returns at the TryGetIdentity guard and that client gets no MyPlayer at all.
	/// </summary>
	public static bool InitialPlayerIdentityIsNew(bool vanilla, MyPlayer.PlayerId id)
		=> ClusterInitialPlayerIdentityIsNew?.Invoke(id.SteamId, vanilla) ?? vanilla;

	/// <summary>
	/// Make the controlling-player map agree with the controller, without tearing anything down.
	/// </summary>
	/// <remarks>
	/// GetControllingPlayer reads m_controlledEntities, and that map is maintained by exactly one thing:
	/// the ControlledEntityChanged event. Re-assigning a character a player ALREADY controls fires no
	/// such event - but SetControlledEntityInternal removes the entry before calling TakeControl, so the
	/// re-assignment leaves the map empty. MyClientState.Read then finds no controlling player and drops
	/// every input packet from that client silently, while the character keeps its ControllerInfo, its
	/// physics, and its gravity. The player falls and cannot steer.
	///
	/// A node adopting a handed-over client is precisely the case where the player already holds the
	/// character, so it needs to assert the map rather than reassign control. Idempotent: the same-player
	/// early return makes the repeated calls from ControlReassertService cost a dictionary lookup.
	/// </remarks>
	public static void ClusterEnsureControlledEntity(this MyPlayerCollection self, MyPlayer.PlayerId id, MyEntity entity)
	{
		if (entity == null || !self.m_players.ContainsKey(id)) return;
		if (self.m_controlledEntities.TryGetValue(entity.EntityId, out MyPlayer.PlayerId existing))
		{
			if (existing == id) return;
			self.m_controlledEntities.Remove(entity.EntityId, immediate: true);
		}
		self.m_controlledEntities.Add(entity.EntityId, id, immediate: true);
		// entity.OnClosing -= / += m_entityClosingHandler (idempotent re-subscribe); see PublicizedEvents.
		PublicizedEvents.RemoveOnClosing(entity, self.m_entityClosingHandler);
		PublicizedEvents.AddOnClosing(entity, self.m_entityClosingHandler);
	}

	/// <summary>
	/// Attach control server-side - ChangeCharacter, TakeControl, controlling-player map - without
	/// telling any client. For a client adopted mid-transfer the server controller may not hold the
	/// entity yet while the client still does; SetControlledEntity would broadcast
	/// OnControlChangedSuccess at that client, whose handler removes its own map entry and then
	/// no-ops in TakeControl, leaving it reporting no control forever (Defect 4). This is the
	/// sync:false path vanilla itself uses in ChangeCharacter, exposed for those callers.
	/// </summary>
	public static void ClusterTakeControlNoSync(this MyPlayerCollection self, MyPlayer.PlayerId id, MyEntity entity)
	{
		if (entity == null || !self.m_players.ContainsKey(id)) return;
		self.SetControlledEntityInternal(id, entity, sync: false);
	}

	/// <summary>
	/// Drop the server's controlling-player mapping for an entity, leaving the controller and the client's
	/// own map untouched. This is exactly the state a switch produces when controller rebinding is skipped,
	/// and it exists so P1-CHR-03 can produce it deliberately: without it the §6.6 control-liveness fallback
	/// could only be tested by waiting for the bug. Test-only caller (ControlReassertService).
	/// </summary>
	public static void ClusterForgetControlledEntity(this MyPlayerCollection self, MyEntity entity)
	{
		if (entity != null) self.m_controlledEntities.Remove(entity.EntityId, immediate: true);
	}

	/// <summary>
	/// Tell the clients about a control assignment the server is already sure of.
	/// Control is two halves that break independently. ClusterEnsureControlledEntity repairs the
	/// server's half; this is the client's. A client that missed its assignment sends no input at all
	/// and, because the handler ends by calling SetCameraController, sits in a camera it cannot switch
	/// out of - the player's report was dead movement and a dead third-person toggle together.
	/// DESTRUCTIVE if the client already holds the entity: the handler removes the client's own map
	/// entry and then TakeControl no-ops, so the client is left reporting that it controls nothing and
	/// every input dies. Never send this on a schedule - see ControlReassertService. The client half IS
	/// observable after all, through MyClientStateBase.IsControllingCharacter/Jetpack/Grid, which the
	/// character census reports as clientControl.
	/// This is the message SetControlledEntityInternal sends at its end, without the removal that
	/// precedes it there, and addressed to the one client that needs it rather than broadcast.
	/// Vanilla broadcasts because it is announcing a change everyone should hear once; this repeats
	/// an assignment nobody else's view is missing, and every other client that received it would
	/// run the same remove-then-no-op on its own map that this whole fix exists to undo.
	/// </summary>
	public static void ClusterNotifyControlledEntity(this MyPlayerCollection self, MyPlayer.PlayerId id, long entityId)
	{
		if (!Sync.IsServer || entityId == 0L) return;
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyPlayerCollection.OnControlChangedSuccess,
			id.SteamId, id.SerialId, entityId, false, new EndpointId(id.SteamId));
	}

	/// <summary>
	/// Re-announces an identity the node already holds. Identities are allocated at the World Authority and
	/// replicated between nodes, but never to clients, so a node can know an identity that the client about to
	/// receive OnPlayerCreated for has never heard of. OnPlayerCreated then returns at its TryGetIdentity guard
	/// and nothing retries, leaving that client with no MyPlayer for the rest of the session. A repeat is
	/// harmless: the client-side handler routes to CreateNewIdentity, and AfterCreateIdentity only adds to
	/// m_allIdentities when the id is absent.
	/// </summary>
	public static void ClusterAnnounceIdentity(this MyPlayerCollection self, long identityId)
	{
		if (!Sync.IsServer || Sync.MyId == 0) return;
		MyIdentity identity = self.TryGetIdentity(identityId);
		if (identity == null) return;
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyPlayerCollection.OnIdentityCreated,
			self.IdentityIsNpc(identityId), identityId, identity.DisplayPlatformName);
	}

	/// <summary>
	/// The same announcement addressed to ONE client (T-0178). The mirror installs a remote
	/// player's identity on the clients attached to this node, and every other client in the
	/// cluster either already holds that identity or is being told by its own node - so a
	/// broadcast would repeat the message once per node for no benefit. Same guards as the
	/// broadcast form: an identity this node does not hold is not announced at all, because the
	/// client-side handler would create one from a name we would be inventing.
	/// </summary>
	public static void ClusterAnnounceIdentity(this MyPlayerCollection self, long identityId, ulong target)
	{
		if (!Sync.IsServer || Sync.MyId == 0 || target == 0) return;
		MyIdentity identity = self.TryGetIdentity(identityId);
		if (identity == null) return;
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyPlayerCollection.OnIdentityCreated,
			self.IdentityIsNpc(identityId), identityId, identity.DisplayPlatformName, new EndpointId(target));
	}
}
