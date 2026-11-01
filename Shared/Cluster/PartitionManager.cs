using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Network;
using VRageMath;

namespace ClusterPlugin;

internal sealed class PartitionManager
{
	internal readonly struct ClientPartition
	{
		public readonly ulong Client;
		public readonly ulong Partition;
		public readonly long Entity;
		public readonly uint Generation;
		public ClientPartition(ulong client, ulong partition, long entity, uint generation = 0)
		{ Client = client; Partition = partition; Entity = entity; Generation = generation; }
	}

	internal sealed class Partition
	{
		public ulong Id;
		public HashSet<long> Members = new HashSet<long>();
		public BoundingSphereD Sphere;
		public bool Anchored;
		public double MaxMemberSpeed;
		/// <summary>Members adopted from a save whose entities have not finished the parallel init
		/// pipeline yet. A capture taken now would silently omit them, so periodic saves skip the
		/// partition and handover captures refuse it until the set drains (or the pin expires).</summary>
		public bool HasPending;
		/// <summary>T-0292: a member of this partition moves fast (FastMotionTracker); the partition is
		/// alone with that group, and the registry keeps it out of merges, wakes, pulls and migration.</summary>
		public bool Fast;
		/// <summary>The fastest member's measured speed (position over time), m/s; 0 when not fast.</summary>
		public double FastSpeed;
	}

	/// <summary>
	/// T-0319 (2026-10-01): THE NODE'S SPLIT NO LONGER READS THIS. The split distance is
	/// partition.splitDistance (<see cref="SplitDistance"/>, "auto" = 4 x the sync distance); Margin is
	/// still reported to the registry, whose co-location envelope is D + Margin. Everything below is the
	/// history of the split rule while it was D + 2 * Margin.
	///
	/// Hysteresis on the partition merge/split rule in <see cref="PartitionMembership"/>. The rule
	/// is ASYMMETRIC and Margin appears on ONE side of it only:
	///
	///     merge:  nearest member gap &lt;  D                  (Margin absent)
	///     split:  every cross-group gap &gt;= D + 2 * Margin
	///
	/// with D = <c>MyLayers.GetSyncDistance()</c> = the world's SyncDistance, 3000 m on w29. So the
	/// deadband is [D, D + 2*Margin] and Margin sets only its upper edge.
	///
	/// LOWERED 1000 -&gt; 500 on 2026-09-12 (T-0121, Viktor's call): split threshold 5000 m -&gt; 4000 m,
	/// deadband [3000, 5000] -&gt; [3000, 4000]. THE DEADBAND IS HALVED; NOTHING IS SPLIT BY IT.
	///
	/// I FIRST JUSTIFIED THIS CHANGE WITH THE WRONG RULE, and the corrected arithmetic is kept here
	/// because the wrong version was quoted to two people. I took the threshold from the
	/// merge/adopt test further down this file (<c>gap &gt;= GetSyncDistance() + Margin</c>) and
	/// called it D + Margin = 4000 m, then read this file's own "D+2m" as "D plus 2 metres". On the
	/// live blob that motivated the change - node-3 partition 43946, longest MST edge over 566
	/// member positions = 3981.0 m - that gave "19 m from splittable, frees ~141 members". Against
	/// the real rule:
	///
	///     Margin 1000 -&gt; split at 5000 m -&gt; no split, by 1019 m
	///     Margin  500 -&gt; split at 4000 m -&gt; STILL no split, by 19 m
	///
	/// The 19 m was right about the NEW threshold and wrong about the old one, which is exactly why
	/// it survived the substitution unnoticed. Converting the measured single-linkage sweep with
	/// Margin = (threshold - D) / 2:
	///
	///     Margin 1000 -&gt; 5000 m -&gt;  1 component, 566 members
	///     Margin  500 -&gt; 4000 m -&gt;  1 component, 566            &lt;- this value
	///     Margin  250 -&gt; 3500 m -&gt;  5 components, largest 425
	///     Margin    0 -&gt; 3000 m -&gt; 10 components, largest 409
	///     Margin -250 -&gt; 2500 m -&gt; 21 components, largest 404
	///     Margin -500 -&gt; 2000 m -&gt; 41 components, largest 361
	///
	/// Splitting that blob at all needs Margin at roughly 240 or below. At Margin &lt;= 0 the split
	/// threshold reaches the merge distance and the deadband closes entirely - see T-0126.
	///
	/// WHAT THIS VALUE CAN BE JUDGED ON: flapping, and nothing else. Margin IS the deadband, and
	/// halving it halves the protection against merge/split churn. Watch a loaded node's partition
	/// count at a constant entity count - sawing rather than drifting is this change. A partition
	/// count that does NOT move is the EXPECTED result, not a failure. Revert is one constant.
	/// See findings/the-blob-is-geometrically-contiguous-so-gap-splitting-correctly-declines.md.
	/// </summary>
	/// <summary>
	/// T-0126: no longer a constant. The value lives in the config plane as `partition.margin`
	/// (default 1000 m), and is read per membership pass so a PUT takes effect without a redeploy.
	/// It is operator-set only - there is deliberately no controller moving it; see the knob.
	/// </summary>
	private static double Margin => ClusterConfig.GetInt("partition.margin");

