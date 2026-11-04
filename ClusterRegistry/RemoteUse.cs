namespace ClusterRegistry;

internal sealed record RemoteUseReport(long Generation, ulong[] Users);

internal sealed partial class GatewayRegistry
{
    private readonly Dictionary<ulong, (string Node, long Epoch, long Generation, ulong[] Users)> _remoteUse = [];

    /// <summary>Heartbeat presence is ownership-fenced and volatile, just like client bindings.
    /// A transfer or node epoch change invalidates it immediately; a subsequent empty heartbeat clears it.</summary>
    internal void ReportRemoteUse(string node, long epoch, PartitionVersion[] ownerships, Dictionary<ulong, RemoteUseReport>? reports)
    {
        lock (_lock)
        {
            if (!_state.Nodes.TryGetValue(node, out var registered) || registered.Epoch != epoch)
                throw new RegistryConflictException("Remote-use report has a stale node epoch");
            // A registration/liveness heartbeat has no presence snapshot. Never interpret it as a close.
            if (reports == null) return;
            foreach (var reported in ownerships ?? [])
            {
                if (!_state.Partitions.TryGetValue(reported.Partition, out var owner) || owner.Node != node
                    || owner.Epoch != epoch || owner.Generation != reported.Generation) continue;
                // Both samples must name this generation; a delayed game-thread snapshot must not
                // acquire the newer generation sampled by the heartbeat thread.
                // Missing means this ownership was not yet in the game-thread sample. A clear
                // must be an explicit empty record carrying that sample's generation too.
                if (!reports.TryGetValue(reported.Partition, out var report) || report.Generation != reported.Generation) continue;
                _remoteUse.Remove(reported.Partition);
                if (report?.Users is { Length: > 0 })
                    _remoteUse[reported.Partition] = (node, epoch, reported.Generation, report.Users.Where(id => id != 0).Distinct().Take(1024).ToArray());
            }
        }
    }

    internal bool HasRemoteUse(ulong partition)
    {
        lock (_lock) return _remoteUse.TryGetValue(partition, out var report) && report.Users.Length > 0
            && _state.Partitions.TryGetValue(partition, out var owner) && owner.Node == report.Node
            && owner.Epoch == report.Epoch && owner.Generation == report.Generation;
    }
}
