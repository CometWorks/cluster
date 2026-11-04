using System.Collections.Concurrent;
using System.Text.Json;

namespace ClusterRegistry;

/// <summary>
/// The perimeter admission book (Security.md §3.4): every check that reads platform or config
/// data is decided here, at the Gateway, before a client is attached to any node. Mirrors the
/// vanilla `MyConfigDedicatedData` fields the gate and the browser row need.
/// </summary>
internal sealed record AdmissionSettings(
    string ServerName = "Magnetar Cluster",
    string WorldName = "Cluster",
    string GameMode = "S1-1-1-1",
    double SyncDistance = 3000,
    ulong GroupId = 0,
    int MemberLimit = 16,
    bool PasswordProtected = false,
    bool FamilySharing = true,
    bool Profiling = false,
    string DataHash = "",
    int ModCount = 0,
    long ModsTotalSize = 0,
    ulong[]? Banned = null,
    ulong[]? Administrators = null,
    ulong[]? Reserved = null,
    // The world's Workshop mod ids (#1529): the Steam frontend sums their Workshop sizes into ModsTotalSize,
    // as MyDedicatedServerBase.Mods does, when the deployment did not give the total.
    ulong[]? ModIds = null)
{
    public static AdmissionSettings Load(string? path)
    {
        if (path == null)
            return new AdmissionSettings();
        AdmissionSettings settings = JsonSerializer.Deserialize<AdmissionSettings>(File.ReadAllBytes(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new ArgumentException("Empty admission configuration " + path);
        if (settings.MemberLimit < 2)
            throw new ArgumentException("Admission memberLimit must be at least 2 (vanilla SetMemberLimit floor)");
        return settings;
    }
}

internal sealed class AdmissionBook
{
    // MyDedicatedServerBase.KICK_TIMEOUT_MS
    private static readonly TimeSpan KickTimeout = TimeSpan.FromMinutes(5);
    private readonly HashSet<ulong> _banned;
    private readonly HashSet<ulong> _administrators;
    private readonly HashSet<ulong> _reserved;
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _kicked = new();

    public AdmissionSettings Settings { get; }

    private long _modsTotalSize = -1;

    /// <summary>The browser row's modsTotalSize: Settings' value until the Steam frontend measured the
    /// world's mods (MyDedicatedServerBase.Mods: the sum of their Workshop file sizes).</summary>
    public ulong ModsTotalSize
    {
        get { long measured = Interlocked.Read(ref _modsTotalSize); return measured < 0 ? (ulong)Settings.ModsTotalSize : (ulong)measured; }
        set => Interlocked.Exchange(ref _modsTotalSize, (long)value);
    }

    private byte[]? _serverData;

    /// <summary>
    /// The vanilla server-details blob (SE-1076): MyCachedServerItem.GetServerData() as a node computed it from its
    /// session, i.e. the compressed MyServerData with the world settings and the mod list. Null until a node reported
    /// it. The client's server details screen reads its Settings and Mods tabs from nothing else.
    /// </summary>
    public byte[]? ServerData => Volatile.Read(ref _serverData);

    /// <summary>Stores a node's report; true when it differs from what is held.</summary>
    public bool SetServerData(byte[] data)
    {
        byte[]? old = Interlocked.Exchange(ref _serverData, data);
        return old == null || !old.AsSpan().SequenceEqual(data);
    }

    /// <summary>
    /// The A2S rules MyCachedServerItem.SendSettingsToSteam sets: "sc" is the blob's length, "sc0".."scN" its 93-byte
    /// chunks in base64. Empty until a node reported the blob.
    /// </summary>
    public IEnumerable<(string Key, string Value)> ServerDataRules()
    {
        byte[]? data = ServerData;
        if (data == null)
            yield break;
        yield return ("sc", data.Length.ToString());
        for (int i = 0; i * ServerDataChunk < data.Length; i++)
            yield return ("sc" + i, Convert.ToBase64String(data, i * ServerDataChunk,
                Math.Min(ServerDataChunk, data.Length - i * ServerDataChunk)));
    }

    // MyCachedServerItem.RULE_LENGTH
    private const int ServerDataChunk = 93;

    public AdmissionBook(AdmissionSettings settings)
    {
        Settings = settings;
        _banned = [.. settings.Banned ?? []];
        _administrators = [.. settings.Administrators ?? []];
        _reserved = [.. settings.Reserved ?? []];
    }

    public bool IsAdmin(ulong userId) => _administrators.Contains(userId);
    public bool IsBanned(ulong userId) { lock (_banned) return _banned.Contains(userId); }

    public void Ban(ulong userId, bool banned)
    {
        lock (_banned)
        {
            if (banned) _banned.Add(userId);
            else _banned.Remove(userId);
        }
    }

    public void Kick(ulong userId) => _kicked[userId] = DateTimeOffset.UtcNow;

    public void ClearKickCooldown(ulong userId) => _kicked.TryRemove(userId, out _);

    public CometWorks.ClusterGateway.AdminContract.V1.PlayerKickCooldown[] KickCooldowns()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return _kicked.Where(entry => entry.Value + KickTimeout > now).OrderBy(entry => entry.Key)
            .Select(entry => new CometWorks.ClusterGateway.AdminContract.V1.PlayerKickCooldown(entry.Key, entry.Value + KickTimeout)).ToArray();
    }

    public ulong[] BannedUsers() { lock (_banned) return _banned.Order().ToArray(); }

    public bool WasKickedRecently(ulong userId) =>
        _kicked.TryGetValue(userId, out DateTimeOffset when) && DateTimeOffset.UtcNow - when < KickTimeout;

    /// <summary>
    /// The vanilla async gate of MyDedicatedServerBase.SetUserConnectionResult, in its order, with
    /// the member limit taken cluster-wide. Group status is asynchronous and checked by the caller
    /// after this returns null. Returns null when the client is admitted.
    /// </summary>
    public JoinResult? Reject(ulong userId, ulong ownerId, int clusterPlayers, bool alreadyJoined, bool isProfiling)
    {
        if (alreadyJoined) return JoinResult.AlreadyJoined;
        if (IsBanned(userId)) return JoinResult.BannedByAdmins;
        if (WasKickedRecently(userId)) return JoinResult.KickedRecently;
        if (!Settings.FamilySharing && ownerId != 0 && ownerId != userId) return JoinResult.FamilySharing;
        // Admins and reserved ids bypass the cap - reserved slots are not a separate pool (§3.1).
        if (_administrators.Contains(userId) || _reserved.Contains(userId)) return null;
        if (Settings.MemberLimit > 0 && clusterPlayers >= Settings.MemberLimit) return JoinResult.ServerFull;
        if (isProfiling && !Settings.Profiling) return JoinResult.ProfilingNotAllowed;
        return null;
    }

    /// <summary>MyDedicatedServer.SendGameTagsToSteam, with the cluster's accept-all state tagged on.</summary>
    public string GameTags(string version, bool acceptAll) =>
        $"groupId{Settings.GroupId} version{BuildNumber(version)} datahash{Settings.DataHash} mods{Settings.ModCount}"
        + $" gamemode{Settings.GameMode} view{Settings.SyncDistance:0} modsTotalSize{ModsTotalSize}"
        + (acceptAll ? " acceptall1" : string.Empty);

    /// <summary>MyFinalBuildConstants.APP_VERSION.ToString(), what vanilla advertises (tag, game data, GameServer.Init):
    /// the build number, 1.210.014 -> 1210014. The client's server details read the tag as a number (#1529).</summary>
    public static string BuildNumber(string version)
    {
        string[] parts = version.Split('.');
        return parts.Length == 3 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor)
            && int.TryParse(parts[2], out int patch) ? (major * 1_000_000 + minor * 1_000 + patch).ToString() : version;
    }
}

/// <summary>
/// Durable admin ban overrides. The admission config file describes the deployment's
/// baseline; bans issued through the admin API are appended here (atomic rewrite) and
/// applied over the baseline at start, so they survive a Gateway restart instead of
/// dying with the in-memory AdmissionBook.
/// </summary>
internal sealed class BanStore(string path)
{
    private sealed record Shape(ulong[] Banned);

    public static BanStore? Open(string? registryDirectory)
    {
        if (registryDirectory == null)
            return null;
        Directory.CreateDirectory(registryDirectory);
        return new BanStore(Path.Combine(registryDirectory, "admission-bans.json"));
    }

    public ulong[] Load()
    {
        try
        {
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<Shape>(File.ReadAllBytes(path),
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.Banned ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Ban store '{path}' is unreadable; starting with no admin bans: {e.Message}");
            return [];
        }
    }

    public void Save(ulong[] banned)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Shape(banned),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        File.Move(temporary, path, overwrite: true);
    }
}
