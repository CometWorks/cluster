using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace ClusterPlugin;

/// <summary>
/// Partitioning.md 6.10: the handover round-trip gate's comparison and its governed normalizer.
/// The target re-serializes every imported entity and compares against the frozen source object
/// builders; the residual differences that survive normalization are the gate's verdict (empty =
/// the import is faithful). Exactly three classes of difference may be normalized away - order noise
/// from unordered containers (canonicalized by sorting, never erased), late-resolved fields the
/// engine writes but does not restore (erased symmetrically, no more broadly than the load path
/// justifies), and per-node DERIVED state the engine recomputes from local inputs after load, which
/// the two nodes are right to disagree about (PlayerPresenceTier; a seated character's pose is the
/// same class) - and every entry cites the engine code path that proves its class. Lives in its own
/// file so ClusterPlugin.CodecTest can link it and replay fixtures without the game.
///
/// Numeric comparison is not exact: see ValuesEqual and EpsilonFor. The pose and velocity tolerances
/// exist because a moving entity keeps moving inside its own freeze window, the safe zone countdown
/// one because those countdowns run on each node's own game clock.
/// </summary>
internal static class ImportRoundTrip
{
	private static readonly XName XsiType = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type";

	internal static List<string> Residuals(MyObjectBuilder_EntityBase[] source, MyObjectBuilder_EntityBase[] target, HashSet<long> displacedPilots = null)
		=> ResidualsOfDocuments(ToDocument(source), ToDocument(target), displacedPilots);

	/// <summary>
	/// The source half of the comparison, serialised and normalised ahead of time, in contiguous
	/// chunks of entities so the target half can be built and walked in parallel. The staged import
	/// builds it on a pool thread while the workers construct the entities, so the frozen window
	/// pays only the target half (compareMs p95 was 784 ms on w22 before this, half of it the source
	/// document; a 60-entity partition still paid 460-650 ms single-threaded). Chunking changes no
	/// residual path: entities are addressed by EntityId, never by ordinal. Single use:
	/// ResidualsAgainstPrepared mutates the chunks.
	/// </summary>
	internal sealed class PreparedSource
	{
		internal XDocument[] Chunks;
		internal int[] Starts;
		internal int Count;
		/// <summary>The builders the chunks were serialised from, left untouched by the comparison.</summary>
		internal MyObjectBuilder_EntityBase[] Source;
	}

	internal static int DefaultChunks(int entities)
		=> Math.Max(1, Math.Min(entities, Math.Min(8, Environment.ProcessorCount / 2)));

	internal static PreparedSource PrepareSource(MyObjectBuilder_EntityBase[] source, int chunks = 0)
	{
		if (chunks <= 0) chunks = DefaultChunks(source.Length);
		int[] starts = ChunkStarts(source.Length, chunks);
		XDocument[] documents = new XDocument[starts.Length - 1];
		System.Threading.Tasks.Parallel.For(0, documents.Length, index =>
		{
			XDocument left = ToDocument(source.AsSpan(starts[index], starts[index + 1] - starts[index]).ToArray());
			NormalizeDocument(left);
			documents[index] = left;
		});
		return new PreparedSource { Chunks = documents, Starts = starts, Count = source.Length, Source = source };
	}

	/// <summary>
	/// The SHAPE of a residual list (CH 2026-10-08 10:08, the import warm-up's constant residuals=32): which fields
	/// differ and how, without the instance - entity ids (#N) dropped from the path, digits in the message masked,
	/// the sector prefix shortened - one distinct entry per field, sorted. <paramref name="hash"/> is the first 8
	/// hex of the SHA-256 of the entries joined, so a boot whose residuals name another field (or one more, or one
	/// fewer) logs another hash, while one whose ids or values merely differ logs the same.
	/// </summary>
	internal static List<string> ResidualShape(IEnumerable<string> residuals, out string hash)
	{
		SortedSet<string> fields = new SortedSet<string>(StringComparer.Ordinal);
		foreach (string residual in residuals)
		{
			int colon = residual.IndexOf(": ", StringComparison.Ordinal);
			string path = colon < 0 ? residual : residual.Substring(0, colon);
			string message = colon < 0 ? "" : residual.Substring(colon + 2);
			path = System.Text.RegularExpressions.Regex.Replace(path, "#-?[0-9]+", "")
				.Replace("MyObjectBuilder_Sector/SectorObjects/", "").Replace("MyObjectBuilder_", "");
			message = System.Text.RegularExpressions.Regex.Replace(message, "[0-9]+(\\.[0-9]+)?", "N");
			fields.Add(path + ": " + message);
		}
		List<string> shape = fields.ToList();
		hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", shape)))).Substring(0, 8).ToLowerInvariant();
		return shape;
	}

	internal static List<string> ResidualsAgainstPrepared(PreparedSource prepared, MyObjectBuilder_EntityBase[] target)
	{
		if (target.Length != prepared.Count)
			throw new ArgumentException("Prepared source holds " + prepared.Count + " entities, target " + target.Length);
		List<string>[] partial = new List<string>[prepared.Chunks.Length];
		System.Threading.Tasks.Parallel.For(0, partial.Length, index =>
		{
			int start = prepared.Starts[index], length = prepared.Starts[index + 1] - start;
			XDocument right = ToDocument(target.AsSpan(start, length).ToArray());
			NormalizeDocument(right);
			partial[index] = ResidualsOfNormalizedDocuments(prepared.Chunks[index], right, null);
		});
		List<string> residuals = new List<string>();
		foreach (List<string> part in partial)
		{
			foreach (string entry in part)
			{
				if (residuals.Count >= ResidualCap) return residuals;
				residuals.Add(entry);
			}
		}
		return residuals;
	}

	private static int[] ChunkStarts(int count, int chunks)
	{
		chunks = Math.Max(1, Math.Min(chunks, Math.Max(1, count)));
		int[] starts = new int[chunks + 1];
		for (int index = 0; index <= chunks; index++) starts[index] = (int)((long)count * index / chunks);
		return starts;
	}

	/// <summary>
	/// Compares two raw serialized sector documents (a retained handover-diff dump pair replays
	/// through here unchanged). Normalizes both in place, then walks them collecting residual
	/// entries of the form "path: left != right". ClusterSeams.CorruptImportedDocument, when the test
	/// plugin has set it, perturbs the target document after normalization, so it deterministically
	/// defeats the comparison (P3-HND-03); it is null in every release deployment.
	/// </summary>
	internal static List<string> ResidualsOfDocuments(XDocument left, XDocument right, HashSet<long> displacedPilots = null)
	{
		NormalizeDocument(left);
		NormalizeDocument(right);
		if (displacedPilots != null && displacedPilots.Count > 0)
		{
			StripDisplacedPilots(left, right, displacedPilots);
		}
		return ResidualsOfNormalizedDocuments(left, right, ClusterSeams.CorruptImportedDocument);
	}

	/// <summary>
	/// T-0051: a pilot the target deliberately did not seat (the same character is already seated here in
	/// the live cockpit - see ClusterControlServer.ResolveGhostSeats) is removed from the comparison on
	/// both sides: the cockpit's pilot-derived fields and the character's entry among the cockpit's
	/// hierarchy children. Everything else about the cockpit and the grid stays validated.
	/// </summary>
	/// <summary>
	/// A displaced pilot is absent from one side by design (unseated locally, ignored or stripped on
	/// arrival), so its seat is normalised on BOTH sides by cockpit id: the side that lost the pilot
	/// still serialises the seat's pilot fields as nil elements (PilotJetpackEnabled, PilotRelativeWorld
	/// ...), and matching only the side that still names the pilot left those nils against nothing -
	/// the Earth-base blob blocked on exactly that after a stale seated copy was stripped (w29
	/// 2026-09-08 00:38, signature 0ae3b99db69dbae8).
	/// </summary>
	private static void StripDisplacedPilots(XDocument left, XDocument right, HashSet<long> displacedPilots)
	{
		string[] pilotFields = { "Pilot", "PilotJetpackEnabled", "PilotRelativeWorld", "PilotGunDefinition", "IsInFirstPersonView", "OxygenLevel" };
		XDocument[] both = { left, right };
		var seats = new HashSet<long>();
		// The seat is named by the legacy Pilot element OR by the cockpit's hierarchy Children - the
		// retained 29128 dumps (w29 2026-09-08 05:14) carry the pilot ONLY as a hierarchy child on
		// both sides, so matching Pilot alone found no seat and stripped nothing.
		foreach (XDocument document in both)
			foreach (XElement cockpit in document.Descendants("MyObjectBuilder_CubeBlock"))
			{
				if (!long.TryParse(cockpit.Element("EntityId")?.Value, out long cockpitId)) continue;
				IEnumerable<XElement> embedded = cockpit.Elements("Pilot")
					.Concat(cockpit.Descendants("Children").Elements("MyObjectBuilder_EntityBase"));
				if (embedded.Any(pilot => long.TryParse(pilot.Element("EntityId")?.Value, out long pilotId) && displacedPilots.Contains(pilotId)))
					seats.Add(cockpitId);
			}
		foreach (XDocument document in both)
		{
			foreach (XElement cockpit in document.Descendants("MyObjectBuilder_CubeBlock").ToList())
				if (long.TryParse(cockpit.Element("EntityId")?.Value, out long cockpitId) && seats.Contains(cockpitId))
					foreach (string field in pilotFields) cockpit.Elements(field).Remove();
			foreach (XElement child in document.Descendants("Children").Elements("MyObjectBuilder_EntityBase").ToList())
				if (long.TryParse(child.Element("EntityId")?.Value, out long childId) && displacedPilots.Contains(childId))
					child.Remove();
		}
	}

