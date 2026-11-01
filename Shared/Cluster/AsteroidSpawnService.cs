using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Sandbox.Engine.Voxels;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using VRage.Voxels;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Cluster-wide asteroid materialization: a WA-accepted spawn spec is published to every node, and
/// each node deterministically creates the identical procedural voxel map (same storage name, seed,
/// radius, and min corner) so journal entries for it resolve everywhere. The storage NAME encodes
/// the whole spec - <c>ClusterAsteroid-&lt;seed&gt;-&lt;radius&gt;-&lt;x&gt;_&lt;y&gt;_&lt;z&gt;</c> -
/// which lets restart recovery re-materialize the asteroid from the voxel base catalog alone, with
/// no separately persisted spec store.
/// </summary>
internal static class AsteroidSpawnService
{
	private const string Subsystem = "asteroid-spawn";
	private const string Prefix = "ClusterAsteroid-";
	private static readonly object Sync = new object();
	private static readonly HashSet<string> Specs = new HashSet<string>(StringComparer.Ordinal);

	public static void Install()
	{
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveRequest);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ReceiveSnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ReceiveSnapshot);
	}

	public static string TestSpawn(double x, double y, double z, int radius, int seed)
	{
		if (!ClusterRuntime.IsWorldAuthority) throw new InvalidOperationException("This node is not the World Authority");
		if (radius < 8 || radius > 512) throw new ArgumentException("Asteroid radius must be within 8..512 meters");
		string name = string.Format(CultureInfo.InvariantCulture, "{0}{1}-{2}-{3}_{4}_{5}",
			Prefix, seed, radius, (long)x, (long)y, (long)z);
		ReceiveRequest(string.Empty, name);
		return JsonSerializer.Serialize(new { name });
	}

	private static void ReceiveRequest(string origin, string payload)
	{
		if (TryParse(payload) == null) return;
		bool grew;
		string[] snapshot = null;
		lock (Sync)
		{
			grew = Specs.Add(payload);
			if (grew) snapshot = Specs.OrderBy(x => x, StringComparer.Ordinal).ToArray();
		}
		if (grew && ClusterRuntime.IsWorldAuthority)
			GlobalStateChannel.Publish(Subsystem, JsonSerializer.Serialize(snapshot));
	}

	private static void ReceiveSnapshot(string payload)
	{
		string[] names;
		try { names = JsonSerializer.Deserialize<string[]>(payload); }
		catch (Exception) { return; }
		if (names == null) return;
		lock (Sync)
			foreach (string name in names)
				if (TryParse(name) != null) Specs.Add(name);
	}

	private static string Snapshot()
	{
		lock (Sync) return JsonSerializer.Serialize(Specs.OrderBy(x => x, StringComparer.Ordinal).ToArray());
	}

	/// <summary>Parses a spec-encoding storage name; the coordinate block may contain minus signs.</summary>
	internal static (int Seed, int Radius, Vector3D Position)? TryParse(string name)
	{
		if (name == null || !name.StartsWith(Prefix, StringComparison.Ordinal)) return null;
		string[] parts = name.Substring(Prefix.Length).Split('-', 3);
		if (parts.Length != 3) return null;
		string[] coordinates = parts[2].Split('_');
		if (coordinates.Length != 3) return null;
		if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)
			|| !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int radius)
			|| !long.TryParse(coordinates[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long x)
			|| !long.TryParse(coordinates[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long y)
			|| !long.TryParse(coordinates[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long z))
			return null;
		return (seed, radius, new Vector3D(x, y, z));
	}

	/// <summary>
	/// Runs on the game thread. Materializes any known spec whose storage is absent from the
	/// session - unless the voxel base catalog already holds content for it, in which case the
	/// journal service installs the catalogued base instead (it is newer than a fresh shape).
	/// </summary>
	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || Sandbox.Game.World.MySession.Static?.Ready != true) return;
		string[] pending;
		lock (Sync) pending = Specs.ToArray();
		foreach (string name in pending)
		{
			(int Seed, int Radius, Vector3D Position)? spec = TryParse(name);
			if (spec == null) continue;
			if (Sandbox.Game.World.MySession.Static.VoxelMaps.TryGetVoxelMapByName(name) != null) continue;
			if (VoxelJournalService.Instance?.HasBase(name) == true) continue;
			MyStorageBase storage = new MyOctreeStorage(
				MyCompositeShapeProvider.CreateAsteroidShape(spec.Value.Seed, spec.Value.Radius, 0),
				MyVoxelCoordSystems.FindBestOctreeSize(spec.Value.Radius));
			Vector3D corner = spec.Value.Position - new Vector3D(storage.Size.X, storage.Size.Y, storage.Size.Z) * 0.5;
			MyWorldGenerator.AddVoxelMap(name, storage, corner);
			Console.WriteLine("[ClusterPlugin] asteroid materialized name=" + name);
		}
	}
}
