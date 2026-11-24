using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace ModCompatScan;

internal static class ScriptReferences
{
    // MyScriptManager.UpdateCompatibility prepends these to every mod script before compiling it.
    internal const string UsingHeader =
        "using VRage;\r\nusing VRage.Game.Components;\r\nusing VRage.ObjectBuilders;\r\nusing VRage.ModAPI;\r\n"
        + "using VRage.Game.ModAPI;\r\nusing Sandbox.Common.ObjectBuilders;\r\nusing VRage.Game;\r\nusing Sandbox.ModAPI;\r\n"
        + "using VRage.Game.ModAPI.Interfaces;\r\nusing SpaceEngineers.Game.ModAPI;\r\n#line 1\r\n";

    /// <summary>The runtime's own assemblies first, then every managed DS64 assembly the runtime does not provide.</summary>
    internal static List<MetadataReference> References(string dedicated64)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(
                Path.PathSeparator
            )
        )
            paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        foreach (string path in Directory.GetFiles(dedicated64, "*.dll"))
        {
            if (paths.ContainsKey(Path.GetFileNameWithoutExtension(path)))
                continue;
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            if (reader.HasMetadata)
                paths[Path.GetFileNameWithoutExtension(path)] = path;
        }
        return paths
            .Values.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }
}
