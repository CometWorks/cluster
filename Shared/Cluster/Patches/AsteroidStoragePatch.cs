using Sandbox.Game.Entities;
using VRage.Game.Voxels;
using VRage.ObjectBuilders;

namespace ClusterPlugin;

/// <summary>
/// SE1-0114: an asteroid saved in the world (a pre-placed one, or a procedural one a player had edited) is
/// loaded from the world folder, which holds its content as of the world's conversion. Once the World
/// Authority has compacted its journal, the entries before the catalogued base are gone, so a node that
/// loads that file never sees those edits. Load the base instead, as planets do.
/// </summary>
internal static class AsteroidStoragePatch
{
	public static bool InitPrefix(MyVoxelMap __instance, MyObjectBuilder_EntityBase builder)
	{
		if (VoxelJournalService.Instance?.TrySelectAsteroidBase(builder, out IMyStorage storage, out long tailSequence) != true) return true;
		try
		{
			__instance.Init(builder, storage);
			// The base carries edits, so the map is changed content whatever the world's builder said.
			__instance.ContentChanged = true;
			VoxelJournalService.Instance.CommitBase(__instance.StorageName, tailSequence);
			return false;
		}
		catch
		{
			storage.Close();
			throw;
		}
	}
}
