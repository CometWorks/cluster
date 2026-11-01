using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using ClusterPlugin;
using ObjectBuilders.SafeZone;
using Sandbox.Common.ObjectBuilders;
using VRage;
using VRage.Game;
using VRage.Game.ObjectBuilder;
using VRage.Game.ObjectBuilders;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Platform.Windows;
using VRage.Serialization;
using VRage.Utils;
using VRageMath;

// Game assemblies resolve from SPACE_ENGINEERS_BIN64: see Bootstrap.cs.


// Entry point: Bootstrap.Main registers the game-assembly resolver, then calls Tool.Run.
internal static class Tool
{
public static int Run(string[] args)
{
const double Proximity = 4000;

if (!((args.Length == 2 && args[0] is "verify-mps" or "verify-fidelity")
	|| (args.Length == 3 && args[0] is "to-split" or "to-handover" or "to-proximity" or "to-clientless" or "to-counters" or "to-fidelity" or "mps-to-sector" or "global-to-checkpoint")
	|| (args.Length >= 3 && args[0] is "to-vanilla" or "catalog-scan")
	|| (args.Length == 4 && args[0] is "materialize" or "semantic-diff")
	|| (args.Length == 6 && args[0] is "sector-to-mps")))
{
	Console.Error.WriteLine("usage: MagnetarWorld verify-mps FILE | verify-fidelity XML | mps-to-sector FILE OUTPUT_XML | sector-to-mps XML OUTPUT_MPS PARTITION GENERATION SEQUENCE | global-to-checkpoint FILE OUTPUT_XML | semantic-diff BEFORE_XML AFTER_XML ENTITY_IDS | to-fidelity VANILLA OUTPUT | to-handover VANILLA SPLIT | to-split VANILLA SPLIT | to-proximity VANILLA SPLIT | to-clientless VANILLA SPLIT | to-counters VANILLA SPLIT | materialize SPLIT SLOT OUTPUT | to-vanilla SPLIT VANILLA [DATA_ROOT...] | catalog-scan OUTPUT_JSON NODE=DATA_ROOT [NODE=DATA_ROOT...] [--prefer-newest]");
	return 2;
}

InitializeSerializers();
int exitCode = 0;
switch (args[0])
{
	case "verify-mps": VerifyMps(args[1]); break;
	case "verify-fidelity": VerifyFidelity(args[1]); break;
	case "mps-to-sector": MpsToSector(args[1], args[2]); break;
	case "sector-to-mps": SectorToMps(args[1], args[2], ulong.Parse(args[3]), uint.Parse(args[4]), uint.Parse(args[5])); break;
	case "global-to-checkpoint": GlobalToCheckpoint(args[1], args[2]); break;
	case "semantic-diff": exitCode = SemanticDiff(args[1], args[2], args[3]); break;
	case "to-split": ToSplit(args[1], args[2]); break;
	case "to-handover": ToHandover(args[1], args[2]); break;
	case "to-proximity": ToProximity(args[1], args[2]); break;
	case "to-clientless": ToClientless(args[1], args[2]); break;
	case "to-counters": ToCounters(args[1], args[2]); break;
	case "to-fidelity": ToFidelity(args[1], args[2]); break;
	case "materialize": Materialize(args[1], int.Parse(args[2]), args[3]); break;
	case "to-vanilla": ToVanilla(args[1], args[2], args.Skip(3).ToArray()); break;
	case "catalog-scan": CatalogScan(args[1], args.Skip(2).ToArray()); break;
}
return exitCode;

static void VerifyMps(string path)
{
	PartitionSaveFile.Parsed save = PartitionSaveFile.ReadFile(path);
	if (save.PartitionId == 0)
	{
		MyObjectBuilder_Checkpoint checkpoint = GlobalSaveFile.Read(File.ReadAllBytes(path), save.Generation, save.Sequence);
		Console.WriteLine(JsonSerializer.Serialize(new { valid = true, global = true, generation = save.Generation,
			sequence = save.Sequence, kind = save.Kind, elapsedGameTime = checkpoint.ElapsedGameTime, checksum = save.BodyChecksum.ToString("X16") }));
		return;
	}
	MyObjectBuilder_EntityBase[] entities = PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount);
	Console.WriteLine(JsonSerializer.Serialize(new { valid = true, partition = save.PartitionId, generation = save.Generation,
		sequence = save.Sequence, kind = save.Kind, entities = save.EntityCount, entityIds = entities.Select(entity => entity.EntityId).Order().ToArray(), checksum = save.BodyChecksum.ToString("X16") }));
}

static void MpsToSector(string path, string output)
{
	PartitionSaveFile.Parsed save = PartitionSaveFile.ReadFile(path);
	MyObjectBuilder_Sector sector = new MyObjectBuilder_Sector { SectorObjects = PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount).ToList() };
	if (!MyObjectBuilderSerializerKeen.SerializeXML(output, false, sector)) throw new IOException("Cannot write " + output);
}

static void SectorToMps(string xmlPath, string output, ulong partition, uint generation, uint sequence)
{
	// P2-REC-03 poison repair: re-import an exported (and possibly hand-edited) sector XML as a
	// valid Final partition save. The quarantine-repair endpoint validates the id and adopts it.
	if (!MyObjectBuilderSerializerKeen.DeserializeXML(xmlPath, out MyObjectBuilder_Sector sector) || sector == null)
		throw new InvalidDataException("Cannot read " + xmlPath);
	List<MyObjectBuilder_EntityBase> entities = sector.SectorObjects ?? new();
	if (entities.Count == 0) throw new InvalidDataException("Sector has no entities to import");
	BoundingSphereD sphere = Sphere(entities);
	bool anchored = entities.Any(x => x is MyObjectBuilder_CubeGrid grid && grid.IsStatic);
	byte[] file = PartitionSaveFile.Write(partition, generation, sequence, 2, sphere, anchored,
		entities.Count, PartitionSaveFile.SerializeEntities(entities), 0);
	PartitionSaveFile.WriteFile(output, file);
	VerifyPartition(output, entities.Select(x => x.EntityId));
	Console.WriteLine(JsonSerializer.Serialize(new { partition, generation, sequence, entities = entities.Count, output = Path.GetFullPath(output) }));
}

static void GlobalToCheckpoint(string path, string output)
{
	PartitionSaveFile.Parsed save = PartitionSaveFile.ReadFile(path);
	if (save.Kind != 2) throw new InvalidDataException("Warm checkpoint source must be a Final global save");
	MyObjectBuilder_Checkpoint checkpoint = GlobalSaveFile.Read(File.ReadAllBytes(path), save.Generation, save.Sequence);
	if (!MyObjectBuilderSerializerKeen.SerializeXML(output, false, checkpoint)) throw new IOException("Cannot write " + output);
	Console.WriteLine(JsonSerializer.Serialize(new { generation = save.Generation, sequence = save.Sequence,
		elapsedGameTime = checkpoint.ElapsedGameTime, sessionName = checkpoint.SessionName }));
}