	private static List<string> ResidualsOfNormalizedDocuments(XDocument left, XDocument right, Action<XDocument, List<string>> corrupt)
	{
		StripLazyBlockEntityIds(left, right);
		List<string> residuals = new List<string>();
		// The prepared (chunked) path passes null: a chunk is a partial document, and the caller
		// bypasses that path entirely while the seam is set.
		corrupt?.Invoke(right, residuals);
		Collect(left.Root, right.Root, left.Root?.Name.LocalName ?? "?", residuals);
		return residuals;
	}

	/// <summary>
	/// The stable identity of one residual diff: a hash over the residual paths with values stripped,
	/// so consecutive aborts of the same underlying gap carry the same signature (Orchestration.md 3.3
	/// counts them toward HandoverBlocked) while the same partition failing a different way starts a
	/// new count.
	/// </summary>
	internal static string Signature(List<string> residuals)
	{
		IEnumerable<string> paths = residuals
			.Select(entry => { int split = entry.IndexOf(": ", StringComparison.Ordinal); return split < 0 ? entry : entry.Substring(0, split); })
			.Distinct().OrderBy(path => path, StringComparer.Ordinal);
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", paths)));
		return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
	}

	/// <summary>
	/// A cube block's EntityId is allocated LAZILY - first interaction, a subpart, or the import
	/// path itself touching the block - so a moved grid's re-emit can carry block ids the frozen
	/// source never allocated (partition 825, 2026-08-16: two plain armor blocks gained EntityId on
	/// import, and the abort repeated to a 3600 s backoff because the source can never grow them).
	/// Strip the id only where exactly ONE side has it for the same block of the same grid: a block
	/// id that CHANGED across the handover still fails the comparison, as it must - other blocks
	/// reference each other by these ids. Runs after normalization so the positional pairing walks
	/// two identically-ordered documents.
	/// </summary>
	private static void StripLazyBlockEntityIds(XDocument left, XDocument right)
	{
		Dictionary<string, XElement> leftGrids = left.Descendants("MyObjectBuilder_EntityBase")
			.Where(grid => grid.Element("EntityId") != null && grid.Element("CubeBlocks") != null)
			.GroupBy(grid => grid.Element("EntityId").Value)
			.ToDictionary(group => group.Key, group => group.First());
		foreach (XElement rightGrid in right.Descendants("MyObjectBuilder_EntityBase"))
		{
			string id = rightGrid.Element("EntityId")?.Value;
			if (id == null || rightGrid.Element("CubeBlocks") == null
				|| !leftGrids.TryGetValue(id, out XElement leftGrid)) continue;
			XElement[] leftBlocks = leftGrid.Element("CubeBlocks").Elements().ToArray();
			XElement[] rightBlocks = rightGrid.Element("CubeBlocks").Elements().ToArray();
			for (int index = 0; index < Math.Min(leftBlocks.Length, rightBlocks.Length); index++)
			{
				XElement a = leftBlocks[index].Element("EntityId");
				XElement b = rightBlocks[index].Element("EntityId");
				if (a == null != (b == null)) (a ?? b).Remove();
			}
		}
	}

	private static XDocument ToDocument(MyObjectBuilder_EntityBase[] entities)
	{
		using MemoryStream stream = new MemoryStream();
		try
		{
			MyXmlSerializerManager.GetSerializer(typeof(MyObjectBuilder_Sector)).Serialize(stream, new MyObjectBuilder_Sector { SectorObjects = entities.ToList() });
		}
		catch (Exception e)
		{
			throw new InvalidDataException("Cannot serialize imported handover state: " + e.GetBaseException().Message, e);
		}
		stream.Position = 0;
		return XDocument.Load(stream);
	}

	// The fields MyEntityIdRemapHelper rewrites besides the grid and block EntityIds: each
	// MyObjectBuilder_*.Remap override that calls RemapEntityId (connector, mechanical connections,
	// remote control camera, toolbar terminal items, turret controller, sub-block grids) and the id
	// lists of the emotion, event and combat controllers.
	private static readonly HashSet<string> RemappedReferences = new HashSet<string>(StringComparer.Ordinal)
	{
		"ConnectedEntityId", "TopBlockId", "RotorEntityId", "WeldedEntityId", "BindedCamera", "BlockEntityId",
		"AzimuthId", "ElevationId", "CameraId", "SubGridId",
	};

	private static readonly HashSet<string> RemappedReferenceLists = new HashSet<string>(StringComparer.Ordinal)
	{
		"SelectedLCDsIds", "SelectedBlocks", "SelectedWeapons", "ToolIds",
	};

	/// <summary>
	/// #1245 (octahedron-1191, 2026-10-20: 4608 residual lines on 194 partitions, every one of them
	/// HandoverBlocked in the end): a projector's blueprint comes back under new entity ids. The server
	/// projector remaps its blueprint in place whenever it re-registers - MyProjectorBase.Remap from
	/// OnRegisteredToGridSystems, a grid split or an offset change, MyEntities.RemapObjectBuilder over
	/// m_originalGridBuilders - so the ids under ProjectedGrids are fresh MyEntityIdentifier allocations,
	/// reserved for the blocks a welder will build (MyBlueprintIdTracker), not the identity of anything in
	/// the world. A world reload does the same. The comparison read the grid as one missing and one extra.
	///
	/// So a projection's ids compare by position, not by value: each grid and block EntityId under a
	/// projector's ProjectedGrids becomes its ordinal there (-1, -2, ...), and every leaf that carried one
	/// of those ids - a toolbar slot, a rotor's top, an LCD list - carries the same ordinal, so a reference
	/// that points at another block than before still fails. The remap also rewrites references that leave
	/// the blueprint (RemapEntityId allocates for any id it is given); those become "external". MultiBlockId
	/// is remapped to a random group id, so it is numbered the same way, and a grid's or block's Name is
	/// overwritten with its new id. Runs first: the sorts below key on the text these ids are part of.
	/// </summary>
	private static void CanonicalizeProjectedGridIds(XDocument document)
	{
		foreach (XElement projection in document.Descendants("ProjectedGrids").Where(element => !element.Ancestors("ProjectedGrids").Any()).ToList())
		{
			var ordinals = new Dictionary<string, string>(StringComparer.Ordinal);
			var multiBlocks = new Dictionary<string, string>(StringComparer.Ordinal);
			List<XElement> owners = projection.Descendants()
				.Where(element => element.Name == "MyObjectBuilder_CubeGrid" || element.Name == "MyObjectBuilder_CubeBlock").ToList();
			foreach (XElement owner in owners)
			{
				string id = owner.Element("EntityId")?.Value;
				if (!string.IsNullOrEmpty(id) && id != "0" && !ordinals.ContainsKey(id))
					ordinals.Add(id, (-(ordinals.Count + 1)).ToString(CultureInfo.InvariantCulture));
				owner.Elements("Name").Remove();
			}
			foreach (XElement leaf in projection.Descendants().Where(element => !element.HasElements).ToList())
			{
				if (ordinals.TryGetValue(leaf.Value, out string ordinal))
					leaf.Value = ordinal;
				else if (leaf.Name == "MultiBlockId" && leaf.Value != "0")
				{
					if (!multiBlocks.TryGetValue(leaf.Value, out ordinal))
						multiBlocks.Add(leaf.Value, ordinal = (-(multiBlocks.Count + 1)).ToString(CultureInfo.InvariantCulture));
					leaf.Value = ordinal;
				}
				else if ((RemappedReferences.Contains(leaf.Name.LocalName) || RemappedReferenceLists.Contains(leaf.Parent?.Name.LocalName ?? ""))
					&& long.TryParse(leaf.Value, out long reference) && reference != 0)
					leaf.Value = "external";
			}
		}
	}

