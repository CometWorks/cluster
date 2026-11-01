namespace ClusterRegistry;

internal static class DirectTransportConstants
{
    public const ulong ServerId = 0xFF00000000000001UL;
    public const string ProtocolKey = "SE-DirectTransport-v1";
    // Both MUST be bumped whenever the game/server version changes. VanillaVersion is what the
    // Steam frontend advertises (GameServer.Init, SetGameData, the "version" game tag); a real
    // client compares it against its own APP_VERSION in MyJoinGameHelper.JoinGame and refuses the
    // join outright on any mismatch. Nothing at runtime derives these from the server build, so a
    // stale value here is invisible until a retail client tries to connect.
    public const string VanillaVersion = "1.210.014";
    public const string WireFingerprint = "SE-DirectTransport-v1/game-frame-1.210.014";
    // Steam frontend and backend node links. The Direct Transport client frontend sits on its own
    // port, so --steam and --direct can run side by side without colliding on their defaults.
    public const int DefaultPort = 27016;
    public const int DefaultClientPort = 27116;
    public const int ConnectTimeoutMs = 15000;
}
