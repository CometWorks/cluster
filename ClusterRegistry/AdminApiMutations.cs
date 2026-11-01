using System.Net;
using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>
/// The mutating half of the admin contract (Orchestration.md §11). Every route goes
/// through BeginMutation: manage scope, a required Idempotency-Key, and one durable
/// operation record whose outcome a retry replays instead of repeating the effect.
/// </summary>
internal sealed partial class DirectGateway
{
    private void HandleAdminMutation(HttpListenerContext context, string path, AdminActor actor)
    {
        if (_registry == null)
        {
            WriteAdminError(context, 503, "registry_unavailable", "Gateway Registry is not configured.");
            return;
        }

        string prefix = Admin.AdminProtocol.RoutePrefix;
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/shutdown", StringComparison.OrdinalIgnoreCase))
        {
            AdminShutdown(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/gateway/restart", StringComparison.OrdinalIgnoreCase))
        {
            AdminGatewayRestart(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/save-all", StringComparison.OrdinalIgnoreCase))
        {
            AdminSaveAll(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "PUT" && path.Equals(prefix + "/rotation", StringComparison.OrdinalIgnoreCase))
        {
            AdminPublishRotation(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "PUT" && path.Equals(prefix + "/config", StringComparison.OrdinalIgnoreCase))
        {
            AdminPutConfig(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "PUT" && path.Equals(prefix + "/handover-config", StringComparison.OrdinalIgnoreCase))
        {
            AdminPutHandoverConfig(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/world-authority/move", StringComparison.OrdinalIgnoreCase))
        {
            AdminWorldAuthorityMove(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "POST"
            && path.StartsWith(prefix + "/triggers/", StringComparison.OrdinalIgnoreCase))
        {
            AdminTrigger(context, actor, path[(prefix + "/triggers/").Length..]);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/admission/bans", StringComparison.OrdinalIgnoreCase))
        {
            AdminBan(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/chat", StringComparison.OrdinalIgnoreCase))
        {
            AdminChatSend(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "POST"
            && path.StartsWith(prefix + "/ship-storage/takes/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/settle", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(path[(prefix + "/ship-storage/takes/").Length..^"/settle".Length], out Guid settleShip))
        {
            AdminShipStorageSettle(context, actor, settleShip);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/world-exports", StringComparison.OrdinalIgnoreCase))
        {
            AdminWorldExport(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/registry-backups", StringComparison.OrdinalIgnoreCase))
        {
            AdminRegistryBackup(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "DELETE"
            && path.StartsWith(prefix + "/artifacts/", StringComparison.OrdinalIgnoreCase))
        {
            AdminReleaseArtifact(context, actor, path[(prefix + "/artifacts/").Length..]);
            return;
        }
        if (context.Request.HttpMethod == "DELETE"
            && path.StartsWith(prefix + "/admission/bans/", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(path[(prefix + "/admission/bans/").Length..], out ulong unbanUser))
        {
            AdminUnban(context, actor, unbanUser);
            return;
        }
        if (context.Request.HttpMethod == "POST"
            && path.StartsWith(prefix + "/clients/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/kick", StringComparison.OrdinalIgnoreCase))
        {
            string idPart = path[(prefix + "/clients/").Length..^"/kick".Length];
            if (ulong.TryParse(idPart, out ulong kickClient))
            {
                AdminKickClient(context, actor, kickClient);
                return;
            }
        }
        if (context.Request.HttpMethod == "PUT"
            && path.StartsWith(prefix + "/node-control/hosts/", StringComparison.OrdinalIgnoreCase))
        {
            AdminNodeControlSetHost(context, actor, Uri.UnescapeDataString(path[(prefix + "/node-control/hosts/").Length..]));
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/node-control/remove", StringComparison.OrdinalIgnoreCase))
        {
            AdminNodeControlStop(context, actor, "remove");
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/node-control/recycle", StringComparison.OrdinalIgnoreCase))
        {
            AdminNodeControlStop(context, actor, "recycle");
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals(prefix + "/node-control/hosts-file", StringComparison.OrdinalIgnoreCase))
        {
            AdminNodeControlHostsFile(context, actor);
            return;
        }
        if (context.Request.HttpMethod == "POST"
            && path.StartsWith(prefix + "/nodes/", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = path[(prefix + "/nodes/").Length..];
            int separator = remainder.LastIndexOf('/');
            if (separator > 0)
            {
                string slot = Uri.UnescapeDataString(remainder[..separator]);
                string action = remainder[(separator + 1)..];
                if (action.Equals("close", StringComparison.OrdinalIgnoreCase))
                {
                    AdminCloseNode(context, actor, slot);
                    return;
                }
                if (action.Equals("kill", StringComparison.OrdinalIgnoreCase))
                {
                    AdminKillNode(context, actor, slot);
                    return;
                }
            }
        }

        WriteAdminError(context, 404, "route_not_found", "Unknown Gateway admin contract route.");
    }

    private void AdminShutdown(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Admin.ShutdownRequest>(context) ?? new Admin.ShutdownRequest();
        if (request.GraceSeconds < 0 || request.ForceAfterSeconds < 0)
        {
            WriteAdminError(context, 400, "invalid_arguments", "Shutdown timings must be non-negative.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "shutdown", Fingerprint(request), "cluster",
            out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            // The drain runs in the background; the caller polls the operation. Unlike the
            // legacy CLI route, Down keeps this API process alive (model C): final OS
            // process teardown belongs to the packaged cluster CLI / host executor.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ClusterLifecycle lifecycle = ShutdownCluster(request.Emergency,
                        TimeSpan.FromSeconds(request.GraceSeconds + 30),
                        TimeSpan.FromSeconds(request.Emergency ? 0 : request.GraceSeconds));
                    _registry!.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
                    {
                        ["phase"] = lifecycle.Phase.ToString(),
                        ["emergency"] = request.Emergency ? "true" : "false",
                    });
                }
                catch (Exception e)
                {
                    _registry!.FailAdminOperation(record.OperationId, "shutdown_failed", e.Message);
                }
            });
        }
        WriteOperation(context, record);
    }

    private void AdminGatewayRestart(HttpListenerContext context, AdminActor actor)
    {
        AdminOperationRecord? record = BeginMutation(context, actor, "gateway-restart", "", "gateway", out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            // Orchestration §9.5: a Gateway restart is a client-only event - nodes keep
            // simulating and rejoin the new incarnation. The record is completed and the WAL
            // checkpointed BEFORE the process exits, so the retried operation replays as
            // Succeeded; bringing the process back is the supervisor's job (systemd, CLI).
            _registry!.CompleteAdminOperation(record.OperationId,
                new Dictionary<string, string> { ["exiting"] = "true" });
            _registry.Checkpoint();
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(200); Stop(); });
        }
        WriteOperation(context, record);
    }

    private void AdminSaveAll(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Admin.SaveAllRequest>(context) ?? new Admin.SaveAllRequest();
        AdminOperationRecord? record = BeginMutation(context, actor, "save-all", Fingerprint(request), "cluster",
            out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int nodes = TriggerNodes("save", request.Sweep ? "sweep=true" : null);
                    string snapshotId = "save-all-" + record.OperationId;
                    SnapshotRegistration snapshot = _registry!.CreateOnlineSnapshot(snapshotId,
                        DateTimeOffset.UtcNow.UtcTicks);
                    _registry.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
                    {
                        ["snapshotId"] = snapshot.SnapshotId,
                        ["nodesTriggered"] = nodes.ToString(),
                        ["dirty"] = snapshot.Dirty.Length.ToString(),
                        // An online cut is never labeled cleaner than the cluster can prove.
                        ["consistency"] = snapshot.Dirty.Length == 0 ? "CutConsistent" : "CrashConsistent",
                    });
                }
                catch (Exception e)
                {
                    _registry!.FailAdminOperation(record.OperationId, "save_all_failed", e.Message);
                }
            });
        }
        WriteOperation(context, record);
    }

    private void AdminPutConfig(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Admin.AdminConfigUpdate>(context);
        if (request == null || request.Slots == null || request.Slots.Length == 0
            || request.Slots.Any(x => string.IsNullOrWhiteSpace(x.SlotKey) || string.IsNullOrWhiteSpace(x.Host)))
        {
            WriteAdminError(context, 400, "invalid_arguments",
                "PUT /config requires expectedRevision and at least one slot with slotKey and host.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "config", Fingerprint(request), "policy",
            out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            try
            {
                long revision = _registry!.AdminSetSlots(request.ExpectedRevision, request.Slots);
                _registry.CompleteAdminOperation(record.OperationId,
                    new Dictionary<string, string> { ["revision"] = revision.ToString() });
            }
            catch (RegistryConflictException conflict)
            {
                _registry!.FailAdminOperation(record.OperationId, "revision_mismatch", conflict.Message);
            }
        }
        WriteOperation(context, record);
    }

    /// <summary>
    /// HandoverImprovements.md §2: PUT merges {key: value} (null removes) into the durable document and
    /// fans it out to every Active node's /cluster/config; a node that misses the fan-out pulls it on
    /// its next heartbeat (HandoverConfigRevision). The nodes validate keys and values - an unknown key
    /// is reported by the first node that refuses it, in the operation's detail.
    /// </summary>
    private void AdminPutHandoverConfig(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Dictionary<string, string?>>(context);
        if (request == null || request.Count == 0)
        {
            WriteAdminError(context, 400, "invalid_arguments", "PUT /handover-config requires a non-empty {key: value} object.");
            return;
        }
        // T-0400: a knob the registry acts on itself is refused before the operation is recorded.
        if (request.TryGetValue(GatewayRegistry.ArchiveFileDaysKey, out string? archiveFileDays) && archiveFileDays != null)
        {
            try { GatewayRegistry.NormalizeArchiveFileDays(archiveFileDays); }
            catch (ArgumentException error)
            {
                WriteAdminError(context, 400, "invalid_arguments", error.Message);
                return;
            }
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "handover-config", Fingerprint(request), "cluster", out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            HandoverConfigDocument document = _registry!.SetHandoverConfig(request);
            Dictionary<string, string> detail = new() { ["revision"] = document.Revision.ToString() };
            string body = JsonSerializer.Serialize(document.Values);
            foreach (NodeRegistration node in _registry.Nodes().Where(x => x.State == NodeState.Active && x.ControlEndpoint != null))
            {
                try
                {
                    using StringContent content = new(body, System.Text.Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = _http.PutAsync(node.ControlEndpoint!.TrimEnd('/') + "/cluster/config", content).GetAwaiter().GetResult();
                    detail[node.Node] = response.IsSuccessStatusCode ? "applied"
                        : "refused " + (int)response.StatusCode + ": " + response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                }
                catch (Exception failure)
                {
                    detail[node.Node] = "unreachable: " + failure.Message;
                }
            }
            _registry.CompleteAdminOperation(record.OperationId, detail);
        }
        WriteOperation(context, record);
    }

    private void AdminCloseNode(HttpListenerContext context, AdminActor actor, string slot)
    {
        var request = ReadAdminBody<Admin.CloseNodeRequest>(context) ?? new Admin.CloseNodeRequest();
        Admin.NodePlan? plan = _registry!.AdminNodePlan().FirstOrDefault(x => x.SlotKey == slot);
        if (plan?.ObservedNode == null)
        {
            WriteAdminError(context, 404, "not_found", $"Slot '{slot}' observes no node to close.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "node-close",
            Fingerprint(request) + "|" + slot, slot, out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            ThreadPool.QueueUserWorkItem(async _ =>
            {
                try
                {
                    NodeRegistration closed = await CloseNode(
                        TimeSpan.FromSeconds(request.ForceAfterSeconds ?? 900), forceOnly: false, plan.ObservedNode);
                    _registry.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
                    {
                        ["node"] = closed.Node,
                        ["epoch"] = closed.Epoch.ToString(),
                        ["forceDrainAt"] = closed.ForceDrainAt?.ToString("O") ?? string.Empty,
                    });
                }
                catch (Exception e)
                {
                    _registry.FailAdminOperation(record.OperationId, "node_close_failed", e.Message);
                }
            });
        }
        WriteOperation(context, record);
    }

    private void AdminKillNode(HttpListenerContext context, AdminActor actor, string slot)
    {
        var request = ReadAdminBody<Admin.KillNodeRequest>(context);
        if (request == null || string.IsNullOrWhiteSpace(request.Node))
        {
            WriteAdminError(context, 400, "invalid_arguments",
                "Force removal must name the exact node and epoch; a bare slot is not a target.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "node-kill",
            Fingerprint(request) + "|" + slot, slot, out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            try
            {
                _registry!.AdminKillNode(slot, request.Node, request.Epoch);
                _registry.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
                {
                    ["node"] = request.Node,
                    ["epoch"] = request.Epoch.ToString(),
                });
            }
            catch (RegistryConflictException conflict)
            {
                _registry!.FailAdminOperation(record.OperationId, "conflict", conflict.Message);
            }
        }
        WriteOperation(context, record);
    }

    // ---------------------------------------------------------------- T-0320 node control

    /// <summary>GET /admin/v1/node-control, with the gateway's own view of connected clients and capacity.</summary>
    private Admin.NodeControlView NodeControlView()
    {
        Dictionary<IPEndPoint, int> sessions = _clients.Values.GroupBy(x => x.BackendEndpoint).ToDictionary(x => x.Key, x => x.Count());
        Dictionary<string, int> clients = [];
        foreach (NodeRegistration node in _registry!.Nodes())
            if (EndpointParser.TryParse(node.Endpoint, DirectTransportConstants.DefaultPort, out IPEndPoint? endpoint) && endpoint != null)
                clients[node.Node] = sessions.GetValueOrDefault(endpoint);
        return _registry.NodeControlView(clients, CapacityWarningFor(_registry.ServingTargetTotal()),
            Environment.MachineName.Split('.')[0], Environment.ProcessId);
    }

    /// <summary>
    /// One node-control mutation: manage scope, Idempotency-Key, and a durable operation that finishes at once (the
    /// convergence is watched in GET node-control). A request that cannot apply (managed, unknown selector or host,
    /// a repeated stop) is answered 4xx with its code and records no operation - unless its key already names one,
    /// which then replays (a retried remove whose node has since left must not turn into a 404).
    /// </summary>
    private void AdminNodeControlMutation<T>(HttpListenerContext context, AdminActor actor, string kind, string target, T? request,
        Func<string?> check, Func<NodeControlChange> execute) where T : class
    {
        if (!RequireManage(context, actor, kind))
            return;
        string fingerprintTarget = target;
        string? key = context.Request.Headers["Idempotency-Key"];
        if (!string.IsNullOrWhiteSpace(key) && !_registry!.AdminOperationKeyExists(key))
        {
            if (request == null)
            {
                WriteAdminError(context, 400, "invalid_arguments", $"{kind} requires a JSON body.");
                return;
            }
            try { target = check() ?? target; }
            catch (NodeControlException refused)
            {
                WriteAdminError(context, refused.Status, refused.Code, refused.Message);
                return;
            }
        }
        // The fingerprint binds the key to the request as sent (a selector, not what it resolved to); the target is the resolved id.
        AdminOperationRecord? record = BeginMutation(context, actor, kind, Fingerprint(request) + "|" + fingerprintTarget, target, out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            try
            {
                NodeControlChange change = execute();
                var result = new Dictionary<string, string>
                {
                    ["revision"] = change.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["actions"] = JsonSerializer.Serialize(change.Actions),
                    ["warnings"] = JsonSerializer.Serialize(change.Warnings),
                };
                if (change.Node != null) result["node"] = change.Node;
                if (change.Replacement != null) result["replacement"] = change.Replacement;
                _registry!.CompleteAdminOperation(record.OperationId, result);
            }
            catch (NodeControlException refused) { _registry!.FailAdminOperation(record.OperationId, refused.Code, refused.Message); }
            catch (RegistryConflictException conflict) { _registry!.FailAdminOperation(record.OperationId, "conflict", conflict.Message); }
        }
        WriteOperation(context, record);
    }

    private void AdminNodeControlSetHost(HttpListenerContext context, AdminActor actor, string host)
    {
        var request = ReadAdminBody<Admin.HostTargetsUpdate>(context);
        AdminNodeControlMutation(context, actor, "node-control-set", host, request,
            () => { _registry!.CheckHostTargets(host, request!.Serving, request.Spares); return null; },
            () =>
            {
                NodeControlChange change = _registry!.SetHostTargets(host, request!.Serving, request.Spares, actor.Name,
                    request.RequestedBy, request.Reason, CapacityWarningFor);
                return change;
            });
    }

    private void AdminNodeControlStop(HttpListenerContext context, AdminActor actor, string kind)
    {
        var request = ReadAdminBody<Admin.NodeStopRequest>(context);
        if (request is { TimeoutSeconds: < 0 })
        {
            WriteAdminError(context, 400, "invalid_arguments", "timeoutSeconds must be non-negative.");
            return;
        }
        TimeSpan? timeout = request?.TimeoutSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : null;
        AdminNodeControlMutation(context, actor, "node-control-" + kind, request?.Selector ?? "", request,
            () => _registry!.CheckStop(request!.Selector, kind, request.Now),
            () => kind == "remove"
                ? _registry!.RemoveNode(request!.Selector, request.Now, timeout, actor.Name, request.RequestedBy, request.Reason)
                : _registry!.RecycleNode(request!.Selector, request.Now, timeout, actor.Name, request.RequestedBy, request.Reason));
    }

    private void AdminNodeControlHostsFile(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Admin.HostsFileApply>(context);
        Dictionary<string, HostTarget>? hosts = request?.Hosts?.ToDictionary(x => x.Key,
            x => new HostTarget(x.Value.Serving, x.Value.Spares, x.Value.RamFloorMb, x.Value.DiskFloorMb));
        AdminNodeControlMutation(context, actor, "node-control-hosts-file", request?.Sha256 ?? "", request,
            () => { _registry!.CheckHostsFile(hosts); return null; },
            () => _registry!.ApplyHostsFile(hosts, request!.Sha256, actor.Name));
    }

    private void AdminWorldAuthorityMove(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Admin.WorldAuthorityMoveRequest>(context) ?? new Admin.WorldAuthorityMoveRequest();
        Admin.WorldAuthorityStatus authority = _registry!.AdminWorldAuthority();
        if (authority.Node == null)
        {
            WriteAdminError(context, 409, "conflict", "No World Authority is currently registered.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "wa-move", Fingerprint(request),
            authority.Node, out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            try
            {
                // v1 semantics: drain the current WA incarnation; the NodePlan's WA slot
                // respawns through the executor and the successor takes a bumped authority
                // generation at registration. Placement stays a decider/executor concern.
                NodeRegistration closed = _registry.CloseNode(authority.Node, authority.Epoch,
                    request.ForceAfterSeconds is { } force ? TimeSpan.FromSeconds(force) : null);
                _registry.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
                {
                    ["node"] = closed.Node,
                    ["epoch"] = closed.Epoch.ToString(),
                    ["generation"] = authority.Generation.ToString(),
                });
            }
            catch (RegistryConflictException conflict)
            {
                _registry.FailAdminOperation(record.OperationId, "conflict", conflict.Message);
            }
        }
        WriteOperation(context, record);
    }

    /// <summary>One stable operator view of every connected session, with the authoritative
    /// registry route (node/partition) when known. Registry values win over transport guesses.</summary>
    private Admin.ClientSummary[] AdminClients() =>
        _clients.Values.OrderBy(x => x.ClientId).Select(session => new Admin.ClientSummary(
            session.ClientId,
            session.Credential?.Name,
            session.PlayerReady?.Identity.IdentityId,
            session.AdmissionState,
            session.Credential?.IsAdmin,
            session.RegistryRoute?.Node,
            session.RegistryRoute?.Partition,
            session.ConnectedAt,
            session.LastClientActivity)).ToArray();

    private void AdminKickClient(HttpListenerContext context, AdminActor actor, ulong clientId)
    {
        var request = ReadAdminBody<Admin.KickClientRequest>(context) ?? new Admin.KickClientRequest();
        if (!_clients.TryGetValue(clientId, out ClientSession? session))
        {
            WriteAdminError(context, 404, "not_found", $"No connected client '{clientId}'.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "client-kick",
            Fingerprint(request) + "|" + clientId, clientId.ToString(), out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            // Same split as the legacy route: a punitive kick arms the anti-flap cooldown, a
            // reconnect rescues a stuck player without locking them out.
            if (!request.Reconnect)
                _admission.Kick(clientId);
            ForceReconnect(session, request.Reconnect ? "control-liveness-reconnect" : "kicked-by-admins");
            _registry!.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
            {
                ["client"] = clientId.ToString(),
                ["cooldown"] = request.Reconnect ? "false" : "true",
            });
        }
        WriteOperation(context, record);
    }

    private void AdminBan(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Admin.BanRequest>(context);
        if (request == null || request.UserId == 0)
        {
            WriteAdminError(context, 400, "invalid_arguments", "Ban requires a non-zero userId.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "ban",
            request.UserId.ToString(), request.UserId.ToString(), out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            _admission.Ban(request.UserId, true);
            _banStore?.Save(_admission.BannedUsers());
            if (_clients.TryGetValue(request.UserId, out ClientSession? banned))
                ForceReconnect(banned, "banned-by-admins");
            _registry!.CompleteAdminOperation(record.OperationId,
                new Dictionary<string, string> { ["userId"] = request.UserId.ToString(), ["banned"] = "true" });
        }
        WriteOperation(context, record);
    }

    private void AdminUnban(HttpListenerContext context, AdminActor actor, ulong userId)
    {
        AdminOperationRecord? record = BeginMutation(context, actor, "unban",
            userId.ToString(), userId.ToString(), out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            _admission.Ban(userId, false);
            _banStore?.Save(_admission.BannedUsers());
            _registry!.CompleteAdminOperation(record.OperationId,
                new Dictionary<string, string> { ["userId"] = userId.ToString(), ["banned"] = "false" });
        }
        WriteOperation(context, record);
    }

    private void AdminTrigger(HttpListenerContext context, AdminActor actor, string task)
    {
        task = task.ToLowerInvariant();
        if (!AdminTriggerTasks.Contains(task))
        {
            WriteAdminError(context, 400, "invalid_arguments",
                $"Unknown trigger task '{task}'; known tasks: {string.Join(", ", AdminTriggerTasks)}.");
            return;
        }
        // The scoping filters ride the query string like the legacy route; they are part of
        // the request identity, so they join the idempotency fingerprint.
        string fingerprint = task + "|" + (context.Request.QueryString["node"] ?? "") + "|"
            + (context.Request.QueryString["partition"] ?? "") + "|"
            + (context.Request.QueryString["unthrottled"] ?? "");
        AdminOperationRecord? record = BeginMutation(context, actor, "trigger", fingerprint, task, out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            try
            {
                // Same code path as the scheduler and the legacy route; policy still applies.
                object result = RunTrigger(context, task);
                _registry!.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
                {
                    ["task"] = task,
                    ["result"] = JsonSerializer.Serialize(result),
                });
            }
            catch (ArgumentException e)
            {
                _registry!.FailAdminOperation(record.OperationId, "invalid_arguments", e.Message);
            }
        }
        WriteOperation(context, record);
    }

    private void AdminReleaseArtifact(HttpListenerContext context, AdminActor actor, string artifactId)
    {
        if (_artifacts == null)
        {
            WriteAdminError(context, 503, "exports_unconfigured", "No export root is configured.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "artifact-release", artifactId,
            artifactId, out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            bool released = _artifacts.Release(artifactId);
            // #1275: a registry backup's retention hold ends with its artifact.
            _registry!.ReleaseBackupHold(artifactId);
            _registry!.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
            {
                ["artifactId"] = artifactId,
                // Releasing an already-gone artifact is success: the goal state holds either way.
                ["released"] = released ? "true" : "already-gone",
            });
        }
        WriteOperation(context, record);
    }

    /// <summary>
    /// Cluster chat rides the World Authority - the single accept point for global chat -
    /// never a random node (gap analysis §5.6). The WA control endpoint comes from the
    /// registry, so a replaced WA is followed automatically.
    /// </summary>
    private (string Control, long Epoch)? WorldAuthorityControl()
    {
        WorldAuthority authority = _registry!.WorldAuthority();
        return authority.Node == null || string.IsNullOrWhiteSpace(authority.ControlEndpoint)
            ? null
            : (authority.ControlEndpoint!.Replace("http://", string.Empty), authority.Epoch);
    }

    private void AdminChatSend(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Admin.ChatSendRequest>(context);
        if (request == null || string.IsNullOrWhiteSpace(request.Message) || request.Sender == 0)
        {
            WriteAdminError(context, 400, "invalid_arguments",
                "Chat send requires a message and a non-zero sender identity.");
            return;
        }
        if (WorldAuthorityControl() is not { } authority)
        {
            WriteAdminError(context, 503, "world_authority_unavailable",
                "No World Authority with a control endpoint is registered.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "chat", Fingerprint(request), "cluster",
            out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            try
            {
                string url = ClusterUrl(authority.Control, "chat-send")
                    + $"?sender={request.Sender}&text={Uri.EscapeDataString(request.Message)}";
                using HttpResponseMessage response = _http.Send(new HttpRequestMessage(HttpMethod.Post, url));
                response.EnsureSuccessStatusCode();
                _registry!.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
                {
                    ["sender"] = request.Sender.ToString(),
                    ["worldAuthorityEpoch"] = authority.Epoch.ToString(),
                });
            }
            catch (HttpRequestException e)
            {
                _registry!.FailAdminOperation(record.OperationId, "world_authority_unavailable", e.Message);
            }
        }
        WriteOperation(context, record);
    }

    private void AdminChatHistory(HttpListenerContext context)
    {
        if (WorldAuthorityControl() is not { } authority)
        {
            WriteAdminError(context, 503, "world_authority_unavailable",
                "No World Authority with a control endpoint is registered.");
            return;
        }
        long cursor = ParseLong(context, "cursor") ?? 0;
        long limit = ParseLong(context, "limit") ?? 100;
        try
        {
            string url = ClusterUrl(authority.Control, "chat-history") + $"?cursor={cursor}&limit={limit}";
            using HttpResponseMessage response = _http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            response.EnsureSuccessStatusCode();
            NodeChatHistory history = JsonSerializer.Deserialize<NodeChatHistory>(
                response.Content.ReadAsStream(), AdminJson) ?? new NodeChatHistory([], cursor);
            WriteAdminEnvelope(context, new Admin.ChatHistory(
                history.Messages.Select(x => new Admin.ChatMessage(x.Seq, x.At, x.Author, x.Channel, x.Text)).ToArray(),
                history.NextCursor, authority.Epoch));
        }
        catch (HttpRequestException e)
        {
            WriteAdminError(context, 503, "world_authority_unavailable",
                "World Authority chat history is unreachable: " + e.Message);
        }
    }

    private sealed record NodeChatHistory(NodeChatMessage[] Messages, long NextCursor);

    private sealed record NodeChatMessage(long Seq, DateTimeOffset At, ulong Author, byte Channel, string Text);

    /// <summary>#1316: the ship storage takes the World Authority holds; a stranded one waits for an admin.</summary>
    private void AdminShipStorageTakes(HttpListenerContext context)
    {
        if (WorldAuthorityControl() is not { } authority)
        {
            WriteAdminError(context, 503, "world_authority_unavailable",
                "No World Authority with a control endpoint is registered.");
            return;
        }
        try
        {
            using HttpResponseMessage response = _http.Send(new HttpRequestMessage(HttpMethod.Get,
                ClusterUrl(authority.Control, "ship-storage/takes")));
            response.EnsureSuccessStatusCode();
            Admin.ShipStorageTake[] takes = JsonSerializer.Deserialize<Admin.ShipStorageTake[]>(
                response.Content.ReadAsStream(), AdminJson) ?? [];
            WriteAdminEnvelope(context, new Admin.ShipStorageTakes(takes, authority.Epoch));
        }
        catch (HttpRequestException e)
        {
            WriteAdminError(context, 503, "world_authority_unavailable",
                "World Authority ship storage is unreachable: " + e.Message);
        }
    }

    /// <summary>
    /// #1316: settles a stranded ship storage take at the World Authority, after the admin looked for its grid:
    /// release lists the ship again, consume drops it. The WA refuses a take that is not the one held or not
    /// stranded yet (take_not_settled), so a retry or a stale list never settles a newer take.
    /// </summary>
    private void AdminShipStorageSettle(HttpListenerContext context, AdminActor actor, Guid ship)
    {
        var request = ReadAdminBody<Admin.ShipStorageSettleRequest>(context);
        if (request == null || request.Take == Guid.Empty || request.Action is not ("release" or "consume"))
        {
            WriteAdminError(context, 400, "invalid_arguments",
                "Settle requires the take id and an action, release or consume.");
            return;
        }
        if (WorldAuthorityControl() is not { } authority)
        {
            WriteAdminError(context, 503, "world_authority_unavailable",
                "No World Authority with a control endpoint is registered.");
            return;
        }
        AdminOperationRecord? record = BeginMutation(context, actor, "ship-storage-settle",
            Fingerprint(new { ship, request.Take, request.Action }), "ship-storage/" + ship, out bool replayed);
        if (record == null)
            return;
        if (!replayed)
        {
            try
            {
                string url = ClusterUrl(authority.Control, "ship-storage/settle")
                    + $"?id={ship}&take={request.Take}&action={request.Action}";
                using HttpResponseMessage response = _http.Send(new HttpRequestMessage(HttpMethod.Post, url));
                if (response.StatusCode == HttpStatusCode.Conflict)
                    _registry!.FailAdminOperation(record.OperationId, "take_not_settled",
                        response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim());
                else
                {
                    response.EnsureSuccessStatusCode();
                    _registry!.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
                    {
                        ["ship"] = ship.ToString(),
                        ["take"] = request.Take.ToString(),
                        ["settled"] = request.Action,
                        ["worldAuthorityEpoch"] = authority.Epoch.ToString(),
                    });
                }
            }
            catch (HttpRequestException e)
            {
                _registry!.FailAdminOperation(record.OperationId, "world_authority_unavailable", e.Message);
            }
        }
        WriteOperation(context, record);
    }

    /// <summary>Runs one named trigger on every node control endpoint; returns how many were reached.</summary>
    private int TriggerNodes(string task, string? query)
    {
        int reached = 0;
        foreach (NodeRegistration node in _registry?.Nodes() ?? [])
        {
            if (node.ControlEndpoint == null || node.State != NodeState.Active) continue;
            string url = ClusterUrl(node.ControlEndpoint.Replace("http://", string.Empty), "trigger/" + task)
                + (query == null ? string.Empty : "?" + query);
            try
            {
                using HttpResponseMessage response = _http.Send(new HttpRequestMessage(HttpMethod.Post, url));
                response.EnsureSuccessStatusCode();
                reached++;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Trigger {task} on {node.Node} failed: {e.Message}");
            }
        }
        return reached;
    }

    private T? ReadAdminBody<T>(HttpListenerContext context) where T : class
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        string body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(body, AdminJson);
        }
        catch (JsonException e)
        {
            throw new ArgumentException("Invalid JSON body: " + e.Message);
        }
    }

    /// <summary>Canonical request fingerprint binding an idempotency key to one exact request.</summary>
    private static string Fingerprint<T>(T request) => JsonSerializer.Serialize(request, AdminJson);
}