	/// <summary>
	/// A quaternion and its negation are the SAME rotation - the unit quaternions double-cover SO(3),
	/// so q and -q rotate every vector identically. The residual walk compares X/Y/Z/W element by
	/// element with an absolute epsilon, which cannot see that all four siblings flipped together: a
	/// sign flip reads as a ~1.17 difference against OrientationEpsilon of 1e-3 and aborts the
	/// handover.
	///
	/// Observed live on w22, 2026-08-21 14:56:48 CEST, partition 4333: ONE entity of 215 came back
	/// negated, produced exactly 4 residuals (X, Y, Z, W), and aborted the transfer after 486 ms of
	/// validation. The other 20 orientations that differed in that same document were float rounding
	/// (dot = +1.000000) and were correctly tolerated.
	///
	/// Canonicalizing the sign on BOTH documents removes the false positive without weakening the
	/// gate: a real divergence separates by metres and degrees (handover-duplicate-incarnation.md)
	/// and still fails, because canonicalization maps q and -q to the same representative and leaves
	/// every genuinely different rotation different.
	/// </summary>
	private static void CanonicalizeQuaternionSign(XDocument document)
	{
		foreach (XElement orientation in document.Descendants("Orientation"))
		{
			XElement x = orientation.Element("X"), y = orientation.Element("Y");
			XElement z = orientation.Element("Z"), w = orientation.Element("W");
			if (x == null || y == null || z == null || w == null) continue;
			if (!TryValue(x, out double xv) || !TryValue(y, out double yv)
				|| !TryValue(z, out double zv) || !TryValue(w, out double wv)) continue;
			// Pick the representative by the first component that is not (near) zero, W first, so the
			// choice is deterministic and independent of which side we are looking at.
			double lead = Math.Abs(wv) > 1e-12 ? wv
				: Math.Abs(zv) > 1e-12 ? zv
				: Math.Abs(yv) > 1e-12 ? yv : xv;
			if (lead >= 0) continue;
			Write(x, -xv); Write(y, -yv); Write(z, -zv); Write(w, -wv);
		}

		static bool TryValue(XElement element, out double value)
			=> double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

		static void Write(XElement element, double value)
			=> element.Value = value.ToString("R", CultureInfo.InvariantCulture);
	}

	// The vanilla skins whose AssetModifier sets a DefaultColor (Content/Data/AssetModifiers/ArmorModifiers.sbc,
	// game 1.210). Used as they are offline (no definitions loaded), and joined by any mod skin with a
	// DefaultColor once the session's definitions are loaded.
	private static readonly string[] VanillaDefaultColorSkins = { "Golden_Armor", "Silver_Armor" };
	private static HashSet<string> m_defaultColorSkins;

	private static HashSet<string> DefaultColorSkins()
	{
		HashSet<string> skins = m_defaultColorSkins;
		if (skins != null) return skins;
		skins = new HashSet<string>(VanillaDefaultColorSkins, StringComparer.Ordinal);
		try
		{
			Sandbox.Definitions.MyDefinitionManager definitions = Sandbox.Definitions.MyDefinitionManager.Static;
			if (definitions == null || definitions.Loading || definitions.GetAssetModifierDefinitions().Count == 0) return skins;
			foreach (Sandbox.Definitions.MyAssetModifierDefinition skin in definitions.GetAssetModifierDefinitions())
				if (skin.DefaultColor.HasValue) skins.Add(skin.Id.SubtypeName);
			m_defaultColorSkins = skins;
		}
		catch (Exception)
		{
			// Offline replay without the game's definitions: the vanilla list.
		}
		return skins;
	}

