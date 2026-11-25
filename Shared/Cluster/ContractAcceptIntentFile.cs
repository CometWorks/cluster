using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ClusterPlugin;

internal static class ContractAcceptIntentFile
{
    public static string[] Pending(string directory) =>
        Directory
            .GetFiles(directory, "*.intent")
            .Concat(Directory.GetFiles(directory, "*.release"))
            .ToArray();

    public static bool IsReleasing(string path) =>
        path.EndsWith(".release", StringComparison.Ordinal);

    public static async Task<string> PrepareRelease(string path, Func<Task<bool>> ensureReserved)
    {
        // Recreating a reservation after its Release can strand it if the authority
        // still remembers the Release result but has evicted the older Reserve.
        if (IsReleasing(path))
            return path;
        if (!await ensureReserved().ConfigureAwait(false))
            return null;
        string release = Path.ChangeExtension(path, ".release");
        File.Move(path, release);
        return release;
    }
}
