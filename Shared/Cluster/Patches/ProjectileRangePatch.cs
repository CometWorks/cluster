using System.Collections.Generic;
using Sandbox.Definitions;
using VRage.Game;
using VRage.Utils;

namespace ClusterPlugin;

internal static class ProjectileRangePatch
{
	private static readonly HashSet<MyDefinitionId> Warnings = new HashSet<MyDefinitionId>();
	private static float m_cap = float.PositiveInfinity;
	public static float Cap => m_cap;

	public static void Configure(float cap)
	{
		m_cap = cap;
		MyAmmoDefinitionSeams.ClusterClampTrajectory = ClampTrajectory;
		MyAmmoDefinitionSeams.ClusterSuppressCappedExplosion = SuppressCappedExplosion;
	}

	public static float ClampTrajectory(MyAmmoDefinition definition, float trajectory)
	{
		if (trajectory <= m_cap) return trajectory;
		lock (Warnings)
			if (Warnings.Add(definition.Id))
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] projectile range capped definition=" + definition.Id
					+ " configured=" + trajectory + " cap=" + m_cap);
		return m_cap;
	}

	public static bool SuppressCappedExplosion(MyAmmoDefinition definition, float trajectory, bool expired) =>
		expired && definition is MyMissileAmmoDefinition missile && trajectory < missile.MaxTrajectory;
}
