using System;
using Sandbox.Definitions;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// WA-3: wires the vanilla cargo-ship spawner into the cluster. The SpawnCargoShip global event ticks only on the
/// World Authority (MyGlobalEvents is an authoritative system), where vanilla's random-online-player pick always
/// comes up empty - so a cluster never spawned a cargo ship. The WA rides vanilla's own event cadence and spawn
/// group pick, and only the target choice and execution move: the target comes from the position registry, and
/// the node owning that player runs vanilla's placement and prefab spawn where the player really is.
/// </summary>
public static class MyNeutralShipSpawnerPatch
{
	public static void Install() => MyNeutralShipSpawnerSeams.ClusterSpawnHandoff = SpawnHandoff;

	private static bool SpawnHandoff(MyGlobalEventBase senderEvent, MySpawnGroupDefinition spawnGroup)
	{
		// Regular nodes execute dispatched spawns through vanilla's own body (with the pinned target).
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority) return false;
		ClusterPlayerPosition target = SpawnDirectorService.SelectTarget(player => player.ClientId != 0 && player.EntityId != 0);
		if (target == null)
		{
			Reschedule(senderEvent);
			return true;
		}
		var request = new ClusterEncounterRequest(Guid.NewGuid(), null, spawnGroup.Id.SubtypeName, target.ClientId, ClusterEncounterKinds.Cargo);
		// SE-1228: the dispatch waits on the target's node, so it runs off the WA's game thread.
		EncounterDirectorService.Lane.Run(() =>
		{
			try { EncounterDirectorService.Dispatch(request); }
			catch (Exception exception)
			{
				// A player between partitions has no route yet; vanilla's own retry cadence covers it.
				Console.WriteLine("[ClusterPlugin] cargo dispatch failed client=" + target.ClientId
					+ " error=" + SecretRedactor.Redact(exception.Message));
				Sandbox.MySandboxGame.Static?.Invoke(() => Reschedule(senderEvent), "ClusterPlugin cargo reschedule");
			}
		});
		return true;
	}

	private static void Reschedule(MyGlobalEventBase senderEvent)
	{
		if (senderEvent != null)
			MyGlobalEvents.RescheduleEvent(senderEvent, MyNeutralShipSpawner.NEUTRAL_SHIP_RESCHEDULE_TIME);
	}
}
