using System;
using System.Collections.Generic;
using System.Linq;

namespace ClusterPlugin;

internal sealed class PartitionMembership
{
	/// <summary><paramref name="Fast"/>: T-0292, FastMotionTracker says this member moves faster than
	/// the partition machinery can follow. Its whole atomic group is then fast.</summary>
	internal readonly record struct Member(long Id, double X, double Y, double Z, double Radius, double Speed, long AtomicGroup = 0,
		bool Fast = false);
	/// <summary>
	/// <see cref="Pending"/> pins members adopted from a save whose entities have not materialized
	/// in the world census yet. A large grid loaded from a partition save spends over a minute in
	/// the parallel entity-init pipeline (11k blocks took 64 s on 2026-08-16), during which the
	/// census does not report it; without the pin its layout dissolved, and when the entity finally
	/// surfaced the unassigned-member pass minted a brand-new REGISTERED partition around it -
	/// laundering a stale seed-save copy into legitimate content and wedging the cluster on a
	/// duplicate incarnation (partitions 330/333, "Pirate Base Large mk.2"). A pinned member counts
	/// as assigned, and the layout holding it takes no part in splits, merges or nearest-adoption
	/// until the pin resolves. <see cref="PinnedUntil"/> bounds the wait: an entity that never
	/// materializes (init crashed, content superseded) unpins there and leaves normally.
	/// </summary>
	internal sealed record Layout(ulong Id, long[] Members, double CreatedAt)
	{
		public long[] Pending { get; init; } = Array.Empty<long>();
		public double PinnedUntil { get; init; }
	}
	/// <summary><paramref name="Why"/>: the trigger and the values compared, for the commit log (T-0073).</summary>
	internal sealed record Change(bool Split, ulong[] Inputs, Layout[] Outputs, string Why = "")
	{
		/// <summary>The input layouts exactly as they stood before the change, so <see cref="Revert"/> can undo it.</summary>
		public Layout[] InputLayouts { get; init; } = Array.Empty<Layout>();
		/// <summary>
		/// T-0339 (design C): the members a TRANSFER moves. A transfer is a split with two inputs that keeps both ids:
		/// Inputs = [source, target], Outputs = [source without <see cref="Moved"/>, target with it], in that order.
		/// Empty for every other change.
		/// </summary>
		public long[] Moved { get; init; } = Array.Empty<long>();
		public bool Transfer => Moved.Length != 0;
	}

	/// <summary>How long an adopted-but-absent member stays pinned before it is given up on.</summary>
	internal const double PinSeconds = 180.0;


	private double m_distance;
	private double m_margin;
	// T-0354: null = no ids at hand right now (the node's reserve of registry ids is short). The change that asked
	// is not made in this pass - nothing is rewritten before the ids are in hand - and is judged again on the next.
	private readonly Func<int, ulong[]> m_allocate;
	// Partitioning.md 3.4: Frozen / HandoverInProgress / MergePending partitions are skipped by the
	// membership pass and revisited next cycle. Splitting or merging a partition whose entities are
	// mid-transfer rewrites the very set the transaction agreed on.
	private readonly Func<ulong, bool> m_locked;
	private List<Layout> m_layouts = new List<Layout>();

	public PartitionMembership(double distance, double margin, Func<int, ulong[]> allocate, Func<ulong, bool> locked = null)
	{
		m_distance = distance;
		m_margin = margin;
		m_allocate = allocate;
		m_locked = locked ?? (_ => false);
	}

	public IReadOnlyList<Layout> Layouts => m_layouts;

	/// <summary>
	/// T-0350: while set, an unassigned member that no layout adopts is left unassigned instead of getting a new
	/// layout - a new layout needs a partition id from the registry, and the registry is unreachable. The member is
	/// a candidate again on every later pass. Set together with a lock on every layout, so no split asks for ids.
	/// </summary>
	public bool DeferNewLayouts;

	/// <summary>
	/// T-0353: adoption under a topology hold (a Closed node's HoldTopology, the registry hold). A hold locks every
	/// layout against splits and merges, and it used to lock them against adoption too: a pilot who left the seat on
	/// a held node was then in NO layout, so in no save and no drain - the ship was drained as clientless and the body
	/// was lost (client ...037 on node-98, 2026-10-01: 12 minutes connected without a character). Adoption needs no
	/// registry call. When set, this predicate replaces the lock for the adoption pass only (frozen, unowned and
	/// held-for-one-pass layouts stay locked) and the nearest such layout adopts up to the split distance instead of the
	/// merge distance.
	/// </summary>
	public Func<ulong, bool> HoldAdoptionLocked;
	/// <summary>
	/// T-0354b: the lock of the adoption step without a hold (null = the lock splits and merges use). PartitionManager
	/// sets it from <see cref="RegistryApplyQueue.AdoptionLock"/>: a layout whose only unanswered registry request is the
	/// claim of its own id adopts, as it did when the claim was answered inside the pass that made the layout.
	/// </summary>
	public Func<ulong, bool> AdoptionLocked;
	/// <summary>T-0354: the layouts the last pass made around unassigned members (not split outputs) - the caller claims their ids.</summary>
	public List<ulong> Created { get; } = new();
	public void SetDistance(double distance) => m_distance = distance;

	/// <summary>
	/// T-0319: the default split distance, in sync distances. r34 (2026-09-30) churned 582 topology
	/// commits per 10 min cluster-wide at D + 2 x 1000 = 5000 m because pilots shuttle between parked
	/// groups 6-10 km apart: every trip merged on arrival and split on departure. A replay of the logged
	/// trajectories through this class (tools/t0319/replay) put the commit rate at -19% for 6000 m,
	/// -38% for 8000 m, -51% for 10000 m and -62% for 12000 m, the largest partition growing from 72 to
	/// 105 members. Groups whose cores are within the split distance stay one partition once a traveller
	/// has merged them, so the traffic between them commits nothing. Scales with the sync distance in use.
	/// </summary>
	internal const double AutoSplitSyncDistances = 4.0;

	/// <summary>The split distance "auto" resolves to at sync distance <paramref name="distance"/>.</summary>
	internal static double AutoSplitDistance(double distance) => AutoSplitSyncDistances * distance;

	/// <summary>
	/// T-0319: judge a merge on the cores. On by default; the node sets it from partition.coreMerge per pass.
	/// See <see cref="CoresHold"/>.
	/// </summary>
	public bool CoreMerge { get; set; } = true;

	/// <summary>
	/// T-0339 (design C): move a piece that a split would cut off straight into the layout it is already within merge
	/// distance of, in ONE change (<see cref="Change.Transfer"/>), instead of giving it a new id that the merge below
	/// retires in the same pass. r77 2026-10-03: 62% of the new ids died within 60 s, most of them that split followed
	/// in the same second by that merge (median life 0 s). The node sets it per pass: the registry must say it applies
	/// transfers (heartbeat TopologyTransfer) and partition.transfer must be on. Off, the pass is exactly what it was.
	/// </summary>
	/// <remarks>
	/// The piece goes where the merge pass would have put it, judged by the merge pass's own tests on the piece as it
	/// would stand after the split (it carries the parent's age, A1): atomically linked, or both ages past the 10 s gate,
	/// the nearest member pair under D and, with <see cref="CoreMerge"/>, the cores holding. A target is a layout this
	/// pass does not split (and not locked, pinned or fast); among several the smallest gap wins, ties to the smaller id.
	/// The largest group still keeps the source's id (B), and the groups no layout takes are split off as before.
	/// The merge pass may then merge the grown target like any layout: every change is its own registry operation on
	/// the generations the previous one left, and <see cref="Revert"/> undoes them last first.
	/// </remarks>
	public bool TransferEnabled;

	/// <summary>
	/// T-0339 (design C2): widen the transfer reach from D to a band. With <see cref="TransferEnabled"/> and this set, a
	/// piece a split cuts off may also go to a standing layout whose nearest gap g is D &lt;= g &lt; the band limit
	/// <see cref="BandLimit"/> = min(split distance, D + piece speed x <see cref="TransferBandSeconds"/>, the piece's gap
	/// to the rest of its source - <see cref="BandReturnSyncDistances"/> x D). r101 2026-10-04: 118 of 251 short-lived ids
	/// (median life 13 s, merge gap at death ~2988 m) were pieces that left P at the split distance while already 3-12 km
	/// from a standing Q and crossed D seconds later. The node sets it per pass from partition.transferBand (default
	/// true since 2026-10-05; false: the pass is design C exactly) and partition.transferBandSeconds.
	/// </summary>
	/// <remarks>
	/// Stable by construction: the limit never exceeds the split distance, and the split links members within it
	/// (Components, gap &lt;= distance), so the grown target does not split the piece off on the next pass; it does once
	/// the piece is past the split distance from every member of the target, as any member. The speed is the scalar
	/// Member.Speed (no direction), so a receding piece is taken too and leaves the target later - the birth it had
	/// anyway, one transfer late. The return margin stops a ping-pong between two layouts whose split edges touch: a
	/// piece hovering there is past the split distance of each in turn, and without the margin it was moved back and
	/// forth every pass; with it a band move needs the target nearer than the source by a third of D, so a move back
	/// needs the piece to travel that far. The 10 s age gate, the locks and the nearest-target choice are design C's;
	/// the core test takes its contact at the band limit for a band candidate (see <see cref="TransferTarget"/>).
	/// </remarks>
	public bool TransferBand;
	/// <summary>T-0339 (design C2): the look-ahead of the transfer band, in seconds (partition.transferBandSeconds).</summary>
	public double TransferBandSeconds = 60;
	/// <summary>T-0339 (design C2): a band move needs the target nearer than the rest of the source by this many sync distances.</summary>
	internal const double BandReturnSyncDistances = 1.0 / 3;

	/// <summary>
	/// T-0339 (design C2): how far a piece of speed <paramref name="speed"/>, <paramref name="sourceGap"/> from the rest
	/// of its source layout, may be from a transfer target: D without the band (design C), else the band limit, never
	/// below D and never above the split distance.
	/// </summary>
	internal double BandLimit(double speed, double sourceGap)
	{
		if (!TransferBand) return m_distance;
		double limit = Math.Min(m_distance + 2 * m_margin, m_distance + Math.Max(0, speed) * TransferBandSeconds);
		limit = Math.Min(limit, sourceGap - BandReturnSyncDistances * m_distance);
		return double.IsNaN(limit) ? m_distance : Math.Max(m_distance, limit);
	}

