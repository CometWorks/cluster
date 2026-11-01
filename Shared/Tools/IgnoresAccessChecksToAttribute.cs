// The IgnoresAccessChecksToAttribute class required to use publicized assemblies at runtime.
// Defined only when the plugin is built by Magnetar (or Pulsar); the Krafs.Publicizer package
// provides it when the project is built in an IDE or by msbuild (LOCAL_BUILD).
#if !LOCAL_BUILD

namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class IgnoresAccessChecksToAttribute : Attribute
{
    public IgnoresAccessChecksToAttribute(string assemblyName)
    {
        AssemblyName = assemblyName;
    }

    public string AssemblyName { get; }
}

#endif
