using System;
using System.Collections.Generic;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Network;
using VRage.Game.Entity;

namespace ClusterPlugin;

public static class ReplicationServerPatch
{
	public static Func<long> ClusterAuthorityGeneration;

	public static ClientHandoverState Export(ulong clientId, HashSet<long> entityIds)
	{
		MyReplicationServer replication = Server();
		Endpoint endpoint = Endpoint(clientId);
		ClientHandoverState state = ReplicationClientViewPatch.Export(replication, endpoint, entityIds);
		if (replication.GetClientData(endpoint) is MyClientState clientState)
			foreach ((long planet, HashSet<long> sectors) in clientState.KnownSectors) state.KnownSectors.Add(planet, new HashSet<long>(sectors));
		return state;
	}

	public static ClientHandoverState ExportDetached(ulong clientId) => ReplicationClientViewPatch.ExportDetached(Server(), Endpoint(clientId));
	public static int Prune(ulong clientId, ClientHandoverState state) => ReplicationClientViewPatch.Prune(Server(), Endpoint(clientId), state);
	public static void Resume(ulong clientId) => ReplicationClientViewPatch.Resume(Server(), Endpoint(clientId));
	public static void Finish(ulong clientId) => ReplicationClientViewPatch.Finish(Server(), Endpoint(clientId));
	public static string ExportNetworkIds(HashSet<long> entityIds) => ReplicationNetworkIdPatch.Export(Server(), entityIds);

	/// <summary>Human-readable identity of a network object, for pending-replicable diagnostics.</summary>
	public static string DescribeNetworkObject(uint networkId)
	{
		try
		{
			string kind = ReplicationClientViewPatch.DescribeNetworkObject(Server(), networkId, out long entityId);
			if (entityId == 0) return kind;
			MyEntities.TryGetEntityById(entityId, out MyEntity entity);
			return kind + " entity=" + entityId
				+ (entity == null ? " <no live entity>" : " '" + (entity.DisplayName ?? entity.Name) + "'");
		}
		catch (Exception exception)
		{
			return "<describe failed: " + exception.Message + ">";
		}
	}
	public static void PrepareNetworkIds(string state) => ReplicationNetworkIdPatch.Prepare(Server(), state);
	public static void FinalizeNetworkIds() => ReplicationNetworkIdPatch.Finalize(Server());
	public static void FinalizeNetworkIds(HashSet<long> entityIds) => ReplicationNetworkIdPatch.FinalizeEntities(Server(), entityIds);
	public static void DiscardNetworkIds() => ReplicationNetworkIdPatch.Discard(Server());

	/// <param name="ownBindings">Take the controlled and character bindings the client has here instead of the exported
	/// ones: a client a node takes back after a failed switch (SE-1189).</param>
	public static int Adopt(ulong clientId, ClientHandoverState state, bool ownBindings = false)
	{
		MyReplicationServer replication = Server();
		Endpoint endpoint = Endpoint(clientId);
		MyClientState clientState = replication.GetClientData(endpoint) as MyClientState ?? Activator.CreateInstance(MyPerGameSettings.ClientStateType) as MyClientState;
		if (clientState == null) throw new InvalidOperationException("Cannot create target client state");
		if (clientState.KnownSectors.Count != 0) throw new InvalidOperationException("Target client known-sector view is already populated");
		foreach ((long planet, HashSet<long> sectors) in state.KnownSectors) clientState.KnownSectors.Add(planet, new HashSet<long>(sectors));
		if (ownBindings) ReplicationClientViewPatch.TakeBindings(replication, clientState, state);
		SeedPosition(clientState, state);
		return ReplicationClientViewPatch.Adopt(replication, endpoint, clientState, state);
	}

