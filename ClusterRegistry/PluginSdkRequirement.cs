using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ClusterProtocol;

namespace ClusterRegistry;

internal static class PluginSdkRequirement
{
    internal static void CheckFile(string path)
    {
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        var types = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle))
            .Select(type => metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name)).ToHashSet();
        string? contract = ReadContract(metadata);
        if (contract is not null && !PluginSdkContract.IsSatisfiedBy(contract))
            throw new InvalidDataException($"Cluster needs PluginSdk contract {PluginSdkContract.Required}, but this Magnetar has {contract}. Use a Magnetar and a cluster release with compatible PluginSdk contracts.");
        // Magnetar releases before the contract: the version and the types tell.
        if (metadata.GetString(assembly.Name) != "PluginSdk" || (contract is null && assembly.Version < new Version(2, 4, 2, 1))
            || !types.Contains("PluginSdk.Config.ManagedPluginConfiguration")
            || !types.Contains("PluginSdk.Clustering.PluginCluster"))
            throw new InvalidDataException("Cluster 1.1.0 requires Magnetar 2.4.2.1 or newer with managed configuration and plugin services. Upgrade Magnetar before launching nodes.");
    }

    /// <summary>The PluginSdk.dll's declared contract ("major.minor"), null for Magnetar releases before it.</summary>
    internal static string? ReadContract(string path)
    {
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);
        return ReadContract(pe.GetMetadataReader());
    }

    // Reads [assembly: AssemblyMetadata("PluginSdkContract", ...)] without loading the DLL, which references game assemblies.
    private static string? ReadContract(MetadataReader metadata)
    {
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
            if (!metadata.StringComparer.Equals(type.Name, "AssemblyMetadataAttribute")
                || !metadata.StringComparer.Equals(type.Namespace, "System.Reflection")) continue;
            // Blob: prolog 0x0001, then the (key, value) constructor arguments as SerStrings.
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() == 1 && blob.ReadSerializedString() == PluginSdkContract.Key)
                return blob.ReadSerializedString();
        }
        return null;
    }
}

internal static partial class SelfTest
{
    private static void TestPluginSdkRequirement()
    {
        string path = Path.GetTempFileName();
        try
        {
            foreach (var (version, services, contract, accepted) in new (Version, bool, string?, bool)[] {
                (new Version(2, 4, 2, 0), true, null, false),
                (new Version(2, 4, 2, 1), false, null, false),
                (new Version(2, 4, 2, 1), true, null, true),
                (new Version(2, 4, 3, 7), true, "1.0", false),
                (new Version(2, 4, 3, 7), true, "1.1", true),
                (new Version(2, 4, 3, 7), true, "1.4", true),
                (new Version(2, 4, 3, 7), true, "2.0", false),
                (new Version(2, 4, 3, 7), true, "0.9", false),
                (new Version(2, 4, 3, 7), true, "1", false),
                (new Version(2, 4, 3, 7), false, "1.1", false) })
            {
                var metadata = new System.Reflection.Metadata.Ecma335.MetadataBuilder();
                metadata.AddModule(0, metadata.GetOrAddString("PluginSdk.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
                var assembly = metadata.AddAssembly(metadata.GetOrAddString("PluginSdk"), version, default, default, 0, 0);
                if (contract is not null) AddAssemblyMetadata(metadata, assembly, PluginSdkContract.Key, contract);
                metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default, default, default);
                metadata.AddTypeDefinition(System.Reflection.TypeAttributes.Public, metadata.GetOrAddString("PluginSdk.Config"),
                    metadata.GetOrAddString("ManagedPluginConfiguration"), default, default, default);
                if (services) metadata.AddTypeDefinition(System.Reflection.TypeAttributes.Public,
                    metadata.GetOrAddString("PluginSdk.Clustering"), metadata.GetOrAddString("PluginCluster"), default, default, default);
                var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
                    new System.Reflection.Metadata.Ecma335.MetadataRootBuilder(metadata), new BlobBuilder());
                var bytes = new BlobBuilder();
                pe.Serialize(bytes);
                File.WriteAllBytes(path, bytes.ToArray());
                bool passed;
                try { PluginSdkRequirement.CheckFile(path); passed = true; }
                catch (InvalidDataException) { passed = false; }
                Assert(passed == accepted);
                Assert(PluginSdkRequirement.ReadContract(path) == contract);
            }
        }
        finally { File.Delete(path); }
    }

    private static void AddAssemblyMetadata(System.Reflection.Metadata.Ecma335.MetadataBuilder metadata,
        AssemblyDefinitionHandle assembly, string key, string value)
    {
        var runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0), default, default, 0, default);
        var type = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System.Reflection"), metadata.GetOrAddString("AssemblyMetadataAttribute"));
        var signature = new BlobBuilder();
        new System.Reflection.Metadata.Ecma335.BlobEncoder(signature).MethodSignature(isInstanceMethod: true)
            .Parameters(2, returnType => returnType.Void(), parameters =>
            {
                parameters.AddParameter().Type().String();
                parameters.AddParameter().Type().String();
            });
        var constructor = metadata.AddMemberReference(type, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(signature));
        var arguments = new BlobBuilder();
        new System.Reflection.Metadata.Ecma335.BlobEncoder(arguments).CustomAttributeSignature(
            fixedArguments =>
            {
                fixedArguments.AddArgument().Scalar().Constant(key);
                fixedArguments.AddArgument().Scalar().Constant(value);
            },
            namedArguments => namedArguments.Count(0));
        metadata.AddCustomAttribute(assembly, constructor, metadata.GetOrAddBlob(arguments));
    }
}
