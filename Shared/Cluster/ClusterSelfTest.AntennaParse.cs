using System;
using System.Collections.Generic;
using System.IO;
using VRage.Game;
using VRage.ObjectBuilders.Private;
using VRage.Utils;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// #1257: AntennaGraphCodec.ReadBuilder parses an antenna proxy builder into the same builder the stock
	/// MyObjectBuilderSerializerKeen.DeserializeXML gives, and without the stock call's per-parse name-table
	/// garbage. Needs the game's XML serializers, so it runs on a node. Measured with this thread's allocation
	/// counter over repeated parses of one builder (after a warm-up parse of each, which fills the name table
	/// and the serializer caches); the stock figure is the control, measured the same way in the same process.
	/// </summary>
	private static void AntennaParseAllocation()
	{
		const int Parses = 20;
		var source = new MyObjectBuilder_ProxyAntenna
		{
			EntityId = 0, AntennaEntityId = 1257, InfoEntityId = 1258, Owner = 144115188075855873, InfoName = "Ion Pilot-007",
			StateText = "Antenna", Position = new VRageMath.Vector3D(1234.5, -67.25, 158737.2), BroadcastRadius = 20000,
			HasReceiver = true, HasRemote = true, MainRemoteOwner = 144115188075855873, MainRemoteId = 1259,
			HudParams = new List<MyObjectBuilder_HudEntityParams>
			{
				new MyObjectBuilder_HudEntityParams { EntityId = 1258, Text = "Ion Pilot-007", Owner = 144115188075855873,
					Position = new VRageMath.Vector3D(1234.5, -67.25, 158737.2) },
			},
		};
		var entry = new AntennaGraphEntry("node-1", source.AntennaEntityId, AntennaGraphCodec.WriteBuilder(source), 1260);
		byte[] xml = Convert.FromBase64String(entry.Builder);
		MyObjectBuilder_ProxyAntenna Stock()
		{
			using var stream = new MemoryStream(xml, writable: false);
			Assert(MyObjectBuilderSerializerKeen.DeserializeXML(stream, out MyObjectBuilder_ProxyAntenna parsed), "antenna-parse-alloc: the stock parse failed");
			return parsed;
		}
		long PerParse(Func<MyObjectBuilder_ProxyAntenna> parse)
		{
			parse();
			long before = GC.GetAllocatedBytesForCurrentThread();
			for (int index = 0; index < Parses; index++) parse();
			return (GC.GetAllocatedBytesForCurrentThread() - before) / Parses;
		}

		// Same content as the stock parse, each written back the way the graph writes it. Not compared with the
		// source string: re-serialising a parse need not reproduce it byte for byte, the stock parse is the reference.
		string ours = AntennaGraphCodec.WriteBuilder(AntennaGraphCodec.ReadBuilder(entry));
		Assert(AntennaGraphCodec.WriteBuilder(Stock()) == ours, "antenna-parse-alloc: ReadBuilder and the stock parse differ");
		Assert(ours.Length > 0 && ours != AntennaGraphCodec.WriteBuilder(AntennaGraphCodec.ReadBuilder(entry with
			{ Builder = AntennaGraphCodec.WriteBuilder(new MyObjectBuilder_ProxyAntenna { EntityId = 0, AntennaEntityId = 1257, InfoEntityId = 1258,
				Owner = 1, Position = new VRageMath.Vector3D(1, 2, 3), BroadcastRadius = 100 }) })),
			"antenna-parse-alloc: two different builders read back the same");
		AssertThrows(() => AntennaGraphCodec.ReadBuilder(entry with { Builder = Convert.ToBase64String(new byte[] { (byte)'<', (byte)'x' }) }),
			"antenna-parse-alloc: a malformed builder must be refused");

		long stock = PerParse(Stock);
		long read = PerParse(() => AntennaGraphCodec.ReadBuilder(entry));
		MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] self-test antenna-parse-alloc: stock DeserializeXML {stock} B/parse, ReadBuilder {read} B/parse");
		// The stock parse re-interns ~7000 names per call (421 KB per parse on the 2026-10-21 bench, ReadBuilder
		// 53 KB); ReadBuilder's remainder is the reader's buffers and the builder itself.
		Assert(stock > 50_000, $"antenna-parse-alloc: the control measured only {stock} B per stock parse; the measurement is not seeing the name table");
		Assert(read * 4 < stock, $"antenna-parse-alloc: ReadBuilder allocates {read} B per parse, stock {stock} B: the per-thread name table is not reused");
	}
}
