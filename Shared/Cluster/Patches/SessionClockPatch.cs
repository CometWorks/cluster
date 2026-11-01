using System;
using Sandbox.Game.World;

namespace ClusterPlugin;

public static class SessionClockPatch
{
	/// <summary>
	/// The sun offset this node's game time currently carries. TimeOfDayService defers its vanilla
	/// client broadcast until this matches the received offset, so the ticks it hands clients are
	/// post-step. Game-thread only.
	/// </summary>
	public static long AppliedOffsetMilliseconds { get; private set; }

	/// <summary>
	/// A node's game time is the slew-only replication clock plus the WA's accumulated admin
	/// time-of-day offset (WorldAuthority.md §8.2). Keeping the offset out of the synced clock is
	/// what lets an admin set step game time exactly once per node while the replication timestamp
	/// stays "slewed, never stepped" (Partitioning.md §5).
	/// </summary>
	public static void Postfix(MySession __instance)
	{
		if (ClusterRuntime.IsWorldAuthority || !ClusterRuntime.HasAuthorityClock)
			return;

		long offset = TimeOfDayService.OffsetMilliseconds;
		double adjusted = ClusterRuntime.ClusterMilliseconds(__instance.ElapsedGameTime.TotalMilliseconds) + offset;
		if (double.IsFinite(adjusted) && adjusted >= 0)
		{
			__instance.ElapsedGameTime = TimeSpan.FromMilliseconds(adjusted);
			AppliedOffsetMilliseconds = offset;
		}
	}
}
