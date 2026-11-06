using System;
using System.Collections.Generic;
using System.Linq;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1150: one bad respawn point no longer costs a node its whole list at the World Authority. Oxygen outside 0..1
	/// and a long name are repaired, unusable points and duplicates are dropped, the rest are kept; a broken envelope is
	/// still refused. Each (point, check) is logged once while it lasts, again if it comes back.
	/// </summary>
	private static void RespawnRegistryRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("respawn-registry: " + message); }
		ClusterRespawnPoint Point(long id, float oxygen = 0.5f) =>
			new ClusterRespawnPoint("node-1", id, 900 + id, 7, 2, "Medical Room " + id, 144115188075855873, 1, true, false, true, oxygen, 1, 2, 3);

		// The live payload: a medbay in an overfilled room read just above 1, and the WA refused all of the node's points.
		ClusterRespawnPoint good = Point(1), over = Point(2, 1.0000001f), far = Point(3, 1.75f), negative = Point(4, -0.25f);
		var problems = new List<RespawnPointProblem>();
		ClusterRespawnSnapshot read = RespawnRegistryCodec.Read(RespawnRegistryCodec.Write(new[] { good, over, far, negative }), problems);
		Check(read.Points.Length == 4 && read.Points[0] == good, "the good point is kept as it was");
		Check(read.Points[1].OxygenLevel == 1f && read.Points[2].OxygenLevel == 1f && read.Points[3].OxygenLevel == 0f,
			"oxygen outside 0..1 is clamped: " + string.Join(",", read.Points.Select(x => x.OxygenLevel)));
		Check(read.Points.Skip(1).All(x => x with { OxygenLevel = 0.5f } == Point(x.RespawnEntityId)), "a clamped point keeps every other field");
		Check(problems.Count == 3 && problems.All(x => !x.Dropped && x.Check == "OxygenLevel outside 0..1")
			&& problems.Select(x => x.RespawnEntityId).SequenceEqual(new long[] { 2, 3, 4 }), "one repair per clamped point");

		problems.Clear();
		ClusterRespawnPoint longName = Point(5) with { Name = new string('x', 3000) };
		read = RespawnRegistryCodec.Read(RespawnRegistryCodec.Write(new[]
		{
			good, longName, Point(6) with { Generation = 0 }, Point(7) with { PartitionId = 0 }, Point(8) with { ShareMode = 3 },
			Point(9) with { GridEntityId = 0 }, Point(10) with { OwnerIdentityId = -1 }, Point(1) with { Name = "duplicate" },
		}), problems);
		Check(read.Points.Select(x => x.RespawnEntityId).SequenceEqual(new long[] { 1, 5 }), "unusable points and the duplicate are dropped, the rest kept: "
			+ string.Join(",", read.Points.Select(x => x.RespawnEntityId)));
		Check(read.Points[0] == good && read.Points[1].Name.Length == RespawnRegistryCodec.MaxNameLength, "the first of a duplicate id is kept, a long name truncated");
		Check(problems.Count(x => x.Dropped) == 6 && problems.Single(x => !x.Dropped).Check == "Name length"
			&& problems.Single(x => x.Check == "duplicate RespawnEntityId").RespawnEntityId == 1, "one problem per repaired or dropped point");

		// Capture: a non-finite oxygen level becomes 0, and the point then serializes (JSON has no NaN).
		ClusterRespawnPoint nan = RespawnRegistryCodec.Sanitize(Point(11, float.NaN), out RespawnPointProblem? nanProblem);
		Check(nan != null && nan.OxygenLevel == 0f && nanProblem?.Dropped == false, "NaN oxygen is repaired to 0");
		Check(RespawnRegistryCodec.Write(new[] { nan }).Contains("\"OxygenLevel\":0"), "the repaired point serializes");
		Check(RespawnRegistryCodec.Sanitize(Point(12) with { X = double.PositiveInfinity }, out RespawnPointProblem? position) == null
			&& position?.Check == "position invalid", "a non-finite position is dropped");
		Check(ReferenceEquals(RespawnRegistryCodec.Sanitize(good, out RespawnPointProblem? none), good) && none == null, "a good point passes untouched");

		bool Throws(string payload)
		{
			try { RespawnRegistryCodec.Read(payload); return false; }
			catch (InvalidOperationException) { return true; }
		}
		Check(Throws(null) && Throws("{\"Points\":null}") && Throws("null")
			&& Throws(RespawnRegistryCodec.Write(Enumerable.Range(1, 4097).Select(x => Point(x)))), "a broken envelope is still refused");

		// Log once: a report every second with the same bad medbay logs it the first time only.
		var log = new RespawnProblemLog();
		var first = new List<RespawnPointProblem> { new(2, "OxygenLevel outside 0..1", "1.0000001", false), new(6, "Generation invalid", "", true) };
		Check(log.Fresh("node-1", first).Count == 2, "the first report logs both");
		Check(log.Fresh("node-1", new List<RespawnPointProblem> { first[0] with { Value = "1.02" }, first[1] }).Count == 0,
			"the next report logs nothing, also with a changed value");
		Check(log.Fresh("node-2", first).Count == 2, "another origin is its own scope");
		Check(log.Fresh("node-1", new List<RespawnPointProblem> { first[1] }).Count == 0, "a point that recovered is forgotten");
		Check(log.Fresh("node-1", first).Single().RespawnEntityId == 2, "and logged again when it breaks again");
		Check(log.Fresh("node-1", new List<RespawnPointProblem>()).Count == 0 && log.Fresh("node-1", first).Count == 2, "an empty report clears the scope");
		Check(RespawnProblemLog.Line("authority", "node-1", first[0])
			== "[ClusterPlugin] respawn_point repaired where=authority origin=node-1 entity=2 check='OxygenLevel outside 0..1' value=1.0000001",
			"the log line: " + RespawnProblemLog.Line("authority", "node-1", first[0]));
	}
}
