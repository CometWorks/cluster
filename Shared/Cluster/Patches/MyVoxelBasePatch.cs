using Sandbox.Game.Entities;
using VRageMath;

namespace ClusterPlugin;

internal static class MyVoxelBasePatch
{
	public static void RequestVoxelCutoutSpherePostfix(MyVoxelBase __instance, Vector3D center, float radius,
		bool createDebris, bool damage)
		=> VoxelJournalService.Instance?.ObserveCutoutSphere(__instance, center, radius, createDebris, damage);
}
