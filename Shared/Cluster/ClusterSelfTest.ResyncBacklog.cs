using System;
using System.Diagnostics;
using System.Threading;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1577: a replica asks for a subsystem's snapshot again every second until one arrives. On the live cluster
	/// restarted nodes asked for 2-3 MB snapshots while the World Authority's ordered Gateway queue was already long, so
	/// every answer queued behind the last one and each node's full snapshot set was queued up to 137 times: 4.7 million
	/// packets in the heap dump, 19.7 GB live at the exit. Thirty requests from one node while the first answer is still
	/// queued must build one snapshot, and a request after the queue drained must build a new one.
	/// </summary>
	private static void ResyncBacklog()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("resync-backlog: " + message); }
		if (!ClusterRuntime.IsWorldAuthority) return;
		Check(DirectTransportNodeLinkPatch.IsConnected, "the World Authority has no Gateway link");
		const string Subsystem = "self-test-resync-backlog", Origin = "self-test-resync-backlog-node";
		const int Requests = 30;
		string snapshot = new string('x', 2 * 1024 * 1024);
		int built = 0;
		GlobalStateChannel.RegisterSnapshot(Subsystem, () =>
		{
			Interlocked.Increment(ref built);
			return snapshot;
		}, _ => { });
		try
		{
			long heapBefore = GC.GetTotalMemory(true);
			int depthBefore = DirectTransportNodeLinkPatch.GlobalOrderedDepth();
			GlobalStateChannel.SelfTestResync(Subsystem, Origin, Requests);
			int depthAfter = DirectTransportNodeLinkPatch.GlobalOrderedDepth();
			long heapAfter = GC.GetTotalMemory(true);
			int burst = Volatile.Read(ref built);
			var drain = Stopwatch.StartNew();
			while (DirectTransportNodeLinkPatch.GlobalOrderedDepth() > 0 && drain.ElapsedMilliseconds < 90000) Thread.Sleep(50);
			long drainMs = drain.ElapsedMilliseconds;
			GlobalStateChannel.SelfTestResync(Subsystem, Origin, 1);
			int afterDrain = Volatile.Read(ref built) - burst;
			Console.WriteLine("[ClusterPlugin] self-test resync-backlog: requests=" + Requests + " snapshotsBuilt=" + burst
				+ " queuedPackets=" + (depthAfter - depthBefore) + " heapGrowthMB=" + (heapAfter - heapBefore) / (1024 * 1024)
				+ " drainMs=" + drainMs + " builtAfterDrain=" + afterDrain);
			Check(burst == 1, Requests + " requests while the first answer was queued built " + burst + " snapshots, "
				+ (depthAfter - depthBefore) + " packets queued, heap +" + (heapAfter - heapBefore) / (1024 * 1024) + " MB");
			Check(afterDrain == 1, "a request after the queue drained built " + afterDrain + " snapshots");
		}
		finally
		{
			snapshot = null;
		}
	}
}
