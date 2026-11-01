using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// Journal the
/// vanilla drill/explosion cutout notification after the original reliable broadcast. Inert while
/// VoxelJournalService has not installed the seam.
/// </summary>
[HarmonyPatch(typeof(MyVoxelBase))]
public static class MyVoxelBasePatch
{
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyVoxelBase.RequestVoxelCutoutSphere), typeof(Vector3D), typeof(float), typeof(bool), typeof(bool))]
	private static void RequestVoxelCutoutSpherePostfix(MyVoxelBase __instance, Vector3D center, float radius, bool createDebris, bool damage)
	{
		MyVoxelBaseSeams.ClusterVoxelCutoutSphere?.Invoke(__instance, center, radius, createDebris, damage);
	}
}
