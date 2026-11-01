using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Replication;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyCharacterReplicable"/>. The cluster makes the replicable's far
/// broadcaster dependencies follow the radio receiver's own found/lost events instead of the 960-frame
/// dependency refresh, and adds the static invalidation the antenna proxy swap calls. The hook half lives
/// in Shared/Patches/MyCharacterReplicablePatch.cs (postfix on OnHook); the static helper is here.
/// </summary>
public static class MyCharacterReplicableSeams
{
	/// <summary>
	/// Postfix body for OnHook. GetDependencies walks the receiver's cached broadcasters-in-range, but the
	/// server only revisits those dependencies on the outermost update layer's interval - 960 frames, about
	/// 16 seconds. Every antenna that comes or goes therefore leaves the player's HUD signal marker wrong
	/// for that long: a marker missing after the cache fills (a character created by a cluster handover
	/// starts with an empty one), or a stale one after it drains. The receiver already detects both within
	/// 10 frames, so treat that as what it is - the dependency set changed.
	/// </summary>
	public static void HookRadioReceiver(MyCharacterReplicable replicable)
	{
		if (!ClusterRuntime.IsClustered || !Sync.IsServer) return;
		MyDataReceiver receiver = replicable.Instance?.RadioReceiver;
		if (receiver == null) return;
		// receiver.OnBroadcasterFound/Lost += ...; see PublicizedEvents.
		MyDataReceiver.BroadcasterChangeInfo changed = _ => replicable.DependenciesDirty = true;
		PublicizedEvents.AddOnBroadcasterFound(receiver, changed);
		PublicizedEvents.AddOnBroadcasterLost(receiver, changed);
	}

	/// <summary>
	/// Forces every player's far-broadcaster dependencies to be recomputed on the next server frame.
	/// The receiver events above miss one case: its in-range bookkeeping is keyed by antenna id, so
	/// replacing the broadcaster OBJECT behind an id - a cluster antenna proxy giving way to the real
	/// antenna, or taking over again - looks like no change at all, while the replicable the client
	/// holds is on its way out. Whoever performs that replacement calls this.
	/// </summary>
	public static int InvalidateBroadcasterDependencies()
	{
		if (!Sync.IsServer || MySession.Static == null)
		{
			return 0;
		}
		int num = 0;
		foreach (MyPlayer onlinePlayer in MySession.Static.Players.GetOnlinePlayers())
		{
			if (onlinePlayer?.Character != null && MyExternalReplicable.FindByObject(onlinePlayer.Character) is MyCharacterReplicable myCharacterReplicable)
			{
				// T-0160: rescan first. GetDependencies reads the receiver's CACHED in-range set, and a proxy
				// installed this frame is not in it yet, so the dirty flag alone was consumed against the old
				// set: node-2's End proxy (installed right after a balance migration took the real beacon to
				// node-1) never reached the client. The client-view adopt path does the same rescan.
				onlinePlayer.Character.RadioReceiver?.UpdateBroadcastersInRange();
				myCharacterReplicable.DependenciesDirty = true;
				num++;
			}
		}
		return num;
	}
}
