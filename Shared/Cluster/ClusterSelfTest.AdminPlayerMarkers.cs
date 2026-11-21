using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Sandbox.Game.Screens.Helpers;
using VRageMath;
using static ClusterPlugin.AdminPlayerMarkers;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// <summary>
    /// SE-1596, the pure parts of the Show players markers: which players an admin gets a marker for, what a pass
    /// sends (add, move as delete plus add, keep-alive, removal), the client's GPS hash, and the name cleanup.
    /// </summary>
    private static void AdminPlayerMarkerRules()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("admin-player-markers: " + message);
        }
        OnlinePlayersService.OnlinePlayerEntry Online(string node, ulong steam, string name) =>
            new(node, steam, 0, (long)steam, name);
        ClusterPlayerPosition At(ulong steam, double x) => new((long)steam, steam, 0, x, 0, 0);

        // -- Select --
        var cluster = new[]
        {
            Online("node-1", 1, "Here"),
            Online("node-2", 2, "Away"),
            Online("node-3", 3, "No position"),
            Online("node-2", 4, "Just arrived"), // stale report: attached to node-1 already
            Online("node-2", 6, "Away & #xodd"),
            new OnlinePlayersService.OnlinePlayerEntry("node-2", 2, 1, 99, "Away's second player"),
        };
        var positions = new[]
        {
            At(1, 10),
            At(2, 20),
            At(4, 40),
            At(5, 50), // position without an online entry: left the cluster
            At(6, 60),
            new ClusterPlayerPosition(777, 0, 777, 1, 2, 3), // a viewed camera
        };
        Wanted[] wanted = Select(cluster, positions, "node-1", new HashSet<ulong> { 1, 4 });
        Check(
            wanted.Select(x => x.IdentityId).SequenceEqual(new long[] { 2, 6 }),
            "only players online on another node, not attached here, with a position: "
                + string.Join(",", wanted.Select(x => x.IdentityId))
        );
        Check(
            wanted[0] == new Wanted(2, "Away", "node-2", 20, 0, 0),
            "the marker carries the player's name, node and position: " + wanted[0]
        );
        Check(wanted[1].Name == "Away   odd", "the name is cleaned as vanilla's add cleans it");

        // -- Diff --
        const double Now = 100_000;
        var shown = new Dictionary<long, Shown>();
        Plan plan = Diff(shown, wanted, Now);
        Check(
            plan.Add.Count == 2 && plan.Remove.Count == 0 && plan.Refresh.Count == 0,
            "an admin who holds nothing gets one add per player"
        );
        foreach (Wanted player in plan.Add)
            shown[player.IdentityId] = new Shown(player, Hash(player), Now);

        plan = Diff(shown, wanted, Now + IntervalMilliseconds);
        Check(
            plan.Add.Count + plan.Remove.Count + plan.Refresh.Count == 0,
            "nothing changed, nothing is sent"
        );

        Wanted near = wanted[0] with { X = wanted[0].X + MoveMeters * 0.9 };
        Wanted far = wanted[1] with { Y = MoveMeters * 1.1 };
        plan = Diff(shown, new[] { near, far }, Now + IntervalMilliseconds);
        Check(
            plan.Add.SequenceEqual(new[] { far })
                && plan.Remove.Select(x => x.Player.IdentityId).SequenceEqual(new long[] { 6 })
                && plan.Refresh.Count == 0,
            "a player who moved more than MoveMeters is a delete of the old marker plus an add; a smaller move sends nothing"
        );

        Wanted renamed = wanted[0] with { Name = "Renamed" };
        Wanted moved = wanted[1] with { Node = "node-3" };
        plan = Diff(shown, new[] { renamed, moved }, Now);
        Check(
            plan.Add.Count == 2 && plan.Remove.Count == 2,
            "a new name or a new node replaces the marker"
        );

        plan = Diff(shown, wanted, Now + KeepAliveMilliseconds);
        Check(
            plan.Refresh.Count == 2 && plan.Add.Count == 0 && plan.Remove.Count == 0,
            "an unchanged marker is sent again once KeepAliveMilliseconds old"
        );
        Check(
            KeepAliveMilliseconds * 2 < LifetimeSeconds * 1000.0
                && IntervalMilliseconds < KeepAliveMilliseconds,
            "a marker kept alive never expires on the client, even with one keep-alive lost"
        );

        plan = Diff(shown, new[] { wanted[1] }, Now);
        Check(
            plan.Remove.Select(x => x.Player.IdentityId).SequenceEqual(new long[] { 2 })
                && plan.Add.Count == 0,
            "a player who left the cluster or came to the admin's node loses the marker"
        );
        plan = Diff(shown, Array.Empty<Wanted>(), Now);
        Check(
            plan.Remove.Count == 2 && plan.Add.Count == 0,
            "the flag going off takes every marker back"
        );
        plan = Diff(
            new Dictionary<long, Shown>(),
            new[] { wanted[0], wanted[0] with { X = 1 } },
            Now
        );
        Check(plan.Add.Count == 1, "one marker per player even if a player is listed twice");

        // -- Hash: what the client computes in MyGps.CalculateHash for a GPS without an entity --
        foreach (Wanted player in wanted.Append(far))
        {
            var gps = (MyGps)RuntimeHelpers.GetUninitializedObject(typeof(MyGps));
            gps.Name = player.Name;
            gps.Coords = new Vector3D(player.X, player.Y, player.Z);
            Check(
                gps.CalculateHash() == Hash(player),
                "the marker hash equals the client's GPS hash for " + player
            );
        }

        // -- MarkerName --
        Check(MarkerName("a\u0001b", 9) == "ab", "a character XML cannot hold is dropped");
        Check(MarkerName("🚀 Rocket", 9) == "🚀 Rocket", "a surrogate pair stays");
        Check(MarkerName(null, 9) == "Player 9", "a player without a name gets one");
    }
}
