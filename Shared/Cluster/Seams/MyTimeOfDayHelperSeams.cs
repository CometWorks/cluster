using System;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using Sandbox.Game.WorldEnvironment;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.WorldEnvironment.MyTimeOfDayHelper</c>. The route prefix is
/// installed by <c>Shared.Patches.MyTimeOfDayHelperPatch</c>; the publish helper raises the private client
/// event directly through the publicized method group (the ledger's "keep ClusterPublishTimeOfDay as a helper").
/// </summary>
public static class MyTimeOfDayHelperSeams
{
	/// <summary>
	/// Cluster seam: an admin's time-of-day change moves <see cref="MySession.ElapsedGameTime"/>, which in a
	/// cluster belongs to the World Authority - its game time is the clock every node follows and the only one
	/// a save carries. Returns true when the cluster took the change over, leaving this session untouched.
	/// </summary>
	public static Func<float, bool> ClusterRouteTimeOfDay;

	/// <summary>
	/// Cluster apply: hands this node's clients the game time they must show. A node follows the authority
	/// clock, so a time-of-day change reaches it as a clock correction with nothing to tell the clients about.
	/// </summary>
	public static void ClusterPublishTimeOfDay(float time)
	{
		if (MySession.Static == null)
		{
			return;
		}
		MyTimeOfDayHelper.timeOfDay = time;
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyTimeOfDayHelper.UpdateTimeOfDayClient, MySession.Static.ElapsedGameTime.Ticks, time);
	}
}