static void ToFidelity(string source, string output)
{
	const long gridId = 128014564749527700, floatingId = 128014564749527707;
	PrepareOutput(source, output);
	foreach (string checkpoint in new[] { "Sandbox.sbc", "Sandbox_config.sbc" })
	{
		string checkpointPath = Path.Combine(output, checkpoint);
		File.WriteAllText(checkpointPath, File.ReadAllText(checkpointPath).Replace("<AutoSaveInMinutes>0</AutoSaveInMinutes>", "<AutoSaveInMinutes>1</AutoSaveInMinutes>"));
	}
	MyObjectBuilder_Sector sector = LoadSector(source);
	List<MyObjectBuilder_EntityBase> entities = sector.SectorObjects ?? new();
	MyObjectBuilder_CubeGrid station = entities.OfType<MyObjectBuilder_CubeGrid>().First(grid => grid.IsStatic);
	MyObjectBuilder_CubeGrid transport = entities.OfType<MyObjectBuilder_CubeGrid>().Single(grid => grid.DisplayName == "Cluster Transport");
	MyObjectBuilder_Character character = entities.OfType<MyObjectBuilder_Character>().Single();
	character.IsPersistenceCharacter = true;

	MyObjectBuilder_CubeGrid fixture = (MyObjectBuilder_CubeGrid)MyObjectBuilderSerializerKeen.Clone(transport);
	fixture.EntityId = gridId;
	fixture.Name = gridId.ToString();
	fixture.DisplayName = "P1 SAV Fidelity";
	fixture.PositionAndOrientation = new MyPositionAndOrientation(Position(transport) + new Vector3D(100, 0, 0), Vector3.Forward, Vector3.Up);
	fixture.LinearVelocity = Vector3.Zero;
	fixture.AngularVelocity = Vector3.Zero;
	fixture.IsStatic = false;
	fixture.CubeBlocks = new List<MyObjectBuilder_CubeBlock>();

	MyObjectBuilder_BatteryBlock battery = Block<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock", gridId + 1, new Vector3I(0, 0, 0));
	battery.CurrentStoredPower = 2;
	MyObjectBuilder_MyProgrammableBlock pb = Block<MyObjectBuilder_MyProgrammableBlock>("LargeProgrammableBlock", gridId + 2, new Vector3I(1, 0, 0));
	pb.Enabled = false;
	pb.Program = "public void Main(string argument) { }\n";
	pb.Storage = "P1-SAV-01/storage/v1";
	pb.DefaultRunArgument = "P1-SAV-01/default";
	MyObjectBuilder_MotorStator rotor = Block<MyObjectBuilder_MotorStator>("LargeStator", gridId + 3, new Vector3I(2, 0, 0));
	rotor.Enabled = false;
	rotor.TargetVelocity = 0.375f;
	rotor.MinAngle = -0.5f;
	rotor.MaxAngle = 1f;
	rotor.CurrentAngle = 0.25f;
	rotor.LimitsActive = true;
	rotor.RotorLock = true;
	MyObjectBuilder_PistonBase piston = Block<MyObjectBuilder_PistonBase>("LargePistonBase", gridId + 4, new Vector3I(3, 0, 0));
	piston.Enabled = false;
	piston.Velocity = 0.25f;
	piston.MinLimit = 0.5f;
	piston.MaxLimit = 5f;
	piston.CurrentPosition = 2.25f;
	piston.Reverse = true;
	MyObjectBuilder_ShipConnector connector = Block<MyObjectBuilder_ShipConnector>("Connector", gridId + 5, new Vector3I(4, 0, 0));
	connector.Enabled = false;
	connector.ThrowOut = false;
	connector.CollectAll = false;
	fixture.CubeBlocks.AddRange(new MyObjectBuilder_CubeBlock[] { battery, pb, rotor, piston, connector,
		CounterContainer(gridId + 6, new Vector3I(5, 0, 0), "Fidelity Cargo", 37) });
	entities.Add(fixture);
	entities.Add(new MyObjectBuilder_FloatingObject
	{
		EntityId = floatingId,
		Name = floatingId.ToString(),
		PersistentFlags = MyPersistentEntityFlags2.Enabled | MyPersistentEntityFlags2.InScene,
		PositionAndOrientation = new MyPositionAndOrientation(Position(fixture) + new Vector3D(0, 5, 0), Vector3.Forward, Vector3.Up),
		Item = new MyObjectBuilder_InventoryItem { Amount = 13, ItemId = 73,
			PhysicalContent = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Component>("SteelPlate") }
	});
	WriteSector(output, sector);
	File.WriteAllText(Path.Combine(output, "fixture.json"), JsonSerializer.Serialize(new { entityIds = new[] { station.EntityId, transport.EntityId, fixture.EntityId, floatingId, character.EntityId } }) + Environment.NewLine);

	static T Block<T>(string subtype, long id, Vector3I min) where T : MyObjectBuilder_CubeBlock, new()
	{
		T block = MyObjectBuilderSerializerKeen.CreateNewObject<T>(subtype);
		block.EntityId = id;
		block.Name = id.ToString();
		block.Min = min;
		block.Owner = 144115188075855895;
		block.BuiltBy = 144115188075855895;
		block.ShareMode = MyOwnershipShareModeEnum.All;
		return block;
	}
}

static void VerifyFidelity(string path)
{
	if (!MyObjectBuilderSerializerKeen.DeserializeXML(path, out MyObjectBuilder_Sector sector)) throw new InvalidDataException("Cannot read " + path);
	List<MyObjectBuilder_EntityBase> entities = sector.SectorObjects ?? new();
	MyObjectBuilder_CubeGrid fixture = entities.OfType<MyObjectBuilder_CubeGrid>().Single(grid => grid.EntityId == 128014564749527700);
	MyObjectBuilder_MyProgrammableBlock pb = fixture.CubeBlocks.OfType<MyObjectBuilder_MyProgrammableBlock>().Single();
	MyObjectBuilder_MotorStator rotor = fixture.CubeBlocks.OfType<MyObjectBuilder_MotorStator>().Single();
	MyObjectBuilder_PistonBase piston = fixture.CubeBlocks.OfType<MyObjectBuilder_PistonBase>().Single();
	MyObjectBuilder_CargoContainer cargo = fixture.CubeBlocks.OfType<MyObjectBuilder_CargoContainer>().Single();
	MyObjectBuilder_Inventory inventory = (MyObjectBuilder_Inventory)cargo.ComponentContainer.Components.Single(component => component.TypeId == "MyInventoryBase").Component;
	MyObjectBuilder_FloatingObject floating = entities.OfType<MyObjectBuilder_FloatingObject>().Single(item => item.EntityId == 128014564749527707);
	List<string> failed = new();
	if (pb.Storage != "P1-SAV-01/storage/v1") failed.Add("pb-storage");
	if (pb.DefaultRunArgument != "P1-SAV-01/default") failed.Add("pb-argument");
	if (pb.Program != "public void Main(string argument) { }\n") failed.Add("pb-program");
	if (rotor.TargetVelocity != 0.375f || rotor.MinAngle != -0.5f || rotor.MaxAngle != 1f || !rotor.RotorLock) failed.Add("rotor");
	if (piston.Velocity != 0.25f || piston.MinLimit != 0.5f || piston.MaxLimit is not (1f or 5f)) failed.Add("piston");
	if (fixture.CubeBlocks.OfType<MyObjectBuilder_ShipConnector>().Count() != 1) failed.Add("connector");
	if (inventory.Items.Single().Amount != 37) failed.Add("cargo");
	if (floating.Item.Amount != 13) failed.Add("floating");
	if (!entities.OfType<MyObjectBuilder_Character>().Any()) failed.Add("character");
	if (!entities.OfType<MyObjectBuilder_CubeGrid>().Any(grid => grid.IsStatic)) failed.Add("station");
	if (failed.Count != 0) throw new InvalidDataException("P1-SAV-01 fixture markers do not match: " + string.Join(",", failed));
	Console.WriteLine(JsonSerializer.Serialize(new { valid = true, entities = entities.Count, fixture = fixture.EntityId }));
}

