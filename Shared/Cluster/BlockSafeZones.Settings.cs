using System;
using System.Globalization;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Network;
using VRage.Utils;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// SE-1997: what the players set on a block's safe zone, kept apart from the zone, to detect settings that
/// changed without a player changing them.
/// <para>
/// The block's builders carry the settings as a second <see cref="BlockBuilderEntry"/> (<see cref="SettingsKey"/>),
/// so they pass through handover, partition saves, offline partitions, recovery and a keep-world deploy. Unlike the
/// zone entry and vanilla's <c>SafeZoneOb</c>, which are rewritten from the live zone on every save, this one changes
/// only when a client's request changed the zone (<see cref="Editing"/>, <see cref="Edited"/>). A block that has none
/// yet takes the live zone's settings.
/// </para>
/// <para>
/// The live zone is compared with it when the block is saved, after the zone was restored or created again, and
/// after a faction snapshot. A difference is logged once per node as <c>safe zone settings drift</c> and the entry
/// is left alone. The enabled state is not part of it: a zone that ran out of zone chips or power is not drift.
/// </para>
/// </summary>
internal static partial class BlockSafeZones
{
    internal static readonly Guid SettingsKey = new Guid("5c1a1997-5afe-4a2e-9d0e-b10c25afe200");

    private const char FieldSeparator = ';';

    /// <summary>The settings a player can change, as <c>name=value</c> fields in a fixed order.</summary>
    internal static string Settings(MyObjectBuilder_SafeZone zone)
    {
        var color = new Color((Vector3)zone.ModelColor);
        return string.Join(
            FieldSeparator.ToString(),
            "players=" + zone.AccessTypePlayers,
            "factions=" + zone.AccessTypeFactions,
            "grids=" + zone.AccessTypeGrids,
            "floating=" + zone.AccessTypeFloatingObjects,
            "actions=" + zone.AllowedActions,
            "shape=" + zone.Shape,
            "radius=" + Number(zone.Radius),
            "size=" + Number(zone.Size.X) + "," + Number(zone.Size.Y) + "," + Number(zone.Size.Z),
            "color=" + color.R + "," + color.G + "," + color.B,
            "texture=" + zone.Texture,
            "visible=" + zone.IsVisible,
            "playerList=" + Ids(zone.Players),
            "factionList=" + Ids(zone.Factions),
            "gridList=" + Ids(zone.Entities)
        );
    }

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Ids(long[] ids) =>
        ids == null ? "" : string.Join(",", ids.Distinct().OrderBy(id => id));

    /// <summary>The fields that differ, as <c>name: was -> is</c>.</summary>
    internal static string SettingsDiff(string was, string now)
    {
        string[] before = was.Split(FieldSeparator);
        string[] after = now.Split(FieldSeparator);
        if (before.Length != after.Length)
            return "settings: " + was + " -> " + now;
        return string.Join(
            " | ",
            before
                .Zip(after, (a, b) => (a, b))
                .Where(pair => pair.a != pair.b)
                .Select(pair =>
                {
                    int split = pair.a.IndexOf('=');
                    return pair.a.Substring(0, split)
                        + ": "
                        + pair.a.Substring(split + 1)
                        + " -> "
                        + pair.b.Substring(pair.b.IndexOf('=') + 1);
                })
        );
    }

    /// <summary>The kept settings with the fields a request changed (from <paramref name="before"/> to
    /// <paramref name="after"/>) taken over. A field that drifted before the request stays as the player set it.</summary>
    internal static string SettingsMerge(string kept, string before, string after)
    {
        string[] keptFields = kept.Split(FieldSeparator);
        string[] beforeFields = before.Split(FieldSeparator);
        string[] afterFields = after.Split(FieldSeparator);
        if (keptFields.Length != afterFields.Length || beforeFields.Length != afterFields.Length)
            return after;
        for (int i = 0; i < afterFields.Length; i++)
            if (beforeFields[i] != afterFields[i])
                keptFields[i] = afterFields[i];
        return string.Join(FieldSeparator.ToString(), keptFields);
    }

    private static MySafeZoneBlock BlockOf(long zoneId, out MySafeZone zone)
    {
        zone = null;
        return
            Active
            && OnGameThread
            && MyEntities.TryGetEntityById(zoneId, out zone, false)
            && zone.SafeZoneBlockId != 0
            && MyEntities.TryGetEntityById(zone.SafeZoneBlockId, out MySafeZoneBlock block, false)
            ? block
            : null;
    }

    /// <summary>Prefix of the server handlers of a client's zone update: the zone's settings before the request.</summary>
    internal static string Editing(long zoneId)
    {
        MySafeZoneBlock block = BlockOf(zoneId, out MySafeZone zone);
        if (block == null)
            return null;
        var builder = (MyObjectBuilder_SafeZone)zone.GetObjectBuilder();
        // Seeds the entry, and reports drift that was there before the player touched the zone.
        CheckSettings(block, zone, builder, "edit");
        return Settings(builder);
    }

