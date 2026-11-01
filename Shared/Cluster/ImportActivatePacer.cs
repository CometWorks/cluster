using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClusterPlugin;

/// <summary>
/// T-0392, <c>import.activateMsPerSecond=N</c>: a node spends at most N ms of sliced import
/// activation per rolling second, across ALL imports in flight on it. The per-frame budget
/// (<c>import.activateBudgetMs</c>) cut frames >= 100 ms by 35 % in the live A/B but not the
/// per-second server sim: a 40+ entity import still spent ~300 ms of activation inside one second.
/// Pure bookkeeping with the clock passed in, so the self-test (section <c>activate-pacing</c>)
/// drives it without a game; the live import uses <see cref="Node"/> and <see cref="NowMs"/>.
///
/// Ledger: (time, ms) per activation frame (slices and the final frame), the frame's MEASURED
/// game-thread ms, never an estimate; an entry stops counting 1000 ms after it was spent. Every
/// sliced import records, whatever its N, so a PUT that turns pacing on starts from the truth.
///
/// Before each activation frame an import asks <see cref="Decide"/>:
///  * N = 0: go, sized by the per-frame budget alone - exactly the decisions without this knob.
///  * an older import (registered earlier) is waiting for the allowance: wait (FIFO, oldest first).
///  * remaining = N - spentLast1s &gt; 0: go, the slice sized to min(per-frame budget, remaining)
///    through the same Add-ms estimate (<see cref="ImportActivateSlices.Take"/>; at least one group,
///    so a group larger than the allowance is a slice of its own, flagged overBudgetGroup).
///    An empty window is remaining = N, so a lone import never waits on an empty window.
///  * the import's own wait plus the waits of imports that finished on this node in the last 10 s exceed
///    <see cref="ForceAfterWaitMs"/>: go regardless of the ledger (forced), sized by the per-frame budget
///    alone, and so on while that holds - pacing holds this node's imports back by at most W (plus one
///    frame each) within any 10 s (see DefaultForceAfterWaitMs for the deadlines and the margin).
///  * otherwise: wait one frame. A waiting import stays staged/frozen exactly as between two slices.
/// An import whose transfer was failed or discarded goes at once, so its frame throws and cleans up.
/// Thread-safe (one lock): the handler thread registers and unregisters, the game thread decides
/// and spends.
/// </summary>
internal sealed class ImportActivatePacer
{
	internal const double WindowMs = 1000;

	/// <summary>
	/// W: pacing wait (ms) this node may impose within any <see cref="DeadlineHorizonMs"/> before an
	/// import's frames go regardless of the ledger; see <see cref="DefaultForceAfterWaitMs"/>.
	/// </summary>
	internal readonly double ForceAfterWaitMs;

	/// <summary>
	/// T-0392: W = 2000 ms, counted per NODE over the last 10 s (an import's own wait plus the waits of
	/// imports that finished here within <see cref="DeadlineHorizonMs"/>), so back-to-back imports share it.
	///
	/// Deadlines an in-flight import is subject to (census on r119, 2026-10-06):
	///  * The gateway's POST /cluster/import: DirectGateway._http.Timeout = 10 s, hard-coded
	///    (ClusterRegistry/DirectGateway.cs, ImportWithFallback / UnfencedHandover), no retry. It covers the
	///    whole request: grant check, parse, staging, every slice, compare, import_finish. THE binding one.
	///  * The node's control server answers on ONE listener thread (ListenLoop), so a second import, a
	///    capture for a handover where this node is the source, adopt/discard/thaw and every other control
	///    call queue behind a running import, and their callers' 10 s clocks (same _http) run meanwhile.
	///    That is why W is per node over 10 s, not per import: two queued imports cannot add 2 W.
	///  * Invoke's 30 s game-thread wait is per frame (each paced attempt is its own short Invoke), the
	///    staged-worker wait (120 s) ends before the first slice, the transfer grant (1 min) is consumed
	///    at request start; the source freeze has no node-side timeout and the gateway no overall handover
	///    deadline (HandoverTransaction.md: proposed only); the adopt/traffic/tee/commit waits (10-30 s)
	///    start after the import returns; the client-to-node backlog during the switch is a 4 MB byte cap
	///    (age cap waived while switching), not a timer.
	/// Margin: today's largest imports take 1.6-2.7 s end to end (25 handover_import lines, 3 polaris node logs
	/// 2026-10-06: 90-120 entities; p50 0.17 s), two of them back to back on one node 5.4 s; pacing adds at most W (+1 frame per
	/// import) inside any 10 s, so 7.4 s worst case against 10 s, a single import 4.7 s. 2000 ms still
	/// lets a lone 40-entity import at 5 ms/entity under N = 70 spread over three one-second windows (two
	/// waits of ~1 s each, self-test activate-pacing).
	/// </summary>
	internal const double DefaultForceAfterWaitMs = 2000;

	/// <summary>The gateway's import request timeout (DirectGateway._http.Timeout): waits older than this cannot
	/// be inside any request still pending, so they stop counting against W.</summary>
	internal const double DeadlineHorizonMs = 10000;

	internal static readonly ImportActivatePacer Node = new ImportActivatePacer(DefaultForceAfterWaitMs);
	private static readonly Stopwatch s_clock = Stopwatch.StartNew();
	/// <summary>Monotonic ms for the live ledger.</summary>
	internal static double NowMs => s_clock.Elapsed.TotalMilliseconds;

