using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace ModCompatScan;

internal static class WorkshopScripts
{
    // DS 1.211.019: MyScriptManager.UpdateCompatibility applies the current game's
    // substitutions to source text, including old namespaces still used in Workshop mods.
    // Loading Sandbox.Game needs a resolver for the DedicatedServer64 folder.
    private static readonly Dictionary<string, string> CompatibilityChanges =
        (Dictionary<string, string>)
            Assembly
                .Load("Sandbox.Game")
                .GetType("Sandbox.Game.World.MyScriptManager", throwOnError: true)
                .GetField("m_compatibilityChanges", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

    /// <summary>A script's text as the game compiles it: its using header, then its substitutions.</summary>
    internal static string PrepareSource(string text)
    {
        text = ScriptReferences.UsingHeader + text;
        foreach (var change in CompatibilityChanges)
            text = text.Replace(change.Key, change.Value);
        return text;
    }

    internal static IEnumerable<(string Path, string Text)> Scripts(string mod)
    {
        foreach (
            var data in Directory
                .EnumerateDirectories(mod)
                .Where(d => Path.GetFileName(d).Equals("Data", StringComparison.OrdinalIgnoreCase))
        )
        foreach (
            var scripts in Directory
                .EnumerateDirectories(data)
                .Where(d =>
                    Path.GetFileName(d).Equals("Scripts", StringComparison.OrdinalIgnoreCase)
                )
        )
        foreach (
            var file in Directory
                .EnumerateFiles(scripts, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        )
            yield return (
                Path.GetRelativePath(scripts, file).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllText(file)
            );
        foreach (var file in Directory.EnumerateFiles(mod, "*_legacy.bin"))
        {
            using var zip = ZipFile.OpenRead(file);
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (
                    !path.StartsWith("Data/Scripts/", StringComparison.OrdinalIgnoreCase)
                    || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                using var reader = new StreamReader(entry.Open());
                yield return (path[13..], reader.ReadToEnd());
            }
        }
    }
}
