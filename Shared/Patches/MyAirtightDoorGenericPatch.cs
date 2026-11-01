using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Shared.Tools;
using VRage.ModAPI;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// MyAirtightDoorGenericConstraintGuard. MyPhysicsBody.AddConstraint
/// silently declines a constraint whose bodies are not in a Havok world, but CreateConstraints then
/// calls HkConstraint.SetVirtualMassInverse unconditionally - a P/Invoke against the native runtime
/// that only HavokWorld.AddConstraint creates. The result is a null dereference in native code, not a
/// managed exception (SIGSEGV, dump ~/cluster-runs/dumps/node-1.dmp). Vanilla never reaches it: a
/// grid's blocks do not tick before its physics is in the world. A handover import creates the grid
/// frozen while the door block's BEFORE_NEXT_FRAME flag still fires. Suppressing the native call alone
/// would leave the door holding a constraint that was never added, so the guard re-arms
/// BEFORE_NEXT_FRAME and RecreateConstraints builds a real one once the grid's physics is live.
/// </summary>
[HarmonyPatch(typeof(MyAirtightDoorGeneric), "CreateConstraints")]
public static class MyAirtightDoorGenericPatch
{
    /// <summary>Transpiler call site replacing the unconditional SetVirtualMassInverse.</summary>
    public static void SetVirtualMassInverseGuarded(HkConstraint constraint, MyAirtightDoorGeneric door)
    {
        if (!ClusterRuntime.IsClustered || MyPhysicsBody.IsConstraintValid(constraint))
            constraint.SetVirtualMassInverse(Vector4.Zero, Vector4.One);
        else
            door.NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> CreateConstraintsTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var setVirtualMassInverse = AccessTools.Method(typeof(HkConstraint), nameof(HkConstraint.SetVirtualMassInverse));
        var addConstraint = AccessTools.Method(typeof(MyPhysicsBody), nameof(MyPhysicsBody.AddConstraint), new[] { typeof(HkConstraint) });
        var guarded = AccessTools.Method(typeof(MyAirtightDoorGenericPatch), nameof(SetVirtualMassInverseGuarded));

        // The pairing is the anchor: AddConstraint(constraint); constraint.SetVirtualMassInverse(Zero, One).
        var calls = il.FindAllIndex(ci => ci.Calls(setVirtualMassInverse));
        if (calls.Count != 1)
            throw new CodeInstructionNotFound($"{original.Name}: expected exactly one SetVirtualMassInverse call, found {calls.Count}");
        var call = calls[0];
        if (call < 4
            || !IsVector4Constant(il[call - 1]) || !IsVector4Constant(il[call - 2])
            || !il[call - 3].IsLdloc()
            || !il[call - 4].Calls(addConstraint))
            throw new CodeInstructionNotFound($"{original.Name}: SetVirtualMassInverse is not paired with AddConstraint on the same constraint local");
        if (il[call - 1].labels.Count > 0 || il[call].labels.Count > 0)
            throw new CodeInstructionNotFound($"{original.Name}: unexpected branch target inside the SetVirtualMassInverse argument sequence");

        // Keep `ldloc constraint` (and any labels on the two loads that follow it), replace the two
        // Vector4 loads and the native call with `ldarg.0; call SetVirtualMassInverseGuarded`.
        var first = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(il[call - 2]);
        il.RemoveRange(call - 2, 3);
        il.InsertRange(call - 2, new[]
        {
            first,
            new CodeInstruction(OpCodes.Call, guarded),
        });
        return il;
    }

    private static bool IsVector4Constant(CodeInstruction ci)
    {
        return ci.opcode == OpCodes.Ldsfld && ci.operand is FieldInfo field && field.DeclaringType == typeof(Vector4);
    }
}
