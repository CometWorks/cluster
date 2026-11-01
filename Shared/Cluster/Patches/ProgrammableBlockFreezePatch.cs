using System;
using Sandbox.Game.Entities.Blocks;

namespace ClusterPlugin;

public static class ProgrammableBlockFreezePatch
{
	public static Func<long, bool> IsFrozen;

	public static bool Prefix(MyProgrammableBlock __instance, ref MyProgrammableBlock.ScriptTerminationReason __result, ref string response)
	{
		if (IsFrozen?.Invoke(__instance.CubeGrid.EntityId) != true) return true;
		response = "Programmable block execution paused during cluster transfer.";
		__result = MyProgrammableBlock.ScriptTerminationReason.None;
		return false;
	}
}
