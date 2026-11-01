using System;
using System.Collections.Concurrent;
using System.Threading;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Network;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// CLUSTER-ANTENNA-SCAN-THROTTLE on <see cref="MyDataReceiver.UpdateBroadcastersInRange"/>, gated to
/// PROXY antennas only, banded by distance from this node's own content (T-0138).
///
/// THE COST, measured on w31 2026-09-13. `bin/game-thread-profile` put
/// <c>UpdateBroadcastersInRange</c> at 56.31% of node wall time - with 81% of the world's antennas
/// already DISABLED, so it is not a "too many antennas" problem. The amplification is ours: the cluster
/// replicates every remote antenna as a <see cref="MyProxyAntenna"/> and
/// <c>MyProxyAntenna.UpdateAfterSimulation10</c> runs the receiver's full rescan. node-1 held 1494
/// proxies each rescanning 6x/s - ~9,000 sphere queries a second for antennas the node does not own,
/// which is why a HOT SPARE owning ZERO entities still carried the full 1893-receiver graph.
///
/// WHY THROTTLING AND NOT REMOVAL. Giving proxies no receiver looks like the obvious fix and is wrong:
/// <c>MyAntennaSystem.GetAllRelayedBroadcasters</c> recurses through
/// <c>item.Receiver.BroadcastersInRange</c>, so proxy receivers ARE the multi-hop relay graph and
/// dropping them would silently cut relay paths. What is wasted is the FREQUENCY - a from-scratch
/// rescan six times a second for parked ships that have not moved since the world was seeded.
///
/// THE BANDS (Viktor 2026-09-13). A proxy far from anything this node simulates cannot affect
/// anything here soon, so it is scanned less often. Distance is measured from the proxy to the
/// nearest SURFACE of this node's partition spheres, in multiples of the world SyncDistance D:
///
///     within  nearBand  (10 x D)  ->  nearMultiplier     6   -> every  60 frames (~1 s)
///     within  farBand  (100 x D)  ->  farMultiplier     30   -> every 300 frames (~5 s)
///     beyond  farBand             ->  distantMultiplier 60   -> every 600 frames (~10 s)
///
/// Every multiplier is a config knob and 1 means vanilla, so the whole mechanism reverts to stock
/// behaviour with three PUTs and no redeploy. The base cadence is vanilla's own 10 frames
/// (UpdateAfterSimulation10), so a multiplier of N means "every N-th call that would have happened".
///
/// WHAT IS NEVER THROTTLED. Real antennas - the ones on grids this node owns - return early and keep
/// vanilla timing exactly, so local play, the pilots' own ships and anything a player interacts with
/// are bit-identical to stock. A proxy also always scans on its FIRST call, so a freshly installed
/// proxy relays immediately instead of waiting out a band interval.
///
/// THE COST OF THE THROTTLE, stated plainly: a proxy's relay set can be one band interval stale, so a
/// MOVING ship far from this node updates its relay neighbours up to ten seconds late. Nothing in the
/// cluster reads proxy receivers except that relay traversal and DiagSnapshot's counter, and a
/// dedicated server draws no HUD. If a moving-ship relay ever needs to be fresher, lower
/// distantMultiplier rather than widening the bands - the far band is where the population is.
/// </summary>
[HarmonyPatch(typeof(MyDataReceiver))]
internal static class MyProxyAntennaScanPatch
{
	private static readonly ConcurrentDictionary<long, long> m_lastScan = new ConcurrentDictionary<long, long>();
	/// <summary>Enclosing spheres of this node's partitions, republished by PartitionManager.Evaluate.</summary>
	private static BoundingSphereD[] m_localSpheres = new BoundingSphereD[0];

	/// <summary>
	/// The band edges and multipliers as of one frame - an immutable snapshot swapped by reference,
	/// the same trick <see cref="ClusterConfig"/> itself uses, so a reader sees one whole set of
	/// values and never a half-updated one.
	///
	/// WHY IT IS CACHED AT ALL (T-0140). The first version read all three knobs through
	/// <c>ClusterConfig.GetInt</c> INSIDE the prefix, which is a dictionary lookup plus an
	/// <c>int.Parse</c> each, per proxy per call: ~1500 proxies x 6 calls/s = ~27,000 parses a
	/// second on the game thread, for values that change perhaps once a day. Once per frame is
	/// three. A band snapshot may be one frame stale after a live config PUT, which is harmless -
	/// the knobs control a scan cadence measured in seconds.
	/// </summary>
	private sealed record Bands(double NearEdge, double FarEdge, int NearMul, int FarMul, int DistantMul)
	{
		/// <summary>Smallest interval any band can yield, so "not due in ANY band" is decidable cheaply.</summary>
		internal int MinInterval => 10 * Math.Max(1, Math.Min(NearMul, Math.Min(FarMul, DistantMul)));
	}