	private static void NormalizeDocument(XDocument document)
	{
		CanonicalizeProjectedGridIds(document);
		CanonicalizeQuaternionSign(document);
		document.Descendants("SubtypeName").Where(element => string.IsNullOrEmpty(element.Value)).Remove();
		document.Descendants("UpdateStartOfFirstTexture").Remove();
		// A cycling panel's current slide is a clock reading, not state. MyTextPanelComponent.UpdateTexture
		// re-derives it from (ElapsedGameTime - UpdateStartOfFirstTexture) / ChangeInterval: this node's
		// session clock against the per-node stamp erased just above. Two nodes with different uptimes show
		// different slides of the same slideshow (octahedron-1191, 2026-10-20, #1216: 0 != 1, 1 != 2 ... on
		// seven partitions, every one a panel cycling 2-3 images every 1-3 s). Scoped to a panel that cycles:
		// a static panel shows the slide Init restores, so there it stays compared. The images and the
		// interval stay compared everywhere.
		document.Descendants("CurrentShownTexture")
			.Where(element => double.TryParse(element.Parent?.Element("ChangeInterval")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double interval)
				&& interval > 0)
			.Remove();
		// The image an Emotion Controller shows is its output, not state. MyEmotionControllerComponent writes
		// the first panel of every LCD in SelectedLCDsIds from SelectedEmotionSubtype
		// (SelectedEmotionSubtype_ValueChanged), and its once-before-frame pass first resets those panels to
		// the default texture (SelectedLCDsIds_ValueChanged -> SetDefaultTexture). So an imported controller
		// can re-emit "Online" against a source showing LCD_Emote_Neutral while both sides carry the same
		// SelectedEmotionSubtype (octahedron-1191, 2026-10-20, #1216: Unknown Signal pods on ten
		// partitions). The emotion and the LCD list stay compared, so a lost emotion still fails; only the
		// first panel's image list of a driven LCD leaves the comparison.
		HashSet<string> driven = new HashSet<string>(document.Descendants("SelectedLCDsIds").Elements().Select(element => element.Value));
		if (driven.Count > 0)
			foreach (XElement block in document.Descendants("MyObjectBuilder_CubeBlock")
				.Where(block => driven.Contains(block.Element("EntityId")?.Value ?? "")).ToList())
				block.Descendants().FirstOrDefault(element => element.Name == "MySerializedTextPanelData" || element.Name == "TextPanelContent")
					?.Elements("SelectedImages").Remove();
		// A remote control's autopilot speed limit can be rewritten by mod code after the engine restored it.
		// MyAutopilotComponent.InitWithObjectBuilder restores AutopilotSpeedLimit in the same pass as
		// AutoPilotEnabled, FlightMode, the waypoints and the other siblings, which all round-trip. But the
		// RelativeTopSpeed mod (workshop 1359618037, RemoteControl.cs) sets every remote control's SpeedLimit
		// to its RemoteControlSpeedLimit setting (100) once the block updates, while MES had set the NPC
		// cargo ship's limit to its prefab speed (CargoShipWatcher.AutopilotToCoords). The import re-emitted
		// 100 against the source's 25 on every attempt (octahedron-1191, 2026-10-20, #1216: RivalAI remote
		// controls on THIE-* ships, 39 partitions, 14 still HandoverBlocked at the end of the run). The
		// siblings stay compared, so a lost autopilot builder still fails.
		document.Descendants("AutopilotSpeedLimit")
			.Where(element => (string)element.Parent?.Attribute(XsiType) == "MyObjectBuilder_AutopilotComponent")
			.Remove();
		document.Descendants("InventoryFlags").Remove();
		// An unconnected ship connector's master flag is dead state: MyShipConnector.Init reads IsMaster
		// only when ConnectedEntityId is set, so a block that loads unconnected reports false whatever
		// the save said. A scenario grid saved with it true otherwise fails every handover forever.
		document.Descendants("IsMaster").Where(element => element.Parent?.Element("ConnectedEntityId") == null).Remove();
		document.Descendants("EnabledComponents").Elements("string").Where(element => element.Value == "RagdollComponent").Remove();
		// A text panel's colours belong to its LcdSurfaceComponent: MyTextPanel.GetObjectBuilderCubeBlock
		// nulls the block-level copies whenever that component exists, and a converted stock world's copies
		// are stale anyway - one observed block carried white here against the component's orange, and the
		// component is what renders. The component's own copies stay in the comparison and must still agree.
		document.Descendants().Where(element => element.Parent?.Name == "MyObjectBuilder_CubeBlock"
			&& (element.Name == "FontColor" || element.Name == "BackgroundColor"
				|| element.Name == "ScriptBackgroundColor" || element.Name == "ScriptForegroundColor")).Remove();
		// A skinned block's colour can belong to the skin. MyCubeBlock.UpdateSkin sets a fat block's ColorMaskHSV
		// to its skin's AssetModifier DefaultColor (ColorToHSVDX11), and MyCubeBlock.Init runs it, as does every
		// UpdateVisual. So a loaded block with such a skin has the skin's colour whatever its builder said: a DS
		// probe loaded a Golden_Armor block saved as 0.275/-0.8/-0.45 and got 0.14583/0.69412/-0.33333, which is
		// Golden_Armor's (85,76,13). The MES Wrecks-N grids carry these skins ([AssignGridSkin:...]), and their
		// source builders still had the spawn colour on a few blocks: every import re-coloured them and aborted
		// (octahedron-1191c, 2026-10-21, #1382: Wrecks-52 blocked six partitions in turn). For such a block the
		// colour derives from SkinSubtypeId, and SkinSubtypeId stays compared. So does every other block's colour.
		HashSet<string> skins = DefaultColorSkins();
		document.Descendants("ColorMaskHSV")
			.Where(element => element.Parent?.Name == "MyObjectBuilder_CubeBlock"
				&& skins.Contains(element.Parent.Element("SkinSubtypeId")?.Value ?? ""))
			.Remove();
		// A meteor's basis carries its size. MyMeteor.InitInternal sets PositionComp.Scale from the ore amount
		// ((Amount * Volume / VoxelDebrisModelVolume)^0.333) after base.Init placed the entity, so a loaded
		// meteor's world matrix, and the Forward/Up written from it, is that long. A meteor spawned live
		// is not: MyMeteor.SetSpawnSettings assigns a unit matrix after Init. So a shower's fresh meteors came
		// back 1.08-1.54 times as long, pointing the same way to the last digit, with identical Orientation
		// quaternions (octahedron-1191, 2026-10-20, #1253: 6 pairs, partitions 36 and 1387, |Forward|^3 /
		// Amount the same constant for all 20 meteors of one pair). Compare the direction: rescale both
		// vectors to unit length. The Orientation quaternion and the position stay compared as they are.
		foreach (XElement pose in document.Descendants("MyObjectBuilder_EntityBase")
			.Where(entity => (string)entity.Attribute(XsiType) == "MyObjectBuilder_Meteor")
			.Elements("PositionAndOrientation"))
			foreach (XElement vector in pose.Elements().Where(element => element.Name == "Forward" || element.Name == "Up"))
				NormalizeLength(vector);
		// A grid's oxygen rooms are rebuilt by the gas system's first pressurization pass. The import did
		// take the values - MyCubeGridSystems.Init feeds them to GasSystem.Init - but GetOxygenAmount
		// returns null until m_rooms exists, so a grid validated in the same frame cannot re-emit what it
		// was just given.
		document.Descendants("OxygenRooms").Remove();
		// A grid's player-presence tier is a per-node update-rate hint, not world state a handover
		// carries. MyCubeGrid.Init does restore it from the builder on a dedicated server
		// (MyCubeGrid.cs:4442), but MyTieredUpdateSystem.ChangePlayerTier immediately re-derives it
		// from which players are near THIS node - so the source, whose players have just left, and the
		// target, whose player is right there, correctly disagree and can never converge. Same class
		// as a seated character's pose: calculated per node, so validating it compares two derivations
		// of different inputs (Viktor, 2026-08-15).
		document.Descendants("PlayerPresenceTier").Remove();
		// Same class, grid flavour: MyTieredUpdateSystem.GridPresenceUpdate -> ChangeGridTier re-derives
		// GridPresenceTier from whether any grid is present near THIS node's players, and fires it off
		// the replication/presence callbacks. First seen as a residual only with import.mode=staged
		// (w22 2026-08-21 17:21:14, partition 4853 onto a 13 s old node-5: four grids came back
		// Tier1 against a source that omitted the element; the retry 6 s later passed), because the
		// staged activate frame finalises network ids - and so lets replication react - before it
		// collects the builders, while the legacy frame collected them first. Either order is a
		// per-node derivation, never transferred state.
		document.Descendants("GridPresenceTier").Remove();
		// Third member of the same family: MyTimerComponent.TimerTickInFrames is the block's UPDATE
		// PERIOD, re-derived per node from the block's tier (MyFunctionalBlock.GetTimerTime ->
		// BlockDefinition.TieredUpdateTimes[tier], set by MyTieredUpdateSystem from how near THIS
		// node's players are). Proof it is not per-block state: in one frozen source builder the SAME
		// subtype carries three different values - SmallBlockSmallAtmosphericThrustSciFi 614x100,
		// 171x3600, 28x0 (w22 2026-08-21 21:38, partition 6849, 29 aborts with residuals only on this
		// field). The countdown itself, FramesFromLastTrigger, IS state and stays compared; only the
		// period is dropped. This supersedes the reading in
		// findings/import-entities-per-frame-resets-timer-components.md, where the 0/100/320 values
		// were read as a component re-arm rather than as tier periods.
		document.Descendants("TimerTickInFrames").Remove();
		// A SEATED character's velocity is derived from the seat, not carried state. The source
		// freezes a pilot mid-flight and emits the ship's velocity for the character (60.4/43.1/81.3
		// m/s on partition 7683, w22 2026-08-21 23:23); the target has just re-seated the same
		// character, whose body is attached to the cockpit and has not been stepped, so it emits
		// zeros. One frame later both agree again. Same family as PilotRelativeWorld above, and the
		// dominant abort class of w17. Only characters that appear as a Pilot are normalised - a free
		// character keeps its velocity validated, which is what catches a dropped physics state.
		HashSet<string> seated = new HashSet<string>(document.Descendants("Pilot")
			.Select(pilot => pilot.Element("EntityId")?.Value).Where(id => !string.IsNullOrEmpty(id)));
		if (seated.Count > 0)
		{
			foreach (XElement pilot in document.Descendants("Pilot"))
			{
				pilot.Elements("LinearVelocity").Remove();
				pilot.Elements("AngularVelocity").Remove();
			}
			foreach (XElement entity in document.Descendants("MyObjectBuilder_EntityBase")
				.Where(entity => seated.Contains(entity.Element("EntityId")?.Value)).ToList())
			{
				entity.Elements("LinearVelocity").Remove();
				entity.Elements("AngularVelocity").Remove();
			}
		}
		// A FREE character's linear velocity is per-node derived state too. MyCharacter.Init does
		// restore it (MyCharacter.cs:3162, Physics.LinearVelocity = builder.LinearVelocity) - the
		// import carries it faithfully - but the Havok character controller re-solves the proxy's
		// velocity every step from the movement input, so a walking player captured at ~1 m/s on
		// the source reads back ~0 on the target, which has not received an input packet yet (the
		// Earth base, partitions 7870/8858, signature 4d7c94d7d80a281b: LinearVelocity x/y/z of
		// character 103753901892655110, -0.86/0.43/-0.19 vs ~0.00, 8 aborts on w22 2026-08-22).
		// The next input packet re-derives it; nothing is lost. The pose and everything else of a
		// free character stay validated. Angular velocity is left in: a character proxy has none
		// worth carrying and a mismatch there would point at a different importer fault.
		foreach (XElement character in document.Descendants("MyObjectBuilder_EntityBase")
			.Where(entity => (string)entity.Attribute(XsiType) == "MyObjectBuilder_Character").ToList())
			character.Elements("LinearVelocity").Remove();
		// An attachable top block's link to its base is pending, not lost, at this moment.
		// MyAttachableTopBlockBase.Init reads ParentEntityId and only calls MarkForReattach; m_parentBlock
		// - which GetObjectBuilderCubeBlock writes back - is set by the reattach itself, and FrozenUpdatePatch
		// defers that until the partition thaws and the grid is live. Comparing it here asks the target to
		// have finished work the freeze exists to postpone.
		document.Descendants("ParentEntityId").Remove();
		// An NPC grid's claim timer is queued on import, not stored. MyCubeGrid.Init calls
		// RequestSetFramesElapsed, which only puts the value in m_NPCGridsChanges;
		// MyNPCGridClaimSessionComponent.UpdateAfterSimulation drains that on a 60-frame boundary, so a
		// grid validated in the import frame re-emits whatever the previous tick held - one 60-frame
		// step behind. Pending, like OxygenRooms, not lost: the session component is not an entity
		// update, so the freeze does not hold it and the value lands within the second.
		document.Descendants("NPCGridClaimElapsed").Remove();
		// T-0153. An offensive combat block's hit-and-run state stamps the frame its retreat began
		// (RunAwayStartedFrame) against the node's OWN frame counter, and carries the stamp even while
		// CurrentState is None. Two nodes never share a frame counter, so the source's stamp and the
		// value the target re-emits differ by exactly the nodes' uptime difference (w31 2026-09-15,
		// partitions 23174 and 23492: -8420675 vs -8436099, ~15 400 frames, one residual, signature
		// 5143f2dd17bf2ed7; the partition was blocked and dissolved both times). Same family as
		// TimerTickInFrames: per-node derived, nothing to restore. The state itself (CurrentState,
		// RunAwayDirection, LastPosition) stays in the comparison.
		document.Descendants("RunAwayStartedFrame").Where(element => element.Parent?.Name == "HitAndRunState").Remove();
		// A drone AI's lock on its quarry is resolved late by design. MyDroneAI.Init only keeps the id in
		// m_loadCurrentTarget; m_currentTarget - which GetObjectBuilder writes back - is filled by
		// LoadEntities, and that runs inside Update, which the freeze defers. So an imported drone
		// re-emits 0 while still holding the id it will re-acquire on its first live frame.
		document.Descendants("CurrentTarget").Where(element => element.Parent?.Name == "AutomaticBehaviour").Remove();
		// A turret's lock on what it is shooting at, same family again and the clearest case of it:
		// MyLargeTurretBase.Init parks the id in m_targetToSet, and the only thing that turns it into
		// m_targetingSystem.Target - which GetObjectBuilderCubeBlock reads back - is UpdateOnceBeforeFrame,
		// the very update FrozenUpdatePatch defers. IsShooting travels with it: the object builder only
		// carries it when PreviousControlledEntity has been resolved, which is just as late. A pirate base
		// firing at whoever triggered the handover could otherwise never be handed over at all.
		// A timer block's JustTriggered is not state, it is the question "is an update pending" -
		// MyTimerBlock.GetObjectBuilderCubeBlock writes it straight from
		// NeedsUpdate.HasFlag(BEFORE_NEXT_FRAME), and Init turns it back into that flag. Keeping updates
		// pending across a freeze is the entire purpose of FrozenUpdatePatch, so an imported timer
		// truthfully answers yes where the source, which had already run its tick, answers no.
		document.Descendants().Where(element => element.Parent?.Name == "MyObjectBuilder_CubeBlock"
			&& (element.Name == "Target" || element.Name == "IsShooting"
				|| element.Name == "JustTriggered")).Remove();
		// A character's relative dampening reference is resolved late by design, same family as
		// ParentEntityId above. MyCharacter.Init parks the id in m_relativeDampeningEntityInit
		// (MyCharacter.cs:3386); the only thing that turns it back into RelativeDampeningEntity -
		// which GetObjectBuilder writes back, or 0 when unresolved (:4061) - is
		// UpdateBeforeSimulation100 (:4347-4353), which the freeze defers. So a character validated
		// in the import frame re-emits 0 while still holding the id it will resolve on its first
		// live tick. Pending, not lost; P3-HND-02's dampener cell asserts the live half after thaw.
		document.Descendants("RelativeDampeningEntity").Remove();
		// A character's locomotion does not survive its own Init. The airborne states are lost to the
		// frozen state machine (it recomputes from the character proxy every frame, and a frozen
		// character runs no frames with live physics). The moving ground states are lost earlier and
		// more directly: MyCharacter.Init restores m_currentMovementState from the builder (:3114),
		// then JetpackComp.Init -> TurnOnJetpack(fromLoad: true) runs 140 lines later and its
		// jetpack-off branch forces any non-Standing state back to Standing
		// (MyCharacterJetpackComponent.cs:308) - nothing in that branch checks fromLoad. Either way
		// the import settles on Standing, the enum default, omitted from the XML altogether, against
		// a source that says whatever the player was doing - Falling, Flying, or RunningLeftFront
		// (partition 21/25 handover aborts, 2026-08-11: a player caught mid-run bounced the transfer
		// until a retry found them standing).
		//
		// Unlike the rest of this list, nothing needs restoring afterwards: the state machine
		// re-derives locomotion from the controlling player's input within a frame of control
		// resuming, exactly as it does after any vanilla world load. It is the comparison that is
		// wrong - for every locomotion state. Only Died and Sitting are load-bearing (a body must
		// arrive dead, a pilot seated), each preserved by its own path (LootingCounter, cockpit
		// attach), so those two stay in the comparison.
		document.Descendants("MovementState")
			.Where(element => element.Value is not ("Died" or "Sitting"))
			.Remove();
		// A Custom Turret Controller's IsCorrect is its rotor/turret-reference validation verdict,
		// and the server never trusts the saved value: MyTurretControlBlock.Init forces it to false
		// on the server (MyTurretControlBlock.cs:1603) and only re-validation flips it back once the
		// referenced subgrid attaches - which a frozen import has not run yet. A fully assembled
		// source (true) therefore always differs from its just-imported copy (false) until the first
		// live validation pass (Siren frigate pulls, 2026-08-11: every spawn-point pull of an
		// encounter frigate aborted on this residual). Re-derived on both sides, nothing to restore.
		document.Descendants("IsCorrect").Remove();
		// An inventory's item ids are per-load bookkeeping, not content: MyInventory hands out
		// m_nextItemID sequentially as stacks load, so a source whose earlier stacks were consumed
		// in play carries gapped ids (1,2,3 / next 4) that the import legitimately compacts
		// (0,1,2 / next 3) - and the frozen source never changes, so the abort repeats forever.
		// The items themselves - type, amount, document order - stay in the comparison; only the
		// counters leave it. (Partition 179, 2026-08-16: nine ItemId/nextItemId residuals blocked
		// the handover of two Visitors and the Deterrent base at escalating backoff.)
		document.Descendants("ItemId")
			.Where(element => element.Parent?.Name == "MyObjectBuilder_InventoryItem").Remove();
		document.Descendants("nextItemId").Remove();
		// A construction stockpile item with nothing in it does not survive a load. MyConstructionStockpile.Init
		// keeps only items with Amount > 0, and MySlimBlock.GetObjectBuilderCubeBlock writes no stockpile at all
		// once it holds no items (MySlimBlock.cs:1376-1378). Live damage can leave a zero-amount item behind
		// (a camera on an NPC escort drone kept Scrap x0), so the target re-emitted the block without a
		// ConstructionStockpile, one element short, on every attempt (octahedron-1191, 2026-10-20, #1253:
		// partition 375, HandoverBlocked at the end of the run). Model the load: drop the empty items, then a
		// stockpile left with none. A stockpile that really holds components stays compared.
		document.Descendants("ConstructionStockpile").Elements("Items")
			.Where(item => !int.TryParse(item.Element("Amount")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
			.Remove();
		document.Descendants("ConstructionStockpile").Where(stockpile => !stockpile.Elements("Items").Any()).Remove();
		// A gun's magazine count is a cache of its ammo inventory. MyGunBase.Init does read
		// RemainingMagazines back, but RefreshAmmunitionAmount immediately recomputes it from
		// m_user.AmmoInventory.GetItemAmount (MyGunBase.cs:1035-1057) on every server update - and forces
		// it to 0 under Creative infinite ammo, which is why Creative-rewritten lanes never saw the field
		// at all. The inventory it is derived from transfers in the same snapshot and stays fully
		// validated, so the erase is the derived-cache kind: unconditional. Found live 2026-07-31 - the
		// pirate base's partition could never hand over in the first Survival serve.
		document.Descendants("GunBase").Elements("RemainingMagazines").Remove();
		// A landing gear in ReadyToLock (magnetic, candidate in range, not locked) writes its attachment
		// candidate but never reads it back: GetObjectBuilderCubeBlock emits AttachedEntityId and the
		// pivots unconditionally whenever a candidate exists (MyLandingGear.cs:909-929), while Init
		// restores them only when IsLocked is true (:664-679) - a ReadyToLock gear re-derives them from
		// proximity on its next update, which the freeze deliberately never runs. The erase is scoped to
		// the not-locked state: a genuinely locked gear keeps all four fields in the comparison, so real
		// dock locks stay validated. Found live 2026-07-31 - a ship parked gear-down deadlocked its
		// co-location for as long as the player stayed.
		foreach (XElement gear in document.Descendants("MyObjectBuilder_CubeBlock")
			.Where(block => (string)block.Attribute(XsiType) == "MyObjectBuilder_LandingGear"
				&& block.Element("IsLocked")?.Value != "true"))
		{
			gear.Elements("AttachedEntityId").Remove();
			gear.Elements("MasterToSlave").Remove();
			gear.Elements("GearPivotPosition").Remove();
			gear.Elements("OtherPivot").Remove();
		}
		// A cockpit nobody is sitting in cannot restore its sit animation. MyCockpit.Init reads
		// SitAnimation back only inside its Pilot != null branch (MyCockpit.cs:674-676), while
		// GetObjectBuilderCubeBlock writes m_sitAnimation out unconditionally (:1009) - and the seed
		// is chosen when a pilot sits (:2171, :2655, :2756) and never cleared when one leaves. So a
		// seat somebody has used and left keeps a stale non-zero seed that the import drops to 0, and
		// its partition can never hand over again (partitions 84 and 89, 2026-08-12: every Visitor a
		// test pilot had flown and climbed out of blocked its platform's handover permanently, one
		// residual each). Same class as the unconnected connector's master flag above, and scoped the
		// same way: an occupied cockpit keeps the field in the comparison, because there Init does
		// restore it.
		foreach (XElement cockpit in document.Descendants("MyObjectBuilder_CubeBlock")
			.Where(block => block.Element("Pilot") == null))
			cockpit.Elements("SitAnimation").Remove();
		// A damaged armor block's name is an identity echo, not state. Once damage drops an armor
		// block below its edge-model threshold it carries a MyCubeBlock wrapper: live damage creates
		// it bare (MySlimBlock.cs:2117), while the import's load path builds it through Init
		// (MySlimBlock.cs:1220,1226), and MyCubeBlock.Init names an unnamed block after its EntityId
		// (MyCubeBlock.cs:768-770), which GetObjectBuilderCubeBlock then writes back unconditionally
		// (MyCubeBlock.cs:876). So the target re-emits Name == EntityId against a source with no Name
		// at all. Erase exactly that echo - a genuinely named block still compares.
		document.Descendants("Name")
			.Where(element => element.Parent?.Name.LocalName == "MyObjectBuilder_CubeBlock"
				&& element.Value == element.Parent.Element("EntityId")?.Value)
			.Remove();
		// An empty gas tank does not stay empty across an Init. MyGasTank.Init tops a zero FilledRatio up
		// to 0.5 when IsCreativeModeEnabled - which is true in a survival world too, for an NPC-owned
		// NPC-spawned grid. So a pirate base's drained oxygen tank re-emits 0.5 against the source's 0 and
		// its partition can never hand over, which is what kept the C1/D1 cluster pinned. Unlike the rest
		// of this list the rewrite is deliberate and permanent, but it is also exactly what a world reload
		// does to that block, so the import is a faithful continuation and the comparison is what is wrong.
		// Model the rewrite rather than dropping the field: a tank that really lost its gas still fails.
		foreach (XAttribute attribute in document.Descendants().Attributes()) NormalizeNumber(attribute);
		foreach (XElement element in document.Descendants().Where(element => !element.HasElements)) NormalizeNumber(element);
		foreach (XElement filled in document.Descendants("FilledRatio").Where(element => element.Value == "0"))
			filled.Value = "0.5";
		// A seated character's world pose is derived, not stored. MyCockpit.Init re-attaches the saved
		// pilot (MyCockpit.cs:1128) through AttachPilot, which overwrites the character's world matrix
		// with the cockpit's own (MyCockpit.cs:2197-2199) before PlacePilotInSeat positions it - so the
		// imported pilot re-emits a pose recomputed from the seat in single precision, ~1e-4 off the
		// captured one, and a partition with a player in any seat could never hand over (partition 26,
		// 2026-08-09). The seat reference is the authoritative fact; the pose is rebuilt wherever the
		// unit lands. Erase it from every embedded character - the legacy Pilot element and the
		// hierarchy Children copy both carry the same character OB - while a top-level (unseated)
		// character's pose and everything else on the embedded character stay validated.
		// A seated character's InScene flag is pending, not lost, at this moment - the same deferral
		// as the turret Target above, on the same update. MyCockpit.Init only parks the saved pilot:
		// CreateFromObjectBuilder (not ...AndAdd, so never in scene), m_savedPilot/m_defferAttach
		// (MyCockpit.cs:689-694); the only thing that seats it - AttachPilot, which is what puts the
		// character in the scene - is UpdateOnceBeforeFrame (MyCockpit.cs:1113-1131), the very update
		// FrozenUpdatePatch defers until the partition thaws. So an imported occupied cockpit re-emits
		// its pilot's PersistentFlags without InScene against a live source that has it, one residual,
		// and the abort repeats forever because every retry re-imports and re-validates inside a single
		// frozen frame (partitions 1319/1525, w14, 2026-08-18: every rotation drain of a partition with
		// a seated pilot aborted, and the freeze/thaw retry cycles ejected live pilots fleet-wide).
		// Strip exactly the InScene token from embedded characters on both sides - the pilot's
		// presence, identity and every other field stay validated; a dropped pilot still fails.
		foreach (XElement character in document.Descendants()
			.Where(element => element.Parent?.Name.LocalName != "SectorObjects"
				&& (element.Name.LocalName == "Pilot"
					|| (string)element.Attribute(XsiType) == "MyObjectBuilder_Character")))
		{
			character.Elements("PositionAndOrientation").Remove();
			// Same class for the air a seated character reports: MyCharacterOxygenComponent.UpdateOxygen
			// copies the pressurized seat's fill level into EnvironmentOxygenLevel every 100 frames
			// (MyCharacterOxygenComponent.cs:351). The seat's own OxygenLevel stays compared (SeatOxygenEpsilon).
			character.Elements("EnvironmentOxygenLevel").Remove();
			foreach (XElement flags in character.Elements("PersistentFlags"))
				flags.Value = string.Join(" ", flags.Value.Split(' ').Where(token => token != "InScene"));
		}
		// A grid's deformation skeleton is a map keyed by bone position, so the same dents come back in
		// whatever order the imported grid's hash map enumerates - identical bones, different sequence,
		// and the comparison is positional. Sorting is what makes the comparison about the bones rather
		// than about the order vanilla happened to emit them in. Runs after NormalizeNumber so the sort
		// key is the normalized text.
		foreach (XElement skeleton in document.Descendants("Skeleton"))
		{
			XElement[] bones = skeleton.Elements("BoneInfo").OrderBy(bone => bone.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal).ToArray();
			skeleton.Elements("BoneInfo").Remove();
			skeleton.Add(bones);
		}
		// T-0290 (w33 2026-09-29 11:05, first start with Workshop mods): a mod's `Entity.Storage = new
		// MyModStorageComponent()` goes through MyEntity.Storage's setter, Components.Add<MyModStorageComponentBase>,
		// so the source serialises the entry under the BASE type's key; the import registers the same component
		// under its own type, and the target emits TypeId MyModStorageComponent. Same component, same data, and
		// MyEntity.Storage finds it under either key (MyEntity.OnComponentAdded, IsAssignableFrom). Every grid a
		// storage-using mod touched failed its handover on this key alone (P215809, P215846). One spelling.
		foreach (XElement typeId in document.Descendants("TypeId")
			.Where(element => element.Parent?.Name == "ComponentData" && element.Value == "MyModStorageComponentBase").ToArray())
			typeId.Value = "MyModStorageComponent";
		// Modular Encounters Systems stamps its NPC record with the writing node's game clock: NpcData.Update sets
		// LastChangeToData (ProtoMember 25) to MyAPIGateway.Session.GameDateTime before it saves the record into the
		// grid's storage, and nothing in MES reads the stamp back (only NpcData.ToString prints it). A node whose
		// MES re-saves the record while the import adds the grid re-emits its own clock against the source's
		// (octahedron-1191, 2026-10-20, #1253: 15 records in 3 transfers, each to the node that had released the
		// same grids 2-3 s before, the retry 4-5 s later clean; on one target node the stamps of two aborts lie
		// exactly the wall time apart that separates them). Every other field of the record matched. Drop only
		// that field; the rest of the record, and every other mod's storage, stays compared byte for byte.
		foreach (XElement value in document.Descendants("item")
			.Where(item => item.Parent?.Name == "dictionary" && item.Element("Key")?.Value == MesNpcDataKey)
			.Elements("Value"))
			if (TryRemoveProtobufField(value.Value, MesLastChangeToDataField, out string stripped))
				value.Value = stripped;
		// A block's component container is a Dictionary<Type, ...> (MyComponentContainer.cs:13), so a
		// fresh import re-emits its ComponentData entries in whatever order the new map enumerates - a
		// cockpit's MyInventoryBase and MyMultiTextPanelComponent legitimately come back swapped.
		// Identical entries, different sequence: same disease as the Skeleton bones, same cure. Runs
		// after NormalizeNumber so the sort key is the normalized text. Found live 2026-07-31; re-landed
		// here through the tested-change protocol after unblocking that debug session as a hotfix.
		foreach (XElement components in document.Descendants("Components").Where(element => element.Parent?.Name == "ComponentContainer"))
		{
			XElement[] entries = components.Elements("ComponentData").OrderBy(entry => entry.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal).ToArray();
			components.Elements("ComponentData").Remove();
			components.Add(entries);
		}
	}

	private static void NormalizeLength(XElement vector)
	{
		double[] parts = new double[3];
		string[] axes = { "x", "y", "z" };
		for (int i = 0; i < 3; i++)
			if (!double.TryParse(vector.Attribute(axes[i])?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parts[i])) return;
		double length = Math.Sqrt(parts[0] * parts[0] + parts[1] * parts[1] + parts[2] * parts[2]);
		if (length < 1e-9) return;
		for (int i = 0; i < 3; i++)
			vector.SetAttributeValue(axes[i], (parts[i] / length).ToString("R", CultureInfo.InvariantCulture));
	}

	private const string MesNpcDataKey = "29ffd684-13d7-4045-bf76-cd48bf80e36a";
	private const int MesLastChangeToDataField = 25;

	/// <summary>
	/// A base64 protobuf message without the top-level field <paramref name="field"/>. False, and the value left
	/// alone, when the text is not a well-formed message, so a value the walk cannot read stays compared whole.
	/// </summary>
	internal static bool TryRemoveProtobufField(string base64, int field, out string stripped)
	{
		stripped = null;
		byte[] data;
		try { data = Convert.FromBase64String(base64); }
		catch (FormatException) { return false; }
		using MemoryStream kept = new MemoryStream(data.Length);
		int index = 0;
		while (index < data.Length)
		{
			int start = index;
			if (!TryReadVarint(data, ref index, out ulong key)) return false;
			ulong skip;
			switch (key & 7)
			{
				case 0: if (!TryReadVarint(data, ref index, out _)) return false; skip = 0; break;
				case 1: skip = 8; break;
				case 2: if (!TryReadVarint(data, ref index, out skip)) return false; break;
				case 5: skip = 4; break;
				default: return false;
			}
			if (skip > (ulong)(data.Length - index)) return false;
			index += (int)skip;
			if (key >> 3 != (ulong)field) kept.Write(data, start, index - start);
		}
		stripped = Convert.ToBase64String(kept.ToArray());
		return true;

		static bool TryReadVarint(byte[] bytes, ref int position, out ulong value)
		{
			value = 0;
			for (int shift = 0; shift < 64 && position < bytes.Length; shift += 7)
			{
				byte next = bytes[position++];
				value |= (ulong)(next & 0x7F) << shift;
				if ((next & 0x80) == 0) return true;
			}
			return false;
		}
	}

	private static void NormalizeNumber(XObject value)
	{
		string text = value is XAttribute attribute ? attribute.Value : ((XElement)value).Value;
		if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e')) return;
		if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return;
		string normalized = Math.Round(number, 5).ToString("0.#####", CultureInfo.InvariantCulture);
		if (value is XAttribute targetAttribute) targetAttribute.Value = normalized;
		else ((XElement)value).Value = normalized;
	}

	// The comparison walks text, so every number had to agree character for character, and one class
	// of state cannot: a character's position lives in its single-precision Havok proxy, and Init reads it
	// back before any freeze can intervene - the imported character lands some ten micrometres from the
	// exported one while every grid in the same partition round-trips bit-exact. Rounding alone does not
	// answer that, because rounding is a quantizer and not a tolerance: two values a picometre apart still
	// fall either side of a boundary. So compare numbers as numbers. The floor is Normalize's own 1e-5
	// quantum - and it must be strictly MORE than that quantum, not equal to it. Normalize rounds to five
	// decimals before this runs, so two values three parts in a hundred million apart, either side of a
	// rounding boundary, arrive here a full quantum apart: a grid's float Up vector re-derived from the
	// imported matrix does exactly that. A floor of 1e-5 rejected them, because 0.48502 - 0.48501 is
	// 1.0000000000287e-05 in binary and not the 1e-5 it reads as. Two quanta clears the boundary with
	// room for the float error on top. Above the floor the bound is relative, which stays orders of
	// magnitude tighter than any state change worth catching.
	/// <summary>
	/// The pose tolerance (Viktor, 2026-08-15: "accept the minor difference in pose during handover,
	/// don't hold up everything else"). PositionAndOrientation is the one field a moving entity keeps
	/// changing while the handover runs, and the general 2e-5 is an order of magnitude tighter than
	/// the drift a rotating grid accumulates inside its own freeze window - the Monolith aborted every
	/// handover it was ever offered on differences of 1e-4 to 3e-4 in its orientation basis.
	///
	/// It applies to the ORIENTATION BASIS ONLY - Forward, Up and Orientation - and deliberately not
	/// to Position, which stays on the general tolerance: the drift observed is angular (a grid
	/// turning), a position that has moved is a different and more serious claim, and "a free
	/// character's pose stays validated" in CodecTest asserts exactly that on a 1.3e-4 Position
	/// difference.
	///
	/// 1e-3 on a normalized basis vector is about 0.06 degrees, far below anything a real divergence
	/// produces: a duplicate incarnation separates by metres and degrees and keeps growing
	/// (handover-duplicate-incarnation.md), so it still fails the gate.
	/// </summary>
	private const double OrientationEpsilon = 1e-3;

	/// <summary>
	/// Velocity tolerance, same argument as the pose one. A body that is nominally at rest keeps a
	/// jitter of a fraction of a millimetre per second, and the source freezes it a few milliseconds
	/// before the target re-emits it. Partition 7178 (w22, 2026-08-21 22:16, presync OFF, so this is
	/// the plain staged path) aborted on a standing character's LinearVelocity differing by 3.5e-4 to
	/// 1.6e-3 m/s - the only velocity residuals in 1111 residual lines that day. 1e-2 m/s clears them
	/// six times over and is still two orders of magnitude below anything a player could see; a real
	/// divergence (a duplicate incarnation, a dropped physics state) separates by whole m/s.
	/// </summary>
	private const double VelocityEpsilon = 1e-2;

	/// <summary>
	/// Safe zone block countdowns, in milliseconds. MySafeZoneComponent writes UpkeepTime and
	/// ActivationTime as its deadline minus MySandboxGame.TotalGamePlayTimeInMilliseconds, and Init adds
	/// the node's own clock back. So the target re-emits the value its import restored, less the game
	/// time that passed between that Init and the validation: North Station on the bench (SE1-0114,
	/// 2026-10-11) came back 117 ms further down its countdown than the frozen source, and every station
	/// with a safe zone block failed every handover on those two residuals. A countdown off by a few
	/// seconds is not lost state; a dropped or reset one differs by minutes or by the whole value.
	/// </summary>
	private const double SafeZoneClockEpsilon = 10000;

	/// <summary>
	/// A pressurized seat's fill level, by up to two breaths of a 60 L seat. A seated pilot breathes
	/// OxygenConsumptionRate (0.063 L) out of the seat every 100 frames (MyCharacterOxygenComponent.UpdateOxygen),
	/// and MyCockpit.UpdateBeforeSimulation100 tops the seat back up to the air's oxygen level, so in breathable
	/// air the level saws between 1 and one breath below it, and in vacuum it falls one breath per step. Which
	/// side of the step each node froze or re-emitted is the phase of its own 100-frame updates: three cockpits
	/// came back exactly 0.063 / 60 = 0.00105 short of a full source (octahedron-1191, 2026-10-20, #1253: piloted
	/// Ion ships, 2 pairs). 0.0025 is 0.15 L of the smallest cockpit; a lost or reset level differs by the whole
	/// fill. A 1 L cryo chamber's breath is 0.063 and stays a residual: none has been seen.
	/// </summary>
	private const double SeatOxygenEpsilon = 0.0025;

	private static bool IsBlockField(string path, string field)
	{
		if (path.Length < field.Length + 2 || !path.EndsWith("/" + field, StringComparison.Ordinal)) return false;
		int parent = path.LastIndexOf('/', path.Length - field.Length - 2);
		return string.CompareOrdinal(path, parent + 1, "MyObjectBuilder_CubeBlock", 0, "MyObjectBuilder_CubeBlock".Length) == 0;
	}

	private static double EpsilonFor(string path)
		=> path.Contains("/PositionAndOrientation/Forward", StringComparison.Ordinal)
			|| path.Contains("/PositionAndOrientation/Up", StringComparison.Ordinal)
			|| path.Contains("/PositionAndOrientation/Orientation", StringComparison.Ordinal)
			? OrientationEpsilon
			: path.Contains("/LinearVelocity", StringComparison.Ordinal)
				|| path.Contains("/AngularVelocity", StringComparison.Ordinal)
				? VelocityEpsilon
			: path.EndsWith("/Component/UpkeepTime", StringComparison.Ordinal)
				|| path.EndsWith("/Component/ActivationTime", StringComparison.Ordinal)
				? SafeZoneClockEpsilon
			: IsBlockField(path, "OxygenLevel") ? SeatOxygenEpsilon : 2e-5;

	/// <summary>T-0363: the value MyFueledPowerProducer.Init restores from a saved capacity (MathHelper.Clamp to [0, max]).</summary>
	internal static float ClampCapacity(float capacity, float maximum)
		=> capacity < 0f ? 0f : capacity > maximum ? maximum : capacity;

	private static bool ValuesEqual(string left, string right, double absolute = 2e-5)
	{
		if (left == right) return true;
		return double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
			&& double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
			&& Math.Abs(a - b) <= Math.Max(absolute, 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b)));
	}

