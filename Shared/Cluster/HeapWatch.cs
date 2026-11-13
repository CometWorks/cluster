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

	internal enum Step { None, Dump, Exit }

	internal static Step Decide(long heapMb, int dumpMb, int limitMb, bool dumped)
	{
		if (limitMb > 0 && heapMb >= limitMb) return Step.Exit;
		if (dumpMb > 0 && !dumped && heapMb >= dumpMb) return Step.Dump;
		return Step.None;
	}

	private static int m_started;

	internal static void Start()
	{
		if (Interlocked.Exchange(ref m_started, 1) != 0) return;
		new Thread(Watch) { IsBackground = true, Name = "ClusterHeapWatch" }.Start();
	}

	private static void Watch()
	{
		bool dumped = false;
		while (true)
		{
			Thread.Sleep(2000);
			if (!ClusterRuntime.IsWorldAuthority) continue;
			long heapMb = GC.GetTotalMemory(false) / MB;
			switch (Decide(heapMb, ClusterConfig.GetInt("wa.heapDumpMb"), ClusterConfig.GetInt("wa.heapLimitMb"), dumped))
			{
				case Step.Dump:
					dumped = true;
					Log("managed heap " + heapMb + " MB passed wa.heapDumpMb; " + WriteDump(heapMb));
					break;
				case Step.Exit:
					ClusterRuntime.ExitProcess(ExitCode, "managed heap " + heapMb + " MB passed wa.heapLimitMb");
					return;
			}
		}
	}

	private static void Log(string text) => MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] heap watch: " + text);

	/// <summary>
	/// A dump with the managed heap, readable with dotnet-dump (dumpheap -stat, gcroot). The folder is the one crash dumps go to
	/// (DOTNET_DbgMiniDumpName), else the node's data root. Never throws; skipped when the disk has less room than twice the
	/// process's working set. The process stands still while the tool writes.
	/// </summary>
	internal static string WriteDump(long heapMb)
	{
		try
		{
			if (!OperatingSystem.IsLinux()) return "no dump: Linux only";
			string crashDumps = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName");
			string folder = !string.IsNullOrEmpty(crashDumps) ? Path.GetDirectoryName(Path.GetFullPath(crashDumps))
				: Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT") ?? Environment.CurrentDirectory;
			long freeMb = new DriveInfo(folder).AvailableFreeSpace / MB, needMb = 2 * Environment.WorkingSet / MB;
			if (freeMb < needMb) return "no dump: " + freeMb + " MB free in " + folder + ", needs " + needMb + " MB";
			string path = Path.Combine(folder, "heap-" + Environment.ProcessId + "-" + heapMb + "MB.dmp");
			long started = Environment.TickCount64;
			const uint withHeap = 2;
			GameThreadStacks.RequestDump(path, withHeap, 0);
			return "heap dump " + path + " written in " + (Environment.TickCount64 - started) / 1000.0 + " s";
		}
		catch (Exception exception) { return "no dump (" + exception.GetType().Name + "): " + exception.Message; }
	}
}
