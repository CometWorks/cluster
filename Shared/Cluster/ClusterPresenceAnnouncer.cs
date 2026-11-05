using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Sandbox.Game.Multiplayer;

namespace ClusterPlugin;

/// <summary>
/// T-0179 part C: announce a real login or logout ONCE, to the whole cluster.
///
/// The other two parts of T-0179 are subtractive - <see cref="TraversalAnnouncementPatch"/> removes
/// the two vanilla toasts that a node traversal produces, because neither is true. This part is the
/// additive half: with the toasts gone and T-0178's mirror keeping every node's member list
/// identical, a player arriving in or leaving the CLUSTER would otherwise be announced only to the
/// clients that happen to share its node, and to nobody else.
///
/// The source is <see cref="OnlinePlayersService"/>'s merged deltas, which is what makes this
/// correct rather than merely cluster-wide. They are keyed by (SteamId, SerialId) and not by node,
/// so a traversal - which only changes WHICH node reports the player - usually raises nothing here.
///
/// USUALLY, NOT ALWAYS, and the first live measurement proved it. The merged list is a plain set
/// difference between consecutive snapshots (<see cref="OnlinePlayersService"/>.SetCluster), and a
/// handover has no rule that the target must report the player before the source stops. When it
/// drops first the key is absent from one snapshot, and the whole cluster is told that a player
/// who never went anywhere left and came back. Measured on w32, 2026-09-19 05:41-05:45: 6 of 32
/// traversals produced such a pair, the two lines 67 ms to 1.03 s apart, every one of them within
/// 1.3 s of a "traversal ... toast suppressed" line for that same player. The other 26 traversals
/// happened to overlap and were silent, which is why the original claim survived review - it is
/// true of the ordering the handover happens to take most of the time.
///
/// SO A DEPARTURE IS HELD for <see cref="GraceSeconds"/> before it is announced, and cancelled if
/// the same (SteamId, SerialId) reappears within it; the arrival that cancels it is not announced
/// either, since nobody was told about the departure. A real logout cannot be confused with this:
/// re-entering means loading the world, tens of seconds, never the sub-second gaps measured here.
/// The hold also cannot invent a departure - <see cref="Flush"/> re-checks the merged list at the
/// moment it would speak, so a player still online is dropped silently however the hold was armed.
///
/// EMITTED BY THE WORLD AUTHORITY ONLY. Every node holds the merged list and would raise the same
/// delta, so an announcement from each would be one line per node. The WA is the single merger, so
/// it is the natural single emitter, and it sends through <see cref="ChatAuthorityPatch"/>'s
/// ordinary accept path - the same one a player message takes - rather than a side channel. That
/// also puts the line in /cluster/chat-history, where a scenario can assert on it.
///
/// AUTHORED AS THE SERVER (<c>Sync.MyId</c> on the WA), following
/// <c>MyDedicatedServerBase.SendChatMessageToPlayer:662-673</c>, which is how vanilla writes a
/// server line. It matters beyond cosmetics: <c>MyMultiplayerClient.OnChatMessage:468</c> feeds
/// any message whose author is an ADMIN member through <c>MyClientDebugCommands.Process</c> on
/// every client that receives it. The server is not in <c>m_memberData</c>, so a server-authored
/// line is never treated as a command; a line authored as the joining player would be, whenever
/// that player happened to be an admin.
///
/// KNOWN AND DELIBERATE OVERLAP: on the node the player actually attached to, vanilla still shows
/// its own "X connected" HUD toast, so those clients get the toast AND this chat line. They are
/// different media and the duplication is mild, and the alternative - suppressing the vanilla toast
/// for every login, not just handovers - would mean a cluster with a broken chat path announces
/// nothing at all. Fail-safe wins; if the duplication is unwanted, that is a one-line change to
/// <see cref="TraversalAnnouncementPatch"/> and Viktor's call, not a silent one to make here.
///
/// SE-1081: ONLY PLAYERS ADMITTED THROUGH STEAM (or EOS) ARE ANNOUNCED. Clients that join through
/// Direct Transport are bots and headless clients, and fifty trader bots logging in and out drowned
/// the chat on Octahedron. The Gateway decides the transport, so the reporting node marks the entry
/// (<see cref="OnlinePlayersService.OnlinePlayerEntry.DirectTransport"/>) instead of trusting the
/// ServiceName the client asserted. CLUSTER_PRESENCE_DIRECT=1 announces them too, for test benches
/// whose clients all join through Direct Transport.
/// </summary>
public static class ClusterPresenceAnnouncer
{
	/// <summary>Off switch, matching the mirror's: CLUSTER_PRESENCE_ANNOUNCE=0 restores silence.</summary>
	private static readonly bool Enabled =
		(Environment.GetEnvironmentVariable("CLUSTER_PRESENCE_ANNOUNCE") ?? "1") != "0";

	/// <summary>SE-1081: CLUSTER_PRESENCE_DIRECT=1 announces Direct Transport players as well.</summary>
	private static readonly bool AnnounceDirect =
		Environment.GetEnvironmentVariable("CLUSTER_PRESENCE_DIRECT") == "1";

