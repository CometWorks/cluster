using System.Collections.Generic;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	private const string DriftHead = "<MyObjectBuilder_Sector xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><SectorObjects>";
	private const string DriftTail = "</SectorObjects></MyObjectBuilder_Sector>";

	/// <summary>
	/// SE-1152..1155 (octahedron-test, 2026-11-06): benign drift the round-trip gate rejected, each part pinned to the
	/// values of the live handover-diff dump it came from, and each with a control that must still fail.
	/// </summary>
	private static void HandoverDrift()
	{
		HandoverDriftWarhead();
	}

	private static string DriftReport(List<string> found) => string.Join("; ", found);

	/// <summary>SE-1152: a warhead whose countdown ran out without an explosion restarts from Init's 1000 ms clamp.</summary>
	private static void HandoverDriftWarhead()
	{
		string Warhead(string countdown, string counting) => DriftHead + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Warhead\"><SubtypeName>SmallWarhead</SubtypeName><EntityId>2</EntityId>"
			+ "<CountdownMs>" + countdown + "</CountdownMs><IsArmed>false</IsArmed><IsCountingDown>" + counting + "</IsCountingDown>"
			+ "</MyObjectBuilder_CubeBlock></CubeBlocks></MyObjectBuilder_EntityBase>" + DriftTail;
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Warhead("-1", "true"), Warhead("984", "true"));
		Assert(residuals.Count == 0, "a run-out warhead countdown restarted from Init's clamp was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Warhead("30000", "true"), Warhead("29984", "true"));
		Assert(residuals.Count == 0, "a running warhead countdown one import tick further was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Warhead("5000000", "false"), Warhead("3600000", "false"));
		Assert(residuals.Count == 0, "a warhead countdown over Init's maximum was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Warhead("30000", "true"), Warhead("10000", "true"));
		Assert(residuals.Count == 1 && residuals[0].EndsWith("/CountdownMs: 30000 != 10000"), "a LOST warhead countdown must stay a residual, got: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Warhead("-1", "true"), Warhead("984", "false"));
		Assert(residuals.Count == 1 && residuals[0].Contains("/IsCountingDown"), "a stopped countdown must stay a residual, got: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml("<A><CountdownMs>-1</CountdownMs></A>", "<A><CountdownMs>1000</CountdownMs></A>");
		Assert(residuals.Count == 1, "CountdownMs outside a warhead was normalised away");
	}
}
