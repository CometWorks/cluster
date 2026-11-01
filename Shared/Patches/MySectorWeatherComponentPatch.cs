using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// MySectorWeatherComponent seams (WeatherAuthorityService; the diff's "ClusterPlugin patch point" comment is
/// the spec). Three injections, all inert while the seams are null:
/// <list type="bullet">
/// <item>UpdatePlanetDataServer (transpiler): role 1 (World Authority, decisions only) skips the per-effect
/// LIGHTNING loop; role 2 (regular node, lightning only) returns before the per-planet weather DECISION loop;
/// role 0 runs vanilla.</item>
/// <item>CreateRandomWeather(MyPlanet, Action&lt;string&gt;, int?) (transpiler): the per-player loop iterates
/// <see cref="MySectorWeatherComponentSeams.WeatherPlayerPositionsOrLocal"/> - cluster-wide positions on the
/// WA, this server's own online players otherwise - instead of Sync.Players.GetOnlinePlayers().</item>
/// <item>CreateLightning (prefix/postfix): maintains the lightning counter (<see cref="MySectorWeatherComponentSeams.CountLightning"/>) - one per lightning vanilla added.</item>
/// </list>
/// </summary>
[HarmonyPatch(typeof(MySectorWeatherComponent))]
public static class MySectorWeatherComponentPatch
{
    // Vanilla:
    //   if (!Sync.IsServer || MySession.Static == null || !Settings.WeatherSystem) return;
    //   for (int i = 0; i < m_weatherPlanetData.Count; i++) { [lightning ticks] }
    //   foreach (var planetData in m_weatherPlanetData) { [weather decisions] }
    // Stock 1.210.014 IL: the guard's three checks each `brfalse` to the method's single, final `ret` (the
    // foreach's try/finally shares it) - there is NO early ret of its own. The lightning for-loop
    // (`ldc.i4.0; stloc.0; br <cond>`) follows the `ldfld WeatherSystem; brfalse` pair directly, and the
    // decision foreach starts with `ldarg.0; ldfld m_weatherPlanetData; callvirt GetEnumerator` outside the try.
    // Intended shape:
    //   int clusterRole = ClusterWeatherRole?.Invoke() ?? 0;
    //   for (int i = 0; clusterRole != 1 && i < ...; i++) { ... }   -> gate A before the for: role 1 jumps to gate B
    //   if (clusterRole == 2) return;                                -> gate B before the foreach
    //   foreach (...) { ... }
    [HarmonyTranspiler]
    [HarmonyPatch("UpdatePlanetDataServer")]
    private static IEnumerable<CodeInstruction> UpdatePlanetDataServerTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var il = instructions.ToList();
        var role = AccessTools.Method(typeof(MySectorWeatherComponentSeams), nameof(MySectorWeatherComponentSeams.WeatherRole));

        // The guard's last check loads Settings.WeatherSystem and branches (brfalse) to the final ret; the
        // lightning for-loop (int i = 0) starts right after that branch. The method's only ret is its last
        // instruction, which is what makes "jump to the final ret" the guard's (and gate B's) return.
        GateTranspilerHelpers.Require(il.FindIndex(ci => ci.opcode == OpCodes.Ret) == il.Count - 1, original,
            "a single ret at the end of the method (the guard branches to it)");
        int weatherSystem = il.IndexOfSingle(original, "MyObjectBuilder_SessionSettings.WeatherSystem load",
            ci => ci.LoadsFieldNamed("WeatherSystem"));
        GateTranspilerHelpers.Require(weatherSystem + 3 < il.Count
            && (il[weatherSystem + 1].opcode == OpCodes.Brfalse || il[weatherSystem + 1].opcode == OpCodes.Brfalse_S),
            original, "the guard's brfalse after the WeatherSystem check");
        int forStart = weatherSystem + 2;
        GateTranspilerHelpers.Require(il[forStart].opcode == OpCodes.Ldc_I4_0
            && il[forStart + 1].TryGetLocalIndex(out _, out bool storesCounter) && storesCounter,
            original, "the lightning for-loop (int i = 0) after the guard");

        // The decision foreach starts with `this.m_weatherPlanetData.GetEnumerator()` on the planet-data list.
        int getEnumerator = il.IndexOfSingle(original, "List<MyObjectBuilder_WeatherPlanetData>.GetEnumerator",
            ci => ci.CallsNamed("GetEnumerator", typeof(List<MyObjectBuilder_WeatherPlanetData>)));
        int foreachStart = getEnumerator - 2;
        GateTranspilerHelpers.Require(foreachStart > forStart
            && il[getEnumerator - 1].LoadsFieldNamed("m_weatherPlanetData")
            && il[foreachStart].IsLdarg(0),
            original, "the decision foreach to start with this.m_weatherPlanetData");

        var ret = il.LabelOnLastRet(generator, original);

        // Gate B (before the foreach): role == 2 -> return.
        var gateB = new CodeInstruction(OpCodes.Call, role).MoveLabelsFrom(il[foreachStart]);
        var gateBLabel = gateB.AddLabel(generator);
        il.InsertRange(foreachStart, new[]
        {
            gateB,
            new CodeInstruction(OpCodes.Ldc_I4_2),
            new CodeInstruction(OpCodes.Beq, ret),
        });

        // Gate A (before the for): role == 1 -> skip the lightning loop, continue at gate B.
        var gateA = new CodeInstruction(OpCodes.Call, role).MoveLabelsFrom(il[forStart]);
        il.InsertRange(forStart, new[]
        {
            gateA,
            new CodeInstruction(OpCodes.Ldc_I4_1),
            new CodeInstruction(OpCodes.Beq, gateBLabel),
        });

