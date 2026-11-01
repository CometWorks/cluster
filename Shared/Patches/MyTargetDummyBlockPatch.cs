using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using SpaceEngineers.ObjectBuilders.ObjectBuilders;
using VRage.Game;

namespace Shared.Patches;

/// <summary>
/// SE1-0225: a target dummy's saved state survives serialization before its first live frame.
///
/// MyTargetDummyBlock.Init parks the saved IsDestroyed, TimeToRestore and SubpartState in
/// LoadIsDestroyed, LoadTimeToRestore and LoadSubpartState, and only UpdateOnceBeforeFrame applies
/// them, once the block is working. GetObjectBuilderCubeBlock writes IsDestroyed and TimeToRestore
/// only after the subparts are initialized, so a block serialized in between writes IsDestroyed as
/// nil and TimeToRestore as 0. A handover import is exactly that block: FrozenUpdatePatch holds its
/// UpdateOnceBeforeFrame until the thaw, and the §6.10 gate re-serializes it before that. Every
/// partition holding a dummy failed the gate (IsDestroyed false against nil), and an offline save
/// of a just-imported partition would have dropped the state.
///
/// The postfix writes the pending loaded values while the subparts do not exist yet, which is what
/// the block will apply on its first working frame. It also writes an empty SubpartState as absent:
/// Init turns an absent list into an empty one, so the two are the same state, and without this a
/// destroyed dummy whose list was already consumed (absent) comes back from an import as empty.
/// </summary>
[HarmonyPatch(typeof(MyTargetDummyBlock), nameof(MyTargetDummyBlock.GetObjectBuilderCubeBlock))]
public static class MyTargetDummyBlockPatch
{
	[HarmonyPostfix]
	private static void Postfix(MyTargetDummyBlock __instance, MyObjectBuilder_CubeBlock __result)
	{
		if (!ClusterRuntime.IsClustered || __result is not MyObjectBuilder_TargetDummyBlock builder)
		{
			return;
		}
		if (!__instance.m_areSubpartsInitialized && __instance.LoadIsDestroyed.HasValue)
		{
			builder.IsDestroyed = __instance.LoadIsDestroyed;
			builder.TimeToRestore = __instance.LoadTimeToRestore;
		}
		if (builder.SubpartState?.Count == 0)
		{
			builder.SubpartState = null;
		}
	}
}
