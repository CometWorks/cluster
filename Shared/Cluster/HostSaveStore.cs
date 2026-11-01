#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace ClusterProtocol;

/// <summary>
/// T-0340: the per-host save store, <c>&lt;runRoot&gt;/saves/&lt;host&gt;/</c>. Every node of a host writes its saves and
/// its fetched copies there, under the names a node's own save directory used (<c>P-&lt;X16&gt;/g&lt;X8&gt;-s&lt;X8&gt;.mps</c>,
/// <c>GLOBAL/...</c>), so a file belongs to the host and a stopped node leaves nothing catalogued in its data dir.
/// The path is a pure function of (host, save id): the registry computes the same one (it never takes a path from a
/// request) and so does a node that serves the file. Shared by both plugins and the registry, whose self-test
/// exercises the path and the install. The registry alone switches the store on (CLUSTER_HOST_STORE=1 in the gateway's
/// environment) and tells each node in its registration answer; a node never decides it from its own environment, so
/// the two cannot disagree about where a save lies.
///
/// Several processes write one directory, so a file is installed, never overwritten: written under a temporary name
/// that carries the writer's node id (the host agent's sweep can tell whose leftover it is), then linked into place
/// without replacing (<see cref="Install"/>).
/// </summary>
public static class HostSaveStore
{
    /// <summary>The catalogue's holder key of <paramref name="host"/>'s store.</summary>
    public static string Holder(string host) => "host:" + host;

    /// <summary>The store directory of <paramref name="host"/>. The host name is one path component, or this throws.</summary>
    public static string Root(string runRoot, string host) =>
        host.Length == 0 || host == "." || host == ".." || host.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
            ? throw new InvalidDataException("Host name '" + host + "' cannot name a save store directory")
            : Path.Combine(runRoot, "saves", host);

    /// <summary>
    /// Where a node keeps its save files: its host's store when the registry told it so at registration
    /// (<paramref name="store"/>; the run root is the parent of the node's data dir), else
    /// <c>&lt;dataRoot&gt;/cluster/saves</c> as it always was.
    /// </summary>
    public static string NodeSaves(string dataRoot, string host, bool store)
    {
        string data = Path.GetFullPath(dataRoot);
        if (!store) return Path.Combine(data, "cluster", "saves");
        string trimmed = data.TrimEnd(Path.DirectorySeparatorChar);
        return Root(Path.GetDirectoryName(trimmed) ?? throw new InvalidDataException("Data root '" + dataRoot + "' has no parent run root"), host);
    }

    public static string PartitionSave(string saves, ulong partition, long generation, long sequence) =>
        Path.Combine(saves, "P-" + partition.ToString("X16"), FileName(generation, sequence));

    public static string GlobalSave(string saves, long generation, long sequence) => Path.Combine(saves, "GLOBAL", FileName(generation, sequence));

    /// <summary>
    /// The file a store-mode node reads for a save it is asked for: the store's, or the one in its own data dir
    /// (<paramref name="legacy"/>) when only that exists. The catalogue names both kinds while files are unmoved - the
    /// registry re-keys a location only once it sees the file in the store - so a node must still serve its old path.
    /// </summary>
    public static string Held(string store, string legacy) => !File.Exists(store) && File.Exists(legacy) ? legacy : store;

    private static string FileName(long generation, long sequence) =>
        string.Format(CultureInfo.InvariantCulture, "g{0:X8}-s{1:X8}.mps", generation, sequence);

    /// <summary>A temporary name beside <paramref name="final"/> that no other writer uses: the node id and a fresh guid.</summary>
    public static string Temporary(string final, string node) =>
        Path.Combine(Path.GetDirectoryName(final) ?? string.Empty, "." + Path.GetFileName(final) + "." + node + "." + Guid.NewGuid().ToString("N") + ".tmp");

    /// <summary>The resumable partial download of <paramref name="final"/> by <paramref name="node"/>: two nodes of one host
    /// fetching the same save (split siblings recovering their parent's cover save) must not append to one file.</summary>
    public static string Partial(string final, string node) => final + "." + node + ".part";

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    /// <summary>
    /// Gives <paramref name="temporary"/> the name <paramref name="final"/> unless that name exists (then false):
    /// link(2), which the file system does atomically or refuses with EEXIST. File.Move without overwrite is NOT
    /// that on .NET 10 / Linux - it checks for the target and then renames over it: eight concurrent installs all
    /// "succeeded" in the registry self-test. On Windows a move without overwrite is the atomic form.
    /// </summary>
    private static bool LinkNew(string temporary, string final)
    {
        if (Path.DirectorySeparatorChar == '\\')
        {
            try
            {
                File.Move(temporary, final);
                return true;
            }
            catch (IOException) when (File.Exists(final)) { return false; }
        }
        if (link(temporary, final) == 0) return true;
        const int Exists = 17;
        int error = Marshal.GetLastWin32Error();
        return error == Exists ? false : throw new IOException("link " + temporary + " -> " + final + " failed with errno " + error);
    }

    /// <summary>
    /// Installs <paramref name="temporary"/> as <paramref name="final"/> without replacing a file that is already
    /// there (<see cref="LinkNew"/>). When the target exists, <paramref name="existing"/> judges it: true - it is the file being installed (another node got
    /// there first), which is success; null - it is damaged (unreadable as a save: a torn write), it is replaced;
    /// false - it is a readable save with other content, replaced only if <paramref name="replaceOther"/> (a fetched
    /// copy, verified against the catalogue, is the right one), else refused with an <see cref="IOException"/>.
    /// The temporary file is gone afterwards in every case. Returns whether this call put the file there.
    /// </summary>
    public static bool Install(string temporary, string final, Func<string, bool?> existing, bool replaceOther)
    {
        try
        {
            if (LinkNew(temporary, final)) return true;
            bool? verdict = existing(final);
            if (verdict == true) return false;
            if (verdict == false && !replaceOther)
                throw new IOException("Save store file " + final + " exists with other content; not replaced");
            File.Move(temporary, final, true);
            return true;
        }
        finally { File.Delete(temporary); }
    }
}
