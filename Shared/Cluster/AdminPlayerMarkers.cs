using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Network;
using VRage.Utils;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// SE-1596: the Alt-F10 "Show players" checkbox for players on OTHER nodes.
///
/// Vanilla (<c>MyDataReceiver.UpdateHud</c>) draws a marker only for a player whose character the admin's client
/// holds. A player on another node reaches that client as a phantom MyPlayer with no character
/// (<see cref="RemotePlayerMirrorService"/>), so it gets none. This service sends each admin with the flag on a
/// GPS marker per player on another node, named after the player, at the position the cluster position registry
/// has for them (about a second old).
///
/// THE MARKERS LIVE ONLY ON THE ADMIN'S CLIENT. They go out as vanilla's <c>AddGpsOnClient</c> and
/// <c>DeleteGpsOnClient</c> events addressed to that one client and never enter this node's
/// <c>MyGpsCollection</c>, so no save (partition or World Authority) and no snapshot of the cluster's GPS
/// authority can contain them, and no other identity gets them. An edit or delete the admin makes in the G screen
/// reaches the server as a request for a hash the server does not have, which vanilla ignores.
///
/// THEY EXPIRE BY THEMSELVES. Each one is sent unconfirmed (IsFinal false) with a <see cref="LifetimeSeconds"/>
/// lifetime, which the client's own <c>MyGpsCollection.DiscardOld</c> enforces; sending the same marker again
/// restarts it. So a marker this node could not take back (the admin left by a handover or a cross-node respawn,
/// or this node died) is gone from the client within that time anyway. The G screen lists unconfirmed markers in
/// gray after the admin's own.
///
/// ONLY CHANGES ARE SENT. A pass every <see cref="IntervalMilliseconds"/> compares what each admin was sent with
/// what they should see (<see cref="Diff"/>): a new player is one add, a player who moved more than
/// <see cref="MoveMeters"/> or changed node is a delete plus an add (the GPS hash includes the coordinates, and
/// vanilla's modify event would make the marker permanent), a player who left the cluster or came to this node is
/// one delete, and a marker unchanged for <see cref="KeepAliveMilliseconds"/> is sent again to keep it alive.
///
/// Players on this node are left to vanilla, which draws the ones whose character the client holds.
///
/// CLUSTER_ADMIN_SHOW_PLAYERS=0 turns it off (read at startup).
/// </summary>
public static class AdminPlayerMarkers
{
    internal const double IntervalMilliseconds = 2000;
    internal const double MoveMeters = 10;
    internal const double KeepAliveMilliseconds = 10000;
    internal const int LifetimeSeconds = 30;

    /// <summary>Description prefix, so the admin (and a test) can tell these markers from their own.</summary>
    internal const string DescriptionPrefix = "Show players: ";

    private static readonly Color MarkerColor = new Color(255, 160, 60);

    private static readonly bool Enabled =
        (Environment.GetEnvironmentVariable("CLUSTER_ADMIN_SHOW_PLAYERS") ?? "1") != "0";

    private static readonly string Node =
        Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

    /// <summary>A player the admin should see a marker for.</summary>
    internal readonly record struct Wanted(
        long IdentityId,
        string Name,
        string Node,
        double X,
        double Y,
        double Z
    );

    /// <summary>A marker the admin's client was sent, with its GPS hash and when it was last sent.</summary>
    internal readonly record struct Shown(Wanted Player, int Hash, double SentAt);

    /// <summary>What one pass sends one admin. A moved player is in both Remove and Add.</summary>
    internal sealed class Plan
    {
        public readonly List<Shown> Remove = new();
        public readonly List<Wanted> Add = new();
        public readonly List<Shown> Refresh = new();
    }

    /// <summary>Per admin client: the markers it holds, by player identity.</summary>
    private static readonly Dictionary<ulong, Dictionary<long, Shown>> Sent = new();

    /// <summary>
    /// Clients whose markers <see cref="Withdraw"/> took back, until when this node leaves them alone. A client
    /// committed to another node can stay ready here for a moment after its export; markers sent then would
    /// duplicate the next node's until they expire. One still here afterwards (a thawed handover) gets them again.
    /// </summary>
    private static readonly Dictionary<ulong, double> Withdrawn = new();

    private const double WithdrawnMilliseconds = 10000;

    private static double m_nextPass;
    private static double m_nextSummary;
    private static long m_adds;
    private static long m_removes;
    private static long m_refreshes;

