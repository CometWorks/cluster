using System.Net;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Platform;

namespace ClusterRegistry;

/// <summary>
/// Gateway.md §4.2 / Security.md §10: the EOS half of the public transport, selected with
/// <c>--eos</c>. The platform and the dedicated-server Connect login are the same flow
/// <c>MyEOSService.InitNetworking</c> runs when <c>isDedicated</c>, and they work in this
/// non-game process (verified on polaris - see Findings/2026-07-25-p3-eos-spike.md).
///
/// The client data path is deliberately absent. EOS game traffic does not ride raw EOS P2P:
/// <c>MyEOSPeer2Peer</c> goes through <c>MyP2PQoSAdapter</c>, a bespoke reliability,
/// fragmentation and QoS layer both ends of a vanilla EOS session speak, and terminating EOS
/// clients means reproducing that wire format exactly. Until it is pinned against captured
/// vanilla EOS traffic this frontend logs in and stays silent rather than publishing a browser
/// row nobody can actually join.
/// </summary>
internal sealed class EosFrontend
{
    // MyProgram.cs's dedicated-server credentials. Order matters: MyEOSService.InitNetworking
    // takes (clientId, clientSecret, productId, sandboxId, deploymentId), and reading the call
    // site as product-first yields CreateDeviceId: AccessDenied with no other diagnostic.
    private const string ClientId = "xyza7891A4WeGrpP85BTlBa3BSfUEABN";
    private const string ClientSecret = "ZdHZVevSVfIajebTnTmh5MVi3KPHflszD9hJB7mRkgg";
    private const string ProductId = "24b1cd652a18461fa9b3d533ac8d6b5b";
    private const string SandboxId = "1958fe26c66d4151a327ec162e4d49c8";
    private const string DeploymentId = "07c169b3b641401496d352cad1c905d6";

    private readonly IPEndPoint _endpoint;
    private readonly string _version;
    private PlatformInterface? _platform;
    private Thread? _pump;
    private volatile bool _running;

    public EosFrontend(IPEndPoint endpoint, string version)
    {
        _endpoint = endpoint;
        _version = version;
    }

    public string? LocalUserId { get; private set; }
    public bool LoggedOn => LocalUserId != null;

    /// <summary>Always false: see the class remarks. Kept explicit so the relay never guesses.</summary>
    public bool TerminatesClients => false;

    public void Start()
    {
        Result initialize = PlatformInterface.Initialize(
            new InitializeOptions { ProductName = "Space Engineers", ProductVersion = _version });
        // AlreadyConfigured is success on a second Start in the same process (self-tests).
        if (initialize is not (Result.Success or Result.AlreadyConfigured))
            throw new InvalidOperationException($"EOS PlatformInterface.Initialize failed: {initialize}");

        _platform = PlatformInterface.Create(new Options
        {
            ClientCredentials = new ClientCredentials { ClientId = ClientId, ClientSecret = ClientSecret },
            ProductId = ProductId,
            SandboxId = SandboxId,
            DeploymentId = DeploymentId,
            // The dedicated server creates the platform with IsServer = false; mirroring it keeps
            // this login indistinguishable from a vanilla DS to the EOS backend.
            IsServer = false,
        }) ?? throw new InvalidOperationException("EOS PlatformInterface.Create returned null");

        _running = true;
        _pump = new Thread(Pump) { Name = "Gateway-EOS", IsBackground = true };
        _pump.Start();
        Login();
    }

    private void Login()
    {
        ConnectInterface connect = _platform!.GetConnectInterface();
        connect.CreateDeviceId(new CreateDeviceIdOptions { DeviceModel = "MagnetarClusterGateway" }, null, device =>
        {
            if (device.ResultCode is not (Result.Success or Result.DuplicateNotAllowed))
            {
                Console.Error.WriteLine($"EOS CreateDeviceId failed: {device.ResultCode}");
                return;
            }
            connect.Login(new LoginOptions
            {
                Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken },
                UserLoginInfo = new UserLoginInfo { DisplayName = "MagnetarGateway" },
            }, null, login =>
            {
                if (login.ResultCode != Result.Success)
                {
                    Console.Error.WriteLine($"EOS Connect.Login failed: {login.ResultCode}");
                    return;
                }
                LocalUserId = login.LocalUserId?.ToString();
                Console.WriteLine($"EOS frontend logged in as {LocalUserId} for {_endpoint}"
                    + " - cross-play publishing and client termination are not implemented yet"
                    + " (MyP2PQoSAdapter wire format, see Findings/2026-07-25-p3-eos-spike.md)");
            });
        });
    }

    private void Pump()
    {
        while (_running)
        {
            try { _platform?.Tick(); }
            catch (Exception exception) { Console.Error.WriteLine($"EOS pump: {exception.Message}"); }
            Thread.Sleep(50);
        }
    }

    public void Stop()
    {
        _running = false;
        _pump?.Join(TimeSpan.FromSeconds(2));
        try { _platform?.Release(); } catch { }
        _platform = null;
    }

    public object Status() => new
    {
        endpoint = _endpoint.ToString(), localUserId = LocalUserId, loggedOn = LoggedOn,
        terminatesClients = TerminatesClients,
    };
}
