namespace ClusterRegistry;

/// <summary>
/// SE-1347: retention trims a retained partition save's copies down to the copy target. The replica queue only ever adds
/// copies and whole-save retention deletes every copy at once, so a save kept a copy on every node that ever held it: its
/// old owners after handovers, its replicas, and stopped nodes whose copies were copied again elsewhere. On a test cluster
/// on 2026-11-11 all 9,231 partitions sat on 2 to 8 nodes (502 on 3, 467 on 4, 447 on 5-8).
/// <list type="bullet">
/// <item>Kept: the target number of holders (two nodes, on two hosts while more than one host serves; with the host
/// store, the hosts' copy target), each a live, usable, non-corrupt holder: the partition's owner first, then Active
/// nodes before Closed ones, then by name. With fewer such holders nothing is trimmed.</item>
/// <item>Not touched: a save pending deletion, a save with a replica copy in flight, a partition that is recovering, being
/// loaded offline or in an incomplete handover, and with the host store a save that still has a node-held location (the
/// re-key moves it first). Only a live holder's copy is trimmed (the same test as for a kept one): a stopped, gone or
/// released holder's copy is left to the gone-holder release, or trimmed once its holder serves again.</item>
/// <item>A trimmed copy leaves Locations at once, so nothing counts or offers it again, and waits in
/// <see cref="SaveRegistration.TrimmedLocations"/> until its holder deletes it through /registry/deletions and acks. The
/// replica queue never hands a copy of that save to that holder in the meantime, and a holder that registers the location
/// again cancels the trim.</item>
/// </list>
/// At most CLUSTER_RETENTION_TRIM_BATCH copies a pass (default 1000, 0 = off), so the first pass on a cluster with a
/// backlog spreads its deletes over minutes.
/// </summary>
internal sealed partial class GatewayRegistry
{
    // Not EnvInt: that drops 0, and 0 here means off.
    internal static int TrimBatch =
        int.TryParse(
            Environment.GetEnvironmentVariable("CLUSTER_RETENTION_TRIM_BATCH"),
            out int batch
        )
        && batch >= 0
            ? batch
            : 1000;

    /// <summary>Copies trimmed since this registry started (status trimmedCopies).</summary>
    internal long TrimmedCopies { get; private set; }

