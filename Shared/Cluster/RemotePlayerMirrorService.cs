using System;
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
///
/// SE-1192: WHAT A CLIENT HOLDS IS TRACKED PER CLIENT, AND ONLY FOR CLIENTS THAT CAN RECEIVE. The mirror used to
/// send to a client from the moment the replication layer had its client data, which exists from the join, while
/// the world is still loading. Everything sent then is lost (the world request flushes it, as SE-732 found for the
/// client's own member message), but it was booked as delivered, so a fresh login never got the players on other
/// nodes. And a client arriving by handover, switch or cross-node respawn was assumed to hold every member from its
/// previous node, which is false when that node never got to send them. Now only a READY client is sent anything,
/// and the set it holds travels with it in the client handover state (<see cref="HeldBy"/>, <see cref="NoteArrival"/>).
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
	/// The remote players this node has broadcast a phantom for, by the (SteamId, SerialId) the merged list is keyed
	/// by: the set a withdrawal works from. Purely local bookkeeping - a wrong entry costs one redundant message.
	/// SE-1372: the value is the name the phantom was broadcast with. A client keeps the MyPlayer it has when
	/// OnPlayerCreated comes again (CreateNewPlayerInternal returns the existing one), so a player back under a new
	/// name before its phantom was withdrawn kept the old name there: /w found nobody by the new one.
	/// </summary>
	private static readonly Dictionary<(ulong SteamId, int SerialId), string> Installed = new();

	/// <summary>
	/// SE-1372: phantoms whose player attached here, dropped from <see cref="Installed"/> without a withdrawal. Their
	/// MyPlayer is still on the clients here until <see cref="BeforeLocalPlayer"/> replaces it at the player's login.
	/// </summary>
	private static readonly HashSet<(ulong SteamId, int SerialId)> Displaced = new();

	/// <summary>The ready clients the last pass sent to, for <see cref="Status"/>.</summary>
	private static readonly HashSet<ulong> Served = new();

	/// <summary>
	/// What one client holds of each cluster player, by the (SteamId, SerialId) the merged list is keyed by. The
	/// member entry and the MyPlayer travel separately: when a player leaves this node, vanilla's RemovePlayer
	/// broadcasts OnPlayerRemoved to every client here, while a handover keeps the member (T-0179).
	/// </summary>
	private sealed class Holdings
	{
		public readonly HashSet<(ulong SteamId, int SerialId)> Members = new();
		public readonly HashSet<(ulong SteamId, int SerialId)> Players = new();

		public Holdings() { }

		public Holdings(IEnumerable<(ulong, int)> both)
		{
			Members.UnionWith(both);
			Players.UnionWith(Members);
		}
	}

	/// <summary>
	/// PER CLIENT: what each attached client already has - because this node sent it, because vanilla did (the
	/// checkpoint at login, SetUserAccepted and the OnPlayerCreated broadcast for a player arriving here later),
	/// or because its previous node did (<see cref="NoteArrival"/>).
	///
	/// WHY PER CLIENT (w33 2026-09-26 00:2x, Viktor's F3 showed 620 players for 124 - every pilot five
	/// times). The member message is not idempotent on the client the way the player events are:
	/// MyMultiplayerClient.OnConnectedClient raises ClientJoined for every one it receives, and an open
	/// MyGuiScreenPlayers adds a table row per ClientJoined without looking for an existing one (its
	/// header count IS the row count). So a member message goes only to a client that lacks it.
	/// Kept while the client is attached, ready or not: a client whose view is detached for a switch that is
	/// then thawed comes back holding what it held.
	/// </summary>
	private static readonly Dictionary<ulong, Holdings> Held = new();

	/// <summary>
	/// SE-1192: what a client arriving by handover, switch or cross-node respawn holds, as its previous node said
	/// in the client handover state. Noted at the adopt, taken over into <see cref="Held"/> on the first pass the
	/// client is ready. A client with no note is a fresh login: it holds what the checkpoint gave it.
	/// </summary>
	private static readonly Dictionary<ulong, Holdings> Carried = new();

	/// <summary>
	/// The players hosted or attached here as of the last pass. One gone from it lost its MyPlayer on every client
	/// here (RemovePlayer's OnPlayerRemoved broadcast). One new to it is NOT assumed delivered: a client arriving
	/// by a switch gets no SetUserAccepted round at all (SE-1192), so members are booked from what was actually
	/// sent (<see cref="NoteMemberSent"/>), and a player event is simply broadcast again - it is idempotent.
	/// </summary>
	private static HashSet<(ulong SteamId, int SerialId)> m_present = new();

	/// <summary>
	/// SE-1192, target side, at the adopt (game thread): <paramref name="held"/> is what the client's previous
	/// node vouched for, or null when it said nothing - then the client holds nothing this node can vouch for.
	/// </summary>
	internal static void NoteArrival(ulong client, List<MirrorHolding> held)
	{
		if (!Enabled) return;
		Held.Remove(client);
		var holdings = new Holdings();
		foreach (MirrorHolding holding in held ?? new List<MirrorHolding>())
		{
			if (holding.Member) holdings.Members.Add((holding.SteamId, holding.SerialId));
			if (holding.Player) holdings.Players.Add((holding.SteamId, holding.SerialId));
		}
		Carried[client] = holdings;
	}

	/// <summary>
	/// SE-1192, source side, at the export (game thread): what this client holds, for the node it is moving to. A
	/// client this node has not tracked yet (it moved before its first pass here) holds what a fresh login holds.
	/// Null when the mirror is off, so the target falls back to sending everything.
	/// </summary>
	internal static List<MirrorHolding> HeldBy(ulong client)
	{
		if (!Enabled || !ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority) return null;
		if (!Held.TryGetValue(client, out Holdings holdings) && !Carried.TryGetValue(client, out holdings))
			holdings = new Holdings(Present(OnlinePlayersService.Cluster, AttachedClients()));
		// SE-1476: a player whose withdrawal is pending here is about to be taken off this client by a broadcast it
		// still gets while it is attached, so this node does not vouch for it. The next node sends it again if the
		// player is still online, which at worst repeats a member the client has.
		List<MirrorHolding> held = holdings.Members.Union(holdings.Players)
			.Where(key => !Pending.ContainsKey(key))
			.Select(key => new MirrorHolding(key.SteamId, key.SerialId, holdings.Members.Contains(key), holdings.Players.Contains(key)))
			.ToList();
		Exported[client] = (new HashSet<(ulong, int)>(held.Where(h => h.Member).Select(h => (h.SteamId, h.SerialId))),
			ClusterRuntime.LocalMilliseconds);
		return held;
	}

	/// <summary>
	/// SE-1476: the members a client riding a handover (or a cross-node respawn) out of this node was vouched to hold
	/// at its export, and when. The next node trusts that list, so nothing here may take one of them off the client
	/// while it is still attached: a withdrawal waits (<see cref="VouchedInTransit"/>) and a real leave is followed by
	/// the member again (<see cref="BeforeMemberGone"/>). Live 2026-11-14 00:14Z: a player logged out, LJS was
	/// exported 4 s later still holding him, this node's withdrawal reached LJS before the release, and the next node
	/// never sent the player again when he came back: his lines showed no sender name on LJS's client.
	/// Kept while the client is attached and either still leaving by handover or exported less than
	/// <see cref="ExportedHoldMilliseconds"/> ago (a detach for a respawn, a thawed handover).
	/// </summary>
	private static readonly Dictionary<ulong, (HashSet<(ulong SteamId, int SerialId)> Members, double At)> Exported = new();

	private const double ExportedHoldMilliseconds = 10000;

	private static bool VouchedInTransit((ulong, int) key) => Exported.Values.Any(export => export.Members.Contains(key));

	/// <summary>
	/// SE-1476: vanilla's disconnect broadcast for <paramref name="steamId"/> (MyDedicatedServer_ClientLeft) is about
	/// to reach every member here, including clients this node exported as holding that player. Returns the member
	/// message to send them right after it, or null when no such client is attached. Game thread, before vanilla runs:
	/// the name is read while the member data is still here.
	/// </summary>
	internal static Action BeforeMemberGone(ulong steamId)
	{
		if (!Enabled || Exported.Count == 0) return null;
		List<ulong> clients = Exported
			.Where(pair => pair.Key != steamId && pair.Value.Members.Contains((steamId, 0)))
			.Select(pair => pair.Key).ToList();
		if (clients.Count == 0) return null;
		// The cluster list first: a player mirrored here from another node has no member data on this node, and the
		// member data's fallback is the formatted id ("[000...891]" on the bench).
		OnlinePlayersService.OnlinePlayerEntry entry = OnlinePlayersService.Cluster
			.FirstOrDefault(e => e.SteamId == steamId && e.SerialId == 0 && !string.IsNullOrEmpty(e.Name));
		MyMultiplayerBase multiplayer = MyMultiplayer.Static;
		var msg = new ConnectedClientDataMsg
		{
			ClientId = new EndpointId(steamId),
			Name = entry?.Name ?? multiplayer.GetMemberPlatformName(steamId),
			IsAdmin = entry?.IsAdmin ?? MySession.Static.IsUserAdmin(steamId),
			Join = false,
			ServiceName = entry?.ServiceName ?? multiplayer.GetMemberServiceName(steamId)
		};
		return () =>
		{
			if (MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer replication) return;
			foreach (ulong client in clients)
				replication.SendClientConnected(ref msg, client);
			MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] player mirror kept member steamId={steamId} on clients in transit [{string.Join(",", clients)}] (SE-1476)");
		};
	}

	/// <summary>
	/// SE-1192: every member message this node sends - vanilla's SetUserAccepted round and the mirror's own -
	/// passes MyReplicationServer.SendClientConnected (TraversalAnnouncementPatch), and is booked here for a client
	/// the mirror tracks. A client not tracked yet is still loading, and what it is sent now is flushed away.
	/// </summary>
	internal static void NoteMemberSent(ulong sendTo, ulong member)
	{
		if (Held.TryGetValue(sendTo, out Holdings holdings)) holdings.Members.Add((member, 0));
	}

	/// <summary>
	/// A player that left this node for real - a logout, a kick, a ban, not a handover - had its member removed on
	/// every client here by vanilla's own disconnect message (TraversalAnnouncementPatch suppresses only the
	/// handover's). Game thread.
	/// </summary>
	internal static void NoteMemberGone(ulong steamId)
	{
		foreach (Holdings holdings in Held.Values.Concat(Carried.Values))
			holdings.Members.RemoveWhere(key => key.SteamId == steamId);
	}

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
	private static long m_memberMessages, m_seededByArrival, m_selfAnnounced, m_renamed, m_replacedByLogin;

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

		// SE-1192: only clients that can receive - world loaded and not on their way out - are sent anything.
		ulong[] targets = Targets(replication);
		HashSet<(ulong, int)> present = Present(cluster, attached);

		foreach (ulong gone in Held.Keys.Where(client => !attached.Contains(client)).ToArray()) Held.Remove(gone);
		Displaced.RemoveWhere(key => !attached.Contains(key.SteamId));
		foreach (ulong gone in Carried.Keys.Where(client => !attached.Contains(client)).ToArray()) Carried.Remove(gone);
		double now = ClusterRuntime.LocalMilliseconds;
		foreach (ulong gone in Exported.Where(pair => !attached.Contains(pair.Key)
			|| (!ClusterControlServer.IsLeavingByHandover(pair.Key) && now - pair.Value.At >= ExportedHoldMilliseconds))
			.Select(pair => pair.Key).ToArray())
			Exported.Remove(gone);

		// A player that left this node took its MyPlayer off every client here (RemovePlayer's OnPlayerRemoved).
		var departed = new HashSet<(ulong, int)>(m_present);
		departed.ExceptWith(present);
		m_present = present;
		foreach (Holdings holdings in Held.Values.Concat(Carried.Values)) holdings.Players.ExceptWith(departed);

		foreach (ulong target in targets)
		{
			if (Held.ContainsKey(target)) continue;
			if (Carried.Remove(target, out Holdings carried))
			{
				// Came from another node with its own list: it holds what that node vouched for, and nothing of
				// this node's checkpoint, which it never downloaded.
				Held[target] = carried;
				m_seededByArrival++;
				continue;
			}
			// Ready before its adopt noted what it carries: wait for the note rather than guess.
			if (ClusterControlServer.IsArrivingByHandover(target)) continue;
			// A FRESH LOGIN, its world loaded: it holds this node's checkpoint, and needs its own member message.
			Held[target] = new Holdings(present);
			AnnounceSelf(target);
		}
		targets = targets.Where(Held.ContainsKey).ToArray();
		Served.Clear();
		foreach (ulong target in targets) Served.Add(target);
		if (attached.Count == 0)
		{
			// Nothing attached: nothing holds a phantom, so there is nothing to withdraw and
			// nothing to remember. The next client to attach starts from an empty set.
			Installed.Clear();
			Displaced.Clear();
			Pending.Clear();
			return;
		}

		// Every cluster player some ready client here lacks: remote ones, and the ones hosted here that a client did
		// not get from vanilla (a switch arrival, or a client that arrived from another node). Never a client's own
		// row, which it always has.
		var wanted = new List<(OnlinePlayersService.OnlinePlayerEntry Entry, bool Local)>();
		foreach (var pair in desired) wanted.Add((pair.Value, false));
		foreach (OnlinePlayersService.OnlinePlayerEntry entry in cluster)
			if (string.Equals(entry.Node, Node, StringComparison.Ordinal) && attached.Contains(entry.SteamId))
				wanted.Add((entry, true));

		// SE-1372: a phantom whose player came back under another name is withdrawn whole, member and MyPlayer, so the
		// loop below installs it again under the new name. The player is not attached here (desired holds none), so
		// the withdrawal's broadcast cannot reach it.
		foreach (var pair in desired)
		{
			if (!Installed.TryGetValue(pair.Key, out string shown) || shown == pair.Value.Name) continue;
			if (VouchedInTransit(pair.Key)) continue;   // SE-1476: after the client in transit is gone
			MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] player mirror renamed steamId={pair.Key.Item1} serial={pair.Key.Item2} from='{shown}' to='{pair.Value.Name}' node={pair.Value.Node} (SE-1372)");
			Remove(pair.Key);
			Installed.Remove(pair.Key);
			Forget(pair.Key);
			m_renamed++;
		}

		int budget = MaxInstallsPerTick;
		var lacking = new List<ulong>();
		foreach ((OnlinePlayersService.OnlinePlayerEntry entry, bool isLocal) in wanted)
		{
			if (budget <= 0) break;          // resume on the next tick; nothing is lost
			(ulong, int) key = (entry.SteamId, entry.SerialId);
			lacking.Clear();
			bool playerNeeded = false;
			foreach (ulong target in targets)
			{
				if (target == entry.SteamId) continue;   // its own row it always has
				if (!Held[target].Members.Contains(key)) lacking.Add(target);
				playerNeeded |= !Held[target].Players.Contains(key);
			}
			if (lacking.Count == 0 && !playerNeeded) continue;
			budget--;
			if (!Add(replication, lacking, playerNeeded, entry, isLocal)) continue;  // not delivered: next tick
			if (!isLocal) Installed[key] = entry.Name;
			foreach (ulong target in lacking) Held[target].Members.Add(key);
			if (playerNeeded)
				foreach (ulong target in targets) Held[target].Players.Add(key);   // a broadcast reached them all
			m_memberMessages += lacking.Count;
		}

		foreach ((ulong, int) key in Installed.Keys.Where(key => !desired.ContainsKey(key)).ToArray())
		{
			// ARRIVED HERE: vanilla now owns the real member and MyPlayer under the SAME
			// (SteamId, SerialId) as the phantom, so OnUserKicked cannot tell them apart and
			// would tear down the real one. Drop the bookkeeping and send nothing.
			if (local.Contains(key))
			{
				Installed.Remove(key);
				Displaced.Add(key);
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
				Displaced.Add(key);
				Pending.Remove(key);
				continue;
			}

			// GONE FROM THE MERGED LIST: hold it, for the same reason T-0179's announcer holds a
			// departure (07be091). A traversal drops the player from the source node's report and
			// adds it to the target's with no ordering guarantee between the two, so the merged
			// list shows a sub-second absence that is not a logout. Measured on this cluster:
			// 6 of 32 traversals produced such a gap.
			if (!Expired(key)) continue;
			// SE-1476: a client on its way out was exported holding this player; withdrawing now would reach it and
			// leave the next node believing it still holds the member. Stay pending until it is gone from here.
			if (VouchedInTransit(key)) continue;
			Remove(key);
			Installed.Remove(key);
			Pending.Remove(key);
			Forget(key);
		}

		foreach (var pair in desired)
			Pending.Remove(pair.Key);
		m_passes++;
	}

	/// <summary>No client here holds this player's row any more: it was withdrawn.</summary>
	private static void Forget((ulong SteamId, int SerialId) key)
	{
		foreach (Holdings holdings in Held.Values.Concat(Carried.Values))
		{
			holdings.Members.Remove(key);
			holdings.Players.Remove(key);
		}
	}

	/// <summary>
	/// SE-1372: a player is about to get its real MyPlayer here (a fresh login, game thread), and vanilla's
	/// OnPlayerCreated broadcast would leave the clients here with the phantom they already have, under the name it
	/// was installed with. Withdraw the phantom's MyPlayer first. The member stays: vanilla sends its own member
	/// message for the login, which replaces the name. The joining client gets the removal too, but it has no
	/// MyPlayer of its own yet, so there is nothing for it to remove.
	/// </summary>
	internal static void BeforeLocalPlayer(ulong steamId, int serialId)
	{
		if (!Enabled || !Installed.Remove((steamId, serialId)) & !Displaced.Remove((steamId, serialId))) return;
		Pending.Remove((steamId, serialId));
		MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] player mirror phantom replaced by login steamId={steamId} serial={serialId} (SE-1372)");
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyPlayerCollection.OnPlayerRemoved, steamId, serialId);
		foreach (Holdings holdings in Held.Values.Concat(Carried.Values)) holdings.Players.Remove((steamId, serialId));
		m_replacedByLogin++;
	}

	/// <summary>
	/// Every client attached to this node, by Steam id - a superset of <see cref="Targets"/>. Used to decide
	/// who a BROADCAST would reach and which clients' bookkeeping to keep.
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

	/// <summary>
	/// The clients on this node that can actually receive a member message or a player event: READY at the
	/// replication layer (MyClient.IsReady, set by ClientReady once the world is loaded, or by a handover adopt).
	/// The client data exists from the join, and anything sent before the world request is flushed away
	/// (SE-1192, SE-732). A client riding a handover out is left to the node it moves to, which gets this node's
	/// view of what it holds (<see cref="HeldBy"/>) at the export.
	/// </summary>
	private static ulong[] Targets(MyReplicationServer replication)
	{
		MyClientCollection clients = Sync.Clients;
		if (clients == null) return Array.Empty<ulong>();
		return clients.GetClients()
			.Select(client => client.SteamUserId)
			.Where(steamId => steamId != Sync.ServerId && steamId != 0
				&& replication.m_clientStates.TryGetValue(new Endpoint(new EndpointId(steamId), 0), out MyClient state)
				&& state.IsReady && !ClusterControlServer.IsLeavingByHandover(steamId))
			.ToArray();
	}

	/// <summary>The players every client here has from vanilla: hosted here, and attached here.</summary>
	private static HashSet<(ulong, int)> Present(OnlinePlayersService.OnlinePlayerEntry[] cluster, HashSet<ulong> attached)
	{
		var present = new HashSet<(ulong, int)>();
		foreach (OnlinePlayersService.OnlinePlayerEntry entry in cluster)
			if (string.Equals(entry.Node, Node, StringComparison.Ordinal))
				present.Add((entry.SteamId, entry.SerialId));
		foreach (ulong client in attached) present.Add((client, 0));
		return present;
	}

	/// <summary>
	/// Install one player, in the order the clients need it: the member first
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
	///
	/// SE-1192: a player hosted HERE is installed only for a client here that vanilla did not give it to.
	/// Its OnPlayerCreated broadcast also reaches that player's own client, so it carries what vanilla's own
	/// would (CreateNewPlayer): the real player's builder with its promote level, creative tools and remote admin
	/// settings. A phantom's defaults there would switch the player's creative tools off (T-0378).
	/// </summary>
	private static bool Add(MyReplicationServer replication, List<ulong> memberTargets, bool announcePlayer,
		OnlinePlayersService.OnlinePlayerEntry entry, bool isLocal)
	{
		MyIdentity identity = MySession.Static?.Players.TryGetIdentity(entry.IdentityId);
		if (identity == null) return false;   // not replicated here yet; next tick
		MyObjectBuilder_Player builder = !announcePlayer ? null : isLocal ? LocalBuilder(entry) : Builder(entry, identity);
		if (announcePlayer && builder == null) return false;

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

		// A broadcast, and idempotent on every client that already has the player.
		MySession.Static.Players.ClusterAnnounceIdentity(entry.IdentityId);
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyPlayerCollection.OnPlayerCreated,
			entry.SteamId, entry.SerialId, builder);
		return true;
	}

	/// <summary>What vanilla's CreateNewPlayer broadcasts for a player hosted here; null when it is not here yet.</summary>
	private static MyObjectBuilder_Player LocalBuilder(OnlinePlayersService.OnlinePlayerEntry entry)
	{
		MySession session = MySession.Static;
		if (!session.Players.TryGetPlayerById(new MyPlayer.PlayerId(entry.SteamId, entry.SerialId), out MyPlayer player)
			|| player.Identity == null) return null;
		MyObjectBuilder_Player builder = player.GetObjectBuilder();
		session.RemoteAdminSettings.TryGetValue(entry.SteamId, out AdminSettingsEnum settings);
		builder.PromoteLevel = session.GetUserPromoteLevel(entry.SteamId);
		builder.CreativeToolsEnabled = session.CreativeToolsEnabled(entry.SteamId);
		builder.RemoteAdminSettings = (int)settings;
		return builder;
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
	/// <see cref="Targets"/> holds only ready clients, so a fresh one is announced on its first pass there.
	/// </summary>
	private static void AnnounceSelf(ulong client)
	{
		MyReplicationServer replication = (MyReplicationServer)MyMultiplayer.Static.ReplicationLayer;
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
			// The ready clients this node sends to, and the remote players it has broadcast a phantom for.
			clients = Served.OrderBy(client => client).ToArray(),
			// What each attached client holds (Held), by key, and the totals.
			membersHeld = Held.OrderBy(pair => pair.Key).Select(pair => new
			{
				client = pair.Key,
				count = pair.Value.Members.Count,
				members = pair.Value.Members.OrderBy(key => key.SteamId).ThenBy(key => key.SerialId)
					.Select(key => new { steamId = key.SteamId, serialId = key.SerialId }).ToArray(),
				players = pair.Value.Players.OrderBy(key => key.SteamId).ThenBy(key => key.SerialId)
					.Select(key => new { steamId = key.SteamId, serialId = key.SerialId }).ToArray()
			}).ToArray(),
			carriedPending = Carried.Keys.OrderBy(client => client).ToArray(),
			// SE-1476: withdrawals waiting out the grace, and clients in transit with what their export vouched for.
			pending = Pending.Keys.OrderBy(key => key.SteamId).ThenBy(key => key.SerialId)
				.Select(key => new { steamId = key.SteamId, serialId = key.SerialId }).ToArray(),
			exported = Exported.OrderBy(pair => pair.Key).Select(pair => new
			{
				client = pair.Key,
				members = pair.Value.Members.OrderBy(key => key.SteamId).Select(key => key.SteamId).ToArray()
			}).ToArray(),
			memberMessagesTotal = m_memberMessages,
			seededByArrivalTotal = m_seededByArrival,
			renamedTotal = m_renamed,
			replacedByLoginTotal = m_replacedByLogin,
			moderationForwardedTotal = Shared.Patches.GatewayModerationPatch.Forwarded,
			moderationFailedTotal = Shared.Patches.GatewayModerationPatch.Failed,
			selfAnnouncedTotal = m_selfAnnounced,
			mirrored = Installed.Keys.OrderBy(key => key.SteamId).ThenBy(key => key.SerialId)
				.Select(key => new { steamId = key.SteamId, serialId = key.SerialId }).ToArray()
		};
	}
}