static int SemanticDiff(string beforePath, string afterPath, string entityIds)
{
	HashSet<long> ids = entityIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToHashSet();
	Dictionary<string, XElement> defaults = new();
	Dictionary<long, XElement> before = Read(beforePath), after = Read(afterPath);
	List<object> differences = new();
	foreach (long id in ids.Order())
	{
		bool hasLeft = before.TryGetValue(id, out XElement left), hasRight = after.TryGetValue(id, out XElement right);
		if (!hasLeft || !hasRight)
		{
			differences.Add(new { entity = id, path = "$", before = left != null, after = right != null });
			continue;
		}
		ComparePhysics(id, left, right, differences);
		XElement normalizedLeft = Normalize(left), normalizedRight = Normalize(right);
		if (!XNode.DeepEquals(normalizedLeft, normalizedRight))
			differences.Add(new { entity = id, path = "nonPhysics", before = normalizedLeft.ToString(SaveOptions.DisableFormatting), after = normalizedRight.ToString(SaveOptions.DisableFormatting) });
	}
	Console.WriteLine(JsonSerializer.Serialize(new { equal = differences.Count == 0, entities = ids.Count, differences }));
	return differences.Count == 0 ? 0 : 1;

	Dictionary<long, XElement> Read(string path) => XDocument.Load(path).Descendants("SectorObjects").Elements()
		.Where(element => long.TryParse(element.Element("EntityId")?.Value, out long id) && ids.Contains(id))
		.ToDictionary(element => long.Parse(element.Element("EntityId")!.Value), element => element);

	// The binary round trip materializes null block fields as their compile-time
	// defaults (a nil FontColor comes back as explicit white); a value equal to the
	// type's default is semantically identical to its absence, so block children
	// matching the default-serialized form of their block type are erased from the
	// comparison. The default XML runs through the same normalization pipeline.
	XElement NormalizedDefault(string type)
	{
		if (defaults.TryGetValue(type, out XElement cached)) return cached;
		XElement normalized = null;
		try
		{
			var builder = MyObjectBuilderSerializerKeen.CreateNewObject(MyObjectBuilderType.Parse(type));
			using MemoryStream stream = new MemoryStream();
			if (MyObjectBuilderSerializerKeen.SerializeXML(stream, builder))
				normalized = Normalize(XDocument.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray())).Root);
		}
		catch (Exception)
		{
			// An unknown type simply gets no default erasure.
		}
		defaults[type] = normalized;
		return normalized;
	}

	void EraseDefaultBlockFields(XElement copy)
	{
		XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
		foreach (XElement block in copy.Descendants("CubeBlocks").Elements())
		{
			XElement fallback = NormalizedDefault((string)block.Attribute(xsi + "type") ?? block.Name.LocalName);
			if (fallback == null) continue;
			foreach (XElement child in block.Elements().ToArray())
			{
				XElement standard = fallback.Element(child.Name);
				if (standard != null && XNode.DeepEquals(child, standard)) child.Remove();
			}
		}
	}

	XElement Normalize(XElement source)
	{
		XElement copy = new XElement(source);
		copy.Descendants("PositionAndOrientation").Remove();
		copy.Descendants("LinearVelocity").Remove();
		copy.Descendants("AngularVelocity").Remove();
		copy.Descendants("PlayerPresenceTier").Remove();
		copy.Descendants("GridPresenceTier").Remove();
		copy.Descendants("MovementState").Remove();
		copy.Descendants("PilotRelativeWorld").Remove();
		copy.Descendants("UpdateStartOfFirstTexture").Remove();
		copy.Descendants("FramesFromLastTrigger").Remove();
		copy.Descendants("TimerTickInFrames").Remove();
		// TimerEnabled is MyObjectBuilder_TimerComponent's update-scheduler switch (TimerType
		// Frame10/Frame100), flipped by the engine as it (de)schedules block updates - the
		// player-facing timer block serializes "Enabled"/"IsCountingDown" instead. Same wobble
		// family as its siblings FramesFromLastTrigger/TimerTickInFrames above.
		foreach (XElement component in copy.Descendants("Component")
			.Where(x => (string)x.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type") == "MyObjectBuilder_TimerComponent").ToArray())
			component.Elements("TimerEnabled").Remove();
		copy.Descendants("InventoryFlags").Remove();
		foreach (XAttribute attribute in copy.Descendants("StoredGas").Descendants("Id").Attributes("Subtype")) attribute.Remove();
		// The binary save body stores floats, but XML written by different runtimes
		// prints the same float with different digit counts (9 significant digits vs
		// shortest round trip), and integrity/build ratios pass through a ushort in
		// the block serializer (truncate-vs-round leaves them one quantum apart).
		// Canonicalize fractional leaves to the float grid (integer text such as
		// entity ids stays untouched) and ratio fields to 4 decimals so
		// representation differences below the storage precision compare equal.
		foreach (XElement element in copy.Descendants().Where(x => x.Name.LocalName is "IntegrityPercent" or "BuildPercent"))
			if (double.TryParse(element.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ratio))
				element.Value = ratio.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
		// Local (attachment) poses travel through the compressed orientation encoding,
		// whose ~1e-5 quantization is not a fixed per-component grid, so no
		// canonicalization can unify both sides; exclude them like the world pose
		// above — poses are physics state, not the semantic payload.
		copy.Descendants("LocalPositionAndOrientation").Remove();
		foreach (XElement element in copy.DescendantsAndSelf())
		{
			foreach (XAttribute attribute in element.Attributes())
				attribute.Value = CanonicalNumber(attribute.Value);
			if (!element.HasElements) element.Value = CanonicalNumber(element.Value);
		}
		// A protobuf round trip materializes absent (null) collections as empty
		// elements; null and empty deserialize identically, so strip empty attribute-
		// less elements until a fixpoint to compare both shapes as equal. An
		// xsi:nil="true" empty element is the same null in a third spelling — drop
		// the attribute first so the strip below treats it as empty too.
		XNamespace nil = "http://www.w3.org/2001/XMLSchema-instance";
		foreach (XAttribute attribute in copy.Descendants().Attributes(nil + "nil").ToArray()) attribute.Remove();
		while (true)
		{
			XElement[] empty = copy.Descendants().Where(x => !x.HasElements && !x.HasAttributes && string.IsNullOrEmpty(x.Value)).ToArray();
			if (empty.Length == 0) break;
			foreach (XElement element in empty) element.Remove();
		}
		Sort(copy);
		EraseDefaultBlockFields(copy);
		return copy;
	}

	static string CanonicalNumber(string text)
	{
		if (text.IndexOfAny(new[] { '.', 'E', 'e' }) < 0) return text;
		return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
			? value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : text;
	}

	static void Sort(XElement element)
	{
		foreach (XElement child in element.Elements().ToArray()) Sort(child);
		XElement[] children = element.Elements().ToArray();
		if (children.Length > 1 && children.All(child => Key(child) != null)) element.ReplaceNodes(children.OrderBy(Key, StringComparer.Ordinal));
	}

	static string Key(XElement element)
	{
		string id = element.Element("EntityId")?.Value ?? element.Element("ItemId")?.Value;
		if (id != null) return id.PadLeft(24, '0');
		XElement min = element.Element("Min");
		return min == null ? null : string.Join(',', min.Attributes().OrderBy(attribute => attribute.Name.LocalName).Select(attribute => attribute.Value));
	}

	static void ComparePhysics(long id, XElement left, XElement right, List<object> differences)
	{
		CompareVector("position", left.Descendants("PositionAndOrientation").Elements("Position").FirstOrDefault(), right.Descendants("PositionAndOrientation").Elements("Position").FirstOrDefault(), 1, id, differences);
		CompareVector("linearVelocity", left.Descendants("LinearVelocity").FirstOrDefault(), right.Descendants("LinearVelocity").FirstOrDefault(), 0.5, id, differences);
		CompareVector("angularVelocity", left.Descendants("AngularVelocity").FirstOrDefault(), right.Descendants("AngularVelocity").FirstOrDefault(), 0.5, id, differences);
		XElement leftOrientation = left.Descendants("PositionAndOrientation").Elements("Orientation").FirstOrDefault();
		XElement rightOrientation = right.Descendants("PositionAndOrientation").Elements("Orientation").FirstOrDefault();
		if (leftOrientation != null && rightOrientation != null)
		{
			double dot = Math.Abs(ElementValue(leftOrientation, "X") * ElementValue(rightOrientation, "X")
				+ ElementValue(leftOrientation, "Y") * ElementValue(rightOrientation, "Y")
				+ ElementValue(leftOrientation, "Z") * ElementValue(rightOrientation, "Z")
				+ ElementValue(leftOrientation, "W") * ElementValue(rightOrientation, "W"));
			double delta = 2 * Math.Acos(Math.Min(1, dot));
			if (delta > 0.5) differences.Add(new { entity = id, path = "orientation", delta, tolerance = 0.5 });
		}
	}

	static void CompareVector(string path, XElement left, XElement right, double tolerance, long id, List<object> differences)
	{
		if (left == null && right == null) return;
		double dx = (left == null ? 0 : Value(left, "x")) - (right == null ? 0 : Value(right, "x"));
		double dy = (left == null ? 0 : Value(left, "y")) - (right == null ? 0 : Value(right, "y"));
		double dz = (left == null ? 0 : Value(left, "z")) - (right == null ? 0 : Value(right, "z"));
		double delta = Math.Sqrt(dx * dx + dy * dy + dz * dz);
		if (delta > tolerance) differences.Add(new { entity = id, path, delta, tolerance });
	}

	static double Value(XElement element, string name) => double.Parse(element.Attribute(name)?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture);
	static double ElementValue(XElement element, string name) => double.Parse(element.Element(name)?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture);
}

static void ToProximity(string source, string output)
{
	string staged = Path.Combine(Path.GetTempPath(), "MagnetarWorld-proximity-" + Guid.NewGuid().ToString("N"));
	try
	{
		PrepareOutput(source, staged);
		foreach (string checkpoint in new[] { "Sandbox.sbc", "Sandbox_config.sbc" })
		{
			string path = Path.Combine(staged, checkpoint);
			File.WriteAllText(path, File.ReadAllText(path).Replace("<InfiniteAmmo>false</InfiniteAmmo>", "<InfiniteAmmo>true</InfiniteAmmo>"));
		}
		MyObjectBuilder_Sector sector = LoadSector(source);
		List<MyObjectBuilder_CubeGrid> grids = (sector.SectorObjects ?? new()).OfType<MyObjectBuilder_CubeGrid>().ToList();
		MyObjectBuilder_CubeGrid transport = grids.Single(x => x.DisplayName == "Cluster Transport");
		MyObjectBuilder_CubeGrid[] anchors = grids.Where(x => x.IsStatic).OrderBy(x => Position(x).Z).ToArray();
		if (anchors.Length != 2) throw new InvalidDataException("Proximity fixture requires exactly two static anchors");
		anchors[0].PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(0, 10, -10000), Vector3.Forward, Vector3.Up);
		anchors[1].PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(0, 10, 5000), Vector3.Forward, Vector3.Up);
		MyObjectBuilder_CubeGrid target = (MyObjectBuilder_CubeGrid)MyObjectBuilderSerializerKeen.Clone(transport);
		target.EntityId = 128014564749527617;
		target.DisplayName = "Cluster Target";
		// Parked just OUTSIDE the 7.1 co-location envelope (sync 3000 + margin 1000):
		// the reverse pull must trigger from the approach, never from the boot layout.
		target.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(-20.4912500718292, 5.963790399982827, -4600.0), Vector3.Forward, Vector3.Up);
		long targetBlockId = 128014564749527618;
		foreach (MyObjectBuilder_CubeBlock block in target.CubeBlocks) block.EntityId = targetBlockId++;
		transport.CubeBlocks.Add(new MyObjectBuilder_LargeGatlingTurret
		{
			EntityId = 128014564749527616, Min = new Vector3I(1, 0, 2), Owner = 144115188075855895,
			BuiltBy = 144115188075855895, ShareMode = MyOwnershipShareModeEnum.All, EnableIdleRotation = false
		});
		sector.SectorObjects.Add(target);
		WriteSector(staged, sector);
		ToSplit(staged, output);
	}
	finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
}