    /// <summary>PlanRetention, under _lock, after the whole-save deletes: the copies trimmed this pass.</summary>
    private List<DeleteDirective> PlanCopyTrim()
    {
        List<DeleteDirective> result = [];
        int target = _hostStore ? HostCopyTarget() : 2;
        if (TrimBatch == 0 || target < 1)
            return result;
        DateTimeOffset now = _clock();
        bool multiHost = !_hostStore && MultiHost(CurrentHolderStates());
        foreach (SaveRegistration save in _state.Saves.Values)
        {
            if (result.Count >= TrimBatch)
                break;
            if (save.PendingDelete || save.LocationHolders.Count <= target)
                continue;
            ulong partition = save.SaveId.Partition;
            _state.Partitions.TryGetValue(partition, out PartitionOwnership? owner);
            if (
                owner?.Recovering == true
                || _state.OfflinePartitions.TryGetValue(partition, out OfflinePartition? offline)
                    && offline.LoadRequestedNode != null
                || _replicaReservations.TryGetValue(
                    save.SaveId,
                    out (string Node, DateTimeOffset Until) reservation
                )
                    && reservation.Until > now
                || HandoversOf(partition)
                    .Any(handover => handover.State is HandoverState.Begun or HandoverState.Active)
                || _hostStore && save.LocationHolders.Values.Any(holder => !IsHostHolder(holder))
            )
                continue;
            HashSet<string>? keep = KeptHolders(save, owner?.Node, target, multiHost, now);
            if (keep == null)
                continue;
            string[] trim = save
                .Locations.Where(location =>
                    save.LocationHolders.GetValueOrDefault(location, save.Node) is { } holder
                    && !keep.Contains(holder)
                    && Keepable(holder, now)
                )
                .Take(TrimBatch - result.Count)
                .ToArray();
            if (trim.Length == 0)
                continue;
            save.TrimmedLocations ??= new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string location in trim)
            {
                string holder = save.LocationHolders.GetValueOrDefault(location, save.Node);
                save.Locations.Remove(location);
                save.LocationHolders.Remove(location);
                if (!save.LocationHolders.ContainsValue(holder))
                {
                    save.HolderEndpoints.Remove(holder);
                    save.CorruptHolders.Remove(holder);
                }
                save.TrimmedLocations[location] = holder;
                result.Add(new DeleteDirective(save.SaveId, holder, location));
            }
            Touch(save);
            TrimmedCopies += trim.Length;
        }
        return result;
    }

    /// <summary>The holders a save keeps, or null when it has fewer than <paramref name="target"/> holders to keep.</summary>
    private HashSet<string>? KeptHolders(
        SaveRegistration save,
        string? owner,
        int target,
        bool multiHost,
        DateTimeOffset now
    )
    {
        string? ownerHolder =
            owner == null ? null
            : _hostStore
                ? PlanHostOf(owner) is { } host ? HostHolder(host)
                    : null
            : owner;
        string[] candidates = save
            .LocationHolders.Values.Distinct(StringComparer.Ordinal)
            .Where(holder => !save.CorruptHolders.Contains(holder) && Keepable(holder, now))
            .OrderBy(holder => holder == ownerHolder ? 0 : 1)
            .ThenBy(holder =>
                _state.Nodes.TryGetValue(holder, out NodeRegistration? row)
                && row.State == NodeState.Closed
                    ? 1
                    : 0
            )
            .ThenBy(holder => holder, StringComparer.Ordinal)
            .ToArray();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string holder in candidates)
        {
            if (keep.Count == target)
                break;
            string hostKey = _state.Nodes.GetValueOrDefault(holder)?.Host ?? "#" + holder;
            // Multi-host: the second copy must be on another host, as the replica queue counts it (Satisfied).
            if (multiHost && keep.Count == 1 && hosts.Contains(hostKey))
                continue;
            keep.Add(holder);
            hosts.Add(hostKey);
        }
        return keep.Count == target ? keep : null;
    }

    /// <summary>A holder whose copy may be kept or trimmed: a serving host store, or a live Active or Closed node that is
    /// neither gone nor released, so it can serve a kept copy or delete a trimmed one now.</summary>
    private bool Keepable(string holder, DateTimeOffset now) =>
        _hostStore
            ? IsHostHolder(holder) && StoreState(HostOfHolder(holder)!) == HostStoreState.Serving
            : _state.Nodes.TryGetValue(holder, out NodeRegistration? row)
                && row.State is NodeState.Active or NodeState.Closed
                && row.LeaseExpires > now
                && HolderUsable(holder)
                && GoneHolder(holder) == null;

    /// <summary>PendingDeletes, under _lock: the trimmed copies held by <paramref name="node"/> or its host.</summary>
    private static IEnumerable<DeleteDirective> PendingTrims(
        SaveRegistration save,
        string node,
        string? host
    ) =>
        save.TrimmedLocations is not { Count: > 0 } trimmed
            ? []
            : trimmed
                .Where(entry => entry.Value == node || host != null && entry.Value == host)
                .Select(entry => new DeleteDirective(save.SaveId, node, entry.Key));

    /// <summary>A holder registers <paramref name="location"/> again: the copy there is catalogued, no longer one to delete.</summary>
    private static void CancelTrim(SaveRegistration save, string location)
    {
        if (save.TrimmedLocations?.Remove(location) == true && save.TrimmedLocations.Count == 0)
            save.TrimmedLocations = null;
    }

    /// <summary>Whether <paramref name="node"/> (or its host's store) still has a trimmed copy of the save to delete: the
    /// replica queue must not hand it a fresh copy at that path before the delete is acknowledged.</summary>
    private bool TrimPendingFor(SaveRegistration save, string node) =>
        save.TrimmedLocations is { Count: > 0 } trimmed
        && (
            trimmed.ContainsValue(node)
            || _hostStore && PlanHostOf(node) is { } host && trimmed.ContainsValue(HostHolder(host))
        );
}
