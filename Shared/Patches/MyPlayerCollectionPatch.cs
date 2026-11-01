using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// Game-side injection points of the MyPlayerCollection seams. Every prefix returns true / changes nothing
/// while its seam delegate is null, so a standalone server runs vanilla byte for byte. The two transpilers
/// only add a call whose target is inert without a seam.
/// </summary>
[HarmonyPatch(typeof(MyPlayerCollection))]
public static class MyPlayerCollectionPatch
{
	// RespawnRequestPrefix: route real exact-medbay requests through WA arbitration before vanilla runs.
	[HarmonyPrefix]
	[HarmonyPatch("OnRespawnRequest", new[] { typeof(MyPlayerCollection.RespawnMsg) })]
	private static bool RespawnRequestPrefix(MyPlayerCollection.RespawnMsg msg)
	{
		Func<MyPlayerCollection.RespawnMsg, bool> seam = MyPlayerCollectionSeams.ClusterRespawnRequestPrefix;
		return seam == null || seam(msg);
	}

	// BeforeNewPlayerRequest: install the WA-issued binding on the game thread after vanilla's sender-spoof
	// guard and before its identity lookup. A prefix is insufficient: it would run before the spoof
	// validation and could consume another user's pending binding.
	[HarmonyTranspiler]
	[HarmonyPatch("OnNewPlayerRequest", new[] { typeof(MyPlayerCollection.NewPlayerRequestParameters) })]
	private static IEnumerable<CodeInstruction> OnNewPlayerRequestTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
	{
		const string method = "MyPlayerCollection.OnNewPlayerRequest";
		List<CodeInstruction> code = instructions.ToList();
		MethodInfo validationFailed = AccessTools.Method(typeof(MyMultiplayerServerBase), nameof(MyMultiplayerServerBase.ValidationFailed));
		ConstructorInfo playerIdCtor = AccessTools.Constructor(typeof(MyPlayer.PlayerId), new[] { typeof(ulong), typeof(int) });
		MethodInfo gate = AccessTools.Method(typeof(MyPlayerCollectionSeams), nameof(MyPlayerCollectionSeams.BeforeNewPlayerRequestOrFail));

		// The spoof guard is the only ValidationFailed call; it ends at the first ret after it.
		int failed = PlayersTranspilerHelpers.IndexOfSingle(code, c => PlayersTranspilerHelpers.CallsMethod(c, validationFailed),
			"MyMultiplayerServerBase.ValidationFailed call", method);
		int guardEnd = -1;
		for (int i = failed + 1; i < code.Count; i++)
			if (code[i].opcode == OpCodes.Ret) { guardEnd = i; break; }
		if (guardEnd < 0 || guardEnd + 1 >= code.Count)
			throw new InvalidOperationException($"{method}: no ret after the spoof guard; the game code changed");
		// The statement following the guard must be the PlayerId construction. PlayerId is a struct, so
		// `PlayerId playerId = new PlayerId(steamId, serialId)` compiles to `ldloca.s 0; ldarg.0; ldfld ClientSteamId;
		// ldarg.0; ldfld PlayerSerialId; call PlayerId::.ctor(uint64, int32)` - the constructor is the first call
		// after the guard, invoked with `call` on the local's address, never `newobj`. Accept either encoding,
		// and require exactly one up to and including the first call-like instruction.
		int ctors = 0;
		for (int i = guardEnd + 1; i < code.Count; i++)
		{
			bool callLike = PlayersTranspilerHelpers.IsCall(code[i]) || code[i].opcode == OpCodes.Newobj;
			if (callLike && Equals(code[i].operand, playerIdCtor)) ctors++;
			if (callLike) break;
		}
		if (ctors != 1)
			throw new InvalidOperationException($"{method}: expected one MyPlayer.PlayerId construction after the spoof guard, found {ctors}");

		CodeInstruction target = code[guardEnd + 1];
		Label resume = generator.DefineLabel();
		CodeInstruction load = new CodeInstruction(OpCodes.Ldarg_0);
		load.labels.AddRange(target.labels);   // the guard's branch now lands on the seam call
		target.labels.Clear();
		target.labels.Add(resume);
		code.InsertRange(guardEnd + 1, new[]
		{
			load,
			new CodeInstruction(OpCodes.Call, gate),
			new CodeInstruction(OpCodes.Brtrue, resume),
			new CodeInstruction(OpCodes.Ret),
		});
		return code;
	}

	// InitialPlayerIdentityIsNew: replace the value stored into PlayerDataMsg.NewIdentity in the initial-player
	// branch of CreateNewPlayer, so the joining client materializes the identity from the same message.
	[HarmonyTranspiler]
	[HarmonyPatch(nameof(MyPlayerCollection.CreateNewPlayer), new[]
	{
		typeof(MyIdentity), typeof(MyPlayer.PlayerId), typeof(string), typeof(bool), typeof(bool), typeof(bool),
		typeof(bool), typeof(string), typeof(string)
	})]
	private static IEnumerable<CodeInstruction> CreateNewPlayerTranspiler(IEnumerable<CodeInstruction> instructions)
	{
		const string method = "MyPlayerCollection.CreateNewPlayer";
		List<CodeInstruction> code = instructions.ToList();
		FieldInfo newIdentity = AccessTools.Field(typeof(PlayerDataMsg), nameof(PlayerDataMsg.NewIdentity));
		MethodInfo helper = AccessTools.Method(typeof(MyPlayerCollectionSeams), nameof(MyPlayerCollectionSeams.InitialPlayerIdentityIsNew));
		int store = PlayersTranspilerHelpers.IndexOfSingle(code, c => c.opcode == OpCodes.Stfld && Equals(c.operand, newIdentity),
			"PlayerDataMsg.NewIdentity store", method);
		// stack: [&msg, newIdentity] -> [&msg, newIdentity, id] -> helper -> [&msg, bool] -> stfld
		code.InsertRange(store, new[]
		{
			new CodeInstruction(OpCodes.Ldarg_2),
			new CodeInstruction(OpCodes.Call, helper),
		});
		return code;
	}

