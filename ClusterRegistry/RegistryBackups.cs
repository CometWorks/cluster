using System.Net;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>
/// A consistent copy of the registry database as a durable admin operation, for an online backup of
/// a running cluster. The copy is the state a crash at the copy time would leave: every commit up to
/// it, nothing after it. The caller copies the node data after the copy completes, so every save the
/// copied catalogue names is already a complete file, and retention keeps those files until the
/// artifact is released or expires (#1275). It is published as an artifact under the export root,
/// which the backup reads and then releases.
/// </summary>
internal sealed partial class DirectGateway
{
    internal const string RegistryBackupFormat = "ClusterRegistrySqlite";

    private void AdminRegistryBackup(HttpListenerContext context, AdminActor actor)
    {
        var request =
            ReadAdminBody<Admin.RegistryBackupRequest>(context)
            ?? new Admin.RegistryBackupRequest();
        if (_options.ExportRoot == null)
        {
            WriteAdminError(
                context,
                503,
                "exports_unconfigured",
                "Registry backups need --export-root."
            );
            return;
        }
        if (request.TtlHours is <= 0)
        {
            WriteAdminError(context, 400, "invalid_arguments", "ttlHours must be positive.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(
            context,
            actor,
            "registry-backup",
            Fingerprint(request),
            "registry",
            out bool replayed
        );
        if (record == null)
            return;
        if (!replayed)
            ThreadPool.QueueUserWorkItem(_ =>
                RunRegistryBackup(record, TimeSpan.FromHours(request.TtlHours ?? 6))
            );
        WriteOperation(context, record);
    }

    private void RunRegistryBackup(AdminOperationRecord record, TimeSpan ttl)
    {
        string staging = Path.Combine(_options.ExportRoot!, "staging-" + record.OperationId);
        string artifactId = "registry-" + record.OperationId;
        try
        {
            Admin.ClusterStatus status = _registry!.AdminStatus(0);
            Admin.SnapshotSummary? newest = _registry.AdminSnapshots().FirstOrDefault();
            Admin.ExportConsistency consistency =
                status.Phase == Admin.ClusterPhase.Down
                && newest is { Kind: Admin.AdminSnapshotKind.CleanShutdown }
                && newest.Dirty.Length == 0
                    ? Admin.ExportConsistency.Quiescent
                    : Admin.ExportConsistency.CrashConsistent;
            Directory.CreateDirectory(staging);
            DateTimeOffset copiedAt = DateTimeOffset.UtcNow;
            // #1275: retention keeps every save the copy names until the artifact is released or expires.
            _registry.CopyStore(Path.Combine(staging, RegistryStore.FileName), artifactId, copiedAt + ttl);
            (Dictionary<string, string> checksums, long sizeBytes, string manifestSha256) =
                ChecksumTree(staging);
            string finalPath = Path.Combine(_options.ExportRoot!, artifactId);
            Directory.Move(staging, finalPath);
            var descriptor = new Admin.ArtifactDescriptor(
                artifactId,
                RegistryBackupFormat,
                newest?.SnapshotId,
                consistency,
                copiedAt,
                copiedAt + ttl,
                sizeBytes,
                manifestSha256,
                checksums,
                new Admin.ArtifactRetrieval(
                    Admin.ArtifactRetrievalKind.SharedPath,
                    Path.GetFullPath(finalPath)
                ),
                []
            );
            _artifacts!.Add(descriptor); // non-null whenever ExportRoot is, which AdminRegistryBackup checked
            _events.Append(
                "artifact.published",
                target: artifactId,
                data: new Dictionary<string, string>
                {
                    ["consistency"] = consistency.ToString(),
                    ["sizeBytes"] = sizeBytes.ToString(),
                }
            );
            _registry.CompleteAdminOperation(
                record.OperationId,
                new Dictionary<string, string>
                {
                    ["artifactId"] = artifactId,
                    ["consistency"] = consistency.ToString(),
                    ["sizeBytes"] = sizeBytes.ToString(),
                    ["copiedAt"] = copiedAt.ToString("O"),
                    ["localPath"] = descriptor.Retrieval.LocalPath!,
                }
            );
        }
        catch (Exception e)
        {
            _registry!.ReleaseBackupHold(artifactId);
            _registry.FailAdminOperation(record.OperationId, "registry_backup_failed", e.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
            }
            catch { }
        }
    }
}

/// <summary>
/// #1275 (race review F32): the retention hold of an outstanding registry backup. The backup copies node data AFTER
/// the registry copy, so every save the copy catalogues must stay on disk until the backup is done with it: without
/// the hold, three newer saves of a busy partition during the data copy let retention delete the copy's whole cover,
/// and the restored world loses that partition. The hold keeps every save the copy can name out of PlanRetention
/// until the artifact is released or expires. It lives in memory only: a gateway restart ends it, and Quasar refuses
/// to activate a cluster while its Hosts copy it.
/// </summary>
internal sealed partial class GatewayRegistry
{
    /// <summary>By holder (the artifact id): the held saves, null while the copy runs (everything held), and the end.</summary>
    private readonly Dictionary<string, (HashSet<SaveId>? Saves, HashSet<GlobalSaveId>? Globals, DateTimeOffset Until)> _backupHolds = new(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="CopyStore(string)"/> under a retention hold for <paramref name="holder"/> until
    /// <paramref name="until"/> or <see cref="ReleaseBackupHold"/>. While the copy runs retention marks nothing; once it
    /// is done the hold narrows to the saves not pending deletion, a superset of what the copy lists as live.
    /// </summary>
    public void CopyStore(string destination, string holder, DateTimeOffset until)
    {
        lock (_lock) _backupHolds[holder] = (null, null, until);
        try
        {
            CopyStore(destination);
        }
        catch
        {
            ReleaseBackupHold(holder);
            throw;
        }
        lock (_lock)
            _backupHolds[holder] = (_state.Saves.Values.Where(save => !save.PendingDelete).Select(save => save.SaveId).ToHashSet(),
                _state.GlobalSaves.Values.Where(save => !save.PendingDelete).Select(save => save.SaveId).ToHashSet(), until);
    }

    /// <summary>Ends <paramref name="holder"/>'s hold; false when it had none (already released or expired).</summary>
    public bool ReleaseBackupHold(string holder)
    {
        lock (_lock) return _backupHolds.Remove(holder);
    }

    /// <summary>PlanRetention, under _lock: adds what the live holds keep to <paramref name="retained"/>, drops expired holds.</summary>
    private void RetainBackupHolds(HashSet<SaveId> retained)
    {
        DateTimeOffset now = _clock();
        foreach ((string holder, (HashSet<SaveId>? saves, _, DateTimeOffset until)) in _backupHolds.ToArray())
            if (until <= now)
                _backupHolds.Remove(holder);
            else
                retained.UnionWith(saves ?? _state.Saves.Values.Select(save => save.SaveId));
    }

    /// <summary>SE-1346, PlanRetention after <see cref="RetainBackupHolds"/>, under _lock: the global saves the live holds keep.</summary>
    private void RetainGlobalBackupHolds(HashSet<GlobalSaveId> retained)
    {
        foreach ((_, HashSet<GlobalSaveId>? globals, _) in _backupHolds.Values)
            retained.UnionWith(globals ?? _state.GlobalSaves.Values.Select(save => save.SaveId));
    }
}
