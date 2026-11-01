using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.GameServices;
using VRage.Network;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// T-0178: make the vanilla F3 player list show every player in the CLUSTER, not just the ones
/// attached to this node, with no client plugin and no protocol change.
///
/// <see cref="OnlinePlayersService"/> already knows the cluster-wide list. What the F3 screen needs
/// on top of that is, per row, the two things a node only ever has for its OWN clients:
/// a member entry (<c>MyMultiplayerClient.m_memberData</c>, without which
/// <c>MyGuiScreenPlayers.AddPlayer</c> returns immediately) and a <c>MyPlayer</c> in
/// <c>Sync.Players</c>. Both are installed by messages the server already sends for real logins,
/// so this service sends the same messages for players attached elsewhere. The result on the
/// client is a "phantom" <c>MyPlayer</c> with no character and no controlled entity, which every
/// vanilla iterator tolerates (see the ticket's survey).
///
/// It is entirely server-side and touches nothing on this node: the phantom is never added to this
/// node's own <c>m_members</c>, <c>Sync.Clients</c> or <c>m_players</c>. The events are addressed
/// to one client at a time rather than broadcast, so nothing reaches a node that would have to
/// undo it.
///
/// PAIRED WITH T-0179. Once every node shows every cluster player, a node traversal stops changing
/// any member list at all: the player was already mirrored on the target and stays mirrored on the
/// source, and only the reporting node in the merged list changes. That is also why
/// <see cref="TraversalAnnouncementPatch"/>'s leave half is safe to ship - this service is what
/// removes a departing player from every node's clients cluster-wide.
///
/// WHY RECONCILIATION RATHER THAN THE DELTA EVENTS. <see cref="OnlinePlayersService"/> raises
/// PlayerEnteredCluster/PlayerLeftCluster, and an earlier draft consumed them. Reconciling the
/// whole set once a second is better here for three reasons, and costs a couple of set operations
/// over a few dozen players:
///   - A client that attaches to this node needs the full set, not the next delta. It arrives with
///     the checkpoint's members (<c>MyMultiplayerServerBase.OnWorldRequest</c>), which lists only
///     this node's - so every new client is a full re-emit anyway.
///   - The deltas are raised on the global-state-channel thread, and installing members and
///     players is game-thread work. A queue would have to be drained on the update tick regardless.
///   - It is self-healing. A dropped event, a node that went DEAD and expired out of the merge, a
///     client that was not yet connected at the replication layer when its delta arrived - all of
///     them correct themselves on the next tick instead of leaving a row that is wrong until
///     relog. Under a rotation this cluster reshuffles players constantly, and "eventually right"
///     beats "right if no message was ever missed".
/// </summary>
public static class RemotePlayerMirrorService
{
	/// <summary>Reconcile cadence. The player list changes on a human timescale; 1 s is already generous.</summary>
	private const double IntervalMilliseconds = 1000;

	/// <summary>
	/// Most installs this tick may perform. The work is O(local clients x remote players): on a
	/// 5-node cluster carrying 120 pilots a node holds ~24 clients and mirrors ~96 players, so the
	/// FIRST reconcile after a start would otherwise send 24 x 96 = 2304 installs - three events
	/// each, near 7000 in a single update tick, at exactly the moment the whole fleet is connecting
	/// and the node has the least headroom. Steady state is nowhere near this: it is a set compare
	/// per pair and only a CHANGE sends anything. The cap therefore costs nothing once seeded and
	/// turns the one dangerous burst into a few quiet seconds of catch-up, because an unfinished
	/// reconcile simply resumes on the next tick.
	/// Removals are deliberately NOT capped: leaving a departed player on a client is a wrong
	/// player list, while a late arrival is only an incomplete one.
	/// </summary>
	private const int MaxInstallsPerTick = 200;

	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	/// <summary>
	/// Off switch, read once at startup. This service sends member and player events to every
	/// attached client, so its first live run reaches the whole fleet at once and there is no way
	/// to tell from a bench of two clients how 76 will behave. Setting CLUSTER_PLAYER_MIRROR=0 and
	/// restarting the nodes puts the cluster back to vanilla behaviour without a rebuild or a
	/// rollback - the phantoms are per-session and go with the client's session.
	/// DEFAULT ON: a flag that has to be found and set in an incident is not a kill switch.
	/// </summary>
	private static readonly bool Enabled =
		(Environment.GetEnvironmentVariable("CLUSTER_PLAYER_MIRROR") ?? "1") != "0";

