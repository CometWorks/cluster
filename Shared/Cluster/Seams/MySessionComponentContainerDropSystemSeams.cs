using System;
using System.Linq;
using Sandbox.Game.SessionComponents;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem</c>.
/// The prefix delegate is installed by <c>Shared.Patches.MySessionComponentContainerDropSystemPatch</c>;
/// the spawn helper is written against the publicized private members instead of the reverse patch the
/// ledger suggested (same effect, no reflection: <c>MyPlayerContainerData</c> is a private nested class,
/// <c>m_playerData</c> and <c>SpawnContainerDropInternal</c> are private).
/// </summary>
public static class MySessionComponentContainerDropSystemSeams
{
	// ClusterPlugin seam adapter for MySessionComponentContainerDropSystemPatch.UpdateContainerSpawnerPrefix
	public static Func<bool> ClusterSpawnerPrefix;

	// ClusterPlugin patch point: MySessionComponentContainerDropSystemPatch.ExecuteDrop
	// Harmony migration: reverse patch on SpawnContainerDropInternal, or a publicized call to it.
	// The World Authority decides which player is due a drop, but it hosts no clients and owns no partitions, so
	// it cannot run the spawn: the grid, its MyContainerDropComponent, its GPS and its removal timer all have to
	// live on the node that owns the player. DropContainerDirectorService dispatches the decision here, to that
	// node, where the player really is online and the vanilla base-position lookup resolves normally.
	public static bool ClusterSpawnContainerDrop(this MySessionComponentContainerDropSystem self, long identityId, bool competitive)
	{
		var playerData = self.m_playerData.FirstOrDefault(entry => entry.PlayerId == identityId);
		if (playerData == null)
		{
			playerData = new MySessionComponentContainerDropSystem.MyPlayerContainerData(identityId, 0, active: true, competitive, 0L);
			self.m_playerData.Add(playerData);
		}
		ApplyLifetime(self.m_definition);
		return self.SpawnContainerDropInternal(playerData, competitive, out _, !competitive);
	}

	private static (int Personal, int Competitive)? s_vanillaLifetime;

	/// <summary>
	/// SE-1146: drops.lifetimeSeconds replaces the definition's drop lifetime before a spawn, which vanilla reads when the
	/// prefab is in (both kinds); 0 puts vanilla's back.
	/// </summary>
	private static void ApplyLifetime(VRage.Game.Definitions.SessionComponents.MyContainerDropSystemDefinition definition)
	{
		if (definition == null) return;
		s_vanillaLifetime ??= (definition.PersonalContainerGridTimeOut, definition.CompetetiveContainerGridTimeOut);
		int seconds = ClusterConfig.GetInt("drops.lifetimeSeconds");
		definition.PersonalContainerGridTimeOut = seconds > 0 ? seconds : s_vanillaLifetime.Value.Personal;
		definition.CompetetiveContainerGridTimeOut = seconds > 0 ? seconds : s_vanillaLifetime.Value.Competitive;
	}
}