    /// <summary>Game thread, every update.</summary>
    public static void Update()
    {
        if (
            !Enabled
            || !ClusterRuntime.IsClustered
            || ClusterRuntime.IsWorldAuthority
            || MySession.Static?.Ready != true
        )
            return;
        double now = ClusterRuntime.LocalMilliseconds;
        if (now < m_nextPass)
            return;
        m_nextPass = now + IntervalMilliseconds;
        try
        {
            Pass(now);
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                "[ClusterPlugin] admin player markers pass failed: "
                    + SecretRedactor.Redact(exception.ToString())
            );
        }
    }

    private static void Pass(double now)
    {
        MyReplicationServer replication =
            MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer;
        if (replication == null)
            return;
        var targets = new HashSet<ulong>(RemotePlayerMirrorService.Targets(replication));

        // A client no longer ready here took its markers along or lost them with its session; either way this node
        // can no longer reach them, and they expire on the client.
        foreach (ulong gone in Sent.Keys.Where(client => !targets.Contains(client)).ToArray())
            Sent.Remove(gone);
        foreach (ulong done in Withdrawn.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
            Withdrawn.Remove(done);
        targets.ExceptWith(Withdrawn.Keys);

        Wanted[] wanted = null;
        foreach (ulong client in targets)
        {
            bool admin = MySession.Static.IsUserShowAllPlayers(client);
            if (!admin && !Sent.ContainsKey(client))
                continue;
            if (admin)
                wanted ??= Select(
                    OnlinePlayersService.Cluster,
                    PositionRegistryService.Players,
                    Node,
                    LocalPlayers()
                );
            if (!Sent.TryGetValue(client, out Dictionary<long, Shown> shown))
                Sent[client] = shown = new Dictionary<long, Shown>();
            Send(client, Diff(shown, admin ? wanted : Array.Empty<Wanted>(), now), shown, now);
            if (shown.Count == 0)
                Sent.Remove(client);
        }

        if (now >= m_nextSummary && m_adds + m_removes + m_refreshes > 0)
        {
            m_nextSummary = now + 60000;
            Console.WriteLine(
                "[ClusterPlugin] admin player markers admins="
                    + Sent.Count
                    + " shown="
                    + Sent.Values.Sum(x => x.Count)
                    + " adds="
                    + m_adds
                    + " removes="
                    + m_removes
                    + " refreshes="
                    + m_refreshes
            );
        }
    }

    /// <summary>
    /// Source side of a handover or a cross-node respawn (game thread, at the export): take the markers back while
    /// the client still listens to this node. The next node starts from nothing and sends its own; a delete that
    /// does not arrive leaves a marker that expires on the client.
    /// </summary>
    internal static void Withdraw(ulong client)
    {
        if (!Enabled)
            return;
        Withdrawn[client] = ClusterRuntime.LocalMilliseconds + WithdrawnMilliseconds;
        if (!Sent.Remove(client, out Dictionary<long, Shown> shown))
            return;
        foreach (Shown marker in shown.Values)
            SendDelete(client, marker.Hash);
        m_removes += shown.Count;
    }

    /// <summary>Steam ids of the players attached to this node: vanilla handles them.</summary>
    private static HashSet<ulong> LocalPlayers()
    {
        var local = new HashSet<ulong>();
        foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            local.Add(player.Id.SteamId);
        return local;
    }

    /// <summary>
    /// The players an admin on <paramref name="node"/> gets a marker for: online in the cluster on another node, not
    /// attached here (a stale report of another node can still name a player who just arrived), with a position.
    /// Viewed cameras in the position registry (ClientId 0) are not players. Pure.
    /// </summary>
    internal static Wanted[] Select(
        IReadOnlyList<OnlinePlayersService.OnlinePlayerEntry> cluster,
        IReadOnlyList<ClusterPlayerPosition> positions,
        string node,
        HashSet<ulong> local
    )
    {
        var remote = new Dictionary<ulong, OnlinePlayersService.OnlinePlayerEntry>();
        foreach (OnlinePlayersService.OnlinePlayerEntry entry in cluster)
            if (
                entry.SerialId == 0
                && !string.IsNullOrEmpty(entry.Node)
                && !string.Equals(entry.Node, node, StringComparison.Ordinal)
                && !local.Contains(entry.SteamId)
            )
                remote[entry.SteamId] = entry;
        var wanted = new List<Wanted>();
        foreach (ClusterPlayerPosition position in positions)
            if (
                position.ClientId != 0
                && remote.Remove(
                    position.ClientId,
                    out OnlinePlayersService.OnlinePlayerEntry entry
                )
            )
                wanted.Add(
                    new Wanted(
                        position.IdentityId,
                        MarkerName(entry.Name, entry.SteamId),
                        entry.Node,
                        position.X,
                        position.Y,
                        position.Z
                    )
                );
        return wanted.ToArray();
    }

    /// <summary>
    /// What to send one admin, given what it holds and what it should see. A marker moves (remove + add) when its
    /// player moved more than <see cref="MoveMeters"/>, was renamed or changed node; an unchanged one is sent again
    /// once it is <see cref="KeepAliveMilliseconds"/> old. Pure.
    /// </summary>
    internal static Plan Diff(
        IReadOnlyDictionary<long, Shown> shown,
        IReadOnlyList<Wanted> wanted,
        double now
    )
    {
        var plan = new Plan();
        var seen = new HashSet<long>();
        foreach (Wanted player in wanted)
        {
            if (!seen.Add(player.IdentityId))
                continue;
            if (!shown.TryGetValue(player.IdentityId, out Shown marker))
            {
                plan.Add.Add(player);
                continue;
            }
            Wanted was = marker.Player;
            double dx = player.X - was.X,
                dy = player.Y - was.Y,
                dz = player.Z - was.Z;
            if (
                dx * dx + dy * dy + dz * dz > MoveMeters * MoveMeters
                || was.Name != player.Name
                || was.Node != player.Node
            )
            {
                plan.Remove.Add(marker);
                plan.Add.Add(player);
            }
            else if (now - marker.SentAt >= KeepAliveMilliseconds)
                plan.Refresh.Add(marker);
        }
        foreach (Shown marker in shown.Values)
            if (!seen.Contains(marker.Player.IdentityId))
                plan.Remove.Add(marker);
        return plan;
    }

    private static void Send(ulong client, Plan plan, Dictionary<long, Shown> shown, double now)
    {
        foreach (Shown marker in plan.Remove)
        {
            SendDelete(client, marker.Hash);
            shown.Remove(marker.Player.IdentityId);
        }
        foreach (Wanted player in plan.Add)
        {
            SendAdd(client, player);
            shown[player.IdentityId] = new Shown(player, Hash(player), now);
        }
        foreach (Shown marker in plan.Refresh)
        {
            SendAdd(client, marker.Player);
            shown[marker.Player.IdentityId] = marker with { SentAt = now };
        }
        m_removes += plan.Remove.Count;
        m_adds += plan.Add.Count;
        m_refreshes += plan.Refresh.Count;
    }

    private static void SendAdd(ulong client, Wanted player)
    {
        var message = new MyGpsCollection.AddMsg
        {
            Name = player.Name,
            DisplayName = player.Name,
            Description = DescriptionPrefix + player.Node,
            Coords = new Vector3D(player.X, player.Y, player.Z),
            ShowOnHud = true,
            IsFinal = false,
            AlwaysVisible = true,
            GPSColor = MarkerColor,
            PlaySoundOnCreation = false,
            DiscardSeconds = LifetimeSeconds,
        };
        MyMultiplayer.RaiseStaticEvent(
            (IMyEventOwner s) => MyGpsCollection.AddGpsOnClient,
            message,
            new EndpointId(client)
        );
    }

    private static void SendDelete(ulong client, int hash) =>
        MyMultiplayer.RaiseStaticEvent(
            (IMyEventOwner s) => MyGpsCollection.DeleteGpsOnClient,
            hash,
            new EndpointId(client)
        );

    /// <summary>
    /// The hash the client gives the marker: <c>MyGps.CalculateHash</c> for a GPS without an entity. The client
    /// computes it from the name after <c>CleanUpAddMessage</c>, which <see cref="MarkerName"/> has already
    /// applied, so both sides agree.
    /// </summary>
    internal static int Hash(Wanted player)
    {
        int hash = MyUtils.GetHash(player.Name);
        hash = MyUtils.GetHash(player.X, hash);
        hash = MyUtils.GetHash(player.Y, hash);
        return MyUtils.GetHash(player.Z, hash);
    }

    /// <summary>
    /// The player's name as the client will store it: vanilla's add replaces '&amp;' and drops "#x", and refuses a
    /// marker whose text has a character XML cannot hold.
    /// </summary>
    internal static string MarkerName(string name, ulong steamId)
    {
        name ??= string.Empty;
        var text = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
            if (XmlConvert.IsXmlChar(name[i]))
                text.Append(name[i]);
            else if (i + 1 < name.Length && XmlConvert.IsXmlSurrogatePair(name[i + 1], name[i]))
                text.Append(name[i]).Append(name[++i]);
        string clean = text.ToString().Replace('&', ' ').Replace("#x", string.Empty).Trim();
        return clean.Length != 0 ? clean : "Player " + steamId;
    }
}