	/// <summary>
	/// What this node has ALREADY BROADCAST an install for, by the same (SteamId, SerialId) the
	/// merged list is keyed by. One set for the node, not one per client, because the events are
	/// broadcasts: see <see cref="Add"/>. Purely local bookkeeping - a wrong entry costs one
	/// redundant message on the next tick.
	/// </summary>
	private static readonly HashSet<(ulong SteamId, int SerialId)> Installed = new();

	/// <summary>
	/// The clients that were attached when those broadcasts went out. A client that attaches later
	/// missed every one of them, so its arrival empties <see cref="Installed"/> and the next pass
	/// re-emits the whole set. Re-emitting is safe: the client-side handler
	/// (MyPlayerCollection.CreateNewPlayerInternal) returns the existing player rather than making
	/// a second one, so the clients that already had the row see no change.
	/// </summary>
	private static readonly HashSet<ulong> Served = new();

	/// <summary>
	/// PER CLIENT: the members each attached client already holds, by the key the merged list uses -
	/// either because this node sent it the member message, or because it holds the member from
	/// elsewhere (vanilla, for players hosted here; its previous node, for a client that traversed in).
	///
	/// WHY PER CLIENT (w33 2026-09-26 00:2x, Viktor's F3 showed 620 players for 124 - every pilot five
	/// times). The member message is not idempotent on the client the way the player events are:
	/// MyMultiplayerClient.OnConnectedClient raises ClientJoined for every one it receives, and an open
	/// MyGuiScreenPlayers adds a table row per ClientJoined without looking for an existing one (its
	/// header count IS the row count). This service used to answer ANY new client by re-sending the
	/// whole set to EVERY client here, so each arrival - every pilot reconnect and every traversal into
	/// this node - added one more row per remote player to every open F3 screen on the node.
	/// </summary>
	private static readonly Dictionary<ulong, HashSet<(ulong SteamId, int SerialId)>> MemberHeld = new();

	/// <summary>
	/// Clients that attached by HANDOVER (a traversal), noted by TraversalAnnouncementPatch when this
	/// node announces the arrival - the transfer marker it reads is gone once the handover commits,
	/// usually before the next reconcile. Such a client already holds every cluster member from its
	/// previous node, so it is seeded as holding them all and sent nothing. Local-milliseconds stamp;
	/// an unconsumed note expires after a minute.
	/// </summary>
	private static readonly ConcurrentDictionary<ulong, double> HandoverArrivals = new();

	/// <summary>
	/// Fresh clients still owed their own member message (SE-736), sent by <see cref="AnnounceSelf"/>
	/// once the client's replication state is ready, see there.
	/// </summary>
	private static readonly HashSet<ulong> AnnounceDue = new();

	/// <summary>Called when this node announces a client that arrived by handover (T-0179 join half).</summary>
	internal static void NoteHandoverArrival(ulong client) => HandoverArrivals[client] = ClusterRuntime.LocalMilliseconds;

	/// <summary>
	/// When each pending withdrawal was first seen missing from the merged list. A traversal
	/// removes the player from one node's report and adds it to another's with no ordering
	/// guarantee, so the merged list shows a sub-second absence that is not a departure; holding
	/// the withdrawal lets the next report cancel it.
	/// </summary>
	private static readonly Dictionary<(ulong SteamId, int SerialId), double> Pending = new();

	/// <summary>
	/// How long a player must stay absent from the merged list before its phantom is withdrawn.
	/// CLUSTER_MIRROR_GRACE, seconds, default 5 - the same default and the same reasoning as
	/// <c>ClusterPresenceAnnouncer.GraceSeconds</c>: a real logout means re-loading the world, tens
	/// of seconds, never the sub-second gap a traversal leaves.
	/// </summary>
	private static readonly double GraceSeconds = ParseGrace();

	private static double ParseGrace()
	{
		string raw = Environment.GetEnvironmentVariable("CLUSTER_MIRROR_GRACE");
		return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			&& value >= 0 ? value : 5.0;
	}

	/// <summary>True once <see cref="GraceSeconds"/> has passed since this key first went missing.</summary>
	private static bool Expired((ulong, int) key)
	{
		double now = ClusterRuntime.LocalMilliseconds;
		if (!Pending.TryGetValue(key, out double since))
		{
			Pending[key] = now;
			return GraceSeconds <= 0;
		}
		return now - since >= GraceSeconds * 1000.0;
	}

