using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>One player's settings of record; see <see cref="PlayerProfileService"/>.</summary>
/// <remarks>
/// The toolbar travels as the XML of its <c>MyObjectBuilder_Toolbar</c> rather than as a structured copy:
/// the toolbar is a rich object builder (blocks, items, groups, PB arguments) whose vanilla serializer is
/// the only thing that reads and writes every case correctly, and the record is written straight back into
/// a checkpoint's <c>AllPlayersData</c> where the same serializer picks it up.
/// </remarks>
/// <param name="Seen">
/// SE-1870: Unix seconds (UTC) when a node last reported this camera for an entity it holds; the World
/// Authority stamps it. 0 = unknown, kept, but first to go when the player is over the camera cap.
/// </param>
public sealed record PlayerCameraSetting(
    long EntityId,
    double Distance,
    float HeadX,
    float HeadY,
    bool FirstPerson,
    long Seen = 0
);

/// <param name="AdminSettings">
/// The player's Alt-F10 flags as <c>AdminSettingsEnum</c> bits. Vanilla keys them by STEAM id alone
/// (<c>MySession.Static.RemoteAdminSettings</c>), so every serial of one client carries the same value here.
/// </param>
public sealed record PlayerProfile(
    ulong SteamId,
    int SerialId,
    long IdentityId,
    string ToolbarXml,
    float[] Colors,
    int SelectedColor,
    string ArmorSkin,
    bool CreativeTools,
    int AdminSettings,
    PlayerCameraSetting[] Cameras
);

public sealed record PlayerProfileReport(string Origin, PlayerProfile[] Entries);

public sealed record PlayerProfileKey(ulong SteamId, int SerialId);

/// <summary>
/// A full snapshot when <see cref="Profiles"/> is set, otherwise a delta (SE-1870): the profiles that changed
/// since the previous broadcast and the ones dropped from the record. A node running code from before the
/// delta format rejects a delta as an invalid snapshot and keeps the record it had.
/// </summary>
public sealed record PlayerProfileSnapshot(
    PlayerProfile[] Profiles,
    PlayerProfile[] Changed = null,
    PlayerProfileKey[] Removed = null
)
{
    public bool IsFull => Profiles != null;
}

public static class PlayerProfileCodec
{
    public const int MaxEntries = 4096;
    public const int MaxToolbarBytes = 256 * 1024;

    /// <summary>
    /// The most cameras a report or snapshot may carry per player. How many the World Authority keeps is the
    /// <c>profiles.maxCameras</c> config value, clamped to this.
    /// </summary>
    public const int MaxCameras = 256;
    public const int ColorSlotCount = 14;

    /// <summary>Every bit vanilla's <c>AdminSettingsEnum</c> defines; a report carrying anything else is junk.</summary>
    public const int AdminSettingsMask = 0x7F;

    public static string WriteReport(PlayerProfileReport report) =>
        JsonSerializer.Serialize(report);

    public static PlayerProfileReport ReadReport(string payload)
    {
        PlayerProfileReport report =
            JsonSerializer.Deserialize<PlayerProfileReport>(payload)
            ?? throw new InvalidOperationException("Invalid player profile report");
        if (
            string.IsNullOrEmpty(report.Origin)
            || report.Origin.Length > 128
            || report.Entries == null
            || report.Entries.Length > MaxEntries
            || report.Entries.Any(entry => !Valid(entry))
        )
            throw new InvalidOperationException("Invalid player profile report");
        return report;
    }

    public static string WriteSnapshot(IEnumerable<PlayerProfile> profiles) =>
        JsonSerializer.Serialize(
            new PlayerProfileSnapshot(
                profiles
                    .OrderBy(profile => profile.SteamId)
                    .ThenBy(profile => profile.SerialId)
                    .ToArray()
            )
        );

    public static PlayerProfileSnapshot ReadSnapshot(string payload)
    {
        if (payload == null || payload.Length > 32 * 1024 * 1024)
            throw new InvalidOperationException("Invalid player profile snapshot");
        PlayerProfileSnapshot snapshot =
            JsonSerializer.Deserialize<PlayerProfileSnapshot>(payload)
            ?? throw new InvalidOperationException("Invalid player profile snapshot");
        if (snapshot.Profiles != null)
        {
            if (snapshot.Profiles.Length > MaxEntries)
                throw new InvalidOperationException("Invalid player profile snapshot");
            return new PlayerProfileSnapshot(snapshot.Profiles.Where(Valid).ToArray());
        }
        if (
            snapshot.Changed == null
            || snapshot.Changed.Length > MaxEntries
            || snapshot.Removed?.Length > MaxEntries
        )
            throw new InvalidOperationException("Invalid player profile snapshot");
        return new PlayerProfileSnapshot(
            null,
            snapshot.Changed.Where(Valid).ToArray(),
            snapshot.Removed?.Where(key => key != null).ToArray() ?? Array.Empty<PlayerProfileKey>()
        );
    }

