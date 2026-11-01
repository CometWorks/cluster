using Sandbox.Game.Entities;
using VRage.Game;
using VRage.Game.Voxels;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRageMath;

namespace ClusterPlugin;

internal static class PlanetStoragePatch
{
	public static bool InitPrefix(MyPlanet __instance, MyObjectBuilder_EntityBase builder)
	{
		if (VoxelJournalService.Instance?.TrySelectBase(builder, out IMyStorage storage, out long tailSequence) != true) return true;
		try
		{
			__instance.Init(builder, storage);
			VoxelJournalService.Instance.CommitBase(((MyObjectBuilder_Planet)builder).StorageName, tailSequence);
			return false;
		}
		catch
		{
			storage.Close();
			throw;
		}
	}

	public static void Postfix(MyPlanet __instance, Vector3I minChanged, Vector3I maxChanged, MyStorageDataTypeFlags dataChanged)
		=> VoxelJournalService.Instance?.Observe(__instance, minChanged, maxChanged, dataChanged);
}
