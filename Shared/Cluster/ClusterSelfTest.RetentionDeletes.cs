using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// SE-1346/SE-1347: the node side of retention (PartitionPersistence.ApplyRetentionDeletes), through its get/post seams
    /// and a temp data dir. A partition copy, a trimmed copy and a global copy (partition 0) are deleted and acked with their
    /// own ids; a directive outside the save roots is skipped and not acked. Then manifest.jsonl: past its 4 MB it moves to
    /// manifest.jsonl.1, replacing an older one, so the two files stay under 4 MB plus one rotation's worth.
    private static void RetentionDeletes()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("retention-deletes: " + message);
        }
        string root = Path.Combine(
            Path.GetTempPath(),
            "retention-deletes-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            string saves = Path.Combine(root, "saves");
            string File(string name)
            {
                string path = Path.Combine(saves, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, "x");
                return path;
            }
            string partition = File("P-0000000000000007/g00000001-s00000001.mps");
            string trimmed = File("P-0000000000000008/g00000001-s00000002.mps");
            string global = File("GLOBAL/g00000003-s00000004.mps");
            string foreign = Path.Combine(
                Path.GetTempPath(),
                "elsewhere-" + Guid.NewGuid().ToString("N") + ".mps"
            );
            object Directive(ulong p, long g, long s, string location, bool isGlobal = false) =>
                new
                {
                    SaveId = new
                    {
                        Partition = p,
                        Generation = g,
                        Sequence = s,
                    },
                    Node = "node-1",
                    Location = location,
                    Global = isGlobal,
                };
            string listing = JsonSerializer.Serialize(
                new[]
                {
                    Directive(7, 1, 1, partition),
                    Directive(8, 1, 2, trimmed),
                    Directive(0, 3, 4, global, isGlobal: true),
                    Directive(9, 1, 1, foreign),
                }
            );
            List<string> asked = new List<string>(),
                acked = new List<string>();
            var foreignSeen = new HashSet<string>(StringComparer.Ordinal);
            PartitionPersistence.ApplyRetentionDeletes(
                path =>
                {
                    asked.Add(path);
                    return JsonDocument.Parse(listing);
                },
                acked.Add,
                "node-1",
                5,
                saves,
                root,
                foreignSeen
            );
            Check(
                asked.SingleOrDefault() == "/registry/deletions?node=node-1&epoch=5",
                "one listing request: " + string.Join(" | ", asked)
            );
            Check(
                !System.IO.File.Exists(partition)
                    && !System.IO.File.Exists(trimmed)
                    && !System.IO.File.Exists(global),
                "the three copies are deleted"
            );
            Check(
                acked.Count == 3,
                "three acks, the foreign copy not acked: " + string.Join(" | ", acked)
            );
            Check(
                acked.Any(x =>
                    x.StartsWith(
                        "/registry/deletions/ack?partition=0&generation=3&sequence=4&location=",
                        StringComparison.Ordinal
                    )
                ),
                "the global copy is acked as partition 0"
            );
            Check(
                foreignSeen.Contains(Path.GetFullPath(foreign)),
                "the copy outside the save roots is skipped"
            );
            string[] manifest = System.IO.File.ReadAllLines(Path.Combine(root, "manifest.jsonl"));
            Check(
                manifest.Length == 3 && manifest.All(x => x.Contains("\"op\":\"delete\"")),
                "one tombstone per deleted copy"
            );

            // Rotation: lines of 1 MB; the fifth append finds 4 MB and starts a new file.
            string line = new string('m', 1 << 20) + Environment.NewLine;
            for (int i = 0; i < 12; i++)
                SaveManifest.Append(root, line, durable: false);
            long current = new FileInfo(Path.Combine(root, "manifest.jsonl")).Length,
                previous = new FileInfo(Path.Combine(root, "manifest.jsonl.1")).Length;
            Check(
                current < SaveManifest.RotateBytes + line.Length
                    && previous < SaveManifest.RotateBytes + line.Length,
                $"both files stay bounded: {current} and {previous} bytes"
            );
            Check(
                Directory.GetFiles(root, "manifest.jsonl*").Length == 2,
                "one rotated file, never more"
            );
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
