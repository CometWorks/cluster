using System;
using System.Collections.Generic;
using System.Linq;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// T-0392 import.activateMsPerSecond: the pacing decision (ImportActivatePacer) with the clock passed
	/// in, on private pacer instances (the node's ledger is never touched). N = 0 decides exactly what the
	/// import decided before the knob; the ledger window rolls; a slice is sized to the remaining allowance
	/// through the estimate and takes at least one group; concurrent imports are served oldest first; an
	/// import held back past W goes forced, and W is shared by imports that finish on the node within 10 s;
	/// a 40-entity import at 5 ms/entity under N = 70 spreads over three one-second windows, none over
	/// N + one group.
	/// </summary>
	private static void ActivatePacing()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("activate-pacing: " + message); }
		const double W = ImportActivatePacer.DefaultForceAfterWaitMs;

		// N = 0: every frame goes, with the per-frame budget handed through unchanged, whatever the ledger
		// holds; the slices are the ones Take makes without the knob.
		int[][] plans = { new[] { 1, 1, 1, 1, 1, 1, 1 }, new[] { 2, 1, 3, 1, 1, 1 }, new[] { 25, 1, 4 }, Enumerable.Repeat(1, 40).ToArray(), new[] { 3, 3, 3, 3 } };
		foreach (int[] sizes in plans)
			foreach (int perFrame in new[] { 1, 3, 20 })
				foreach ((int frameBudget, double estimate) in new[] { (0, 0.0), (0, 5.0), (100, 0.0), (100, 30.0), (20, 5.0) })
				{
					ImportActivatePacer off = new ImportActivatePacer(W);
					off.Spend(null, 5000, 0); // a full window must not matter at N = 0
					ImportActivatePacer.Ticket ticket = off.Register();
					List<int> paced = new List<int>(), plain = new List<int>();
					for (int next = 0, frame = 0; next < sizes.Length; frame++)
					{
						ImportActivatePacer.Decision decision = off.Decide(ticket, 0, frameBudget, false, frame * 16);
						Check(decision.Go && !decision.Forced && decision.PacedFrames == 0 && decision.BudgetMs == frameBudget,
							"N=0 must go at once with the per-frame budget (" + decision + ")");
						int take = ImportActivateSlices.Take(sizes, next, perFrame, decision.BudgetMs, estimate, out _);
						off.Spend(ticket, 300, frame * 16);
						paced.Add(take);
						next += take;
					}
					for (int next = 0; next < sizes.Length;)
					{
						int take = ImportActivateSlices.Take(sizes, next, perFrame, frameBudget, estimate, out _);
						plain.Add(take);
						next += take;
					}
					Check(paced.SequenceEqual(plain), "N=0 slices " + string.Join(",", paced) + " differ from the unpaced " + string.Join(",", plain));
					Check(ticket.PacedFramesTotal == 0 && ticket.ForcedFrames == 0, "N=0 never waits or forces");
				}

		// The window rolls: spent at t counts until t + 1000 ms, not at t + 1000.
		ImportActivatePacer window = new ImportActivatePacer(W);
		window.Spend(null, 50, 100);
		window.Spend(null, 20, 600);
		Check(window.SpentMs(100) == 70 && window.SpentMs(1099.9) == 70, "both entries inside the window");
		Check(window.SpentMs(1100) == 20, "the entry at 100 ms is free at 1100 ms");
		Check(window.SpentMs(1600) == 0, "the window is empty at 1600 ms");

		// An empty window never makes an import wait; the slice is sized to N (or the per-frame budget if smaller).
		ImportActivatePacer empty = new ImportActivatePacer(W);
		ImportActivatePacer.Ticket lone = empty.Register();
		ImportActivatePacer.Decision first = empty.Decide(lone, 70, 0, false, 0);
		Check(first.Go && !first.Forced && first.BudgetMs == 70 && first.SpentMs == 0, "empty window: go with the whole allowance (" + first + ")");
		Check(empty.Decide(lone, 70, 50, false, 0).BudgetMs == 50, "empty window: the per-frame budget still caps the slice");

		// Remaining sizing through the estimate: 40 of 70 spent, 5 ms/entity -> 6 entities; with a 20 ms frame budget 4.
		ImportActivatePacer sizing = new ImportActivatePacer(W);
		sizing.Spend(null, 40, 0);
		ImportActivatePacer.Ticket sized = sizing.Register();
		int[] ones = Enumerable.Repeat(1, 20).ToArray();
		ImportActivatePacer.Decision rest = sizing.Decide(sized, 70, 0, false, 10);
		Check(rest.Go && rest.BudgetMs == 30 && rest.SpentMs == 40, "remaining = N - spent (" + rest + ")");
		Check(ImportActivateSlices.Take(ones, 0, 20, rest.BudgetMs, 5, out bool restOver) == 6 && !restOver, "30 ms at 5 ms/entity is 6 entities");
		ImportActivatePacer.Decision capped = sizing.Decide(sized, 70, 20, false, 10);
		Check(capped.BudgetMs == 20 && ImportActivateSlices.Take(ones, 0, 20, capped.BudgetMs, 5, out _) == 4, "min(per-frame budget, remaining)");
		// No estimate yet (the node's first slice): the remaining allowance cannot size it, the count cap does.
		Check(ImportActivateSlices.Take(ones, 0, 20, rest.BudgetMs, 0, out _) == 20, "no estimate: the count cap");

		// One group larger than the allowance is a slice of its own, flagged.
		ImportActivatePacer big = new ImportActivatePacer(W);
		big.Spend(null, 60, 0);
		ImportActivatePacer.Decision small = big.Decide(big.Register(), 70, 0, false, 10);
		Check(small.Go && small.BudgetMs == 10, "10 ms left (" + small + ")");
		Check(ImportActivateSlices.Take(new[] { 5, 1 }, 0, 20, small.BudgetMs, 5, out bool bigOver) == 1 && bigOver, "a 5-entity group (25 ms) over 10 ms left: alone, overBudget");

		// The window full: wait, nothing per frame but the count; free again: go, with the frames waited.
		ImportActivatePacer full = new ImportActivatePacer(W);
		full.Spend(null, 70, 0);
		ImportActivatePacer.Ticket held = full.Register();
		Check(!full.Decide(held, 70, 0, false, 16).Go && !full.Decide(held, 70, 0, false, 32).Go, "a full window holds the import back");
		Check(full.Decide(held, 70, 0, true, 48) is { Go: true, Forced: false, PacedFrames: 2 }, "an abandoned/discarded import goes at once (its frame throws)");
		Check(!full.Decide(held, 70, 0, false, 64).Go, "held back again");
		ImportActivatePacer.Decision freed = full.Decide(held, 70, 0, false, 1000);
		Check(freed is { Go: true, Forced: false, PacedFrames: 1, BudgetMs: 70.0 } && held.PacedFramesTotal == 3, "the window freed at 1000 ms (" + freed + ")");

		// Two imports: oldest first. A registered before B; both held back by a full window; when it frees, B
		// (asking first in the frame) yields to A; the frame after, B takes what A left.
		ImportActivatePacer fifo = new ImportActivatePacer(W);
		ImportActivatePacer.Ticket a = fifo.Register(), b = fifo.Register();
		fifo.Spend(null, 70, 0);
		Check(!fifo.Decide(b, 70, 0, false, 16).Go && !fifo.Decide(a, 70, 0, false, 16).Go, "both wait on a full window");
		Check(!fifo.Decide(b, 70, 0, false, 1000).Go, "the younger yields to an older waiting import even with allowance left");
		Check(fifo.Decide(a, 70, 0, false, 1000) is { Go: true, BudgetMs: 70.0 }, "the oldest goes first");
		fifo.Spend(a, 30, 1000);
		Check(fifo.Decide(b, 70, 0, false, 1016) is { Go: true, BudgetMs: 40.0, PacedFrames: 2 }, "then the younger, with the remainder");
		fifo.Spend(b, 40, 1016);
		Check(!fifo.Decide(a, 70, 0, false, 1032).Go && !fifo.Decide(b, 70, 0, false, 1032).Go, "the shared allowance is spent for both");
		fifo.Unregister(a, 1040);
		fifo.Unregister(b, 1040);

		// Forced progress: a ledger that never frees (another import's frames keep it full) holds an import back
		// for W in total, then its frames go regardless, sized by the per-frame budget alone.
		ImportActivatePacer hog = new ImportActivatePacer(W);
		ImportActivatePacer.Ticket starved = hog.Register();
		int forcedAt = -1;
		for (int t = 0; t <= 5000 && forcedAt < 0; t += 16)
		{
			hog.Spend(null, 70, t);
			ImportActivatePacer.Decision d = hog.Decide(starved, 70, 25, false, t);
			if (!d.Go) continue;
			Check(d.Forced && d.BudgetMs == 25, "a full ledger only lets a forced frame through (" + d + ")");
			forcedAt = t;
		}
		Check(forcedAt > W && forcedAt <= W + 2 * 16, "forced once the import waited W (at " + forcedAt + " ms)");
		hog.Spend(null, 70, forcedAt + 16);
		Check(hog.Decide(starved, 70, 25, false, forcedAt + 16) is { Go: true, Forced: true, PacedFrames: 0 }, "past W every later frame goes forced, without waiting");
		hog.Unregister(starved, forcedAt + 20);
		// W is the node's within 10 s: the next import is not held back again inside that horizon...
		ImportActivatePacer.Ticket following = hog.Register();
		hog.Spend(null, 70, forcedAt + 40);
		Check(hog.Decide(following, 70, 25, false, forcedAt + 40) is { Go: true, Forced: true }, "an import right after a starved one is not held back");
		hog.Unregister(following, forcedAt + 50);
		// ...and is again once the earlier waits are older than the gateway's import timeout.
		ImportActivatePacer.Ticket later = hog.Register();
		double after = forcedAt + 20 + ImportActivatePacer.DeadlineHorizonMs;
		hog.Spend(null, 70, after);
		Check(!hog.Decide(later, 70, 25, false, after).Go, "waits older than 10 s no longer count against W");

		// A lone 40-entity import, loose grids at 5 ms/entity (estimate = cost), N = 70, count cap 20, no
		// per-frame budget, 16 ms frames: slices of 14, 14, 12 one window apart, never forced, every 1 s
		// window within N + one group.
		ImportActivatePacer sim = new ImportActivatePacer(W);
		ImportActivatePacer.Ticket forty = sim.Register();
		int[] loose = Enumerable.Repeat(1, 40).ToArray();
		List<(int At, int Entities, double Ms, bool Forced)> frames = new List<(int, int, double, bool)>();
		for (int t = 0, done = 0; done < loose.Length && t < 20000; t += 16)
		{
			ImportActivatePacer.Decision d = sim.Decide(forty, 70, 0, false, t);
			if (!d.Go) continue;
			int take = ImportActivateSlices.Take(loose, done, 20, d.BudgetMs, 5, out _);
			done += take;
			frames.Add((t, take, take * 5.0, d.Forced));
			sim.Spend(forty, take * 5.0, t);
		}
		string shape = string.Join(" ", frames.Select(f => f.Entities + "@" + f.At));
		Check(frames.Sum(f => f.Entities) == 40, "all 40 activated: " + shape);
		Check(frames.Last().At - frames.First().At >= 2000, "spread over three one-second windows: " + shape);
		Check(frames.All(f => !f.Forced) && forty.ForcedFrames == 0, "a lone import under W is never forced: " + shape);
		foreach (var f in frames)
		{
			double inWindow = frames.Where(x => x.At > f.At - ImportActivatePacer.WindowMs && x.At <= f.At).Sum(x => x.Ms);
			Check(inWindow <= 70 + 5, "1 s window ending at " + f.At + " ms holds " + inWindow + " ms: " + shape);
		}
		Check(forty.ActivateSpanMs == frames.Last().At - frames.First().At, "activateSpanMs = first to last activation frame");
		Check(forty.PacedFramesTotal > 100, "the waits were counted (" + forty.PacedFramesTotal + " frames): " + shape);
	}
}