static void ToHandover(string source, string output)
{
	string staged = Path.Combine(Path.GetTempPath(), "MagnetarWorld-handover-" + Guid.NewGuid().ToString("N"));
	try
	{
		PrepareOutput(source, staged);
		MyObjectBuilder_Sector sector = LoadSector(source);
		MyObjectBuilder_CubeGrid transport = (sector.SectorObjects ?? new()).OfType<MyObjectBuilder_CubeGrid>().Single(x => x.DisplayName == "Cluster Transport");
		MyObjectBuilder_MyProgrammableBlock pb = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_MyProgrammableBlock>("LargeProgrammableBlock");
		pb.EntityId = 128014564749527630;
		pb.Name = pb.EntityId.ToString();
		pb.Min = new Vector3I(1, 0, 2);
		pb.Owner = 144115188075855895;
		pb.BuiltBy = 144115188075855895;
		pb.ShareMode = MyOwnershipShareModeEnum.All;
		pb.CustomName = "P1 HND 01 State";
		pb.Program = "public void Save() { Storage = \"P1-HND-01/saved\"; }\n";
		pb.Storage = "P1-HND-01/initial";
		pb.DefaultRunArgument = "P1-HND-01/default";
		pb.Enabled = false;
		transport.CubeBlocks.Add(pb);
		WriteSector(staged, sector);
		ToSplit(staged, output);
	}
	finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
}

static void ToClientless(string source, string output)
{
	string staged = Path.Combine(Path.GetTempPath(), "MagnetarWorld-clientless-" + Guid.NewGuid().ToString("N"));
	try
	{
		PrepareOutput(source, staged);
		MyObjectBuilder_Sector sector = LoadSector(source);
		List<MyObjectBuilder_CubeGrid> grids = (sector.SectorObjects ?? new()).OfType<MyObjectBuilder_CubeGrid>().ToList();
		MyObjectBuilder_CubeGrid transport = grids.Single(x => x.DisplayName == "Cluster Transport");
		MyObjectBuilder_CubeGrid[] anchors = grids.Where(x => x.IsStatic).OrderBy(x => Vector3D.DistanceSquared(Position(x), Position(transport))).ToArray();
		if (anchors.Length != 2) throw new InvalidDataException("Clientless fixture requires exactly two static anchors");
		Vector3D origin = Position(transport);
		anchors[0].PositionAndOrientation = new MyPositionAndOrientation(origin + new Vector3D(0, 0, 8000), Vector3.Forward, Vector3.Up);
		anchors[1].PositionAndOrientation = new MyPositionAndOrientation(origin + new Vector3D(0, 0, -20000), Vector3.Forward, Vector3.Up);
		WriteSector(staged, sector);
		ToSplit(staged, output);
	}
	finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
}

static void ToCounters(string source, string output)
{
	string staged = Path.Combine(Path.GetTempPath(), "MagnetarWorld-counters-" + Guid.NewGuid().ToString("N"));
	try
	{
		PrepareOutput(source, staged);
		MyObjectBuilder_Sector sector = LoadSector(source);
		MyObjectBuilder_CubeGrid transport = (sector.SectorObjects ?? new()).OfType<MyObjectBuilder_CubeGrid>().Single(x => x.DisplayName == "Cluster Transport");
		transport.CubeBlocks.Add(CounterContainer(128014564749527620, new Vector3I(-1, 0, 2), "Counters Source", 64));
		transport.CubeBlocks.Add(CounterContainer(128014564749527621, new Vector3I(1, 0, 2), "Counters Destination", 0));
		WriteSector(staged, sector);
		ToSplit(staged, output);
	}
	finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
}

static MyObjectBuilder_CargoContainer CounterContainer(long id, Vector3I min, string name, int amount)
{
	MyObjectBuilder_Inventory inventory = new MyObjectBuilder_Inventory
	{
		nextItemId = amount == 0 ? 0u : 1u,
		InventoryFlags = MyInventoryFlags.CanReceive | MyInventoryFlags.CanSend,
	};
	if (amount != 0)
	{
		inventory.Items.Add(new MyObjectBuilder_InventoryItem
		{
			Amount = amount,
			ItemId = 0,
			PhysicalContent = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Component>("SteelPlate"),
		});
	}
	MyObjectBuilder_CargoContainer container = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CargoContainer>("LargeBlockSmallContainer");
	container.EntityId = id;
	container.Name = id.ToString();
	container.Min = min;
	container.Owner = 144115188075855895;
	container.BuiltBy = 144115188075855895;
	container.ShareMode = MyOwnershipShareModeEnum.All;
	container.CustomName = name;
	container.ComponentContainer = new MyObjectBuilder_ComponentContainer
	{
		Components = new List<MyObjectBuilder_ComponentContainer.ComponentData>
		{
			new MyObjectBuilder_ComponentContainer.ComponentData { TypeId = "MyInventoryBase", Component = inventory }
		}
	};
	return container;
}

static void InitializeSerializers()
{
	CompatPatches.Apply();
	MyLog.Default = new MyLog();
	// The game's serializers report failures only through MyLog; keep that visible.
	string logPath = Environment.GetEnvironmentVariable("MAGNETAR_WORLD_LOG") ?? Path.Combine(Path.GetTempPath(), "MagnetarWorld.log");
	MyLog.Default.Init(logPath, new System.Text.StringBuilder("MagnetarWorld"), checkFilepath: false);
	MyVRageWindows.Init("MagnetarWorld", MyLog.Default, Path.GetTempPath(), detectLeaks: false, performInit: false);
	// The stock platform creates its protobuf type model only in the full Init (Windows-only
	// subsystems come with it) and the serializer captures it in its static constructor; without
	// it no protobuf read or write works. Build
	// the same dynamic model here, before LoadSerializers registers the object builder types.
	if (MyObjectBuilderSerializerKeen.Serializer == null)
	{
		var model = new VRage.Platform.Windows.Serialization.DynamicTypeModel();
		MyObjectBuilderSerializerKeen.m_typeModel = model;
		MyObjectBuilderSerializerKeen.Serializer = model.Model;
	}
	Assembly[] assemblies =
	{
		typeof(MyGlobalTypeMetadata).Assembly,
		Assembly.Load("SpaceEngineers.Game"),
		Assembly.Load("Sandbox.Game"),
		Assembly.Load("Sandbox.Common"),
		typeof(MyObjectBuilder_BatteryBlock).Assembly
	};
	MyXmlSerializerManager.RegisterSerializableBaseType(typeof(MyObjectBuilder_Base));
	foreach (Assembly assembly in assemblies)
		MyGlobalTypeMetadata.Static.RegisterAssembly(assembly);
	MyObjectBuilderSerializerKeen.LoadSerializers();

	// The partition save body stores each subtype as a MyStringHash int32; reading one back
	// yields an EMPTY subtype unless this process already maps the hash to its string. The
	// server learns every subtype from its loaded definitions, but this tool loads none - so
	// without this registration every XML product (mps-to-sector, to-vanilla) silently wrote
	// <SubtypeName /> and a server loading the result dropped the blocks. Register every
	// subtype string in the game content up front so the hashes resolve.
	string contentRoot = Environment.GetEnvironmentVariable("SPACE_ENGINEERS_ROOT") is string root && root.Length > 0
		? Path.Combine(root, "Content")
		: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".steam", "steam", "steamapps", "common", "SpaceEngineers", "Content");
	string contentData = Path.Combine(contentRoot, "Data");
	if (Directory.Exists(contentData))
	{
		foreach (string file in Directory.EnumerateFiles(contentData, "*.sbc", SearchOption.AllDirectories))
			foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
				File.ReadAllText(file), "<Subtype(?:Id|Name)>([^<]+)</Subtype(?:Id|Name)>"))
				MyStringHash.GetOrCompute(match.Groups[1].Value.Trim());
	}
	else
	{
		Console.Error.WriteLine("warning: no game content at " + contentData
			+ "; subtype hashes read from binary saves may not resolve and blocks would lose their SubtypeName");
	}
}