	/// <summary>
	/// T-0261 (Viktor's blink): a fresh target client state has no Position until the client's first
	/// state packet names its camera entity (MyClientState.ReadShared sets m_positionEntityServer).
	/// Adopt forces an immediate layer rebuild (UpdateTimer = 0), and with no Position
	/// MyClient.CalculateLayerOfReplicable answers null for everything, so RefreshReplicable sent a
	/// DESTROY for every adopted replicable that is not the controlled/character entity, crucial or
	/// permanent - the base grids around a seated player, the pilot's own parked ship - and the next
	/// rebuild after the client's packet re-created them about a second later. Measured 2026-09-27
	/// 06:52-07:32 (client grid_list at 4 Hz across 12 live switches): grids vanished +0.7-1.1 s after
	/// the switch and returned 0.2-2 s later, with the source prune at destroyedRoots=0. Seeding the
	/// entity the client would name (its character: seated, that is the cockpit's position; on foot,
	/// itself) makes the first rebuild keep what is in range; the client's own packet replaces it.
	/// </summary>
	private static void SeedPosition(MyClientState clientState, ClientHandoverState state)
	{
		if (clientState.Position.HasValue) return;
		foreach (long anchor in new[] { state.CharacterEntityId, state.ControlledEntityId })
		{
			if (anchor == 0 || !MyEntities.TryGetEntityById(anchor, out MyEntity entity) || entity.GetTopMostParent().MarkedForClose) continue;
			clientState.m_positionEntityServer = entity;
			return;
		}
	}

	public static void PrepareAdoption(ulong clientId, ClientHandoverState state)
	{
		MyReplicationServer replication = Server();
		Endpoint endpoint = Endpoint(clientId);
		if (replication.GetClientData(endpoint) is not MyClientState clientState) throw new InvalidOperationException("Target replication client is not connected");
		MyNetworkClient networkClient = clientState.GetClient() ?? throw new InvalidOperationException("Target network client is not ready");
		MyCharacter character = null;
		if (state.CharacterEntityId != 0)
		{
			if (!MyEntities.TryGetEntityById(state.CharacterEntityId, out MyEntity entity) || entity is not MyCharacter importedCharacter) throw new InvalidOperationException("Transferred character entity is not ready");
			character = importedCharacter;
		}
		MyPlayer player = clientState.GetPlayer();
		if (player == null && character != null)
		{
			long identityId = character.GetPlayerIdentityId();
			if (identityId == 0) throw new InvalidOperationException("Transferred character has no player identity");
			MyIdentity identity = MySession.Static.Players.TryGetIdentity(identityId) ?? MySession.Static.Players.CreateNewIdentity(networkClient.DisplayName, identityId, null, null);
			if ((ClusterAuthorityGeneration?.Invoke() ?? 0) <= 0)
				player = MySession.Static.Players.CreateNewPlayer(identity, networkClient, networkClient.DisplayName, true);
			else
			{
				if (!MySession.Static.Players.TryGetPlayerId(identityId, out MyPlayer.PlayerId playerId) || playerId.SteamId != clientId
					|| MySession.Static.Players.TryGetIdentityId(playerId.SteamId, playerId.SerialId) != identityId)
					throw new InvalidOperationException("Transferred player has no canonical World Authority binding");
				player = MySession.Static.Players.CreateNewPlayer(identity, playerId, networkClient.DisplayName, true,
					initialPlayer: false, newIdentity: false);
			}
		}
		if (player == null) throw new InvalidOperationException("Target player is not ready");
		if (character != null && player.Identity.Character != character) player.Identity.ChangeCharacter(character);
		ReplicationClientViewPatch.ValidateBindings(replication, clientState, state);
	}

	public static int AddEntities(ulong clientId, IEnumerable<long> entityIds) => ReplicationClientViewPatch.AddEntities(Server(), Endpoint(clientId), entityIds);

	/// <summary>Read-only accessor for a client's replication state, for instrumentation.
	/// Returns null when the client is not connected to this node.</summary>
	public static MyClientState StateFor(ulong clientId) =>
		(MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer)?.GetClientData(Endpoint(clientId)) as MyClientState;

	public static bool HasClient(ulong clientId) =>
		(MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer)?.GetClientData(Endpoint(clientId)) != null;

	/// <summary>False once a switch detached the client's view here (Prune clears IsReady): the client is
	/// still connected to this node, but its view belongs to the node it is switching to.</summary>
	public static bool IsClientReady(ulong clientId) =>
		(MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer)?.m_clientStates.TryGetValue(Endpoint(clientId), out MyClient client) == true
		&& client.IsReady;

	private static MyReplicationServer Server() => MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer ?? throw new InvalidOperationException("No multiplayer server");
	private static Endpoint Endpoint(ulong clientId) => new Endpoint(new EndpointId(clientId), 0);
}
