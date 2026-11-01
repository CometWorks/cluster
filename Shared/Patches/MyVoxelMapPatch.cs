using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.ObjectBuilders;

namespace Shared.Patches;

/// <summary>
/// The MyVoxelMap injection point of the voxel journal, the asteroid counterpart of
/// <see cref="MyPlanetPatch"/>. Inert while VoxelJournalService has not installed the seam.
/// </summary>
[HarmonyPatch(typeof(MyVoxelMap))]
public static class MyVoxelMapPatch
{
	/// <summary>
	/// Prefix on the one-argument Init: a matching catalogued native base replaces the storage the vanilla
	/// body would load from the world folder; the explicit two-argument overload is not patched.
	/// </summary>
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyVoxelMap.Init), typeof(MyObjectBuilder_EntityBase))]
	private static bool InitPrefix(MyVoxelMap __instance, MyObjectBuilder_EntityBase builder)
	{
		var seam = MyVoxelMapSeams.ClusterStorageInit;
		return seam == null || seam(__instance, builder);
	}
}
