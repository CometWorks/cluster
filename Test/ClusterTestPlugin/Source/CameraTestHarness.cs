using System;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// P2-CON-01 client-view demand test seam. Spawns a static probe grid holding a single camera block, and flips
/// its viewer count. A real remote viewer raises the camera's synced m_activeViewers from its own client (the
/// View button path); the server-side count is all the position registry's demand feed reads, so the toggle
/// stands in for the client half exactly like conceal-test-teleport stands in for a real approach.
/// </summary>
public static class CameraTestHarness
{
	private static int m_probeCounter;

	public static object Spawn(double x, double y, double z)
	{
		var camera = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CameraBlock>("LargeCameraBlock");
		camera.Min = new SerializableVector3I(0, 0, 0);
		var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
		builder.GridSizeEnum = MyCubeSize.Large;
		builder.IsStatic = true;
		builder.CreatePhysics = true;
		builder.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
		builder.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(x, y, z), Vector3D.Forward, Vector3D.Up);
		builder.DisplayName = "cluster-camera-probe-" + ++m_probeCounter;
		builder.CubeBlocks.Add(camera);
		MyEntity spawned = MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false)
			?? throw new InvalidOperationException("Could not spawn the camera probe grid");
		MyCameraBlock block = ((MyCubeGrid)spawned).GetFatBlocks<MyCameraBlock>().FirstOrDefault()
			?? throw new InvalidOperationException("The camera probe grid spawned without its camera block");
		return new { grid = spawned.EntityId, camera = block.EntityId, name = builder.DisplayName };
	}

	public static object SetView(long entityId, bool active)
	{
		if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyCameraBlock camera)
			throw new System.Collections.Generic.KeyNotFoundException("No camera block with entity id " + entityId);
		camera.OnActiveChanged(active);
		return new { camera = entityId, viewed = ((Sandbox.ModAPI.Ingame.IMyCameraBlock)camera).IsActive };
	}
}