static void ToSplit(string source, string output)
{
	source = Path.GetFullPath(source);
	output = Path.GetFullPath(output);
	MyObjectBuilder_Sector sector = LoadSector(source);
	List<MyObjectBuilder_EntityBase> entities = sector.SectorObjects ?? new();
	List<MyObjectBuilder_EntityBase> partitionEntities = entities.Where(IsPartitionEntity).ToList();
	List<MyObjectBuilder_EntityBase> environment = entities.Where(x => !IsPartitionEntity(x)).ToList();
	int blockZones = MoveBlockSafeZones(partitionEntities, environment);
	List<List<MyObjectBuilder_EntityBase>> anchored = partitionEntities.OfType<MyObjectBuilder_CubeGrid>()
		.Where(x => x.IsStatic).Select(x => new List<MyObjectBuilder_EntityBase> { x }).ToList();
	HashSet<MyObjectBuilder_EntityBase> staticEntities = anchored.SelectMany(x => x).ToHashSet();
	List<List<MyObjectBuilder_EntityBase>> partitions = anchored.Concat(Components(partitionEntities.Where(x => !staticEntities.Contains(x)).ToList())).ToList();

	PrepareOutput(source, output);
	var catalog = new Catalog(1, source, new());
	int[] spatialSlots = anchored.Count == 0 ? SplitAlongLongestAxis(partitions) : null;
	for (int i = 0; i < partitions.Count; i++)
	{
		List<MyObjectBuilder_EntityBase> members = partitions[i];
		BoundingSphereD sphere = Sphere(members);
		bool isAnchored = members.Any(x => x is MyObjectBuilder_CubeGrid grid && grid.IsStatic);
		// The first boot spreads the seed over catalog slots 1 and 2 by static grid; every other partition
		// joins the slot of its nearest one. A world without static grids is split in space instead.
		int slot = isAnchored ? anchored.IndexOf(members) % 2 + 1 : spatialSlots != null ? spatialSlots[i] : anchored
			.Select((part, index) => (index, distance: Vector3D.DistanceSquared(sphere.Center, Position(part[0]))))
			.MinBy(x => x.distance).index % 2 + 1;
		ulong id = (ulong)i + 1;
		string relative = $"saves/P-{id:X16}/g00000001-s00000001.mps";
		byte[] file = PartitionSaveFile.Write(id, 1, 1, 0, sphere, isAnchored, members.Count, PartitionSaveFile.SerializeEntities(members), 0);
		PartitionSaveFile.WriteFile(Path.Combine(output, relative), file);
		VerifyPartition(Path.Combine(output, relative), members.Select(x => x.EntityId));
		catalog.Partitions.Add(new Entry(id, 1, 1, isAnchored, slot, members.Select(x => x.EntityId).ToArray(), relative));
	}

	sector.SectorObjects = environment;
	WriteSector(output, sector);
	File.WriteAllText(Path.Combine(output, "catalog-seed.json"), JsonSerializer.Serialize(catalog, Settings.JsonOptions) + Environment.NewLine);

	// P2-CNV-01: the split world carries an initial Final global save so recovery has a
	// durable global baseline (identities, factions, GPS, economy) before the World
	// Authority writes its first periodic one.
	string globalRelative = "saves/GLOBAL/g00000001-s00000001.mps";
	PartitionSaveFile.WriteFile(Path.Combine(output, globalRelative),
		GlobalSaveFile.Write(LoadCheckpoint(output), 1, 1, 2, 0));
	GlobalSaveFile.Read(File.ReadAllBytes(Path.Combine(output, globalRelative)), 1, 1);
	Console.WriteLine($"wrote {partitions.Count} partitions ({partitionEntities.Count} entities), {environment.Count} environment entities, and the initial global save to {output}"
		+ (blockZones > 0 ? $"; moved {blockZones} safe zone(s) into their blocks" : ""));
}

// SE1-0253: a safe zone block's zone is no partition member, and a node drops the block zones its world file
// loads (it keeps only admin zones, SE1-0251). In its block's builder, as the entry SE1-0231 restores it from,
// the zone arrives with the block on whichever node loads it. A zone whose block is not in the world stays.
static int MoveBlockSafeZones(List<MyObjectBuilder_EntityBase> partitionEntities, List<MyObjectBuilder_EntityBase> environment)
{
	Dictionary<long, MyObjectBuilder_SafeZoneBlock> blocks = partitionEntities.OfType<MyObjectBuilder_CubeGrid>()
		.SelectMany(grid => grid.CubeBlocks).OfType<MyObjectBuilder_SafeZoneBlock>().ToDictionary(block => block.EntityId);
	int moved = 0;
	foreach (MyObjectBuilder_SafeZone zone in environment.OfType<MyObjectBuilder_SafeZone>().ToList())
	{
		if (zone.SafeZoneBlockId == 0 || !blocks.TryGetValue(zone.SafeZoneBlockId, out MyObjectBuilder_SafeZoneBlock block)
			|| block.SafeZoneId != zone.EntityId)
			continue;
		BlockBuilderEntry.Write(block, BlockSafeZones.Key, BlockSafeZones.Serialize(zone));
		environment.Remove(zone);
		moved++;
	}
	return moved;
}

static bool IsPartitionEntity(MyObjectBuilder_EntityBase entity) => entity is MyObjectBuilder_CubeGrid or MyObjectBuilder_Character
	or MyObjectBuilder_FloatingObject or MyObjectBuilder_InventoryBagEntity or MyObjectBuilder_Meteor or MyObjectBuilder_Missile;

static void Materialize(string split, int slot, string output)
{
	Catalog catalog = ReadCatalog(split);
	List<MyObjectBuilder_EntityBase> entities = catalog.Partitions.Where(x => x.Slot == slot)
		.SelectMany(x => ReadPartition(Path.Combine(split, x.Save))).ToList();
	WriteVanilla(split, output, entities);
	Console.WriteLine($"materialized slot {slot} with {entities.Count} entities to {Path.GetFullPath(output)}");
}

static void ToVanilla(string split, string output, string[] dataRoots)
{
	// P2-CNV-02: reassemble a vanilla world from the NEWEST catalogued state, not the
	// static seed. Save stores are the split world's own saves plus any node/WA data
	// roots (each contributes <root>[/cluster]/saves); every partition directory found
	// anywhere is a candidate, so partitions created by later topology changes are
	// picked up and partitions dissolved by merges resolve through the generation rule
	// below. The newest Final global save supplies the checkpoint (identities,
	// factions, GPS, economy) instead of the split source's stale copy.
	List<string> stores = SaveStores(split, dataRoots);
	var chosen = new Dictionary<ulong, PartitionSaveFile.Parsed>();
	foreach (ulong id in stores.SelectMany(store => Directory.EnumerateDirectories(store, "P-*"))
		.Select(directory => ulong.Parse(Path.GetFileName(directory)[2..], System.Globalization.NumberStyles.HexNumber)).Distinct())
	{
		PartitionSaveFile.Parsed newest = NewestSave(stores, "P-" + id.ToString("X16"), id).Parsed;
		if (newest != null) chosen[id] = newest;
	}
	if (chosen.Count == 0) throw new InvalidDataException("No readable partition saves found in any save store");

	// One entity may appear in several partitions' newest saves when it moved between
	// partitions (handover, split, merge) and the old owner never saved again after
	// the move. Generations are per-partition counters and carry no cross-partition
	// order; the cluster capture time does. The destination's save necessarily
	// post-dates the move while the stale copy pre-dates it, so the copy from the
	// most recently captured save is the current one. An exact capture-time tie
	// cannot come from a move and aborts the conversion.
	var owners = new Dictionary<long, (ulong Partition, long ClusterTime, MyObjectBuilder_EntityBase Entity)>();
	foreach ((ulong id, PartitionSaveFile.Parsed save) in chosen)
		foreach (MyObjectBuilder_EntityBase entity in PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount))
		{
			if (!owners.TryGetValue(entity.EntityId, out var existing))
			{
				owners[entity.EntityId] = (id, save.ClusterTime, entity);
				continue;
			}
			if (existing.ClusterTime == save.ClusterTime)
				throw new InvalidDataException($"Entity {entity.EntityId} appears in partitions {existing.Partition} and {id} captured at the same cluster time {save.ClusterTime}");
			if (save.ClusterTime > existing.ClusterTime) owners[entity.EntityId] = (id, save.ClusterTime, entity);
			Console.WriteLine($"entity {entity.EntityId} duplicated between partitions {existing.Partition} and {id}; kept the copy captured at cluster time {Math.Max(existing.ClusterTime, save.ClusterTime)}");
		}
	List<MyObjectBuilder_EntityBase> entities = owners.Values.Select(x => x.Entity).ToList();

	(PartitionSaveFile.Parsed globalSave, string globalPath) = NewestSave(stores, "GLOBAL", 0);
	GlobalSaveFile.Bundle bundle = globalSave != null ? GlobalSaveFile.ReadBundle(File.ReadAllBytes(globalPath)) : null;
	// SE1-0252: the World Authority's admin safe zones replace the split world's, which predate any change made in game.
	MyObjectBuilder_SafeZone[] adminZones = bundle?.AdminSafeZonesRecord is string record
		? AdminSafeZones.ReadSet(record).Zones.Select(AdminSafeZones.FromXml).ToArray() : null;
	WriteVanilla(split, output, entities, adminZones);
	string checkpointSource = "split source";
	if (globalSave != null)
	{
		MyObjectBuilder_Checkpoint restored = bundle.Checkpoint;
		MyObjectBuilder_Checkpoint original = LoadCheckpoint(split);
		if (restored.WorldId != original.WorldId)
			throw new InvalidDataException("Newest global save belongs to a different world");
		if (restored.Settings != null) restored.Settings.CanJoinRunning = true;
		if (bundle.ShipStorageRecord != null) WriteStoredShips(output, restored, bundle.ShipStorageRecord);
		WriteCheckpoint(output, restored);
		checkpointSource = $"global save g{globalSave.Generation}-s{globalSave.Sequence}";
	}
	Console.WriteLine($"wrote vanilla XML/PB world with {entities.Count} entities from {chosen.Count} partitions"
		+ $" (newest generations across {stores.Count} save stores) and checkpoint from {checkpointSource} to {Path.GetFullPath(output)}"
		+ (adminZones != null ? $"; {adminZones.Length} admin safe zone(s) from the global save" : ""));
}

