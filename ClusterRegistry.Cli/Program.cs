using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry.Cli;

internal static class Program
{
    private const string Usage = """
        Usage: ClusterRegistry.Cli [--url URL] [--timeout SECONDS] COMMAND
        Reads:     health | capabilities | status | plan | recovery-readiness | nodes
                   | world-authority | partitions | snapshots | config | clients | bans
                   | chat-history [--cursor N] [--limit M] | ship-storage-takes | artifacts | artifact ID
                   | events [--cursor N] [--limit M] | operations [--limit M] | operation ID
                   | diag [--depth D] [--node N] [--partition P] [--player S] [--entity E]
                          [--limit M] [--cursor C]
        Mutations (all accept --key IDEMPOTENCY_KEY, generated when omitted):
                   shutdown [--grace N] [--force-after N] [--emergency]
                   | gateway-restart | save-all [--sweep]
                   | handover-config | handover-config-set FILE
                   | rotation | rotation-set FILE (RotationPublication JSON)
                   | client-diagnostics --node NODE
                   | config-set FILE (AdminConfigUpdate JSON; '-' reads stdin)
                   | node-close SLOT [--force-after N] | node-kill SLOT NODE EPOCH
                   | wa-move [--force-after N]
                   | trigger TASK [--node N] [--partition P] [--unthrottled]
                   | kick CLIENTID [--reconnect] | ban USERID | unban USERID
                   | chat MESSAGE --sender STEAMID
                   | ship-storage-settle SHIPID TAKEID release|consume (a stranded take, #1316)
                   | world-export [--ttl-hours H] | registry-backup [--ttl-hours H] | artifact-release ID
        Node control (T-0320):
                   node-control | node-control-history [--limit M] [--cursor N] [--host H]
                   | node-control-set HOST [--serving N] [--spares S] [--reason R] [--requested-by W]
                   | node-control-remove SELECTOR [--now] [--drain-timeout S] [--reason R] [--requested-by W]
                   | node-control-recycle SELECTOR [--now] [--drain-timeout S] [--reason R] [--requested-by W]
                   | node-control-hosts-file FILE (HostsFileApply JSON; '-' reads stdin)
                   SELECTOR: node-N | PID@host | host#position
        """;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    /// <summary>Node-control bodies leave unset fields out, so a server default (drain timeout) applies.</summary>
    private static readonly JsonSerializerOptions NodeControlJson = new(JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    /// <summary>
    /// The CLI command registry: exactly one spelling per admin-contract HTTP operation.
    /// Uppercase tokens are positional placeholders; ExtraArgs are flags an invocation
    /// requires. The self-test proves a 1:1 mapping onto AdminProtocol.Operations and that
    /// each spelling parses to its operation's method and route - the static half of the
    /// P4-QSR-01 parity sweep, so a new route cannot land without a CLI spelling.
    /// </summary>
    internal static readonly (string Spelling, string Operation, string[] ExtraArgs)[] Commands =
    [
        ("health", "GET /admin/v1/health", []),
        ("capabilities", "GET /admin/v1/capabilities", []),
        ("status", "GET /admin/v1/status", []),
        ("plan", "GET /admin/v1/plan", []),
        ("recovery-readiness", "GET /admin/v1/recovery-readiness", []),
        ("nodes", "GET /admin/v1/nodes", []),
        ("world-authority", "GET /admin/v1/world-authority", []),
        ("partitions", "GET /admin/v1/partitions", []),
        ("snapshots", "GET /admin/v1/snapshots", []),
        ("events", "GET /admin/v1/events", []),
        ("operations", "GET /admin/v1/operations", []),
        ("operation {id}", "GET /admin/v1/operations/{id}", []),
        ("rotation", "GET /admin/v1/rotation", []),
        ("rotation-set {file}", "PUT /admin/v1/rotation", []),
        ("client-diagnostics", "GET /admin/v1/client-diagnostics", ["--node", "node-1"]),
        ("config", "GET /admin/v1/config", []),
        ("handover-config", "GET /admin/v1/handover-config", []),
        ("handover-config-set {file}", "PUT /admin/v1/handover-config", []),
        ("config-set {file}", "PUT /admin/v1/config", []),
        ("shutdown", "POST /admin/v1/shutdown", []),
        ("gateway-restart", "POST /admin/v1/gateway/restart", []),
        ("save-all", "POST /admin/v1/save-all", []),
        ("node-close {slot}", "POST /admin/v1/nodes/{slot}/close", []),
        ("node-kill {slot} {node} {epoch}", "POST /admin/v1/nodes/{slot}/kill", []),
        ("wa-move", "POST /admin/v1/world-authority/move", []),
        ("diag", "GET /admin/v1/diag", []),
        ("trigger {task}", "POST /admin/v1/triggers/{task}", []),
        ("clients", "GET /admin/v1/clients", []),
        ("kick {id}", "POST /admin/v1/clients/{id}/kick", []),
        ("bans", "GET /admin/v1/admission/bans", []),
        ("ban {userId}", "POST /admin/v1/admission/bans", []),
        ("unban {userId}", "DELETE /admin/v1/admission/bans/{userId}", []),
        ("chat {message}", "POST /admin/v1/chat", ["--sender", "76561198000000001"]),
        ("chat-history", "GET /admin/v1/chat/history", []),
        ("ship-storage-takes", "GET /admin/v1/ship-storage/takes", []),
        ("ship-storage-settle {id} {take} release", "POST /admin/v1/ship-storage/takes/{id}/settle", []),
        ("world-export", "POST /admin/v1/world-exports", []),
        ("registry-backup", "POST /admin/v1/registry-backups", []),
        ("artifacts", "GET /admin/v1/artifacts", []),
        ("artifact {id}", "GET /admin/v1/artifacts/{id}", []),
        ("artifact-release {id}", "DELETE /admin/v1/artifacts/{id}", []),
        ("node-control", "GET /admin/v1/node-control", []),
        ("node-control-set {host}", "PUT /admin/v1/node-control/hosts/{host}", ["--serving", "3"]),
        ("node-control-remove {selector}", "POST /admin/v1/node-control/remove", []),
        ("node-control-recycle {selector}", "POST /admin/v1/node-control/recycle", []),
        ("node-control-history", "GET /admin/v1/node-control/history", []),
        ("node-control-hosts-file {file}", "POST /admin/v1/node-control/hosts-file", []),
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(["--self-test"]))
            return SelfTest();

        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteLocalError("invalid_arguments", exception.Message);
            return 2;
        }

