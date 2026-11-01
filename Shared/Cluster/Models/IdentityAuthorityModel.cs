using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

public sealed record IdentityAuthorityRequest(string Kind, string Name, string Model, bool HasColor,
	float ColorX, float ColorY, float ColorZ, bool InitialPlayer, bool AddToNpcs);
public sealed record IdentityAuthorityEntry(long IdentityId, string Name, string Model, bool HasColor,
	float ColorX, float ColorY, float ColorZ, bool IsNpc);
public sealed record PlayerBindingRequest(string ServiceName, ulong UserId, ulong OwnerId, int SerialId, string Name, bool IsAdmin,
	long ExpectedIdentityId = 0);
public sealed record PlayerBindingEntry(ulong UserId, int SerialId, long IdentityId);
public sealed record PlayerRebindRequest(ulong UserId, int SerialId, long PreviousIdentityId, long IdentityId);
public sealed record PlayerReady(long AuthorityGeneration, string ServiceName, ulong UserId, ulong OwnerId, int SerialId,
	bool IsAdmin, IdentityAuthorityEntry Identity);
public sealed record IdentityAuthoritySnapshot(IdentityAuthorityEntry[] Identities, PlayerBindingEntry[] Bindings);

public static class IdentityAuthorityCodec
{
	public static string WriteRequest(IdentityAuthorityRequest request) => JsonSerializer.Serialize(request);
	public static IdentityAuthorityRequest ReadRequest(string payload) => Validate(JsonSerializer.Deserialize<IdentityAuthorityRequest>(payload)
		?? throw new InvalidOperationException("Invalid identity request"));
	public static string WriteEntry(IdentityAuthorityEntry entry) => JsonSerializer.Serialize(entry);
	public static IdentityAuthorityEntry ReadEntry(string payload) => Validate(JsonSerializer.Deserialize<IdentityAuthorityEntry>(payload)
		?? throw new InvalidOperationException("Invalid identity entry"));
	public static string WriteBindingRequest(PlayerBindingRequest request) => JsonSerializer.Serialize(Validate(request));
	public static PlayerBindingRequest ReadBindingRequest(string payload) => Validate(JsonSerializer.Deserialize<PlayerBindingRequest>(payload)
		?? throw new InvalidOperationException("Invalid player binding request"));
	public static string WriteReady(PlayerReady ready) => JsonSerializer.Serialize(Validate(ready));
	public static PlayerReady ReadReady(string payload) => Validate(JsonSerializer.Deserialize<PlayerReady>(payload)
		?? throw new InvalidOperationException("Invalid player binding response"));
	public static string WriteRebindRequest(PlayerRebindRequest request) => JsonSerializer.Serialize(Validate(request));
	public static PlayerRebindRequest ReadRebindRequest(string payload) => Validate(JsonSerializer.Deserialize<PlayerRebindRequest>(payload)
		?? throw new InvalidOperationException("Invalid player rebind request"));
	public static string WriteSnapshot(IEnumerable<IdentityAuthorityEntry> entries)
		=> WriteSnapshot(entries, Array.Empty<PlayerBindingEntry>());
	public static string WriteSnapshot(IEnumerable<IdentityAuthorityEntry> entries, IEnumerable<PlayerBindingEntry> bindings)
		=> JsonSerializer.Serialize(new IdentityAuthoritySnapshot(entries.OrderBy(x => x.IdentityId).ToArray(),
			bindings.OrderBy(x => x.UserId).ThenBy(x => x.SerialId).ToArray()));

	public static IdentityAuthoritySnapshot ReadSnapshot(string payload)
	{
		IdentityAuthoritySnapshot snapshot = JsonSerializer.Deserialize<IdentityAuthoritySnapshot>(payload)
			?? throw new InvalidOperationException("Invalid identity snapshot");
		if (snapshot.Identities == null || snapshot.Bindings == null || snapshot.Identities.Length > 16384 || snapshot.Bindings.Length > 16384)
			throw new InvalidOperationException("Invalid identity snapshot");
		foreach (IdentityAuthorityEntry entry in snapshot.Identities) Validate(entry);
		foreach (PlayerBindingEntry binding in snapshot.Bindings) Validate(binding);
		if (snapshot.Identities.Select(x => x.IdentityId).Distinct().Count() != snapshot.Identities.Length)
			throw new InvalidOperationException("Duplicate identity");
		if (snapshot.Bindings.Select(x => (x.UserId, x.SerialId)).Distinct().Count() != snapshot.Bindings.Length
			|| snapshot.Bindings.Select(x => x.IdentityId).Distinct().Count() != snapshot.Bindings.Length)
			throw new InvalidOperationException("Duplicate player binding");
		if (snapshot.Bindings.Any(x => !snapshot.Identities.Any(identity => identity.IdentityId == x.IdentityId)))
			throw new InvalidOperationException("Player binding references an unknown identity");
		return snapshot;
	}

	private static IdentityAuthorityRequest Validate(IdentityAuthorityRequest request)
	{
		if (request.Kind is not ("Player" or "Npc") || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 256
			|| request.Model?.Length > 256 || !ValidColor(request.HasColor, request.ColorX, request.ColorY, request.ColorZ))
			throw new InvalidOperationException("Invalid identity request");
		return request;
	}

	private static IdentityAuthorityEntry Validate(IdentityAuthorityEntry entry)
	{
		if (entry.IdentityId <= 0 || entry.Name == null || entry.Name.Length > 256 || entry.Model?.Length > 256
			|| !ValidColor(entry.HasColor, entry.ColorX, entry.ColorY, entry.ColorZ))
			throw new InvalidOperationException("Invalid identity entry");
		return entry;
	}

	private static PlayerBindingRequest Validate(PlayerBindingRequest request)
	{
		// Gateway.md §2.7: Steam and EOS clients are distinguished by the ServiceName they carry;
		// DirectTransport is the debug frontend's name.
		if (request.ServiceName is not ("DirectTransport" or "Steam" or "EOS")
			|| request.UserId == 0 || request.UserId == ulong.MaxValue
			|| request.OwnerId == 0 || request.SerialId < 0 || request.Name == null || request.Name.Length > 256
			|| request.ExpectedIdentityId < 0)
			throw new InvalidOperationException("Invalid player binding request");
		return request;
	}

	private static PlayerBindingEntry Validate(PlayerBindingEntry entry)
	{
		if (entry.UserId == 0 || entry.UserId == ulong.MaxValue || entry.SerialId < 0 || entry.IdentityId <= 0)
			throw new InvalidOperationException("Invalid player binding");
		return entry;
	}

	private static PlayerRebindRequest Validate(PlayerRebindRequest request)
	{
		if (request.UserId == 0 || request.UserId == ulong.MaxValue || request.SerialId < 0
			|| request.PreviousIdentityId <= 0 || request.IdentityId <= 0 || request.PreviousIdentityId == request.IdentityId)
			throw new InvalidOperationException("Invalid player rebind request");
		return request;
	}

	private static PlayerReady Validate(PlayerReady ready)
	{
		Validate(new PlayerBindingRequest(ready.ServiceName, ready.UserId, ready.OwnerId, ready.SerialId,
			ready.Identity?.Name, ready.IsAdmin));
		if (ready.AuthorityGeneration <= 0) throw new InvalidOperationException("Invalid player binding generation");
		Validate(ready.Identity ?? throw new InvalidOperationException("Invalid player binding identity"));
		return ready;
	}

	private static bool ValidColor(bool hasColor, float x, float y, float z)
		=> !hasColor || float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
}
