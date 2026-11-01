namespace ClusterPlugin;

/// <summary>
/// Seams of VRage.Library.Utils.WaitForTargetFrameRate.
/// Read by the TickPerFrame getter prefix in Shared.Patches.WaitForTargetFrameRatePatch.
/// </summary>
public static class WaitForTargetFrameRateSeams
{
	// ClusterPlugin seam: hot-spare dormancy (HotSpareNodes.md). A registered but empty cluster
	// node throttles the WHOLE loop instead of pausing it - a stopped loop deadlocks the invoke
	// queue and trips the gateway's SimulationFrame stall watchdog. 0 = use the constructor value.
	// TickPerFrame recomputes per Wait() and the catch-up guard there absorbs the rate switch.
	public static volatile float ClusterTargetFrequency;

	// T-0336: control requests waiting for the game thread. Above 0 the throttle is lifted, so a
	// dormant node answers at its normal frame rate (after at most the frame it is sleeping in).
	public static int PendingControlCalls;
}
