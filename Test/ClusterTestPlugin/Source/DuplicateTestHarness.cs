using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

using ClusterPlugin;
using static ClusterPlugin.ClusterControlServer;

namespace ClusterTestPlugin;

/// <summary>
/// T-0169 / P2-CHR-03 test seam: a stale duplicate of a live character. POST
/// /duplicate-test?entity=E&amp;op=capture keeps the character's object builder in memory; op=plant
/// re-creates it later with the same entity id and no owning identity - the shape of the w32 Pilot-018
/// copy recovered from an old save - once this node no longer holds that id, and lets the topology
/// sweep give it a partition of its own.
/// #1286 / P1-REC-16: op=lift keeps any entity's object builder and closes the entity; op=drop re-creates it with
/// the same id, offset by dx metres, once this node no longer holds the id - an entity that left its partition
/// without a lineage record and lands in a new partition with no save yet.
/// </summary>
internal static class DuplicateTestHarness
{
	private static readonly Dictionary<long, MyObjectBuilder_Character> s_captured = new();
	private static readonly Dictionary<long, MyObjectBuilder_EntityBase> s_lifted = new();

	public static void Register()
	{
		TestRoutes.Add("POST", "/duplicate-test", context =>
		{
			long entityId = long.Parse(context.Request.QueryString["entity"] ?? throw new ArgumentException("Missing entity query parameter"));
			string op = context.Request.QueryString["op"] ?? throw new ArgumentException("Missing op query parameter");
			string result = Invoke(() => op switch
			{
				"capture" => Capture(entityId),
				"plant" => Plant(entityId),
				"lift" => Lift(entityId),
				"drop" => Drop(entityId, double.Parse(context.Request.QueryString["dx"] ?? "0", System.Globalization.CultureInfo.InvariantCulture)),
				_ => throw new ArgumentException("op must be capture, plant, lift or drop"),
			});
			Write(context, 200, "application/json", System.Text.Encoding.UTF8.GetBytes(result));
		});
	}

	private static string Capture(long entityId)
	{
		if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyCharacter character)
			throw new ArgumentException("Unknown character " + entityId);
		s_captured[entityId] = (MyObjectBuilder_Character)character.GetObjectBuilder(copy: true);
		return JsonSerializer.Serialize(new { captured = entityId });
	}

	private static string Lift(long entityId)
	{
		if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity))
			throw new ArgumentException("Unknown entity " + entityId);
		ulong partition = PartitionOfEntity(entityId);
		s_lifted[entityId] = entity.GetObjectBuilder(copy: true);
		entity.Close();
		return JsonSerializer.Serialize(new { lifted = entityId, partition });
	}

	private static string Drop(long entityId, double dx)
	{
		if (!s_lifted.TryGetValue(entityId, out MyObjectBuilder_EntityBase builder))
			throw new ArgumentException("Nothing lifted for " + entityId);
		if (MyEntities.EntityExists(entityId))
			throw new InvalidOperationException("Entity " + entityId + " still exists on this node");
		if (builder.PositionAndOrientation is { } placed)
			builder.PositionAndOrientation = new VRage.MyPositionAndOrientation(placed.Position + new VRageMath.Vector3D(dx, 0, 0), placed.Forward, placed.Up);
		MyEntity dropped = MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false)
			?? throw new InvalidOperationException("Entity creation failed");
		s_lifted.Remove(entityId);
		m_partitions.Evaluate();
		return JsonSerializer.Serialize(new { dropped = dropped.EntityId, partition = PartitionOfEntity(dropped.EntityId) });
	}

	private static string Plant(long entityId)
	{
		if (!s_captured.TryGetValue(entityId, out MyObjectBuilder_Character builder))
			throw new ArgumentException("Nothing captured for " + entityId);
		if (MyEntities.EntityExists(entityId))
			throw new InvalidOperationException("Entity " + entityId + " still exists on this node");
		builder.OwningPlayerIdentityId = null;
		MyEntity planted = MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false)
			?? throw new InvalidOperationException("Character creation failed");
		m_partitions.Evaluate();
		return JsonSerializer.Serialize(new { planted = planted.EntityId, partition = PartitionOfEntity(planted.EntityId) });
	}
}
