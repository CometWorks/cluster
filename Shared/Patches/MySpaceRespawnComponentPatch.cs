using System;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SpaceEngineers.Game.World;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// Raises <see cref="MySpaceRespawnComponentSeams.ClusterCooldownChanged"/> for a respawn-ship spawn:
/// <c>SpawnAtShip</c> calls <c>ResetRespawnCooldown(player.Id)</c>, and a postfix reports that reset once the spawn has run (every path past the reset, including the
/// missing-definition early return, still carries the reset cooldown). Inert while the seam is null.
/// </summary>
[HarmonyPatch(typeof(MySpaceRespawnComponent), nameof(MySpaceRespawnComponent.SpawnAtShip), new[]
{
	typeof(MyPlayer), typeof(string), typeof(MyBotDefinition), typeof(string), typeof(Color?), typeof(long?)
})]
public static class MySpaceRespawnComponentPatch
{
	[HarmonyPostfix]
	private static void SpawnAtShipPostfix(MyPlayer player)
	{
		Action<MyPlayer.PlayerId> seam = MySpaceRespawnComponentSeams.ClusterCooldownChanged;
		if (seam == null || !Sync.IsServer || player == null) return;
		seam(player.Id);
	}
}