	/// <summary>
	/// T-0339 (merge hold-off): for this many seconds after a split, a merge of two of its outputs with each other is
	/// skipped unless their nearest members are under D/2 apart; the pair stays two layouts and is judged again on the
	/// next pass. r117 2026-10-05: 36 % of the topology-born ids that died within 60 s were a split piece re-merged with
	/// the standing layout it had just split from (median life 25-33 s). The node sets it per pass from
	/// partition.mergeHoldoffSeconds; 0 (the default) records nothing and the pass is exactly what it was.
	/// </summary>
	/// <remarks>
	/// Only the outputs of a distance split are siblings (not a fast split, not a transfer), keyed by id and stamped
	/// with the pass clock (<c>now</c>), so a replay with the same clock makes the same decisions. Only the plain merge
	/// is held: an atomic link (attached grids), touching members (gap 0 is under D/2), the fast-group join, adoption,
	/// splits and transfers never are, and a merge of either sibling with any other layout is unaffected. A sibling pair
	/// is forgotten once the window is over (as the knob stands at that pass), when either id leaves this membership,
	/// or when the knob is 0.
	/// </remarks>
	public double MergeHoldoffSeconds;
	/// <summary>T-0339 (merge hold-off): sibling pairs whose merge the last pass held off.</summary>
	public int HeldMerges { get; private set; }
	/// <summary>T-0339 (merge hold-off): sibling pairs whose merge was held off at least once, since this membership was made.</summary>
	public long HeldMergePairs { get; private set; }
	/// <summary>T-0339 (merge hold-off): the pairs the last pass held off for the FIRST time (lower id, higher id, nearest gap in m,
	/// seconds since the split) - the caller logs them, one line per split at most.</summary>
	public List<(ulong A, ulong B, double Gap, double Age)> HoldoffsStarted { get; } = new();
	// T-0339 (merge hold-off): (lower id, higher id) -> the pass clock of the split that made them siblings, and whether
	// a merge of the pair has been held off yet.
	private readonly Dictionary<(ulong, ulong), (double At, bool Held)> m_siblings = new();
	private static (ulong, ulong) PairKey(ulong a, ulong b) => a < b ? (a, b) : (b, a);

	/// <summary>
	/// T-0339 (split hysteresis): hold a due split for up to this many seconds while a piece that would get a NEW id is
	/// closing on a standing layout, so it can be transferred there (design C) instead of minting a short-lived id. w33
	/// 2026-10-06 12:50-13:12: the split-born ids that died young merged into a THIRD standing layout 8-46 s after the
	/// split, never into their sibling. The node sets it per pass from partition.splitHysteresisSeconds; 0 (the default)
	/// records nothing and the pass is exactly what it was. Needs <see cref="TransferEnabled"/> (without transfers a held
	/// piece could only become a fresh id later, then merge: nothing saved).
	/// </summary>
	/// <remarks>
	/// Closing (<see cref="Closing"/>): to a layout the pass could transfer into (not split this pass, not locked, pinned
	/// or fast), the piece's nearest member gap now g is >= D, the same gap over the same members since the reference census fell at
	/// more than <see cref="ClosingEpsilonMps"/>, and at that rate it reaches D before the hold runs out:
	/// g - D &lt;= rate x (N - seconds held so far). The rate is read against a reference census at least
	/// <see cref="ClosingBaselineSeconds"/> old in GAME time (<see cref="Evaluate"/>'s gameNow; no history to keep per piece,
	/// so a piece is judged on the very pass its split falls due: one that is not closing splits on that pass, exactly as
	/// today). A member that moved faster than <see cref="MaxMemberSpeedMps"/> since the reference (a jump) is left out of
	/// the rate, and a rate above <see cref="MaxClosingMps"/> is no rate at all.
	/// Hold: the closing pieces stay with the group that keeps the layout's id; every other fresh piece is split off and
	/// every planned transfer is made as today. The run is per LAYOUT: it starts on the first held pass and the layout is
	/// split as today on the first pass at or past N seconds after it ("expired"), or on the first pass no fresh piece is
	/// closing ("not-closing"; T-0339j: a piece of the run already inside D of the run's target counts as closing - it waits
	/// there for the transfer the target's core test refuses until the core is near); once a run has ended that way the
	/// layout is not held again until it is no longer due. A
	/// transfer of a held piece (any of its members) ends the run as "transferred"; another closing piece then starts a
	/// run of its own. A run whose layout is no longer due (or gone) without either ends "cleared". A layout skipped
	/// for a lock or a pin keeps its run (its clock keeps going). A held layout is not a transfer target for OTHER
	/// layouts' pieces while it is due (as any layout the pass splits); merges with it are judged as with any layout, so a
	/// layout that comes within D of its kept group is refused by the core test while the hold lasts whenever the held
	/// piece (a core of the held layout there) is beyond the split distance of it, and merges once the hold ends. Splits, transfers and
	/// merges between other layouts are untouched. A hold is not a deferral (<see cref="DeferredSplits"/> never lists it).
	/// </remarks>
	public double SplitHysteresisSeconds;
	/// <summary>
	/// #1300 (octahedron-1191, 2026-10-21): a layout with more members than this is split at a shorter distance than the
	/// split distance. local-node-1 ran at sim 0.40 next to four nodes at 1.00 because one partition held 578 members (parked
	/// grids along the lanes, 26 pilots, 1.1 M of the node's 1.44 M sim-us/s) over a 119 km bounding radius: its longest
	/// member-tree edge was 9.7 km, under the 12 km split distance (T-0319), so it never split. The balancer can only move a
	/// partition whole, and a 335 s estimated lag is never worth moving. An oversized layout is cut at the split distance
	/// less <see cref="OversizeStepMetres"/>, again and again, until every piece holds at most this many members or the cut
	/// reaches <see cref="OversizeSplitFloor"/>; the first cut that fits wins, so it sheds as little as it can. A compact
	/// base with no wide gap stays whole at any cut. The node sets it per pass from partition.oversizeMembers; 0 (the
	/// default here) = off, the pass is exactly what it was.
	/// </summary>
	public int OversizeMembers;
	/// <summary>#1300: the shortest cut of an oversized layout, in metres; the node passes D + partition.margin, the
	/// registry's co-location envelope (#1305: it was D + 2 x margin, the split distance before T-0319, and the 266-member
	/// lane partition 3187 of octahedron-1191, widest member gap 4.9 km, stayed whole under it). At or under the merge
	/// distance D the oversize split is off: a split under D oscillates.</summary>
	public double OversizeSplitFloor;
	/// <summary>#1300: how far each step of an oversized layout's cut comes down.</summary>
	internal const double OversizeStepMetres = 1000;
	/// <summary>T-0339 (split hysteresis): a gap must fall faster than this between two passes to count as closing.</summary>
	internal const double ClosingEpsilonMps = 1.0;
	/// <summary>
	/// T-0339i (split hysteresis): the closing rate is read against a census at least this many GAME seconds old. r119 read it
	/// against the previous CALL's census on the wall clock: the node evaluates several times inside one game frame (maintenance
	/// triggers, control server, diag snapshot, offline persistence), positions advance once per frame, so the second call of
	/// a frame saw the same positions - rate 0, every run ended "not-closing" 0-1 s after it started (2026-10-07 13:11-13:45,
	/// 10 of 10) - and a call a few ms after a frame boundary divided one frame's motion by those ms (3084 and 7743 m/s).
	/// </summary>
	internal const double ClosingBaselineSeconds = 1.0;
	/// <summary>
	/// T-0339i (split hysteresis): a member that moved faster than this since the reference census (a jump drive, a re-placed
	/// copy) is left out of the closing rate. 1000 m/s is ~2.5x the fastest cruise the cluster flies (T-0294 fast cruise,
	/// ~409 m/s) and 10x the vanilla cap; no ship gets there by flying.
	/// </summary>
	internal const double MaxMemberSpeedMps = 1000;
	/// <summary>T-0339i (split hysteresis): two members closing at <see cref="MaxMemberSpeedMps"/> each; a faster rate (a
	/// bounding sphere that grew or shrank a lot in a second) is not a closing rate and holds nothing.</summary>
	internal const double MaxClosingMps = 2 * MaxMemberSpeedMps;
	/// <summary>T-0339 (split hysteresis): layouts the last pass held whole (or in part) under the hysteresis.</summary>
	public int HysteresisHeld { get; private set; }
	/// <summary>T-0339 (split hysteresis): hold runs started, since this membership was made.</summary>
	public long HysteresisHeldTotal { get; private set; }
	/// <summary>T-0339 (split hysteresis): runs that ended with the held piece transferred (no new id).</summary>
	public long HysteresisTransferred { get; private set; }
	/// <summary>T-0339 (split hysteresis): runs that ended at N seconds with the split made as today.</summary>
	public long HysteresisExpired { get; private set; }
	/// <summary>T-0339 (split hysteresis): runs that ended otherwise - the piece stopped closing, or the layout is no longer due.</summary>
	public long HysteresisReleased { get; private set; }
	/// <summary>T-0339 (split hysteresis): the run starts and ends of the last pass, for the log (one line each).</summary>
	public List<(ulong Layout, int Piece, ulong Target, double Gap, double Rate, string Outcome, double HeldS)> HysteresisEvents { get; } = new();
	/// <summary>
	/// T-0339 (class B diag, CH 2026-10-08 10:21; log-only): every id a split MINTS this pass, with the nearest other
	/// layout on this node, the gap to it, the transfer reach the piece had, and the first transfer gate that layout
	/// failed in <see cref="TransferTarget"/>'s order: transfer-off, not-a-target, locked, pending, fast, age, reach, core
	/// - or "qualified" (it passed every gate yet the piece was minted), "no-other-layout". Changes nothing.
	/// </summary>
	public List<(ulong Piece, ulong Source, int Members, ulong Nearest, double Gap, double Reach, string Gate)> MintedPieces { get; } = new();
	// T-0339 (split hysteresis): layout id -> its run: when it started, the members of the pieces held on its last held
	// pass (a transfer is recognized by overlap), the last target/gap/rate (for the log), and whether it has ended.
	private sealed class HysteresisRun
	{
		public double Since;
		public long[] Members = Array.Empty<long>();
		public ulong Target;
		public double Gap, Rate;
		public bool Ended;
	}
	private readonly Dictionary<ulong, HysteresisRun> m_hysteresis = new();
	// T-0339i (split hysteresis): the reference census the closing rate is read against and its game time, and the candidate
	// that replaces it once the candidate is ClosingBaselineSeconds old (so the reference is 1-2 s old with frequent passes,
	// never one taken inside the same game frame). Kept by reference (byId is built fresh every pass and never written after).
	private Dictionary<long, Member> m_reference, m_candidate;
	private double m_referenceAt, m_candidateAt;

	/// <summary>T-0339i: advance the closing-rate reference to game time <paramref name="gameNow"/>; see <see cref="ClosingBaselineSeconds"/>.</summary>
	private void AdvanceReference(Dictionary<long, Member> census, double gameNow)
	{
		// Game time that went back (a new session) invalidates both.
		if (m_candidate != null && gameNow < m_candidateAt || m_reference != null && gameNow < m_referenceAt)
			(m_reference, m_candidate) = (null, null);
		if (m_candidate != null && gameNow - m_candidateAt >= ClosingBaselineSeconds)
			(m_reference, m_referenceAt, m_candidate) = (m_candidate, m_candidateAt, null);
		if (m_candidate == null) (m_candidate, m_candidateAt) = (census, gameNow);
	}