        return il;
    }

    // Vanilla:
    //   foreach (MyPlayer onlinePlayer in Sync.Players.GetOnlinePlayers())
    //   {
    //       if (onlinePlayer == null || GetClosestPlanet(onlinePlayer.GetPosition())?.EntityId != planet.EntityId) continue;
    //       ...
    //       Vector3D worldPosition = planet.GetClosestSurfacePointGlobal(onlinePlayer.GetPosition());
    // Intended shape:
    //   foreach (Vector3D clusterPosition in ClusterWeatherPlayerPositions?.Invoke() ?? <local player positions>)
    //   {
    //       if (GetClosestPlanet(clusterPosition)?.EntityId != planet.EntityId) continue;
    //       ...
    //       Vector3D worldPosition = planet.GetClosestSurfacePointGlobal(clusterPosition);
    // The enumerator local (IEnumerator<MyPlayer>) and the element local (MyPlayer) are retyped to Vector3D
    // through two new locals; the null check disappears; the two GetPosition() calls become plain loads.
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MySectorWeatherComponent.CreateRandomWeather), new[] { typeof(MyPlanet), typeof(Action<string>), typeof(int?) })]
    private static IEnumerable<CodeInstruction> CreateRandomWeatherTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var il = instructions.ToList();

        int online = il.IndexOfSingle(original, "MyPlayerCollection.GetOnlinePlayers", ci => ci.CallsNamed("GetOnlinePlayers", typeof(MyPlayerCollection)));
        GateTranspilerHelpers.Require(online > 0 && il[online - 1].CallsNamed("get_Players", typeof(Sync)), original, "Sync.Players before GetOnlinePlayers");
        GateTranspilerHelpers.Require(il[online + 1].CallsNamed("GetEnumerator", typeof(IEnumerable<MyPlayer>)), original, "IEnumerable<MyPlayer>.GetEnumerator after GetOnlinePlayers");
        GateTranspilerHelpers.Require(il[online + 2].TryGetLocalIndex(out int enumeratorLocal, out bool storesEnumerator) && storesEnumerator, original, "the enumerator to be stored in a local");

        int current = il.IndexOfSingle(original, "IEnumerator<MyPlayer>.get_Current", ci => ci.CallsNamed("get_Current", typeof(IEnumerator<MyPlayer>)));
        GateTranspilerHelpers.Require(il[current + 1].TryGetLocalIndex(out int playerLocal, out bool storesPlayer) && storesPlayer, original, "the current player to be stored in a local");

        var positions = generator.DeclareLocal(typeof(IEnumerator<Vector3D>));
        var position = generator.DeclareLocal(typeof(Vector3D));

        il[online - 1].MakeNop();
        il[online].opcode = OpCodes.Call;
        il[online].operand = AccessTools.Method(typeof(MySectorWeatherComponentSeams), nameof(MySectorWeatherComponentSeams.WeatherPlayerPositionsOrLocal));
        il[online + 1].operand = AccessTools.Method(typeof(IEnumerable<Vector3D>), nameof(IEnumerable<Vector3D>.GetEnumerator));
        il[online + 2].opcode = OpCodes.Stloc;
        il[online + 2].operand = positions;
        il[current].operand = AccessTools.PropertyGetter(typeof(IEnumerator<Vector3D>), nameof(IEnumerator<Vector3D>.Current));
        il[current + 1].opcode = OpCodes.Stloc;
        il[current + 1].operand = position;

        int enumeratorLoads = 0;
        int nullChecks = 0;
        int positionReads = 0;
        for (int i = 0; i < il.Count; i++)
        {
            if (i == online + 2 || i == current + 1)
                continue;
            if (!il[i].TryGetLocalIndex(out int local, out bool isStore))
                continue;
            if (local == enumeratorLocal)
            {
                GateTranspilerHelpers.Require(!isStore, original, "the player enumerator to be stored once");
                il[i].opcode = OpCodes.Ldloc;
                il[i].operand = positions;
                enumeratorLoads++;
            }
            else if (local == playerLocal)
            {
                GateTranspilerHelpers.Require(!isStore && i + 1 < il.Count, original, "the player local to be stored once");
                var use = il[i + 1];
                if (use.opcode == OpCodes.Brfalse || use.opcode == OpCodes.Brfalse_S)
                {
                    // `if (onlinePlayer == null) continue;` - a position is never null.
                    il[i].MakeNop();
                    use.MakeNop();
                    nullChecks++;
                }
                else if (use.CallsNamed("GetPosition", typeof(MyPlayer)))
                {
                    il[i].opcode = OpCodes.Ldloc;
                    il[i].operand = position;
                    use.MakeNop();
                    positionReads++;
                }
                else
                {
                    throw new InvalidOperationException($"{GateTranspilerHelpers.Describe(original)}: unexpected use of the player local at {i}: {use}");
                }
            }
        }

        GateTranspilerHelpers.Require(enumeratorLoads >= 3, original, "the enumerator to be read for Current, MoveNext and Dispose");
        GateTranspilerHelpers.Require(nullChecks == 1, original, "exactly one null check of the player");
        GateTranspilerHelpers.Require(positionReads == 2, original, "exactly two GetPosition() reads of the player");

        return il;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MySectorWeatherComponent.CreateLightning))]
    private static void CreateLightningPrefix(MySectorWeatherComponent __instance, out int __state)
    {
        __state = __instance.m_lightnings.Count;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MySectorWeatherComponent.CreateLightning))]
    private static void CreateLightningPostfix(MySectorWeatherComponent __instance, int __state)
    {
        if (__instance.m_lightnings.Count > __state)
            MySectorWeatherComponentSeams.CountLightning(__instance);
    }
}