	private static double m_next;

	/// <summary>
	/// Completed reconcile passes. Exposed so a test can ORDER A READ AFTER A PASS instead of
	/// sleeping: an absence read a few hundred milliseconds early is indistinguishable from the
	/// rule working, and that is not a hypothetical - the T-0204 falsifier build, with the local
	/// player rule deleted, passed its own check in 3 runs out of 4 for exactly this reason.
	/// </summary>
	private static long m_passes;
	private static long m_memberMessages, m_seededByHandover, m_selfAnnounced;

	public static void Update()
	{
		if (!Enabled || !ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority
			|| MySession.Static?.Ready != true || ClusterRuntime.LocalMilliseconds < m_next) return;
		m_next = ClusterRuntime.LocalMilliseconds + IntervalMilliseconds;
		try
		{
			Reconcile();
		}
		catch (Exception exception)
		{
			// A mirror that throws must not take the update tick with it: every row it draws is
			// cosmetic, and the next tick reconciles from scratch anyway.
			Console.WriteLine("[ClusterPlugin] remote player mirror failed: " + exception);
		}
	}

	private static void Reconcile()
	{
		MyReplicationServer replication = MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer;
		if (replication == null) return;

		// One snapshot for the whole pass: Cluster takes a lock per read, and the arrival and
		// departure sides must be decided against the SAME list or they can disagree.
		OnlinePlayersService.OnlinePlayerEntry[] cluster = OnlinePlayersService.Cluster;

		// Players hosted by some OTHER node. A player attached here already has the real member and
		// MyPlayer that vanilla made, and mirroring it would fight with them.
		// EVERY CLIENT ATTACHED HERE, not just the ones with replication data. A withdrawal is a
		// BROADCAST, so it reaches this whole set whatever endpoint it names, and a client that has
		// not got replication data yet is still attached and still listening.
		HashSet<ulong> attached = AttachedClients();

		// T-0378: an entry of ANOTHER node that names a client attached HERE is that node's stale report (a dead
		// node's entry lives up to the 30 s report expiry, a traversal's about a second). Mirroring it sent the
		// client an OnPlayerCreated for ITSELF with a remote player's defaults, and vanilla then removed its own id
		// from CreativeTools (and overwrote its promote level): creative tools off on the client until a relog.
		var desired = new Dictionary<(ulong, int), OnlinePlayersService.OnlinePlayerEntry>();
		foreach (OnlinePlayersService.OnlinePlayerEntry entry in cluster)
			if (!string.Equals(entry.Node, Node, StringComparison.Ordinal) && !attached.Contains(entry.SteamId))
				desired[(entry.SteamId, entry.SerialId)] = entry;

		// Players the merged list says are hosted HERE. These are the ones a withdrawal must never
		// touch, and they are exactly the ones a traversal INTO this node produces.
		var local = new HashSet<(ulong, int)>();
		foreach (OnlinePlayersService.OnlinePlayerEntry entry in cluster)
			if (string.Equals(entry.Node, Node, StringComparison.Ordinal))
				local.Add((entry.SteamId, entry.SerialId));

		ulong[] targets = Targets(replication);

		// A FRESH CLIENT (a login, not a traversal) MISSED EVERY BROADCAST. Re-emit the player events
		// for it - the clients that already have those players ignore them - but send MEMBER messages
		// only to the clients that lack them (see MemberHeld). A client that traversed in already holds
		// every member and player from its previous node, so it is seeded as holding them all.
		double now = ClusterRuntime.LocalMilliseconds;
		foreach (ulong gone in MemberHeld.Keys.Where(client => Array.IndexOf(targets, client) < 0).ToArray())
		{
			MemberHeld.Remove(gone);
			AnnounceDue.Remove(gone);
		}
		foreach (ulong target in targets)
		{
			if (MemberHeld.ContainsKey(target)) continue;
			if (HandoverArrivals.TryRemove(target, out _))
			{
				MemberHeld[target] = new HashSet<(ulong, int)>(desired.Keys.Concat(Installed));
				m_seededByHandover++;
			}
			else
			{
				MemberHeld[target] = new HashSet<(ulong, int)>();
				Installed.Clear();
				AnnounceDue.Add(target);
			}
		}
		foreach (ulong target in AnnounceDue.ToArray())
			if (replication.m_clientStates.TryGetValue(new Endpoint(new EndpointId(target), 0), out MyClient state) && state.IsReady)
			{
				AnnounceSelf(replication, target);
				AnnounceDue.Remove(target);
			}
		foreach (var note in HandoverArrivals.ToArray())
			if (now - note.Value > 60000) HandoverArrivals.TryRemove(note.Key, out _);
		Served.Clear();
		foreach (ulong target in targets) Served.Add(target);
		if (targets.Length == 0)
		{
			// Nothing attached: nothing holds a phantom, so there is nothing to withdraw and
			// nothing to remember. The next client to attach starts from an empty set.
			Installed.Clear();
			Pending.Clear();
			return;
		}

		// Players hosted here, and clients attached here, are REAL members on every client of this
		// node (vanilla sent them at login, the checkpoint carries them), and a traversal away keeps
		// them there (the leave broadcast is suppressed, T-0179) - so none of them needs our message.
		foreach (HashSet<(ulong, int)> held in MemberHeld.Values)
		{
			foreach ((ulong, int) key in local) held.Add(key);
			foreach (ulong client in attached) held.Add((client, 0));
		}

		int budget = MaxInstallsPerTick;
		var lacking = new List<ulong>();
		foreach (var pair in desired)
		{
			if (budget <= 0) break;          // resume on the next tick; nothing is lost
			lacking.Clear();
			foreach (ulong target in targets)
				if (!MemberHeld[target].Contains(pair.Key)) lacking.Add(target);
			bool playerNeeded = !Installed.Contains(pair.Key);
			if (lacking.Count == 0 && !playerNeeded) continue;
			budget--;
			if (!Add(replication, lacking, playerNeeded, pair.Value)) continue;  // not delivered: next tick
			Installed.Add(pair.Key);
			foreach (ulong target in lacking) MemberHeld[target].Add(pair.Key);
			m_memberMessages += lacking.Count;
		}

		foreach ((ulong, int) key in Installed.Where(key => !desired.ContainsKey(key)).ToArray())
		{
			// ARRIVED HERE: vanilla now owns the real member and MyPlayer under the SAME
			// (SteamId, SerialId) as the phantom, so OnUserKicked cannot tell them apart and
			// would tear down the real one. Drop the bookkeeping and send nothing.
			if (local.Contains(key))
			{
				Installed.Remove(key);
				Pending.Remove(key);
				continue;
			}

			// ATTACHED HERE, WHATEVER THE MERGED LIST SAYS. OnUserKicked and OnPlayerRemoved are
			// [Broadcast] event sites, so the withdrawal reaches the player it is about, and on
			// that client OnUserKicked(userId == Sync.MyId) runs MySessionLoader.UnloadAndExitToMenu
			// - it throws the player out of the game. The `local` test above cannot cover this: it
			// reads the merged list, and the case that matters is exactly the one where the list
			// has NOT caught up with an arrival (T-0204). Drop the bookkeeping and send nothing;
			// the next tick re-installs if the list disagrees.
			if (attached.Contains(key.Item1))
			{
				Installed.Remove(key);
				Pending.Remove(key);
				continue;
			}

			// GONE FROM THE MERGED LIST: hold it, for the same reason T-0179's announcer holds a
			// departure (07be091). A traversal drops the player from the source node's report and
			// adds it to the target's with no ordering guarantee between the two, so the merged
			// list shows a sub-second absence that is not a logout. Measured on this cluster:
			// 6 of 32 traversals produced such a gap.
			if (!Expired(key)) continue;
			Remove(key);
			Installed.Remove(key);
			Pending.Remove(key);
			foreach (HashSet<(ulong, int)> held in MemberHeld.Values) held.Remove(key);
		}

		foreach (var pair in desired)
			Pending.Remove(pair.Key);
		m_passes++;
	}

