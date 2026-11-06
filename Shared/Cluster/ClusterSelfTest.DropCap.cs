using System;
using System.Text;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// #1443 drop cap, the pure parts: the census answer's drop rows parse (flag, offline, deadline), the tally counts online
	/// and offline drops until their deadline, holds a drop the census stopped listing for 30 s, counts a dispatched drop as
	/// pending until a census pass 30 s later, and the drops.maxActive knob.
	/// </summary>
	private static void DropCapRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("drop-cap: " + message); }
		byte[] answer = Encoding.UTF8.GetBytes("{\"ageSeconds\":2,\"online\":0,\"offline\":0,\"grids\":["
			+ "{\"id\":144115188075855873,\"x\":0,\"y\":0,\"z\":0,\"u\":10,\"k\":\"unowned\",\"d\":true,\"e\":1761200000},"
			+ "{\"id\":2,\"x\":0,\"y\":0,\"z\":0,\"u\":10,\"k\":\"unowned\",\"o\":true,\"d\":true},"
			+ "{\"id\":3,\"x\":0,\"y\":0,\"z\":0,\"u\":10,\"k\":\"unowned\"},"
			+ "{\"id\":4,\"x\":0,\"y\":0,\"z\":0,\"u\":10,\"k\":\"npc\"}]}");
		NpcCensusService.Snapshot snapshot = NpcCensusService.Parse(answer, answeredAt: 1000);
		Check(snapshot.Drops is [{ Id: 144115188075855873, Offline: false, Expires: 1761200000 }, { Id: 2, Offline: true, Expires: 0 }],
			"the drop rows parse: exact 64-bit id, offline flag, deadline or none; plain unowned and NPC rows are no drops");

		const long Now = 1761200000 - 60;
		var seen = new[]
		{
			new DropContainerCap.Seen(100_000, false, 0), new DropContainerCap.Seen(100_000, true, 0),
			new DropContainerCap.Seen(100_000, true, Now + 1), new DropContainerCap.Seen(100_000, true, Now),
			new DropContainerCap.Seen(100_000 - 30_001, false, 0),
		};
		DropContainerCap.Count count = DropContainerCap.Tally(seen, Array.Empty<double>(), 100_000, 98_000, Now);
		Check(count == new DropContainerCap.Count(3, 1, 2, 0),
			"online and offline drops count until their deadline; one past it, and one unseen for more than 30 s, do not: " + count);
		count = DropContainerCap.Tally(seen, Array.Empty<double>(), 130_000, 128_000, Now);
		Check(count.Active == 3, "a drop last seen exactly 30 s ago still counts");
		count = DropContainerCap.Tally(Array.Empty<DropContainerCap.Seen>(), new double[] { 60_000, 68_001, 99_000 }, 100_000, 98_000, Now);
		Check(count == new DropContainerCap.Count(2, 0, 0, 2),
			"a dispatched drop is pending until a census pass 30 s after it (pass 98 s: 68.001 s and 99 s pending, 60 s not): " + count);

		var known = new System.Collections.Generic.Dictionary<long, DropContainerCap.Seen> { [1] = new(50_000, false, 0) };
		var pending = new System.Collections.Generic.List<double> { 95_000, 90_000, 99_000 };
		DropContainerCap.Merge(known, pending, new NpcCensusService.DropRow[] { new(1, false, 0), new(2, false, 0) }, 100_000, 97_000);
		Check(known.Count == 2 && known[1].At == 100_000 && pending.Count == 2 && pending.Contains(95_000) && pending.Contains(99_000),
			"a newly listed drop takes the oldest spawn dispatched before the pass off pending; a known one takes none");
		DropContainerCap.Merge(known, pending, new NpcCensusService.DropRow[] { new(3, true, 0) }, 101_000, 94_000);
		Check(pending.Count == 2 && known[3].Offline, "a spawn dispatched after the pass stays pending");

		Check(ClusterConfig.DefaultOf("drops.maxActive") == "5", "drops.maxActive defaults to 5");
		Check(ClusterConfig.DefaultOf("drops.lifetimeSeconds") == "0", "drops.lifetimeSeconds defaults to 0 (vanilla's drop lifetime)");
		Check(ClusterConfig.Normalize("drops.maxActive", "10") == "10" && ClusterConfig.Normalize("drops.maxActive", "0") == "0",
			"drops.maxActive takes a count, 0 = no cap");
		bool refused = false;
		try { ClusterConfig.Normalize("drops.maxActive", "-1"); } catch (ArgumentException) { refused = true; }
		Check(refused, "drops.maxActive refuses a negative count");
	}
}
