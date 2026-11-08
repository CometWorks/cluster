using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using ClusterPlugin;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1280 bench routes: a burst of reputation writes in one game-thread frame, the way a MES damage trigger makes them
/// (ChangeDamageOwnerReputation: read the reputation, add the change, set it, for each faction of its list, on every hit).
/// Goes through the mod API, IMyFactionCollection, as mods and scripts do.
/// </summary>
internal static class ReputationBurstRoutes
{
    public static void Register()
    {
        // POST /faction-test-reputation-burst?identityId=&calls=&factions=&kind=player|faction&maxMs=
        // In one frame: every chosen NPC faction set to 0, then `calls` read-add(-1)-set writes over them in turn. Stops
        // early after maxMs (a blocking build holds the frame for a WA round trip per call). kind=faction writes the
        // first faction's reputation with each of the others instead of the identity's.
        TestRoutes.Add(
            "POST",
            "/faction-test-reputation-burst",
            context =>
            {
                long identity = TestRoutes.Long(context, "identityId");
                int calls = (int)TestRoutes.Long(context, "calls");
                int count = (int)TestRoutes.Long(context, "factions");
                bool player = TestRoutes.Query(context, "kind", "player") == "player";
                long maxMs = TestRoutes.Long(context, "maxMs");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => Burst(identity, calls, count, player, maxMs))
                );
            }
        );
        // GET /faction-test-reputation-queue - the node's queued writes and the WA commands sent for them (null before SE-1280).
        TestRoutes.Add(
            "GET",
            "/faction-test-reputation-queue",
            context =>
                TestRoutes.Json(
                    context,
                    200,
                    new
                    {
                        queued = Counter("m_setCallsQueued"),
                        sent = Counter("m_setCommandsSent"),
                    }
                )
        );
    }

    private static object Burst(long identity, int calls, int count, bool player, long maxMs)
    {
        IMyFactionCollection api = MySession.Static.Factions;
        MyFaction[] factions = MySession
            .Static.Factions.Select(entry => entry.Value)
            .Where(faction =>
                MySession.Static.Factions.IsNpcFaction(faction.Tag) && !faction.StaticReputation
            )
            .OrderBy(faction => faction.FactionId)
            .Take(player ? count : count + 1)
            .ToArray();
        if (factions.Length < (player ? 1 : 2))
            throw new InvalidOperationException("Not enough NPC factions: " + factions.Length);
        MyFaction from = factions[0];
        MyFaction[] targets = player ? factions : factions.Skip(1).ToArray();
        int Read(MyFaction faction) =>
            player
                ? api.GetReputationBetweenPlayerAndFaction(identity, faction.FactionId)
                : api.GetReputationBetweenFactions(from.FactionId, faction.FactionId);
        void Set(MyFaction faction, int value)
        {
            if (player)
                api.SetReputationBetweenPlayerAndFaction(identity, faction.FactionId, value);
            else
                api.SetReputation(from.FactionId, faction.FactionId, value);
        }

        var watch = Stopwatch.StartNew();
        foreach (MyFaction faction in targets)
            Set(faction, 0);
        int done = 0;
        long slowestMs = 0;
        while (done < calls && watch.ElapsedMilliseconds < maxMs)
        {
            long started = watch.ElapsedMilliseconds;
            MyFaction faction = targets[done % targets.Length];
            Set(faction, Read(faction) - 1);
            slowestMs = Math.Max(slowestMs, watch.ElapsedMilliseconds - started);
            done++;
        }
        long elapsedMs = watch.ElapsedMilliseconds;
        var rows = targets
            .Select(
                (faction, index) =>
                    new
                    {
                        tag = faction.Tag,
                        factionId = faction.FactionId,
                        expected = -(
                            done / targets.Length + (index < done % targets.Length ? 1 : 0)
                        ),
                        local = Read(faction),
                    }
            )
            .ToArray();
        string kind = player ? "player" : "faction";
        Plugin.Log(
            $"reputation-burst kind={kind} calls={done}/{calls} factions={targets.Length} elapsedMs={elapsedMs} slowestMs={slowestMs} (SE-1280)"
        );
        return new
        {
            kind,
            from = from.Tag,
            identity,
            calls = done,
            elapsedMs,
            slowestMs,
            factions = rows,
        };
    }

    private static long? Counter(string name) =>
        (long?)
            typeof(MyFactionCollectionPatch)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);
}
