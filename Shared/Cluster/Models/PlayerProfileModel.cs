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
public sealed record PlayerCameraSetting(long EntityId, double Distance, float HeadX, float HeadY, bool FirstPerson);

/// <param name="AdminSettings">
/// The player's Alt-F10 flags as <c>AdminSettingsEnum</c> bits. Vanilla keys them by STEAM id alone
/// (<c>MySession.Static.RemoteAdminSettings</c>), so every serial of one client carries the same value here.
/// </param>
public sealed record PlayerProfile(ulong SteamId, int SerialId, long IdentityId, string ToolbarXml,
	float[] Colors, int SelectedColor, string ArmorSkin, bool CreativeTools, int AdminSettings,
	PlayerCameraSetting[] Cameras);

public sealed record PlayerProfileReport(string Origin, PlayerProfile[] Entries);

public sealed record PlayerProfileSnapshot(PlayerProfile[] Profiles);

public static class PlayerProfileCodec
{
	public const int MaxEntries = 4096;
	public const int MaxToolbarBytes = 256 * 1024;
	public const int MaxCameras = 256;
	public const int ColorSlotCount = 14;
	/// <summary>Every bit vanilla's <c>AdminSettingsEnum</c> defines; a report carrying anything else is junk.</summary>
	public const int AdminSettingsMask = 0x7F;

	public static string WriteReport(PlayerProfileReport report) => JsonSerializer.Serialize(report);

	public static PlayerProfileReport ReadReport(string payload)
	{
		PlayerProfileReport report = JsonSerializer.Deserialize<PlayerProfileReport>(payload)
			?? throw new InvalidOperationException("Invalid player profile report");
		if (string.IsNullOrEmpty(report.Origin) || report.Origin.Length > 128 || report.Entries == null
			|| report.Entries.Length > MaxEntries || report.Entries.Any(entry => !Valid(entry)))
			throw new InvalidOperationException("Invalid player profile report");
		return report;
	}

	public static string WriteSnapshot(IEnumerable<PlayerProfile> profiles)
		=> JsonSerializer.Serialize(new PlayerProfileSnapshot(profiles
			.OrderBy(profile => profile.SteamId).ThenBy(profile => profile.SerialId).ToArray()));

	public static PlayerProfileSnapshot ReadSnapshot(string payload)
	{
		if (payload == null || payload.Length > 32 * 1024 * 1024)
			throw new InvalidOperationException("Invalid player profile snapshot");
		PlayerProfileSnapshot snapshot = JsonSerializer.Deserialize<PlayerProfileSnapshot>(payload)
			?? throw new InvalidOperationException("Invalid player profile snapshot");
		if (snapshot.Profiles == null || snapshot.Profiles.Length > MaxEntries)
			throw new InvalidOperationException("Invalid player profile snapshot");
		return snapshot with { Profiles = snapshot.Profiles.Where(Valid).ToArray() };
	}

	public static bool Valid(PlayerProfile profile)
		=> profile != null && profile.SteamId != 0 && profile.SerialId >= 0 && profile.IdentityId > 0
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
	/// </summary>
	/// <param name="record">The record so far; mutated in place, and returned for convenience.</param>
	/// <param name="reportsOldestFirst">Each origin's latest report, ordered by when it was received.</param>
	public static Dictionary<(ulong SteamId, int SerialId), PlayerProfile> Merge(
		Dictionary<(ulong SteamId, int SerialId), PlayerProfile> record,
		IEnumerable<PlayerProfile[]> reportsOldestFirst)
	{
		foreach (PlayerProfile[] entries in reportsOldestFirst)
			foreach (PlayerProfile profile in entries ?? Array.Empty<PlayerProfile>())
			{
				if (!Valid(profile)) continue;
				(ulong, int) key = (profile.SteamId, profile.SerialId);
				record[key] = record.TryGetValue(key, out PlayerProfile stored)
					? profile with { Cameras = UnionCameras(stored.Cameras, profile.Cameras) }
					: profile;
			}
		if (record.Count > MaxEntries)
			foreach ((ulong, int) key in record.Keys.Take(record.Count - MaxEntries).ToArray())
				record.Remove(key);
		return record;
	}

	/// <summary>Stored union reported, the reporting node's copy winning per entity.</summary>
	public static PlayerCameraSetting[] UnionCameras(PlayerCameraSetting[] stored, PlayerCameraSetting[] reported)
	{
		if (stored == null || stored.Length == 0) return reported ?? Array.Empty<PlayerCameraSetting>();
		Dictionary<long, PlayerCameraSetting> union = stored.ToDictionary(camera => camera.EntityId);
		foreach (PlayerCameraSetting camera in reported ?? Array.Empty<PlayerCameraSetting>())
			union[camera.EntityId] = camera;
		return union.Values.OrderBy(camera => camera.EntityId).Take(MaxCameras).ToArray();
	}
}
