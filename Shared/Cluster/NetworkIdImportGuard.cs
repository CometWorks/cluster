using System;

namespace ClusterPlugin;

internal sealed class NetworkIdImportGuard
{
	private Guid? m_owner;

	public void Acquire(Guid owner)
	{
		if (m_owner.HasValue && m_owner.Value != owner)
			throw new InvalidOperationException("Another NetworkId import is in progress");
		m_owner = owner;
	}

	public void Require(Guid owner)
	{
		if (m_owner != owner)
			throw new InvalidOperationException("NetworkId import is not prepared by this transfer");
	}

	public bool IsOwnedBy(Guid owner) => m_owner == owner;

	public bool Release(Guid owner)
	{
		if (m_owner != owner) return false;
		m_owner = null;
		return true;
	}
}
