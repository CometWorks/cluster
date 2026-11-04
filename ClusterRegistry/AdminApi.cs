using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal sealed partial class DirectGateway
{
    private static readonly JsonSerializerOptions AdminJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The canonical operation list lives in the contract package; the CLI's
    /// self-test asserts its command registry maps 1:1 onto the same list.</summary>
    internal static string[] AdminOperations => Admin.AdminProtocol.Operations;

    /// <summary>Stable trigger vocabulary (Orchestration.md §11.2); unknown tasks are refused.</summary>
    internal static readonly string[] AdminTriggerTasks =
        ["balance", "save", "split-sweep", "membership-eval", "conceal-eval", "compact", "catalog-gc", "rotation-check", "profile"];

    private static readonly IReadOnlyDictionary<string, string> AdminFeatures = new Dictionary<string, string>
    {
        ["authScopes"] = "query,manage",
        ["events"] = "cursor",
        ["executorReports"] = "fenced-v1",
        ["nodeControl"] = "v1",
        ["clientDiagnostics"] = "v1",
        ["rotationPublication"] = "v1",
        ["playerModeration"] = "v1",
        ["offlinePartitions"] = "v1",
    };

    /// <summary>
    /// Who is calling the admin contract, or null for 401. A loopback bind is the
    /// unauthenticated development path and gets full manage scope; beyond loopback the
    /// caller must present either the legacy admin token (manage) or a token from the
    /// scoped token file, which carries its own name and scope for audit.
    /// </summary>
    private AdminActor? ResolveAdminActor(HttpListenerContext context)
    {
        if (_adminTokens is null && _options.AdminToken is null
            && (_options.Control == null || IPAddress.IsLoopback(_options.Control.Address)))
            return new AdminActor("loopback", Admin.AdminScope.Manage);
        // T-0321: with a LAN --control, the automatic loopback prefix is today's loopback development path.
        if (LoopbackExempt(context))
            return new AdminActor("loopback", Admin.AdminScope.Manage);
        string supplied = context.Request.Headers["Authorization"] ?? string.Empty;
        const string prefix = "Bearer ";
        if (!supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        if (_adminTokens?.Match(supplied[prefix.Length..]) is { } named)
            return named;
        return Authorized(context) ? new AdminActor("admin-token", Admin.AdminScope.Manage) : null;
    }

    private void HandleAdminRequest(HttpListenerContext context, string path, AdminActor actor)
    {
        try { HandleAdminRequestCore(context, path, actor); }
        catch (ArgumentException e) when (e is not ArgumentNullException)
        { WriteAdminError(context, 400, "invalid_arguments", e.Message); }
    }

    private void HandleAdminRequestCore(HttpListenerContext context, string path, AdminActor actor)
    {
        context.Response.Headers["X-Cluster-Gateway-Protocol"] = Admin.AdminProtocol.Version.ToString();
        if (actor.Scope == Admin.AdminScope.Executor)
        {
            WriteAdminError(context, 403, "executor_scope_only", "Executor credentials can only use the executor contract.");
            return;
        }
        if (context.Request.HttpMethod is "POST" or "PUT" or "DELETE")
        {
            HandleAdminMutation(context, path, actor);
            return;
        }
        if (context.Request.HttpMethod != "GET")
        {
            WriteAdminError(context, 405, "method_not_allowed",
                "Admin contract routes accept GET for reads and POST/PUT for mutations.");
            return;
        }

        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/health", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, new Admin.GatewayHealth("cluster-gateway", _registry != null,
                DirectTransportConstants.WireFingerprint));
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/capabilities", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, new Admin.AdminCapabilities("cluster-gateway", actor.Scope,
                AdminOperations, AdminFeatures));
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/events", StringComparison.OrdinalIgnoreCase))
        {
            long cursor = ParseLong(context, "cursor") ?? 0;
            int limit = (int)(ParseLong(context, "limit") ?? 100);
            (IReadOnlyList<EventLog.Entry> events, long next, long earliest) = _events.Read(cursor, limit);
            WriteAdminEnvelope(context, new Admin.AdminEventPage(
                events.Select(x => new Admin.AdminEvent(x.Seq, x.At, x.Type, x.Actor, x.RequestId, x.Target, x.Data)).ToArray(),
                next, earliest));
            return;
        }

        if (_registry == null)
        {
            WriteAdminError(context, 503, "registry_unavailable", "Gateway Registry is not configured.");
            return;
        }

        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/status", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.AdminStatus(_clients.Count, _paused));
            return;
        }
        if (HandleAdminOfflineRead(context, path)) return;
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/nodes", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.AdminNodes());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/world-authority", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.AdminWorldAuthority());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/partitions", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.AdminPartitions());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/snapshots", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.AdminSnapshots());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/rotation", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.RotationStatus());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/client-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.ClientDiagnostics(Required(context, "node")));
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/config", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.AdminConfig(_options.LeaseTtl));
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/handover-config", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.HandoverConfig());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/diag", StringComparison.OrdinalIgnoreCase))
        {
            // Same structured snapshot as the legacy front door (Orchestration §11.1), now with
            // the versioned envelope and scope checks. Filters ride the query string unchanged.
            WriteAdminEnvelope(context, DiagSnapshot(context));
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/clients", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, AdminClients());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/players/moderation", StringComparison.OrdinalIgnoreCase))
        {
            AdminPlayerModeration(context);
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/admission/bans", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, new Admin.AdmissionBans(_admission.BannedUsers()));
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/chat/history", StringComparison.OrdinalIgnoreCase))
        {
            AdminChatHistory(context);
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/ship-storage/takes", StringComparison.OrdinalIgnoreCase))
        {
            AdminShipStorageTakes(context);
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/artifacts", StringComparison.OrdinalIgnoreCase))
        {
            if (_artifacts == null)
                WriteAdminError(context, 503, "exports_unconfigured", "No export root is configured.");
            else
                WriteAdminEnvelope(context, new Admin.ArtifactPage(_artifacts.List()));
            return;
        }
        if (path.StartsWith(Admin.AdminProtocol.RoutePrefix + "/artifacts/", StringComparison.OrdinalIgnoreCase))
        {
            string artifactId = path[(Admin.AdminProtocol.RoutePrefix + "/artifacts/").Length..];
            if (_artifacts == null)
                WriteAdminError(context, 503, "exports_unconfigured", "No export root is configured.");
            else if (_artifacts.Get(artifactId) is { } descriptor)
                WriteAdminEnvelope(context, descriptor);
            else
                WriteAdminError(context, 404, "not_found", $"Unknown artifact '{artifactId}'.");
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/plan", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.AdminNodePlan());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/node-control", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, NodeControlView());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/node-control/history", StringComparison.OrdinalIgnoreCase))
        {
            long? cursor = ParseLong(context, "cursor");
            int limit = (int)Math.Min(ParseLong(context, "limit") ?? 50, GatewayRegistry.MaxNodeHistory);
            string? host = context.Request.QueryString["host"];
            WriteAdminEnvelope(context, _registry.NodeHistoryPage(limit, cursor, string.IsNullOrWhiteSpace(host) ? null : host));
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/recovery-readiness", StringComparison.OrdinalIgnoreCase))
        {
            WriteAdminEnvelope(context, _registry.AdminRecoveryReadiness());
            return;
        }
        if (path.Equals(Admin.AdminProtocol.RoutePrefix + "/operations", StringComparison.OrdinalIgnoreCase))
        {
            int limit = (int)(ParseLong(context, "limit") ?? 100);
            WriteAdminEnvelope(context, new Admin.AdminOperationPage(_registry.AdminOperationList(limit)));
            return;
        }
        if (path.StartsWith(Admin.AdminProtocol.RoutePrefix + "/operations/", StringComparison.OrdinalIgnoreCase))
        {
            string id = path[(Admin.AdminProtocol.RoutePrefix + "/operations/").Length..];
            if (_registry.AdminOperationView(id) is { } operation)
                WriteAdminEnvelope(context, operation);
            else
                WriteAdminError(context, 404, "not_found", $"Unknown admin operation '{id}'.");
            return;
        }

        WriteAdminError(context, 404, "route_not_found", "Unknown Gateway admin contract route.");
    }

    /// <summary>
    /// Front door for every mutating admin route: manage scope, a required
    /// Idempotency-Key header, and one durable operation record per key. Returns null
    /// after writing the error response when the request must not proceed; sets
    /// <paramref name="replayed"/> when the key already carries a recorded outcome, in
    /// which case the caller responds with that record instead of re-executing.
    /// </summary>
    private AdminOperationRecord? BeginMutation(HttpListenerContext context, AdminActor actor,
        string kind, string fingerprint, string? target, out bool replayed)
    {
        replayed = false;
        if (!RequireManage(context, actor, kind))
            return null;
        string? key = context.Request.Headers["Idempotency-Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            WriteAdminJson(context, 400, new Admin.AdminErrorEnvelope(Admin.AdminProtocol.Version,
                DateTimeOffset.UtcNow, new Admin.AdminError("invalid_arguments",
                    "Mutating admin routes require an Idempotency-Key header.",
                    new Dictionary<string, string[]> { ["Idempotency-Key"] = ["required"] })));
            return null;
        }
        AdminOperationRecord record;
        try
        {
            record = _registry!.BeginAdminOperation(kind, fingerprint, actor.Name, key, target, out bool created);
            replayed = !created;
        }
        catch (RegistryConflictException conflict)
        {
            WriteAdminError(context, 409, "idempotency_key_reuse", conflict.Message);
            return null;
        }
        return record;
    }

    /// <summary>The uniform mutation response: the durable operation record, 200 when terminal, 202 while running.</summary>
    private void WriteOperation(HttpListenerContext context, AdminOperationRecord record)
    {
        Admin.AdminOperation view = _registry!.AdminOperationView(record.OperationId)!;
        WriteAdminJson(context, view.State == Admin.AdminOperationState.Running ? 202 : 200,
            new Admin.AdminEnvelope<Admin.AdminOperation>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, view));
    }

    /// <summary>Guards a mutating route: false writes the stable denial and audits the refusal.</summary>
    private bool RequireManage(HttpListenerContext context, AdminActor actor, string operation)
    {
        if (actor.Scope == Admin.AdminScope.Manage)
            return true;
        _events.Append("admin.denied", actor.Name, target: operation);
        WriteAdminError(context, 403, "authorization_denied",
            "This operation requires the manage scope; the presented credential is query-only.");
        return false;
    }

    private static long? ParseLong(HttpListenerContext context, string key)
    {
        string? raw = context.Request.QueryString[key];
        if (raw == null) return null;
        if (!long.TryParse(raw, out long value) || value < 0)
            throw new ArgumentException($"Query parameter '{key}' must be a non-negative integer");
        return value;
    }

    private static void WriteAdminEnvelope<T>(HttpListenerContext context, T data) =>
        WriteAdminJson(context, 200, new Admin.AdminEnvelope<T>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, data));

    private static void WriteAdminError(HttpListenerContext context, int status, string code, string message) =>
        WriteAdminJson(context, status, new Admin.AdminErrorEnvelope(Admin.AdminProtocol.Version,
            DateTimeOffset.UtcNow, new Admin.AdminError(code, message)));

    private static bool IsAdminRequestPath(string path) =>
        path.Equals(Admin.AdminProtocol.RoutePrefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Admin.AdminProtocol.RoutePrefix + "/", StringComparison.OrdinalIgnoreCase);

    private static void WriteAdminJson(HttpListenerContext context, int status, object value)
    {
        context.Response.Headers["X-Cluster-Gateway-Protocol"] = Admin.AdminProtocol.Version.ToString();
        byte[] body = SecretRedactor.Redact(JsonSerializer.SerializeToUtf8Bytes(value, AdminJson));
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body);
        context.Response.Close();
    }
}
