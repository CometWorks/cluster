using ClusterPlugin;

namespace VRage;

/// <summary>
/// ClusterPlugin addition: a small per-caller reserve of entity ids for loops that allocate many
/// (blueprint/projector/paste remapping). In cluster mode it draws 100 consecutive ids per refill
/// from the registry-issued range and abandons the unused tail when the caller is done; outside
/// cluster mode every Next() is a vanilla AllocateId.
/// </summary>
public struct MyEntityIdBatch
{
	public const int DefaultSize = 100;

	private readonly MyEntityIdentifier.ID_OBJECT_TYPE m_type;
	private readonly int m_size;
	private long m_next;
	private int m_remaining;

	public MyEntityIdBatch(MyEntityIdentifier.ID_OBJECT_TYPE type = MyEntityIdentifier.ID_OBJECT_TYPE.ENTITY, int size = DefaultSize)
	{
		m_type = type;
		m_size = size;
		m_next = 0L;
		m_remaining = 0;
	}

	public long Next()
	{
		if (m_remaining == 0)
		{
			// A default-initialised struct (`new MyEntityIdBatch()` or a zeroed field) has size 0
			// and type UNKNOWN (= 0): treat it as the default ENTITY batch of DefaultSize instead
			// of asking for 0 ids of type UNKNOWN.
			int size = m_size > 0 ? m_size : DefaultSize;
			MyEntityIdentifier.ID_OBJECT_TYPE type = m_type == MyEntityIdentifier.ID_OBJECT_TYPE.UNKNOWN ? MyEntityIdentifier.ID_OBJECT_TYPE.ENTITY : m_type;
			if (!MyEntityIdentifierSeams.AllocateIds(size, out m_next, type))
			{
				return MyEntityIdentifier.AllocateId(type);
			}
			m_remaining = size;
		}
		m_remaining--;
		return m_next++;
	}

	/// <summary>Drops the unused tail; the next call reserves a fresh run.</summary>
	public void Clear()
	{
		m_remaining = 0;
	}
}