    /// <summary>
    /// The delta to broadcast for the <paramref name="touched"/> keys (<see cref="GlobalStateDelta{TKey,TValue}"/>):
    /// the profiles whose JSON differs from the last broadcast, and the ones no longer in the record. Null when
    /// nothing changed. Read back by <see cref="ReadSnapshot"/>, whose record has the same Changed and Removed.
    /// </summary>
    public static string TakeDelta(
        Dictionary<(ulong SteamId, int SerialId), PlayerProfile> record,
        IEnumerable<(ulong SteamId, int SerialId)> touched,
        GlobalStateDelta<PlayerProfileKey, PlayerProfile> published
    ) =>
        published.Take(
            touched.Select(key => new PlayerProfileKey(key.SteamId, key.SerialId)),
            key => record.GetValueOrDefault((key.SteamId, key.SerialId))
        );

    /// <summary>A node's copy of the record: a full snapshot replaces it, a delta patches it.</summary>
    public static void Apply(
        Dictionary<(ulong SteamId, int SerialId), PlayerProfile> record,
        PlayerProfileSnapshot snapshot
    )
    {
        if (snapshot.IsFull)
            record.Clear();
        foreach (PlayerProfile profile in snapshot.Profiles ?? snapshot.Changed)
            record[(profile.SteamId, profile.SerialId)] = profile;
        foreach (PlayerProfileKey key in snapshot.Removed ?? Array.Empty<PlayerProfileKey>())
            record.Remove((key.SteamId, key.SerialId));
    }

    public static bool Valid(PlayerProfile profile) =>
        profile != null
        && profile.SteamId != 0
        && profile.SerialId >= 0
        && profile.IdentityId > 0
        && (profile.ToolbarXml == null || profile.ToolbarXml.Length <= MaxToolbarBytes)
        && (profile.Colors == null || profile.Colors.Length == ColorSlotCount * 3)
        && (profile.ArmorSkin == null || profile.ArmorSkin.Length <= 256)
        && (profile.AdminSettings & ~AdminSettingsMask) == 0
        && (profile.Cameras == null || profile.Cameras.Length <= MaxCameras);

    /// <summary>
    /// Fold node reports into the record. Three rules, and every one of them is a defect that was reasoned
    /// about rather than a preference:
    ///
    /// * <b>Sticky.</b> A player nobody reports keeps their record - that is the whole point, a player
    ///   offline for a week still has their toolbar.
    /// * <b>Freshest report wins.</b> Reports arrive per node and must be folded OLDEST FIRST. A node that
    ///   has just lost a player to a handover keeps listing them until its next report, and folding in name
    ///   order would let that stale copy overwrite the new host's.
    /// * <b>Cameras union.</b> They are per entity, a node holds a fraction of the cluster's entities, and
    ///   vanilla's <c>SaveCameraCollection</c> deletes the settings whose entity is not local - so a report
    ///   is an update to the camera set, never its replacement.
    ///
    /// SE-1870: the union alone only ever grew (every respawn is a new character entity), up to the cap,
    /// and the cap then kept the LOWEST entity ids, dropping the newest cameras. Cameras now carry when a
    /// node last reported them (<see cref="PlayerCameraSetting.Seen"/>); a camera not seen for
    /// <paramref name="cameraRetentionSeconds"/> is dropped, and a player keeps at most
    /// <paramref name="maxCameras"/>, the most recently seen.
    /// </summary>
    /// <param name="record">The record so far; mutated in place.</param>
    /// <param name="reportsOldestFirst">Each origin's latest report, ordered by when it was received.</param>
    /// <param name="now">Unix seconds (UTC).</param>
    /// <param name="cameraRetentionSeconds">0 or less: cameras never expire by age.</param>
    /// <returns>The keys whose profile may have changed: reported, pruned or trimmed.</returns>
    public static HashSet<(ulong SteamId, int SerialId)> Merge(
        Dictionary<(ulong SteamId, int SerialId), PlayerProfile> record,
        IEnumerable<PlayerProfile[]> reportsOldestFirst,
        long now,
        long cameraRetentionSeconds,
        int maxCameras
    )
    {
        var touched = new HashSet<(ulong SteamId, int SerialId)>();
        maxCameras = Math.Clamp(maxCameras, 0, MaxCameras);
        foreach (PlayerProfile[] entries in reportsOldestFirst)
        foreach (PlayerProfile profile in entries ?? Array.Empty<PlayerProfile>())
        {
            if (!Valid(profile))
                continue;
            (ulong, int) key = (profile.SteamId, profile.SerialId);
            touched.Add(key);
            record.TryGetValue(key, out PlayerProfile stored);
            record[key] = profile with
            {
                Cameras = PruneCameras(
                    UnionCameras(
                        stored?.Cameras,
                        profile.Cameras,
                        now,
                        cameraRetentionSeconds,
                        profile.SteamId
                    ),
                    now,
                    cameraRetentionSeconds,
                    maxCameras
                ),
            };
        }
        // Players nobody reports age too, so an offline player's cameras expire once, as a small change.
        foreach ((ulong, int) key in record.Keys.ToArray())
        {
            PlayerCameraSetting[] cameras = record[key].Cameras;
            if (!NeedsPruning(cameras, now, cameraRetentionSeconds, maxCameras))
                continue;
            record[key] = record[key] with
            {
                Cameras = PruneCameras(cameras, now, cameraRetentionSeconds, maxCameras),
            };
            touched.Add(key);
        }
        if (record.Count > MaxEntries)
            foreach ((ulong, int) key in record.Keys.Take(record.Count - MaxEntries).ToArray())
            {
                record.Remove(key);
                touched.Add(key);
            }
        return touched;
    }

