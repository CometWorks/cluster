using System;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1091: a drain's offline capture whose fresh World Authority clock read times out falls back to the last durable
	/// clock and asks no more; with no clock either, or any other capture failure, the drain gets no profiles and no
	/// exception, so its Final is still saved. Outside a drain the timeout still reaches the caller. Runs the real
	/// AuthorityClock callback Concealment calls, with the two clock reads swapped for fakes.
	/// </summary>
	private static void DrainClockFallback()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("drain-clock-fallback: " + message); }
		var fresh = OfflineCompensation.FreshClock;
		var lastKnown = OfflineCompensation.LastKnownClock;
		bool lost = OfflineCompensation.DrainClockLost;
		(string, long, long, long) Read() => OfflineCompensation.AuthorityClock();
		var known = ("epoch", 1091L, 3L, 7L);
		int freshReads = 0;
		try
		{
			OfflineCompensation.DrainClockLost = false;
			OfflineCompensation.FreshClock = () => { freshReads++; throw new TimeoutException("World Authority command timed out"); };
			OfflineCompensation.LastKnownClock = () => known;
			bool thrown = false;
			try { Read(); }
			catch (TimeoutException) { thrown = true; }
			Check(thrown && !OfflineCompensation.DrainClockLost, "outside a drain the timeout reaches the caller");

			(string, long, long, long) got = default;
			byte[] profiles = OfflineCompensation.DrainCapture(1091, () => { got = Read(); return new byte[] { 1 }; });
			Check(profiles is [1] && got == known && OfflineCompensation.DrainClockLost, "a timed out fresh read falls back to the last durable clock");
			freshReads = 0;
			OfflineCompensation.DrainCapture(1091, () => { got = Read(); return new byte[] { 1 }; });
			Check(freshReads == 0 && got == known, "a later drain capture does not wait for the World Authority again");

			OfflineCompensation.LastKnownClock = () => throw new InvalidOperationException("Current durable WA simulation clock is unavailable");
			Check(OfflineCompensation.DrainCapture(1091, () => { Read(); return new byte[] { 1 }; }) == null, "with no clock at all the drain gets no profiles and no exception");
			Check(OfflineCompensation.DrainCapture(1091, () => throw new System.IO.InvalidDataException("any capture failure")) == null,
				"any other capture failure gives no profiles and no exception");

			OfflineCompensation.DrainClockLost = false;
			OfflineCompensation.FreshClock = () => ("fresh", 2L, 3L, 7L);
			OfflineCompensation.DrainCapture(1091, () => { got = Read(); return null; });
			Check(got.Item1 == "fresh" && !OfflineCompensation.DrainClockLost, "a fresh read that answers is used");
		}
		finally
		{
			OfflineCompensation.FreshClock = fresh;
			OfflineCompensation.LastKnownClock = lastKnown;
			OfflineCompensation.DrainClockLost = lost;
		}
	}
}
