using System;
using System.Collections.Generic;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// CH 2026-10-08 10:08: ImportRoundTrip.ResidualShape, the import warm-up's per-boot residual shape. The same fields
	/// with other entity ids and other values give the same hash; a PLANTED field (the known positive), a dropped one
	/// or another message kind gives another; entries are distinct per field.
	/// </summary>
	private static void ResidualShapeRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("residual-shape: " + message); }
		const string P = "MyObjectBuilder_Sector/SectorObjects/MyObjectBuilder_EntityBase#";
		List<string> boot1 = new List<string>
		{
			P + "72057594037927936/OwnerId: 0 != 144115188075855873",
			P + "72057594037927936: 13 != 12 child elements",
			P + "72057594037927936/CubeBlocks/MyObjectBuilder_CubeBlock#72057594037927937/BuiltBy: present on the source side, absent on the target side",
			P + "72057594037927936/CubeBlocks/MyObjectBuilder_CubeBlock#72057594037927938/BuiltBy: present on the source side, absent on the target side",
		};
		List<string> boot2 = new List<string>
		{
			P + "72057594037930000/OwnerId: 0 != 144115188075855999",
			P + "72057594037930000: 13 != 12 child elements",
			P + "72057594037930000/CubeBlocks/MyObjectBuilder_CubeBlock#72057594037930001/BuiltBy: present on the source side, absent on the target side",
			P + "72057594037930000/CubeBlocks/MyObjectBuilder_CubeBlock#72057594037930002/BuiltBy: present on the source side, absent on the target side",
		};
		List<string> shape1 = ImportRoundTrip.ResidualShape(boot1, out string hash1);
		ImportRoundTrip.ResidualShape(boot2, out string hash2);
		Check(hash1 == hash2, "other ids and values keep the hash");
		Check(shape1.Count == 3, "one entry per field (two blocks' BuiltBy are one), got " + shape1.Count);
		Check(shape1.Contains("EntityBase/CubeBlocks/CubeBlock/BuiltBy: present on the source side, absent on the target side"),
			"ids dropped and prefix shortened: " + string.Join(" | ", shape1));
		Check(shape1.Contains("EntityBase/OwnerId: N != N"), "digits masked: " + string.Join(" | ", shape1));
		List<string> planted = new List<string>(boot1) { P + "72057594037927936/Planted: present on the source side, absent on the target side" };
		ImportRoundTrip.ResidualShape(planted, out string hashPlanted);
		Check(hashPlanted != hash1, "KNOWN POSITIVE: a planted field changes the hash");
		ImportRoundTrip.ResidualShape(boot1.GetRange(1, 3), out string hashDropped);
		Check(hashDropped != hash1, "a dropped field changes the hash");
		List<string> kind = new List<string>(boot1); kind[0] = P + "72057594037927936/OwnerId: present on the source side, absent on the target side";
		ImportRoundTrip.ResidualShape(kind, out string hashKind);
		Check(hashKind != hash1, "another message kind on the same field changes the hash");
		ImportRoundTrip.ResidualShape(new List<string>(), out string hashEmpty);
		Check(hashEmpty.Length == 8 && hashEmpty != hash1, "an empty list has its own hash");
	}
}
