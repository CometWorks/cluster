using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;

namespace ClusterRegistry;

internal sealed record GatewayOptions(IPEndPoint Listen, IPEndPoint Backend, IPEndPoint? Control, bool Paused, string? SourceControl, string? TargetControl, IPEndPoint? TargetBackend, string? RegistryDirectory, TimeSpan LeaseTtl, NodeAdmission? Admission, [property: JsonIgnore] string? AdminToken, IpNetwork[] InternalNetworks, bool DirectFrontend = false, IPEndPoint? SteamListen = null, IPEndPoint? EosListen = null, string? AdmissionConfig = null, [property: JsonIgnore] string? JoinToken = null, string? CostModelPath = null, string? AdminTokensPath = null, string? World = null, string? ExportRoot = null, string[]? ExportDataRoots = null, string? WorldTool = null, bool ExternalRelay = false, string? RelaySocket = null,
    [property: JsonIgnore] string[]? ExtraAdminTokens = null)
{
    private enum Arity
    {
        Value,          // --backend host:port
        Flag,           // --paused
        OptionalValue,  // --steam [host:port]
    }

    /// <summary>One command-line option and the environment variable that stands in for it.</summary>
    private sealed record Option(string Flag, Arity Arity, string? Default = null)
    {
        // --lease-ttl-seconds -> CLUSTER_LEASE_TTL_SECONDS. Derived rather than tabulated, so a
        // new option cannot arrive with a name that disagrees with its flag.
        public string Environment => "CLUSTER_" + Flag[2..].Replace('-', '_').ToUpperInvariant();
    }

    private static readonly Option[] Options =
    [
        new("--listen", Arity.Value, "0.0.0.0:" + DirectTransportConstants.DefaultClientPort),
        new("--backend", Arity.Value, "127.0.0.1:27016"),
        new("--control", Arity.Value),
        new("--paused", Arity.Flag),
        new("--source-control", Arity.Value),
        new("--target-control", Arity.Value),
        new("--target-backend", Arity.Value),
        new("--registry", Arity.Value),
        new("--lease-ttl-seconds", Arity.Value, "10"),
        new("--direct", Arity.Flag),
        new("--steam", Arity.OptionalValue),
        new("--eos", Arity.OptionalValue),
        new("--admission", Arity.Value),
        new("--cost-model", Arity.Value),
        new("--world", Arity.Value),
        new("--export-root", Arity.Value),
        new("--export-data-roots", Arity.Value),
        // T-0229: "external" hands the packet path (Direct, Steam, node links) to gateway-rs over
        // the relay IPC (Docs/Gateway/RelayIpc.md); "internal" is the in-process relay.
        new("--relay", Arity.Value, "internal"),
        new("--relay-socket", Arity.Value),
    ];

    /// <param name="environment">The environment to read, for a self-test that must not depend on
    /// the ambient one. Defaults to the process environment.</param>
    public static GatewayOptions Parse(string[] args, Func<string, string?>? environment = null)
    {
        if (args.Contains("--help"))
            throw new ArgumentException(Usage);

        // Three layers, resolved once for every option: the command line wins, then the
        // environment, then the built-in default. What a layer supplies is only ever a raw string -
        // the validation below is identical whichever one it came from.
        Func<string, string?> read = environment ?? System.Environment.GetEnvironmentVariable;
        Dictionary<string, Option> byFlag = Options.ToDictionary(option => option.Flag, StringComparer.Ordinal);
        Dictionary<string, string?> values = Options.ToDictionary(option => option.Flag,
            option => read(option.Environment) is { Length: > 0 } value ? value : option.Default, StringComparer.Ordinal);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--self-test")
                continue;
            if (!byFlag.TryGetValue(arg, out Option? option))
                throw new ArgumentException($"Unknown or incomplete argument '{arg}'\n{Usage}");
            values[arg] = option.Arity switch
            {
                Arity.Flag => "true",
                Arity.OptionalValue => i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "0.0.0.0",
                _ => i + 1 < args.Length ? args[++i]
                    : throw new ArgumentException($"Unknown or incomplete argument '{arg}'\n{Usage}"),
            };
        }

        string listen = values["--listen"]!;
        string backend = values["--backend"]!;
        string? control = values["--control"];
        bool paused = Enabled(values["--paused"], "--paused");
        string? sourceControl = values["--source-control"];
        string? targetControl = values["--target-control"];
        string? targetBackend = values["--target-backend"];
        string? registryDirectory = values["--registry"];
        bool directFrontend = Enabled(values["--direct"], "--direct");
        string? steamListen = values["--steam"];
        string? eosListen = values["--eos"];
        string? admissionConfig = values["--admission"];
        string? costModelPath = values["--cost-model"];
        // World export inputs (Docs/AdminAPI.md): the split world, a scoped export root the
        // Gateway may write artifacts under, and the node data roots holding the save bodies.
        // All are optional; /admin/v1/world-exports refuses until they are configured.
        string? world = values["--world"];
        string? exportRoot = values["--export-root"];
        string[]? exportDataRoots = values["--export-data-roots"]
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool externalRelay = values["--relay"] switch
        {
            "internal" => false,
            "external" => true,
            string other => throw new ArgumentException($"--relay must be internal or external, not '{other}'\n{Usage}"),
            null => false,
        };
        string? relaySocket = values["--relay-socket"];
        if (externalRelay && relaySocket == null && registryDirectory == null)
            throw new ArgumentException("--relay external needs --relay-socket or --registry (the socket defaults to <registry>/../gateway-relay.sock)");
        if (!double.TryParse(values["--lease-ttl-seconds"], NumberStyles.Float, CultureInfo.InvariantCulture,
            out double leaseTtlSeconds) || leaseTtlSeconds <= 0)
            throw new ArgumentException($"--lease-ttl-seconds must be a positive number\n{Usage}");

        // Secrets and the accept-all gate stay environment-only: they have no command-line spelling,
        // so they are not overrides of anything. They read through the same accessor all the same,
        // so an injected environment describes the whole of what Parse sees.
        IpNetwork[] internalNetworks = (read("CLUSTER_INTERNAL_NETWORKS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(IpNetwork.Parse).ToArray();
        string? adminToken = read("CLUSTER_ADMIN_TOKEN");
        // A path to the scoped admin token file, not a secret itself - still environment-only,
        // because which credentials exist is deployment configuration, not tuning.
        string? adminTokensPath = read("CLUSTER_ADMIN_TOKENS");
        if (adminTokensPath != null && !File.Exists(adminTokensPath))
            throw new ArgumentException($"CLUSTER_ADMIN_TOKENS points at '{adminTokensPath}' which does not exist");
        IPEndPoint backendEndpoint = EndpointParser.Parse(backend, DirectTransportConstants.DefaultPort);
        IPEndPoint? controlEndpoint = control == null ? null : EndpointParser.Parse(control, 28016);
        IPEndPoint? targetBackendEndpoint = targetBackend == null ? null : EndpointParser.Parse(targetBackend, DirectTransportConstants.DefaultPort);
        RequireInternal(backendEndpoint, internalNetworks, "backend");
        if (targetBackendEndpoint != null) RequireInternal(targetBackendEndpoint, internalNetworks, "target backend");
        if (sourceControl != null) RequireInternal(ControlEndpoint(sourceControl), internalNetworks, "source control");
        if (targetControl != null) RequireInternal(ControlEndpoint(targetControl), internalNetworks, "target control");
        if (controlEndpoint != null && !IPAddress.IsLoopback(controlEndpoint.Address))
        {
            RequireInternal(controlEndpoint, internalNetworks, "admin control");
            ValidateToken(adminToken, "CLUSTER_ADMIN_TOKEN");
        }

        IPEndPoint? steamEndpoint = steamListen == null ? null : EndpointParser.Parse(steamListen, DirectTransportConstants.DefaultPort);
        IPEndPoint? eosEndpoint = eosListen == null ? null : EndpointParser.Parse(eosListen, DirectTransportConstants.DefaultPort);
        IPEndPoint listenEndpoint = EndpointParser.Parse(listen, DirectTransportConstants.DefaultClientPort);
        // --eos does not count as a client frontend: it logs in and publishes nothing, because the
        // EOS data path is not implemented (see EosFrontend). A Gateway with only --eos would accept
        // no players at all, so it is refused rather than started blind.
        if (steamEndpoint == null && !directFrontend)
            throw new ArgumentException("No client frontend is enabled: pass --steam [host:port] or, for development only, --direct\n" + Usage);
        if (eosEndpoint != null && steamEndpoint != null && eosEndpoint.Port == steamEndpoint.Port)
            throw new ArgumentException("The EOS frontend and the Steam frontend cannot share a port");
        if (eosEndpoint != null && directFrontend && eosEndpoint.Port == listenEndpoint.Port)
            throw new ArgumentException("The EOS frontend and the Direct Transport frontend cannot share a port");
        if (steamEndpoint != null && directFrontend && steamEndpoint.Port == listenEndpoint.Port)
            throw new ArgumentException("The Steam frontend and the Direct Transport frontend cannot share a port");
        // Security.md §3.6: accept-all is fail-closed - it takes a deliberate, audited act to enable,
        // and it may never sit on a publicly reachable interface next to a real platform frontend.
        if (directFrontend)
        {
            if (!string.Equals(read("CLUSTER_ALLOW_TEST_ACCEPT_ALL"), "true", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--direct enables accept-all admission and requires CLUSTER_ALLOW_TEST_ACCEPT_ALL=true");
            bool publicInterface = !IPAddress.IsLoopback(listenEndpoint.Address)
                && !internalNetworks.Any(network => network.Contains(listenEndpoint.Address));
            if (publicInterface && !listenEndpoint.Address.Equals(IPAddress.Any))
                throw new ArgumentException($"--direct refuses the publicly reachable interface '{listenEndpoint.Address}'; bind it to loopback or a network in CLUSTER_INTERNAL_NETWORKS");
            if (publicInterface && steamEndpoint != null)
                throw new ArgumentException("--direct cannot share a publicly reachable interface with the Steam frontend");
        }

        return new GatewayOptions(
            listenEndpoint,
            backendEndpoint,
            controlEndpoint,
            paused,
            sourceControl,
            targetControl,
            targetBackendEndpoint,
            registryDirectory,
            TimeSpan.FromSeconds(leaseTtlSeconds),
            registryDirectory == null ? null : NodeAdmission.FromEnvironment(),
            adminToken,
            internalNetworks,
            directFrontend,
            steamEndpoint,
            eosEndpoint,
            admissionConfig,
            read("CLUSTER_JOIN_TOKEN_CURRENT") ?? read("CLUSTER_JOIN_TOKEN"),
            costModelPath,
            adminTokensPath,
            world,
            exportRoot,
            exportDataRoots,
            read("MAGNETAR_WORLD_TOOL"),
            externalRelay,
            relaySocket ?? (registryDirectory == null ? null
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(registryDirectory)) ?? ".", "gateway-relay.sock")));
    }

    // Case-insensitive, so CLUSTER_DIRECT=True out of a shell profile is not silently ignored -
    // and anything else is refused rather than read as off, because a typo that quietly clears a
    // flag is a worse outcome than a start that stops and says so.
    private static bool Enabled(string? value, string flag) => value switch
    {
        null => false,
        _ when value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
        _ when value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0"
            || value.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
        _ => throw new ArgumentException($"{flag} must be true or false, not '{value}'\n{Usage}"),
    };

    private static IPEndPoint ControlEndpoint(string value)
    {
        string endpoint = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? value[7..]
            : value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? value[8..] : value;
        return EndpointParser.Parse(endpoint.TrimEnd('/'), 80);
    }

    private static void RequireInternal(IPEndPoint endpoint, IpNetwork[] networks, string name)
    {
        if (!IPAddress.IsLoopback(endpoint.Address) && !networks.Any(network => network.Contains(endpoint.Address)))
            throw new ArgumentException($"Non-loopback {name} endpoint '{endpoint}' is outside CLUSTER_INTERNAL_NETWORKS");
    }

    private static void ValidateToken(string? token, string name)
    {
        try { if (token != null && Convert.FromBase64String(token).Length >= 32) return; }
        catch (FormatException) { }
        throw new ArgumentException($"{name} must be base64 encoding of at least 32 bytes for a non-loopback control bind");
    }

    public const string Synopsis = "Usage: ClusterRegistry [--steam [host:port]] [--eos [host:port]] [--direct] [--admission file] [--cost-model file] [--listen host:port] [--backend host:port] [--control host:port] [--registry directory] [--lease-ttl-seconds seconds] [--paused] [--relay internal|external] [--relay-socket path] [--source-control host:port --target-control host:port --target-backend host:port]";

    // The environment half is generated from the table, so help cannot drift from what Parse reads.
    public static string Usage => Synopsis
        + "\n\nEvery option above is also settable in the environment; the command line wins.\n"
        + string.Join("\n", Options.Select(option => $"  {option.Flag,-20} {option.Environment}"
            + (option.Default == null ? string.Empty : $" (default {option.Default})")));

    /// <summary>The environment variable standing in for each option, for the self-test.</summary>
    internal static IEnumerable<(string Flag, string Environment)> EnvironmentNames =>
        Options.Select(option => (option.Flag, option.Environment));
}

internal readonly record struct IpNetwork(IPAddress Address, int PrefixLength)
{
    public static IpNetwork Parse(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? address)
            || !int.TryParse(parts[1], out int prefix) || prefix < 0 || prefix > address.GetAddressBytes().Length * 8)
            throw new ArgumentException($"Invalid CIDR '{value}' in CLUSTER_INTERNAL_NETWORKS");
        return new(address, prefix);
    }

    public bool Contains(IPAddress address)
    {
        byte[] network = Address.GetAddressBytes(), candidate = address.GetAddressBytes();
        if (network.Length != candidate.Length) return false;
        int bytes = PrefixLength / 8, bits = PrefixLength % 8;
        if (!network.AsSpan(0, bytes).SequenceEqual(candidate.AsSpan(0, bytes))) return false;
        return bits == 0 || (network[bytes] & (0xff << (8 - bits))) == (candidate[bytes] & (0xff << (8 - bits)));
    }
}
