#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClusterProtocol;

/// <summary>One Final save a node wrote during a local stop (T-0347) that the registry never catalogued.</summary>
public sealed class UnregisteredFinal
{
    public ulong Partition { get; set; }
    public long Generation { get; set; }
    public long Sequence { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>The save body's XXH3, 16 hex digits - what the catalogue stores.</summary>
    public string Checksum { get; set; } = "";
    public long ClusterTime { get; set; }
    public int Entities { get; set; }
    /// <summary>T-0237 home stamps as the save registration carries them ("entity:version,..."), or null.</summary>
    public string? Homes { get; set; }
    /// <summary>The save file's absolute path on the writer's host.</summary>
    public string Location { get; set; } = "";
    /// <summary>The node that owned the partition and wrote the file, and its registry epoch then.</summary>
    public string Writer { get; set; } = "";
    public long WriterEpoch { get; set; }
}

/// <summary>
/// T-0347: the journal of Final saves written while the registry was unreachable (a cluster stop with the registry
/// down). The file is the only record that a save newer than the catalogue's newest exists: the registry was never
/// told, and recovery reads the catalogue. One JSON line per save, appended and fsynced after the save file itself,
/// in the writer's <c>&lt;node&gt;-data/cluster</c> directory (a managed slot's <c>nodes/&lt;slot&gt;/data/cluster</c>). At the
/// next start the first node of the host reports every journal under its run root (node ids are never reused under node
/// control, so the writer itself does not come back) and the registry decides, save by save, whether it is still the
/// partition's newest state. Shared by the node plugin and the registry, whose self-test exercises it.
/// </summary>
public static class UnregisteredFinals
{
    public const string FileName = "unregistered-finals.jsonl";
    /// <summary>What a journal is renamed to once the registry has answered for it (kept as evidence, overwritten by the next).</summary>
    public const string ReportedFileName = "unregistered-finals.reported.jsonl";

    public static void Append(string clusterRoot, UnregisteredFinal entry)
    {
        Directory.CreateDirectory(clusterRoot);
        using FileStream stream = new FileStream(Path.Combine(clusterRoot, FileName), FileMode.Append, FileAccess.Write, FileShare.Read);
        using StreamWriter writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(entry) + "\n");
        writer.Flush();
        stream.Flush(true);
    }

    /// <summary>
    /// The journal's saves, oldest first per partition. A line that does not parse (the torn tail of a killed writer) is
    /// skipped and counted; the last line wins for a save id written twice (a retried write of the same slot).
    /// </summary>
    public static List<UnregisteredFinal> Read(string file, out int unreadable)
    {
        unreadable = 0;
        var byId = new Dictionary<(ulong, long, long), UnregisteredFinal>();
        foreach (string line in File.ReadAllLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            UnregisteredFinal? entry;
            try { entry = JsonSerializer.Deserialize<UnregisteredFinal>(line); }
            catch (JsonException) { entry = null; }
            if (entry == null || entry.Sequence < 1 || entry.Generation < 1 || string.IsNullOrWhiteSpace(entry.Checksum)
                || string.IsNullOrWhiteSpace(entry.Location) || string.IsNullOrWhiteSpace(entry.Writer))
            {
                unreadable++;
                continue;
            }
            byId[(entry.Partition, entry.Generation, entry.Sequence)] = entry;
        }
        return byId.Values.OrderBy(x => x.Partition).ThenBy(x => x.Generation).ThenBy(x => x.Sequence).ToList();
    }

    /// <summary>Every journal under the run root that holds <paramref name="dataRoot"/> (a node's CLUSTER_DATA_ROOT): this
    /// node's own and those of the nodes that ran on this host before it. #1298: a managed slot's data root is
    /// <c>&lt;runtime&gt;/nodes/&lt;slot&gt;/data</c> (Quasar.Host), so with <paramref name="managed"/> the journals are those of
    /// every slot in that <c>nodes</c> directory (<c>nodes/*/data/cluster</c>), as for ProxiedSavePath.Resolve.</summary>
    public static string[] JournalsUnder(string? dataRoot, bool managed = false)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) return Array.Empty<string>();
        string? runRoot = Path.GetDirectoryName(Path.GetFullPath(dataRoot!).TrimEnd(Path.DirectorySeparatorChar));
        if (runRoot == null || !Directory.Exists(runRoot)) return Array.Empty<string>();
        IEnumerable<string> dataRoots = managed && Path.GetDirectoryName(runRoot) is { } nodes
                && Path.GetFileName(nodes) == ProxiedSavePath.ManagedNodesDirectory
            ? Directory.GetDirectories(nodes).Select(slot => Path.Combine(slot, "data"))
            : Directory.GetDirectories(runRoot, "*-data");
        return dataRoots.Select(data => Path.Combine(data, "cluster", FileName)).Where(File.Exists)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The registry has answered for this journal: it leaves the name the next start would report again.</summary>
    public static void MarkReported(string file)
    {
        string target = Path.Combine(Path.GetDirectoryName(file)!, ReportedFileName);
        try
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(file, target);
        }
        catch (IOException) { }   // another node of this host reported the same journal first
    }
}
