using System.IO;
using System.Text;
using System.Text.Json;
using Sandbox.Common.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace ClusterPlugin;

/// <summary>
/// The admin safe zone set as the World Authority publishes it and keeps it in its global save: each zone's builder
/// as XML, with the World Authority generation and the set's version, which grows with each change. MagnetarWorld
/// links this file, since <c>to-vanilla</c> writes the set into the vanilla world's sector.
/// </summary>
internal static partial class AdminSafeZones
{
	internal sealed record Set(long Generation, long Version, string[] Zones);

	/// <summary>The zone's builder as XML, without the entities it holds: each node works those out itself.</summary>
	internal static string ToXml(MyObjectBuilder_SafeZone zone)
	{
		zone.ContainedEntities = null;
		zone.ContainedGrids = null;
		using var stream = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, zone))
			throw new InvalidDataException("Safe zone " + zone.EntityId + " did not serialize");
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	internal static MyObjectBuilder_SafeZone FromXml(string xml)
	{
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml), writable: false);
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(stream, out MyObjectBuilder_SafeZone zone) || zone == null)
			throw new InvalidDataException("Unreadable admin safe zone");
		return zone;
	}

	internal static Set ReadSet(string json)
		=> JsonSerializer.Deserialize<Set>(json) is { Zones: not null } set ? set : throw new InvalidDataException("Invalid admin safe zone set");
}
