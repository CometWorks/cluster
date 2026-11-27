using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ModCompatScan;

internal static class WorkshopScripts
{
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