	// The recursive comparison, collecting residual entries instead of answering a bare bool: the
	// abort log and the P3-HND scenarios both assert on the entries themselves. Capped - a structural
	// shift (one missing child) cascades positionally, and 32 entries diagnose anything the dumps can.
	private const int ResidualCap = 32;

	/// <summary>
	/// Every differing leaf between two builders' normalised documents, uncapped, as
	/// "path: left != right" - the delta classifier's input (HandoverDelta.md §3). Same walk,
	/// same normalisation and lazy-id stripping as the gate, so whatever the gate would call a
	/// residual is a diff here and vice versa.
	/// </summary>
	internal static List<string> DiffPaths(MyObjectBuilder_EntityBase left, MyObjectBuilder_EntityBase right)
	{
		XDocument a = ToDocument(new[] { left }), b = ToDocument(new[] { right });
		NormalizeDocument(a);
		NormalizeDocument(b);
		StripLazyBlockEntityIds(a, b);
		List<string> found = new List<string>();
		Collect(a.Root, b.Root, a.Root?.Name.LocalName ?? "?", found, int.MaxValue);
		return found;
	}

	/// <summary>
	/// Self-test hook: the gate's residuals between two builder DOCUMENTS given as XML - the same
	/// normalisation and walk as Residuals, without needing live object builders. Lets a
	/// normalisation rule be pinned against the exact XML of the handover-diff dump it came from.
	/// </summary>
	internal static List<string> ResidualsOfXml(string leftXml, string rightXml)
	{
		XDocument a = XDocument.Parse(leftXml), b = XDocument.Parse(rightXml);
		NormalizeDocument(a);
		NormalizeDocument(b);
		List<string> found = new List<string>();
		Collect(a.Root, b.Root, a.Root?.Name.LocalName ?? "?", found);
		return found;
	}

