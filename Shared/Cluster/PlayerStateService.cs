using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using VRage.Network;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// WA-1: player state beyond the identity snapshot - death state, last-death position, and the
/// controlled entity. Each node reports the state of its LOCALLY ATTACHED players once a second - the
/// fresh report IS the liveness signal (see the online-players trap) - and the World Authority treats
/// the hosting node as the only authority for its players: a node that just lost a player to a handover
/// fires vanilla logout bookkeeping (SetDead(true)) that must never overwrite the new host's alive
/// report. Death facts of players no one currently hosts stay sticky at the WA (a disconnected player
/// keeps their last death position), the merged view replicates, and nodes apply the death facts to
/// their identity replicas. The <see cref="MyIdentitySeams.ClusterStateChanged"/> seam only accelerates the
/// next report so a death propagates sub-second.
/// </summary>
public static class PlayerStateService
{
	private const string Subsystem = "player-state";
	private const int MaxEntries = 4096;

	/// <summary>Dead-node expiry, mirroring the online-players list: live nodes refresh every second.</summary>
	private const double ReportExpiryMilliseconds = 30000;

	private static readonly object SyncRoot = new object();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	// WA merge state: per-origin live reports plus sticky death facts for unhosted identities.
	private static readonly Dictionary<string, StampedReport> ReportsByOrigin = new(StringComparer.Ordinal);
	private static readonly Dictionary<long, DeathEntry> StickyDeaths = new();

	// Replicated cluster view (parked on the WA after its own publish - it never receives its broadcasts).
	private static Dictionary<long, DeathEntry> m_clusterDeaths = new();
	private static Dictionary<long, long> m_clusterControlled = new();

	private static string m_snapshot = JsonSerializer.Serialize(
		new StateSnapshot(Array.Empty<DeathEntry>(), Array.Empty<ControlledEntry>()));
	private static string m_pending;
	private static string m_applied;
	private static bool m_applying;
	private static double m_nextReport;

	private sealed record StampedReport(double Received, PlayerStateEntry[] Entries);

	public sealed record PlayerStateEntry(long IdentityId, long EntityId, bool Dead, bool HasPosition,
		double X, double Y, double Z);
	public sealed record StateReport(string Origin, PlayerStateEntry[] Entries);
	public sealed record DeathEntry(long IdentityId, bool Dead, bool HasPosition, double X, double Y, double Z);
	public sealed record ControlledEntry(long IdentityId, long EntityId);
	public sealed record StateSnapshot(DeathEntry[] Deaths, ControlledEntry[] Controlled);

