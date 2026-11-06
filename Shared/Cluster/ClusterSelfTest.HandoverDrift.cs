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
		HandoverDriftBlockGroups();
		HandoverDriftSuitOxygen();
	}

	/// <summary>
	/// SE-1154: a suit one breath under full against a refilled one, seated (Pilot and the hierarchy copy) or free, and
	/// a free character's reading of a room the import has not rebuilt yet. The values are partition 5888's and 5774's.
	/// </summary>
	private static void HandoverDriftSuitOxygen()
	{
		string Gases(string oxygen) => "<OxygenLevel>" + oxygen + "</OxygenLevel><StoredGases>"
			+ "<StoredGas><Id Type=\"MyObjectBuilder_GasProperties\" Subtype=\"Hydrogen\" /><FillLevel>0.9864242</FillLevel></StoredGas>"
			+ "<StoredGas><Id Type=\"MyObjectBuilder_GasProperties\" Subtype=\"Oxygen\" /><FillLevel>" + oxygen + "</FillLevel></StoredGas></StoredGases>";
		string Character(string oxygen) => "<SubtypeName>Default_Astronaut_Female</SubtypeName><EntityId>9</EntityId>" + Gases(oxygen) + "<MovementState>Sitting</MovementState>";
		string Seated(string oxygen) => DriftHead + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Cockpit\"><SubtypeName>LargeBlockCockpitSeat</SubtypeName><EntityId>2</EntityId>"
			+ "<ComponentContainer><Components><ComponentData><TypeId>MyHierarchyComponentBase</TypeId><Component xsi:type=\"MyObjectBuilder_HierarchyComponentBase\">"
			+ "<Children><MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_Character\">" + Character(oxygen) + "</MyObjectBuilder_EntityBase></Children>"
			+ "</Component></ComponentData></Components></ComponentContainer>"
			+ "<Pilot>" + Character(oxygen) + "</Pilot><OxygenLevel>0.8792505</OxygenLevel></MyObjectBuilder_CubeBlock></CubeBlocks></MyObjectBuilder_EntityBase>" + DriftTail;
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Seated("0.9979011"), Seated("1"));
		Assert(residuals.Count == 0, "a seated pilot's suit one breath under full was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Seated("0.9"), Seated("1"));
		Assert(residuals.Count == 4 && residuals.TrueForAll(entry => entry.EndsWith("/OxygenLevel: 0.9 != 1") || entry.EndsWith("/FillLevel: 0.9 != 1")),
			"a seated pilot's LOST suit oxygen must stay a residual in both copies, got: " + DriftReport(residuals));

		string Free(string oxygen, string environment) => DriftHead + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_Character\"><EntityId>8</EntityId>"
			+ Gases(oxygen) + "<EnvironmentOxygenLevel>" + environment + "</EnvironmentOxygenLevel></MyObjectBuilder_EntityBase>" + DriftTail;
		residuals = ImportRoundTrip.ResidualsOfXml(Free("1", "0.99324"), Free("1", "0"));
		Assert(residuals.Count == 0, "a free character's reading of a room the import has not rebuilt was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Free("0.9979011", "1"), Free("1", "1"));
		Assert(residuals.Count == 0, "a free character's suit one breath under full was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Free("0.5", "1"), Free("1", "1"));
		Assert(residuals.Count == 2, "a free character's LOST suit oxygen must stay a residual, got: " + DriftReport(residuals));
	}

	/// <summary>SE-1153: a block group without blocks is dropped by the load; one with blocks stays compared.</summary>
	private static void HandoverDriftBlockGroups()
	{
		const string empty = "<MyObjectBuilder_BlockGroup><Name>Railguns</Name><Blocks /></MyObjectBuilder_BlockGroup>";
		const string guns = "<MyObjectBuilder_BlockGroup><Name>Guns</Name><Blocks><Vector3I><X>1</X><Y>2</Y><Z>3</Z></Vector3I></Blocks></MyObjectBuilder_BlockGroup>";
		string Grid(string groups) => DriftHead + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><GridSizeEnum>Small</GridSizeEnum>"
			+ (groups == null ? "" : "<BlockGroups>" + groups + "</BlockGroups>") + "<DisplayName>Small Grid 766</DisplayName></MyObjectBuilder_EntityBase>" + DriftTail;
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Grid(empty), Grid(null));
		Assert(residuals.Count == 0, "an empty block group the load drops was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Grid(empty + guns), Grid(guns));
		Assert(residuals.Count == 0, "an empty block group next to a real one was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Grid(guns), Grid(null));
		Assert(residuals.Exists(entry => entry.Contains("BlockGroups")), "a LOST block group must stay a residual, got: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Grid(guns), Grid(guns.Replace("<X>1</X>", "<X>4</X>")));
		Assert(residuals.Count == 1 && residuals[0].Contains("/Blocks/"), "a group's blocks must stay compared, got: " + DriftReport(residuals));
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
