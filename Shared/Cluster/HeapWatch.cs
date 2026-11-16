using System;
using System.IO;
using System.Threading;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-1382: the World Authority's managed heap grew from 1.5 to 67 GB in 70 minutes and froze its host, every node with it,
/// and the process left no evidence of what held the memory. This watch writes one heap dump when the heap first passes
/// wa.heapDumpMb, and ends the process (exit 6) when it passes wa.heapLimitMb, so Host restarts a WA that still fits in
/// memory instead of the host swapping to a standstill.
/// </summary>
internal static class HeapWatch
{
    internal const int ExitCode = 6;
    private const int MB = 1 << 20;

    internal enum Step
    {
        None,
        Dump,
        Exit,
    }

    internal static Step Decide(long heapMb, int dumpMb, int limitMb, bool dumped)
    {
        if (limitMb > 0 && heapMb >= limitMb)
            return Step.Exit;
        if (dumpMb > 0 && !dumped && heapMb >= dumpMb)
            return Step.Dump;
        return Step.None;
    }

    private static int m_started;

    internal static void Start()
    {
        if (Interlocked.Exchange(ref m_started, 1) != 0)
            return;
        new Thread(Watch) { IsBackground = true, Name = "ClusterHeapWatch" }.Start();
    }

    private static void Watch()
    {
        bool dumped = false;
        while (true)
        {
            Thread.Sleep(2000);
            if (!ClusterRuntime.IsWorldAuthority)
                continue;
            long heapMb = GC.GetTotalMemory(false) / MB;
            switch (
                Decide(
                    heapMb,
                    ClusterConfig.GetInt("wa.heapDumpMb"),
                    ClusterConfig.GetInt("wa.heapLimitMb"),
                    dumped
                )
            )
            {
                case Step.Dump:
                    dumped = true;
                    Log(
                        "managed heap " + heapMb + " MB passed wa.heapDumpMb; " + WriteDump(heapMb)
                    );
                    Log(Facts(collect: false));
                    break;
                case Step.Exit:
                    // A heap that jumped past both marks between two checks still leaves its dump.
                    if (!dumped)
                        Log(
                            "managed heap "
                                + heapMb
                                + " MB passed wa.heapLimitMb; "
                                + WriteDump(heapMb)
                        );
                    Log(Facts(collect: true));
                    ClusterRuntime.ExitProcess(
                        ExitCode,
                        "managed heap " + heapMb + " MB passed wa.heapLimitMb"
                    );
                    return;
            }
        }
    }

    /// <summary>
    /// SE-1561: the dump of the second runaway heap was deleted before anyone read it, and the log said nothing about what
    /// the heap held. These numbers stay in the log: what the collector sees, and the node link's send queues, which have
    /// no cap. With <paramref name="collect"/> the heap is collected first, so "live" tells kept memory from garbage the
    /// collector had not reached; that pause is only taken right before the exit.
    /// </summary>
    internal static string Facts(bool collect)
    {
        try
        {
            string live = collect ? " live=" + GC.GetTotalMemory(true) / MB + "MB" : string.Empty;
#if NET5_0_OR_GREATER
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            var generations = new System.Text.StringBuilder();
            ReadOnlySpan<GCGenerationInfo> sizes = info.GenerationInfo;
            string[] names = { "gen0", "gen1", "gen2", "loh", "poh" };
            for (int i = 0; i < sizes.Length && i < names.Length; i++)
                generations
                    .Append(' ')
                    .Append(names[i])
                    .Append('=')
                    .Append(sizes[i].SizeAfterBytes / MB)
                    .Append("MB");
            return "facts:"
                + live
                + " heap="
                + info.HeapSizeBytes / MB
                + "MB committed="
                + info.TotalCommittedBytes / MB
                + "MB fragmented="
                + info.FragmentedBytes / MB
                + "MB"
                + generations
                + " collections="
                + GC.CollectionCount(0)
                + "/"
                + GC.CollectionCount(1)
                + "/"
                + GC.CollectionCount(2)
                + " workingSet="
                + Environment.WorkingSet / MB
                + "MB linkQueue="
                + System.Text.Json.JsonSerializer.Serialize(
                    DirectTransportNodeLinkPatch.GlobalQueueDepths()
                );
#else
            return "facts:"
                + live
                + " heap="
                + GC.GetTotalMemory(false) / MB
                + "MB gen2= loh= collections="
                + GC.CollectionCount(0)
                + "/"
                + GC.CollectionCount(1)
                + "/"
                + GC.CollectionCount(2)
                + " linkQueue="
                + System.Text.Json.JsonSerializer.Serialize(
                    DirectTransportNodeLinkPatch.GlobalQueueDepths()
                );
#endif
        }
        catch (Exception exception)
        {
            return "facts: unavailable (" + exception.GetType().Name + "): " + exception.Message;
        }
    }

    private static void Log(string text) =>
        MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] heap watch: " + text);

    /// <summary>
    /// A dump with the managed heap, readable with dotnet-dump (dumpheap -stat, gcroot). The folder is the one crash dumps go to
    /// (DOTNET_DbgMiniDumpName), else the node's data root. Never throws; skipped when the disk has less room than the
    /// process's working set and 1 GB. The process stands still while the tool writes.
    /// </summary>
    internal static string WriteDump(long heapMb)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return "no dump: Linux only";
            string crashDumps = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName");
            string folder = !string.IsNullOrEmpty(crashDumps)
                ? Path.GetDirectoryName(Path.GetFullPath(crashDumps))
                : Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT")
                    ?? Environment.CurrentDirectory;
            long freeMb = new DriveInfo(folder).AvailableFreeSpace / MB,
                needMb = Environment.WorkingSet / MB + 1024;
            if (freeMb < needMb)
                return "no dump: " + freeMb + " MB free in " + folder + ", needs " + needMb + " MB";
            string path = Path.Combine(
                folder,
                "heap-" + Environment.ProcessId + "-" + heapMb + "MB.dmp"
            );
            long started = Environment.TickCount64;
            const uint withHeap = 2;
            GameThreadStacks.RequestDump(path, withHeap, 0);
            return "heap dump "
                + path
                + " written in "
                + (Environment.TickCount64 - started) / 1000.0
                + " s";
        }
        catch (Exception exception)
        {
            return "no dump (" + exception.GetType().Name + "): " + exception.Message;
        }
    }
}
