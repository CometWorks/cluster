using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ClusterRegistry;

/// <summary>
/// The game version the Gateway advertises: GameServer.Init, SetGameData, the "version" game tag,
/// the EOS frontend and the admin status. A retail client hides a server whose version tag differs
/// from its own APP_VERSION and MyJoinGameHelper refuses the join, so the value must be the one of
/// the DS the nodes run. It is read from SpaceEngineersGame.SE_VERSION in SPACE_ENGINEERS_BIN64,
/// the DedicatedServer64 the deployment also hands the nodes, on every Gateway start. A missing or
/// unreadable DS stops the Gateway instead of advertising a stale version.
/// </summary>
internal static class GameVersion
{
    private static string? _current;

    public static string Current =>
        _current ??= Read(Environment.GetEnvironmentVariable("SPACE_ENGINEERS_BIN64"));

    /// <summary>The Direct Transport frame codec is vanilla's; this only names the game build it runs against.</summary>
    public static string WireFingerprint =>
        DirectTransportConstants.ProtocolKey + "/game-frame-" + Current;

    /// <summary>Self-tests run without a DS install.</summary>
    internal static void UseForSelfTest(string version) => _current = version;

    internal static string Read(string? bin64)
    {
        if (string.IsNullOrWhiteSpace(bin64))
            throw new InvalidOperationException(
                "SPACE_ENGINEERS_BIN64 is not set: the Gateway advertises the game version of the cluster's DedicatedServer64 and has no other source for it"
            );
        string path = Path.Combine(bin64, "SpaceEngineers.Game.dll");
        using var pe = new PEReader(File.OpenRead(path));
        MetadataReader metadata = pe.GetMetadataReader();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            if (
                metadata.GetString(type.Name) != "SpaceEngineersGame"
                || metadata.GetString(type.Namespace) != "SpaceEngineers.Game"
            )
                continue;
            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                if (metadata.GetString(field.Name) != "SE_VERSION")
                    continue;
                Constant constant = metadata.GetConstant(field.GetDefaultValue());
                if (constant.TypeCode != ConstantTypeCode.Int32)
                    break;
                return Format(metadata.GetBlobReader(constant.Value).ReadInt32());
            }
        }
        throw new InvalidDataException($"No SpaceEngineersGame.SE_VERSION constant in {path}");
    }

    /// <summary>MyVersion's text form: 1211018 -> 1.211.018.</summary>
    internal static string Format(int version) =>
        $"{version / 1000000}.{version / 1000 % 1000:000}.{version % 1000:000}";
}
