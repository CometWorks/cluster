using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal sealed partial class DirectGateway
{
    private void HandleExecutorRequest(HttpListenerContext context)
    {
        // Never inherit the unauthenticated loopback development administrator.
        string bearer = context.Request.Headers["Authorization"] ?? "";
        AdminActor? actor = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? _adminTokens?.Match(bearer[7..]) : null;
        if (actor?.Scope != Admin.AdminScope.Executor)
        {
            WriteAdminError(context, 403, "executor_credential_required", "A host-scoped executor credential is required.");
            return;
        }
        if (context.Request.HttpMethod != "POST")
        {
            WriteAdminError(context, 405, "method_not_allowed", "Executor heartbeat requires POST.");
            return;
        }
        if (_registry is null)
        {
            WriteAdminError(context, 503, "registry_unavailable", "Gateway Registry is not configured.");
            return;
        }
        try
        {
            // Bound reads even when the caller uses chunked encoding.
            using var body = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = context.Request.InputStream.Read(buffer)) != 0)
            {
                if (body.Length + read > 1024 * 1024) throw new ArgumentException("Executor request exceeds 1 MiB.");
                body.Write(buffer, 0, read);
            }
            var request = JsonSerializer.Deserialize<Admin.ExecutorPollRequest>(body.ToArray(), AdminJson)
                ?? throw new ArgumentException("Executor request is required.");
            if (request.Host != actor.Name)
            {
                WriteAdminError(context, 403, "executor_host_mismatch", "Credential does not own the requested host.");
                return;
            }
            WriteAdminEnvelope(context, _registry.PollExecutor(request));
        }
        catch (RegistryConflictException error) { WriteAdminError(context, 409, "executor_fenced", error.Message); }
        catch (Exception error) when (error is ArgumentException or JsonException)
        { WriteAdminError(context, 400, "invalid_executor_request", error.Message); }
    }
}

internal sealed partial class GatewayRegistry
{
    // Leases are deliberately boot-local. Gateway restart invalidates every outstanding
    // executor lease; persisted process records, not stale plans, drive re-adoption.
    private readonly Dictionary<string, ExecutorLease> _executorLeases = new(StringComparer.Ordinal);
    private sealed record ExecutorLease(Guid Session, Guid Lease, DateTimeOffset Expires,
        long Sequence, string Fingerprint);

