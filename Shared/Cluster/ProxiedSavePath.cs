#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClusterProtocol;

/// <summary>
/// T-0323: which file a node may serve as the proxy of a stopped save holder on its host. The registry grant binds the
/// exact catalogued location; this is the node's own check that the location really is a partition save file under
/// this host's run root, after resolving "..", "." and every symlink of every component (realpath). Shared by the node
/// plugin and the registry, whose self-test exercises it.
/// </summary>
public static class ProxiedSavePath
{
    /// <summary>The resolved file to read, or null when <paramref name="location"/> is not a .mps file under the run root
    /// that holds <paramref name="dataRoot"/> (this node's CLUSTER_DATA_ROOT). #1210: a managed slot's data root is
    /// <c>&lt;runtime&gt;/nodes/&lt;slot&gt;/data</c> (Quasar.Host), so with <paramref name="managed"/> the host's slots share
    /// that <c>nodes</c> directory, not the data root's parent (which holds only this slot).</summary>
    public static string? Resolve(string? location, string? dataRoot, bool managed = false)
    {
        if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(location)) return null;
        string? data = RealPath(dataRoot!);
        string? root = data == null ? null : Path.GetDirectoryName(data);
        if (managed && root != null && Path.GetDirectoryName(root) is { } nodes && Path.GetFileName(nodes) == ManagedNodesDirectory)
            root = nodes;
        string? file = RealPath(location!);
        if (root == null || file == null) return null;
        return file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && file.EndsWith(".mps", StringComparison.Ordinal) ? file : null;
    }

    /// <summary>The directory Quasar.Host keeps every managed slot of a host in (<c>runtime/nodes</c>).</summary>
    public const string ManagedNodesDirectory = "nodes";

    /// <summary>T-0348: the save-file route's answer when the catalogued file is not on the holder's (or its proxy's) disk.
    /// Distinct from 404 (unknown route), 401 (grant or path refused) and 500, so the requester can tell "this copy does
    /// not exist" - which it reports as corrupt, like a torn file - from a failure that says nothing about the copy.</summary>
    public const int MissingStatus = 410;

    /// <summary>Whether a failed read of a save file means the file (or its directory) is not there.</summary>
    public static bool IsMissing(Exception error) => error is FileNotFoundException || error is DirectoryNotFoundException;

    /// <summary>realpath: the absolute path with every "..", "." and symlink (of any component) resolved; null on a link loop.</summary>
    public static string? RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full) ?? "/";
        var pending = new Stack<string>(full.Substring(current.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Reverse());
        int links = 0;
        while (pending.Count != 0)
        {
            string part = pending.Pop();
            if (part == ".") continue;
            if (part == "..") { current = Path.GetDirectoryName(current) ?? current; continue; }
            string next = Path.Combine(current, part);
            string? target = new FileInfo(next).LinkTarget;
            if (target == null) { current = next; continue; }
            if (++links > 40) return null;
            string absolute = Path.IsPathFullyQualified(target) ? target : Path.Combine(current, target);
            string root = Path.GetPathRoot(absolute) ?? "/";
            foreach (string component in absolute.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Reverse())
                pending.Push(component);
            current = root;
        }
        return current;
    }
}
