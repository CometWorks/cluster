using Sandbox.Definitions;
using Sandbox.Game.Entities;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.Entities.MyMeteorShower</c>: a call-in only, written against
/// the publicized private statics (<c>IsRestricted</c>, <c>CalculateMeteorAmount</c>, <c>StartWave</c>). No
/// Harmony patch is needed.
/// </summary>
public static class MyMeteorShowerSeams
{
	// ClusterPlugin seam: per-player MeteorWave events only activate on player join, which never happens on the
	// World Authority, so vanilla's scheduler cannot fire there. The WA keeps its own per-player schedule and the
	// owning node runs the wave here with vanilla's own restriction check, radii, and amount. Returns the meteor
	// count spawned, or -1 when the position is meteor-restricted (a planet with AllowMeteors false).
	public static int ClusterTryStartWave(Vector3D position)
	{
		if (MyMeteorShower.IsRestricted(position))
		{
			return -1;
		}
		float targetRadius = MyDefinitionManager.Static.GetMeteorEventTargetRadius() ?? 500f;
		float disableRadius = MyDefinitionManager.Static.GetMeteorEventDisableRadius() ?? 3000f;
		int amount = MyMeteorShower.CalculateMeteorAmount();
		MyMeteorShower.StartWave(new BoundingSphereD(position, targetRadius), disableRadius, amount);
		return amount;
	}
}