	private static void Collect(XElement left, XElement right, string path, List<string> found)
		=> Collect(left, right, path, found, ResidualCap);

	private static void Collect(XElement left, XElement right, string path, List<string> found, int Cap)
	{
		if (left == null || right == null) { if (left != right) found.Add(path + ": missing document root"); return; }
		if (found.Count >= Cap) return;
		if (left.Name != right.Name)
		{
			found.Add(path + ": element " + left.Name.LocalName + " != " + right.Name.LocalName);
			return;
		}
		foreach (XAttribute attribute in left.Attributes())
		{
			if (found.Count >= Cap) return;
			XAttribute other = right.Attribute(attribute.Name);
			if (other == null || !ValuesEqual(attribute.Value, other.Value, EpsilonFor(path)))
				found.Add(path + "/@" + attribute.Name.LocalName + ": " + Clip(attribute.Value) + " != " + (other == null ? "(absent)" : Clip(other.Value)));
		}
		foreach (XAttribute attribute in right.Attributes())
		{
			if (found.Count >= Cap) return;
			if (left.Attribute(attribute.Name) == null)
				found.Add(path + "/@" + attribute.Name.LocalName + ": (absent) != " + Clip(attribute.Value));
		}
		if (!left.HasElements && !right.HasElements)
		{
			if (!ValuesEqual(left.Value, right.Value, EpsilonFor(path))) found.Add(path + ": " + Clip(left.Value) + " != " + Clip(right.Value));
			return;
		}
		XElement[] leftChildren = left.Elements().ToArray(), rightChildren = right.Elements().ToArray();
		if (leftChildren.Length != rightChildren.Length)
			found.Add(path + ": " + leftChildren.Length + " != " + rightChildren.Length + " child elements");
		if (TryPairByKey(leftChildren, rightChildren, out (XElement Left, XElement Right)[] paired,
			out string[] onlyLeft, out string[] onlyRight))
		{
			foreach (string key in onlyLeft)
			{
				if (found.Count >= Cap) return;
				found.Add(path + "/" + key.TrimEnd('#') + ": present on the source side, absent on the target side");
			}
			foreach (string key in onlyRight)
			{
				if (found.Count >= Cap) return;
				found.Add(path + "/" + key.TrimEnd('#') + ": absent on the source side, present on the target side");
			}
			foreach ((XElement a, XElement b) in paired)
			{
				if (found.Count >= Cap) return;
				string key = KeyOf(a);
				Collect(a, b, path + "/" + a.Name.LocalName + (key.Length == 0 ? "" : "#" + key), found, Cap);
			}
			return;
		}
		int shared = Math.Min(leftChildren.Length, rightChildren.Length);
		for (int index = 0; index < shared && found.Count < Cap; index++)
			Collect(leftChildren[index], rightChildren[index], ChildPath(path, leftChildren, index), found, Cap);
	}