	private static Bands m_bands;
	private static long m_bandsFrame = long.MinValue;

	internal static long ScannedNear, ScannedFar, ScannedDistant, Skipped;

	internal static void PublishLocalSpheres(BoundingSphereD[] spheres)
		=> Volatile.Write(ref m_localSpheres, spheres ?? new BoundingSphereD[0]);

	/// <summary>True when this node holds no partitions - the World Authority always, a dormant
	/// hot spare while it waits. Such a node must not throttle: T-0140.</summary>
	internal static bool OwnsNothing => Volatile.Read(ref m_localSpheres).Length == 0 && !KeepThrottleWhenEmpty;

	/// <summary>T-0193: set once this node has taken a partition offline. A regular node whose content all
	/// went offline is not a spare waiting to become joinable - it is an emptied node, and without the
	/// throttle it would pay the full T-0138 scan cost for every proxy in the cluster (every proxy then
	/// lands in the distant band, since the node has no spheres to be near).</summary>
	internal static volatile bool KeepThrottleWhenEmpty;

	internal static void Reset()
	{
		m_lastScan.Clear();
		Volatile.Write(ref m_localSpheres, new BoundingSphereD[0]);
		Volatile.Write(ref m_bands, null);
		Volatile.Write(ref m_bandsFrame, long.MinValue);
		ScannedNear = ScannedFar = ScannedDistant = Skipped = 0;
	}

	private static Bands BandsFor(long frame)
	{
		Bands cached = Volatile.Read(ref m_bands);
		if (cached != null && Volatile.Read(ref m_bandsFrame) == frame) return cached;
		double sync = MyLayersHelper.SyncDistance();
		Bands fresh = new Bands(
			sync * ClusterConfig.GetInt("antenna.proxyScanNearBandSyncDistances"),
			sync * ClusterConfig.GetInt("antenna.proxyScanFarBandSyncDistances"),
			ClusterConfig.GetInt("antenna.proxyScanNearMultiplier"),
			ClusterConfig.GetInt("antenna.proxyScanFarMultiplier"),
			ClusterConfig.GetInt("antenna.proxyScanDistantMultiplier"));
		// Two threads racing here compute the SAME values and the loser's write is harmless.
		Volatile.Write(ref m_bands, fresh);
		Volatile.Write(ref m_bandsFrame, frame);
		return fresh;
	}

	/// <summary>Metres from <paramref name="point"/> to the nearest local partition SURFACE, 0 inside one.</summary>
	private static double DistanceToLocalContent(Vector3D point)
	{
		BoundingSphereD[] spheres = Volatile.Read(ref m_localSpheres);
		// T-0140 ROOT CAUSE. This used to return PositiveInfinity here - "node owns nothing:
		// everything is distant" - and that is exactly backwards for the two roles that own
		// nothing. The WORLD AUTHORITY holds no partitions ever (measured: wa-1 reports
		// partitions 0 / entities 0 for its whole life), and a DORMANT HOT SPARE holds none
		// either, yet both compile and run this patch - WorldAuthorityPlugin imports the whole
		// of Shared. So on wa-1 every proxy fell into the distant band at the 60x multiplier,
		// i.e. a receiver rescan every 600 frames = 10 SECONDS.
		//
		// The WA is the node that brokers player admission, and admission walks the relay graph
		// those receivers ARE. On magnetar-cluster-3a79fe3 (this patch's banding commit) every
		// player admission timed out: node -> WA commands took 20.0s from every node, every
		// time, against 0.131s and 0.048s on 74b95d1 - and wa-1.log had no exceptions, empty
		// queues and zero publish latency, because nothing was failing. It was waiting.
		//
		// A node with no content has no basis to call anything far, and the node that owns
		// nothing is the one that must be MOST current. No local content therefore means NO
		// throttling - vanilla timing, which is precisely the 74b95d1 behaviour that was
		// measured joinable. Enforced in the prefix (see OwnsNothing below) rather than by
		// returning 0 here: 0 would land in the NEAR band and still throttle 6x.
		if (spheres.Length == 0) return double.PositiveInfinity;
		double best = double.PositiveInfinity;
		foreach (BoundingSphereD sphere in spheres)
		{
			double gap = Vector3D.Distance(point, sphere.Center) - sphere.Radius;
			if (gap < best) best = gap;
			if (best <= 0) return 0;
		}
		return best < 0 ? 0 : best;
	}