    /// <summary>Postfix of the same handlers: what the request changed is what the player set.</summary>
    internal static void Edited(long zoneId, string before)
    {
        if (before == null)
            return;
        MySafeZoneBlock block = BlockOf(zoneId, out MySafeZone zone);
        if (block == null || !States.TryGetValue(block, out State state))
            return;
        string after = Settings((MyObjectBuilder_SafeZone)zone.GetObjectBuilder());
        if (after == before)
            return;
        state.Settings = SettingsMerge(state.Settings ?? after, before, after);
        if (state.Settings == after)
            state.LoggedDrift = null;
    }

    /// <summary>Postfix of vanilla's zone creation (block working again, terminal on/off): the zone has the settings
    /// vanilla kept for it, also where its own re-apply was skipped, then it is compared with what the players set.</summary>
    internal static void Created(MySafeZoneComponent component)
    {
        MySafeZoneBlock block = component.m_parentBlock;
        if (!Active || !OnGameThread || block == null)
            return;
        long zoneId = component.SafeZoneEntityId;
        MySafeZone zone = zoneId == 0 ? null : Zone(block, zoneId);
        if (zone == null)
            return;
        var live = (MyObjectBuilder_SafeZone)zone.GetObjectBuilder();
        MyObjectBuilder_SafeZone kept = component.m_obSafezoneWhenDisabled;
        if (kept != null)
        {
            string was = Settings(live);
            string wanted = Settings(kept);
            if (was != wanted)
            {
                // MySessionComponentSafeZones.UpdateSafeZone returns without a word when the zone would collide
                // with another one, and the new zone then stays on vanilla's defaults: a whitelist with nobody on it.
                var apply = (MyObjectBuilder_SafeZone)kept.Clone();
                apply.EntityId = zoneId;
                apply.SafeZoneBlockId = block.EntityId;
                apply.Enabled = zone.Enabled;
                apply.PositionAndOrientation = null;
                zone.InitInternal(apply);
                MyMultiplayer.RaiseStaticEvent(
                    (IMyEventOwner x) => MySessionComponentSafeZones.UpdateSafeZone_Broadcast,
                    apply
                );
                MyLog.Default?.WriteLineAndConsole(
                    "[ClusterPlugin] safe zone settings reapplied block="
                        + block.EntityId
                        + " zone="
                        + zoneId
                        + " fields="
                        + SettingsDiff(was, wanted)
                );
                live = (MyObjectBuilder_SafeZone)zone.GetObjectBuilder();
            }
        }
        CheckSettings(block, zone, live, "create");
    }

    /// <summary>After a faction snapshot replaced the faction collection: every block zone of this node.</summary>
    internal static void CheckAllSettings(string at)
    {
        if (!Active || !OnGameThread)
            return;
        foreach (MySafeZone zone in MySessionComponentSafeZones.SafeZones)
            if (
                zone.SafeZoneBlockId != 0
                && !zone.MarkedForClose
                && MyEntities.TryGetEntityById(
                    zone.SafeZoneBlockId,
                    out MySafeZoneBlock block,
                    false
                )
            )
                CheckSettings(block, zone, (MyObjectBuilder_SafeZone)zone.GetObjectBuilder(), at);
    }

    /// <summary>Game thread: compare the live zone with what the players set, and log a difference once.</summary>
    private static void CheckSettings(
        MySafeZoneBlock block,
        MySafeZone zone,
        MyObjectBuilder_SafeZone builder,
        string at
    )
    {
        State state = States.GetValue(block, _ => new State());
        string live = Settings(builder);
        // The list holds objects the faction collection no longer has: no faction rule matches then.
        bool stale = !SafeZoneFactions.Current(zone);
        if (!stale)
            state.Settings ??= live;
        string drift = live + (stale ? "!" : "");
        if (drift == state.Settings)
        {
            state.LoggedDrift = null;
            return;
        }
        if (drift == state.LoggedDrift)
            return;
        state.LoggedDrift = drift;
        MyLog.Default?.WriteLineAndConsole(
            "[ClusterPlugin] safe zone settings drift block="
                + block.EntityId
                + " zone="
                + zone.EntityId
                + " grid="
                + block.CubeGrid?.EntityId
                + " owner="
                + block.OwnerId
                + " at="
                + at
                + " fields="
                + string.Join(
                    " | ",
                    new[]
                    {
                        state.Settings == null || state.Settings == live
                            ? null
                            : SettingsDiff(state.Settings, live),
                        stale ? "factionObjects: stale" : null,
                    }.Where(field => field != null)
                )
        );
    }
}
