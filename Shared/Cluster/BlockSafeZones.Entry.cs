using System;
using System.IO;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace ClusterPlugin;

/// <summary>
/// The entry itself: the zone's builder as XML under <see cref="Key"/>. MagnetarWorld links this file too, since
/// <c>to-split</c> moves a block's zone from the sector into its block's builder (SE1-0253).
/// </summary>
internal static partial class BlockSafeZones
{
	internal static readonly Guid Key = new Guid("5c1a0231-5afe-4a2e-9d0e-b10c25afe200");

	/// <summary>The zone's builder without what it is rebuilt from on arrival: its pose and what it holds.</summary>
	internal static string Serialize(MyObjectBuilder_SafeZone builder)
	{
		builder.PositionAndOrientation = null;
		builder.ContainedEntities = null;
		builder.ContainedGrids = null;
		using var stream = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, builder))
			throw new InvalidDataException("Safe zone " + builder.EntityId + " did not serialize");
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	internal static MyObjectBuilder_SafeZone Parse(string value)
	{
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value), writable: false);
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(stream, out MyObjectBuilder_SafeZone builder))
			throw new InvalidDataException("Unreadable safe zone entry");
		return builder;
	}
}
