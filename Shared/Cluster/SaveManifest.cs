using System.IO;

namespace ClusterPlugin;

/// <summary>
/// SE-1347: <c>manifest.jsonl</c> in a node's data dir, one JSON line per save file written or deleted. Nothing reads it;
/// it is an audit trail for a person looking into a node's disk. It was append-only and reached 5 MB a node within days, so
/// at <see cref="RotateBytes"/> it moves to <c>manifest.jsonl.1</c> (replacing the previous one): at most twice that on disk.
/// </summary>
internal static class SaveManifest
{
    internal const long RotateBytes = 4L << 20;
    private static readonly object Sync = new object();

    /// <summary>Appends <paramref name="line"/> (with its newline) to <paramref name="root"/>/manifest.jsonl; fsyncs it when
    /// <paramref name="durable"/>. The partition and the World Authority writers of one node share the file, hence the lock.</summary>
    public static void Append(string root, string line, bool durable = true)
    {
        string path = Path.Combine(root, "manifest.jsonl");
        lock (Sync)
        {
            Directory.CreateDirectory(root);
            FileInfo file = new FileInfo(path);
            if (file.Exists && file.Length >= RotateBytes)
            {
                string previous = path + ".1";
                File.Delete(previous);
                File.Move(path, previous);
            }
            using FileStream stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read
            );
            using StreamWriter writer = new StreamWriter(stream);
            writer.Write(line);
            writer.Flush();
            stream.Flush(durable);
        }
    }
}
