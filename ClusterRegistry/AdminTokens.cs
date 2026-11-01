using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>The authenticated identity behind one admin request: audit actor plus scope.</summary>
internal sealed record AdminActor(string Name, Admin.AdminScope Scope);

/// <summary>
/// Scoped admin credentials (Security.md §6): named bearer tokens with a query or manage
/// scope, stored as SHA-256 hashes with a current and an optional previous slot for
/// rotation. The file is re-read whenever its size or mtime changes, so removing a token
/// revokes it on the next request without a Gateway restart. A malformed edit keeps the
/// last good set and complains on stderr rather than failing open or locking everyone out.
/// </summary>
internal sealed class AdminTokenFile(string path)
{
    internal sealed record TokenEntry(string Name, Admin.AdminScope Scope, string Sha256, string? PreviousSha256 = null);

    private sealed record FileShape(TokenEntry[] Tokens);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private TokenEntry[] _tokens = [];
    private DateTime _mtime;
    private long _length = -1;
    private bool _warned;

    /// <summary>Parses eagerly so a malformed file fails the Gateway start, not the first request.</summary>
    public static AdminTokenFile Load(string path)
    {
        var file = new AdminTokenFile(path);
        file.Reload(throwOnError: true);
        return file;
    }

    public AdminActor? Match(string token)
    {
        Reload(throwOnError: false);
        byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        lock (_lock)
        {
            foreach (TokenEntry entry in _tokens)
            {
                if (HashEquals(supplied, entry.Sha256) || entry.PreviousSha256 != null && HashEquals(supplied, entry.PreviousSha256))
                    return new AdminActor(entry.Name, entry.Scope);
            }
        }
        return null;
    }

    private static bool HashEquals(byte[] supplied, string expectedHex)
    {
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHex); }
        catch (FormatException) { return false; }
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private void Reload(bool throwOnError)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException($"Admin token file '{path}' does not exist");
            lock (_lock)
            {
                if (info.LastWriteTimeUtc == _mtime && info.Length == _length)
                    return;
                FileShape? shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllBytes(path), Json);
                TokenEntry[] tokens = shape?.Tokens ?? [];
                foreach (TokenEntry entry in tokens)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Sha256))
                        throw new JsonException("Every admin token needs a name and a sha256 hash");
                }
                _tokens = tokens;
                _mtime = info.LastWriteTimeUtc;
                _length = info.Length;
                _warned = false;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FileNotFoundException or UnauthorizedAccessException)
        {
            if (throwOnError)
                throw new ArgumentException($"Cannot read admin token file '{path}': {e.Message}");
            if (!_warned)
            {
                _warned = true;
                Console.Error.WriteLine($"Admin token file '{path}' is unreadable; keeping the previous token set: {e.Message}");
            }
        }
    }
}