	// TryGetPlayerIdPrefix: expose the WA player-binding replica to vanilla lookups. Lock-free on the seam side;
	// the engine calls this from its parallel entity update.
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyPlayerCollection.TryGetPlayerId), new[] { typeof(long), typeof(MyPlayer.PlayerId) },
		new[] { ArgumentType.Normal, ArgumentType.Out })]
	private static bool TryGetPlayerIdPrefix(MyPlayerCollection __instance, long identityId, ref MyPlayer.PlayerId result, ref bool __result)
	{
		MyPlayerCollectionSeams.ClusterTryGetPlayerIdDelegate seam = MyPlayerCollectionSeams.ClusterTryGetPlayerId;
		if (seam == null) return true;
		result = default;
		bool found = false;
		if (seam(__instance, identityId, ref result, ref found)) return true;
		__result = found;
		return false;
	}

	// TryGetIdentityIdPrefix: resolve the WA-issued binding before local player creation materializes it.
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyPlayerCollection.TryGetIdentityId), new[] { typeof(ulong), typeof(int) })]
	private static bool TryGetIdentityIdPrefix(MyPlayerCollection __instance, ulong steamId, int serialId, ref long __result)
	{
		MyPlayerCollectionSeams.ClusterTryGetIdentityIdDelegate seam = MyPlayerCollectionSeams.ClusterTryGetIdentityId;
		if (seam == null) return true;
		long value = 0L;
		if (seam(__instance, steamId, serialId, ref value)) return true;
		__result = value;
		return false;
	}

	// LoadPlayerPrefix: rebuild the canonical binding ledger from checkpoint player rows on a replacement WA.
	[HarmonyPrefix]
	[HarmonyPatch("LoadPlayerInternal", new[] { typeof(MyPlayer.PlayerId), typeof(MyObjectBuilder_Player), typeof(bool) },
		new[] { ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal })]
	private static void LoadPlayerPrefix(MyPlayerCollection __instance, ref MyPlayer.PlayerId playerId, MyObjectBuilder_Player playerOb, bool obsolete)
	{
		MyPlayerCollectionSeams.ClusterLoadPlayer?.Invoke(__instance, ref playerId, playerOb, obsolete);
	}

	// CreateNewPlayerPrefix: reconcile vanilla's private player<->identity maps to the canonical WA binding.
	// The two private maps are injected by Harmony, which fails the install if either field is absent.
	[HarmonyPrefix]
	[HarmonyPatch("CreateNewPlayerInternal", new[] { typeof(MyNetworkClient), typeof(MyPlayer.PlayerId), typeof(MyObjectBuilder_Player) })]
	private static void CreateNewPlayerPrefix(MyPlayer.PlayerId playerId, MyObjectBuilder_Player playerBuilder,
		ConcurrentDictionary<MyPlayer.PlayerId, long> ___m_playerIdentityIds, Dictionary<long, MyPlayer.PlayerId> ___m_identityPlayerIds)
	{
		MyPlayerCollectionSeams.ClusterCreatePlayer?.Invoke(playerId, playerBuilder, ___m_playerIdentityIds, ___m_identityPlayerIds);
	}

	// CreateIdentityPrefix: reserve every fresh serial player identity on the WA.
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyPlayerCollection.CreateNewIdentity), new[] { typeof(string), typeof(string), typeof(Vector3?), typeof(bool), typeof(bool) })]
	private static bool CreateIdentityPrefix(MyPlayerCollection __instance, string name, string model, Vector3? colorMask,
		bool initialPlayer, bool addToNpcs, ref MyIdentity __result)
	{
		MyIdentity clusterIdentity = MyPlayerCollectionSeams.ClusterCreateIdentity?.Invoke(__instance, name, model, colorMask, initialPlayer, addToNpcs);
		if (clusterIdentity == null) return true;
		__result = clusterIdentity;
		return false;
	}

	// CreateNpcIdentityPrefix: reserve fresh NPC identity ids on the WA; explicit ids stay vanilla (the seam declines).
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyPlayerCollection.CreateNewNpcIdentity), new[] { typeof(string), typeof(long) })]
	private static bool CreateNpcIdentityPrefix(MyPlayerCollection __instance, string name, long identityId, ref MyIdentity __result)
	{
		MyIdentity clusterIdentity = MyPlayerCollectionSeams.ClusterCreateNpcIdentity?.Invoke(__instance, name, identityId);
		if (clusterIdentity == null) return true;
		__result = clusterIdentity;
		return false;
	}

	// AfterCreateIdentityPrefix: keep NPC classification / initial-player suppression when a WA grant is applied.
	[HarmonyPrefix]
	[HarmonyPatch("AfterCreateIdentity", new[] { typeof(MyIdentity), typeof(bool), typeof(bool) })]
	private static void AfterCreateIdentityPrefix(MyIdentity identity, ref bool addToNpcs, ref bool sendToClients)
	{
		MyPlayerCollectionSeams.ClusterAfterCreateIdentity?.Invoke(identity, ref addToNpcs, ref sendToClients);
	}
}