// SE1-0249: on a cluster the stored ships (Services terminal) live in the global save's ship storage section, not
// in the checkpoint's session component, which still holds the seed world's index, nor as gs- files in a session
// folder. Write them back the way vanilla keeps them. A ship a node had taken for a retrieval that never finished
// is exported too: the WA would have released it.
static void WriteStoredShips(string output, MyObjectBuilder_Checkpoint checkpoint, string payload)
{
	ShipStorageRecord.Record record = JsonSerializer.Deserialize<ShipStorageRecord.Record>(payload)
		?? throw new InvalidDataException("Unreadable ship storage section in the global save");
	checkpoint.SessionComponents ??= new List<MyObjectBuilder_SessionComponent>();
	var storage = checkpoint.SessionComponents.OfType<MyObjectBuilder_GridsStorageSessionComponent>().FirstOrDefault();
	if (storage == null)
		checkpoint.SessionComponents.Add(storage = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_GridsStorageSessionComponent>());
	ShipStorageRecord.User[] users = record.Users ?? Array.Empty<ShipStorageRecord.User>();
	var queued = users.SelectMany(user => user.Orders).Select(order => order.Id).ToHashSet();
	storage.GridsData = new SerializableDictionary<Guid, MyStoredGridData>();
	foreach (ShipStorageRecord.Ship ship in record.Ships ?? Array.Empty<ShipStorageRecord.Ship>())
	{
		File.WriteAllBytes(Path.Combine(output, $"gs-{ship.Id}.sbc"), ShipStorageRecord.Unpack(ship.Blueprint));
		File.WriteAllText(Path.Combine(output, $"gs-{ship.Id}-detail.sbc"), ship.Details);
		storage.GridsData.Dictionary[ship.Id] = ShipStorageRecord.ToData(ship, null, queued.Contains(ship.Id));
	}
	storage.UserData = new SerializableDictionary<long, MyObjectBuilder_GridsStorageSessionComponent.MyUserData>();
	foreach (ShipStorageRecord.User user in users)
		storage.UserData.Dictionary[user.Identity] = new MyObjectBuilder_GridsStorageSessionComponent.MyUserData
		{
			AutoDeploy = user.AutoDeploy,
			OrdersData = user.Orders.Select(order => new MyGridRetrievalOrderData
			{
				Id = order.Id,
				DisplayName = order.DisplayName,
				RemainingTimeMs = Math.Max(0, (long)(order.Due - DateTime.UtcNow).TotalMilliseconds),
				CreditsPaid = order.CreditsPaid,
				CallerEndpointId = order.CallerEndpointId,
				CallerEntityId = order.CallerEntityId,
			}).ToList(),
		};
	Console.WriteLine($"wrote {storage.GridsData.Dictionary.Count} stored ship(s) and {users.Length} retrieval queue(s)");
}

static List<string> SaveStores(string split, string[] dataRoots)
{
	var stores = new List<string> { Path.Combine(Path.GetFullPath(split), "saves") };
	stores.AddRange(dataRoots.Select(root => ResolveSaveStore(Path.GetFullPath(root))));
	return stores.Where(Directory.Exists).Distinct().ToList();
}

static string ResolveSaveStore(string full) =>
	new[] { Path.Combine(full, "cluster", "saves"), Path.Combine(full, "saves"), full }
		.FirstOrDefault(x => Directory.Exists(x) && (Directory.EnumerateDirectories(x, "P-*").Any() || Directory.Exists(Path.Combine(x, "GLOBAL"))))
		?? throw new DirectoryNotFoundException("No cluster save store under " + full);

// Reads a partition save and demands its envelope matches the g<gen>-s<seq>.mps path it
// was found under; a mismatch means a misplaced or tampered file, never a valid catalog entry.
static PartitionSaveFile.Parsed ReadValidatedSave(string path, ulong partition)
{
	string stem = Path.GetFileNameWithoutExtension(path);
	uint generation = uint.Parse(stem[1..9], System.Globalization.NumberStyles.HexNumber);
	uint sequence = uint.Parse(stem[11..19], System.Globalization.NumberStyles.HexNumber);
	PartitionSaveFile.Parsed parsed = PartitionSaveFile.ReadFile(path);
	if (parsed.PartitionId != partition || parsed.Generation != generation || parsed.Sequence != sequence)
		throw new InvalidDataException("Envelope does not match its path");
	return parsed;
}

static (PartitionSaveFile.Parsed Parsed, string Path) NewestSave(List<string> stores, string name, ulong partition)
{
	var candidates = stores.Select(store => Path.Combine(store, name)).Where(Directory.Exists)
		.SelectMany(directory => Directory.EnumerateFiles(directory, "g*-s*.mps"))
		.Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
		.Select(x => (x.Path,
			Generation: uint.Parse(x.Name[1..9], System.Globalization.NumberStyles.HexNumber),
			Sequence: uint.Parse(x.Name[11..19], System.Globalization.NumberStyles.HexNumber)))
		.OrderByDescending(x => x.Generation).ThenByDescending(x => x.Sequence)
		.ToList();
	foreach ((string path, uint _, uint _) in candidates)
	{
		try
		{
			return (ReadValidatedSave(path, partition), path);
		}
		catch (Exception error)
		{
			// Fall back to the next-newest copy: mid-write crashes legitimately leave a
			// torn newest file, and the durable-catalogue rule guarantees an older good
			// generation still exists somewhere.
			Console.WriteLine($"skipping unreadable save {path}: {error.Message}");
		}
	}
	return (null, null);
}

