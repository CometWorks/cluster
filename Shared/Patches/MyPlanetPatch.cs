using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// The two MyPlanet injection points of the
/// voxel journal. Both are inert while VoxelJournalService has not installed the seams.
/// </summary>
[HarmonyPatch(typeof(MyPlanet))]
public static class MyPlanetPatch
{
	/// <summary>
	/// Prefix on the one-argument Init: a matching catalogued native base replaces the vanilla
	/// one-line body (<c>Init(builder, null)</c>); the explicit two-argument overload is not patched.
	/// </summary>
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyPlanet.Init), typeof(MyObjectBuilder_EntityBase))]
	private static bool InitPrefix(MyPlanet __instance, MyObjectBuilder_EntityBase builder)
	{
		var seam = MyPlanetSeams.ClusterStorageInit;
		return seam == null || seam(__instance, builder);
	}

	/// <summary>
	/// Postfix on the private storage_RangeChangedPlanet: runs after the vanilla physics, render,
	/// replication and content-change notifications.
	/// </summary>
	[HarmonyPostfix]
	[HarmonyPatch("storage_RangeChangedPlanet", typeof(Vector3I), typeof(Vector3I), typeof(MyStorageDataTypeFlags))]
	private static void RangeChangedPostfix(MyPlanet __instance, Vector3I minChanged, Vector3I maxChanged, MyStorageDataTypeFlags dataChanged)
	{
		MyPlanetSeams.ClusterRangeChanged?.Invoke(__instance, minChanged, maxChanged, dataChanged);
	}
}