    internal Admin.ExecutorPollResult PollExecutor(Admin.ExecutorPollRequest request)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(request.Host) || request.Host.Length > 128
                || string.IsNullOrWhiteSpace(request.Executor) || request.Executor.Length > 128
                || request.Session == Guid.Empty || request.Sequence <= 0
                || request.Reports is null || request.Reports.Length > Admin.ExecutorProtocol.MaxReports
                || request.Reports.Any(report => report is null)
                || request.Reports.Select(report => report.SlotKey).Distinct(StringComparer.Ordinal).Count() != request.Reports.Length)
                throw new ArgumentException("Invalid executor identity, sequence or report batch.");
            EnsurePersistenceHealthy();
            DateTimeOffset now = _clock();
            _executorLeases.TryGetValue(request.Host, out var current);
            // T-0321 P4: a remote host's executor lease is held inside a network window, like its nodes' leases.
            bool active = current is not null && (current.Expires > now || ExecutorLeaseHeld(request.Host, now));
            if (request.Lease is null)
            {
                if (request.Reports.Length != 0) throw new ArgumentException("Lease acquisition cannot report observations.");
                if (active && current!.Session != request.Session)
                    throw new RegistryConflictException("Another executor session owns this host.");
                current = active ? current : new(request.Session, Guid.NewGuid(), now, 0, "");
            }
            else if (!active || current!.Lease != request.Lease || current.Session != request.Session)
                throw new RegistryConflictException("Executor lease expired or belongs to another Gateway/session.");
            string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
            if (request.Sequence < current!.Sequence
                || request.Sequence == current.Sequence && hash != current.Fingerprint)
                throw new RegistryConflictException("Executor sequence was replayed with different or stale content.");

            if (request.Sequence > current.Sequence)
            {
                // Validate the whole batch before mutating a slot. In particular, Gone must
                // acknowledge the exact incumbent; old reports cannot release a successor.
                foreach (var report in request.Reports)
                {
                    ValidateManagedReport(request, report);
                    if (string.IsNullOrWhiteSpace(report.SlotKey) || string.IsNullOrWhiteSpace(report.AttemptKey)
                        || report.AttemptKey.Length > 128 || report.Failure?.Length > 2048 || !Enum.IsDefined(report.State))
                        throw new ArgumentException("Invalid executor observation.");
                    if (!_state.NodePlan.TryGetValue(report.SlotKey, out var slot) || slot.Host != request.Host)
                        throw new RegistryConflictException("Observation does not belong to this host's plan.");
                    if (report.Epoch < 0 || report.Epoch > 0 && (report.Node is null
                        || !_state.Nodes.TryGetValue(report.Node, out var node) || node.Epoch != report.Epoch))
                        throw new RegistryConflictException("Observation names a stale node incarnation.");
                    // #1452: a process that registers between the Host reading its receipt and sending the report
                    // is still reported as Spawning without an epoch. That repeats the slot's observation, replaces nothing.
                    bool repeatsSpawning = report.State == Admin.NodeObservation.Spawning
                        && slot.Observed == SpawnState.Spawning && slot.ObservedNode == report.Node;
                    if (report.Epoch == 0 && !repeatsSpawning && slot.ObservedNode is { } observed
                        && _state.Nodes.TryGetValue(observed, out var incumbent)
                        && incumbent.State == NodeState.Active && incumbent.LeaseExpires > now)
                        throw new RegistryConflictException("An unfenced observation cannot replace a live incarnation.");
                    if (report.State == Admin.NodeObservation.Ready && (report.Epoch <= 0
                        || _state.Nodes[report.Node!].State == NodeState.Active
                            && !NodeLeaseLive(_state.Nodes[report.Node!], now)   // #1218: a held lease is live
                        || _state.Nodes[report.Node!].Role != slot.Role))
                        throw new RegistryConflictException("Ready requires a live matching Registry incarnation.");
                    if (report.State == Admin.NodeObservation.Gone && slot.IncumbentAction == NodeIncumbentAction.Kill
                        && (report.Node != slot.IncumbentNode || report.Epoch != slot.IncumbentEpoch))
                        throw new RegistryConflictException("Gone does not acknowledge the commanded incumbent.");
                }
                // A current process can report its last ready receipt after Registry starts a drain.
                // Treat that as running, never as fresh admission proof; still return its stop plan.
                request = request with { Reports = request.Reports.Select(report =>
                    report.State == Admin.NodeObservation.Ready && _state.Nodes[report.Node!].State != NodeState.Active
                        ? report with { State = Admin.NodeObservation.Spawning } : report).ToArray() };
                long before = _state.StateSequence;
                ApplyExecutorObservations(request.Executor, request.Reports.Select(report => new SpawnObservation(
                    report.SlotKey, report.AttemptKey, report.State switch
                    {
                        Admin.NodeObservation.Missing => SpawnState.Missing,
                        Admin.NodeObservation.Spawning => SpawnState.Spawning,
                        Admin.NodeObservation.Ready => SpawnState.Ready,
                        Admin.NodeObservation.Gone => SpawnState.Gone,
                        _ => SpawnState.Failed,
                    }, report.Node, report.Failure, Epoch: report.Epoch)).ToArray());
                RecordManagedReports(request);
                // Steady-state freshness must not flush unrelated pending world mutations.
                if (_state.StateSequence != before) Flush();
            }
            current = current with { Expires = now.AddSeconds(Admin.ExecutorProtocol.LeaseSeconds),
                Sequence = request.Sequence, Fingerprint = hash };
            _executorLeases[request.Host] = current;
            return new(current.Lease, current.Expires, AdminNodePlan().Where(slot => slot.Host == request.Host).ToArray());
        }
    }
}
