using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>
/// Full-world export as a durable admin operation (gap analysis §7): the Gateway shells
/// out to the packaged MagnetarWorld tool, assembles a vanilla world under a scoped
/// staging directory, checksums it, and atomically promotes it to an artifact the caller
/// retrieves by SharedPath. A remote caller never supplies a server-side output path -
/// the export root is deployment configuration, not request input.
/// </summary>
internal sealed partial class DirectGateway
{
    private void AdminWorldExport(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Admin.WorldExportRequest>(context) ?? new Admin.WorldExportRequest();
        if (_options.ExportRoot == null || _options.World == null
            || _options.ExportDataRoots is not { Length: > 0 } || _options.WorldTool == null)
        {
            WriteAdminError(context, 503, "exports_unconfigured",
                "World exports need --world, --export-root, --export-data-roots, and MAGNETAR_WORLD_TOOL.");
            return;
        }
        if (request.TtlHours is <= 0)
        {
            WriteAdminError(context, 400, "invalid_arguments", "ttlHours must be positive.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "world-export", Fingerprint(request),
            "world", out bool replayed);
        if (record == null)
            return;
        if (!replayed)
            ThreadPool.QueueUserWorkItem(_ => RunWorldExport(record, TimeSpan.FromHours(request.TtlHours ?? 24)));
        WriteOperation(context, record);
    }

    private void RunWorldExport(AdminOperationRecord record, TimeSpan ttl)
    {
        string staging = Path.Combine(_options.ExportRoot!, "staging-" + record.OperationId);
        try
        {
            // The consistency label is decided from registry evidence, never upgraded: the
            // implemented to-vanilla assembles the newest catalogued saves rather than one
            // pinned snapshot, so an online cluster's export is CrashConsistent (decision
            // §11.4) and only a cleanly-down cluster earns Quiescent.
            Admin.ClusterStatus status = _registry!.AdminStatus(0);
            Admin.SnapshotSummary? newest = _registry.AdminSnapshots().FirstOrDefault();
            var warnings = new List<string>();
            Admin.ExportConsistency consistency;
            if (status.Phase == Admin.ClusterPhase.Down && newest is { Kind: Admin.AdminSnapshotKind.CleanShutdown }
                && newest.Dirty.Length == 0)
                consistency = Admin.ExportConsistency.Quiescent;
            else
            {
                consistency = Admin.ExportConsistency.CrashConsistent;
                warnings.Add("onlineExportNotSnapshotPinned");
            }

            Directory.CreateDirectory(_options.ExportRoot!);
            Directory.CreateDirectory(staging);
            string[] arguments = ["to-vanilla", _options.World!, staging, .. _options.ExportDataRoots!];
            (int exitCode, string tail) = RunWorldTool(arguments, TimeSpan.FromMinutes(30));
            if (exitCode != 0)
            {
                _registry.FailAdminOperation(record.OperationId, "export_failed",
                    $"MagnetarWorld to-vanilla exited {exitCode}: {tail}");
                return;
            }

            (Dictionary<string, string> checksums, long sizeBytes, string manifestSha256) = ChecksumTree(staging);
            if (checksums.Count == 0)
            {
                _registry.FailAdminOperation(record.OperationId, "export_failed", "Export produced no files.");
                return;
            }
            string artifactId = "world-" + record.OperationId;
            string finalPath = Path.Combine(_options.ExportRoot!, artifactId);
            Directory.Move(staging, finalPath);
            var descriptor = new Admin.ArtifactDescriptor(
                artifactId,
                "SpaceEngineersVanillaWorld",
                newest?.SnapshotId,
                consistency,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow + ttl,
                sizeBytes,
                manifestSha256,
                checksums,
                new Admin.ArtifactRetrieval(Admin.ArtifactRetrievalKind.SharedPath, Path.GetFullPath(finalPath)),
                warnings.ToArray());
            _artifacts!.Add(descriptor); // non-null whenever ExportRoot is, which AdminWorldExport checked
            _events.Append("artifact.published", target: artifactId, data: new Dictionary<string, string>
            {
                ["consistency"] = consistency.ToString(),
                ["sizeBytes"] = sizeBytes.ToString(),
            });
            _registry.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
            {
                ["artifactId"] = artifactId,
                ["consistency"] = consistency.ToString(),
                ["sizeBytes"] = sizeBytes.ToString(),
                ["localPath"] = descriptor.Retrieval.LocalPath!,
            });
        }
        catch (Exception e)
        {
            _registry!.FailAdminOperation(record.OperationId, "export_failed", e.Message);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    private (int ExitCode, string Tail) RunWorldTool(string[] arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(_options.WorldTool!);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var tail = new Queue<string>();
        void Keep(string? line)
        {
            if (line == null) return;
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 20) tail.Dequeue();
            }
        }
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "timed out after " + timeout);
        }
        process.WaitForExit();
        lock (tail) return (process.ExitCode, string.Join(" | ", tail));
    }

    /// <summary>Per-file SHA-256 plus a manifest hash over the sorted relative-path:hash lines.</summary>
    internal static (Dictionary<string, string> Checksums, long SizeBytes, string ManifestSha256) ChecksumTree(string root)
    {
        var checksums = new Dictionary<string, string>();
        long sizeBytes = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order())
        {
            using FileStream stream = File.OpenRead(file);
            checksums[Path.GetRelativePath(root, file).Replace('\\', '/')] =
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            sizeBytes += new FileInfo(file).Length;
        }
        string manifest = string.Join('\n', checksums.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + ":" + x.Value));
        return (checksums, sizeBytes,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant());
    }
}

