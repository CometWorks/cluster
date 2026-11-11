namespace ClusterRegistry;

/// <summary>
/// SE-1346: retention for global saves. The World Authority writes one a minute and nothing deleted any, so GLOBAL grew
/// by ~1,440 files a day on every holder (1,744 files on the octahedron-test World Authority node, 2026-11-11).
/// <list type="bullet">
/// <item>Kept: the newest <see cref="GlobalRetentionKeep"/>, every snapshot's GlobalRef, the last clean shutdown's, the
/// one this process warm-started from, the current shutdown Final, and everything an outstanding registry backup
/// catalogues (RegistryBackups.cs).</item>
/// <item>Nothing at or above the floor is deleted: the newest save with its full copy target of usable holders. With no
/// such save nothing is deleted at all, so a replication backlog never costs the last good copy.</item>
/// <item>The rest is marked PendingDelete and handed out through /registry/deletions like a partition save's copies.
/// A global directive carries partition 0 (never a partition save's id, as for the replica reservations) and
/// <c>Global: true</c>. The node acknowledges with partition 0, which <see cref="AcknowledgeDelete"/> routes here, so
/// a node built before this change deletes and acknowledges global copies correctly.</item>
/// </list>
/// CLUSTER_RETENTION_GLOBAL_KEEP=0 turns it off: global saves are kept forever, as before.
/// </summary>
internal sealed partial class GatewayRegistry
{
    // Not EnvInt: that drops 0, and 0 here means off.
    internal static int GlobalRetentionKeep =
        int.TryParse(
            Environment.GetEnvironmentVariable("CLUSTER_RETENTION_GLOBAL_KEEP"),
            out int keep
        )
        && keep >= 0
            ? keep
            : 10;

    /// <summary>Bumped when a global row is removed, so <see cref="GlobalReplicasRequired"/> rebuilds its cached set
    /// (the catalogue's count alone repeats once rows leave).</summary>
    private int _globalRemovals;

    /// <summary>PlanRetention, under _lock: marks the global saves retention releases, returns their directives.</summary>
    private List<DeleteDirective> PlanGlobalRetention()
    {
        List<DeleteDirective> result = [];
        int keep = GlobalRetentionKeep;
        if (keep <= 0)
            return result;
        GlobalSaveRegistration[] live = _state
            .GlobalSaves.Values.Where(save => !save.PendingDelete)
            .OrderByDescending(save => save.SaveId)
            .ToArray();
        if (live.Length <= keep)
            return result;
        int target = GlobalCopyTarget();
        GlobalSaveRegistration? floor = live.FirstOrDefault(save =>
            UsableGlobalCopies(save) >= target
        );
        if (floor == null)
            return result;

        HashSet<GlobalSaveId> retained = live.Take(keep).Select(save => save.SaveId).ToHashSet();
        foreach (SnapshotRegistration snapshot in _state.Snapshots)
            if (snapshot.GlobalRef is GlobalSaveId reference)
                retained.Add(reference);
        if (_state.LastShutdown?.GlobalRef is GlobalSaveId shutdown)
            retained.Add(shutdown);
        if (_startedFromGlobal is GlobalSaveId started)
            retained.Add(started);
        if (ShutdownGlobalSave() is { } final)
            retained.Add(final.SaveId);
        RetainGlobalBackupHolds(retained);

        foreach (GlobalSaveRegistration save in live)
        {
            if (save.SaveId.CompareTo(floor.SaveId) >= 0 || retained.Contains(save.SaveId))
                continue;
            if (save.Locations.Count == 0)
            {
                RemoveGlobal(save.SaveId);
                continue;
            }
            save.PendingDelete = true;
            Touch(save);
            result.AddRange(
                save.Locations.Select(location =>
                    GlobalDelete(
                        save,
                        save.LocationHolders.GetValueOrDefault(location, save.Node),
                        location
                    )
                )
            );
        }
        return result;
    }

    /// <summary>How many usable holders a global save needs before older ones may go: the hosts' copy target with the
    /// host store, else two nodes, or one while only one node can hold anything.</summary>
    private int GlobalCopyTarget() =>
        Math.Max(
            1,
            _hostStore
                ? HostCopyTarget()
                : Math.Min(
                    2,
                    _state.Nodes.Values.Count(node =>
                        !IsHostHolder(node.Node) && GlobalHolderUsable(node)
                    )
                )
        );

    private int UsableGlobalCopies(GlobalSaveRegistration save) =>
        save.LocationHolders.Values.Distinct(StringComparer.Ordinal).Count(GlobalHolderUsable);

    private static DeleteDirective GlobalDelete(
        GlobalSaveRegistration save,
        string node,
        string location
    ) => new(GlobalReservationKey(save.SaveId), node, location, Global: true);

    /// <summary>PendingDeletes, under _lock: the pending global copies held by <paramref name="node"/> or its host.</summary>
    private IEnumerable<DeleteDirective> PendingGlobalDeletes(string node, string? host) =>
        _state
            .GlobalSaves.Values.Where(save => save.PendingDelete)
            .SelectMany(save =>
                save.Locations.Where(location =>
                        save.LocationHolders.GetValueOrDefault(location, save.Node) is { } holder
                        && (holder == node || host != null && holder == host)
                    )
                    .Select(location => GlobalDelete(save, node, location))
            );

    /// <summary>AcknowledgeDelete for partition 0, under _lock: drops the deleted location; the last one removes the row.</summary>
    private bool AcknowledgeGlobalDelete(SaveId id, string location)
    {
        GlobalSaveId globalId = new(id.Generation, id.Sequence);
        if (
            !_state.GlobalSaves.TryGetValue(GlobalKey(globalId), out GlobalSaveRegistration? save)
            || !save.PendingDelete
            || !save.Locations.Remove(location)
        )
            return false;
        if (
            save.LocationHolders.Remove(location, out string? holder)
            && !save.LocationHolders.ContainsValue(holder)
        )
            save.HolderEndpoints.Remove(holder);
        Touch(save);
        if (save.Locations.Count == 0)
            RemoveGlobal(globalId);
        Persist();
        return true;
    }

    private void RemoveGlobal(GlobalSaveId id)
    {
        if (_state.GlobalSaves.Remove(GlobalKey(id)))
            _globalRemovals++;
    }
}