	/// <summary>
	/// T-0339 (split deferral): the layouts whose members the last pass found in two or more split groups (at the split
	/// distance) but did not split or transfer: <c>locked</c> (m_locked: frozen member, topology or registry hold, not
	/// owned, held for the pass, registry request unanswered - the caller names which), <c>pending</c> (a pinned member
	/// has not materialized) or <c>no-ids</c> (the allocator had no fresh id for the split, so the whole split and its
	/// transfers wait). Since: seconds since the first pass in a row that deferred the layout. Read-only diagnostics: the
	/// decisions of the pass are exactly what they were without it. A layout holding a fast group is never listed (the
	/// fast pass, not the distance split, separates it).
	/// </summary>
	public List<(ulong Layout, string Reason, int Groups, double Since)> DeferredSplits { get; } = new();
	// T-0339 (split deferral): layout id -> the pass clock of the first pass in a row that deferred its split.
	private readonly Dictionary<ulong, double> m_deferredSince = new();
	/// <summary>T-0339 (split deferral): a deferral persisting this long is logged again.</summary>
	internal const double DeferralLogSeconds = 60;

	/// <summary>
	/// T-0339 (split deferral): the deferrals of one pass the node logs - a layout's first, one whose reason changed, and
	/// one last logged <see cref="DeferralLogSeconds"/> or more ago - recorded in <paramref name="logged"/>; a layout the
	/// pass did not defer is forgotten, so its next deferral is logged at once.
	/// </summary>
	internal static List<(ulong Layout, string Reason, int Groups, double Since)> DeferralsToLog(
		IEnumerable<(ulong Layout, string Reason, int Groups, double Since)> deferred, Dictionary<ulong, (string Reason, double At)> logged, double now)
	{
		List<(ulong, string, int, double)> due = new List<(ulong, string, int, double)>();
		HashSet<ulong> seen = new HashSet<ulong>();
		foreach ((ulong layout, string reason, int groups, double since) in deferred)
		{
			if (!seen.Add(layout)) continue;
			if (logged.TryGetValue(layout, out (string Reason, double At) last) && last.Reason == reason && now - last.At < DeferralLogSeconds) continue;
			logged[layout] = (reason, now);
			due.Add((layout, reason, groups, since));
		}
		foreach (ulong gone in logged.Keys.Where(x => !seen.Contains(x)).ToArray()) logged.Remove(gone);
		return due;
	}

	/// <summary>Merges the last pass refused because only a travelling contact piece joined the two layouts.</summary>
	public int RefusedMerges { get; private set; }
	/// <summary>T-0353: what the last pass adopted while <see cref="HoldAdoptionLocked"/> was set (entity, layout, gap in m) - the caller logs it.</summary>
	public List<(long Entity, ulong Layout, double Gap)> HoldAdoptions { get; } = new();
	/// <summary>
	/// T-0126: Margin is a controlled variable, pushed in per pass exactly as the distance is.
	/// It sets ONLY the upper edge of the deadband - split needs every cross-group gap to reach
	/// D + 2*margin, while merge stays at D - so a change here widens or narrows the hysteresis
	/// band without moving the merge rule. T-0319: the node no longer passes partition.margin here; it
	/// passes (split distance - D) / 2 from partition.splitDistance (PartitionManager.SplitDistance), and
	/// partition.margin is left to the registry's co-location envelope.
	/// </summary>
	public void SetMargin(double margin) => m_margin = margin;
	public void Adopt(ulong id, IEnumerable<long> members, double now, bool mature,
		IEnumerable<long> pending = null)
	{
		long[] pinned = (pending ?? Array.Empty<long>()).Distinct().Order().ToArray();
		long[] adopted = members.Concat(pinned).Distinct().Order().ToArray();
		m_layouts = m_layouts.Select(x => x with { Members = x.Members.Except(adopted).ToArray(),
			Pending = x.Pending.Except(adopted).ToArray() }).Where(x => x.Members.Length != 0).ToList();
		if (mature) m_layouts = m_layouts.Select(x => x with { CreatedAt = double.NegativeInfinity }).ToList();
		m_layouts.RemoveAll(x => x.Id == id);
		m_layouts.Add(new Layout(id, adopted, mature ? double.NegativeInfinity : now)
			{ Pending = pinned, PinnedUntil = pinned.Length == 0 ? 0 : now + PinSeconds });
	}
	public void Remove(ulong id) => m_layouts.RemoveAll(x => x.Id == id);

	/// <summary>
	/// T-0339 (merge hold-off) test seam: stand this membership on <paramref name="layouts"/> exactly as given (the
	/// self-tests and Tools/MembershipEquivalence put a knob-0 shadow on the hold-off arm's layouts before a pass).
	/// </summary>
	internal void Restore(IEnumerable<Layout> layouts) => m_layouts = layouts.ToList();

	/// <summary>
	/// T-0339: the catalogue time of the next topology change of this node: the clock, but strictly after the
	/// previous change. The registry reads lineage by time (a merge input's cover is cut at the merge's time and
	/// must still see the split that made the input), and since a split child merges in the pass that made it,
	/// the two commits are a registry round trip apart - under a millisecond on a quiet host. At an equal time
	/// the merged-in piece has no cover until its new partition is saved.
	/// </summary>
	internal static long NextChangeTime(long clock, ref long previous) => previous = Math.Max(clock, previous + 1);

	/// <summary>
	/// T-0296 r3: undo <see cref="Adopt"/> for the members of <paramref name="id"/> named in
	/// <paramref name="previous"/> (member -> the layout that held it before the adoption, 0 for none). Each goes
	/// back to that layout when it still exists; otherwise it is left unassigned and the next pass places it like
	/// any stray. The other members of <paramref name="id"/> stay, and the layout is dropped when that empties it.
	/// </summary>
	public void Return(ulong id, IReadOnlyDictionary<long, ulong> previous)
	{
		Layout from = m_layouts.FirstOrDefault(x => x.Id == id);
		long[] leaving = from?.Members.Where(previous.ContainsKey).ToArray();
		if (leaving == null || leaving.Length == 0) return;
		m_layouts = m_layouts.Select(x => x.Id == id
				? x with { Members = x.Members.Except(leaving).ToArray(), Pending = x.Pending.Except(leaving).ToArray() }
				: x with { Members = x.Members.Concat(leaving.Where(member => previous[member] == x.Id)).Distinct().Order().ToArray() })
			.Where(x => x.Members.Length != 0).ToList();
	}

	/// <summary>
	/// Undo one change this membership already applied in memory: every input layout comes back exactly
	/// as it stood when the change was made, pulling its members out of wherever they are now, and the
	/// outputs that empties are dropped. Revert several changes LAST FIRST.
	/// </summary>
	/// <remarks>
	/// T-0150: the layouts are rewritten before the registry is asked. When the registry then refuses
	/// the change (a node it has already Closed), the split used to stay applied - node-2 held 31 grids
	/// of partition 8432 in two unregistered placeholder layouts, released 8432 as memberless, declared
	/// itself Empty and was killed with them (2026-09-14 03:48). Put back, the members leave with 8432.
	/// </remarks>
	public void Revert(Change change)
	{
		foreach (Layout before in change.InputLayouts)
		{
			m_layouts = m_layouts.Select(x => x with { Members = x.Members.Except(before.Members).ToArray(),
				Pending = x.Pending.Except(before.Members).ToArray() }).Where(x => x.Members.Length != 0).ToList();
			m_layouts.RemoveAll(x => x.Id == before.Id);
			m_layouts.Add(before);
		}
		m_layouts.Sort((a, b) => a.Id.CompareTo(b.Id));
	}