	/// <summary>
	/// How long a departure is held before it is spoken, so a handover gap can cancel it.
	/// Five seconds is two orders of magnitude above the gaps measured (67 ms - 1.03 s) and two
	/// orders below a genuine re-entry, which has to load the world first. CLUSTER_PRESENCE_GRACE=0
	/// announces immediately, restoring the pre-fix behaviour for a bisect.
	/// </summary>
	private static readonly double GraceSeconds = ParseGrace();

	private static double ParseGrace()
	{
		string raw = Environment.GetEnvironmentVariable("CLUSTER_PRESENCE_GRACE");
		if (string.IsNullOrWhiteSpace(raw)) return 5.0;
		return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
			&& parsed >= 0.0 ? parsed : 5.0;
	}

	private static readonly object SyncRoot = new object();

	/// <summary>Departures waiting out <see cref="GraceSeconds"/>, keyed as the merged list is.</summary>
	private static readonly Dictionary<(ulong SteamId, int SerialId), (string Name, DateTime DueAt)> Pending = new();

	private static Timer m_flushTimer;

	internal static void Install()
	{
		OnlinePlayersService.PlayerEnteredCluster += (entry, seeding) => Announce(entry, seeding, true);
		OnlinePlayersService.PlayerLeftCluster += (entry, seeding) => Announce(entry, seeding, false);
		// A held departure has to be spoken even if nothing else ever happens in the cluster, so it
		// cannot be flushed lazily on the next delta - the quiet cluster is exactly the case where
		// the last player leaving would go unannounced forever.
		m_flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}

	private static void Announce(OnlinePlayersService.OnlinePlayerEntry entry, bool seeding, bool entered)
	{
		// Seeding is the first snapshot this process sees, where every player present is reported as
		// arriving. Announcing those would have a World Authority restart tell the cluster that
		// everyone just logged in.
		if (!Enabled || seeding || !ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority) return;
		if (!Announces(entry, AnnounceDirect)) return;
		var key = (entry.SteamId, entry.SerialId);
		string name = NameOf(entry);
		if (entered)
		{
			lock (SyncRoot)
			{
				// Cancels the held departure AND says nothing itself: the cluster was never told
				// this player left, so telling it they arrived would be the same false statement
				// with the halves swapped.
				if (Pending.Remove(key)) return;
			}
			Say(name + " joined the cluster", entry.SteamId, true);
			return;
		}
		if (GraceSeconds <= 0.0)
		{
			Say(name + " left the cluster", entry.SteamId, false);
			return;
		}
		lock (SyncRoot)
			Pending[key] = (name, DateTime.UtcNow.AddSeconds(GraceSeconds));
	}

	/// <summary>
	/// Whether a login or logout of this entry is worth a chat line. A second character on the same
	/// account is not a second person arriving, and a Direct Transport client is a bot (SE-1081).
	/// </summary>
	internal static bool Announces(OnlinePlayersService.OnlinePlayerEntry entry, bool announceDirect) =>
		entry.SerialId == 0 && (announceDirect || !entry.DirectTransport);

	/// <summary>Speak the departures whose grace has run out and that are really gone.</summary>
	private static void Flush()
	{
		if (Pending.Count == 0) return;
		List<(ulong SteamId, int SerialId, string Name)> due = null;
		DateTime now = DateTime.UtcNow;
		lock (SyncRoot)
		{
			foreach (var pair in Pending)
			{
				if (pair.Value.DueAt > now) continue;
				(due ??= new List<(ulong, int, string)>())
					.Add((pair.Key.SteamId, pair.Key.SerialId, pair.Value.Name));
			}
			foreach (var row in due ?? Enumerable.Empty<(ulong SteamId, int SerialId, string Name)>())
				Pending.Remove((row.SteamId, row.SerialId));
		}
		if (due == null) return;
		// Losing authority, or a snapshot that re-adds the player without raising an Entered delta,
		// would otherwise have us announce a departure for somebody sitting in the world. The
		// merged list is the authority on who is here; the hold is only what gives us time to ask.
		OnlinePlayersService.OnlinePlayerEntry[] present =
			ClusterRuntime.IsWorldAuthority ? OnlinePlayersService.Cluster : null;
		if (present == null) return;
		foreach (var row in due)
		{
			if (present.Any(e => e.SteamId == row.SteamId && e.SerialId == row.SerialId)) continue;
			Say(row.Name + " left the cluster", row.SteamId, false);
		}
	}

	private static string NameOf(OnlinePlayersService.OnlinePlayerEntry entry) =>
		string.IsNullOrEmpty(entry.Name) ? entry.SteamId.ToString() : entry.Name;

	private static void Say(string line, ulong steamId, bool entered)
	{
		try
		{
			ChatAuthorityPatch.SendFromControl(Sync.MyId, line);
		}
		catch (Exception exception)
		{
			// Raised on the global-state-channel thread, or on the flush timer: a throw here would
			// stop the remaining delta handlers, and an announcement is the least important thing
			// in this pipeline.
			Console.WriteLine("[ClusterPlugin] presence announcement failed steam=" + steamId
				+ " entered=" + entered + ": " + exception);
		}
	}
}
