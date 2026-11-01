using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

public sealed record GpsAuthorityEntry(
	long IdentityId,
	int Hash,
	string Name,
	string DisplayName,
	string Description,
	double X,
	double Y,
	double Z,
	bool IsFinal,
	bool ShowOnHud,
	bool AlwaysVisible,
	uint Color,
	long EntityId,
	bool IsContainerGps,
	bool IsObjective,
	long ContractId,
	bool IsGlobalEncounterGps);

public sealed record GpsAuthoritySnapshot(GpsAuthorityEntry[] Entries);

public sealed record GpsAuthorityRequest(string Operation, GpsAuthorityEntry Entry, int Hash, bool Value, uint Color);

public static class GpsAuthorityCodec
{
	private static readonly HashSet<string> Operations = new HashSet<string>(StringComparer.Ordinal)
	{
		"Add", "Delete", "Modify", "ShowOnHud", "AlwaysVisible", "Color"
	};

	public static string WriteSnapshot(IEnumerable<GpsAuthorityEntry> entries)
		=> JsonSerializer.Serialize(new GpsAuthoritySnapshot(entries.OrderBy(x => x.IdentityId).ThenBy(x => x.Hash).ToArray()));

	/// <summary>
	/// #1405: one identity's entries (already in hash order) as the comma-separated JSON they take inside
	/// <see cref="WriteSnapshot(IEnumerable{GpsAuthorityEntry})"/>'s array, so the WA serializes only identities that changed.
	/// </summary>
	public static string WriteEntries(IEnumerable<GpsAuthorityEntry> entries) => string.Join(",", entries.Select(WriteEntry));

	/// <summary>#1405: one entry's JSON as it appears inside the snapshot's array.</summary>
	public static string WriteEntry(GpsAuthorityEntry entry) => JsonSerializer.Serialize(entry);

	/// <summary>#1405: a snapshot from <see cref="WriteEntries"/> parts in identity order; the same text as WriteSnapshot of those entries.</summary>
	public static string WriteSnapshotFromParts(IEnumerable<string> entryParts)
		=> "{\"Entries\":[" + string.Join(",", entryParts.Where(x => x.Length > 0)) + "]}";

	public static GpsAuthoritySnapshot ReadSnapshot(string payload)
	{
		GpsAuthoritySnapshot snapshot = JsonSerializer.Deserialize<GpsAuthoritySnapshot>(payload)
			?? throw new InvalidOperationException("Invalid GPS snapshot");
		if (snapshot.Entries == null || snapshot.Entries.Length > 100000 || snapshot.Entries.Any(x => x == null || !Valid(x)))
			throw new InvalidOperationException("Invalid GPS snapshot entries");
		if (snapshot.Entries.Select(x => (x.IdentityId, x.Hash)).Distinct().Count() != snapshot.Entries.Length)
			throw new InvalidOperationException("Duplicate GPS snapshot entry");
		return snapshot;
	}

	public static string WriteRequest(GpsAuthorityRequest request) => JsonSerializer.Serialize(request);

	public static GpsAuthorityRequest ReadRequest(string payload)
	{
		GpsAuthorityRequest request = JsonSerializer.Deserialize<GpsAuthorityRequest>(payload)
			?? throw new InvalidOperationException("Invalid GPS request");
		if (!Operations.Contains(request.Operation) || request.Entry == null || !Valid(request.Entry))
			throw new InvalidOperationException("Invalid GPS request fields");
		return request;
	}

	private static bool Valid(GpsAuthorityEntry entry)
		=> entry.IdentityId >= 0 && double.IsFinite(entry.X) && double.IsFinite(entry.Y) && double.IsFinite(entry.Z)
			&& (entry.Name?.Length ?? 0) <= 256 && (entry.DisplayName?.Length ?? 0) <= 256
			&& (entry.Description?.Length ?? 0) <= 65536;
}
