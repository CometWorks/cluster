using System;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// #1390 wreck cleanup, the pure parts: the regime's hysteresis (in below the threshold, out at threshold + 0.02, never
	/// with threshold 0), the wreck shape (block count and blocks that are not cheap), which clear count decides in which
	/// regime, and the cleanup.wreck* knobs (off by default).
	/// </summary>
	private static void WreckCleanupRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("wreck-cleanup: " + message); }
		Check(!WreckageCleaner.NextAggressive(false, 0.95, 0.95) && WreckageCleaner.NextAggressive(false, 0.949, 0.95), "in only below the threshold");
		Check(WreckageCleaner.NextAggressive(true, 0.96, 0.95) && !WreckageCleaner.NextAggressive(true, 0.97, 0.95), "out only at threshold + 0.02");
		Check(!WreckageCleaner.NextAggressive(false, 0.1, 0) && !WreckageCleaner.NextAggressive(true, 0.1, 0), "threshold 0: never aggressive");
		Check(WreckageCleaner.IsWreckShape(50, 2, 50, 2) && !WreckageCleaner.IsWreckShape(51, 0, 50, 2) && !WreckageCleaner.IsWreckShape(10, 3, 50, 2)
			&& !WreckageCleaner.IsWreckShape(0, 0, 50, 2), "at most 50 blocks, at most 2 not cheap, not empty");
		Check(WreckageCleaner.IsDue(false, 120, 0, 120) && !WreckageCleaner.IsDue(false, 119, 999, 120), "normal: the sync-distance clear count decides");
		Check(WreckageCleaner.IsDue(true, 0, 120, 120) && !WreckageCleaner.IsDue(true, 999, 119, 120), "aggressive: the touch-radius clear count decides");
		Check(!WreckageCleaner.IsDue(false, 1e9, 1e9, 0), "wreckSeconds 0: never due");
		Check(ClusterConfig.DefaultOf("cleanup.wreckSeconds") == "0" && ClusterConfig.DefaultOf("cleanup.wreckBeforeOffline") == "false"
			&& ClusterConfig.DefaultOf("cleanup.wreckMaxBlocks") == "50" && ClusterConfig.DefaultOf("cleanup.wreckMaxOtherBlocks") == "2"
			&& ClusterConfig.DefaultOf("cleanup.wreckAggressiveSimSpeed") == "0.95" && ClusterConfig.DefaultOf("cleanup.wreckTouchMeters") == "50"
			&& ClusterConfig.DefaultOf("cleanup.wreckCheapComponents").Split(',').Length == 8, "cleanup.wreck* defaults (off)");
		Check(ClusterConfig.Normalize("cleanup.wreckAggressiveSimSpeed", "0.9") == "0.9" && ClusterConfig.Normalize("cleanup.wreckTouchMeters", "25.5") == "25.5",
			"the sim speed threshold and the touch radius are decimals");
		Check(ClusterConfig.Normalize("cleanup.wreckCheapComponents", " SteelPlate , Girder,,") == "SteelPlate,Girder", "the cheap components are an id list");
	}
}