	internal static void Install()
	{
		MyIdentitySeams.ClusterStateChanged = OnStateChanged;
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ReceiveSnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ReceiveSnapshot);
	}

	/// <summary>
	/// Death facts change on the game thread of the hosting node; pull the next report forward so they
	/// propagate sub-second instead of waiting out the cadence. The report itself stays the sole
	/// carrier - a non-hosting node's vanilla logout bookkeeping must not speak for the player.
	/// </summary>
	private static void OnStateChanged(MyIdentity identity)
	{
		if (!ClusterRuntime.IsClustered || m_applying) return;
		m_nextReport = 0;
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered) return;
		MySession session = MySession.Static;
		if (session?.Ready != true) return;
		if (ClusterRuntime.LocalMilliseconds >= m_nextReport)
		{
			m_nextReport = ClusterRuntime.LocalMilliseconds + 1000;
			// Every second even when unchanged: the fresh report keeps this origin alive in the WA
			// merge - the expiry drops DEAD nodes, not stable ones.
			string payload = JsonSerializer.Serialize(new StateReport(Node, CaptureLocal(session)));
			if (ClusterRuntime.IsWorldAuthority) ReceiveReport(Node, payload);
			else GlobalStateChannel.SendRequest(Subsystem, payload);
		}
		ApplyPending(session);
	}

	/// <summary>The silent input gate's last decision for this client on this node: how many frames
	/// since a client-state packet was read at all, and which gate branch the controls last took
	/// ("applied", or the name of the branch that discarded them). The census's clientControl can
	/// read healthy-but-STALE precisely because every discard branch skips the flag update.</summary>
	private static string ClientGate(MySession session, ulong steamId)
	{
		if (steamId == 0) return "unknown";
		MyClientStateBase state = (MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer)
			?.GetClientData(new Endpoint(new EndpointId(steamId), 0));
		if (state is not Sandbox.Engine.Multiplayer.MyClientState clientState) return "unknown";
		long readAge = clientState.ClusterLastReadFrame < 0 ? -1L
			: session.GameplayFrameCounter - clientState.ClusterLastReadFrame;
		return "readAge=" + readAge + " gate=" + clientState.ClusterLastGate;
	}

	/// <summary>What the client itself reports it is controlling, or "unknown" if it has no client state.</summary>
	private static string ClientControl(ulong steamId)
	{
		if (steamId == 0) return "unknown";
		MyClientStateBase state = (MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer)
			?.GetClientData(new Endpoint(new EndpointId(steamId), 0));
		if (state == null) return "unknown";
		if (state.IsControllingJetpack) return "jetpack";
		if (state.IsControllingCharacter) return "character";
		if (state.IsControllingGrid) return "grid";
		return "none";
	}

	/// <summary>
	/// Every character entity this node holds, with the player and identity that own it. A body no
	/// player controls and no identity claims is a leftover: it still saves, loads as an orphan, and
	/// the rejoining player spawns a second one.
	/// </summary>
	public static object Characters()
	{
		MySession session = MySession.Static;
		if (session == null) return new { node = Node, characters = Array.Empty<object>() };
		Dictionary<long, long> characterToIdentity = session.Players.GetAllIdentities()
			.Where(identity => identity.Character != null)
			.GroupBy(identity => identity.Character.EntityId)
			.ToDictionary(group => group.Key, group => group.First().IdentityId);
		// Which identity OWNS each body, as opposed to which identity is currently wearing it. An offline
		// player's identity often has no Character at all, so identityOwner reads 0 for a body that is the
		// only one they have - mistaking that for an orphan would delete the player's character. The saved
		// set is the durable link, and MyCharacterPatch is what restores it on load.
		Dictionary<long, long> savedToIdentity = new Dictionary<long, long>();
		foreach (MyIdentity identity in session.Players.GetAllIdentities())
			foreach (long saved in identity.SavedCharacters)
				savedToIdentity[saved] = identity.IdentityId;
		Dictionary<long, long> controlledToIdentity = session.Players.GetOnlinePlayers()
			.Where(player => player.Identity != null && player.Controller?.ControlledEntity?.Entity != null)
			.GroupBy(player => player.Controller.ControlledEntity.Entity.EntityId)
			.ToDictionary(group => group.Key, group => group.First().Identity.IdentityId);
		object[] characters = MyEntities.GetEntities().OfType<MyCharacter>()
			.Select(character => (object)new
			{
				entityId = character.EntityId,
				name = character.DisplayName,
				dead = character.IsDead,
				identityOwner = characterToIdentity.GetValueOrDefault(character.EntityId),
				savedFor = savedToIdentity.GetValueOrDefault(character.EntityId),
				controllingIdentity = controlledToIdentity.GetValueOrDefault(character.EntityId),
				controllerType = character.ControllerInfo?.Controller?.GetType().Name,
				usedBy = character.ControllerInfo?.ControllingIdentityId ?? 0,
				position = new[] { character.PositionComp.GetPosition().X, character.PositionComp.GetPosition().Y,
					character.PositionComp.GetPosition().Z },
				// A character that can be teleported but cannot walk has lost its Havok character
				// controller, which is invisible from every other endpoint - the entity is present, in
				// the right partition, at the right position, and simply not self-propelled.
				physicsEnabled = character.Physics?.Enabled ?? false,
				hasProxy = character.Physics?.CharacterProxy != null,
				inWorld = character.Physics?.IsInWorld ?? false,
				frozen = ClusterRuntime.IsFrozen(character.EntityId),
				// The three above were all healthy on a character that could not move a millimetre, so
				// they do not discriminate. These two do: a proxy can be present and still be absent from
				// the Havok world, and an entity can be un-frozen and still have had its update
				// registration restored as NONE - the freeze zeroes NeedsUpdate and the restore puts back
				// whatever it captured, which is NONE if it captured an already-frozen entity.
				// IsActive rather than the proxy's rigid body directly: ClusterPlugin does not reference
				// HavokWrapper, and for a character MyPhysicsBody.IsActive already reads through to the
				// proxy's own body.
				physicsActive = character.Physics?.IsActive ?? false,
				// The one that matters: UpdateCharacterPhysics builds a dynamic body on a server and a
				// static mass-0 network proxy otherwise, and a static character is exactly "rotates,
				// teleports, will not walk". Vanilla treats IsStatic as grounds to rebuild.
				isStatic = character.Physics?.IsStatic ?? false,
				mass = character.Physics?.Mass ?? 0f,
				speed = character.Physics?.LinearVelocity.Length() ?? 0f,
				// Do not draw conclusions from this one. It read [0,0,0] throughout a confirmed 38 m move
				// at 18 m/s: it is whatever the last MoveAndRotate happened to set, sampled from an HTTP
				// thread between frames, and a client that is moving perfectly reads zero here most of the
				// time. Kept only because a NON-zero reading is still evidence that input arrived.
				moveIndicator = new[] { character.MoveIndicator.X, character.MoveIndicator.Y, character.MoveIndicator.Z },
				// For the second kind of stuck character, which has none of the tells above: alive,
				// controlled by both halves, physics enabled and active, unfrozen, on a node simulating at
				// full speed, and inert to a millimetre through a client reconnect.
				movementState = character.GetCurrentMovementState().ToString(),
				jetpackOn = character.JetpackComp?.TurnedOn ?? false,
				dampeners = character.JetpackComp?.DampenersTurnedOn ?? false,
				// MyRemoteControl.CanControl walks the antenna graph to the character, so a suit radio
				// that is off makes every remote take fail SILENTLY server-side (UseFailureCallback,
				// no log line anywhere). §5.5 settles a fresh spawn's radio off; this is the effective
				// broadcast state the antenna system actually registers.
				radioOn = character.RadioBroadcaster?.Enabled ?? false,
				// Set from the object builder in Init and only connected to a real entity by an update -
				// the same deferred-restoration shape as the eight round-trip defects, and FrozenUpdatePatch
				// defers exactly those updates. A character dampening relative to the wrong entity is a
				// character whose velocity is being computed against something that is not there.
				relativeDampeningEntity = character.RelativeDampeningEntity?.EntityId ?? 0L,
				usingLadder = character.IsOnLadder,
				// MyClientState.Read gates every scrap of client input on exactly this, and drops the input
				// silently when it does not hold - no log, no error, the stream is just skipped. A character
				// with a controller but no CONTROLLING PLAYER, or one whose player carries a different steam
				// id than the packet's sender, is a character whose owner cannot move it while gravity and
				// collisions still work on it perfectly.
				controllingSteamId = session.Players.GetControllingPlayer(character)?.Client?.SteamUserId ?? 0,
				// The other half, which we assumed all along was invisible from here: what the CLIENT
				// says it controls, reported in every client-state packet and kept in MyClientStateBase.
				// "none" on a player the server maps to this character is the silent-input state - the
				// client walks its own m_controlledEntities to decide what to send, and a client that
				// lost that entry writes "I control nothing" while every server-side field stays perfect.
				// A player in spectator also reports "none" - vanilla stops reporting control while the
				// spectator camera is active - so this discriminates a stuck client only for a player who
				// is not spectating.
				clientControl = ClientControl(session.Players.GetControllingPlayer(character)?.Client?.SteamUserId ?? 0),
				clientGate = ClientGate(session, session.Players.GetControllingPlayer(character)?.Client?.SteamUserId ?? 0),
				needsUpdate = character.NeedsUpdate.ToString(),
				// needsUpdate reads the REGISTRATION flags; this measures EXECUTION. A character whose
				// UpdateBeforeSimulation stopped running never applies jetpack thrust, so its velocity
				// stays exactly zero while every other field reads healthy - which no field above can see.
				framesSinceUpdate = character.ClusterLastUpdateFrame < 0 ? -1L
					: (long)(session.GameplayFrameCounter - character.ClusterLastUpdateFrame),
				// The input half of the same question: the last frame nonzero movement input reached
				// MoveAndRotate. -1 = never. moveIndicator sampled between frames cannot answer this.
				framesSinceInput = character.ClusterLastInputFrame < 0 ? -1L
					: (long)(session.GameplayFrameCounter - character.ClusterLastInputFrame),
				// One layer below: the last controls payload received from the client for this
				// character, and the move it carried. Receipt with nonzero move but no MoveAndRotate
				// is a server-side application defect; zero move or no receipt is client-side.
				framesSinceControls = character.ClusterLastControlsFrame < 0 ? -1L
					: (long)(session.GameplayFrameCounter - character.ClusterLastControlsFrame),
				lastControlsMove = new[] { character.ClusterLastControlsMove.X, character.ClusterLastControlsMove.Y, character.ClusterLastControlsMove.Z },
				// What the jetpack actually produced this frame; input arriving with zero thrust
				// pins the defect in the thrust pipeline rather than input delivery.
				jetpackThrust = character.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>()?.FinalThrust.Length() ?? -1f,
				// Which Havok cluster the body sits in and what that cluster's world actually simulates -
				// inWorld above is a plain flag and says nothing about the world it names.
				havok = MyPhysicsSeams.DescribeBodyWorld(character),
			})
			.Take(MaxEntries)
			.ToArray();
		return new { node = Node, characters };
	}

	/// <summary>
	/// Closes a character body that no identity claims. A rejoining player whose old body is on another
	/// node gets a fresh one - MySpaceRespawnComponent.TryFindExistingCharacter walks SavedCharacters and
	/// resolves through MyEntities.GetEntityById, both node-local - and the abandoned body keeps its owner's
	/// name and shows up as a GPS marker in a partition the player was never in.
	///
	/// Every refusal below is deliberate: the caller passes entity ids gathered from a report that may
	/// already be stale, so this decides for itself whether a body is claimed rather than trusting that it
	/// was told the truth. A body that is any identity's current one, is controlled, or is mid-handover
	/// stays, and the reason is returned instead.
	/// </summary>
	public static object CloseUnclaimedCharacter(long entityId, bool force = false)
	{
		MySession session = MySession.Static;
		if (session == null) return new { closed = false, entityId, reason = "no session" };
		if (FindCharacter(entityId) is not { } character)
			return new { closed = false, entityId, reason = "not a character on this node" };
		MyIdentity owner = session.Players.GetAllIdentities()
			.FirstOrDefault(identity => identity.Character?.EntityId == entityId);
		// Being an identity's current body is the right default refusal - it is how an offline player's
		// only body looks, and deleting it destroys their inventory. force is for the case where that is
		// the point: clearing a player's bodies deliberately so their next join starts from the respawn
		// screen. The checks below are NOT bypassed, so a body someone is actually holding is still safe.
		if (owner != null && !force)
			return new { closed = false, entityId, reason = $"is identity {owner.IdentityId}'s current body" };
		if ((character.ControllerInfo?.ControllingIdentityId ?? 0) != 0)
			return new { closed = false, entityId, reason = "is being controlled" };
		if (session.Players.GetOnlinePlayers().Any(player =>
				player.Controller?.ControlledEntity?.Entity?.EntityId == entityId
				|| player.Character?.EntityId == entityId))
			return new { closed = false, entityId, reason = "belongs to an online player" };
		if (ClusterRuntime.IsFrozen(character.GetTopMostParent()?.EntityId ?? entityId))
			return new { closed = false, entityId, reason = "frozen for a handover in progress" };
		string name = character.DisplayName;
		// T-0237: a seated body is closed like a standing one (a seat is where a body is, not what it is), but out of
		// its seat first: closing a cockpit's pilot in place leaves the cockpit pointing at a closed character.
		long unseatedFrom = 0;
		if (character.Parent is MyCockpit seat && seat.Pilot == character)
		{
			seat.RemovePilot();
			unseatedFrom = seat.EntityId;
		}
		long detachedFrom = 0;
		if (owner != null)
		{
			// Leave the identity with no body rather than a reference to a closed one.
			owner.ChangeCharacter(null);
			detachedFrom = owner.IdentityId;
		}
		character.Close();
		return new { closed = true, entityId, name, detachedFrom, unseatedFrom };
	}

	/// <summary>A character by entity id, standing or seated. A seated body is out of MyEntities' scene set
	/// (MyCockpit.AttachPilot removes it), so a miss by id also walks the cockpits.</summary>
	internal static MyCharacter FindCharacter(long entityId)
	{
		if (MyEntities.TryGetEntityById(entityId, out MyEntity entity) && entity is MyCharacter known) return known;
		return MyEntities.GetEntities().OfType<MyCubeGrid>().SelectMany(grid => grid.GetFatBlocks<MyCockpit>())
			.Select(cockpit => cockpit.Pilot).FirstOrDefault(pilot => pilot != null && pilot.EntityId == entityId);
	}

	private static PlayerStateEntry[] CaptureLocal(MySession session)
		=> session.Players.GetOnlinePlayers()
			.Where(player => player.IsRealPlayer && player.Identity != null)
			.Select(player => new PlayerStateEntry(player.Identity.IdentityId,
				player.Controller?.ControlledEntity?.Entity?.EntityId ?? 0,
				EffectiveDead(player.Identity),
				player.Identity.LastDeathPosition.HasValue,
				player.Identity.LastDeathPosition?.X ?? 0,
				player.Identity.LastDeathPosition?.Y ?? 0,
				player.Identity.LastDeathPosition?.Z ?? 0))
			.OrderBy(entry => entry.IdentityId)
			.Take(MaxEntries)
			.ToArray();

	/// <summary>
	/// The effective death state. Vanilla identities START dead (Init), go alive when a character
	/// attaches, and only SetDead again at respawn or logout - at character-death time only the
	/// CHARACTER's IsDead flips. Where a character exists it is the truth; identity replicas on other
	/// nodes have no character and carry the applied flag instead.
	/// </summary>
	private static bool EffectiveDead(MyIdentity identity) => identity.Character?.IsDead ?? identity.IsDead;

	private static void ReceiveReport(string origin, string payload)
	{
		StateReport report = Read(payload);
		string snapshot;
		lock (SyncRoot)
		{
			double now = ClusterRuntime.LocalMilliseconds;
			ReportsByOrigin[origin] = new StampedReport(now, report.Entries);
			foreach (string stale in ReportsByOrigin
				.Where(entry => now - entry.Value.Received >= ReportExpiryMilliseconds)
				.Select(entry => entry.Key).ToArray())
			{
				ReportsByOrigin.Remove(stale);
				Console.WriteLine("[ClusterPlugin] player state report expired origin=" + stale);
			}
			// The hosting node is the authority: fold every live entry into the sticky death map, so
			// facts survive the player leaving (disconnect keeps the last death position).
			var controlled = new List<ControlledEntry>();
			foreach (StampedReport live in ReportsByOrigin.OrderBy(entry => entry.Key, StringComparer.Ordinal)
				.Select(entry => entry.Value))
				foreach (PlayerStateEntry entry in live.Entries)
				{
					StickyDeaths[entry.IdentityId] = new DeathEntry(entry.IdentityId, entry.Dead,
						entry.HasPosition, entry.X, entry.Y, entry.Z);
					controlled.Add(new ControlledEntry(entry.IdentityId, entry.EntityId));
				}
			string merged = JsonSerializer.Serialize(new StateSnapshot(
				StickyDeaths.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray(),
				controlled.OrderBy(entry => entry.IdentityId).ToArray()));
			if (merged == m_snapshot) return;
			snapshot = m_snapshot = merged;
			m_pending = merged;
		}
		GlobalStateChannel.Publish(Subsystem, snapshot);
	}

	private static void ReceiveSnapshot(string payload)
	{
		if (payload == null || payload.Length > 8 * 1024 * 1024)
			throw new InvalidOperationException("Invalid player state snapshot");
		lock (SyncRoot) m_pending = payload;
	}

	/// <summary>
	/// Applies the latest replicated snapshot on the game thread. Identities of players attached to THIS
	/// node are skipped - the local node originates their facts, and a lagging snapshot must not briefly
	/// resurrect a player who just died here.
	/// </summary>
	private static void ApplyPending(MySession session)
	{
		string payload;
		lock (SyncRoot)
		{
			payload = m_pending;
			if (payload == null) return;
			if (string.Equals(payload, m_applied, StringComparison.Ordinal))
			{
				m_pending = null;
				return;
			}
		}
		StateSnapshot snapshot = null;
		try { snapshot = JsonSerializer.Deserialize<StateSnapshot>(payload); }
		catch (Exception e) { Console.WriteLine("[ClusterPlugin] player state snapshot rejected: " + e.Message); }
		if (snapshot?.Deaths == null || snapshot.Controlled == null
			|| snapshot.Deaths.Length > MaxEntries || snapshot.Controlled.Length > MaxEntries) snapshot = null;
		if (snapshot != null)
		{
			lock (SyncRoot)
			{
				m_clusterDeaths = snapshot.Deaths.ToDictionary(entry => entry.IdentityId);
				m_clusterControlled = new Dictionary<long, long>();
				foreach (ControlledEntry entry in snapshot.Controlled)
					m_clusterControlled[entry.IdentityId] = entry.EntityId;
			}
			m_applying = true;
			try
			{
				foreach (DeathEntry entry in snapshot.Deaths)
				{
					MyIdentity identity = session.Players.TryGetIdentity(entry.IdentityId);
					if (identity == null) continue;
					if (session.Players.TryGetPlayerId(entry.IdentityId, out MyPlayer.PlayerId playerId)
						&& session.Players.GetPlayerById(playerId) != null) continue;
					Vector3D? position = entry.HasPosition ? new Vector3D(entry.X, entry.Y, entry.Z) : null;
					if (identity.IsDead != entry.Dead || !Nullable.Equals(identity.LastDeathPosition, position))
						identity.ClusterApplyPlayerState(entry.Dead, position);
				}
			}
			finally { m_applying = false; }
		}
		lock (SyncRoot)
		{
			m_applied = payload;
			if (string.Equals(m_pending, payload, StringComparison.Ordinal)) m_pending = null;
		}
	}

	public static object Status(long identityId)
	{
		MySession session = MySession.Static;
		MyIdentity identity = session?.Players.TryGetIdentity(identityId);
		bool attached = false;
		long localControlled = 0;
		if (session != null && session.Players.TryGetPlayerId(identityId, out MyPlayer.PlayerId playerId))
		{
			MyPlayer player = session.Players.GetPlayerById(playerId);
			if (player != null)
			{
				attached = true;
				localControlled = player.Controller?.ControlledEntity?.Entity?.EntityId ?? 0;
			}
		}
		DeathEntry death;
		long controlled;
		lock (SyncRoot)
		{
			m_clusterDeaths.TryGetValue(identityId, out death);
			m_clusterControlled.TryGetValue(identityId, out controlled);
		}
		return new
		{
			node = Node,
			identityId,
			exists = identity != null,
			attached,
			dead = identity != null && EffectiveDead(identity),
			deathPosition = identity?.LastDeathPosition is { } local ? new[] { local.X, local.Y, local.Z } : null,
			controlledEntity = localControlled,
			cluster = new
			{
				known = death != null,
				dead = death?.Dead ?? false,
				deathPosition = death is { HasPosition: true } ? new[] { death.X, death.Y, death.Z } : null,
				controlledEntity = controlled
			}
		};
	}

	private static StateReport Read(string payload)
	{
		if (payload == null || payload.Length > 8 * 1024 * 1024)
			throw new InvalidOperationException("Invalid player state report");
		StateReport report = JsonSerializer.Deserialize<StateReport>(payload)
			?? throw new InvalidOperationException("Invalid player state report");
		if (string.IsNullOrEmpty(report.Origin) || report.Origin.Length > 128 || report.Entries == null
			|| report.Entries.Length > MaxEntries || report.Entries.Any(entry => entry == null))
			throw new InvalidOperationException("Invalid player state report");
		return report;
	}

	private static string Snapshot() { lock (SyncRoot) return m_snapshot; }
}
