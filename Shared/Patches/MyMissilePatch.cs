using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using Shared.Tools;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// ProjectileRangePatch, missile half: cap clustered missile
/// trajectories at the configured sync distance and destroy a range-capped missile without an
/// endpoint explosion. Null delegates leave every vanilla value and branch unchanged.
/// </summary>
[HarmonyPatch(typeof(MyMissile))]
public static class MyMissilePatch
{
    /// <summary>
    /// Transpiler call site: true when the missile expired by trajectory and the cap (not the
    /// definition's own range) is what expired it. Recomputes the same distance test the vanilla
    /// branch used: <c>vector3D</c> there is the position just stored into m_previousPosition.
    /// </summary>
    public static bool SuppressCappedExplosion(MyMissile missile)
    {
        var suppress = MyAmmoDefinitionSeams.ClusterSuppressCappedExplosion;
        if (suppress == null)
            return false;
        var maxTrajectory = missile.m_maxTrajectory;
        var expired = Vector3D.DistanceSquared(missile.m_previousPosition, missile.m_origin) >= (double)(maxTrajectory * maxTrajectory);
        return suppress(missile.m_missileAmmoDefinition, maxTrajectory, expired);
    }

    /// <summary>Clamp inserted immediately after <c>m_maxTrajectory = m_missileAmmoDefinition.MaxTrajectory</c>.</summary>
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MyMissile.Init), new[] { typeof(MyObjectBuilder_EntityBase) })]
    private static IEnumerable<CodeInstruction> InitTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var maxTrajectory = AccessTools.Field(typeof(MyMissile), "m_maxTrajectory")
                            ?? throw new CodeInstructionNotFound("MyMissile.m_maxTrajectory not found");
        var ammoDefinition = AccessTools.Field(typeof(MyMissile), "m_missileAmmoDefinition")
                             ?? throw new CodeInstructionNotFound("MyMissile.m_missileAmmoDefinition not found");
        var clamp = AccessTools.Method(typeof(MyProjectilePatch), nameof(MyProjectilePatch.ClampTrajectory));

        var stores = il.FindAllIndex(ci => ci.StoresField(maxTrajectory));
        if (stores.Count != 1)
            throw new CodeInstructionNotFound($"{original.Name}: expected exactly one m_maxTrajectory store, found {stores.Count}");

        il.InsertRange(stores[0] + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, ammoDefinition),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, maxTrajectory),
            new CodeInstruction(OpCodes.Call, clamp),
            new CodeInstruction(OpCodes.Stfld, maxTrajectory),
        });
        return il;
    }

    /// <summary>
    /// In the distance/health expiration branch, before <c>m_explosionType = MISSILE_EXPLOSION;
    /// MarkForExplosion();</c>: when the cap suppresses the explosion, MarkForDestroy and return.
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MyMissile.UpdateBeforeSimulation))]
    private static IEnumerable<CodeInstruction> UpdateBeforeSimulationTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var il = instructions.ToList();
        var explosionType = AccessTools.Field(typeof(MyMissile), "m_explosionType")
                            ?? throw new CodeInstructionNotFound("MyMissile.m_explosionType not found");
        var markForExplosion = AccessTools.Method(typeof(MyMissile), "MarkForExplosion")
                               ?? throw new CodeInstructionNotFound("MyMissile.MarkForExplosion not found");
        var markForDestroy = AccessTools.Method(typeof(MyMissile), nameof(MyMissile.MarkForDestroy));
        var suppress = AccessTools.Method(typeof(MyMissilePatch), nameof(SuppressCappedExplosion));

        if (il.Any(ci => ci.blocks.Count > 0))
            throw new CodeInstructionNotFound($"{original.Name}: unexpected exception blocks; the emitted `ret` needs a plain body");
        var stores = il.FindAllIndex(ci => ci.StoresField(explosionType));
        if (stores.Count != 1)
            throw new CodeInstructionNotFound($"{original.Name}: expected exactly one m_explosionType store, found {stores.Count}");
        var store = stores[0];
        var at = store - 2;
        if (at < 0 || il[at].opcode != OpCodes.Ldarg_0 || !il[at + 1].LoadsConstant((int)MyExplosionTypeEnum.MISSILE_EXPLOSION))
            throw new CodeInstructionNotFound($"{original.Name}: m_explosionType store is not `m_explosionType = MISSILE_EXPLOSION`");
        if (store + 3 >= il.Count || il[store + 1].opcode != OpCodes.Ldarg_0 || !il[store + 3].Calls(markForExplosion))
            throw new CodeInstructionNotFound($"{original.Name}: m_explosionType store is not followed by MarkForExplosion()");

        var resume = generator.DefineLabel();
        var guard = new List<CodeInstruction>
        {
            new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(il[at]),
            new CodeInstruction(OpCodes.Call, suppress),
            new CodeInstruction(OpCodes.Brfalse, resume),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Callvirt, markForDestroy),
            new CodeInstruction(OpCodes.Ret),
        };
        il[at].labels.Add(resume);
        il.InsertRange(at, guard);
        return il;
    }
}
