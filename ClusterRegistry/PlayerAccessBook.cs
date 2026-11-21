using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>Local admission read model. No external requests occur on the player join path.</summary>
internal sealed class PlayerAccessBook
{
    private readonly string? path;
    private readonly string? requiredPath;
    private readonly object sync = new();
    private Admin.PlayerAccessPolicy? policy;
    private Requirement? required;

    public PlayerAccessBook(
        string? directory,
        string? target = null,
        PlayerAccessLaunchIntent? launch = null
    )
    {
        if (directory is null)
        {
            if (launch is not null)
                throw new InvalidOperationException(
                    "Persistent Registry storage is required for player access launch intent."
                );
            return;
        }
        path = Path.Combine(directory, "player-access.json");
        requiredPath = Path.Combine(directory, "player-access-required");
        if (File.Exists(path))
        {
            policy =
                JsonSerializer.Deserialize<Admin.PlayerAccessPolicy>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid player access policy.");
            Validate(policy, target ?? policy.Target, DateTimeOffset.UtcNow, loading: true);
        }
        if (File.Exists(requiredPath))
        {
            required =
                JsonSerializer.Deserialize<Requirement>(File.ReadAllText(requiredPath))
                ?? throw new InvalidDataException("Invalid player whitelist marker.");
            if (required.Revision < 0 || target is not null && required.Target != target)
                throw new InvalidDataException(
                    "Player whitelist marker belongs to another cluster or revision."
                );
            if (
                policy is not { Enabled: true, Whitelist: true }
                || policy.Revision < required.Revision
            )
                policy = new(
                    1,
                    required.Target,
                    Math.Max(policy?.Revision ?? 0, required.Revision),
                    policy?.IssuedAt ?? DateTimeOffset.MinValue,
                    DateTimeOffset.MinValue,
                    true,
                    true,
                    []
                );
        }
        else if (policy is { Enabled: true, Whitelist: true })
        {
            required = new(policy.Target, policy.Revision);
            WriteDurable(requiredPath, JsonSerializer.Serialize(required));
        }
        if (launch is not null)
        {
            if (target is null || launch.Revision < 0)
                throw new InvalidDataException("Invalid player access launch target or revision.");
            if (launch.Revision >= Math.Max(policy?.Revision ?? 0, required?.Revision ?? 0))
            {
                if (launch.Whitelist)
                {
                    required = new(target, launch.Revision);
                    WriteDurable(requiredPath, JsonSerializer.Serialize(required));
                }
                // Launch intent closes admission or fences stale updates. Only a fresh published policy
                // may remove a persisted whitelist requirement; a launch alone never reopens it.
                if (
                    policy is null
                    || policy.Revision < launch.Revision
                    || launch.Whitelist && policy is not { Enabled: true, Whitelist: true }
                )
                    policy = new(
                        1,
                        target,
                        launch.Revision,
                        DateTimeOffset.MinValue,
                        DateTimeOffset.MinValue,
                        required is not null || launch.Whitelist,
                        required is not null || launch.Whitelist,
                        []
                    );
            }
        }
    }

    public Admin.PlayerAccessStatus Status
    {
        get
        {
            lock (sync)
                return new(
                    policy?.Enabled == true,
                    policy is { Enabled: true, Whitelist: true },
                    policy?.Revision ?? 0,
                    policy?.ExpiresAt ?? DateTimeOffset.MinValue
                );
        }
    }

    public bool Allows(ulong user, DateTimeOffset now) => CheckAdmission(user, now, out _);

    public bool CheckAdmission(ulong user, DateTimeOffset now, out bool priority)
    {
        lock (sync)
        {
            bool waitingForRequiredPolicy =
                required != null
                && (
                    policy is not { Enabled: true, Whitelist: true }
                    || policy.Revision < required.Revision
                );
            if (waitingForRequiredPolicy)
            {
                priority = false;
                return false;
            }
            var grant =
                policy is { Enabled: true } && policy.ExpiresAt > now
                    ? policy.Players.FirstOrDefault(p =>
                        p.SteamId == user && p.Allowed && p.Confirmed && p.ExpiresAt > now
                    )
                    : null;
            priority = grant?.Priority == true;
            return policy is not { Enabled: true, Whitelist: true } || grant != null;
        }
    }

    public bool ConfirmedDenial(ulong user, DateTimeOffset now)
    {
        lock (sync)
        {
            if (policy is not { Enabled: true, Whitelist: true } || policy.ExpiresAt <= now)
                return false;
            var decision = policy.Players.SingleOrDefault(p => p.SteamId == user);
            return decision is null
                || decision.Confirmed && !decision.Allowed && decision.ExpiresAt > now;
        }
    }

    public void Apply(Admin.PlayerAccessPolicy update, string target)
    {
        Validate(update, target, DateTimeOffset.UtcNow);
        lock (sync)
        {
            if (
                update.Revision < (required?.Revision ?? 0)
                || policy is not null
                    && (update.Revision < policy.Revision || update.IssuedAt < policy.IssuedAt)
            )
                throw new ArgumentException("Stale player access policy.");
            if (path is null)
                throw new InvalidOperationException(
                    "Persistent Registry storage is required for player access."
                );
            if (
                update.Enabled
                && update.Whitelist
                && (
                    required is null
                    || required.Revision < update.Revision
                    || !File.Exists(requiredPath)
                )
            )
            {
                var requirement = new Requirement(update.Target, update.Revision);
                WriteDurable(requiredPath!, JsonSerializer.Serialize(requirement));
                required = requirement;
            }
            WriteDurable(path, JsonSerializer.Serialize(update));
            if (!update.Enabled || !update.Whitelist)
            {
                if (File.Exists(requiredPath))
                    File.Delete(requiredPath!);
                required = null;
            }
            policy = update;
        }
    }

    private static void WriteDurable(string destination, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static void Validate(
        Admin.PlayerAccessPolicy value,
        string target,
        DateTimeOffset now,
        bool loading = false
    )
    {
        if (
            value.Version != 1
            || value.Target != target
            || value.Revision < 0
            || value.IssuedAt > now.AddSeconds(30)
            || !loading && value.ExpiresAt <= now
            || value.ExpiresAt > value.IssuedAt.AddMinutes(2)
            || value.Players is null
            || value.Players.Length > 4096
            || value.Players.Any(p => p is null || p.SteamId == 0 || p.Promotion is < 0 or > 4)
            || value.Players.Select(p => p.SteamId).Distinct().Count() != value.Players.Length
        )
            throw new ArgumentException("Invalid or expired player access policy.");
    }

    private sealed record Requirement(string Target, long Revision);
}

internal sealed record PlayerAccessLaunchIntent(long Revision, bool Whitelist)
{
    public static PlayerAccessLaunchIntent? FromEnvironment()
    {
        string? revision = Environment.GetEnvironmentVariable("QUASAR_PLAYER_ACCESS_REVISION");
        string? whitelist = Environment.GetEnvironmentVariable("QUASAR_PLAYER_WHITELIST");
        if (revision is null && whitelist is null)
            return null;
        if (
            !long.TryParse(
                revision,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed
            )
            || parsed < 0
            || !bool.TryParse(whitelist, out var required)
        )
            throw new InvalidDataException("Invalid Quasar player access launch intent.");
        return new(parsed, required);
    }
}