	/// <summary>
	/// The identity of a child element, when it has one: its EntityId, or - for a component - the
	/// TypeId that says which component it is. Null when the child is genuinely positional.
	/// </summary>
	private static string KeyOf(XElement element)
		=> element.Element("EntityId")?.Value ?? element.Element("TypeId")?.Value ?? "";

	/// <summary>
	/// Pair two child lists BY IDENTITY rather than by ordinal, when every child on both sides has
	/// one and the identities are distinct within each side. Otherwise returns false and the caller
	/// keeps the positional walk, which is correct for genuinely ordered content.
	///
	/// T-0146. NormalizeDocument already sorts ComponentData entries (see the Dictionary&lt;Type,...&gt;
	/// comment there), so this is NOT a claim that the walk is naively positional - it is a claim
	/// that the SORT KEY IS THE WRONG ONE. That key is the entry's full serialized text, so a
	/// component whose CONTENT differs between the two sides sorts to a different index, and a
	/// component absent on one side shifts every later index. The sort cures the dictionary-order
	/// disease it was written for and cannot touch either of those.
	///
	/// Evidence that it does happen, from the node's own abort log on w31 partition 4087
	/// (2026-09-13), against two documents NormalizeDocument had already processed:
	///
	///     .../Pilot#72057594826576593/UsingLadderInfo: element UsingLadderInfo != EnableBroadcastingPlayerToggle
	///
	/// An element-NAME mismatch is by definition a desynchronized walk - child i of one side is
	/// being compared with a different child i of the other - so every field after the shift is
	/// reported as a difference. That partition's block 72057594390357412 is missing a
	/// MyHierarchyComponentBase on the target side, one block of 61, which is the kind of single
	/// structural difference that produces it.
	///
	/// A TypeId (or EntityId) key is stable under content changes and under absence, so the walk
	/// reports the missing component once, by name, instead of a disagreement about everything
	/// that follows it. How many of that partition's 20 residuals collapse is NOT claimed here -
	/// measuring it needs this walk run against genuinely normalized documents.
	///
	/// NOTE FOR WHOEVER DEPLOYS THIS: residual PATHS change for keyed children, so
	/// <see cref="Signature"/> changes, and Orchestration.md 3.3 counts consecutive identical
	/// signatures toward HandoverBlocked. A partition mid-backoff at deploy time restarts its
	/// count. That is a one-time effect and it errs toward retrying, not toward blocking.
	/// </summary>
	private static bool TryPairByKey(XElement[] left, XElement[] right,
		out (XElement Left, XElement Right)[] paired, out string[] onlyLeft, out string[] onlyRight)
	{
		paired = null; onlyLeft = onlyRight = null;
		if (left.Length == 0 && right.Length == 0) return false;
		// CHEAP REJECTION FIRST. The overwhelmingly common case is two lists that already agree
		// element for element, where the positional walk is correct and keying buys nothing. Doing
		// the zip-compare BEFORE building any dictionary matters: measured on a retained w31 dump
		// pair, building first cost a median 14.73 ms against the unmodified walk's 10.66 - a 38%
		// REGRESSION on compareMs, which is game-thread time inside the frozen handover window.
		// Rejecting first brings it back. Same check, opposite order.
		if (left.Length == right.Length)
		{
			bool identical = true;
			for (int index = 0; index < left.Length; index++)
			{
				if (left[index].Name == right[index].Name && KeyOf(left[index]) == KeyOf(right[index])) continue;
				identical = false;
				break;
			}
			if (identical) return false;
		}
		Dictionary<string, XElement> leftByKey = new Dictionary<string, XElement>(left.Length, StringComparer.Ordinal);
		Dictionary<string, XElement> rightByKey = new Dictionary<string, XElement>(right.Length, StringComparer.Ordinal);
		foreach ((XElement[] children, Dictionary<string, XElement> index) in
			new[] { (left, leftByKey), (right, rightByKey) })
		{
			foreach (XElement child in children)
			{
				// The NAME is part of the key, and is enough on its own for a struct-like record
				// whose children are all distinct elements - which is what a Pilot is, and where
				// the worst cascade measured actually lives (one absent child of 30 desynchronized
				// the remaining 7). Lists of like-named children - CubeBlocks, inventory items,
				// ComponentData - collide on the name and fall back to their id or TypeId.
				// One duplicated key and the whole list stays positional: a partial key is worse
				// than none, because it silently reorders the children that have one.
				if (!index.TryAdd(child.Name.LocalName + "#" + KeyOf(child), child)) return false;
			}
		}
		onlyLeft = leftByKey.Keys.Where(key => !rightByKey.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
		onlyRight = rightByKey.Keys.Where(key => !leftByKey.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
		paired = left.Select(child => child.Name.LocalName + "#" + KeyOf(child))
			.Where(rightByKey.ContainsKey)
			.Select(key => (leftByKey[key], rightByKey[key]))
			.ToArray();
		return true;
	}

	// Path segments carry the child's EntityId when it has one - a dump reader finds the block by id,
	// not by ordinal - and fall back to a positional index only for repeated anonymous siblings.
	private static string ChildPath(string path, XElement[] siblings, int index)
	{
		XElement child = siblings[index];
		string name = child.Name.LocalName;
		string id = child.Element("EntityId")?.Value;
		if (id != null) return path + "/" + name + "#" + id;
		bool repeated = siblings.Count(sibling => sibling.Name.LocalName == name) > 1;
		return path + "/" + name + (repeated ? "[" + index + "]" : "");
	}

	private static string Clip(string value)
		=> value == null ? "(nil)" : value.Length <= 48 ? value : value.Substring(0, 48) + "…";
}