static void CatalogScan(string output, string[] mappings)
{
	// P2-REC-04: scan surviving node data roots and emit a /registry/rebuild payload built from
	// save headers alone. Unreadable (poison) files are skipped and listed so a lost partition is
	// visible rather than silent. A partition whose newest save's entities all live on in strictly
	// newer saves of other partitions was dissolved by a topology change (merge, drained handover);
	// cataloguing it would duplicate every entity on recovery, so it is excluded and reported.
	bool preferNewest = mappings.Contains("--prefer-newest");
	mappings = mappings.Where(x => x != "--prefer-newest").ToArray();
	var stores = new List<(string Node, string Store)>();
	var roots = new List<(string Node, string Root)>();
	foreach (string mapping in mappings)
	{
		int separator = mapping.IndexOf('=');
		if (separator <= 0) throw new ArgumentException("Catalog scan roots take the form NODE=DATA_ROOT: " + mapping);
		string full = Path.GetFullPath(mapping[(separator + 1)..]);
		stores.Add((mapping[..separator], ResolveSaveStore(full)));
		roots.Add((mapping[..separator], full));
	}
	var files = new List<(string Node, string Path, PartitionSaveFile.Parsed Parsed)>();
	var skipped = new List<object>();
	foreach ((string node, string store) in stores)
	{
		string global = Path.Combine(store, "GLOBAL");
		foreach (string directory in Directory.EnumerateDirectories(store, "P-*").Concat(Directory.Exists(global) ? new[] { global } : Array.Empty<string>()))
		{
			string name = Path.GetFileName(directory);
			ulong partition = name == "GLOBAL" ? 0 : ulong.Parse(name[2..], System.Globalization.NumberStyles.HexNumber);
			foreach (string path in Directory.EnumerateFiles(directory, "g*-s*.mps"))
			{
				try
				{
					files.Add((node, Path.GetFullPath(path), ReadValidatedSave(path, partition)));
				}
				catch (Exception error)
				{
					skipped.Add(new { Path = Path.GetFullPath(path), Error = error.Message });
					Console.WriteLine($"skipping unreadable save {path}: {error.Message}");
				}
			}
		}
	}
	// Roots can hold the same save id with different content: a capture written to disk but
	// refused registration by fencing, or a retention-deleted file not yet unlinked. The live
	// registry held at most one truth per id; with it gone the operator passes --prefer-newest
	// to resolve by cluster time (losers are listed as Superseded), or gets a hard conflict at
	// the rebuild. Copies that agree at the same cluster time stay irreconcilable either way.
	var superseded = new List<object>();
	if (preferNewest)
		files = files.GroupBy(x => (x.Parsed.PartitionId, x.Parsed.Generation, x.Parsed.Sequence)).SelectMany(group =>
		{
			var claims = group.ToList();
			if (claims.Count == 1) return claims.AsEnumerable();
			var newest = claims.OrderByDescending(x => x.Parsed.ClusterTime).First();
			if (claims.Any(x => x.Parsed.ClusterTime == newest.Parsed.ClusterTime && x.Parsed.BodyChecksum != newest.Parsed.BodyChecksum))
				throw new InvalidDataException($"Save {group.Key} conflicts at the same cluster time across scanned roots");
			foreach (var loser in claims.Where(x => x.Parsed.BodyChecksum != newest.Parsed.BodyChecksum))
			{
				superseded.Add(new { loser.Path, By = newest.Path });
				Console.WriteLine($"superseding stale save {loser.Path} with {newest.Path}");
			}
			return claims.Where(x => x.Parsed.BodyChecksum == newest.Parsed.BodyChecksum)
				.Select(x => (x.Node, x.Path, Parsed: newest.Parsed));
		}).ToList();
	var partitions = files.Where(x => x.Parsed.PartitionId != 0).GroupBy(x => x.Parsed.PartitionId)
		.ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.Parsed.Generation).ThenByDescending(x => x.Parsed.Sequence).First().Parsed);
	var owners = new Dictionary<long, (ulong Partition, long ClusterTime)>();
	var members = new Dictionary<ulong, long[]>();
	foreach ((ulong id, PartitionSaveFile.Parsed newest) in partitions)
	{
		long[] ids = PartitionSaveFile.DeserializeEntities(newest.Body, newest.EntityCount).Select(x => x.EntityId).ToArray();
		members[id] = ids;
		foreach (long entity in ids)
		{
			if (!owners.TryGetValue(entity, out var existing))
			{
				owners[entity] = (id, newest.ClusterTime);
				continue;
			}
			if (existing.ClusterTime == newest.ClusterTime)
				throw new InvalidDataException($"Entity {entity} appears in partitions {existing.Partition} and {id} captured at the same cluster time {newest.ClusterTime}");
			if (newest.ClusterTime > existing.ClusterTime) owners[entity] = (id, newest.ClusterTime);
		}
	}
	var shadowed = members.Where(x => x.Value.Length != 0 && x.Value.All(entity => owners[entity].Partition != x.Key))
		.Select(x => new { Partition = x.Key, By = x.Value.Select(entity => owners[entity].Partition).Distinct().Order().ToArray() })
		.OrderBy(x => x.Partition).ToList();
	var shadowedIds = shadowed.Select(x => x.Partition).ToHashSet();
	foreach ((ulong id, long[] ids) in members)
		if (!shadowedIds.Contains(id))
			foreach (long entity in ids.Where(entity => owners[entity].Partition != id))
				throw new InvalidDataException($"Entity {entity} is stale in live partition {id} but current in {owners[entity].Partition};"
					+ " rebuild cannot resolve a partial overlap between live partitions");
	var saves = files.Where(x => x.Parsed.PartitionId != 0 && !shadowedIds.Contains(x.Parsed.PartitionId))
		.Select(x => new { Partition = x.Parsed.PartitionId, Generation = (long)x.Parsed.Generation, Sequence = (long)x.Parsed.Sequence,
			Kind = (int)x.Parsed.Kind, SizeBytes = new FileInfo(x.Path).Length, Checksum = x.Parsed.BodyChecksum.ToString("X16"),
			ClusterTime = x.Parsed.ClusterTime, Location = x.Path, Node = x.Node }).ToList();
	var globalSaves = files.Where(x => x.Parsed.PartitionId == 0)
		.Select(x => new { Generation = (long)x.Parsed.Generation, Sequence = (long)x.Parsed.Sequence, Kind = (int)x.Parsed.Kind,
			SizeBytes = new FileInfo(x.Path).Length, Checksum = x.Parsed.BodyChecksum.ToString("X16"),
			ClusterTime = x.Parsed.ClusterTime, Location = x.Path, Node = x.Node }).ToList();
	// Voxel catalog: bases are compaction snapshots under cluster/voxel/base/<hash>/, named by the
	// storage-name.txt manifest (the hash is one-way); the retained .vjr entries above the newest base
	// give the journal tail. Outbox files are a node's own undelivered edits, not catalogued state.
	var voxelBases = new List<VoxelBaseScan>();
	var journalTails = new Dictionary<string, long>(StringComparer.Ordinal);
	foreach ((string node, string root) in roots)
	{
		string voxelRoot = Path.Combine(root, "cluster", "voxel");
		if (!Directory.Exists(voxelRoot)) continue;
		string baseRoot = Path.Combine(voxelRoot, "base");
		foreach (string directory in Directory.Exists(baseRoot) ? Directory.EnumerateDirectories(baseRoot) : Array.Empty<string>())
		{
			string manifest = Path.Combine(directory, "storage-name.txt");
			if (!File.Exists(manifest))
			{
				skipped.Add(new { Path = Path.GetFullPath(directory), Error = "Voxel base directory has no storage-name.txt manifest" });
				Console.WriteLine($"skipping unnamed voxel base directory {directory}");
				continue;
			}
			string storage = File.ReadAllText(manifest).Trim();
			foreach (string path in Directory.EnumerateFiles(directory, "base-*.vx2"))
			{
				try
				{
					long version = long.Parse(Path.GetFileNameWithoutExtension(path)[5..], System.Globalization.CultureInfo.InvariantCulture);
					byte[] bytes = File.ReadAllBytes(path);
					voxelBases.Add(new VoxelBaseScan(storage, version, version, bytes.LongLength,
						Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), Path.GetFullPath(path), node));
				}
				catch (Exception error)
				{
					skipped.Add(new { Path = Path.GetFullPath(path), Error = error.Message });
					Console.WriteLine($"skipping unreadable voxel base {path}: {error.Message}");
				}
			}
		}
		foreach (string path in Directory.EnumerateFiles(voxelRoot, "*.vjr", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(voxelRoot, path);
			if (relative.StartsWith("base" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
				|| relative.StartsWith("outbox" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
			try
			{
				VoxelJournalCodec.Entry entry = VoxelJournalCodec.Read(File.ReadAllBytes(path));
				if (!journalTails.TryGetValue(entry.StorageName, out long tail) || entry.Sequence > tail)
					journalTails[entry.StorageName] = entry.Sequence;
			}
			catch (Exception error)
			{
				skipped.Add(new { Path = Path.GetFullPath(path), Error = error.Message });
				Console.WriteLine($"skipping unreadable voxel journal entry {path}: {error.Message}");
			}
		}
	}
	var newestBases = voxelBases.GroupBy(x => x.StorageName, StringComparer.Ordinal)
		.ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.BaseVersion).First(), StringComparer.Ordinal);
	foreach (string storage in journalTails.Keys.Where(storage => !newestBases.ContainsKey(storage)).Order(StringComparer.Ordinal))
	{
		skipped.Add(new { Path = "journal:" + storage, Error = "Voxel journal entries survive but no base was scanned for the storage" });
		Console.WriteLine($"skipping voxel journal for {storage}: no scanned base");
	}
	var voxelJournals = newestBases.Values.OrderBy(x => x.StorageName, StringComparer.Ordinal)
		.Select(basis => new { basis.StorageName, basis.BaseVersion,
			TailSeq = Math.Max(basis.TailSequence, journalTails.GetValueOrDefault(basis.StorageName)), basis.Node }).ToList();
	File.WriteAllText(output, JsonSerializer.Serialize(new { Saves = saves, GlobalSaves = globalSaves,
		VoxelBases = voxelBases, VoxelJournals = voxelJournals, Shadowed = shadowed, Superseded = superseded, Skipped = skipped }) + Environment.NewLine);
	Console.WriteLine($"catalogued {saves.Count} partition save(s) across {partitions.Count - shadowedIds.Count} live partition(s)"
		+ $" and {globalSaves.Count} global save(s); {voxelBases.Count} voxel base(s) and {voxelJournals.Count} voxel journal tail(s);"
		+ $" {shadowed.Count} shadowed partition(s), {superseded.Count} superseded file(s), {skipped.Count} unreadable file(s)");
}

