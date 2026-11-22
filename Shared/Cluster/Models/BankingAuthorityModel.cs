using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary><paramref name="ToId"/> is the credited account for Kind "Transfer" and unused otherwise.
/// <paramref name="DelayMilliseconds"/> is a chaos-test knob: the authority sleeps that long inside the
/// mutation before executing, giving lanes a deterministic in-flight window to kill the WA in.</summary>
public sealed record BankingAuthorityRequest(
    string Kind,
    long OwnerId,
    long Amount,
    long ToId = 0,
    int DelayMilliseconds = 0
);

/// <summary>
/// <paramref name="Version"/>: the World Authority's banking version after the command, which the snapshot that carries
/// its effect also has, so a node knows when its replica shows it (SE-1525). <paramref name="Collected"/> and
/// <paramref name="Debt"/>: what a "Collect" took from the account and what it booked as store debt.
/// </summary>
public sealed record BankingAuthorityResult(
    bool Success,
    long Version = 0,
    long Collected = 0,
    long Debt = 0
);

public sealed record BankingAuthorityEntry(long OwnerId, long Balance);

public sealed record BankingAuthoritySnapshot(
    long OverallBalance,
    BankingAuthorityEntry[] Accounts,
    long Version = 0
);

public static class BankingAuthorityCodec
{
    public static string WriteRequest(BankingAuthorityRequest request) =>
        JsonSerializer.Serialize(request);

    public static BankingAuthorityRequest ReadRequest(string payload) =>
        Validate(
            JsonSerializer.Deserialize<BankingAuthorityRequest>(payload)
                ?? throw new InvalidOperationException("Invalid banking request")
        );

    public static string WriteResult(
        bool success,
        long version = 0,
        long collected = 0,
        long debt = 0
    ) => JsonSerializer.Serialize(new BankingAuthorityResult(success, version, collected, debt));

    public static BankingAuthorityResult ReadResult(string payload) =>
        JsonSerializer.Deserialize<BankingAuthorityResult>(payload)
        ?? throw new InvalidOperationException("Invalid banking result");

    public static string WriteSnapshot(
        long overallBalance,
        IEnumerable<BankingAuthorityEntry> accounts,
        long version = 0
    ) =>
        JsonSerializer.Serialize(
            new BankingAuthoritySnapshot(
                overallBalance,
                accounts.OrderBy(x => x.OwnerId).ToArray(),
                version
            )
        );

    public static BankingAuthoritySnapshot ReadSnapshot(string payload)
    {
        BankingAuthoritySnapshot snapshot =
            JsonSerializer.Deserialize<BankingAuthoritySnapshot>(payload)
            ?? throw new InvalidOperationException("Invalid banking snapshot");
        if (
            snapshot.OverallBalance < 0
            || snapshot.Accounts == null
            || snapshot.Accounts.Length > 16384
            || snapshot.Accounts.Any(x => x.OwnerId <= 0 || x.Balance < 0)
            || snapshot.Accounts.Select(x => x.OwnerId).Distinct().Count()
                != snapshot.Accounts.Length
        )
            throw new InvalidOperationException("Invalid banking snapshot");
        return snapshot;
    }

    private static BankingAuthorityRequest Validate(BankingAuthorityRequest request)
    {
        if (
            request.Kind is not ("Create" or "Remove" or "Change" or "Transfer" or "Collect")
            || request.OwnerId <= 0
            || request.Kind == "Create" && request.Amount < 0
            || request.Kind == "Remove" && request.Amount != 0
            || request.Kind == "Collect" && request.Amount <= 0
            || request.Kind == "Transfer"
                && (request.ToId <= 0 || request.Amount <= 0 || request.ToId == request.OwnerId)
            || request.Kind != "Transfer" && request.ToId != 0
            || request.DelayMilliseconds is < 0 or > 10000
        )
            throw new InvalidOperationException("Invalid banking request");
        return request;
    }
}