/// <summary>
/// Durable artifact index under the export root (artifacts.json, atomic rewrite) with lazy
/// TTL cleanup: expired or released artifacts are removed from disk and from the index the
/// next time anyone touches the store. No background thread - exports are rare.
/// </summary>
internal sealed class ArtifactStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private sealed record Shape(Admin.ArtifactDescriptor[] Artifacts);

    private readonly object _lock = new();
    private readonly string _indexPath = Path.Combine(root, "artifacts.json");

    public void Add(Admin.ArtifactDescriptor descriptor)
    {
        lock (_lock)
            Save([.. LoadFresh(), descriptor]);
    }

    public Admin.ArtifactDescriptor[] List()
    {
        lock (_lock)
        {
            Admin.ArtifactDescriptor[] fresh = LoadFresh();
            Save(fresh);
            return fresh;
        }
    }

    public Admin.ArtifactDescriptor? Get(string artifactId)
    {
        lock (_lock)
            return LoadFresh().FirstOrDefault(x => x.ArtifactId == artifactId);
    }

    /// <summary>Explicit release; returns false when the artifact is unknown (already gone is fine).</summary>
    public bool Release(string artifactId)
    {
        lock (_lock)
        {
            Admin.ArtifactDescriptor[] all = LoadFresh();
            Admin.ArtifactDescriptor? found = all.FirstOrDefault(x => x.ArtifactId == artifactId);
            if (found == null)
                return false;
            DeleteBody(found);
            Save(all.Where(x => x.ArtifactId != artifactId).ToArray());
            return true;
        }
    }

    private Admin.ArtifactDescriptor[] LoadFresh()
    {
        Admin.ArtifactDescriptor[] stored = Load();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Admin.ArtifactDescriptor[] fresh = stored.Where(x => x.ExpiresAt > now).ToArray();
        foreach (Admin.ArtifactDescriptor expired in stored.Where(x => x.ExpiresAt <= now))
            DeleteBody(expired);
        return fresh;
    }

    private Admin.ArtifactDescriptor[] Load()
    {
        try
        {
            return File.Exists(_indexPath)
                ? JsonSerializer.Deserialize<Shape>(File.ReadAllBytes(_indexPath), Json)?.Artifacts ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Console.Error.WriteLine($"Artifact index '{_indexPath}' is unreadable; treating as empty: {e.Message}");
            return [];
        }
    }

    private void Save(Admin.ArtifactDescriptor[] artifacts)
    {
        Directory.CreateDirectory(root);
        string temporary = _indexPath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Shape(artifacts), Json));
        File.Move(temporary, _indexPath, overwrite: true);
    }

    private void DeleteBody(Admin.ArtifactDescriptor descriptor)
    {
        // Only paths inside the export root are ever deleted - the descriptor is ours, but a
        // corrupted index must not become an arbitrary filesystem delete primitive.
        string? path = descriptor.Retrieval.LocalPath;
        if (path == null)
            return;
        string full = Path.GetFullPath(path);
        string scope = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(scope, StringComparison.Ordinal))
            return;
        try { if (Directory.Exists(full)) Directory.Delete(full, true); } catch { }
    }
}