	/// <summary>
	/// T-0319: the split distance the membership pass uses at sync distance <paramref name="sync"/>:
	/// partition.splitDistance, "auto" = <see cref="PartitionMembership.AutoSplitDistance"/> (4 x the sync
	/// distance in use), a number = metres, never below the sync distance (a split threshold under the
	/// merge distance inverts the band into an oscillator - see partition.margin's floor). Since T-0319 the
	/// node's split no longer reads partition.margin; that knob is the registry's co-location envelope only.
	/// </summary>
	internal static double SplitDistance(double sync)
	{
		string configured = ClusterConfig.Get("partition.splitDistance");
		double split = string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)
			? PartitionMembership.AutoSplitDistance(sync)
			: double.Parse(configured, CultureInfo.InvariantCulture);
		return Math.Max(split, sync);
	}
	private readonly Func<uint> m_rangeIndex;
	private readonly Dictionary<ulong, Partition> m_partitions = new Dictionary<ulong, Partition>();
	private ulong m_nextId = 1;
	private readonly PartitionMembership m_membership;
	private Func<int, ulong[]> m_allocate;
	// Recomputed at the top of every pass from the layouts as they stand, so the predicate the membership
	// pass consults never observes half-rewritten layouts.
	private HashSet<ulong> m_locked = new HashSet<ulong>();
	// T-0339 (split deferral): layout -> the reason last logged for it and when (PartitionMembership.DeferralsToLog).
	private readonly Dictionary<ulong, (string Reason, double At)> m_deferralsLogged = new Dictionary<ulong, (string, double)>();

	/// <summary>
	/// T-0339 (split deferral): which lock left <paramref name="layout"/>'s due split undone, the first that applies of: a
	/// Closed node's topology hold, the registry hold, not owned, a frozen member, an unanswered registry request, the id
	/// held for this pass. The lasting reasons come first, so a held-for-one-pass lookup does not read as a new reason.
	/// </summary>
	private string LockReason(ulong layout, ulong? held, bool registryHold, HashSet<ulong> unowned)
	{
		if (HoldTopology) return "hold-closed";
		if (registryHold) return "hold-registry";
		if (unowned != null && unowned.Contains(layout)) return "unowned";
		if (m_membership.Layouts.FirstOrDefault(x => x.Id == layout)?.Members.Any(ClusterRuntime.IsFrozen) == true) return "frozen";
		if (RegistryPending?.Invoke(layout) == true) return "registry-pending";
		if (held == layout) return "held-id";
		return "locked";
	}
	private readonly FastMotionTracker m_fast = new FastMotionTracker();
	/// <summary>T-0292: members faster than the partition machinery (position over time).</summary>
	internal FastMotionTracker Fast => m_fast;
	// SE1-0084: the members of the last pass that can move (no static grids), whose positions the tick
	// detector reads every frame, and whether a member entered or left the fast state since the last Tick.
	private MyEntity[] m_tickMembers = Array.Empty<MyEntity>();
	private bool m_fastChanged;
	/// <summary>SE1-0084: how many members the tick detector reads every frame (diag).</summary>
	internal int TickMemberCount => m_tickMembers.Length;
	/// <summary>SE1-0084: the immediate pass after a fast transition runs at most once per this many frames (250 ms),
	/// so a member that keeps flipping cannot make every frame a membership pass. A transition inside the gap
	/// waits for the regular pass, which runs every 250 ms anyway (PartitionPersistence.Update).</summary>
	internal const int ImmediatePassGapFrames = 15;
	private long m_lastImmediatePass = -ImmediatePassGapFrames;
	private readonly FastFlapMonitor m_flaps = new FastFlapMonitor();
	/// <summary>SE1-0084: members reported as flipping in and out of the fast state since the start (diag).</summary>
	internal long FastFlapReports => m_flaps.Reports;
	/// <summary>SE1-0084: immediate passes run since the start (diag).</summary>
	internal long ImmediatePasses { get; private set; }
	public event Action<PartitionMembership.Change[]> TopologyChanged;
	/// <summary>T-0354: the ids of the layouts a pass made around unassigned members; raised before <see cref="TopologyChanged"/>.</summary>
	public event Action<ulong[]> LayoutsCreated;
	/// <summary>What the last pass did, so a triggered sweep can report its outcome (Orchestration.md 11.2).</summary>
	public PartitionMembership.Change[] LastChanges { get; private set; } = Array.Empty<PartitionMembership.Change>();

	public PartitionManager(Func<uint> rangeIndex)
	{
		m_rangeIndex = rangeIndex ?? throw new ArgumentNullException(nameof(rangeIndex));
		m_allocate = count => Enumerable.Range(0, count).Select(_ => NewId()).ToArray();
		m_membership = new PartitionMembership(MyLayers.GetSyncDistance(),
			(SplitDistance(MyLayers.GetSyncDistance()) - MyLayers.GetSyncDistance()) / 2, count => m_allocate(count),
			id => m_locked.Contains(id) || RegistryPending?.Invoke(id) == true);
		m_fast.Transition += (id, fast, speed, by) =>
		{
			m_fastChanged = true;
			VRage.Utils.MyLog.Default?.WriteLineAndConsole(string.Format(CultureInfo.InvariantCulture,
				"[ClusterPlugin] fast_member {0} entity={1} speed={2:F0} m/s (enter > {3:F0} x{4}, exit < {5:F0} x{6}, tick x{7}) by={8}",
				fast ? "enter" : "exit", id, speed, FastMotionTracker.EnterMps, FastMotionTracker.EnterSamples,
				FastMotionTracker.ExitMps, FastMotionTracker.ExitSamples, FastMotionTracker.TickSteps, by));
			int flaps = m_flaps.Note(id, (MySession.Static?.GameplayFrameCounter ?? 0) / 60.0);
			if (flaps != 0)
				VRage.Utils.MyLog.Default?.WriteLineAndConsole(string.Format(CultureInfo.InvariantCulture,
					"[ClusterPlugin] fast_flapping entity={0} transitions={1} in {2:F0} s: each one runs a membership pass and asks"
					+ " the Gateway for a pass (at most one each per 250 ms); reported once per {2:F0} s",
					id, flaps, FastFlapMonitor.WindowSeconds));
		};
	}

	/// <summary>
	/// SE1-0084: every game frame. Feeds the members' positions to the tick detector and, when one entered or
	/// left the fast state, runs the membership pass at once (at most once per <see cref="ImmediatePassGapFrames"/>),
	/// so a fast ship splits off (or rejoins) now and not at the next pass. Returns true when the fast state of a member changed since the last call, by
	/// this tick or by a pass in between: the caller then asks the Gateway for a pass of its own.
	/// </summary>
	public bool Tick()
	{
		long frame = MySession.Static?.GameplayFrameCounter ?? 0;
		if (FastMotionTracker.TickDetector && m_tickMembers.Length != 0
			&& m_fast.Step(m_tickMembers.Where(entity => !entity.MarkedForClose)
				.Select(entity => new KeyValuePair<long, Vector3D>(entity.EntityId, entity.PositionComp.GetPosition())), frame)
			&& frame - m_lastImmediatePass >= ImmediatePassGapFrames)
		{
			m_lastImmediatePass = frame;
			ImmediatePasses++;
			Evaluate();
		}
		bool changed = m_fastChanged;
		m_fastChanged = false;
		return changed;
	}
	/// <summary>T-0292: members that arrive already fast (handover from a node that measured them) start fast here.</summary>
	public void SeedFast(IEnumerable<KeyValuePair<long, double>> speeds, string origin)
	{
		double now = (MySession.Static?.GameplayFrameCounter ?? 0) / 60.0;
		foreach ((long id, double speed) in speeds)
		{
			if (!MyEntities.TryGetEntityById(id, out MyEntity entity)) continue;
			m_fast.Seed(id, entity.PositionComp.GetPosition(), now, speed);
			VRage.Utils.MyLog.Default?.WriteLineAndConsole(string.Format(CultureInfo.InvariantCulture,
				"[ClusterPlugin] fast_member carried entity={0} speed={1:F0} m/s from={2}", id, speed, origin));
		}
	}
	public void SetAllocator(Func<int, ulong[]> allocate) => m_allocate = allocate ?? throw new ArgumentNullException(nameof(allocate));

	/// <summary>
	/// Freeze the topology: no layout is rewritten while set. A closing node flips this the moment
	/// the heartbeat reports it Closed - the registry refuses claims from a Closed node, so any
	/// partition minted from here on could never be registered, saved or handed over, and a member
	/// that a split moved into one would die with the node. Members stay in their current
	/// partitions instead, and the drain moves them out with those partitions. Never unset: a
	/// closed node never reopens (its replacement is a new process).
	/// </summary>
	public volatile bool HoldTopology;

	/// <summary>
	/// T-0350: the registry is unreachable (set and cleared by the heartbeat loop). No layout is rewritten and no new
	/// layout is made while set, so the game thread asks the registry for nothing: a split, a merge or a new partition
	/// id used to retry on the game thread until the registry answered, and the node's simulation stood still for the
	/// whole outage (7 minutes inside a network window, then killed by the stall watchdog). Unlike HoldTopology it is
	/// cleared again: members stay where they are and the pass resumes with the first heartbeat that is answered.
	/// </summary>
	public volatile bool RegistryHold;

	/// <summary>
	/// T-0339 (design C): the registry applies a topology transfer (heartbeat TopologyTransfer, set by the heartbeat
	/// loop). Unset - an older registry, or one that refused a transfer - the pass makes a split and a merge instead.
	/// </summary>
	public volatile bool RegistryTransfer;

	/// <summary>
	/// T-0354: a layout whose registry request (the topology apply that made it or retires it, the claim of its id)
	/// is not answered yet. Locked like a frozen one - no split, merge or adoption - and asked live, not once per
	/// pass: an id handed out DURING a pass is pending from that moment, so the same pass builds nothing on it.
	/// The allocator may return null (no id at hand); the change that asked is then left for a later pass.
	/// </summary>
	public Func<ulong, bool> RegistryPending;

	/// <summary>
	/// T-0354b: a layout whose only unanswered registry request is the claim of its own id
	/// (<see cref="RegistryApplyQueue.IsClaimOnly"/>). It is not owned until the claim is answered, and pending, yet
	/// it may adopt - see <see cref="RegistryApplyQueue.AdoptionLock"/>. Splits and merges stay locked on it.
	/// </summary>
	public Func<ulong, bool> ClaimOnly;

	/// <summary>
	/// T-0307: which layouts the node owns in the registry (null = no registry, everything is local).
	/// A layout it does not own - a startup-fenced stale copy waiting for recovery, above all - is
	/// locked like a frozen one: no split, merge or adoption. The pass used to mutate it anyway and
	/// allocate the split outputs, which the allocator CLAIMS; the topology commit was then deferred
	/// but never undone, so the stale members sat in freshly owned partitions that would be saved and
	/// registered beside the real, newer copy on another node.
	/// </summary>
	public Func<ulong, bool> Owned;

	/// <summary>The layouts one pass must not touch: frozen members, everything under HoldTopology,
	/// the held id, and every layout the node does not own.</summary>
	internal static HashSet<ulong> LockedLayouts(IReadOnlyList<PartitionMembership.Layout> layouts, Func<long, bool> frozen,
		bool hold, ulong? held, Func<ulong, bool> owned)
	{
		HashSet<ulong> locked = layouts.Where(layout => hold || layout.Members.Any(frozen) || owned != null && !owned(layout.Id))
			.Select(layout => layout.Id).ToHashSet();
		if (held.HasValue) locked.Add(held.Value);
		return locked;
	}

	public IReadOnlyCollection<Partition> Evaluate() => Evaluate(null);

	/// <summary>One pass with <paramref name="held"/> locked: no split, merge or adoption touches it.</summary>
	/// <remarks>
	/// T-0150: a handover commit adopts the arriving partition and evaluates. A partition captured on a
	/// Closed source (HoldTopology froze it there) whose members had drifted D+2m apart split INSIDE the
	/// commit, the commit threw on the missing id, and the gateway's retry re-adopted the now-retired id
	/// and split it again - the registry answered "ownership is fenced" and the target node terminated.
	/// 11 nodes died that way on 2026-09-14. Held for the commit pass, it splits on the next regular
	/// pass, after the commit is recorded.
	/// </remarks>
	public IReadOnlyCollection<Partition> EvaluateHolding(ulong held) => Evaluate((ulong?)held);

	/// <summary>
	/// Undo changes the last pass applied in memory but the registry refused, then rebuild the partition
	/// view from the restored layouts. <paramref name="refused"/> is in the order the pass produced them.
	/// </summary>
	/// <remarks>T-0354b: <paramref name="dissolved"/>: layouts whose id claim was refused or never sent; their members are
	/// left unassigned and the next pass places them again (under the hold that follows a refusal: the nearest owned
	/// layout up to the split distance, else a local placeholder).</remarks>
	public void RevertTopology(PartitionMembership.Change[] refused, IEnumerable<ulong> dissolved = null)
	{
		for (int index = refused.Length - 1; index >= 0; index--)
			m_membership.Revert(refused[index]);
		foreach (ulong id in dissolved ?? Array.Empty<ulong>()) m_membership.Remove(id);
		Dictionary<long, MyEntity> entities = MyEntities.GetEntities().Where(IsMember).ToDictionary(entity => entity.EntityId);
		m_partitions.Clear();
		foreach (PartitionMembership.Layout layout in m_membership.Layouts)
			Add(layout.Id, layout.Members.ToHashSet(), entities, layout.Pending.Length != 0);
	}

	private IReadOnlyCollection<Partition> Evaluate(ulong? held)
	{
		double sync = MyLayers.GetSyncDistance();
		m_membership.SetDistance(sync);
		m_membership.SetMargin((SplitDistance(sync) - sync) / 2);
		m_membership.CoreMerge = ClusterConfig.GetBool("partition.coreMerge");
		m_membership.TransferEnabled = RegistryTransfer && ClusterConfig.GetBool("partition.transfer");
		// T-0339 (design C2): the transfer band rides on the transfer (no transfers, no band).
		m_membership.TransferBand = ClusterConfig.GetBool("partition.transferBand");
		m_membership.TransferBandSeconds = ClusterConfig.GetInt("partition.transferBandSeconds");
		// T-0339 (merge hold-off): 0 = off. Read every pass, so a config document change applies on the next pass.
		m_membership.MergeHoldoffSeconds = Math.Max(0, ClusterConfig.GetInt("partition.mergeHoldoffSeconds"));
		// T-0339 (split hysteresis): 0 = off. Read every pass, like the hold-off.
		m_membership.SplitHysteresisSeconds = Math.Max(0, ClusterConfig.GetInt("partition.splitHysteresisSeconds"));
		// #1300: an oversized partition splits at a shorter distance. 0 = off. Read every pass.
		m_membership.OversizeMembers = Math.Max(0, ClusterConfig.GetInt("partition.oversizeMembers"));
		// #1305: down to the registry's co-location envelope D + margin (was D + 2 x margin): pieces cut there are separate
		// gateway components, so the balancer can move them apart, and they stay a margin clear of the merge distance D.
		m_membership.OversizeSplitFloor = sync + Margin;
		Dictionary<long, MyEntity> entities = MyEntities.GetEntities()
			.Where(IsMember)
			.ToDictionary(entity => entity.EntityId);
		// A frozen entity NO local layout owns is an import in progress: created by a transfer
		// between its import and its commit or discard, and its fate is the transfer's to decide -
		// commit hands it to the arriving partition (Adopt), abort closes it. Absorbing it here
		// minted a REGISTERED partition around the copy (the unassigned-member pass allocates a
		// real id), and when the transfer then aborted, one entity was left held by two registered
		// partitions on two nodes - the duplicate-incarnation pairs of 2026-08-16 (316/365,
		// 436/490, "Pirate Base Large mk.2"), whose every co-location import then bounced off the
		// duplicate guard until both sides sat HandoverBlocked forever. Frozen members that a
		// layout ALREADY owns stay: their layout is locked (m_locked below), and dropping them
		// would strip mid-capture partitions of their members instead.
		HashSet<long> owned = m_membership.Layouts.SelectMany(layout => layout.Members).ToHashSet();
		foreach (long inFlight in entities.Keys
				.Where(id => !owned.Contains(id) && ClusterRuntime.IsFrozen(id)).ToArray())
			entities.Remove(inFlight);
		// An entity that arrived through an assigned save whose claim has not been confirmed is not
		// this node's to shape either: if its layout dissolves (fenced purge, pin expiry) before the
		// fence resolves, absorbing it here mints a REGISTERED partition around a copy that belongs
		// to another node's newer generation - the seed copy of 'Pirate Base Large mk.2' became
		// partition 671 opposite its real incarnation in 672 this way (2026-08-16, second wedge of
		// the day). Tainted entities either get re-owned (untaint) or closed by the orphan purge.
		foreach (long tainted in entities.Keys
				.Where(id => !owned.Contains(id) && PartitionPersistence.IsFenceTainted(id)).ToArray())
			entities.Remove(tainted);
		Dictionary<long, long> groups = AtomicGroups(entities);
		// Game time, not wall time: positions advance per simulation step (FastMotionTracker, TIME).
		double gameNow = (MySession.Static?.GameplayFrameCounter ?? 0) / 60.0;
		m_fast.Sample(entities.Select(x => new KeyValuePair<long, Vector3D>(x.Key, x.Value.PositionComp.GetPosition())), gameNow);
		m_tickMembers = entities.Values.Where(entity => !(entity is MyCubeGrid grid && grid.IsStatic)).ToArray();
		bool registryHold = RegistryHold;
		m_locked = LockedLayouts(m_membership.Layouts, ClusterRuntime.IsFrozen, HoldTopology || registryHold, held, Owned);
		// T-0353: under a hold an unassigned member (a pilot who left the seat) still joins the nearest layout the hold
		// alone locks; a frozen, unowned or held-for-this-pass layout adopts nothing, as without a hold.
		// T-0354b: without a hold, a layout whose only unanswered registry request is its id claim adopts: the second grid
		// of a pair spawned a frame apart joins the first one's new layout instead of getting its own. Under a hold it does
		// not: the claim may stay unanswered for the whole outage, the layout is not saved meanwhile, and a pilot's body
		// is better kept in an owned layout (a registry hold makes no new layouts, so no pair forms one there).
		// Ownership as it stands at the top of the pass, as m_locked has it (the adoption step asks per candidate and layout;
		// a layout made during the pass is new and ownerless, and adopts while its id is only taken or claimed).
		HashSet<ulong> adoptionBase = LockedLayouts(m_membership.Layouts, ClusterRuntime.IsFrozen, false, held, null);
		HashSet<ulong> unowned = Owned == null ? null : m_membership.Layouts.Where(layout => !Owned(layout.Id)).Select(layout => layout.Id).ToHashSet();
		Func<ulong, bool> ownedNow = unowned == null ? null : id => !unowned.Contains(id);
		m_membership.AdoptionLocked = RegistryApplyQueue.AdoptionLock(adoptionBase.Contains, ownedNow, RegistryPending, ClaimOnly);
		m_membership.HoldAdoptionLocked = HoldTopology || registryHold
			? RegistryApplyQueue.AdoptionLock(adoptionBase.Contains, ownedNow, RegistryPending, null) : null;
		// A Closed or fenced node gets local placeholder ids without a registry call; only a live node defers.
		m_membership.DeferNewLayouts = registryHold && !HoldTopology;
		// Layout maturity is a purely local age gate, so it must ride the always-initialized local
		// clock: ClusterMilliseconds falls back to host uptime before the first gateway sample, and
		// a layout stamped in that window sat days "in the future" - the merge pass then saw it as
		// immature forever and silently never merged it (P1-MEM-05, chain never aggregated).
		double now = ClusterRuntime.LocalMilliseconds / 1000.0;
		PartitionMembership.Change[] changes = m_membership.Evaluate(entities.OrderBy(x => x.Key).Select(x =>
		{
			BoundingSphereD sphere = BoundingSphereD.CreateFromBoundingBox(x.Value.PositionComp.WorldAABB);
			return new PartitionMembership.Member(x.Key, sphere.Center.X, sphere.Center.Y, sphere.Center.Z, sphere.Radius,
				x.Value.Physics?.LinearVelocity.Length() ?? 0, groups[x.Key], m_fast.IsFast(x.Key));
			// T-0339i: the split hysteresis's closing rate on game time too (this pass runs several times per frame).
		}), now, gameNow);
		// T-0353: the live proof that the hold path ran (the window verdict counts these lines).
		foreach ((long entity, ulong layout, double gap) in m_membership.HoldAdoptions)
			VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] topology hold_adoption entity=" + entity + " partition=" + layout
				+ " gapM=" + gap.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
				+ " hold=" + (HoldTopology ? "closed" : "registry"));
		// T-0339 (merge hold-off): one line per sibling pair, on the first pass that holds its merge off (at most one per split).
		foreach ((ulong a, ulong b, double gap, double age) in m_membership.HoldoffsStarted)
			VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] topology merge_holdoff partitions=" + a + "," + b
				+ " gapM=" + gap.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
				+ " sinceSplitS=" + age.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
				+ " windowS=" + m_membership.MergeHoldoffSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
		// T-0339 (split hysteresis): one line when a hold starts and one when it ends (transferred, expired, not-closing, cleared).
		// A held split is not a deferral: split_deferred never lists it.
		foreach ((ulong layout, int piece, ulong target, double gap, double rate, string outcome, double heldS) in m_membership.HysteresisEvents)
			VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] topology split_hysteresis partition=" + layout + " piece=" + piece
				+ " target=" + (target == 0 ? "-" : target.ToString(System.Globalization.CultureInfo.InvariantCulture))
				+ " gapM=" + (double.IsInfinity(gap) ? "-" : gap.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
				+ " closingMps=" + rate.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
				+ " outcome=" + outcome + " heldS=" + heldS.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
		// T-0339 (class B diag, CH 2026-10-08 10:21): one line per id a split minted - the nearest other layout here and the
		// transfer gate it failed, so a read can say why the piece was not transferred (life3: 24 of 99 short lives on r121i).
		foreach ((ulong piece, ulong source, int members, ulong nearest, double gap, double reach, string gate) in m_membership.MintedPieces)
			VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] topology minted_piece partition=" + piece + " source=" + source
				+ " members=" + members + " nearest=" + (nearest == 0 ? "-" : nearest.ToString(System.Globalization.CultureInfo.InvariantCulture))
				+ " gapM=" + (double.IsInfinity(gap) ? "-" : gap.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
				+ " reachM=" + reach.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " gate=" + gate);
		// T-0339 (split deferral): a layout due to split that the pass left whole, once per layout and reason, again every
		// 60 s while it lasts. The membership says "locked"; the reason is named here from what locked it at the top of the pass.
		if (m_membership.DeferredSplits.Count != 0 || m_deferralsLogged.Count != 0)
			foreach ((ulong layout, string reason, int pieces, double since) in PartitionMembership.DeferralsToLog(
				m_membership.DeferredSplits.Select(x => (x.Layout, x.Reason == "locked" ? LockReason(x.Layout, held, registryHold, unowned) : x.Reason, x.Groups, x.Since)),
				m_deferralsLogged, now))
				VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] topology split_deferred partition=" + layout + " reason=" + reason
					+ " groups=" + pieces + " sinceS=" + since.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
		m_partitions.Clear();
		foreach (PartitionMembership.Layout layout in m_membership.Layouts)
			Add(layout.Id, layout.Members.ToHashSet(), entities, layout.Pending.Length != 0);
		LastChanges = changes;
		if (m_membership.Created.Count != 0) LayoutsCreated?.Invoke(m_membership.Created.ToArray());
		if (changes.Length != 0) TopologyChanged?.Invoke(changes);
		// T-0138: publish the local content envelope for the proxy-antenna scan throttle. It bands
		// a remote antenna by how far it is from anything THIS node owns, and these spheres are
		// the only cheap answer - they are already computed here, once per pass, for every
		// partition the node holds. Recomputing them per proxy per tick would cost more than the
		// scan it is trying to avoid.
		Shared.Patches.MyProxyAntennaScanPatch.PublishLocalSpheres(
			m_partitions.Values.Select(partition => partition.Sphere).ToArray());
		return m_partitions.Values.OrderBy(x => x.Id).ToArray();
	}

	/// <summary>The partition <paramref name="id"/>, after a sweep that holds it.</summary>
	/// <remarks>
	/// T-0158: every caller asks for one partition BY ID (a capture, a snapshot, a Final capture), so
	/// the sweep must not merge or split that id away inside the lookup. An unheld sweep in a balance
	/// capture merged 8372 into 8371 and the same call then threw "Unknown partition 8372"
	/// (2026-09-15 15:38:33, node-2): the pinned migration was lost as a failed handover. Held for
	/// this pass, it merges or splits on the next regular pass.
	/// </remarks>
	public Partition Get(ulong id) => Evaluate((ulong?)id).FirstOrDefault(partition => partition.Id == id)
		?? throw new InvalidOperationException("Unknown partition " + id);

	/// <summary>The partition as it stands, WITHOUT running the topology sweep.</summary>
	/// <remarks>
	/// Get() evaluates - it can merge and split on the spot. A caller holding a partition in a
	/// half-finished state (recovery load between Adopt and its Final capture) must not hand the
	/// sweep that partition: seen live in P1-SAV-07, Get() inside LoadRecovery split the freshly
	/// adopted partition out from under the claim, the lookup then threw Unknown partition, and
	/// the rollback closed every just-created entity while the split had already re-homed them -
	/// content lost and the released claim retried against a now-retired partition forever.
	/// </remarks>
	public Partition Peek(ulong id) => m_partitions.TryGetValue(id, out Partition partition) ? partition
		: throw new InvalidOperationException("Unknown partition " + id);

	public void Adopt(ulong id, IEnumerable<long> members, bool mature = false)
	{
		Dictionary<long, MyEntity> entities = MyEntities.GetEntities().Where(IsMember).ToDictionary(entity => entity.EntityId);
		long[] all = members.Distinct().ToArray();
		HashSet<long> set = all.Where(entities.ContainsKey).ToHashSet();
		// Members the loader says belong here but the census cannot see yet are PINNED, not
		// dropped: a big grid from a partition save spends over a minute in the parallel entity
		// init pipeline, and dropping it here let the unassigned-member pass mint a fresh
		// registered partition around it when it surfaced (the 330/333 duplicate incarnation of
		// 2026-08-16). See PartitionMembership.Layout.Pending.
		long[] pending = all.Where(x => !set.Contains(x)).ToArray();
		foreach (Partition partition in m_partitions.Values)
		{
			partition.Members.ExceptWith(set);
		}
		m_partitions.Remove(id);
		m_membership.Adopt(id, set, ClusterRuntime.LocalMilliseconds / 1000.0, mature, pending);
		Add(id, all.ToHashSet(), entities, pending.Length != 0);
	}

	public void Remove(ulong id) { m_partitions.Remove(id); m_membership.Remove(id); }

	/// <summary>T-0296 r3: <see cref="PartitionMembership.Return"/>, then the partition view rebuilt from the layouts.</summary>
	public void ReturnKept(ulong id, IReadOnlyDictionary<long, ulong> previous)
	{
		m_membership.Return(id, previous);
		Dictionary<long, MyEntity> entities = MyEntities.GetEntities().Where(IsMember).ToDictionary(entity => entity.EntityId);
		m_partitions.Clear();
		foreach (PartitionMembership.Layout layout in m_membership.Layouts)
			Add(layout.Id, layout.Members.ToHashSet(), entities, layout.Pending.Length != 0);
	}

	/// <summary>The partition holding an entity in the STANDING layout, or 0 - no topology sweep.</summary>
	/// <remarks>
	/// For periodic reporters (character registry, character homes) that only need to name the
	/// holder: the sweep already ran this frame, and running it again once per character cost a
	/// ~200 ms hitch every second on a 400-member node (2026-08-22). Same caveat as
	/// <see cref="Peek"/>: at most one frame stale.
	/// </remarks>
	public ulong Holder(long entityId)
	{
		foreach (Partition partition in m_partitions.Values)
			if (partition.Members.Contains(entityId)) return partition.Id;
		return 0;
	}

	/// <summary>
	/// Member -> partition for the whole STANDING layout, built in one pass. No topology sweep.
	/// </summary>
	/// <remarks>
	/// <see cref="Holder"/> walks every partition, so asking it once per character is O(characters x
	/// partitions) - the shape the character registry paid every second. A caller naming more than one
	/// entity should build this map once instead.
	/// </remarks>
	public Dictionary<long, ulong> Holders()
	{
		Dictionary<long, ulong> holders = new Dictionary<long, ulong>();
		foreach (Partition partition in m_partitions.Values)
			foreach (long member in partition.Members) holders[member] = partition.Id;
		return holders;
	}

	/// <summary>True when the id is already a live partition or a membership layout on this node -
	/// the allocator must never hand such an id out again (duplicate layout ids crash the sweep).</summary>
	internal bool IsKnownId(ulong id) => m_partitions.ContainsKey(id) || m_membership.Layouts.Any(x => x.Id == id);

	/// <summary>
	/// Startup-fenced predicate (PartitionPersistence.m_startupFenced): a partition loaded from this
	/// node's own saves at start whose registry row turned out to be owned elsewhere. Its content is
	/// provisional - it is either claimed later or purged - so the report flags it and the recovery
	/// census on other nodes must NOT treat its members as live. On 2026-08-21 03:17 node-2 warm-loaded
	/// a stale copy of the Earth starter base, node-5's recovery of the authoritative lineage skipped
	/// the base as "already live on another node", and node-2 then purged its copy: the base was gone.
	/// </summary>
	public Func<ulong, bool> IsStartupFenced { get; set; } = _ => false;

	public string ReportJson()
	{
		IReadOnlyCollection<Partition> evaluated = Evaluate();
		SampleSimLoad(evaluated);
		ClientPartition[] routes = ClientPartitions(evaluated);
		// One pass over the routes instead of re-scanning them per partition, and one PCU walk per partition
		// instead of two (EstimatedSnapshotBytes recomputed it). This report is polled by the balancer and is
		// also emitted after every handover commit, so both scans were paid on the game thread.
		Dictionary<ulong, string> clientsByPartition = routes.GroupBy(x => x.Partition)
			.ToDictionary(x => x.Key, x => string.Join(',', x.Select(route => route.Client)));
		string partitions = string.Join(',', evaluated.Select(partition => string.Format(CultureInfo.InvariantCulture,
			"{{\"id\":{0},\"memberCount\":{1},\"anchored\":{2},\"fenced\":{12},\"fast\":{16},\"fastSpeed\":{17:R},\"sphere\":{{\"x\":{3:R},\"y\":{4:R},\"z\":{5:R},\"radius\":{6:R}}},\"maxMemberSpeed\":{7:R},\"pcu\":{10},\"estimatedSnapshotBytes\":{11},\"simLoadUs\":{14},\"clients\":[{8}],\"members\":[{9}],\"seated\":[{15}],\"memberSpheres\":[{13}],\"npc\":[{18}]}}",
			partition.Id, partition.Members.Count, partition.Anchored.ToString().ToLowerInvariant(), partition.Sphere.Center.X,
			partition.Sphere.Center.Y, partition.Sphere.Center.Z, partition.Sphere.Radius, partition.MaxMemberSpeed,
			clientsByPartition.TryGetValue(partition.Id, out string attached) ? attached : string.Empty,
			MemberJson(partition), PcuOf(partition, out int partitionPcu), EstimatedSnapshotBytes(partition, partitionPcu),
			IsStartupFenced(partition.Id).ToString().ToLowerInvariant(), MemberSpheresJson(partition),
			(long)Math.Round(m_simLoadUs.TryGetValue(partition.Id, out double simLoad) ? simLoad : 0.0),
			SeatedJson(partition), partition.Fast.ToString().ToLowerInvariant(), Math.Round(partition.FastSpeed, 1),
			NpcCensusService.RowsJson(partition.Members))));   // #1354: the cluster-wide NPC census
		// The node's load is what says whether it is keeping up, and every consumer of this report - the
		// balancer, the monitor, a handover self-test - wants it alongside the partitions rather than from a
		// second call. threadLoad is Partitioning.md 7.2's metric already built, but NOT a percentage:
		// Game.cs:183 divides Parallel.Scheduler.ReadAndClearExecutionTime() - which
		// PrioritizedScheduler.cs:420 SUMS ACROSS THE WHOLE WORKER POOL - by ONE frame budget. So the
		// scale is "worker-seconds per frame-budget-second x100" and runs to roughly workers x 100:
		// values of 191-700 are correct readings, not corruption. (CPULoad on the line above IS a
		// percentage; it uses FrameTimeTicks, the wall time of one frame.)
		// Headroom is therefore NOT 100 - threadLoad, and the worker count is not known here. Nothing
		// computes headroom from it today - the balancer only compares two nodes on the same scale, which
		// is valid for ordering. See findings/threadload-is-aggregate-worker-time-not-a-percentage.md.
		return string.Format(CultureInfo.InvariantCulture,
			"{{\"entityIdRange\":{0},\"syncDistance\":{1:R},\"margin\":{2:R},\"splitDistance\":{7:R},\"refusedMerges\":{8},\"heldMerges\":{9},\"heldMergePairs\":{10},\"deferredSplits\":{11},\"hysteresisHeld\":{12},\"hysteresisHeldTotal\":{13},\"hysteresisTransferred\":{14},\"hysteresisExpired\":{15},\"hysteresisReleased\":{16},\"simulationRatio\":{5:R},\"threadLoad\":{6:R},\"simLoadSupported\":true,\"partitions\":[{3}],\"evaluatedAt\":{4:R}}}",
			m_rangeIndex(), MyLayers.GetSyncDistance(), Margin, partitions, ClusterRuntime.ClusterMilliseconds(),
			Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio, Sandbox.Game.Multiplayer.Sync.ServerThreadLoadSmooth,
			SplitDistance(MyLayers.GetSyncDistance()), m_membership.RefusedMerges, m_membership.HeldMerges, m_membership.HeldMergePairs,
			m_membership.DeferredSplits.Count, m_membership.HysteresisHeld, m_membership.HysteresisHeldTotal, m_membership.HysteresisTransferred,
			m_membership.HysteresisExpired, m_membership.HysteresisReleased);
	}

	// ---- CLUSTER-SIM-COST: per-partition CPU proxy ------------------------------------------------
	// The balancer's load metric. Two measured inputs, both already accumulated by the engine at
	// nanosecond-scale per-frame cost:
	//   1. MyEntity.SimCostTicks - wall time of the entity's Update*/Simulate calls, charged by the
	//      update orchestrator with chained timestamps.
	//   2. MyPhysicsSeams.StepWallTicks - wall time of the whole physics step (per-cluster timing is
	//      impossible in the parallel Havok path), distributed here over partition members by
	//      active-rigid-body weight. Members-only distribution: unpartitioned entities' physics is
	//      node background and the balancer can only move partitions anyway; the attribution is a
	//      proxy, consistent across nodes, not an accounting identity.
	// Sampled as deltas at most twice a second on the game thread (ReportJson is Invoke'd), smoothed
	// with a ~10 s EMA so import/JIT spikes do not whipsaw the balancer. Unit: microseconds of
	// simulation work per wall-clock second.

	private const double SimLoadMinSampleSeconds = 0.5;
	private const double SimLoadEmaTauSeconds = 10.0;
	private Dictionary<long, long> m_lastSimTicks = new Dictionary<long, long>();
	private readonly Dictionary<ulong, double> m_simLoadUs = new Dictionary<ulong, double>();
	private long m_lastSampleStamp;
	private long m_lastPhysTicks;

	/// <summary>The smoothed measured load of a partition for diag display; 0 until sampled.
	/// Refreshed by ReportJson (the balancer's poll), merely read here.</summary>
	public long SimLoadUs(ulong id)
		=> (long)Math.Round(m_simLoadUs.TryGetValue(id, out double value) ? value : 0.0);

	/// <summary>
	/// Main-thread cost of one SIMULATED second, in microseconds - the figure the per-partition
	/// loads must sum to (T-0120).
	///
	/// <c>Game.CPUTimeSmooth</c> is the smoothed wall time of one frame in ms, measured in
	/// <c>RunSingleFrame</c> around <c>UpdateInternal</c>. A simulated second is 60 frames, so the
	/// cost of a simulated second is that times 60. Expressed per SIMULATED second rather than per
	/// wall second on purpose: a node that cannot keep up stretches wall time, and a per-wall-second
	/// figure would hide exactly the overload the balancer needs to see - it saturates at 1000 ms/s
	/// however far behind the node falls, while this rises without bound.
	///
	/// Returns 0 when it cannot be read, which leaves the previous behaviour (attributed terms only)
	/// rather than inventing a number: a DORMANT node throttles its whole loop to dormant.hz, so
	/// its frames are few and far between and multiplying one frame's cost by 60 would report a
	/// spare as the busiest node in the cluster.
	/// </summary>
	private static double MainThreadUsPerSimSecond()
	{
		if (ClusterRuntime.IsDormant)
			return 0;
		string source = ClusterConfig.Get("balance.unattributedLoad");   // thread | cpu | off
		if (string.Equals(source, "thread", StringComparison.OrdinalIgnoreCase))
		{
			// Sync.ServerThreadLoadSmooth returns MySandboxGame.Static.ThreadLoadSmooth on a
			// server, and that is NOT milliseconds and NOT a percentage:
			//     ThreadLoad = workerSecondsThisFrame / (1/60) * 100  =  workerSeconds * 6000
			// so it runs 0..workers*100 (findings/threadload-is-aggregate-worker-time-not-a-
			// percentage.md). Converting to microseconds of worker time per SIMULATED second:
			//     workerSeconds/frame = TLS / 6000
			//     x 60 frames         = TLS / 100   seconds per simulated second
			//     x 1e6               = TLS * 10000 microseconds
			// I first wrote TLS * 600 by treating it as a percentage of one frame - 16.7x low,
			// which would have quietly made the "thread" arm lose every comparison against "cpu".
			return Math.Max(0, Sandbox.Game.Multiplayer.Sync.ServerThreadLoadSmooth) * 10_000.0;
		}
		if (!string.Equals(source, "cpu", StringComparison.OrdinalIgnoreCase))
			return 0;                       // "off", or anything unrecognised: previous behaviour
		MySandboxGame game = MySandboxGame.Static;
		double msPerFrame = game?.CPUTimeSmooth ?? 0f;
		return msPerFrame > 0 ? msPerFrame * 60.0 * 1000.0 : 0;
	}

	private void SampleSimLoad(IReadOnlyCollection<Partition> evaluated)
	{
		long stamp = Stopwatch.GetTimestamp();
		if (m_lastSampleStamp == 0)
		{
			m_lastSampleStamp = stamp;
			m_lastPhysTicks = MyPhysicsSeams.StepWallTicks;
			return;
		}
		double elapsedSeconds = (stamp - m_lastSampleStamp) / (double)Stopwatch.Frequency;
		if (elapsedSeconds < SimLoadMinSampleSeconds)
			return;
		long physTicks = MyPhysicsSeams.StepWallTicks;
		double physDeltaTicks = physTicks - m_lastPhysTicks;
		// First pass: raw update-tick deltas and physics weights per partition, plus the node-wide
		// active weight the physics share divides by.
		Dictionary<ulong, (double UpdateTicks, int ActiveBodies)> raw =
			new Dictionary<ulong, (double, int)>(evaluated.Count);
		Dictionary<long, long> seenTicks = new Dictionary<long, long>(m_lastSimTicks.Count);
		double totalActiveBodies = 0;
		foreach (Partition partition in evaluated)
		{
			double updateTicks = 0;
			int activeBodies = 0;
			foreach (long member in partition.Members)
			{
				if (!MyEntities.TryGetEntityById(member, out MyEntity entity))
					continue;
				long ticks = entity.SimCostTicks;
				seenTicks[member] = ticks;
				// No previous sample (fresh spawn or a just-imported copy): its counter IS the
				// delta - the entity accumulated everything since it appeared here.
				updateTicks += ticks - (m_lastSimTicks.TryGetValue(member, out long last) ? last : 0);
				if (entity.Physics != null && entity.Physics.IsActive)
					activeBodies++;
			}
			raw[partition.Id] = (updateTicks, activeBodies);
			totalActiveBodies += activeBodies;
		}
		m_lastSimTicks = seenTicks;   // swap = prune of departed entities
		double ticksToUsPerSecond = 1_000_000.0 / Stopwatch.Frequency / elapsedSeconds;
		double alpha = 1.0 - Math.Exp(-elapsedSeconds / SimLoadEmaTauSeconds);

		// T-0120: charge the REST of the game thread, not just the two terms above.
		//
		// THE BUG THIS FIXES. SimCostTicks is written by MyParallelEntityUpdateOrchestrator's
		// dispatch loops, and the plugin had no writer for it, so the value read 0. So `updateTicks`
		// above is identically zero and the only surviving term is the physics share - which is
		// the WHOLE node's physics step handed out pro rata, so the per-partition loads summed to
		// the node's physics step time and NOTHING ELSE, no matter how much content it held.
		// Measured 2026-09-11 23:21, four serving nodes:
		//     1558 entities, sim 0.75  ->  summed load  75 ms/s
		//      327 entities, sim 1.02  ->              143 ms/s
		//       54 entities, sim 1.02  ->              158 ms/s
		//       47 entities, sim 1.00  ->              137 ms/s
		// The metric was ANTI-CORRELATED with content, and the gateway picks the lowest-summed
		// node (DirectGateway.PartitionLoad), so the fullest node advertised itself as the
		// cheapest and the balancer kept feeding it. Positive feedback, not just bad reporting.
		//
		// THE TRUE FIGURE. Game.RunSingleFrame measures FrameTimeTicks around UpdateInternal and
		// smooths it into CPUTimeSmooth (ms of WALL time per frame, main thread). One simulated
		// second is 60 frames, so the main-thread cost of a simulated second is
		// CPUTimeSmooth * 60 ms - which reproduces the arithmetic from sim speed exactly: at
		// sim 0.75 a frame takes 16.67/0.75 = 22.2 ms and 22.2 * 60 = 1333 ms/s against the 75
		// this node was reporting.
		//
		// NOT ThreadLoad: that is worker-pool time summed over the whole pool against one frame
		// budget, so it runs 0..workers*100 and is not main-thread cost
		// (findings/threadload-is-aggregate-worker-time-not-a-percentage.md).
		//
		// The remainder is spread by MEMBER COUNT. Like the physics share this is a proxy, not an
		// accounting identity: it buys a correct per-NODE total, which is what the balancer
		// compares, and it deliberately adds no signal about WHICH partition to move - the
		// physics share still carries that. A node's unattributable background (antenna proxies,
		// the plugin's own timers, voxels, replication) is real cost that the node pays for
		// holding what it holds, and hiding it is what produced the inversion above.
		double attributedUs = 0;
		foreach (KeyValuePair<ulong, (double UpdateTicks, int ActiveBodies)> row in raw)
		{
			double physShare = totalActiveBodies > 0 ? physDeltaTicks * row.Value.ActiveBodies / totalActiveBodies : 0;
			attributedUs += Math.Max(0, (row.Value.UpdateTicks + physShare) * ticksToUsPerSecond);
		}
		Dictionary<ulong, int> memberCounts = evaluated.ToDictionary(x => x.Id, x => x.Members.Count);
		double totalMembers = memberCounts.Values.Sum();
		double unattributedUs = Math.Max(0, MainThreadUsPerSimSecond() - attributedUs);

		foreach (KeyValuePair<ulong, (double UpdateTicks, int ActiveBodies)> row in raw)
		{
			double physShare = totalActiveBodies > 0 ? physDeltaTicks * row.Value.ActiveBodies / totalActiveBodies : 0;
			// A rewound counter (entity replaced under the same id by an import) yields a negative
			// delta; clamp instead of poisoning the EMA.
			double sample = Math.Max(0, (row.Value.UpdateTicks + physShare) * ticksToUsPerSecond);
			if (unattributedUs > 0)
				sample += totalMembers > 0
					? unattributedUs * memberCounts[row.Key] / totalMembers
					: unattributedUs / raw.Count;
			m_simLoadUs[row.Key] = m_simLoadUs.TryGetValue(row.Key, out double ema)
				? ema + alpha * (sample - ema) : sample;
		}
		foreach (ulong stale in m_simLoadUs.Keys.Where(id => !raw.ContainsKey(id)).ToArray())
			m_simLoadUs.Remove(stale);
		m_lastSampleStamp = stamp;
		m_lastPhysTicks = physTicks;
	}

	/// <summary>
	/// A partition's build cost, summed over its grids. Partitioning.md 7.2 uses it to tie-break the load
	/// metric and to prefer moving cheap partitions, where it also stands in for "small snapshot" - PCU
	/// tracks block count, and block count is what the freeze has to serialize.
	/// </summary>
	private static int Pcu(Partition partition)
	{
		int total = 0;
		foreach (long member in partition.Members)
			if (MyEntities.TryGetEntityById(member, out MyEntity entity) && entity is MyCubeGrid grid)
				total += grid.BlocksPCU;
		return total;
	}

	/// <summary>
	/// The size input of the Partitioning.md 7.5 cost model: what a freeze would have to serialize,
	/// linear in PCU and member count. The member term is what makes a character-only partition
	/// weigh something - a character carries no PCU, and pricing it at zero made it invisible to
	/// every direction decision. §7.5's exception class needs no term of its own: a seated
	/// character serializes inside its grid's OB and is not a member, and an on-foot character
	/// near a grid is merged into that grid's partition by membership, so pricing members prices
	/// exactly the sides the partitioner produces.
	/// ponytail: bootstrap constants until the first P3-CST-01 calibration fits them from
	/// HandoverTelemetry ground truth.
	/// </summary>
	public static long EstimatedSnapshotBytes(Partition partition) => EstimatedSnapshotBytes(partition, Pcu(partition));

	/// <summary>The same estimate for a caller that already walked the members for their PCU.</summary>
	public static long EstimatedSnapshotBytes(Partition partition, int pcu)
		=> 400L * pcu + 8_000L * partition.Members.Count + 2_000;

	/// <summary>Returns the partition's PCU and also hands it back through <paramref name="pcu"/>.</summary>
	private static int PcuOf(Partition partition, out int pcu) => pcu = Pcu(partition);

	/// <summary>
	/// What a partition actually holds: the member entity ids with their type and display name. The counts
	/// alone answer "how big is it" but never "what is in it", which is the question every partition report
	/// raises. A member whose entity is gone reports type "missing".
	/// </summary>
	/// <summary>
	/// T-0226: the characters seated in the member grids' cockpits. They are not partition members (a seated
	/// character is part of its cockpit's hierarchy), so the recovery census, which screened members only,
	/// let a peer recover a stale standalone copy of a pilot who was live in a ship here: Pilot-020 on w32
	/// 2026-09-23, P176085 recovered on node-1 08:21:47 while seated in a node-4 grid, every handover of
	/// either copy then refused ("Duplicate incarnation", builders differ).
	/// </summary>
	private static string SeatedJson(Partition partition)
	{
		List<long> seated = null;
		foreach (long member in partition.Members)
		{
			if (!MyEntities.TryGetEntityById(member, out MyEntity entity) || entity is not MyCubeGrid grid) continue;
			foreach (MyCockpit cockpit in grid.OccupiedBlocks)
				if (cockpit.Pilot != null) (seated ??= new List<long>()).Add(cockpit.Pilot.EntityId);
		}
		return seated == null ? string.Empty : string.Join(',', seated);
	}

	private static string MemberJson(Partition partition)
		=> string.Join(',', partition.Members.OrderBy(member => member).Select(member =>
		{
			MyEntities.TryGetEntityById(member, out MyEntity entity);
			string name = entity?.DisplayName;
			if (string.IsNullOrEmpty(name)) name = entity?.Name;
			long identity = (entity as MyCharacter)?.ControllerInfo?.ControllingIdentityId ?? 0;
			return string.Format(CultureInfo.InvariantCulture, "{{\"id\":{0},\"type\":{1},\"name\":{2},\"identityId\":{3}}}",
				member, System.Text.Json.JsonSerializer.Serialize(entity?.GetType().Name ?? "missing"),
				System.Text.Json.JsonSerializer.Serialize(name ?? string.Empty), identity);
		}));

	/// <summary>
	/// Plan/EntityGapPlacement.md: per-member bounding spheres with speed, [x,y,z,radius,speed]
	/// rows, so the gateway can place by the TRUE minimum entity-pair gap instead of the partition
	/// bounding sphere (whose inflation on a spread partition manufactured a 443k false-atomic
	/// component out of content 10.9 km apart, w23 2026-08-24). A member whose entity the census
	/// cannot see yet (pending, mid-init) has no position, so the partition's own bounding sphere is
	/// appended as a cover row - the unknown stays conservatively inside the geometry.
	/// </summary>
	private static string MemberSpheresJson(Partition partition)
	{
		List<string> rows = new List<string>(partition.Members.Count);
		int missing = 0;
		foreach (long member in partition.Members)
		{
			if (!MyEntities.TryGetEntityById(member, out MyEntity entity) || entity.PositionComp == null)
			{
				missing++;
				continue;
			}
			BoundingSphereD sphere = BoundingSphereD.CreateFromBoundingBox(entity.PositionComp.WorldAABB);
			// T-0141: the velocity VECTOR rides along ([..., speed, vx, vy, vz]) so the gateway can
			// shorten a pair's gap by its CLOSING speed only. The scalar speed alone linked a ship
			// flying AWAY from a cluster to it for ~1.8 km, and 73% of co-location triggers were
			// such lookahead-only links that chained the pilots into one 15-22-partition component.
			// T-0292: a fast partition reports no velocity, so a warp hop never stretches anyone's
			// closing-speed lookahead; the registry keeps it out of co-location anyway.
			Vector3 velocity = partition.Fast ? Vector3.Zero : entity.Physics?.LinearVelocity ?? Vector3.Zero;
			rows.Add(string.Format(CultureInfo.InvariantCulture, "[{0:R},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R}]",
				sphere.Center.X, sphere.Center.Y, sphere.Center.Z, sphere.Radius,
				velocity.Length(), velocity.X, velocity.Y, velocity.Z));
		}
		if (missing != 0)
			rows.Add(string.Format(CultureInfo.InvariantCulture, "[{0:R},{1:R},{2:R},{3:R},{4:R}]",
				partition.Sphere.Center.X, partition.Sphere.Center.Y, partition.Sphere.Center.Z,
				partition.Sphere.Radius, partition.MaxMemberSpeed));
		return string.Join(',', rows);
	}

	public ClientPartition[] ClientPartitions(IReadOnlyCollection<Partition> partitions)
	{
		Dictionary<long, ulong> owners = partitions.SelectMany(partition => partition.Members.Select(member => new KeyValuePair<long, ulong>(member, partition.Id)))
			.ToDictionary(x => x.Key, x => x.Value);
		if (MySession.Static == null)
			return Array.Empty<ClientPartition>();
		return MySession.Static.Players.GetOnlinePlayers()
			.Where(player => player.IsRealPlayer)
			.Select(player =>
			{
				long entity = player.Controller?.ControlledEntity?.Entity?.GetTopMostParent()?.EntityId ?? 0;
				owners.TryGetValue(entity, out ulong partition);
				return new ClientPartition(player.Id.SteamId, partition, entity);
			})
			.ToArray();
	}

	public IReadOnlyDictionary<long, ulong> EntityPartitions()
		=> Evaluate().SelectMany(partition => partition.Members.Select(member => new KeyValuePair<long, ulong>(member, partition.Id)))
			.ToDictionary(x => x.Key, x => x.Value);

	private bool TryMerge(Dictionary<long, MyEntity> entities)
	{
		Partition[] partitions = m_partitions.Values.ToArray();
		for (int i = 0; i < partitions.Length; i++)
		{
			for (int j = i + 1; j < partitions.Length; j++)
			{
				Partition a = partitions[i];
				Partition b = partitions[j];
				double gap = Vector3D.Distance(a.Sphere.Center, b.Sphere.Center) - a.Sphere.Radius - b.Sphere.Radius;
				if (gap >= MyLayers.GetSyncDistance() + Margin && !AtomicLink(a, b, entities))
				{
					continue;
				}
				Partition keep = a.Members.Count > b.Members.Count || a.Members.Count == b.Members.Count && a.Id < b.Id ? a : b;
				Partition remove = keep == a ? b : a;
				keep.Members.UnionWith(remove.Members);
				m_partitions.Remove(remove.Id);
				return true;
			}
		}
		return false;
	}

	private static bool AtomicLink(Partition a, Partition b, Dictionary<long, MyEntity> entities) =>
		a.Members.Any(left => b.Members.Any(right => Atomic(entities[left], entities[right])));

	private static List<HashSet<long>> Components(IEnumerable<long> ids, Dictionary<long, MyEntity> entities, double distance)
	{
		HashSet<long> remaining = ids.ToHashSet();
		List<HashSet<long>> result = new List<HashSet<long>>();
		while (remaining.Count != 0)
		{
			long seed = remaining.First();
			remaining.Remove(seed);
			HashSet<long> component = new HashSet<long> { seed };
			Queue<long> queue = new Queue<long>();
			queue.Enqueue(seed);
			while (queue.Count != 0)
			{
				long current = queue.Dequeue();
				foreach (long candidate in remaining.Where(candidate => Atomic(entities[current], entities[candidate]) || Gap(entities[current], entities[candidate]) <= distance).ToArray())
				{
					remaining.Remove(candidate);
					component.Add(candidate);
					queue.Enqueue(candidate);
				}
			}
			result.Add(component);
		}
		return result;
	}

	/// <summary>
	/// Atomic-group label of every member: the smallest entity id in its connected component over
	/// the three <see cref="MyCubeGridGroups"/> trees (physical, logical, mechanical).
	/// </summary>
	/// <remarks>
	/// Linear in the member count. The previous labelling was the pairwise
	/// <c>Keys.Where(y => Atomic(x, y)).Min()</c> over every member - three group lookups per
	/// pair, so a wreck-field node holding 412 members paid ~510k lookups per sweep, and the
	/// sweep runs several times per frame (node-5 on 2026-08-22: 33% of the game thread in that
	/// one line, plus a ~200 ms hitch every second when the character reporter re-swept once per
	/// character). It was also not transitive - A~B physically and B~C mechanically labelled C
	/// apart from A although <see cref="PartitionMembership"/> treats the label as an equivalence -
	/// so the component labelling is both cheaper and what the consumer assumes.
	/// Union by smaller root keeps every root the minimum of its component, which is the
	/// same label the old scan produced for the transitive cases.
	/// </remarks>
	private static Dictionary<long, long> AtomicGroups(Dictionary<long, MyEntity> entities)
	{
		Dictionary<long, long> parent = new Dictionary<long, long>(entities.Count);
		foreach (long id in entities.Keys) parent[id] = id;
		long Find(long id)
		{
			while (parent[id] != id)
			{
				parent[id] = parent[parent[id]];
				id = parent[id];
			}
			return id;
		}
		void Union(long a, long b)
		{
			a = Find(a);
			b = Find(b);
			if (a == b) return;
			if (a < b) parent[b] = a;
			else parent[a] = b;
		}
		MyCubeGridGroups trees = MyCubeGridGroups.Static;
		if (trees != null)
		{
			Dictionary<object, long> firstInGroup = new Dictionary<object, long>(ReferenceEqualityComparer.Instance);
			void Link(long id, object group)
			{
				if (group == null) return;
				if (firstInGroup.TryGetValue(group, out long seen)) Union(id, seen);
				else firstInGroup[group] = id;
			}
			foreach (KeyValuePair<long, MyEntity> entry in entities)
			{
				if (entry.Value is not MyCubeGrid grid) continue;
				Link(entry.Key, trees.Physical.GetGroup(grid));
				Link(entry.Key, trees.Logical.GetGroup(grid));
				Link(entry.Key, trees.Mechanical.GetGroup(grid));
			}
		}
		Dictionary<long, long> groups = new Dictionary<long, long>(entities.Count);
		foreach (long id in entities.Keys) groups[id] = Find(id);
		return groups;
	}

	private static bool Atomic(MyEntity a, MyEntity b)
	{
		if (a is not MyCubeGrid left || b is not MyCubeGrid right || MyCubeGridGroups.Static == null)
		{
			return false;
		}
		return MyCubeGridGroups.Static.Physical.HasSameGroup(left, right)
			|| MyCubeGridGroups.Static.Logical.HasSameGroup(left, right)
			|| MyCubeGridGroups.Static.Mechanical.HasSameGroup(left, right);
	}

	private static double Gap(MyEntity a, MyEntity b)
	{
		BoundingBoxD left = a.PositionComp.WorldAABB;
		BoundingBoxD right = b.PositionComp.WorldAABB;
		return left.Distance(ref right);
	}

	private void Add(ulong id, HashSet<long> members, Dictionary<long, MyEntity> entities, bool hasPending = false)
	{
		Partition partition = new Partition { Id = id, Members = members, HasPending = hasPending };
		foreach (long member in members)
			if (m_fast.IsFast(member))
			{
				partition.Fast = true;
				partition.FastSpeed = Math.Max(partition.FastSpeed, m_fast.SpeedOf(member));
			}
		m_partitions.Add(id, partition);
		Update(partition, entities);
	}

	private void Recompute(Dictionary<long, MyEntity> entities)
	{
		foreach (Partition partition in m_partitions.Values)
		{
			Update(partition, entities);
		}
	}

	private static void Update(Partition partition, Dictionary<long, MyEntity> entities)
	{
		// A recovery cover can legitimately resolve to zero live entities; Max over the empty set
		// threw here and livelocked the claim (release, re-claim, throw again) - see P1-SAV-07.
		if (partition.Members.Count == 0)
		{
			partition.Sphere = new BoundingSphereD(Vector3D.Zero, 0);
			partition.Anchored = false;
			partition.MaxMemberSpeed = 0;
			return;
		}
		// Pending members (adopted from a save, entity still initializing) have no live entity to
		// measure; the sphere and speed cover what is actually in the world.
		MyEntity[] live = partition.Members
			.Select(id => entities.TryGetValue(id, out MyEntity entity) ? entity : null)
			.Where(entity => entity != null).ToArray();
		if (live.Length == 0)
		{
			partition.Sphere = new BoundingSphereD(Vector3D.Zero, 0);
			partition.Anchored = false;
			partition.MaxMemberSpeed = 0;
			return;
		}
		BoundingBoxD box = BoundingBoxD.CreateInvalid();
		foreach (MyEntity entity in live)
		{
			box.Include(entity.PositionComp.WorldAABB);
		}
		partition.Sphere = BoundingSphereD.CreateFromBoundingBox(box);
		partition.Anchored = live.Any(entity => entity is MyCubeGrid grid && grid.IsStatic);
		partition.MaxMemberSpeed = live.Max(entity => entity.Physics?.LinearVelocity.Length() ?? 0);
	}

	// Local fallback only (the registry allocator installed by SetAllocator is primary): the node's
	// current EntityId range index is unique cluster-wide, so ids minted under it cannot collide.
	private ulong NewId() => ((ulong)m_rangeIndex() << EntityIdAllocator.RangeBits) | m_nextId++;

	private static bool IsMember(MyEntity entity) => entity.Save && !entity.MarkedForClose
		&& entity is MyCubeGrid or MyCharacter or MyFloatingObject or MyInventoryBagEntity or MyMeteor or MyMissile;
}
