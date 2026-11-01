using VRage;

namespace ClusterPlugin;

/// <summary>
/// Seams of VRage.MyEntityIdentifier. The AllocateId prefix lives in
/// Shared.Patches.MyEntityIdentifierPatch; the batch reservation
/// lives here (MyEntityIdBatch in Shared/Cluster/Seams calls it).
/// </summary>
public static class MyEntityIdentifierSeams
{
	/// <summary>
	/// ClusterPlugin addition: reserves <paramref name="count"/> consecutive ids of one type in a
	/// single allocator round trip. Returns false outside cluster mode, where ids are random and no
	/// contiguous run exists; callers then fall back to AllocateId per id (see MyEntityIdBatch).
	/// </summary>
	public static bool AllocateIds(int count, out long first, MyEntityIdentifier.ID_OBJECT_TYPE objectType = MyEntityIdentifier.ID_OBJECT_TYPE.ENTITY)
	{
		if (!EntityIdAllocator.Enabled)
		{
			first = 0L;
			return false;
		}
		first = MyEntityIdentifier.ConstructId(objectType, EntityIdAllocator.NextIds(count));
		return true;
	}
}
