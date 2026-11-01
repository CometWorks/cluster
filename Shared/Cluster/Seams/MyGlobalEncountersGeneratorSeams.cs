using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using SpaceEngineers.Game.SessionComponents;
using VRage;
using VRage.Utils;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>SpaceEngineers.Game.SessionComponents.MyGlobalEncountersGenerator</c>.
/// The mid-method gate is installed by <c>Shared.Patches.MyGlobalEncountersGeneratorPatch</c> (transpiler);
/// the helpers below are written against the publicized private members instead of the reverse patches the
/// ledger suggested (same effect, no reflection).
/// </summary>
public static class MyGlobalEncountersGeneratorSeams
{
	// ClusterPlugin seam adapter for MyGlobalEncountersGeneratorPatch.UpdateBeforeSimulationPrefix
	public static Func<bool> ClusterUpdatePrefix;

	/// <summary>The gate the transpiler calls: vanilla (true) unless the seam says otherwise.</summary>
	public static bool UpdateGate()
	{
		var prefix = ClusterUpdatePrefix;
		return prefix == null || prefix();
	}

	// ClusterPlugin patch point: MyGlobalEncountersGeneratorPatch.SpawnEncounter
	// Harmony migration: reverse patch on the spawn tail of UpdateBeforeSimulation, or publicized access to
	// GetSpawnPositionNearPlanet, GetSpawnPositionInSpace, SpawnVoxels, SpawnGrids and the sub-encounter helpers.
	// The World Authority decides that an encounter is due, because only it may run a global spawner, but it hosts
	// no clients and owns no partitions: the count gate above reads zero there and the encounter's grids, voxels,
	// GPS and removal timer all have to live on a node that owns a partition. EncounterDirectorService picks the
	// spawn group and the target player cluster-wide and dispatches here, to the node owning that player, where
	// the planets and the player really are and vanilla's own position pickers work unchanged.
	public static bool ClusterSpawnGlobalEncounter(this MyGlobalEncountersGenerator self, string spawnGroupSubtype)
	{
		if (MySession.Static == null || string.IsNullOrEmpty(spawnGroupSubtype))
		{
			return false;
		}
		if (!MyDefinitionManager.Static.TryGetSpawnGroupDefinition(spawnGroupSubtype, out var spawnGroupDefinition) || spawnGroupDefinition == null)
		{
			return false;
		}
		if (!spawnGroupDefinition.TryGetOwnerId(out var ownerId))
		{
			return false;
		}
		if (!MySession.Static.NPCBlockLimits.HasRemainingPCU(spawnGroupDefinition.IsGlobalEncounter))
		{
			return false;
		}
		float randomFloat = MyUtils.GetRandomFloat(0f, 1f);
		Vector3D spawnPosition;
		if (spawnGroupDefinition.GlobalEncounterSettings.PlanetProximitySpawnProbability >= randomFloat)
		{
			if (!self.GetSpawnPositionNearPlanet(spawnGroupDefinition, out spawnPosition))
			{
				return false;
			}
		}
		else if (!self.GetSpawnPositionInSpace(spawnGroupDefinition, out spawnPosition))
		{
			return false;
		}
		long encounterId = MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.GLOBAL_ENCOUNTER);
		self.SpawnVoxels(encounterId, spawnGroupDefinition, spawnPosition);
		self.SpawnGrids(encounterId, spawnGroupDefinition, ownerId, spawnPosition);
		self.GenerateFactionSubEncounters(spawnGroupDefinition, ownerId, spawnPosition, encounterId);
		self.GenerateHostileSubEncounters(spawnGroupDefinition, spawnPosition, encounterId);
		return true;
	}

	// ClusterPlugin patch point: MyGlobalEncountersGeneratorPatch.ActiveEncounters
	// Harmony migration: publicized read of m_activeEncounters and m_encounterComponents.
	// Lets a lane assert that a dispatched encounter materialised on exactly one node.
	public static int ClusterActiveEncounters(this MyGlobalEncountersGenerator self)
	{
		return self.m_encounterComponents.Count;
	}

	// ClusterPlugin patch point: MyGlobalEncountersGeneratorPatch.EncounterGrids
	// Harmony migration: publicized read of m_encounterComponents.
	// Global encounters land on a random sphere around the world center, far from any owned
	// partition; the cluster needs the grid entity ids to prove the landed encounter joined or
	// created a partition on the executing node.
	public static List<(long EncounterId, long EntityId)> ClusterEncounterGrids(this MyGlobalEncountersGenerator self)
	{
		List<(long, long)> grids = new List<(long, long)>();
		foreach (KeyValuePair<long, HashSet<MyGlobalEncounterComponent>> encounter in self.m_encounterComponents)
		{
			foreach (MyGlobalEncounterComponent component in encounter.Value)
			{
				// Voxel asteroids carry encounter components too, but only grids join partitions.
				if (component.Entity is MyCubeGrid)
				{
					grids.Add((encounter.Key, component.Entity.EntityId));
				}
			}
		}
		return grids;
	}
}