	/// <summary>Interval in frames for a proxy at <paramref name="distance"/> metres, and which band it is.</summary>
	private static int IntervalFor(Bands bands, double distance, out int band)
	{
		int multiplier;
		if (distance <= bands.NearEdge) { multiplier = bands.NearMul; band = 0; }
		else if (distance <= bands.FarEdge) { multiplier = bands.FarMul; band = 1; }
		else { multiplier = bands.DistantMul; band = 2; }
		if (multiplier < 1) multiplier = 1;
		return 10 * multiplier;      // 10 = vanilla's own UpdateAfterSimulation10 cadence
	}

	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyDataReceiver.UpdateBroadcastersInRange))]
	private static bool UpdateBroadcastersInRangePrefix(MyDataReceiver __instance)
	{
		if (!ClusterRuntime.IsClustered) return true;
		if (__instance?.Entity is not MyProxyAntenna proxy || proxy.Closed || proxy.MarkedForClose) return true;

		// T-0140. A node holding NO partitions does not throttle at all. See DistanceToLocalContent
		// for the full account: the World Authority and a dormant hot spare both own nothing and
		// both run this patch, and banding them as "distant" gave wa-1 a 10-second receiver rescan,
		// which is what made every player admission time out on magnetar-cluster-3a79fe3. Cheap
		// test, and it sits after the proxy check because IsClustered/proxy are cheaper still.
		if (OwnsNothing) return true;

		long frame = (long)(MySession.Static?.GameplayFrameCounter ?? 0);
		long id = proxy.EntityId;

		// T-0140: everything below runs on the GAME THREAD, ~1500 proxies x 6 calls/s per node, so
		// the order of the tests is the whole performance story and the cheapest discriminator has
		// to come first. There is no lock: the only shared mutable state is a ConcurrentDictionary
		// of last-scan frames and four counters, none of which needs a consistent view across keys.
		if (!m_lastScan.TryGetValue(id, out long last))
		{
			m_lastScan[id] = frame;                     // first sight: scan now, band it next time
			return true;
		}

		// Not due in ANY band: skip without pricing the distance. Sound because every band's
		// interval is at least MinInterval, so `frame - last < MinInterval - 10` implies
		// `frame - last < interval - 10` for each of them, which is exactly what the full test
		// below rejects on. Verified by brute force over 80 multiplier combinations x 2800
		// decisions with per-frame randomised distances: zero divergence from the un-hoisted form.
		//
		// Worth only ~11% of the distance calls at the 6/30/60 defaults, NOT the large factor it
		// looks like: `last` advances only on an actual scan, so a distant proxy on a 600-frame
		// interval sits far past this threshold for most of its cycle and the test rarely fires.
		// The cheap win here is the config caching above (1500x fewer parses per frame); the
		// distance loop is left deliberately intact so this build tests ONE thing. Making it
		// cheaper needs a per-proxy remembered interval, which delays a band change for a MOVING
		// proxy and is therefore a behaviour change, not a refactor - a separate round.
		Bands bands = BandsFor(frame);
		if (frame - last < bands.MinInterval - 10)
		{
			Interlocked.Increment(ref Skipped);
			return false;
		}

		int interval = IntervalFor(bands, DistanceToLocalContent(proxy.PositionComp.GetPosition()), out int band);
		if (interval <= 10) { m_lastScan[id] = frame; return true; }   // multiplier 1: vanilla
		// Stagger by a per-id PHASE, not by "every Nth call": a burst of proxies installed on
		// the same tick would otherwise stay in lockstep for ever and rescan together, and that
		// spike is worse than the flat cost being removed. Calls arrive every 10 frames, so a
		// window of 10 fires each proxy exactly once per interval.
		long phase = (long)((ulong)id % (ulong)interval);
		if ((frame - phase) % interval >= 10 || frame - last < interval - 10)
		{
			Interlocked.Increment(ref Skipped);
			return false;
		}
		m_lastScan[id] = frame;
		if (band == 0) Interlocked.Increment(ref ScannedNear);
		else if (band == 1) Interlocked.Increment(ref ScannedFar);
		else Interlocked.Increment(ref ScannedDistant);
		return true;
	}
}

/// <summary>SyncDistance without dragging MyLayers into every call site, and cached per pass.</summary>
internal static class MyLayersHelper
{
	private static double m_cached;
	internal static double SyncDistance()
	{
		if (m_cached <= 0) m_cached = MyLayers.GetSyncDistance();
		return m_cached;
	}
}
