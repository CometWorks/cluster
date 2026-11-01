using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Weapons;
using Shared.Tools;

namespace Shared.Patches;

/// <summary>
/// ProjectileRangePatch, ballistic half: cap clustered projectile
/// trajectories at the configured sync distance. The clamp is inserted after the optional randomized
/// multiplier and before <c>m_positionChecked = false</c>, i.e. after the final vanilla range
/// calculation and before the ray is built from it. A null delegate leaves the value unchanged.
/// </summary>
[HarmonyPatch(typeof(MyProjectile), nameof(MyProjectile.Start))]
public static class MyProjectilePatch
{
    /// <summary>Transpiler call site shared with MyMissilePatch.</summary>
    public static float ClampTrajectory(MyAmmoDefinition definition, float trajectory)
    {
        var clamp = MyAmmoDefinitionSeams.ClusterClampTrajectory;
        return clamp == null ? trajectory : clamp(definition, trajectory);
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> StartTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var positionChecked = AccessTools.Field(typeof(MyProjectile), "m_positionChecked")
                              ?? throw new CodeInstructionNotFound("MyProjectile.m_positionChecked not found");
        var maxTrajectory = AccessTools.Field(typeof(MyProjectile), "m_maxTrajectory")
                            ?? throw new CodeInstructionNotFound("MyProjectile.m_maxTrajectory not found");
        var clamp = AccessTools.Method(typeof(MyProjectilePatch), nameof(ClampTrajectory));

        var stores = il.FindAllIndex(ci => ci.StoresField(positionChecked));
        if (stores.Count != 1)
            throw new CodeInstructionNotFound($"{original.Name}: expected exactly one m_positionChecked store, found {stores.Count}");
        var at = stores[0] - 2;
        if (at < 0 || il[at].opcode != OpCodes.Ldarg_0 || il[at + 1].opcode != OpCodes.Ldc_I4_0)
            throw new CodeInstructionNotFound($"{original.Name}: m_positionChecked store is not `this.m_positionChecked = false`");

        // ammoDefinition is the second parameter (this, index, ammoDefinition, ...).
        il.InsertRange(at, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(il[at]),
            new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, maxTrajectory),
            new CodeInstruction(OpCodes.Call, clamp),
            new CodeInstruction(OpCodes.Stfld, maxTrajectory),
        });
        return il;
    }
}
