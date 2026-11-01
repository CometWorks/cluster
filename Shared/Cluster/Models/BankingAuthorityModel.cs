using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary><paramref name="ToId"/> is the credited account for Kind "Transfer" and unused otherwise.
/// <paramref name="DelayMilliseconds"/> is a chaos-test knob: the authority sleeps that long inside the
/// mutation before executing, giving lanes a deterministic in-flight window to kill the WA in.</summary>
public sealed record BankingAuthorityRequest(string Kind, long OwnerId, long Amount, long ToId = 0, int DelayMilliseconds = 0);
public sealed record BankingAuthorityResult(bool Success);
public sealed record BankingAuthorityEntry(long OwnerId, long Balance);
public sealed record BankingAuthoritySnapshot(long OverallBalance, BankingAuthorityEntry[] Accounts);

public static class BankingAuthorityCodec
{
	public static string WriteRequest(BankingAuthorityRequest request) => JsonSerializer.Serialize(request);
	public static BankingAuthorityRequest ReadRequest(string payload) => Validate(JsonSerializer.Deserialize<BankingAuthorityRequest>(payload)
		?? throw new InvalidOperationException("Invalid banking request"));
	public static string WriteResult(bool success) => JsonSerializer.Serialize(new BankingAuthorityResult(success));
	public static BankingAuthorityResult ReadResult(string payload) => JsonSerializer.Deserialize<BankingAuthorityResult>(payload)
		?? throw new InvalidOperationException("Invalid banking result");
	public static string WriteSnapshot(long overallBalance, IEnumerable<BankingAuthorityEntry> accounts)
		=> JsonSerializer.Serialize(new BankingAuthoritySnapshot(overallBalance, accounts.OrderBy(x => x.OwnerId).ToArray()));

	public static BankingAuthoritySnapshot ReadSnapshot(string payload)
	{
		BankingAuthoritySnapshot snapshot = JsonSerializer.Deserialize<BankingAuthoritySnapshot>(payload)
			?? throw new InvalidOperationException("Invalid banking snapshot");
		if (snapshot.OverallBalance < 0 || snapshot.Accounts == null || snapshot.Accounts.Length > 16384
			|| snapshot.Accounts.Any(x => x.OwnerId <= 0 || x.Balance < 0)
			|| snapshot.Accounts.Select(x => x.OwnerId).Distinct().Count() != snapshot.Accounts.Length)
			throw new InvalidOperationException("Invalid banking snapshot");
		return snapshot;
	}

	private static BankingAuthorityRequest Validate(BankingAuthorityRequest request)
	{
		if (request.Kind is not ("Create" or "Remove" or "Change" or "Transfer") || request.OwnerId <= 0
			|| request.Kind == "Create" && request.Amount < 0 || request.Kind == "Remove" && request.Amount != 0
			|| request.Kind == "Transfer" && (request.ToId <= 0 || request.Amount <= 0 || request.ToId == request.OwnerId)
			|| request.Kind != "Transfer" && request.ToId != 0
			|| request.DelayMilliseconds is < 0 or > 10000)
			throw new InvalidOperationException("Invalid banking request");
		return request;
	}
}
