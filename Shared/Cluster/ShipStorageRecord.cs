using System;
using System.IO;
using System.IO.Compression;
using Sandbox.Common.ObjectBuilders;
using VRage.Game;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// The ship storage state the World Authority keeps in its global save and publishes to the nodes (SE1-0233,
/// SE1-0248). MagnetarWorld's to-vanilla reads the same section to write the stored ships back into a world (SE1-0249).
/// </summary>
internal static class ShipStorageRecord
{
	/// <summary>
	/// One stored ship. Blueprint, TakenBy, Take, Taken (UTC) and Fetched (the taker read the blueprint) are World
	/// Authority only; the index carries none of them.
	/// </summary>
	public sealed record Ship(Guid Id, long Owner, int Kind, float[] Relative, double[] Terminal, float[] Orientation,
		string Details, long[] Origins, string Blueprint = null, string TakenBy = null, Guid Take = default,
		DateTime Taken = default, bool Fetched = false);
	/// <summary>A delayed retrieval order. Due is World Authority only; the index carries RemainingMs instead.</summary>
	public sealed record Order(Guid Id, string DisplayName, long RemainingMs, long CreditsPaid, ulong CallerEndpointId,
		long CallerEntityId, DateTime Due = default);
	/// <summary>A player's retrieval queue and auto-deploy switch, vanilla's UserData.</summary>
	public sealed record User(long Identity, bool AutoDeploy, Order[] Orders);
	public sealed record Index(Ship[] Ships, long[] Origins, User[] Users = null);
	public sealed record Tombstone(long[] Origins, DateTime Until);
	public sealed record Record(Ship[] Ships, Tombstone[] Tombstones, User[] Users = null);

	/// <summary>The session component's entry for a ship; details stay null when only the save needs it ([XmlIgnore]).</summary>
	public static MyStoredGridData ToData(Ship ship, MyObjectBuilder_SavedGridDetails details, bool inTransition) => new MyStoredGridData
	{
		OwnerId = ship.Owner,
		OwnershipKind = (MyStoredGridOwnershipKind)ship.Kind,
		RelativePosition = new Vector3(ship.Relative[0], ship.Relative[1], ship.Relative[2]),
		StorageEntityPosition = new Vector3D(ship.Terminal[0], ship.Terminal[1], ship.Terminal[2]),
		Orientation = new Quaternion(ship.Orientation[0], ship.Orientation[1], ship.Orientation[2], ship.Orientation[3]),
		InTransition = inTransition,
		SavedGridDetails = details,
	};

	/// <summary>A blueprint file (gs-&lt;id&gt;.sbc) as the record keeps it: gzipped, base64.</summary>
	public static string Pack(byte[] bytes)
	{
		using var packed = new MemoryStream();
		using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(bytes, 0, bytes.Length);
		return Convert.ToBase64String(packed.ToArray());
	}

	public static byte[] Unpack(string blueprint)
	{
		using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(blueprint)), CompressionMode.Decompress);
		using var bytes = new MemoryStream();
		gzip.CopyTo(bytes);
		return bytes.ToArray();
	}
}