        try
        {
            using var client = new HttpClient { BaseAddress = options.BaseUri, Timeout = options.Timeout };
            using var request = new HttpRequestMessage(new HttpMethod(options.Method),
                $"{Admin.AdminProtocol.RoutePrefix}/{options.Command}{options.Query}");
            if (options.Body != null)
            {
                request.Content = new StringContent(options.Body, System.Text.Encoding.UTF8, "application/json");
                request.Headers.Add("Idempotency-Key", options.IdempotencyKey ?? Guid.NewGuid().ToString("n"));
            }
            string? token = Environment.GetEnvironmentVariable("CLUSTER_ADMIN_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            string json = await response.Content.ReadAsStringAsync();
            if (!HasCurrentProtocol(response, json))
            {
                Console.Error.WriteLine("Gateway response has an unsupported or missing protocol version.");
                WriteLocalError("protocol_mismatch", "Gateway response has an unsupported or missing protocol version.");
                return 6;
            }

            Console.Out.WriteLine(json);
            return ExitCode(response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Gateway request timed out.");
            WriteLocalError("gateway_timeout", "Gateway request timed out.");
            return 4;
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteLocalError("gateway_unavailable", "Gateway request failed.");
            return 4;
        }
    }

    private static bool HasCurrentProtocol(HttpResponseMessage response, string json)
    {
        if (!response.Headers.TryGetValues("X-Cluster-Gateway-Protocol", out IEnumerable<string>? values)
            || !values.SequenceEqual([Admin.AdminProtocol.Version.ToString()]))
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("protocolVersion", out JsonElement version)
                && version.GetInt32() == Admin.AdminProtocol.Version;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int ExitCode(HttpStatusCode status) => status switch
    {
        >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => 0,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => 3,
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => 4,
        _ => 5,
    };

    private static void WriteLocalError(string code, string message) => Console.Out.WriteLine(
        JsonSerializer.Serialize(new Admin.AdminErrorEnvelope(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
            new Admin.AdminError(code, message)), JsonOptions));

    private static int SelfTest()
    {
        ParitySweep();
        CliOptions parsed = CliOptions.Parse(["--url", "http://127.0.0.1:28016", "--timeout", "2", "plan"]);
        CliOptions recovery = CliOptions.Parse(["--url", "http://127.0.0.1:28016", "recovery-readiness"]);
        CliOptions events = CliOptions.Parse(["--url", "http://127.0.0.1:28016", "events", "--cursor", "7", "--limit", "5"]);
        bool cursorRefused;
        try { CliOptions.Parse(["--url", "http://127.0.0.1:28016", "status", "--cursor", "7"]); cursorRefused = false; }
        catch (ArgumentException) { cursorRefused = true; }
        if (parsed.Command != "plan" || parsed.BaseUri != new Uri("http://127.0.0.1:28016/")
            || parsed.Timeout != TimeSpan.FromSeconds(2) || ExitCode(HttpStatusCode.Forbidden) != 3
            || recovery.Command != "recovery-readiness"
            || events.Query != "?cursor=7&limit=5" || !cursorRefused
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "capabilities"]).Command != "capabilities"
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "operations", "--limit", "3"]).Query != "?limit=3"
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "operation", "op-1"]).Command != "operations/op-1"
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "nodes"]).Command != "nodes"
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "world-authority"]).Command != "world-authority"
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "partitions"]).Command != "partitions"
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "snapshots"]).Command != "snapshots"
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "config"]) is not { Command: "config", Method: "GET", Body: null }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "shutdown", "--grace", "30", "--key", "k1"])
                is not { Command: "shutdown", Method: "POST", IdempotencyKey: "k1", Body: not null }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "gateway-restart"])
                is not { Command: "gateway/restart", Method: "POST" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "save-all", "--sweep"]).Body?.Contains("\"sweep\":true") != true
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "node-close", "slot-1", "--force-after", "60"])
                is not { Command: "nodes/slot-1/close", Method: "POST" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "node-kill", "slot-1", "node-1", "4"])
                is not { Command: "nodes/slot-1/kill", Method: "POST", Body: not null }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "wa-move"])
                is not { Command: "world-authority/move", Method: "POST" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "diag", "--depth", "nodes", "--node", "n1"])
                is not { Command: "diag", Method: "GET", Query: "?depth=nodes&node=n1" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "trigger", "save", "--unthrottled"])
                is not { Command: "triggers/save", Method: "POST", Query: "?unthrottled=true", Body: "{}" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "clients"]) is not { Command: "clients", Method: "GET" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "bans"]) is not { Command: "admission/bans", Method: "GET" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "kick", "7", "--reconnect"])
                is not { Command: "clients/7/kick", Method: "POST" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "ban", "9"])
                is not { Command: "admission/bans", Method: "POST", Body: not null }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "unban", "9"])
                is not { Command: "admission/bans/9", Method: "DELETE" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "chat-history", "--cursor", "5"])
                is not { Command: "chat/history", Method: "GET", Query: "?cursor=5" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "chat", "hello", "--sender", "7656"])
                is not { Command: "chat", Method: "POST", Body: not null }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "ship-storage-takes"])
                is not { Command: "ship-storage/takes", Method: "GET", Body: null }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "ship-storage-settle", "7e570000-1316-4000-8000-000000000001",
                    "7e570000-1316-4000-8000-000000000002", "consume"])
                is not { Command: "ship-storage/takes/7e570000-1316-4000-8000-000000000001/settle", Method: "POST",
                    Body: "{\"take\":\"7e570000-1316-4000-8000-000000000002\",\"action\":\"consume\"}" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "artifacts"]) is not { Command: "artifacts", Method: "GET" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "artifact", "world-1"])
                is not { Command: "artifacts/world-1", Method: "GET" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "world-export", "--ttl-hours", "2"])
                is not { Command: "world-exports", Method: "POST", Body: not null }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "registry-backup", "--ttl-hours", "2"])
                is not { Command: "registry-backups", Method: "POST", Body: not null }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "artifact-release", "world-1"])
                is not { Command: "artifacts/world-1", Method: "DELETE" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "node-control"]) is not { Command: "node-control", Method: "GET", Body: null }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "node-control-history", "--limit", "5", "--host", "polaris"])
                is not { Command: "node-control/history", Method: "GET", Query: "?limit=5&host=polaris" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "node-control-set", "polaris", "--serving", "5", "--spares", "1"])
                is not { Command: "node-control/hosts/polaris", Method: "PUT", Body: "{\"serving\":5,\"spares\":1}" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "node-control-remove", "12345@polaris", "--now"])
                is not { Command: "node-control/remove", Method: "POST", Body: "{\"selector\":\"12345@polaris\",\"now\":true}" }
            || CliOptions.Parse(["--url", "http://127.0.0.1:28016", "node-control-recycle", "polaris#2", "--drain-timeout", "60", "--reason", "cruft"])
                is not { Command: "node-control/recycle", Method: "POST", Body: "{\"selector\":\"polaris#2\",\"timeoutSeconds\":60,\"reason\":\"cruft\"}" }
            || ExitCode(HttpStatusCode.ServiceUnavailable) != 4 || ExitCode(HttpStatusCode.Conflict) != 5)
            throw new InvalidOperationException("CLI self-test failed");
        Console.Error.WriteLine("self-test ok");
        return 0;
    }

    /// <summary>Generated parity sweep (static half): the command registry maps 1:1 onto the
    /// contract's operation list, and every spelling parses to its operation's method+route.</summary>
    private static void ParitySweep()
    {
        string[] registered = Commands.Select(x => x.Operation).ToArray();
        if (registered.Distinct().Count() != registered.Length)
            throw new InvalidOperationException("Parity sweep: duplicate operation mappings in the CLI registry");
        string[] missing = Admin.AdminProtocol.Operations.Except(registered).ToArray();
        string[] unknown = registered.Except(Admin.AdminProtocol.Operations).ToArray();
        if (missing.Length != 0 || unknown.Length != 0)
            throw new InvalidOperationException("Parity sweep: CLI registry and AdminProtocol.Operations differ."
                + (missing.Length != 0 ? " Missing CLI spellings: " + string.Join(", ", missing) + "." : "")
                + (unknown.Length != 0 ? " Unknown operations: " + string.Join(", ", unknown) + "." : ""));

        var substitutions = new Dictionary<string, string>
        {
            ["{id}"] = "7",
            ["{slot}"] = "slot-1",
            ["{node}"] = "node-1",
            ["{epoch}"] = "3",
            ["{task}"] = "save",
            ["{userId}"] = "9",
            ["{message}"] = "hello",
            ["{host}"] = "host-1",
            ["{selector}"] = "host-1#2",
            ["{take}"] = "7e570000-1316-4000-8000-000000000002",
        };
        string configFile = Path.Combine(Path.GetTempPath(), "cluster-cli-parity-" + Guid.NewGuid() + ".json");
        File.WriteAllText(configFile, """{"expectedRevision":0,"slots":[],"hosts":{}}""");
        substitutions["{file}"] = configFile;
        try
        {
            foreach ((string spelling, string operation, string[] extraArgs) in Commands)
            {
                string[] arguments =
                [
                    "--url", "http://127.0.0.1:28016",
                    .. spelling.Split(' ').Select(token => substitutions.GetValueOrDefault(token, token)),
                    .. extraArgs,
                ];
                CliOptions parsed = CliOptions.Parse(arguments);
                string[] parts = operation.Split(' ');
                string expectedPath = substitutions.Aggregate(parts[1],
                    (path, pair) => path.Replace(pair.Key, Uri.EscapeDataString(pair.Value)));
                string actualPath = $"{Admin.AdminProtocol.RoutePrefix}/{parsed.Command}";
                if (parsed.Method != parts[0] || actualPath != expectedPath)
                    throw new InvalidOperationException(
                        $"Parity sweep: '{spelling}' produced {parsed.Method} {actualPath}, expected {operation}");
                if (parts[0] != "GET" && parsed.Body == null)
                    throw new InvalidOperationException($"Parity sweep: mutation '{spelling}' sends no body");
            }
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    private sealed record CliOptions(Uri BaseUri, TimeSpan Timeout, string Command, string Query = "",
        string Method = "GET", string? Body = null, string? IdempotencyKey = null)
    {
        public static CliOptions Parse(string[] args)
        {
            string? url = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_URL");
            double timeoutSeconds = 30;
            string? command = null, key = null;
            string method = "GET";
            string? body = null;
            long? cursor = null, limit = null;
            double? grace = null, forceAfter = null, ttlHours = null;
            bool emergency = false, sweep = false, unthrottled = false, reconnect = false;
            ulong? sender = null;
            string? depth = null, node = null, partition = null, player = null, entity = null;
            string? host = null, reason = null, requestedBy = null;
            int? serving = null, spares = null;
            double? drainTimeout = null;
            bool now = false;
            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--url" && i + 1 < args.Length)
                    url = args[++i];
                else if (args[i] == "--timeout" && i + 1 < args.Length
                    && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double parsedTimeout) && parsedTimeout > 0)
                    timeoutSeconds = parsedTimeout;
                else if (args[i] == "--cursor" && i + 1 < args.Length
                    && long.TryParse(args[++i], out long parsedCursor) && parsedCursor >= 0)
                    cursor = parsedCursor;
                else if (args[i] == "--limit" && i + 1 < args.Length
                    && long.TryParse(args[++i], out long parsedLimit) && parsedLimit > 0)
                    limit = parsedLimit;
                else if (args[i] == "--key" && i + 1 < args.Length)
                    key = args[++i];
                else if (args[i] == "--grace" && i + 1 < args.Length
                    && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double parsedGrace) && parsedGrace >= 0)
                    grace = parsedGrace;
                else if (args[i] == "--force-after" && i + 1 < args.Length
                    && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double parsedForce) && parsedForce >= 0)
                    forceAfter = parsedForce;
                else if (args[i] == "--emergency")
                    emergency = true;
                else if (args[i] == "--sweep")
                    sweep = true;
                else if (args[i] == "--unthrottled")
                    unthrottled = true;
                else if (args[i] == "--reconnect")
                    reconnect = true;
                else if (args[i] == "--sender" && i + 1 < args.Length
                    && ulong.TryParse(args[++i], out ulong parsedSender) && parsedSender > 0)
                    sender = parsedSender;
                else if (args[i] == "--ttl-hours" && i + 1 < args.Length
                    && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double parsedTtl) && parsedTtl > 0)
                    ttlHours = parsedTtl;
                else if (args[i] == "--depth" && i + 1 < args.Length)
                    depth = args[++i];
                else if (args[i] == "--node" && i + 1 < args.Length)
                    node = args[++i];
                else if (args[i] == "--partition" && i + 1 < args.Length)
                    partition = args[++i];
                else if (args[i] == "--player" && i + 1 < args.Length)
                    player = args[++i];
                else if (args[i] == "--entity" && i + 1 < args.Length)
                    entity = args[++i];
                else if (args[i] == "--host" && i + 1 < args.Length)
                    host = args[++i];
                else if (args[i] == "--reason" && i + 1 < args.Length)
                    reason = args[++i];
                else if (args[i] == "--requested-by" && i + 1 < args.Length)
                    requestedBy = args[++i];
                else if (args[i] == "--serving" && i + 1 < args.Length && int.TryParse(args[++i], out int parsedServing) && parsedServing >= 0)
                    serving = parsedServing;
                else if (args[i] == "--spares" && i + 1 < args.Length && int.TryParse(args[++i], out int parsedSpares) && parsedSpares >= 0)
                    spares = parsedSpares;
                else if (args[i] == "--drain-timeout" && i + 1 < args.Length
                    && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDrain) && parsedDrain >= 0)
                    drainTimeout = parsedDrain;
                else if (args[i] == "--now")
                    now = true;
                else if (command == null && args[i] is "health" or "capabilities" or "status" or "plan"
                    or "recovery-readiness" or "nodes" or "world-authority" or "partitions" or "snapshots"
                    or "rotation" or "rotation-set" or "client-diagnostics" or "handover-config" or "handover-config-set" or "config" or "clients" or "bans" or "chat-history" or "ship-storage-takes" or "artifacts" or "events"
                    or "operations" or "diag" or "shutdown" or "gateway-restart" or "save-all"
                    or "config-set" or "node-close" or "node-kill" or "wa-move" or "trigger" or "kick"
                    or "ban" or "unban" or "chat" or "ship-storage-settle" or "world-export" or "registry-backup" or "artifact-release"
                    or "node-control" or "node-control-history" or "node-control-set" or "node-control-remove"
                    or "node-control-recycle" or "node-control-hosts-file")
                    command = args[i];
                else if (command == null && args[i] == "operation" && i + 1 < args.Length)
                    command = "operations/" + Uri.EscapeDataString(args[++i]);
                else if (command == null && args[i] == "artifact" && i + 1 < args.Length)
                    command = "artifacts/" + Uri.EscapeDataString(args[++i]);
                else if (command != null && !args[i].StartsWith("--"))
                    positional.Add(args[i]);
                else
                    throw new ArgumentException(Usage);
            }

            if (command == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? baseUri)
                || baseUri.Scheme is not ("http" or "https"))
                throw new ArgumentException(Usage);
            bool stopVerb = command is "node-control-remove" or "node-control-recycle";
            if (command is not ("events" or "diag" or "chat-history" or "node-control-history") && cursor != null
                || command is not ("events" or "operations" or "diag" or "chat-history" or "node-control-history") && limit != null
                || command != "node-control-history" && host != null
                || command != "node-control-set" && (serving != null || spares != null)
                || !stopVerb && (now || drainTimeout != null)
                || !stopVerb && command != "node-control-set" && (reason != null || requestedBy != null)
                || command is not ("diag" or "trigger" or "client-diagnostics") && node != null
                || command is not ("diag" or "trigger") && partition != null
                || command != "diag" && (depth != null || player != null || entity != null)
                || command != "trigger" && unthrottled
                || command != "kick" && reconnect
                || command != "chat" && sender != null
                || command is not ("world-export" or "registry-backup") && ttlHours != null)
                throw new ArgumentException(Usage);

            // One CLI spelling per HTTP operation: the mutation commands build the JSON body
            // the route defines and nothing else - no hidden local work.
            switch (command)
            {
                case "shutdown":
                    method = "POST";
                    body = JsonSerializer.Serialize(new Admin.ShutdownRequest(emergency,
                        grace ?? 60, forceAfter ?? 300), JsonOptions);
                    break;
                case "gateway-restart":
                    (method, command, body) = ("POST", "gateway/restart", "{}");
                    break;
                case "save-all":
                    method = "POST";
                    body = JsonSerializer.Serialize(new Admin.SaveAllRequest(sweep), JsonOptions);
                    break;
                case "client-diagnostics":
                    if (string.IsNullOrWhiteSpace(node) || positional.Count != 0) throw new ArgumentException(Usage);
                    break;
                case "rotation-set":
                case "handover-config-set":
                case "config-set":
                    if (positional.Count != 1) throw new ArgumentException(Usage);
                    (method, command) = ("PUT", command == "rotation-set" ? "rotation" : command == "config-set" ? "config" : "handover-config");
                    body = positional[0] == "-" ? Console.In.ReadToEnd() : File.ReadAllText(positional[0]);
                    break;
                case "node-close":
                    if (positional.Count != 1) throw new ArgumentException(Usage);
                    (method, command) = ("POST", $"nodes/{Uri.EscapeDataString(positional[0])}/close");
                    body = JsonSerializer.Serialize(new Admin.CloseNodeRequest(forceAfter), JsonOptions);
                    break;
                case "node-kill":
                    if (positional.Count != 3 || !long.TryParse(positional[2], out long epoch))
                        throw new ArgumentException(Usage);
                    (method, command) = ("POST", $"nodes/{Uri.EscapeDataString(positional[0])}/kill");
                    body = JsonSerializer.Serialize(new Admin.KillNodeRequest(positional[1], epoch), JsonOptions);
                    break;
                case "wa-move":
                    (method, command, body) = ("POST", "world-authority/move",
                        JsonSerializer.Serialize(new Admin.WorldAuthorityMoveRequest(forceAfter), JsonOptions));
                    break;
                case "trigger":
                    if (positional.Count != 1) throw new ArgumentException(Usage);
                    (method, command, body) = ("POST", $"triggers/{Uri.EscapeDataString(positional[0])}", "{}");
                    break;
                case "bans":
                    command = "admission/bans";
                    break;
                case "kick":
                    if (positional.Count != 1 || !ulong.TryParse(positional[0], out ulong kickId))
                        throw new ArgumentException(Usage);
                    (method, command) = ("POST", $"clients/{kickId}/kick");
                    body = JsonSerializer.Serialize(new Admin.KickClientRequest(reconnect), JsonOptions);
                    break;
                case "ban":
                    if (positional.Count != 1 || !ulong.TryParse(positional[0], out ulong banId))
                        throw new ArgumentException(Usage);
                    (method, command, body) = ("POST", "admission/bans",
                        JsonSerializer.Serialize(new Admin.BanRequest(banId), JsonOptions));
                    break;
                case "unban":
                    if (positional.Count != 1 || !ulong.TryParse(positional[0], out ulong unbanId))
                        throw new ArgumentException(Usage);
                    (method, command, body) = ("DELETE", $"admission/bans/{unbanId}", "{}");
                    break;
                case "chat-history":
                    command = "chat/history";
                    break;
                case "chat":
                    if (positional.Count != 1 || sender is not { } chatSender)
                        throw new ArgumentException(Usage);
                    (method, command, body) = ("POST", "chat",
                        JsonSerializer.Serialize(new Admin.ChatSendRequest(positional[0], chatSender), JsonOptions));
                    break;
                case "ship-storage-takes":
                    command = "ship-storage/takes";
                    break;
                case "ship-storage-settle":
                    if (positional.Count != 3 || !Guid.TryParse(positional[1], out Guid settleTake)
                        || positional[2] is not ("release" or "consume"))
                        throw new ArgumentException(Usage);
                    (method, command, body) = ("POST", $"ship-storage/takes/{Uri.EscapeDataString(positional[0])}/settle",
                        JsonSerializer.Serialize(new Admin.ShipStorageSettleRequest(settleTake, positional[2]), JsonOptions));
                    break;
                case "world-export":
                    (method, command, body) = ("POST", "world-exports",
                        JsonSerializer.Serialize(new Admin.WorldExportRequest(ttlHours), JsonOptions));
                    break;
                case "registry-backup":
                    (method, command, body) = ("POST", "registry-backups",
                        JsonSerializer.Serialize(new Admin.RegistryBackupRequest(ttlHours), JsonOptions));
                    break;
                case "node-control-history":
                    command = "node-control/history";
                    break;
                case "node-control-set":
                    if (positional.Count != 1 || serving == null && spares == null) throw new ArgumentException(Usage);
                    (method, command, body) = ("PUT", $"node-control/hosts/{Uri.EscapeDataString(positional[0])}",
                        JsonSerializer.Serialize(new Admin.HostTargetsUpdate(serving, spares, reason, requestedBy), NodeControlJson));
                    break;
                case "node-control-remove":
                case "node-control-recycle":
                    if (positional.Count != 1) throw new ArgumentException(Usage);
                    (method, command, body) = ("POST", command == "node-control-remove" ? "node-control/remove" : "node-control/recycle",
                        JsonSerializer.Serialize(new Admin.NodeStopRequest(positional[0], now, drainTimeout, reason, requestedBy), NodeControlJson));
                    break;
                case "node-control-hosts-file":
                    if (positional.Count != 1) throw new ArgumentException(Usage);
                    (method, command) = ("POST", "node-control/hosts-file");
                    body = positional[0] == "-" ? Console.In.ReadToEnd() : File.ReadAllText(positional[0]);
                    break;
                case "artifact-release":
                    if (positional.Count != 1) throw new ArgumentException(Usage);
                    (method, command, body) = ("DELETE", $"artifacts/{Uri.EscapeDataString(positional[0])}", "{}");
                    break;
                default:
                    if (positional.Count != 0 || key != null || grace != null || forceAfter != null
                        || emergency || sweep)
                        throw new ArgumentException(Usage);
                    break;
            }

            string[] query = [.. cursor is { } c ? [$"cursor={c}"] : Array.Empty<string>(),
                .. limit is { } l ? [$"limit={l}"] : Array.Empty<string>(),
                .. depth is { } d ? [$"depth={Uri.EscapeDataString(d)}"] : Array.Empty<string>(),
                .. node is { } n ? [$"node={Uri.EscapeDataString(n)}"] : Array.Empty<string>(),
                .. partition is { } p ? [$"partition={Uri.EscapeDataString(p)}"] : Array.Empty<string>(),
                .. player is { } s ? [$"player={Uri.EscapeDataString(s)}"] : Array.Empty<string>(),
                .. entity is { } e ? [$"entity={Uri.EscapeDataString(e)}"] : Array.Empty<string>(),
                .. host is { } h ? [$"host={Uri.EscapeDataString(h)}"] : Array.Empty<string>(),
                .. unthrottled ? new[] { "unthrottled=true" } : Array.Empty<string>()];
            return new CliOptions(new Uri(baseUri.ToString().TrimEnd('/') + "/"),
                TimeSpan.FromSeconds(timeoutSeconds), command,
                query.Length == 0 ? string.Empty : "?" + string.Join('&', query),
                method, body, key);
        }
    }
}