	/// <summary>
	/// The clients on this node that can actually receive these events: connected at the
	/// replication layer, which is what <c>MyReplicationServer.DispatchEvent</c> checks before it
	/// drops an event on the floor. Asking the replication layer rather than <c>Sync.Clients</c>
	/// keeps a client that is still loading out of the mirrored set instead of recording messages
	/// for it that were never delivered.
	/// </summary>
	/// <summary>
	/// Every client attached to this node, by Steam id - a superset of <see cref="Targets"/>, which
	/// additionally requires replication data. Used only to decide who a BROADCAST would reach.
	/// </summary>
	private static HashSet<ulong> AttachedClients()
	{
		MyClientCollection clients = Sync.Clients;
		var attached = new HashSet<ulong>();
		if (clients == null) return attached;
		foreach (var client in clients.GetClients())
			if (client.SteamUserId != Sync.ServerId && client.SteamUserId != 0)
				attached.Add(client.SteamUserId);
		return attached;
	}

	private static ulong[] Targets(MyReplicationServer replication)
	{
		MyClientCollection clients = Sync.Clients;
		if (clients == null) return Array.Empty<ulong>();
		return clients.GetClients()
			.Select(client => client.SteamUserId)
			.Where(steamId => steamId != Sync.ServerId && steamId != 0
				&& replication.GetClientData(new Endpoint(new EndpointId(steamId), 0)) != null)
			.ToArray();
	}