	internal sealed class Ticket
	{
		internal readonly long Seq;
		internal bool Waiting;
		internal double WaitStartMs;
		/// <summary>Closed wait episodes, ms.</summary>
		internal double WaitedMs;
		/// <summary>Frames this import waited since its previous activation frame.</summary>
		internal int PacedFrames;
		internal int PacedFramesTotal;
		internal int ForcedFrames;
		internal double FirstFrameMs = -1;
		internal double LastFrameMs = -1;
		/// <summary>From the start of the first activation frame to the end of the last one.</summary>
		internal double ActivateSpanMs => FirstFrameMs < 0 || LastFrameMs < FirstFrameMs ? 0 : LastFrameMs - FirstFrameMs;
		internal Ticket(long seq) => Seq = seq;
	}

	/// <summary>One frame's answer. <see cref="BudgetMs"/> is what Take gets (0 = no cost budget).</summary>
	internal readonly record struct Decision(bool Go, bool Forced, double BudgetMs, double SpentMs, int PacedFrames, int PerSecondMs);

	private readonly object m_lock = new object();
	private readonly Queue<(double At, double Ms)> m_spent = new Queue<(double At, double Ms)>();
	private double m_spentMs;
	private readonly List<Ticket> m_tickets = new List<Ticket>();
	/// <summary>(unregistered at, ms waited) of finished imports that were held back, for the node-level W.</summary>
	private readonly Queue<(double At, double Ms)> m_finishedWaits = new Queue<(double At, double Ms)>();
	private long m_nextSeq;

	internal ImportActivatePacer(double forceAfterWaitMs) => ForceAfterWaitMs = forceAfterWaitMs;

	/// <summary>An import enters the queue; registration order is the FIFO order.</summary>
	internal Ticket Register()
	{
		lock (m_lock)
		{
			Ticket ticket = new Ticket(++m_nextSeq);
			m_tickets.Add(ticket);
			return ticket;
		}
	}

	/// <summary>On every way out of the import: a ticket left waiting would hold younger imports back.</summary>
	internal void Unregister(Ticket ticket, double nowMs)
	{
		if (ticket == null) return;
		lock (m_lock)
		{
			if (!m_tickets.Remove(ticket)) return;
			double waited = ticket.WaitedMs + (ticket.Waiting ? nowMs - ticket.WaitStartMs : 0);
			if (waited > 0) m_finishedWaits.Enqueue((nowMs, waited));
		}
	}

	/// <summary>Activation ms spent in the window ending at <paramref name="nowMs"/>.</summary>
	internal double SpentMs(double nowMs)
	{
		lock (m_lock)
		{
			Expire(nowMs);
			return m_spentMs;
		}
	}

	/// <summary>
	/// Whether <paramref name="ticket"/> runs an activation frame now; see the class comment.
	/// <paramref name="frameBudgetMs"/> is import.activateBudgetMs (0 = none), <paramref name="urgent"/>
	/// an import that must reach its frame to fail (abandoned, discarded).
	/// </summary>
	internal Decision Decide(Ticket ticket, int perSecondMs, int frameBudgetMs, bool urgent, double nowMs)
	{
		lock (m_lock)
		{
			Expire(nowMs);
			double spent = m_spentMs;
			if (perSecondMs <= 0 || ticket == null || urgent) return Admit(ticket, nowMs, false, frameBudgetMs, spent, perSecondMs);
			double remaining = perSecondMs - spent;
			bool olderWaiting = m_tickets.Any(x => x.Seq < ticket.Seq && x.Waiting);
			if (remaining > 0 && !olderWaiting)
				return Admit(ticket, nowMs, false, frameBudgetMs > 0 ? Math.Min(frameBudgetMs, remaining) : remaining, spent, perSecondMs);
			while (m_finishedWaits.Count > 0 && m_finishedWaits.Peek().At <= nowMs - DeadlineHorizonMs) m_finishedWaits.Dequeue();
			double waited = ticket.WaitedMs + (ticket.Waiting ? nowMs - ticket.WaitStartMs : 0) + m_finishedWaits.Sum(x => x.Ms);
			if (waited > ForceAfterWaitMs) return Admit(ticket, nowMs, true, frameBudgetMs, spent, perSecondMs);
			if (!ticket.Waiting)
			{
				ticket.Waiting = true;
				ticket.WaitStartMs = nowMs;
			}
			ticket.PacedFrames++;
			ticket.PacedFramesTotal++;
			return new Decision(false, false, 0, spent, ticket.PacedFrames, perSecondMs);
		}
	}

	private static Decision Admit(Ticket ticket, double nowMs, bool forced, double budgetMs, double spent, int perSecondMs)
	{
		int paced = 0;
		if (ticket != null)
		{
			if (ticket.Waiting)
			{
				ticket.WaitedMs += nowMs - ticket.WaitStartMs;
				ticket.Waiting = false;
			}
			paced = ticket.PacedFrames;
			ticket.PacedFrames = 0;
			if (forced) ticket.ForcedFrames++;
			if (ticket.FirstFrameMs < 0) ticket.FirstFrameMs = nowMs;
		}
		return new Decision(true, forced, budgetMs, spent, paced, perSecondMs);
	}

	/// <summary>An activation frame that ran: its measured game-thread ms enters the ledger at <paramref name="nowMs"/>.</summary>
	internal void Spend(Ticket ticket, double ms, double nowMs)
	{
		lock (m_lock)
		{
			Expire(nowMs);
			if (ms > 0)
			{
				m_spent.Enqueue((nowMs, ms));
				m_spentMs += ms;
			}
			if (ticket != null) ticket.LastFrameMs = nowMs;
		}
	}

	private void Expire(double nowMs)
	{
		while (m_spent.Count > 0 && m_spent.Peek().At <= nowMs - WindowMs) m_spentMs -= m_spent.Dequeue().Ms;
		if (m_spent.Count == 0) m_spentMs = 0; // no drift from the subtractions
	}
}
