using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Game.World;

namespace ClusterPlugin;

public static partial class PromotionService
{
    private static readonly Dictionary<ulong, AccessPromotionLease> Access = new();

    public sealed record AccessPromotionLease(
        ulong SteamId,
        int? Baseline,
        int Level,
        DateTimeOffset ExpiresAt
    );

    public sealed record AccessPromotion(ulong SteamId, int Level, DateTimeOffset ExpiresAt);

    public sealed record AccessPromotionPolicy(DateTimeOffset ExpiresAt, AccessPromotion[] Players);

    private static string AccessFromGateway(string origin, string payload)
    {
        if (origin != "$gateway")
            throw new InvalidOperationException("Player access must originate at the Gateway");
        if (payload == null || payload.Length > 4 * 1024 * 1024)
            throw new ArgumentException("Invalid access policy");
        var policy = JsonSerializer.Deserialize<AccessPromotionPolicy>(payload);
        var now = DateTimeOffset.UtcNow;
        if (
            policy?.Players == null
            || policy.Players.Length > MaxEntries
            || policy.ExpiresAt > now.AddMinutes(3)
            || policy.Players.Any(p => p == null || p.SteamId == 0 || p.Level is < 1 or > 4)
            || policy.Players.Select(p => p.SteamId).Distinct().Count() != policy.Players.Length
        )
            throw new ArgumentException("Invalid access policy");
        var game =
            MySandboxGame.Static
            ?? throw new InvalidOperationException("The World Authority is unavailable");
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void Apply()
        {
            try
            {
                if (
                    !ClusterRuntime.IsWorldAuthority
                    || ClusterRuntime.IsAuthorityFenced
                    || MySession.Static?.Ready != true
                )
                    throw new InvalidOperationException("The World Authority is unavailable");
                SeedFromSession(MySession.Static);
                if (!m_seeded)
                    throw new InvalidOperationException("promotion_not_ready");
                lock (SyncRoot)
                {
                    var desired = policy
                        .Players.Where(p => p.ExpiresAt > now && policy.ExpiresAt > now)
                        .ToDictionary(p => p.SteamId);
                    if (Levels.Keys.Concat(desired.Keys).Distinct().Count() > MaxEntries)
                        throw new InvalidOperationException("Promotion map is full");
                    foreach (var lease in Access.Values.ToArray())
                        if (!desired.ContainsKey(lease.SteamId))
                        {
                            RestoreBaseline(lease);
                            Access.Remove(lease.SteamId);
                        }
                    foreach (var grant in desired.Values)
                    {
                        var baseline =
                            Access.TryGetValue(grant.SteamId, out var lease) ? lease.Baseline
                            : Levels.TryGetValue(grant.SteamId, out var existing) ? existing.Level
                            : (int?)null;
                        Access[grant.SteamId] = new(
                            grant.SteamId,
                            baseline,
                            grant.Level,
                            grant.ExpiresAt < policy.ExpiresAt ? grant.ExpiresAt : policy.ExpiresAt
                        );
                        SetAccessLevel(grant.SteamId, Math.Max(baseline ?? 0, grant.Level));
                    }
                    // The journaled command records baseline, entitlement and expiry together in the promotion section.
                    PublishMerged();
                    Update();
                    completion.SetResult("{}");
                }
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }
        if (game.UpdateThread == Thread.CurrentThread)
            Apply();
        else
            game.Invoke(Apply, "ClusterPlugin player access");
        return completion.Task.GetAwaiter().GetResult();
    }

    private static void SetAccessLevel(ulong steamId, int level)
    {
        Levels.TryGetValue(steamId, out var previous);
        if (previous?.Level == level)
            return;
        Levels[steamId] = new(steamId, level, checked((previous?.Revision ?? 0) + 1));
    }

    internal static void ExpireAccess(DateTimeOffset now)
    {
        lock (SyncRoot)
        {
            var expired = Access.Values.Where(lease => lease.ExpiresAt <= now).ToArray();
            if (expired.Length == 0)
                return;
            foreach (var lease in expired)
            {
                RestoreBaseline(lease);
                Access.Remove(lease.SteamId);
            }
            PublishMerged();
        }
    }

    private static void RestoreBaseline(AccessPromotionLease lease)
    {
        if (lease.Baseline is int baseline)
            SetAccessLevel(lease.SteamId, baseline);
        else
            Levels.Remove(lease.SteamId);
    }

    private static void RestoreAccess(PromotionSnapshot snapshot)
    {
        Access.Clear();
        foreach (var lease in snapshot.Access ?? Array.Empty<AccessPromotionLease>())
            Access.Add(lease.SteamId, lease);
    }

    private static void ValidateAccess(AccessPromotionLease[] leases, PromotionEntry[] entries)
    {
        if (leases == null || leases.Length == 0)
            return;
        var users = new HashSet<ulong>(entries.Select(entry => entry.SteamId));
        if (
            leases.Length > MaxEntries
            || leases.Any(p =>
                p == null
                || !users.Contains(p.SteamId)
                || p.Baseline is < 0 or > 4
                || p.Level is < 1 or > 4
            )
            || leases.Select(p => p.SteamId).Distinct().Count() != leases.Length
        )
            throw new InvalidOperationException("Invalid player access promotion leases");
    }
}
