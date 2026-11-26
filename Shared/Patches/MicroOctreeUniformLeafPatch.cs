using HarmonyLib;
using Sandbox.Engine.Voxels;
using VRage.Voxels;

namespace Shared.Patches;

/// <summary>
/// SE-1950: a voxel write that leaves a 16^3 content leaf of an octree storage uniform at a value other than empty
/// (0) or full (255) does not store that value. MyOctreeStorage.WriteRangeOps.LeafOp asks the rebuilt leaf
/// TryGetUniformValue, drops the leaf when it says yes, and keeps only the node's filtered value for that child,
/// which is what a read at LOD 0 then returns. The content filter (MyOctreeNode.SignedDistanceFilterInternal) halves
/// the signed distance of every value but 0 and 255, so a uniform 114 reads back as 121.
/// <para>
/// For one storage that is a small loss. For a replica it is a divergence: the origin captures the 121 it reads and
/// publishes it, the World Authority writes 121, its leaf collapses again and it holds 124, and every further copy
/// moves another step towards 128. P2-VOX-OCTANT (32^3 cubes of uniform content) diverged in every 16^3 leaf a cube
/// covered completely, and nowhere else.
/// </para>
/// <para>
/// The postfix answers "not uniform" for such a content leaf, so the storage keeps the leaf, which stores the value
/// exactly. Empty and full leaves still collapse: the filter keeps 0 and 255 as they are. Material leaves still
/// collapse too: their histogram filter of one value is that value. A kept leaf is a micro-octree of a single node.
/// </para>
/// </summary>
[HarmonyPatch(typeof(MyMicroOctreeLeaf), nameof(MyMicroOctreeLeaf.TryGetUniformValue))]
public static class MicroOctreeUniformLeafPatch
{
    [HarmonyPostfix]
    private static void Postfix(MyMicroOctreeLeaf __instance, byte uniformValue, ref bool __result)
    {
        if (
            __result
            && __instance.m_dataType == MyStorageDataTypeEnum.Content
            && uniformValue != 0
            && uniformValue != byte.MaxValue
        )
            __result = false;
    }
}
