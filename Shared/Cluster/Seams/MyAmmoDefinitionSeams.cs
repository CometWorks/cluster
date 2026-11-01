using System;
using Sandbox.Definitions;

namespace ClusterPlugin;

/// <summary>
/// Seams of Sandbox.Definitions.MyAmmoDefinition: the ProjectileRangePatch
/// delegates. Consumed by the transpilers in Shared.Patches.MyProjectilePatch and MyMissilePatch.
/// </summary>
public static class MyAmmoDefinitionSeams
{
	// Called with the final vanilla trajectory of a projectile/missile; returns the trajectory to use.
	public static Func<MyAmmoDefinition, float, float> ClusterClampTrajectory;

	// (definition, trajectory, expiredByDistance) -> true to destroy a range-capped missile without an
	// endpoint explosion.
	public static Func<MyAmmoDefinition, float, bool, bool> ClusterSuppressCappedExplosion;
}