	/// <summary>
	/// Install one remote player, in the order the clients need it: the member first
	/// (<c>OnPlayerCreated</c> returns at its <c>Sync.Clients.TryGetClient</c> guard without it),
	/// then the identity (it returns at <c>TryGetIdentity</c> without that), then the player.
	/// Returns false when a step could not be taken, so the caller leaves it uninstalled and tries
	/// again rather than recording a row the clients never got.
	///
	/// ONE EMIT FOR THE WHOLE NODE, NOT ONE PER CLIENT (T-0204 finding 1). OnIdentityCreated and
	/// OnPlayerCreated are [Broadcast] sites: MyReplicationServer loops over every client state for
	/// a Broadcast flag and reads the EndpointId only for BroadcastExcept (VRage.dll, dispatch at
	/// the site's HasBroadcastFlag test), so addressing them to one client was decoration - each
	/// one was delivered to all of them anyway. Sending them per target therefore cost N times what
	/// it needed to: 24 clients x 96 remote players is 2304 raises, each fanned out to 24 clients,
	/// near 170k reliable deliveries on a node start, against an install cap sized as though the
	/// delivery were targeted. The member message is NOT an event site - SendClientConnected writes
	/// to one client - so that one still goes round the targets.
	/// </summary>
	private static bool Add(MyReplicationServer replication, List<ulong> memberTargets, bool announcePlayer,
		OnlinePlayersService.OnlinePlayerEntry entry)
	{
		MyIdentity identity = MySession.Static?.Players.TryGetIdentity(entry.IdentityId);
		if (identity == null) return false;   // not replicated here yet; next tick

		var msg = new ConnectedClientDataMsg
		{
			ClientId = new EndpointId(entry.SteamId),
			Name = entry.Name,
			IsAdmin = entry.IsAdmin,
			// The member is installed WITHOUT the "connected" toast. OnConnectedClient stores
			// m_memberData and raises ClientJoined whatever Join says, and toasts only when it is
			// set, so this gets the row and not the notification. A real login is announced by the
			// node the player actually attached to (T-0179 part C), exactly once, cluster-wide.
			Join = false,
			ServiceName = entry.ServiceName
		};
		foreach (ulong target in memberTargets)
			replication.SendClientConnected(ref msg, target);
		if (!announcePlayer) return true;

		MySession.Static.Players.ClusterAnnounceIdentity(entry.IdentityId);
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyPlayerCollection.OnPlayerCreated,
			entry.SteamId, entry.SerialId, Builder(entry, identity));
		return true;
	}

	/// <summary>
	/// SE-736: send a freshly logged-in client its own member message once, so that it takes the
	/// id it receives server packets from as a client. The packet does it, not the content:
	/// <c>MyMultiplayerClient.OnClientConnected</c> calls <c>Sync.ClientConnected(packet.Sender)</c>.
	///
	/// A Steam client through gateway-rs gets every packet from the gateway's GameServer id
	/// (its <c>Sync.ServerId</c>), while the checkpoint's members list this node by its own id.
	/// Without the gateway id in <c>Sync.Clients</c>, <c>MyModAPIHelper.MyMultiplayer.HandleMessage</c>
	/// drops every message for a secure handler (<c>RegisterSecureMessageHandler</c>): the
	/// Impostors Welcome, the Trade Lanes lanes. Vanilla's own member messages of the login arrive
	/// before the client has a session, and <c>Sync.ClientConnected</c> ignores them then. Any
	/// later member message fixes it, which is why the clients got their mod messages only once
	/// another player was online. Direct Transport clients know the node's own id and already have
	/// it; for them this is a no-op. Join stays false, so the client shows no toast for itself.
	///
	/// SE-732: sent only once the client is ready (its ClientReady came, after its session loaded),
	/// never on the first pass that sees it. The client data exists from the join, and a message
	/// sent before the client's world request is lost: the world request answers with a FLUSH, and
	/// <c>MyTransportLayer.HandleMessage</c> drops every packet the client had buffered until then.
	/// </summary>
	private static void AnnounceSelf(MyReplicationServer replication, ulong client)
	{
		MyMultiplayerBase multiplayer = MyMultiplayer.Static;
		var msg = new ConnectedClientDataMsg
		{
			ClientId = new EndpointId(client),
			Name = multiplayer.GetMemberPlatformName(client),
			IsAdmin = MySession.Static.IsUserAdmin(client),
			Join = false,
			ServiceName = multiplayer.GetMemberServiceName(client)
		};
		replication.SendClientConnected(ref msg, client);
		m_selfAnnounced++;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] own member message sent to client=" + client + " (SE-736)");
	}

	/// <summary>
	/// Withdraw one remote player: the player first, then the member, the reverse of
	/// <see cref="Add"/>. Both are [Broadcast] sites, so one emit reaches every client here - which
	/// is why the caller must never withdraw a key belonging to a client attached to this node.
	///
	/// The member goes through <c>OnUserKicked(userId, kicked: true, add: false)</c> rather than
	/// <c>MyControlDisconnectedMsg</c>, which is protected and would need reflection. On the client
	/// it reaches <c>MyMultiplayerClient_ClientLeft</c> through <c>RaiseClientLeft</c>, and that
	/// handler does not look at the reason: it removes the member from <c>m_members</c>, removes
	/// <c>m_memberData</c>, drops the <c>Sync.Clients</c> entry via <c>MySyncLayer.OnClientLeft</c>
	/// and shows the ordinary "disconnected" notification. <c>add: false</c> keeps the id off the
	/// client's kicked list, so nothing about this looks like a moderation action.
	/// </summary>
	private static void Remove((ulong SteamId, int SerialId) key)
	{
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyPlayerCollection.OnPlayerRemoved,
			key.SteamId, key.SerialId);
		if (key.SerialId != 0) return;   // only the base player owns the member entry
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyMultiplayerBase.OnUserKicked,
			key.SteamId, true, false);
	}

	/// <summary>
	/// The minimum a phantom row needs: the identity it belongs to, the name, and the promote level
	/// for the rank stars. The level comes from this node's <c>PromotedUsers</c>, which
	/// <see cref="PromotionService"/> keeps cluster-wide, in preference to the reporting node's
	/// snapshot in the entry - the snapshot is a fallback for an entry travelling on its own.
	/// Everything else is deliberately left at its default: there is no character, no build colours
	/// and no creative state to carry, and inventing them would put values on the client that this
	/// node does not actually know.
	/// </summary>
	private static MyObjectBuilder_Player Builder(OnlinePlayersService.OnlinePlayerEntry entry, MyIdentity identity)
	{
		MyPromoteLevel level = MySession.Static.PromotedUsers.TryGetValue(entry.SteamId, out MyPromoteLevel known)
			? known
			: entry.IsAdmin ? MyPromoteLevel.Moderator : MyPromoteLevel.None;
		return new MyObjectBuilder_Player
		{
			DisplayName = string.IsNullOrEmpty(entry.Name) ? identity.DisplayName : entry.Name,
			IdentityId = entry.IdentityId,
			PromoteLevel = level,
			ForceRealPlayer = true
		};
	}

	public static object Status()
	{
		return new
		{
			node = Node,
			passes = m_passes,
			// The clients this node emits to, and the one set of keys it has emitted - per-client
			// sets no longer exist, because the emits are broadcasts and every attached client has
			// the same rows (T-0204 finding 1).
			clients = Served.OrderBy(client => client).ToArray(),
			// Member messages are per client (MemberHeld): how many each holds, and the totals.
			membersHeld = MemberHeld.OrderBy(pair => pair.Key).Select(pair => new { client = pair.Key, count = pair.Value.Count }).ToArray(),
			memberMessagesTotal = m_memberMessages,
			seededByHandoverTotal = m_seededByHandover,
			selfAnnouncedTotal = m_selfAnnounced,
			mirrored = Installed.OrderBy(key => key.SteamId).ThenBy(key => key.SerialId)
				.Select(key => new { steamId = key.SteamId, serialId = key.SerialId }).ToArray()
		};
	}
}
