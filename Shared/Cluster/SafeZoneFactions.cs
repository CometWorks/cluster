using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-1997: a safe zone's faction list survives the faction collection being rebuilt and factions arriving late.
/// <para>
/// Vanilla keeps the list as <see cref="MyFaction"/> objects and matches them by reference
/// (<c>MySafeZone.IsSafe</c>, <c>IsSubGridSafe</c>). The World Authority's faction snapshot rebuilds every faction
/// object of a node (<c>MyFactionCollectionPatch.ReplaceLocally</c>), so after it no zone's faction rule matched
/// anyone until the zone was created again. <see cref="Rebind"/> points every list at the current objects.
/// </para>
/// <para>
/// Vanilla also drops a faction id it cannot resolve when the zone is initialized, and the next save then loses
/// it for good. The ids are kept here instead, written back into the zone's builder, and put on the list once
/// the node knows the faction.
/// </para>
/// </summary>
internal static class SafeZoneFactions
{
    private static readonly ConditionalWeakTable<MySafeZone, HashSet<long>> Waiting =
        new ConditionalWeakTable<MySafeZone, HashSet<long>>();

    /// <summary>Zones that may wait for a faction: an upper bound, exact again after each <see cref="Rebind"/>.</summary>
    private static int m_waitingZones;
    private static bool m_rebindQueued;

    /// <summary>MySafeZone.InitInternal postfix: remember the faction ids vanilla just dropped.</summary>
    internal static void Initialized(MySafeZone zone, MyObjectBuilder_SafeZone builder)
    {
        MyFactionCollection factions = MySession.Static?.Factions;
        if (!Sync.IsServer || builder?.Factions == null || factions == null)
            return;
        HashSet<long> missing = null;
        foreach (long id in builder.Factions)
            if (factions.TryGetFactionById(id) == null)
                (missing ??= new HashSet<long>()).Add(id);
        Waiting.Remove(zone);
        if (missing == null)
            return;
        Waiting.Add(zone, missing);
        m_waitingZones++;
        MyLog.Default?.WriteLineAndConsole(
            "[ClusterPlugin] safe zone waits for factions zone="
                + zone.EntityId
                + " block="
                + builder.SafeZoneBlockId
                + " factions="
                + string.Join(",", missing)
        );
    }

    /// <summary>MySafeZone.GetObjectBuilder postfix, any thread: the builder keeps the ids the zone waits for.</summary>
    internal static void Saved(MySafeZone zone, MyObjectBuilder_SafeZone builder)
    {
        if (m_waitingZones == 0 || builder == null || !Waiting.TryGetValue(zone, out var ids))
            return;
        builder.Factions = (builder.Factions ?? new long[0]).Union(ids).ToArray();
    }

    /// <summary>MyFactionCollection.Add postfix: a faction arrived, so a waiting zone may get it.</summary>
    internal static void FactionAdded()
    {
        if (m_waitingZones == 0 || m_rebindQueued || MySandboxGame.Static == null)
            return;
        m_rebindQueued = true;
        MySandboxGame.Static.Invoke(Rebind, "SE-1997 safe zone factions");
    }

    /// <summary>Game thread: every zone's list holds the current object of each faction, and the waiting ids
    /// that resolve now. Lists and id sets are replaced as a whole, never changed in place: IsSafe and a save
    /// on another thread read them.</summary>
    internal static void Rebind()
    {
        m_rebindQueued = false;
        MyFactionCollection factions = MySession.Static?.Factions;
        if (!Sync.IsServer || factions == null)
            return;
        int waitingZones = 0;
        foreach (MySafeZone zone in MySessionComponentSafeZones.SafeZones)
        {
            Waiting.TryGetValue(zone, out HashSet<long> waiting);
            if (zone.Factions.Count == 0 && waiting == null)
                continue;
            IEnumerable<long> ids = zone.Factions.Select(faction => faction.FactionId);
            if (waiting != null)
                ids = ids.Concat(waiting);
            var current = new List<MyFaction>();
            HashSet<long> missing = null;
            foreach (long id in ids.Distinct().ToList())
                if (factions.TryGetFactionById(id) is MyFaction faction)
                    current.Add(faction);
                else
                    (missing ??= new HashSet<long>()).Add(id);
            zone.Factions = current;
            Waiting.Remove(zone);
            if (missing == null)
                continue;
            Waiting.Add(zone, missing);
            waitingZones++;
        }
        m_waitingZones = waitingZones;
    }

    /// <summary>Whether every faction on the zone's list is the object the faction collection holds now.</summary>
    internal static bool Current(MySafeZone zone)
    {
        MyFactionCollection factions = MySession.Static?.Factions;
        if (factions == null)
            return true;
        foreach (MyFaction faction in zone.Factions)
            if (!ReferenceEquals(factions.TryGetFactionById(faction.FactionId), faction))
                return false;
        return true;
    }
}
