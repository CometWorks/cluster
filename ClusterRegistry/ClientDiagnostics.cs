using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal sealed partial class GatewayRegistry
{
    public Admin.RotationStatus RotationStatus()
    {
        lock (_lock)
        {
            Admin.RotationStatus status = _state.Rotation;
            return status with { Nodes = status.ValidUntil > _clock()
                ? status.Nodes.Where(AssignmentMatches).ToArray() : [] };
        }
    }

    private bool AssignmentMatches(Admin.RotationAssignment assignment) =>
        _state.Nodes.TryGetValue(assignment.Node, out var node) && node.Epoch == assignment.Epoch
        && node.Role == NodeRole.Regular && node.State == NodeState.Active && node.Serving && !node.Spare;

    public long PublishRotation(Admin.RotationPublication publication)
    {
        lock (_lock)
        {
            DateTimeOffset now = _clock();
            if (publication.ExpectedRevision != _state.Rotation.Revision)
                throw new RegistryConflictException("Rotation publication revision is stale.");
            if (string.IsNullOrWhiteSpace(publication.Source) || publication.Source.Length > 128
                || publication.ValidUntil <= now || publication.ValidUntil > now.AddMinutes(5)
                || publication.Nodes == null || publication.Nodes.Length > 4096
                || !publication.Enabled && publication.Nodes.Length != 0
                || publication.Nodes.Any(n => n == null || string.IsNullOrWhiteSpace(n.Node) || n.Epoch <= 0 || n.DueAt < DateTimeOffset.UnixEpoch)
                || publication.Nodes.Select(n => n.Node).Distinct(StringComparer.Ordinal).Count() != publication.Nodes.Length)
                throw new ArgumentException("Rotation needs a source, a lease of at most five minutes, and unique incarnation-bound assignments; disabled rotation has no assignments.");
            if (publication.Nodes.Any(n => !AssignmentMatches(n)))
                throw new RegistryConflictException("A rotation assignment no longer names a serving regular-node incarnation.");
            long revision = checked(_state.Rotation.Revision + 1);
            _state.Rotation = new(revision, publication.Source, publication.ValidUntil, publication.Enabled, publication.Nodes.ToArray());
            Emit("rotation.published", data: new Dictionary<string, string>
            {
                ["source"] = publication.Source, ["revision"] = revision.ToString(), ["nodes"] = publication.Nodes.Length.ToString()
            });
            Persist();
            return revision;
        }
    }

    public Admin.DiagnosticsSnapshot ClientDiagnostics(string nodeId, long? epoch = null)
    {
        lock (_lock)
        {
            NodeRegistration current = _state.Nodes.GetValueOrDefault(nodeId)
                ?? throw new ArgumentException("Unknown diagnostic node.");
            if (epoch != null && (current.Epoch != epoch || current.State is not (NodeState.Active or NodeState.Closed)))
                throw new RegistryConflictException("Diagnostic node incarnation is no longer active.");
            DateTimeOffset now = _clock();
            Admin.RotationStatus rotation = RotationStatus();
            bool published = rotation.ValidUntil > now;
            var assignments = rotation.Nodes.ToDictionary(n => n.Node, StringComparer.Ordinal);
            Admin.NodeDiagnostics MapDiagnostic(NodeRegistration row)
            {
                var result = new Admin.NodeDiagnostics
                {
                    Name = row.Node, Endpoint = row.Endpoint, Epoch = row.Epoch, State = row.State.ToString(),
                    AgeSeconds = Math.Max(0, (long)(now - row.Registered).TotalSeconds),
                    ServingSeconds = row.Activated is { } activated ? Math.Max(0, (long)(now - activated).TotalSeconds) : -1,
                    DrainAt = row.ForceDrainAt?.ToUnixTimeSeconds() ?? 0, Serving = row.Serving, Spare = row.Spare,
                    RotationExpiresAt = published ? rotation.ValidUntil!.Value.ToUnixTimeSeconds() : 0,
                    Authority = row.Role == NodeRole.WA, AuthorityGeneration = row.AuthorityGeneration,
                    LeaseAt = row.LeaseExpires.ToUnixTimeSeconds()
                };
                if (row.State is NodeState.Dead or NodeState.Empty) result.Rotation = Admin.RotationState.Stopped;
                else if (row.State == NodeState.Closed) result.Rotation = Admin.RotationState.Draining;
                else if (row.ForceDrainAt != null) result.Rotation = Admin.RotationState.ForceDrainArmed;
                else if (row.Spare) result.Rotation = Admin.RotationState.HotSpare;
                else if (!row.Serving) result.Rotation = Admin.RotationState.NotServing;
                else if (published && !rotation.Enabled) result.Rotation = Admin.RotationState.Disabled;
                else if (published && assignments.TryGetValue(row.Node, out var assignment))
                {
                    result.Rotation = Admin.RotationState.Assigned;
                    result.RotationDueAt = assignment.DueAt.ToUnixTimeSeconds();
                }
                return result;
            }
            Admin.NodeDiagnostics[] nodes = _state.Nodes.Values.Where(n => n.State is NodeState.Active or NodeState.Closed)
                .OrderByDescending(n => n.Role == NodeRole.WA).ThenBy(n => n.Node, StringComparer.Ordinal).Select(MapDiagnostic).ToArray();
            return new Admin.DiagnosticsSnapshot
            {
                Current = MapDiagnostic(current), RegistryAvailable = true,
                CapturedAt = now.ToUnixTimeSeconds(), ServerAt = now.ToUnixTimeSeconds(),
                TotalNodes = nodes.Length, Nodes = nodes.Take(Admin.DiagnosticsProtocol.MaxNodes).ToArray()
            };
        }
    }
}
