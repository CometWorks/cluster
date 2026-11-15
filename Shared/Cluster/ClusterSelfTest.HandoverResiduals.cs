using System.Collections.Generic;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1557 (octahedron-test, 2026-11-14/15): the residuals that still blocked handovers on cluster 0.3.16 and 0.3.17, each
	/// pinned to the values of the live handover-diff dump it came from, and each with a control that must still fail.
	/// </summary>
	private static void HandoverResidualsSe1557()
	{
		HandoverResidualsDisplacedSeat();
		HandoverResidualsPendingBlockState();
		HandoverResidualsOtherQueue();
		HandoverResidualsBuildPlanner();
		HandoverResidualsProjection();
		HandoverResidualsTolerances();
		HandoverResidualsBottles();
	}

	private static string Grid(string blocks, string id = "1")
		=> DriftHead + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>" + id + "</EntityId><CubeBlocks>" + blocks
			+ "</CubeBlocks></MyObjectBuilder_EntityBase>" + DriftTail;

	/// <summary>
	/// Partition 56379's desk seat (2026-11-15 04:19): StripStaleSeatedBodies dropped the pilot's stale saved body on the target,
	/// which re-emitted the seat without SitAnimation and without the hierarchy component; the source's seat had both.
	/// </summary>
	private static void HandoverResidualsDisplacedSeat()
	{
		const string pilot = "<SubtypeName>Default_Astronaut</SubtypeName><EntityId>9</EntityId><MovementState>Sitting</MovementState>";
		string Seat(bool seated, string extra = "") => Grid("<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Cockpit\"><SubtypeName>LargeBlockDeskCorner</SubtypeName><EntityId>2</EntityId>"
			+ (seated ? "<ComponentContainer><Components><ComponentData><TypeId>MyHierarchyComponentBase</TypeId><Component xsi:type=\"MyObjectBuilder_HierarchyComponentBase\">"
				+ "<Children><MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_Character\">" + pilot + "</MyObjectBuilder_EntityBase></Children>"
				+ "</Component></ComponentData></Components></ComponentContainer>" : "")
			+ extra + (seated ? "<Pilot>" + pilot + "</Pilot><PilotJetpackEnabled>true</PilotJetpackEnabled>" : "<PilotJetpackEnabled xsi:nil=\"true\" />")
			+ "<SitAnimation>1</SitAnimation></MyObjectBuilder_CubeBlock>");
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Seat(true), Seat(false), new HashSet<long> { 9 });
		Assert(residuals.Count == 0, "a displaced pilot's seat left SitAnimation or an empty hierarchy component behind: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Seat(true, "<CustomName>Desk</CustomName>"), Seat(false), new HashSet<long> { 9 });
		Assert(residuals.Count == 2 && residuals.Exists(entry => entry.Contains("/CustomName")), "the rest of a displaced pilot's seat must stay compared, got: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Seat(true), Seat(false));
		Assert(residuals.Exists(entry => entry.Contains("/Pilot")), "a pilot LOST without being displaced must stay a residual, got: " + DriftReport(residuals));
	}

	/// <summary>
	/// A jump drive's GPS target (partition 64205's drive, "-1128022872 != nil") and a turret's IsPotentialTarget (partition 54809's
	/// gatling turrets, "false != true") are restored only by the update the freeze defers.
	/// </summary>
	private static void HandoverResidualsPendingBlockState()
	{
		string Drive(string target, string ratio = "1") => Grid("<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_JumpDrive\"><SubtypeName>LargeJumpDrive</SubtypeName><EntityId>2</EntityId>"
			+ (target == null ? "<JumpTarget xsi:nil=\"true\" />" : "<JumpTarget>" + target + "</JumpTarget>") + "<JumpRatio>" + ratio + "</JumpRatio></MyObjectBuilder_CubeBlock>");
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Drive("-1128022872"), Drive(null));
		Assert(residuals.Count == 0, "a jump drive's pending GPS target was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Drive("-1128022872", "1"), Drive(null, "0.5"));
		Assert(residuals.Count == 1 && residuals[0].EndsWith("/JumpRatio: 1 != 0.5"), "the rest of a jump drive must stay compared, got: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml("<A><JumpTarget>5</JumpTarget></A>", "<A><JumpTarget>6</JumpTarget></A>");
		Assert(residuals.Count == 1, "JumpTarget outside a jump drive was normalised away");

		string Turret(string potential, string range = "800") => Grid("<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_LargeGatlingTurret\"><SubtypeName /><EntityId>2</EntityId>"
			+ "<Range>" + range + "</Range><IsPotentialTarget>" + potential + "</IsPotentialTarget></MyObjectBuilder_CubeBlock>");
		residuals = ImportRoundTrip.ResidualsOfXml(Turret("false"), Turret("true"));
		Assert(residuals.Count == 0, "a turret's IsPotentialTarget was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Turret("false"), Turret("true", "600"));
		Assert(residuals.Count == 1 && residuals[0].EndsWith("/Range: 800 != 600"), "a turret's range must stay compared, got: " + DriftReport(residuals));
	}

	/// <summary>Partition 49870's BasicAssembler: an other-mode queue of 94 SteelPlate the load drops, as every load does.</summary>
	private static void HandoverResidualsOtherQueue()
	{
		const string steel = "<Item><Id Type=\"MyObjectBuilder_BlueprintDefinition\" Subtype=\"SteelPlate\" /><Amount>94</Amount><ItemId xsi:nil=\"true\" /></Item>";
		string Assembler(string otherQueue, string queue = "") => Grid("<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Assembler\"><SubtypeName>BasicAssembler</SubtypeName><EntityId>2</EntityId>"
			+ (queue.Length == 0 ? "" : "<Queue>" + queue + "</Queue>") + "<DisassembleEnabled>false</DisassembleEnabled>"
			+ (otherQueue == null ? "" : "<OtherQueue>" + otherQueue + "</OtherQueue>") + "</MyObjectBuilder_CubeBlock>");
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Assembler(steel), Assembler(null));
		Assert(residuals.Count == 0, "an other-mode queue the load drops was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Assembler(null, steel), Assembler(null));
		Assert(residuals.Exists(entry => entry.Contains("/Queue")), "a LOST production queue must stay a residual, got: " + DriftReport(residuals));
	}

	/// <summary>Partition 64410's bot character: the target's copy of the identity had a build planner the source's had not.</summary>
	private static void HandoverResidualsBuildPlanner()
	{
		const string planner = "<BuildPlanner><BuildPlanItem><BlockId Type=\"MyObjectBuilder_ConveyorConnector\" Subtype=\"ConveyorTubeDuct\" />"
			+ "<IsInProgress>true</IsInProgress><Components><ComponentItem><ComponentId Type=\"MyObjectBuilder_Component\" Subtype=\"SteelPlate\" />"
			+ "<Count>24</Count></ComponentItem></Components></BuildPlanItem></BuildPlanner>";
		string Character(string extra, string name = "Bot") => DriftHead + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_Character\"><EntityId>8</EntityId>"
			+ "<DisplayName>" + name + "</DisplayName>" + extra + "</MyObjectBuilder_EntityBase>" + DriftTail;
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Character(""), Character(planner));
		Assert(residuals.Count == 0, "a character's identity build planner was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Character(""), Character(planner, "Other"));
		Assert(residuals.Count == 1 && residuals[0].Contains("/DisplayName"), "the rest of the character must stay compared, got: " + DriftReport(residuals));
	}

	/// <summary>
	/// Partition 53173's projector: the source held the blueprint as set (world position, BuiltBy, no CreatePhysics), the target
	/// as the projector's clipboard left it. Partition 61204's: a projector inside the blueprint, whose own blueprint reused the
	/// outer ids on the source and had fresh ones on the target.
	/// </summary>
	private static void HandoverResidualsProjection()
	{
		string Block(string id, string extra = "") => "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_CubeBlock\"><SubtypeName>LargeBlockArmorBlock</SubtypeName><EntityId>" + id
			+ "</EntityId>" + extra + "</MyObjectBuilder_CubeBlock>";
		string Blueprint(string grid, string blocks, string position, bool prepared)
			=> "<MyObjectBuilder_CubeGrid><SubtypeName /><EntityId>" + grid + "</EntityId><PositionAndOrientation><Position x=\"" + position + "\" y=\"141564.82740551914\" z=\"-112206.86289622878\" />"
				+ "<Forward x=\"0\" y=\"0\" z=\"-1\" /><Up x=\"0\" y=\"1\" z=\"0\" /></PositionAndOrientation><GridSizeEnum>Large</GridSizeEnum><CubeBlocks>" + blocks + "</CubeBlocks>"
				+ "<DisplayName>Ship</DisplayName>" + (prepared ? "<CreatePhysics>false</CreatePhysics>" : "") + "</MyObjectBuilder_CubeGrid>";
		string Projector(string blueprint) => Grid("<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Projector\"><SubtypeName>LargeProjector</SubtypeName><EntityId>2</EntityId>"
			+ "<ProjectedGrids>" + blueprint + "</ProjectedGrids></MyObjectBuilder_CubeBlock>");
		const string by = "<BuiltBy>144115188075855897</BuiltBy>";
		string raw = Projector(Blueprint("10", Block("11", by) + Block("12", by), "27775.283934791667", false));
		string prepared = Projector(Blueprint("20", Block("21") + Block("22"), "0", true));
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(raw, prepared);
		Assert(residuals.Count == 0, "a blueprint the projector's clipboard prepared was reported: " + DriftReport(residuals));
		string lost = Projector(Blueprint("20", Block("21"), "0", true));
		residuals = ImportRoundTrip.ResidualsOfXml(raw, lost);
		Assert(residuals.Count > 0, "a block LOST from the blueprint must stay a residual");

		// The nested projector is block 12 of the outer blueprint; its blueprint reuses ids 10..12 on the source.
		string Nested(string inner, string after) => Projector(Blueprint("10", Block("11")
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Projector\"><SubtypeName>LargeProjector</SubtypeName><EntityId>12</EntityId><ProjectedGrids>"
			+ inner + "</ProjectedGrids></MyObjectBuilder_CubeBlock>" + Block(after), "0", true));
		string source = Nested(Blueprint("10", Block("11") + Block("12"), "0", true), "13");
		string target = Nested(Blueprint("30", Block("31") + Block("32"), "0", true), "13");
		residuals = ImportRoundTrip.ResidualsOfXml(source, target);
		Assert(residuals.Count == 0, "a nested blueprint with remapped ids was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(source, Nested(Blueprint("30", Block("31"), "0", true), "13"));
		Assert(residuals.Count > 0 && residuals.TrueForAll(entry => entry.Contains("/ProjectedGrids/MyObjectBuilder_CubeGrid#-1/CubeBlocks/MyObjectBuilder_CubeBlock#-3/ProjectedGrids/")),
			"a block LOST from a nested blueprint must be reported there and nowhere else, got: " + DriftReport(residuals));
	}

	/// <summary>Partition 53827's draining hydrogen tank (0.96691 != 0.96686) and partition 51941's thruster override (63774.547 != 63774.543).</summary>
	private static void HandoverResidualsTolerances()
	{
		string Tank(string ratio) => Grid("<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_OxygenTank\"><SubtypeName>LargeHydrogenTank</SubtypeName><EntityId>2</EntityId>"
			+ "<FilledRatio>" + ratio + "</FilledRatio></MyObjectBuilder_CubeBlock>");
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Tank("0.96691"), Tank("0.96686"));
		Assert(residuals.Count == 0, "a draining tank's fill a few parts in 100 000 lower was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Tank("0.96691"), Tank("0.5"));
		Assert(residuals.Count == 1, "a LOST tank fill must stay a residual, got: " + DriftReport(residuals));
		string Thruster(string force) => Grid("<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Thrust\"><SubtypeName>LargeBlockLargeHydrogenThrust</SubtypeName><EntityId>2</EntityId>"
			+ "<ThrustOverride>" + force + "</ThrustOverride></MyObjectBuilder_CubeBlock>");
		residuals = ImportRoundTrip.ResidualsOfXml(Thruster("63774.547"), Thruster("63774.543"));
		Assert(residuals.Count == 0, "a thrust override one float step apart was reported: " + DriftReport(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Thruster("63774.547"), Thruster("63700"));
		Assert(residuals.Count == 1, "a CHANGED thrust override must stay a residual, got: " + DriftReport(residuals));
	}

	/// <summary>
	/// Bot Trader Steven's seated pilot (partition 57263): tools, 1 + 24 + 9 bottles in a 0.4 m³ suit times InventorySizeMultiplier
	/// 10. The load keeps 6 of the last 9 OxygenBottles, and a clamped stack loads unchanged the next time.
	/// </summary>
	private static void HandoverResidualsBottles()
	{
		VRage.MyFixedPoint P(int amount) => amount;
		var stacks = new List<(VRage.MyFixedPoint, float, float, bool)>
		{
			(P(4), 0.020f, 3f, true), (P(4), 0.025f, 22f, true), (P(4), 0.008f, 5f, true),
			(P(1), 0.12f, 30f, true), (P(24), 0.12f, 30f, true), (P(9), 0.12f, 30f, true),
		};
		VRage.MyFixedPoint[] loaded = ImportRoundTrip.LoadedAmounts(stacks, (VRage.MyFixedPoint)4.0, VRage.MyFixedPoint.MaxValue);
		Assert(loaded[5] == 6 && loaded[4] == 24 && loaded[0] == 4, "the pilot's last bottle stack must load as 6 beside the rest, the model gives " + string.Join(", ", loaded));
		stacks[5] = (loaded[5], 0.12f, 30f, true);
		Assert(ImportRoundTrip.LoadedAmounts(stacks, (VRage.MyFixedPoint)4.0, VRage.MyFixedPoint.MaxValue)[5] == 6, "a clamped bottle stack must load unchanged the next time");
	}
}
