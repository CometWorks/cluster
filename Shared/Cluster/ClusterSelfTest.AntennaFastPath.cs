using System;
using System.Collections.Generic;
using System.Linq;
using VRage;
using VRage.Game;
using VRageMath;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// #1290: the antenna-graph fast path (AntennaGraphCodec.ReadBuilderShared) must patch a ship antenna that
	/// only MOVED instead of re-parsing it. A ship antenna's builder carries its own Position (attributes) and
	/// one Position per HUD entry (X/Y/Z child elements), and both move with the ship: on the live #1191
	/// cluster the fast path took hits=0 of 4096 lookups because it recognised the first form only. Builders
	/// here come from the real serializer (WriteBuilder), so the section follows the game's XML shape, and every
	/// patched builder must serialize back to the exact string a fresh parse of the new builder gives. Fake
	/// antenna ids far from real entity ids; a live node's antenna_fastpath report gains the few lookups made here.
	private static void AntennaFastPath()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("antenna-fastpath: " + message); }
		const long Antenna = 7_129_000_001, Info = 7_129_000_002;
		MyObjectBuilder_ProxyAntenna Builder(double x, string hudText = "Ship", MyPositionAndOrientation? pose = null)
		{
			var builder = new MyObjectBuilder_ProxyAntenna
			{
				EntityId = 0, AntennaEntityId = Antenna, InfoEntityId = Info, InfoName = "Ship", BroadcastRadius = 5000f,
				Position = new Vector3D(x, -2.25, 3e6), PositionAndOrientation = pose,
				HudParams = new List<MyObjectBuilder_HudEntityParams>
				{
					new MyObjectBuilder_HudEntityParams { EntityId = 7_129_000_003, Text = hudText, Position = new Vector3D(x + 1, 20.123456789, -30) },
					new MyObjectBuilder_HudEntityParams { EntityId = 7_129_000_004, Text = "Grid", Position = new Vector3D(x - 1, 1e-7, 4.5e15) },
				},
			};
			return builder;
		}
		AntennaGraphEntry Entry(MyObjectBuilder_ProxyAntenna builder) => new AntennaGraphEntry("node-1", Antenna, AntennaGraphCodec.WriteBuilder(builder));
		// The parse a node would make without the fast path, written back: the patched instance must match it exactly.
		void SameAsFreshParse(MyObjectBuilder_ProxyAntenna shared, AntennaGraphEntry entry, string label)
			=> Check(AntennaGraphCodec.WriteBuilder(shared) == AntennaGraphCodec.WriteBuilder(AntennaGraphCodec.ReadBuilder(entry)),
				label + ": the shared builder differs from a fresh parse");

		AntennaGraphEntry first = Entry(Builder(1.5));
		MyObjectBuilder_ProxyAntenna parsed = AntennaGraphCodec.ReadBuilderShared(first);
		SameAsFreshParse(parsed, first, "first parse");

		// The ship moved: the antenna and both HUD entries carry new positions, nothing else changed.
		long hits = AntennaGraphCodec.FastPathHitsTotal;
		AntennaGraphEntry moved = Entry(Builder(1234.5678901234));
		MyObjectBuilder_ProxyAntenna patched = AntennaGraphCodec.ReadBuilderShared(moved);
		Check(AntennaGraphCodec.FastPathHitsTotal == hits + 1, "a moved ship antenna was re-parsed, not patched");
		Check(ReferenceEquals(patched, parsed), "the patch did not reuse the memoised builder");
		Check(patched.Position.X == 1234.5678901234 && patched.HudParams[0].Position.X == 1235.5678901234
			&& patched.HudParams[1].Position.X == 1233.5678901234, "the patch missed a position");
		SameAsFreshParse(patched, moved, "after the move");
		Check(ReferenceEquals(AntennaGraphCodec.ReadBuilderShared(moved), patched), "the exact repeat is not a cache hit");

		// The previous string must not hand back the patched instance with a position that is not its own.
		MyObjectBuilder_ProxyAntenna back = AntennaGraphCodec.ReadBuilderShared(first);
		Check(back.Position.X == 1.5 && back.HudParams[0].Position.X == 2.5, "the previous builder string reads the moved position");
		SameAsFreshParse(back, first, "moved back");

		// Anything else changed (a HUD text here) is a full parse.
		hits = AntennaGraphCodec.FastPathHitsTotal;
		AntennaGraphEntry renamed = Entry(Builder(1.5, "Renamed"));
		MyObjectBuilder_ProxyAntenna reparsed = AntennaGraphCodec.ReadBuilderShared(renamed);
		Check(AntennaGraphCodec.FastPathHitsTotal == hits && reparsed.HudParams[0].Text == "Renamed", "a text change took the fast path");
		SameAsFreshParse(reparsed, renamed, "renamed");

		// A second attribute-form Position (the entity's own PositionAndOrientation, null on a captured proxy
		// today) makes the antenna's Position ambiguous: a move must re-parse, never patch the wrong field.
		var pose = new MyPositionAndOrientation(new Vector3D(5, 6, 7), Vector3.Forward, Vector3.Up);
		AntennaGraphCodec.ReadBuilderShared(Entry(Builder(1.5, pose: pose)));
		hits = AntennaGraphCodec.FastPathHitsTotal;
		AntennaGraphEntry posed = Entry(Builder(99.25, pose: pose));
		MyObjectBuilder_ProxyAntenna ambiguous = AntennaGraphCodec.ReadBuilderShared(posed);
		Check(AntennaGraphCodec.FastPathHitsTotal == hits, "an ambiguous Position took the fast path");
		SameAsFreshParse(ambiguous, posed, "ambiguous");
	}
}
