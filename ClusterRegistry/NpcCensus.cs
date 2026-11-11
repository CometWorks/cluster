using System.Text.Json.Serialization;

namespace ClusterRegistry;

/// <summary>
/// #1354: one NPC grid as a node reports it - a top-level grid whose big owners are all NPC identities - with the
/// seconds it has been online with no player within <c>npc.visitRadius</c> (the node's UnvisitedNpcSweeper). The
/// census carries that count so it survives a handover and an offline/online cycle: the node that gets the grid
/// next seeds its own count from the census.
/// #1379: Kind = the node's cleanup class, "keep" (OCT, trade stations), "npc" or "unowned" (no big owner: drops, debris);
/// null from a node that predates it, which reported NPC grids only. Offline = the row comes from an offline partition,
/// set by the merge (a node never sends it).
/// #1443: Drop = an unopened container drop (vanilla takes the component away when a player opens it), Expires = the unix
/// second its vanilla removal was due on the node that reported it, 0 = none there. An offline row keeps both as they were
/// at the freeze. The World Authority caps the active drops cluster-wide on these rows (drops.maxActive).
/// </summary>
internal sealed record NpcGrid(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("u")] double UnvisitedSeconds,
    [property: JsonPropertyName("k"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonPropertyName("o"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Offline = false,
    [property: JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Drop = false,
    [property: JsonPropertyName("e"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Expires = 0)
{
    [JsonIgnore] public bool IsNpc => Kind != "unowned";
}

/// <summary>#1354: the cluster-wide NPC census every node polls (GET /registry/npc-census). Online and Offline count the NPC
/// grids (the kinds keep and npc); Grids also holds the unowned ones (#1379).</summary>
internal sealed record NpcCensus(
    // Seconds since the partition report pass the online rows come from. The node adds its own time since the answer.
    [property: JsonPropertyName("ageSeconds")] double AgeSeconds,
    [property: JsonPropertyName("online")] int Online,
    [property: JsonPropertyName("offline")] int Offline,
    [property: JsonPropertyName("grids")] NpcGrid[] Grids,
    // SE-1316: the unix second the newest registry backup finished (its artifact released), null since the gateway started.
    // The nodes' bot ship cleanup runs on each new one and removes only ships that were already unused when it was taken.
    [property: JsonPropertyName("backupAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BackupAt = null);

internal sealed partial class DirectGateway
{
    private readonly object _npcCensusLock = new();
    private DateTime _npcCensusReportsAt;
    private long _npcCensusOfflineRevision;
    private OfflinePartition[] _npcCensusOfflineRows = [];
    private NpcCensus? _npcCensus;
    /// <summary>SE-1316: the unix second the newest stored registry backup was released, 0 = none since start.</summary>
    private long _lastBackupAt;

    /// <summary>
    /// The NPC grids online on every serving node (the last COMPLETE report pass, as the offline admission reads it)
    /// plus those in offline partitions (the rows keep what the owner captured at the freeze). Null before the first
    /// complete pass. Rebuilt only when the pass or the offline table changed, so N nodes polling every few seconds
    /// cost one merge per pass.
    /// </summary>
    private NpcCensus? NpcCensusNow()
    {
        if (_lastPartitionReports is not { } reports) return null;
        lock (_npcCensusLock)
        {
            OfflineTable offline = _registry!.OfflinePartitions(_npcCensusOfflineRevision);
            if (!offline.Unchanged)
            {
                _npcCensusOfflineRows = offline.Partitions;
                _npcCensusOfflineRevision = offline.Revision;
            }
            if (_npcCensus == null || !offline.Unchanged || _npcCensusReportsAt != reports.At)
            {
                NpcGrid[] grids = MergeNpcCensus(reports.Reports.Select(x => x.Report), _npcCensusOfflineRows, out int online, out int offlineNpc);
                _npcCensus = new NpcCensus(0, online, offlineNpc, grids);
                _npcCensusReportsAt = reports.At;
            }
            return _npcCensus with { BackupAt = _lastBackupAt == 0 ? null : Interlocked.Read(ref _lastBackupAt), AgeSeconds = Math.Round(Math.Max(0, (DateTime.UtcNow - reports.At).TotalSeconds), 1) };
        }
    }

    /// <summary>
    /// One row per NPC grid id, online rows first. A grid two nodes report at once (a handover between their passes)
    /// keeps the smaller unvisited count, so a race can only delay a removal. An offline row's grid that is also
    /// online (a load the table has not caught up with) counts once, as the online row. The counts are of NPC grids.
    /// </summary>
    internal static NpcGrid[] MergeNpcCensus(IEnumerable<PartitionReport> reports, IEnumerable<OfflinePartition> offline,
        out int online, out int offlineNpc)
    {
        var grids = new Dictionary<long, NpcGrid>();
        foreach (PartitionReport report in reports)
            foreach (PartitionInfo partition in report.Partitions)
                foreach (NpcGrid grid in partition.Npc ?? [])
                    if (!grids.TryGetValue(grid.Id, out NpcGrid? seen) || grid.UnvisitedSeconds < seen.UnvisitedSeconds)
                        grids[grid.Id] = grid;
        online = grids.Values.Count(x => x.IsNpc);
        offlineNpc = 0;
        foreach (OfflinePartition row in offline)
            foreach (NpcGrid grid in row.Npc)
                if (!grids.ContainsKey(grid.Id))
                {
                    grids[grid.Id] = grid with { Offline = true };
                    if (grid.IsNpc) offlineNpc++;
                }
        return [.. grids.Values];
    }
}