	/// <param name="gameNow">T-0339i: game time in seconds (the node: GameplayFrameCounter / 60), the clock the members'
	/// positions follow; only the split hysteresis's closing rate reads it. NaN (the default): <paramref name="now"/>.</param>
	public Change[] Evaluate(IEnumerable<Member> input, double now, double gameNow = double.NaN)
	{
		Member[] members = input.OrderBy(x => x.Id).ToArray();
		Dictionary<long, Member> byId = members.ToDictionary(x => x.Id);
		Created.Clear();
		HeldMerges = 0;
		HoldoffsStarted.Clear();
		DeferredSplits.Clear();
		HysteresisHeld = 0;
		HysteresisEvents.Clear();
		MintedPieces.Clear();
		// T-0339i (split hysteresis): the reference census for the closing rate, at least ClosingBaselineSeconds old in game
		// time; read only with the knob on.
		double gameClock = double.IsNaN(gameNow) ? now : gameNow;
		AdvanceReference(byId, gameClock);
		Dictionary<long, Member> previousCensus = m_reference;
		double previousAt = m_referenceAt;
		if (!(SplitHysteresisSeconds > 0) && m_hysteresis.Count != 0) m_hysteresis.Clear();
		if (m_layouts.Count == 0)
		{
			m_deferredSince.Clear();
			m_hysteresis.Clear();
			if (DeferNewLayouts) return Array.Empty<Change>();
			// Plan/EntityGapPlacement.md (Viktor 2026-08-24): grouping happens at the SYNC DISTANCE,
			// not sync + margin. The margin's anticipation job moved to the gateway's velocity-aware
			// placement; keeping it in membership grouping only inflated partitions (and their
			// bounding spheres) with content that never actually met the sync invariant.
			long[][] first = Components(members.Select(x => x.Id), byId, m_distance).ToArray();
			ulong[] firstIds = first.Length == 0 ? Array.Empty<ulong>() : m_allocate(first.Length);
			if (firstIds == null) return Array.Empty<Change>();
			m_layouts = first.Select((ids, i) => new Layout(firstIds[i], ids, now)).OrderBy(x => x.Id).ToList();
			Created.AddRange(firstIds);
			return Array.Empty<Change>();
		}

		List<Change> changes = new List<Change>();
		m_layouts = m_layouts.Select(x =>
		{
			// A pinned member stays in its layout while absent from the census (entity still in the
			// parallel init pipeline); it unpins the moment it is seen, or at the pin deadline.
			long[] pending = now < x.PinnedUntil
				? x.Pending.Where(id => !byId.ContainsKey(id)).ToArray() : Array.Empty<long>();
			return x with {
				Members = x.Members.Where(id => byId.ContainsKey(id) || pending.Contains(id))
					.OrderBy(y => y).ToArray(),
				Pending = pending };
		}).Where(x => x.Members.Length != 0).OrderBy(x => x.Id).ToList();
		HashSet<long> assigned = m_layouts.SelectMany(x => x.Members).ToHashSet();
		// T-0292: a fast member makes its whole atomic group fast (attached grids move together). A
		// layout holding a fast group is never an adoption target, never merged and never split by
		// distance; the fast pass below gives every fast group a layout of its own.
		HashSet<long> fastGroups = members.Where(x => x.Fast).Select(x => x.AtomicGroup).ToHashSet();
		bool IsFast(long id) => fastGroups.Count != 0 && byId.TryGetValue(id, out Member m) && fastGroups.Contains(m.AtomicGroup);
		bool HasFast(Layout layout) => fastGroups.Count != 0 && layout.Members.Any(IsFast);
		// Adopting the unassigned members used to measure the new member against EVERY member of EVERY
		// layout - O(unassigned x members), which degenerates to O(N^2) right after a world load or a mass
		// import, exactly when the node is busiest. The enclosing sphere of a layout bounds the gap to all of
		// its members from below, so a layout that cannot possibly be within the adoption distance is skipped
		// without touching its members. Layouts that survive the bound are measured exactly as before, so the
		// chosen layout and the tie-break (smallest gap, then smallest id) are unchanged.
		// Adoption is a one-member merge, so it uses the merge distance: the sync distance itself
		// (Plan/EntityGapPlacement.md). An entity between D and the old D+m now becomes its own
		// (co-located) partition instead of inflating a neighbour's membership and sphere.
		Func<ulong, bool> adoptionLocked = HoldAdoptionLocked ?? AdoptionLocked ?? m_locked;
		HoldAdoptions.Clear();
		// Under a hold up to the SPLIT distance: nearer than that the split pass would not separate the two anyway, so
		// the layout's sphere grows no further than an unsplit layout's can; a stray beyond it stays unassigned.
		double adoptDistance = HoldAdoptionLocked != null ? m_distance + 2 * m_margin : m_distance;
		// T-0101: the scan below is per (unassigned candidate x layout), and it used to recompute the
		// layout's enclosing sphere and re-resolve every member id through byId inside that product -
		// on a 1323-entity node the member lambda alone was 38% of Evaluate. Both are functions of the
		// layout's membership, so they are computed once and refreshed only for the one layout a
		// candidate is actually adopted into. Built lazily: a steady pass has no unassigned members and
		// must not pay for the cache at all.
		List<Member[]> resolved = null;
		List<(double X, double Y, double Z, double R)> spheres = null;
		foreach (long id in members.Select(x => x.Id).Where(x => !assigned.Contains(x)))
		{
			if (resolved == null)
			{
				resolved = new List<Member[]>(m_layouts.Count);
				spheres = new List<(double, double, double, double)>(m_layouts.Count);
				foreach (Layout layout in m_layouts)
				{
					Member[] set = Resolve(layout, byId);
					resolved.Add(set);
					spheres.Add(Enclosing(set));
				}
			}
			Member candidate = byId[id];
			int nearest = -1;
			double nearestGap = 0;
			// A fast candidate is never adopted: it becomes its own layout (the fast pass joins its
			// atomic group together). A layout holding a fast group adopts nothing.
			for (int i = 0; i < m_layouts.Count && !IsFast(id); i++)
			{
				Layout layout = m_layouts[i];
				if (adoptionLocked(layout.Id) || layout.Pending.Length != 0 || HasFast(layout)) continue;
				// Once some layout is in hand only a STRICTLY smaller gap can displace it, so the sphere
				// bound tightens from the adoption distance to the best gap so far. A layout the tighter
				// bound rejects would have failed the "gap < nearestGap" test below anyway, so the winner
				// and the tie-break are unchanged.
				double bound = nearest < 0 ? adoptDistance : Math.Min(adoptDistance, nearestGap);
				(double X, double Y, double Z, double R) sphere = spheres[i];
				double lower = Math.Sqrt((sphere.X - candidate.X) * (sphere.X - candidate.X)
					+ (sphere.Y - candidate.Y) * (sphere.Y - candidate.Y)
					+ (sphere.Z - candidate.Z) * (sphere.Z - candidate.Z)) - sphere.R - candidate.Radius;
				if (lower >= bound) continue;
				// Manual scan over the resolved members: the LINQ Min paid a delegate call and a byId
				// hash lookup per member. An empty set (a layout whose members all left the census -
				// impossible here, they are filtered above) leaves the gap infinite and skips the layout,
				// where the old Min would have thrown.
				Member[] set = resolved[i];
				double gap = double.PositiveInfinity;
				foreach (Member member in set)
				{
					double one = Gap(candidate, member);
					if (one < gap) gap = one;
				}
				if (!(gap < adoptDistance)) continue;
				// m_layouts is kept ordered by id, so a strict comparison keeps the smallest id on a tie -
				// the same choice "OrderBy(Gap).ThenBy(Layout.Id).First()" made.
				if (nearest < 0 || gap < nearestGap) { nearest = i; nearestGap = gap; }
			}
			if (nearest < 0)
			{
				if (DeferNewLayouts) continue;
				ulong[] fresh = m_allocate(1);
				if (fresh == null) continue;
				m_layouts.Add(new Layout(fresh[0], new[] { id }, now));
				Created.Add(fresh[0]);
				Member[] set = new[] { candidate };
				resolved.Add(set);
				spheres.Add(Enclosing(set));
			}
			else
			{
				Layout grown = m_layouts[nearest] with
					{ Members = m_layouts[nearest].Members.Append(id).Order().ToArray() };
				m_layouts[nearest] = grown;
				if (HoldAdoptionLocked != null) HoldAdoptions.Add((id, grown.Id, nearestGap));
				Member[] set = Resolve(grown, byId);
				resolved[nearest] = set;
				spheres[nearest] = Enclosing(set);
			}
		}

		// T-0292 FAST PASS: a layout holding a fast group together with anything else is split so that
		// every fast atomic group is alone in a partition, and a fast group spread over several layouts
		// (adopted as single members above) is joined into one. Nothing slow is ever merged INTO a
		// fast group: once alone, its layout is skipped by adoption, split and merge until it slows.
		if (fastGroups.Count != 0)
		{
			foreach (Layout layout in m_layouts.OrderBy(x => x.Id).ToArray())
			{
				if (m_locked(layout.Id) || layout.Pending.Length != 0 || !HasFast(layout)) continue;
				long[][] parts = layout.Members.GroupBy(id => IsFast(id) ? byId[id].AtomicGroup : long.MinValue)
					.OrderBy(g => g.Key).Select(g => g.Order().ToArray()).ToArray();
				if (parts.Length < 2) continue;
				ulong[] ids = m_allocate(parts.Length);
				if (ids == null) continue;
				Layout[] children = parts.Select((x, i) => new Layout(ids[i], x, now)).ToArray();
				m_layouts.Remove(layout);
				m_layouts.AddRange(children);
				changes.Add(new Change(true, new[] { layout.Id }, children,
					"fast split: " + (parts.Length - (parts.Any(x => !IsFast(x[0])) ? 1 : 0)) + " fast group(s) separated, members ["
					+ string.Join(",", parts.Select(x => x.Length)) + "]")
					{ InputLayouts = new[] { layout } });
			}
			foreach (IGrouping<long, Layout> spread in m_layouts
					.Where(x => !m_locked(x.Id) && x.Pending.Length == 0 && HasFast(x) && x.Members.All(IsFast))
					.GroupBy(x => byId[x.Members[0]].AtomicGroup).Where(g => g.Count() > 1).ToArray())
			{
				// Two at a time, as every merge the registry's lineage accepts; a third joins next pass.
				Layout[] inputs = spread.OrderBy(x => x.Id).Take(2).ToArray();
				Layout keep = inputs.OrderByDescending(x => x.Members.Length).ThenBy(x => x.Id).First();
				Layout joined = keep with { Members = inputs.SelectMany(x => x.Members).Distinct().Order().ToArray() };
				foreach (Layout joinedInput in inputs) m_layouts.Remove(joinedInput);
				m_layouts.Add(joined);
				changes.Add(new Change(false, inputs.Select(x => x.Id).ToArray(), new[] { joined },
					"fast group " + spread.Key + " joined from " + inputs.Length + " layouts; kept " + keep.Id)
					{ InputLayouts = inputs });
			}
		}

		// T-0339 (design C): which layouts this pass splits, and into which groups, before any of them is split - a
		// transfer target must be a layout the pass leaves whole. A layout's groups depend on its own members alone, so
		// the groups, and the order of the allocations below, are the ones the single loop produced.
		List<(Layout Layout, long[][] Parts, double At)> splitting = new List<(Layout, long[][], double)>();
		foreach (Layout layout in m_layouts.OrderBy(x => x.Id).ToArray())
		{
			bool locked = m_locked(layout.Id);
			if (locked || layout.Pending.Length != 0 || HasFast(layout))
			{
				// T-0339 (split deferral): a skipped layout that is due to split is recorded, nothing else. Its groups are
				// counted over the members the census holds (a pinned member it does not hold has no position).
				if (!HasFast(layout) && layout.Members.Length >= 2)
				{
					long[] present = layout.Members.Where(byId.ContainsKey).ToArray();
					int groups = present.Length < 2 ? present.Length : SplitGroups(present, byId).Parts.Length;
					if (groups >= 2) NoteDeferral(layout.Id, locked ? "locked" : "pending", groups, now);
				}
				continue;
			}
			// A layout of one member always comes back as one component, and "parts.Length < 2" below
			// then discards it - but the call still allocated a union-find, a grid and a result list per
			// layout. On a node whose traffic is mostly single-entity partitions that was most of the
			// split pass (917 of 918 layouts on the 1323-entity node).
			if (layout.Members.Length < 2) continue;
			(long[][] parts, double at) = SplitGroups(layout.Members, byId);
			if (parts.Length < 2) continue;
			splitting.Add((layout, parts, at));
		}
		// T-0339 (design C): the layouts that may take a piece - standing ones the pass does not split. Not a child of
		// this pass's splits either: those are born by this pass.
		HashSet<ulong> targets = TransferEnabled && splitting.Count != 0
			? m_layouts.Select(x => x.Id).Except(splitting.Select(x => x.Layout.Id)).ToHashSet() : null;
		foreach ((Layout layout, long[][] parts, double at) in splitting)
		{
			// T-0339: the largest group keeps the layout's id and only the others get new ones. A parked group used to
			// change its partition id every time a ship left it (r42 2026-10-02: 2286 of 5017 partition births in
			// 340 min were the remainder of a split), and each new id is a registry row, a first save and a
			// re-pointed client. A tie goes to the earlier group - Components orders them by their smallest member,
			// so the choice does not depend on the census order. The registry reads a save of the kept id from
			// before the split as the parent's (GatewayRegistry.ResolveCover).
			int kept = 0;
			for (int i = 1; i < parts.Length; i++)
				if (parts[i].Length > parts[kept].Length) kept = i;
			// T-0339 (design C): a group that is already within merge distance of a standing layout moves there in one
			// transfer, and keeps no id of its own. The source loses it before the split of what is left, so every
			// change names its inputs exactly as they stand when it is applied (and reverted, last first).
			// T-0354: the transfers are planned first (a target as the earlier transfers of this layout grow it) and made
			// only once the split of the rest has its ids: with none at hand (null) nothing of this layout changes and
			// the whole split, transfers included, waits for a later pass, as every other change does.
			List<long[]> rest = new List<long[]>();
			List<(int Target, long[] Part, double Gap, bool Atomic, double Reach)> moves = new List<(int, long[], double, bool, double)>();
			Dictionary<int, Layout> grownBy = new Dictionary<int, Layout>();
			int keep = -1;
			for (int i = 0; i < parts.Length; i++)
			{
				double gap = 0;
				bool atomic = false;
				double reach = 0;
				// T-0339 (design C2): the band reads the piece's gap to the rest of its source (paid only with the band on).
				int target = i == kept || targets == null ? -1
					: TransferTarget(parts[i], layout.CreatedAt,
						TransferBand ? SmallestCrossGap(new[] { parts[i], parts.Where((_, j) => j != i).SelectMany(x => x).ToArray() }, byId) : 0,
						targets, grownBy, byId, now, HasFast, out gap, out atomic, out reach);
				if (target < 0)
				{
					if (i == kept) keep = rest.Count;
					rest.Add(parts[i]);
					continue;
				}
				Layout planned = grownBy.TryGetValue(target, out Layout earlier) ? earlier : m_layouts[target];
				grownBy[target] = planned with { Members = planned.Members.Concat(parts[i]).Order().ToArray() };
				moves.Add((target, parts[i], gap, atomic, reach));
			}
			// T-0339 (split hysteresis): the fresh pieces closing on a standing layout stay with the kept group this pass.
			// The run bookkeeping is committed only once the fresh ids are in hand (below): a no-ids pass leaves the layout
			// and its run exactly as they were.
			int heldMembers = 0;
			Action hysteresisCommit = null;
			if (SplitHysteresisSeconds > 0 && targets != null)
			{
				HashSet<int> hold = Hysteresis(layout, rest, keep, moves.Select(x => (x.Part, m_layouts[x.Target].Id, x.Gap)), targets, byId,
					previousCensus, previousAt, gameClock, now, HasFast, out hysteresisCommit);
				if (hold.Count != 0)
				{
					long[] kept2 = rest[keep].Concat(hold.SelectMany(i => rest[i])).Order().ToArray();
					heldMembers = kept2.Length - rest[keep].Length;
					List<long[]> left = new List<long[]>();
					for (int i = 0; i < rest.Count; i++)
					{
						if (i == keep) { keep = left.Count; left.Add(kept2); }
						else if (!hold.Contains(i)) left.Add(rest[i]);
					}
					rest = left;
				}
			}
			ulong[] fresh = rest.Count < 2 ? Array.Empty<ulong>() : m_allocate(rest.Count - 1);
			if (fresh == null)
			{
				NoteDeferral(layout.Id, "no-ids", parts.Length, now);
				continue;
			}
			hysteresisCommit?.Invoke();
			Layout source = layout;
			foreach ((int target, long[] part, double gap, bool atomic, double reach) in moves)
			{
				Layout into = m_layouts[target];
				Layout from = source with { Members = source.Members.Except(part).ToArray() };
				Layout grown = into with { Members = into.Members.Concat(part).Order().ToArray() };
				m_layouts[target] = grown;
				m_layouts[m_layouts.FindIndex(x => x.Id == source.Id)] = from;
				changes.Add(new Change(true, new[] { layout.Id, into.Id }, new[] { from, grown },
					"transfer: [" + string.Join(",", part) + "] left the rest at the split distance (smallest gap "
					+ SmallestCrossGap(new[] { part, parts.Where(x => !ReferenceEquals(x, part)).SelectMany(x => x).ToArray() }, byId).ToString("F1")
					+ " m) within reach of " + into.Id + " ("
					+ (atomic ? "atomic link, nearest members " + gap.ToString("F1") + " m apart"
						: gap < m_distance ? "nearest member pair " + gap.ToString("F1") + " m apart, under the merge distance " + m_distance.ToString("F0") + " m (D)"
						// T-0339 (design C2): a band move names its band, so a log read can tell the two kinds apart.
						: "nearest member pair " + gap.ToString("F1") + " m apart, in the transfer band [" + m_distance.ToString("F0") + ", "
							+ reach.ToString("F0") + ") m (piece speed " + Resolve(new Layout(0, part, 0), byId).Max(x => x.Speed).ToString("F0")
							+ " m/s x " + TransferBandSeconds.ToString("F0") + " s)")
					+ "); moved " + layout.Id + " -> " + into.Id + " in one change, no new id")
					{ InputLayouts = new[] { source, into }, Moved = part });
				source = from;
			}
			if (rest.Count < 2) continue;
			ulong[] ids = rest.Select((_, i) => i == keep ? layout.Id : fresh[i < keep ? i : i - 1]).ToArray();
			for (int i = 0; i < rest.Count; i++)
				if (i != keep) MintedPieces.Add(MintDiag(ids[i], source, rest[i], rest, targets, byId, now, HasFast));
			// T-0339: the children carry the parent's age. Stamped "now", a piece that left its group at the split
			// distance while already within D of another group sat 10 s in a partition of its own before the merge
			// below would look at it (r42 2026-10-02: 597 such ids in 340 min, each registered, saved and retired).
			// The maturity delay is for a layout that is new to this node; siblings cannot flap, they are a split
			// distance apart, and a merge with anything else is still judged on the cores.
			Layout[] children = rest.Select((x, i) => new Layout(ids[i], x, layout.CreatedAt)).ToArray();
			m_layouts.RemoveAll(x => x.Id == source.Id);
			m_layouts.AddRange(children);
			changes.Add(new Change(true, new[] { layout.Id }, children,
				"members fell into " + rest.Count + " groups of [" + string.Join(",", rest.Select(x => x.Length))
				+ "] with every cross-group gap >= " + at.ToString("F0") + " m ("
				+ (at < m_distance + 2 * m_margin ? "oversize split: " + layout.Members.Length + " members > " + OversizeMembers : "split distance")
				+ "); smallest "
				+ SmallestCrossGap(rest.ToArray(), byId).ToString("F1") + " m"
				+ (heldMembers == 0 ? "" : "; " + heldMembers + " member(s) closing on a standing layout held with " + layout.Id + " (split hysteresis)"))
				{ InputLayouts = new[] { source } });
			// T-0339 (merge hold-off): the outputs of this split are siblings from now on, for the window.
			if (MergeHoldoffSeconds > 0)
				for (int i = 0; i < ids.Length; i++)
					for (int j = i + 1; j < ids.Length; j++)
						m_siblings[PairKey(ids[i], ids[j])] = (now, false);
		}
		// T-0339 (split deferral): a layout this pass did not defer starts a new run of deferrals next time.
		if (m_deferredSince.Count != 0)
			foreach (ulong done in m_deferredSince.Keys.Where(id => !DeferredSplits.Any(x => x.Layout == id)).ToArray())
				m_deferredSince.Remove(done);
		// T-0339 (split hysteresis): a run whose layout is no longer due (or gone) is over; a lock or a pin keeps it.
		if (m_hysteresis.Count != 0)
			foreach (KeyValuePair<ulong, HysteresisRun> run in m_hysteresis.ToArray())
			{
				if (splitting.Any(x => x.Layout.Id == run.Key) || DeferredSplits.Any(x => x.Layout == run.Key)) continue;
				m_hysteresis.Remove(run.Key);
				if (run.Value.Ended) continue;
				HysteresisReleased++;
				HysteresisEvents.Add((run.Key, run.Value.Members.Length, run.Value.Target, run.Value.Gap, run.Value.Rate, "cleared", now - run.Value.Since));
			}

		// The merge search picks the same pair the LINQ cross-join used to (smallest gap, ties by a.Id then
		// b.Id) but does not pay for every pair: the old query evaluated AtomicLink AND the full
		// member-by-member nearest-pair distance for EVERY ordered pair of layouts, then sorted them all to
		// take the first - O(N^2) member-distance computations per pass in the node's total member count, and
		// the pass repeats after every merge. It was 61% of PartitionMembership.Evaluate on the w22 regression
		// cluster (node-3, 372 entities, 2026-08-22). Two exact-preserving changes: atomic linkage is a set
		// overlap instead of |a|x|b| comparisons, and a pair whose enclosing spheres are farther apart than the
		// merge distance cannot contain a close member pair, so its inner scan is skipped. Neither can change
		// which pair wins: the sphere bound is a true lower bound on every member gap in the pair (a member
		// sphere lies inside its layout's enclosing sphere), so a pruned pair would have failed the
		// gap < distance test anyway. Atomic pairs bypass the bound because they merge at any distance.
		// Viktor 2026-08-24: "Don't merge until the partitions actually reach the sync distance."
		// Preemptive same-node placement is the gateway's job (time-to-contact co-location); the
		// merge itself waits for the invariant to actually bind. Split stays at D + 2·margin, so
		// the merge/split hysteresis band widens to [D, D+2m] and churn can only go down.
		double mergeDistance = m_distance;
		// T-0319: pairs refused by the core test this pass. Keyed by id; a merge rewrites one of its inputs
		// under the kept id, so the refusals are dropped after every merge and re-judged on the new layout.
		HashSet<(ulong, ulong)> refused = new HashSet<(ulong, ulong)>();
		RefusedMerges = 0;
		// T-0339 (merge hold-off): forget the sibling pairs whose window is over or whose layouts are gone; null = nothing
		// to hold this pass (the knob is 0, or no split is recent), and then the search below is exactly what it was.
		Dictionary<(ulong, ulong), (double At, bool Held)> holdoff = null;
		if (!(MergeHoldoffSeconds > 0)) m_siblings.Clear();
		else if (m_siblings.Count != 0)
		{
			HashSet<ulong> standing = m_layouts.Select(x => x.Id).ToHashSet();
			foreach (KeyValuePair<(ulong A, ulong B), (double At, bool Held)> sibling in m_siblings.ToArray())
				if (!(now >= sibling.Value.At && now - sibling.Value.At < MergeHoldoffSeconds)
					|| !standing.Contains(sibling.Key.A) || !standing.Contains(sibling.Key.B))
					m_siblings.Remove(sibling.Key);
			if (m_siblings.Count != 0) holdoff = m_siblings;
		}
		HashSet<(ulong, ulong)> heldThisPass = null;
		while (true)
		{
			Layout[] ordered = m_layouts.OrderBy(x => x.Id).ToArray();
			var enclosing = new (double X, double Y, double Z, double R)[ordered.Length];
			var groups = new HashSet<long>[ordered.Length];
			// T-0101 measured this pass and left it alone. It is quadratic in the LAYOUT count, and a
			// spatial grid over the enclosing spheres does remove that - but the live nodes carry 39-47
			// layouts even at 1290 entities (bin/t0097-sample: the traffic is convoys of ships, not one
			// partition per entity), so the scan is ~780 pair tests and cost 0.15 ms of a 10.6 ms
			// Evaluate. A grid, and even hoisting the per-pair predicates, measured SLOWER at that size:
			// the bookkeeping outweighs the tests it saves. If a node ever fragments into hundreds of
			// layouts this is where the quadratic will show up, and the grid argument is the one
			// Components already makes about members.
			for (int i = 0; i < ordered.Length; i++)
			{
				enclosing[i] = Enclosing(ordered[i], byId);
				groups[i] = AtomicGroups(ordered[i], byId);
			}
			Layout bestA = null, bestB = null;
			double bestGap = 0;
			bool bestAtomic = false;
		search:
			bestA = null; bestB = null; bestGap = 0; bestAtomic = false;
			for (int i = 0; i < ordered.Length; i++)
			{
				Layout a = ordered[i];
				if (m_locked(a.Id) || a.Pending.Length != 0 || HasFast(a)) continue;
				for (int j = i + 1; j < ordered.Length; j++)
				{
					Layout b = ordered[j];
					if (m_locked(b.Id) || b.Pending.Length != 0 || HasFast(b)) continue;
					bool atomic = Overlaps(groups[i], groups[j]);
					if (!atomic && !(now - a.CreatedAt >= 10 && now - b.CreatedAt >= 10)) continue;
					if (!atomic && SphereGap(enclosing[i], enclosing[j]) >= mergeDistance) continue;
					double gap = a.Members.Min(x => b.Members.Min(y => Gap(byId[x], byId[y])));
					if (!atomic && !(gap < mergeDistance)) continue;
					if (!atomic && refused.Contains((a.Id, b.Id))) continue;
					// T-0339 (merge hold-off): two outputs of a recent split, not touching or linked, and not under D/2 apart:
					// not merged this pass. Any other pair (either of them with a third layout too) is judged as before.
					if (holdoff != null && !atomic && !(gap < mergeDistance / 2)
						&& holdoff.TryGetValue(PairKey(a.Id, b.Id), out (double At, bool Held) sibling))
					{
						if ((heldThisPass ??= new HashSet<(ulong, ulong)>()).Add(PairKey(a.Id, b.Id)) && !sibling.Held)
						{
							holdoff[PairKey(a.Id, b.Id)] = (sibling.At, true);
							HeldMergePairs++;
							HoldoffsStarted.Add((Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id), gap, now - sibling.At));
						}
						continue;
					}
					// Ascending (a.Id, b.Id) iteration plus a strict comparison keeps the first pair of the
					// smallest gap, which is what "orderby gap, a.Id, b.Id" selected.
					if (bestA == null || gap < bestGap) { bestA = a; bestB = b; bestGap = gap; bestAtomic = atomic; }
				}
			}
			if (bestA == null) break;
			// T-0319: atomic links and touching members (overlapping bounding spheres) merge whatever the
			// cores say - physically interacting grids belong to one partition. Everything else must survive
			// the split rule once the pieces that made the contact are gone.
			if (CoreMerge && !bestAtomic && bestGap > 0 && !CoresHold(bestA, bestB, byId))
			{
				refused.Add((bestA.Id, bestB.Id));
				RefusedMerges++;
				goto search;
			}
			refused.Clear();
			Layout keep = bestA.Members.Length > bestB.Members.Length || bestA.Members.Length == bestB.Members.Length && bestA.Id < bestB.Id ? bestA : bestB;
			Layout merged = keep with { Members = bestA.Members.Concat(bestB.Members).Order().ToArray() };
			m_layouts.Remove(bestA); m_layouts.Remove(bestB); m_layouts.Add(merged);
			changes.Add(new Change(false, new[] { bestA.Id, bestB.Id }, new[] { merged },
				(bestAtomic ? "atomic link (attached grids) between the two, nearest members " + bestGap.ToString("F1") + " m apart"
					: "nearest member pair " + bestGap.ToString("F1") + " m apart, under the merge distance " + mergeDistance.ToString("F0") + " m (D)")
				+ "; kept " + keep.Id + " (" + keep.Members.Length + " of " + merged.Members.Length + " members)")
				{ InputLayouts = new[] { bestA, bestB } });
		}
		HeldMerges = heldThisPass?.Count ?? 0;
		m_layouts.Sort((a, b) => a.Id.CompareTo(b.Id));
		return changes.ToArray();
	}

	/// <summary>
	/// T-0319: would the split rule keep A and B together once the pieces that made the contact are gone?
	/// </summary>
	/// <remarks>
	/// The contact piece of a layout is the D-component (single linkage at D, atomic groups joined) inside
	/// it that holds a member within D of the other layout; its core is everything else. Such a piece is
	/// attached to its own core only through the hysteresis band (its gap to the core is at least D by
	/// construction), and that is exactly how a traveller looks: on node-1 at 21:40:42 Pilot-031's ship
	/// came within D of 285773's grid while still under 5000 m from the three grids it had left, the merge
	/// was taken, and 1.4 s later the split rule took it apart again. The merge is kept only if each core
	/// that is left (none, for a lone ship or a compact group) still reaches the other layout within the
	/// split distance - then the split rule would not undo it, whatever the contact piece does next.
	/// Same distance measure as both rules (<see cref="Gap"/>), no history, no time.
	/// </remarks>
	/// <summary>
	/// T-0339 (design C): the index in m_layouts of the layout the merge pass would merge <paramref name="piece"/> into
	/// once a split had made it a layout aged <paramref name="age"/>, or -1. Only <paramref name="targets"/> qualify,
	/// unlocked, unpinned and not fast; the tests are the merge pass's (atomic link, else both ages past the 10 s gate,
	/// the nearest member pair under D and the cores holding), and the smallest gap wins, ties to the smaller id.
	/// Asked only for a group a split cuts off, never on a pass that splits nothing.
	/// </summary>
	/// <remarks>T-0354: "unlocked" is the lock splits and merges use, which holds every layout an unanswered registry
	/// request names (PartitionManager.RegistryPending) - a claim-only layout too: it may adopt, but never takes a
	/// transfer, whose apply would name an id the registry has not given this node yet. <paramref name="planned"/> holds,
	/// by index, the targets as the transfers planned before this one grow them.</remarks>
	/// <remarks>T-0339 (design C2): <paramref name="sourceGap"/> is the piece's smallest gap to the rest of its source, read
	/// only with <see cref="TransferBand"/>. With the band a target qualifies up to <see cref="BandLimit"/> (out
	/// <paramref name="reach"/>) instead of D, the sphere prefilter included. The core test of a band candidate (nearest
	/// gap >= D) takes its contact at that limit: at D nothing of either side is in contact, every group is a core, and
	/// the nearest pair alone (under the split distance) passes it - the test would wave through the very case it exists
	/// for, a target whose only member near the piece is a traveller band-attached to a core beyond the split distance.
	/// A candidate under D is judged exactly as design C judges it.</remarks>
	/// <summary>T-0339 (class B diag): see <see cref="MintedPieces"/>. Read-only; the gates are <see cref="TransferTarget"/>'s.</summary>
	private (ulong, ulong, int, ulong, double, double, string) MintDiag(ulong pieceId, Layout source, long[] piece, List<long[]> rest,
		HashSet<ulong> targets, Dictionary<long, Member> byId, double now, Func<Layout, bool> hasFast)
	{
		Layout moving = new Layout(0, piece, source.CreatedAt);
		Member[] set = Resolve(moving, byId);
		HashSet<long> groups = AtomicGroups(moving, byId);
		double sourceGap = SmallestCrossGap(new[] { piece, rest.Where(x => !ReferenceEquals(x, piece)).SelectMany(x => x).ToArray() }, byId);
		double reach = TransferBand && set.Length != 0 ? BandLimit(set.Max(x => x.Speed), sourceGap) : m_distance;
		ulong bestId = 0;
		double bestGap = double.PositiveInfinity;
		string bestGate = "no-other-layout";
		foreach (Layout layout in m_layouts)
		{
			if (layout.Id == source.Id) continue;
			double nearest = double.PositiveInfinity;
			foreach (Member member in set)
				foreach (long other in layout.Members)
					if (byId.TryGetValue(other, out Member far)) nearest = Math.Min(nearest, Gap(member, far));
			if (!(nearest < bestGap) && bestId != 0) continue;
			bool linked = Overlaps(groups, AtomicGroups(layout, byId));
			string gate = targets == null ? "transfer-off"
				: !targets.Contains(layout.Id) ? "not-a-target"
				: m_locked(layout.Id) ? "locked"
				: layout.Pending.Length != 0 ? "pending"
				: hasFast(layout) ? "fast"
				: !linked && !(now - source.CreatedAt >= 10 && now - layout.CreatedAt >= 10) ? "age"
				: !linked && !(nearest < reach) ? "reach"
				: CoreMerge && !linked && nearest > 0 && !CoresHold(moving, layout, byId, nearest < m_distance ? m_distance : reach) ? "core"
				: "qualified";
			bestId = layout.Id;
			bestGap = nearest;
			bestGate = gate;
		}
		return (pieceId, source.Id, piece.Length, bestId, bestGap, reach, bestGate);
	}

	private int TransferTarget(long[] piece, double age, double sourceGap, HashSet<ulong> targets, Dictionary<int, Layout> planned,
		Dictionary<long, Member> byId, double now, Func<Layout, bool> hasFast, out double gap, out bool atomic, out double reach)
	{
		Layout moving = new Layout(0, piece, age);
		Member[] set = Resolve(moving, byId);
		(double X, double Y, double Z, double R) sphere = Enclosing(set);
		HashSet<long> groups = AtomicGroups(moving, byId);
		int best = -1;
		gap = 0;
		atomic = false;
		reach = TransferBand && set.Length != 0 ? BandLimit(set.Max(x => x.Speed), sourceGap) : m_distance;
		for (int i = 0; i < m_layouts.Count; i++)
		{
			Layout layout = planned.TryGetValue(i, out Layout grown) ? grown : m_layouts[i];
			if (!targets.Contains(layout.Id) || m_locked(layout.Id) || layout.Pending.Length != 0 || hasFast(layout)) continue;
			bool linked = Overlaps(groups, AtomicGroups(layout, byId));
			if (!linked && !(now - age >= 10 && now - layout.CreatedAt >= 10)) continue;
			if (!linked && SphereGap(sphere, Enclosing(layout, byId)) >= reach) continue;
			double nearest = double.PositiveInfinity;
			foreach (Member member in set)
				foreach (long other in layout.Members)
					if (byId.TryGetValue(other, out Member far)) nearest = Math.Min(nearest, Gap(member, far));
			if (!linked && !(nearest < reach)) continue;
			if (CoreMerge && !linked && nearest > 0 && !CoresHold(moving, layout, byId, nearest < m_distance ? m_distance : reach)) continue;
			if (best < 0 || nearest < gap || nearest == gap && layout.Id < m_layouts[best].Id) { best = i; gap = nearest; atomic = linked; }
		}
		return best;
	}

	/// <summary>
	/// T-0339 (split hysteresis): which of <paramref name="rest"/> (the groups that keep no target; <paramref name="keep"/>
	/// keeps the layout's id) stay with the kept group this pass - the fresh pieces closing on a standing layout, or none.
	/// See <see cref="SplitHysteresisSeconds"/> for the rule. Changes nothing itself: <paramref name="commit"/> records the
	/// run's start, end and the counters, and the caller runs it only once the pass has the fresh ids it needs.
	/// </summary>
	private HashSet<int> Hysteresis(Layout layout, List<long[]> rest, int keep, IEnumerable<(long[] Part, ulong Target, double Gap)> moved,
		HashSet<ulong> targets, Dictionary<long, Member> byId, Dictionary<long, Member> previous, double previousAt, double gameNow, double now,
		Func<Layout, bool> hasFast, out Action commit)
	{
		HashSet<int> hold = new HashSet<int>();
		List<Action> effects = new List<Action>();
		commit = () => { foreach (Action effect in effects) effect(); };
		ulong id = layout.Id;
		m_hysteresis.TryGetValue(id, out HysteresisRun run);
		// A transfer that takes any member of the held piece ends the run: the piece went where the hold waited for it to go.
		if (run != null && !run.Ended)
			foreach ((long[] part, ulong target, double gap) in moved)
			{
				if (!part.Any(x => Array.BinarySearch(run.Members, x) >= 0)) continue;
				HysteresisRun done = run;
				effects.Add(() =>
				{
					m_hysteresis.Remove(id);
					HysteresisTransferred++;
					HysteresisEvents.Add((id, part.Length, target, gap, done.Rate, "transferred", now - done.Since));
				});
				run = null;
				break;
			}
		if (run != null && run.Ended) return hold;
		double elapsed = run == null ? 0 : now - run.Since;
		if (run != null && elapsed >= SplitHysteresisSeconds)
		{
			effects.Add(() =>
			{
				run.Ended = true;
				HysteresisExpired++;
				HysteresisEvents.Add((id, run.Members.Length, run.Target, run.Gap, run.Rate, "expired", elapsed));
			});
			return hold;
		}
		(bool Closing, ulong Target, double Gap, double Rate) best = default, nearest = (false, 0, double.PositiveInfinity, 0);
		double bestEta = double.PositiveInfinity;
		// T-0339i: no reference census to judge against (only after game time went back): a running hold keeps its last
		// judgement - the pieces holding any of its members stay held, until the window ends - and nothing new starts.
		if (previous == null && run != null)
		{
			for (int i = 0; i < rest.Count; i++)
				if (i != keep && rest[i].Any(x => Array.BinarySearch(run.Members, x) >= 0)) hold.Add(i);
			best = (hold.Count != 0, run.Target, run.Gap, run.Rate);
			nearest = (false, run.Target, run.Gap, run.Rate);
		}
		for (int i = 0; previous != null && i < rest.Count; i++)
		{
			if (i == keep) continue;
			(bool closing, ulong target, double gap, double rate) = Closing(rest[i], targets, byId, previous, previousAt, gameNow,
				SplitHysteresisSeconds - elapsed, hasFast);
			if (gap < nearest.Gap) nearest = (closing, target, gap, rate);
			if (!closing) continue;
			hold.Add(i);
			double eta = (gap - m_distance) / rate;
			if (best.Closing == false || eta < bestEta) { bestEta = eta; best = (true, target, gap, rate); }
		}
		// T-0339j: a running hold whose piece is already inside D of the run's target keeps holding - Closing() needs gap >= D,
		// so the pass the piece crosses D found nothing closing and ended the run, while that pass's transfer was refused
		// (TransferTarget's core test: the target's member near the piece is a traveller whose core is still beyond the split
		// distance). The split then minted the id the hold existed to avoid, and it merged into the target seconds later
		// (r120 2026-10-07 16:03:48 347204 -> 347206 -> 347003 after 9.2 s; 16:04:37 340494 -> 347116 -> 333496 after 4.5 s).
		// Held until the transfer takes it or the window runs out, as any hold; Closing() still decides where a run STARTS.
		if (hold.Count == 0 && run != null && previous != null)
		{
			double inside = double.PositiveInfinity;
			for (int i = 0; i < rest.Count; i++)
			{
				if (i == keep || !rest[i].Any(x => Array.BinarySearch(run.Members, x) >= 0)) continue;
				double gap = GapTo(rest[i], run.Target, byId);
				if (!(gap < m_distance)) continue;
				hold.Add(i);
				inside = Math.Min(inside, gap);
			}
			if (hold.Count != 0) best = (true, run.Target, inside, run.Rate);
		}
		if (hold.Count == 0)
		{
			if (run != null)
				effects.Add(() =>
				{
					run.Ended = true;
					HysteresisReleased++;
					HysteresisEvents.Add((id, run.Members.Length, nearest.Target, nearest.Gap, nearest.Rate, "not-closing", elapsed));
				});
			return hold;
		}
		long[] members = hold.SelectMany(i => rest[i]).Order().ToArray();
		effects.Add(() =>
		{
			HysteresisHeld++;
			if (run == null)
			{
				run = m_hysteresis[id] = new HysteresisRun { Since = now };
				HysteresisHeldTotal++;
				HysteresisEvents.Add((id, members.Length, best.Target, best.Gap, best.Rate, "held", 0));
			}
			run.Members = members;
			(run.Target, run.Gap, run.Rate) = (best.Target, best.Gap, best.Rate);
		});
		return hold;
	}

	/// <summary>T-0339j: the nearest member gap of <paramref name="piece"/> to layout <paramref name="target"/> (infinity once it is gone).</summary>
	private double GapTo(long[] piece, ulong target, Dictionary<long, Member> byId)
	{
		double gap = double.PositiveInfinity;
		foreach (Layout layout in m_layouts)
		{
			if (layout.Id != target) continue;
			foreach (long a in piece)
				if (byId.TryGetValue(a, out Member p))
					foreach (long b in layout.Members)
						if (byId.TryGetValue(b, out Member q)) gap = Math.Min(gap, Gap(p, q));
		}
		return gap;
	}

	/// <summary>
	/// T-0339 (split hysteresis): is <paramref name="piece"/> closing on a layout it could be transferred into (in
	/// <paramref name="targets"/>, not locked, pinned or fast)? Closing on a layout: the nearest member gap now is at least
	/// D, the nearest gap over the members present in both censuses fell faster than <see cref="ClosingEpsilonMps"/> since
	/// <paramref name="previousAt"/> (game time, like <paramref name="gameNow"/>), and at that rate the gap reaches D within
	/// <paramref name="remaining"/> seconds. T-0339i: a member that moved faster than <see cref="MaxMemberSpeedMps"/> since
	/// then is not "present in both", and a rate above <see cref="MaxClosingMps"/> is not closing.
	/// Returns the closing layout that gets there first, else (not closing) the nearest one; Target 0 = none at all.
	/// </summary>
	private (bool Closing, ulong Target, double Gap, double Rate) Closing(long[] piece, HashSet<ulong> targets, Dictionary<long, Member> byId,
		Dictionary<long, Member> previous, double previousAt, double gameNow, double remaining, Func<Layout, bool> hasFast)
	{
		(bool Closing, ulong Target, double Gap, double Rate) best = default, nearest = (false, 0, double.PositiveInfinity, 0);
		double bestEta = double.PositiveInfinity, dt = gameNow - previousAt, jump = MaxMemberSpeedMps * dt;
		// In the reference census and not moved further than a flight can since: the member's reference position, else false.
		bool Steady(Member now, out Member before)
		{
			before = default;
			if (previous == null || !previous.TryGetValue(now.Id, out before)) return false;
			double dx = now.X - before.X, dy = now.Y - before.Y, dz = now.Z - before.Z;
			return !(Math.Sqrt(dx * dx + dy * dy + dz * dz) > jump);
		}
		foreach (Layout layout in m_layouts)
		{
			if (!targets.Contains(layout.Id) || m_locked(layout.Id) || layout.Pending.Length != 0 || hasFast(layout)) continue;
			double gap = double.PositiveInfinity, common = double.PositiveInfinity, before = double.PositiveInfinity;
			foreach (long a in piece)
			{
				if (!byId.TryGetValue(a, out Member p)) continue;
				bool seen = Steady(p, out Member pp);
				foreach (long b in layout.Members)
				{
					if (!byId.TryGetValue(b, out Member q)) continue;
					double g = Gap(p, q);
					if (g < gap) gap = g;
					if (seen && Steady(q, out Member pq))
					{
						if (g < common) common = g;
						before = Math.Min(before, Gap(pp, pq));
					}
				}
			}
			double rate = dt > 0 && !double.IsInfinity(before) ? (before - common) / dt : 0;
			if (gap < nearest.Gap) nearest = (false, layout.Id, gap, rate);
			if (!(rate > ClosingEpsilonMps) || !(rate <= MaxClosingMps) || !(gap >= m_distance) || !(gap - m_distance <= rate * remaining)) continue;
			double eta = (gap - m_distance) / rate;
			if (!best.Closing || eta < bestEta) { bestEta = eta; best = (true, layout.Id, gap, rate); }
		}
		return best.Closing ? best : nearest;
	}

	/// <summary>T-0339 (split deferral): record that this pass left <paramref name="layout"/>'s due split undone.</summary>
	private void NoteDeferral(ulong layout, string reason, int groups, double now)
	{
		if (!m_deferredSince.TryGetValue(layout, out double since)) m_deferredSince[layout] = since = now;
		DeferredSplits.Add((layout, reason, groups, now - since));
	}

	/// <remarks>T-0339 (design C2): <paramref name="contactDistance"/> is how near a member must be to the other side to
	/// count as contact - D for the merge pass and design C (the default), the band limit for a band transfer.</remarks>
	private bool CoresHold(Layout a, Layout b, Dictionary<long, Member> byId, double contactDistance = -1)
	{
		Member[] left = Resolve(a, byId), right = Resolve(b, byId);
		double contactAt = contactDistance < 0 ? m_distance : contactDistance;
		return CoreReaches(a, left, right, byId, contactAt) && CoreReaches(b, right, left, byId, contactAt);
	}

	private bool CoreReaches(Layout side, Member[] own, Member[] other, Dictionary<long, Member> byId, double contactDistance)
	{
		double split = m_distance + 2 * m_margin;
		HashSet<long> contact = new HashSet<long>();
		foreach (Member member in own)
			foreach (Member far in other)
				if (Gap(member, far) < contactDistance) { contact.Add(member.Id); break; }
		bool coreSeen = false;
		foreach (long[] part in Components(own.Select(x => x.Id), byId, m_distance))
		{
			if (part.Any(contact.Contains)) continue;
			coreSeen = true;
			foreach (long id in part)
				foreach (Member far in other)
					if (Gap(byId[id], far) <= split) return true;
		}
		return !coreSeen;
	}

	/// <summary>The groups the split pass cuts <paramref name="members"/> into and the distance they are apart: the split
	/// distance, or for an oversized layout the first shorter cut that fits (#1300, <see cref="OversizeMembers"/>).</summary>
	private (long[][] Parts, double At) SplitGroups(long[] members, Dictionary<long, Member> byId)
	{
		double at = m_distance + 2 * m_margin;
		long[][] parts = Components(members, byId, at).ToArray();
		if (OversizeMembers <= 0 || members.Length <= OversizeMembers || !(OversizeSplitFloor > m_distance)
			|| !(OversizeSplitFloor < at) || parts.Max(x => x.Length) <= OversizeMembers) return (parts, at);
		// The groups at the floor equal those at the split distance: the same at every cut between (a compact base pays one
		// extra pass, not eight).
		long[][] floor = Components(members, byId, OversizeSplitFloor).ToArray();
		if (floor.Length == parts.Length) return (parts, at);
		while (at > OversizeSplitFloor && parts.Max(x => x.Length) > OversizeMembers)
		{
			at = Math.Max(OversizeSplitFloor, at - OversizeStepMetres);
			parts = at == OversizeSplitFloor ? floor : Components(members, byId, at).ToArray();
		}
		return (parts, at);
	}

	/// <summary>
	/// The connected components of the members under "atomically linked, or within <paramref name="distance"/>
	/// of each other". Components come out ordered by their smallest member, each one sorted - the order the
	/// previous breadth-first search produced, because it seeded from the smallest remaining id.
	/// </summary>
	/// <remarks>
	/// The BFS re-scanned every still-unassigned member (and allocated an array) for every member it dequeued,
	/// which is O(N^2) distance tests in the member count. That is the whole world census on the cold-start
	/// path at line 71 and the whole partition on every split pass, several times a second on the game thread.
	/// This does the same work by union-find over two edge sources: atomic groups, which are equality classes
	/// and need no pairwise test at all, and proximity, where members are bucketed into a uniform grid whose
	/// cell is wide enough (distance + 2 x the largest member radius) that any linked pair must share a cell
	/// or touch one of its 26 neighbours. Distant members are never compared. A dense clump still costs its
	/// own pairs - members genuinely within reach of each other have to be tested - so the worst case is no
	/// worse than before, while the common case of many separated clumps stops being quadratic across clumps.
	/// </remarks>
	private static IEnumerable<long[]> Components(IEnumerable<long> ids, Dictionary<long, Member> members, double distance)
	{
		long[] all = ids.ToArray();
		if (all.Length == 0) yield break;
		// T-0101: the union-find runs over DENSE INDICES into `all`, not over a Dictionary keyed by the
		// entity id, and the members are resolved once instead of per candidate pair. The pair scan calls
		// Find twice per pair and rewrites parents while halving paths, so on the Earth ground site (400+
		// members all inside one another's sync distance, where the grid below degenerates to one bucket)
		// the dictionary hashing alone was 2.1 s of the 7.8 s Evaluate measured on the 1323-entity node.
		// Nothing about WHICH components come out changes: the union rule still compares the member IDS,
		// so every root is still its component's smallest member.
		Member[] point = new Member[all.Length];
		int[] parent = new int[all.Length];
		int Find(int i)
		{
			while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
			return i;
		}
		void Union(int a, int b)
		{
			a = Find(a); b = Find(b);
			if (a == b) return;
			if (all[a] < all[b]) parent[b] = a; else parent[a] = b;
		}
		// Both call sites pass STRICTLY ASCENDING ids - Evaluate's byId comes from a ToDictionary, which
		// throws on a repeat, and a layout's members are kept sorted and the layouts are disjoint - so a
		// repeated id is impossible here and the check for one costs a comparison per member. The
		// id-keyed structure this replaced collapsed a repeat into ONE node; if the assumption ever stops
		// holding, the fallback below joins the duplicate slots and keeps it one.
		bool distinct = true;
		for (int i = 0; i < all.Length; i++)
		{
			point[i] = members[all[i]];
			parent[i] = i;
			if (i != 0 && all[i] <= all[i - 1]) distinct = false;
		}
		if (!distinct)
		{
			Dictionary<long, int> slot = new Dictionary<long, int>(all.Length);
			for (int i = 0; i < all.Length; i++)
			{
				if (slot.TryGetValue(all[i], out int first)) Union(i, first);
				else slot[all[i]] = i;
			}
		}

		// Atomic linkage is an equality on the group id, so one pass over the members joins each group.
		Dictionary<long, int> firstOfGroup = new Dictionary<long, int>();
		for (int i = 0; i < all.Length; i++)
		{
			long group = point[i].AtomicGroup;
			if (group == 0) continue;
			if (firstOfGroup.TryGetValue(group, out int seen)) Union(i, seen);
			else firstOfGroup[group] = i;
		}

		// Proximity linkage. Cell width covers the largest gap a linked pair can span, so candidates only ever
		// live in the 3x3x3 block around a member's own cell.
		double maxRadius = 0;
		for (int i = 0; i < all.Length; i++) maxRadius = Math.Max(maxRadius, point[i].Radius);
		double cell = distance + 2 * maxRadius;
		if (!(cell > 0) || double.IsInfinity(cell) || double.IsNaN(cell))
		{
			// Degenerate geometry (zero distance and zero radii, or a non-finite coordinate): fall back to the
			// exhaustive comparison rather than risk a grid that cannot represent it.
			for (int i = 0; i < all.Length; i++)
				for (int j = i + 1; j < all.Length; j++)
					if (Gap(point[i], point[j]) <= distance) Union(i, j);
		}
		else
		{
			Dictionary<(long X, long Y, long Z), List<int>> grid = new Dictionary<(long, long, long), List<int>>();
			(long X, long Y, long Z) CellOf(Member member) => ((long)Math.Floor(member.X / cell),
				(long)Math.Floor(member.Y / cell), (long)Math.Floor(member.Z / cell));
			for (int i = 0; i < all.Length; i++)
			{
				(long X, long Y, long Z) key = CellOf(point[i]);
				if (!grid.TryGetValue(key, out List<int> bucket)) grid[key] = bucket = new List<int>();
				bucket.Add(i);
			}
			for (int i = 0; i < all.Length; i++)
			{
				Member member = point[i];
				long id = all[i];
				(long X, long Y, long Z) key = CellOf(member);
				for (long dx = -1; dx <= 1; dx++)
					for (long dy = -1; dy <= 1; dy++)
						for (long dz = -1; dz <= 1; dz++)
						{
							if (!grid.TryGetValue((key.X + dx, key.Y + dy, key.Z + dz), out List<int> bucket)) continue;
							foreach (int other in bucket)
							{
								if (all[other] <= id) continue;
								// Union is a no-op once the two are already in the same component, and in a
								// dense clump nearly every pair after the first few is. Comparing the roots
								// first skips the distance test entirely; it cannot change the outcome,
								// because the only thing that test can do is call a Union that returns.
								int ra = Find(i), rb = Find(other);
								if (ra == rb) continue;
								if (Gap(member, point[other]) <= distance)
								{
									if (all[ra] < all[rb]) parent[rb] = ra; else parent[ra] = rb;
								}
							}
						}
			}
		}

		// Union by smaller root makes every root the minimum of its component, so iterating the roots in
		// ascending order yields the components smallest-member-first. Grouping into lists and sorting
		// them is the same answer as the SortedDictionary of SortedSets, without a tree node per member.
		Dictionary<int, List<long>> byRoot = new Dictionary<int, List<long>>();
		for (int i = 0; i < all.Length; i++)
		{
			int root = Find(i);
			if (!byRoot.TryGetValue(root, out List<long> component)) byRoot[root] = component = new List<long>();
			component.Add(all[i]);
		}
		foreach (int root in byRoot.Keys.OrderBy(x => all[x]).ToArray())
		{
			List<long> component = byRoot[root];
			component.Sort();
			long[] unique = new long[component.Count];
			int count = 0;
			foreach (long id in component)
				if (count == 0 || unique[count - 1] != id) unique[count++] = id;   // the SortedSet deduplicated
			if (count != unique.Length) Array.Resize(ref unique, count);
			yield return unique;
		}
	}

	/// <summary>
	/// T-0319: the smallest gap between members of different groups of a split - the number the split
	/// actually crossed. The log used to name only the threshold, so no gap histogram could be built.
	/// Paid once per split, never on a pass that does not split.
	/// </summary>
	private static double SmallestCrossGap(long[][] parts, Dictionary<long, Member> byId)
	{
		double smallest = double.PositiveInfinity;
		for (int i = 0; i < parts.Length; i++)
			for (int j = i + 1; j < parts.Length; j++)
				foreach (long a in parts[i])
					foreach (long b in parts[j])
						smallest = Math.Min(smallest, Gap(byId[a], byId[b]));
		return smallest;
	}

	private static bool AtomicLink(Layout a, Layout b, Dictionary<long, Member> members) => a.Members.Any(x => b.Members.Any(y => Atomic(members[x], members[y])));

	/// <summary>The non-zero atomic groups a layout's members belong to.</summary>
	private static readonly HashSet<long> NoGroups = new HashSet<long>();

	private static HashSet<long> AtomicGroups(Layout layout, Dictionary<long, Member> members)
	{
		// Most layouts hold nothing atomically linked; the merge scan rebuilt an empty set for each of
		// them on every pass. The shared empty set is never written to - Overlaps only reads it.
		HashSet<long> groups = null;
		foreach (long id in layout.Members)
			if (members.TryGetValue(id, out Member member) && member.AtomicGroup != 0)
				(groups ??= new HashSet<long>()).Add(member.AtomicGroup);
		return groups ?? NoGroups;
	}

	/// <summary>Same answer as <see cref="AtomicLink"/>, in |a|+|b| instead of |a|x|b|.</summary>
	private static bool Overlaps(HashSet<long> a, HashSet<long> b)
	{
		if (a.Count == 0 || b.Count == 0) return false;
		HashSet<long> small = a.Count <= b.Count ? a : b, large = ReferenceEquals(small, a) ? b : a;
		foreach (long group in small) if (large.Contains(group)) return true;
		return false;
	}

	/// <summary>
	/// The layout's members as values. Absent ids are dropped, exactly as <see cref="Enclosing"/> drops
	/// them - a layout that is still pinned legitimately names members the census has not produced yet.
	/// </summary>
	private static Member[] Resolve(Layout layout, Dictionary<long, Member> members)
	{
		Member[] set = new Member[layout.Members.Length];
		int count = 0;
		foreach (long id in layout.Members)
			if (members.TryGetValue(id, out Member member)) set[count++] = member;
		if (count != set.Length) Array.Resize(ref set, count);
		return set;
	}

	/// <summary>
	/// <see cref="Enclosing(Layout, Dictionary{long, Member})"/> over members already resolved: the same
	/// sphere, without a dictionary lookup per member per candidate.
	/// </summary>
	private static (double X, double Y, double Z, double R) Enclosing(Member[] set)
	{
		if (set.Length == 0) return (0, 0, 0, double.PositiveInfinity);
		double x = 0, y = 0, z = 0;
		foreach (Member member in set) { x += member.X; y += member.Y; z += member.Z; }
		x /= set.Length; y /= set.Length; z /= set.Length;
		double radius = 0;
		foreach (Member member in set)
		{
			double dx = member.X - x, dy = member.Y - y, dz = member.Z - z;
			radius = Math.Max(radius, Math.Sqrt(dx * dx + dy * dy + dz * dz) + member.Radius);
		}
		return (x, y, z, radius);
	}

	/// <summary>
	/// A sphere containing every member sphere of the layout: centred on the mean member centre, with the
	/// radius stretched to cover the farthest member's own radius.
	/// </summary>
	private static (double X, double Y, double Z, double R) Enclosing(Layout layout, Dictionary<long, Member> members)
	{
		double x = 0, y = 0, z = 0;
		int count = 0;
		foreach (long id in layout.Members)
			if (members.TryGetValue(id, out Member member)) { x += member.X; y += member.Y; z += member.Z; count++; }
		if (count == 0) return (0, 0, 0, double.PositiveInfinity);
		x /= count; y /= count; z /= count;
		double radius = 0;
		foreach (long id in layout.Members)
			if (members.TryGetValue(id, out Member member))
			{
				double dx = member.X - x, dy = member.Y - y, dz = member.Z - z;
				radius = Math.Max(radius, Math.Sqrt(dx * dx + dy * dy + dz * dz) + member.Radius);
			}
		return (x, y, z, radius);
	}

	/// <summary>A lower bound on the gap between any member of one layout and any member of the other.</summary>
	private static double SphereGap((double X, double Y, double Z, double R) a, (double X, double Y, double Z, double R) b)
	{
		double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
		return Math.Sqrt(x * x + y * y + z * z) - a.R - b.R;
	}
	private static bool Atomic(Member a, Member b) => a.AtomicGroup != 0 && a.AtomicGroup == b.AtomicGroup;
	private static double Gap(Member a, Member b)
	{
		double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
		return Math.Sqrt(x * x + y * y + z * z) - a.Radius - b.Radius;
	}
}