    /// <summary>
    /// Stored union reported, the reporting node's copy winning per entity and stamped as seen now, on a grid of
    /// the refresh interval: a node reporting the same cameras every second changes the record (and causes a
    /// broadcast) once per interval, not every second. The grid also makes every camera of one report tie, so a
    /// player reporting more than the cap keeps the same ones: stamped one by one, the cameras the cap dropped
    /// came back newer than the kept ones and the record flipped on every merge (bench, 2026-11-22). The grid's
    /// phase is per player (<paramref name="phase"/>, the Steam id), so the players online do not all restamp,
    /// and get broadcast, in the same second of the hour.
    /// </summary>
    public static PlayerCameraSetting[] UnionCameras(
        PlayerCameraSetting[] stored,
        PlayerCameraSetting[] reported,
        long now,
        long cameraRetentionSeconds,
        ulong phase = 0
    )
    {
        long refresh = SeenRefreshSeconds(cameraRetentionSeconds);
        long offset = (long)(phase % (ulong)refresh);
        long seen = now - ((now - offset) % refresh + refresh) % refresh;
        Dictionary<long, PlayerCameraSetting> union =
            stored?.ToDictionary(camera => camera.EntityId)
            ?? new Dictionary<long, PlayerCameraSetting>();
        foreach (PlayerCameraSetting camera in reported ?? Array.Empty<PlayerCameraSetting>())
            union[camera.EntityId] = camera with { Seen = seen };
        return union.Values.OrderBy(camera => camera.EntityId).ToArray();
    }

    private static long SeenRefreshSeconds(long cameraRetentionSeconds) =>
        cameraRetentionSeconds > 0 ? Math.Clamp(cameraRetentionSeconds / 4, 1, 3600) : 3600;

    private static bool Expired(PlayerCameraSetting camera, long now, long retention) =>
        retention > 0 && camera.Seen > 0 && now - camera.Seen > retention;

    private static bool NeedsPruning(
        PlayerCameraSetting[] cameras,
        long now,
        long retention,
        int maxCameras
    ) =>
        cameras != null
        && (cameras.Length > maxCameras || cameras.Any(camera => Expired(camera, now, retention)));

    /// <summary>Drop expired cameras, then keep the most recently seen <paramref name="maxCameras"/>, ordered by entity.</summary>
    public static PlayerCameraSetting[] PruneCameras(
        PlayerCameraSetting[] cameras,
        long now,
        long cameraRetentionSeconds,
        int maxCameras
    )
    {
        if (cameras == null)
            return Array.Empty<PlayerCameraSetting>();
        return cameras
            .Where(camera => !Expired(camera, now, cameraRetentionSeconds))
            .OrderByDescending(camera => camera.Seen)
            .ThenByDescending(camera => camera.EntityId)
            .Take(Math.Clamp(maxCameras, 0, MaxCameras))
            .OrderBy(camera => camera.EntityId)
            .ToArray();
    }
}
