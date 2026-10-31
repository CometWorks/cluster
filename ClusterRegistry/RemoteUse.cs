namespace ClusterRegistry;

internal sealed partial class GatewayRegistry
{
    private readonly Dictionary<ulong, (string Node, long Epoch, long Generation, ulong[] Users)> _remoteUse = [];

    /// <summary>Heartbeat presence is ownership-fenced and volatile, just like client bindings.
    /// A transfer or node epoch change invalidates it immediately; a subsequent empty heartbeat clears it.</summary>
    internal void ReportRemoteUse(string node, long epoch, Dictionary<ulong, ulong[]>? reports)
    {
        lock (_lock)
        {
            if (!_state.Nodes.TryGetValue(node, out var registered) || registered.Epoch != epoch)
                throw new RegistryConflictException("Remote-use report has a stale node epoch");
            foreach (ulong old in _remoteUse.Where(x => x.Value.Node == node).Select(x => x.Key).ToArray()) _remoteUse.Remove(old);
            foreach (var report in reports ?? [])
                if (report.Value is { Length: > 0 } && _state.Partitions.TryGetValue(report.Key, out var owner) && owner.Node == node && owner.Epoch == epoch)
                    _remoteUse[report.Key] = (node, epoch, owner.Generation, report.Value.Where(id => id != 0).Distinct().Take(1024).ToArray());
        }
    }

    internal bool HasRemoteUse(ulong partition)
    {
        lock (_lock) return _remoteUse.TryGetValue(partition, out var report) && report.Users.Length > 0
            && _state.Partitions.TryGetValue(partition, out var owner) && owner.Node == report.Node
            && owner.Epoch == report.Epoch && owner.Generation == report.Generation;
    }
}
