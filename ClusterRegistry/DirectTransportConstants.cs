namespace ClusterRegistry;

internal static class DirectTransportConstants
{
    public const ulong ServerId = 0xFF00000000000001UL;
    public const string ProtocolKey = "SE-DirectTransport-v1";

    // The advertised game version comes from the cluster's DS on every start, see GameVersion.

    // Steam frontend and backend node links. The Direct Transport client frontend sits on its own
    // port, so --steam and --direct can run side by side without colliding on their defaults.
    public const int DefaultPort = 27016;
    public const int DefaultClientPort = 27116;
    public const int ConnectTimeoutMs = 15000;
}
