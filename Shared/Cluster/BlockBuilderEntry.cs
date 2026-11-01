using System;
using System.Linq;
using Sandbox.Game.Entities;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Serialization;

namespace ClusterPlugin;

/// <summary>
/// Cluster state of a block kept in the block's own object builder, as a mod storage entry under a key of the
/// caller's. Whatever moves the builder carries it: handover (full and delta), partition saves, offline
/// partitions, recovery. SE1-0232. The usual shape: a GetObjectBuilderCubeBlock postfix calls
/// <see cref="Write"/>, an Init postfix calls <see cref="Read"/> and then <see cref="Forget"/>.
/// The entry stays out of the block's runtime storage component, where vanilla would log the unregistered
/// key on every save and drop it; the postfix writes it again each time, so a builder written before and
/// after a round trip compares equal at the handover gate.
/// </summary>
internal static class BlockBuilderEntry
{
	private const string StorageTypeId = nameof(MyModStorageComponentBase);

	public static string Read(MyObjectBuilder_CubeBlock builder, Guid key) => Read(builder?.ComponentContainer, key);

	/// <summary>The same entry on an entity whose components vanilla never (de)serializes, as a meteor (#1381).</summary>
	public static string Read(MyObjectBuilder_EntityBase builder, Guid key) => Read(builder?.ComponentContainer, key);

	private static string Read(MyObjectBuilder_ComponentContainer container, Guid key)
	{
		foreach (var data in container?.Components ?? Enumerable.Empty<MyObjectBuilder_ComponentContainer.ComponentData>())
			if (data.Component is MyObjectBuilder_ModStorageComponent storage && storage.Storage?.Dictionary?.TryGetValue(key, out string value) == true)
				return value;
		return null;
	}

	public static void Write(MyObjectBuilder_CubeBlock builder, Guid key, string value)
		=> Write(builder.ComponentContainer ??= new MyObjectBuilder_ComponentContainer(), key, value);

	public static void Write(MyObjectBuilder_EntityBase builder, Guid key, string value)
		=> Write(builder.ComponentContainer ??= new MyObjectBuilder_ComponentContainer(), key, value);

	private static void Write(MyObjectBuilder_ComponentContainer container, Guid key, string value)
	{
		var data = container.Components.FirstOrDefault(entry => entry.Component is MyObjectBuilder_ModStorageComponent);
		if (data == null)
		{
			var component = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ModStorageComponent>();
			component.Storage = new SerializableDictionary<Guid, string>();
			data = new MyObjectBuilder_ComponentContainer.ComponentData { TypeId = StorageTypeId, Component = component };
			container.Components.Add(data);
		}
		((MyObjectBuilder_ModStorageComponent)data.Component).Storage[key] = value;
	}

	/// <summary>After Init: drop the entry the builder put into the block's runtime storage component.</summary>
	public static void Forget(MyCubeBlock block, Guid key) => block.Storage?.RemoveValue(key);
}