static void WriteVanilla(string source, string output, List<MyObjectBuilder_EntityBase> entities, MyObjectBuilder_SafeZone[] adminZones = null)
{
	PrepareOutput(source, output);
	MyObjectBuilder_Sector sector = LoadSector(source);
	sector.SectorObjects = (sector.SectorObjects ?? new()).Where(x => !IsPartitionEntity(x)
		&& !(adminZones != null && x is MyObjectBuilder_SafeZone { SafeZoneBlockId: 0 })).Concat(adminZones ?? []).Concat(entities).ToList();
	WriteSector(output, sector);
	string saves = Path.Combine(output, "saves");
	if (Directory.Exists(saves)) Directory.Delete(saves, true);
	File.Delete(Path.Combine(output, "catalog-seed.json"));
	VerifySector(output, sector.SectorObjects.Select(x => x.EntityId));
}

static MyObjectBuilder_Checkpoint LoadCheckpoint(string directory)
{
	string path = Path.Combine(directory, "Sandbox.sbc");
	if (!MyObjectBuilderSerializerKeen.DeserializeXML(path, out MyObjectBuilder_Checkpoint checkpoint) || checkpoint == null)
		throw new InvalidDataException("Cannot read " + path);
	return checkpoint;
}

static void WriteCheckpoint(string directory, MyObjectBuilder_Checkpoint checkpoint)
{
	// The dedicated server reads session settings from Sandbox_config.sbc while
	// Sandbox.sbc carries the world copy - both must agree.
	foreach (string name in new[] { "Sandbox.sbc", "Sandbox_config.sbc" })
		if (!MyObjectBuilderSerializerKeen.SerializeXML(Path.Combine(directory, name), false, checkpoint))
			throw new IOException("Cannot write " + Path.Combine(directory, name));
}

static MyObjectBuilder_Sector LoadSector(string directory)
{
	string xml = Path.Combine(directory, "SANDBOX_0_0_0_.sbs");
	string pb = xml + "B5";
	MyObjectBuilder_Sector sector;
	if (File.Exists(xml))
	{
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(xml, out sector)) throw new InvalidDataException("Cannot read " + xml);
		if (File.Exists(pb))
		{
			if (!MyObjectBuilderSerializerKeen.DeserializePB(pb, out MyObjectBuilder_Sector binary)) throw new InvalidDataException("Cannot read " + pb);
			AssertIds(binary, (sector.SectorObjects ?? new()).Select(x => x.EntityId), "XML/PB input mismatch");
		}
	}
	else if (!MyObjectBuilderSerializerKeen.DeserializePB(pb, out sector)) throw new InvalidDataException("Cannot read " + pb);
	return sector;
}

static void WriteSector(string directory, MyObjectBuilder_Sector sector)
{
	Directory.CreateDirectory(directory);
	string xml = Path.Combine(directory, "SANDBOX_0_0_0_.sbs");
	if (!MyObjectBuilderSerializerKeen.SerializeXML(xml, false, sector)) throw new IOException("Cannot write " + xml);
	if (!MyObjectBuilderSerializerKeen.SerializePB(xml + "B5", false, sector)) throw new IOException("Cannot write " + xml + "B5");
}

static List<List<MyObjectBuilder_EntityBase>> Components(List<MyObjectBuilder_EntityBase> entities)
{
	var remaining = entities.ToHashSet();
	var result = new List<List<MyObjectBuilder_EntityBase>>();
	while (remaining.Count != 0)
	{
		var component = new List<MyObjectBuilder_EntityBase>();
		var pending = new Stack<MyObjectBuilder_EntityBase>(new[] { remaining.First() });
		remaining.Remove(pending.Peek());
		while (pending.TryPop(out MyObjectBuilder_EntityBase current))
		{
			component.Add(current);
			foreach (MyObjectBuilder_EntityBase near in remaining.Where(x => Vector3D.Distance(Position(current), Position(x)) <= Proximity).ToArray())
			{
				remaining.Remove(near);
				pending.Push(near);
			}
		}
		result.Add(component);
	}
	return result;
}

// Catalog slots of a world without static grids: the partitions are ordered along the longest axis of
// their centers' bounding box and cut where the block counts of the two halves are closest. Starting
// everything on one node would leave it overloaded for minutes, since the balancer moves one partition
// a minute; a random spread would separate neighbors and cost handovers. A cut in space does neither,
// and the same world always gives the same layout.
static int[] SplitAlongLongestAxis(List<List<MyObjectBuilder_EntityBase>> partitions)
{
	int[] slots = new int[partitions.Count];
	if (partitions.Count == 0) return slots;
	Vector3D[] centers = partitions.Select(x => Sphere(x).Center).ToArray();
	Vector3D size = centers.Aggregate(Vector3D.Max) - centers.Aggregate(Vector3D.Min);
	int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
	int[] order = Enumerable.Range(0, partitions.Count).OrderBy(i => centers[i].GetDim(axis)).ThenBy(i => i).ToArray();
	long[] weights = partitions.Select(members => members.Sum(x => x is MyObjectBuilder_CubeGrid grid
		? Math.Max(1L, grid.CubeBlocks?.Count ?? 0) : 1L)).ToArray();
	long total = weights.Sum(), before = 0, best = long.MaxValue;
	int cut = 1;
	for (int count = 1; count <= order.Length; count++)
	{
		before += weights[order[count - 1]];
		long imbalance = Math.Abs(total - 2 * before);
		if (imbalance < best) { best = imbalance; cut = count; }
	}
	for (int rank = 0; rank < order.Length; rank++) slots[order[rank]] = rank < cut ? 1 : 2;
	return slots;
}

static Vector3D Position(MyObjectBuilder_EntityBase entity) => entity.PositionAndOrientation?.Position ?? Vector3D.Zero;

static BoundingSphereD Sphere(List<MyObjectBuilder_EntityBase> entities)
{
	Vector3D center = entities.Aggregate(Vector3D.Zero, (sum, x) => sum + Position(x)) / entities.Count;
	return new BoundingSphereD(center, entities.Max(x => Vector3D.Distance(center, Position(x))));
}

static MyObjectBuilder_EntityBase[] ReadPartition(string path)
{
	PartitionSaveFile.Parsed save = PartitionSaveFile.ReadFile(path);
	MyObjectBuilder_EntityBase[] entities = PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount);
	VerifyPartition(path, entities.Select(x => x.EntityId));
	return entities;
}

static void VerifyPartition(string path, IEnumerable<long> expected)
{
	PartitionSaveFile.Parsed save = PartitionSaveFile.ReadFile(path);
	AssertEntityIds(PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount), expected, "MPS readback mismatch: " + path);
}

static void VerifySector(string directory, IEnumerable<long> expected)
{
	MyObjectBuilder_Sector sector = LoadSector(directory);
	AssertIds(sector, expected, "vanilla readback mismatch");
}

static void AssertIds(MyObjectBuilder_Sector sector, IEnumerable<long> expected, string message) =>
	AssertEntityIds(sector.SectorObjects ?? new(), expected, message);

static void AssertEntityIds(IEnumerable<MyObjectBuilder_EntityBase> actual, IEnumerable<long> expected, string message)
{
	if (!actual.Select(x => x.EntityId).Order().SequenceEqual(expected.Order())) throw new InvalidDataException(message);
}

static void PrepareOutput(string source, string output)
{
	source = Path.GetFullPath(source);
	output = Path.GetFullPath(output);
	if (Directory.Exists(output)) Directory.Delete(output, true);
	Directory.CreateDirectory(output);
	foreach (string file in Directory.EnumerateFiles(source))
	{
		string name = Path.GetFileName(file);
		if (name is "SANDBOX_0_0_0_.sbs" or "SANDBOX_0_0_0_.sbsB5" or "catalog-seed.json") continue;
		File.Copy(file, Path.Combine(output, name));
	}
	// Mod world storage (Relative Top Speed's and MES's configs): every node seeds its world from this
	// output, and a mod that finds no file writes its defaults (DF-695).
	string storage = Path.Combine(source, "Storage");
	if (Directory.Exists(storage))
		foreach (string file in Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories))
		{
			string copy = Path.Combine(output, Path.GetRelativePath(source, file));
			Directory.CreateDirectory(Path.GetDirectoryName(copy));
			File.Copy(file, copy);
		}
	foreach (string checkpoint in new[] { "Sandbox.sbc", "Sandbox_config.sbc" })
	{
		string path = Path.Combine(output, checkpoint);
		if (File.Exists(path)) File.WriteAllText(path, File.ReadAllText(path).Replace("<CanJoinRunning>false</CanJoinRunning>", "<CanJoinRunning>true</CanJoinRunning>"));
	}
}

static Catalog ReadCatalog(string directory) => JsonSerializer.Deserialize<Catalog>(File.ReadAllText(Path.Combine(directory, "catalog-seed.json")), Settings.JsonOptions)
	?? throw new InvalidDataException("Invalid catalog-seed.json");

}
}

sealed record Catalog(int FormatVersion, string Source, List<Entry> Partitions);
sealed record VoxelBaseScan(string StorageName, long BaseVersion, long TailSequence, long SizeBytes, string Checksum, string Location, string Node);
sealed record Entry(ulong Id, uint Generation, uint Sequence, bool Anchored, int Slot, long[] Members, string Save);
static class Settings
{
	public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
