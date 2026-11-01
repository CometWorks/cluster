using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components.Contracts;

namespace Shared.Patches;

/// <summary>
/// MyContractPatch plus the ClusterTrySettle seam.
/// <para>
/// Activate: CanActivate_Internal only reads the balance, so two nodes can both pass it and both debit.
/// The transpiler reserves the deposit authoritatively before any contract state changes; Activate_Internal's
/// unchecked debit is then observed and suppressed by the ambient scope, and every early exit refunds. The
/// scope is kept in a thread-static slot and
/// disposed by a Harmony finalizer, which runs on every exit of Activate (return or exception).
/// </para>
/// <para>
/// Activate also hands the contract to the World Authority's ledger of accepted contracts (ClusterAccept) once the
/// deposit is reserved; a refusal returns false like a refused deposit, and the finalizer withdraws an accepted
/// contract whose activation did not reach the commit.
/// </para>
/// <para>
/// Finish/Fail: the terminal transition settles money and must happen exactly once across every node holding
/// a copy of the contract, so it is arbitrated through ClusterTrySettle at the head of the transition.
/// </para>
/// <para>
/// Share, FailFor and FinalizeCondition change an accepted contract without settling it (ClusterChanged), and
/// AddContract(IMyContract) posts an offer whose mod API callbacks the cluster keeps (ClusterPosted). SE1-0247.
/// </para>
/// </summary>
public static class MyContractPatch
{
	[ThreadStatic] private static IDisposable s_deposit;
	[ThreadStatic] private static long s_accepted;

	private static readonly MethodInfo CanActivateInternal = EconomyIl.Required(AccessTools.Method(typeof(MyContract), "CanActivate_Internal"), "MyContract.CanActivate_Internal");
	private static readonly MethodInfo ActivateInternal = EconomyIl.Required(AccessTools.Method(typeof(MyContract), "Activate_Internal"), "MyContract.Activate_Internal");
	private static readonly MethodInfo SetState = EconomyIl.Required(AccessTools.PropertySetter(typeof(MyContract), nameof(MyContract.State)), "MyContract.State setter");
	private static readonly MethodInfo BeginDepositMethod = EconomyIl.Required(AccessTools.Method(typeof(MyContractPatch), nameof(BeginDeposit)), "MyContractPatch.BeginDeposit");
	private static readonly MethodInfo CommitDepositMethod = EconomyIl.Required(AccessTools.Method(typeof(MyContractPatch), nameof(CommitDeposit)), "MyContractPatch.CommitDeposit");
	private static readonly MethodInfo TrySettleMethod = EconomyIl.Required(AccessTools.Method(typeof(MyContractPatch), nameof(TrySettle)), "MyContractPatch.TrySettle");

	// Transpiler seam: MyContractPatch.Activate
	[HarmonyPatch(typeof(MyContract), nameof(MyContract.Activate))]
	private static class Activate
	{
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
		{
			var code = instructions.ToList();
			// Begin anchor: the unique CanActivate_Internal call followed by "return false".
			int guard = EconomyIl.FindUnique(code, original, "CanActivate_Internal call", i => EconomyIl.Calls(code[i], CanActivateInternal));
			int reject = EconomyIl.FindNext(code, original, "CanActivate_Internal rejection return", guard, 4, c => c.opcode == OpCodes.Ret);
			EconomyIl.Require(EconomyIl.LoadsInt(code[reject - 1], 0), original, "CanActivate_Internal rejection does not return false");
			EconomyIl.InjectGateAfter(code, reject, generator, BeginDepositMethod,
				new[] { CodeInstruction.LoadArgument(0), CodeInstruction.LoadArgument(1) },
				new[] { new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Ret) });
			// Commit anchor: the unique Activate_Internal call.
			int activate = EconomyIl.FindUnique(code, original, "Activate_Internal call", i => EconomyIl.Calls(code[i], ActivateInternal));
			EconomyIl.InsertAfter(code, activate, new[] { new CodeInstruction(OpCodes.Call, CommitDepositMethod) });
			return code;
		}

		// Stands in for a `using` scope: refund on every exit after the reservation.
		[HarmonyFinalizer]
		private static void Finalizer()
		{
			IDisposable scope = s_deposit;
			long accepted = s_accepted;
			s_deposit = null;
			s_accepted = 0;
			scope?.Dispose();
			if (accepted != 0) MyContractSeams.ClusterWithdraw?.Invoke(accepted);
		}
	}

	// ClusterPlugin seam: Finish - arbitrate the terminal transition once, cluster-wide.
	[HarmonyPatch(typeof(MyContract), nameof(MyContract.Finish))]
	private static class Finish
	{
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
			=> InjectSettle(instructions, generator, original, MyContractStateEnum.Finished, "Finish", new CodeInstruction(OpCodes.Ldc_I4_1),
				new[] { new CodeInstruction(OpCodes.Ldc_I4, (int)MyContractResults.Fail_NotPossible), new CodeInstruction(OpCodes.Ret) });
	}

	// ClusterPlugin seam: Fail - see Finish.
	[HarmonyPatch(typeof(MyContract), nameof(MyContract.Fail))]
	private static class Fail
	{
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
			=> InjectSettle(instructions, generator, original, MyContractStateEnum.Failed, "Fail", new CodeInstruction(OpCodes.Ldarg_2),
				new[] { new CodeInstruction(OpCodes.Ret) });
	}

	/// <summary>
	/// Injects "if (!TrySettle(this, kind, punishOwner)) { rejection }" immediately before the unique
	/// <c>State = terminal</c> assignment (ldarg.0; ldc.i4 terminal; call set_State). Fail passes its punishOwner
	/// argument, Finish a constant.
	/// </summary>
	private static IEnumerable<CodeInstruction> InjectSettle(IEnumerable<CodeInstruction> instructions, ILGenerator generator,
		MethodBase original, MyContractStateEnum terminal, string kind, CodeInstruction punishOwner, IList<CodeInstruction> rejection)
	{
		var code = instructions.ToList();
		int set = EconomyIl.FindUnique(code, original, "State setter call", i => EconomyIl.Calls(code[i], SetState));
		EconomyIl.Require(set >= 2 && EconomyIl.LoadsInt(code[set - 1], (int)terminal) && code[set - 2].opcode == OpCodes.Ldarg_0,
			original, "State setter is not preceded by ldarg.0; ldc.i4 " + terminal);
		EconomyIl.InjectGateBefore(code, set - 2, generator, TrySettleMethod,
			new[] { CodeInstruction.LoadArgument(0), new CodeInstruction(OpCodes.Ldstr, kind), punishOwner }, rejection);
		return code;
	}

	/// <summary>Called from the injected IL of Activate. Returns false when the deposit was refused.</summary>
	public static bool BeginDeposit(MyContract self, long playerId)
	{
		var seam = MyContractSeams.ClusterBeginDeposit;
		if (seam == null) return true;
		// Deposit payer: exactly what Activate_Internal reads after Owners.Add(playerId) in both the empty
		// and the non-empty case. Reserving against a different identifier than vanilla debits would make
		// the scope reject the observed settlement.
		long clusterDepositPayer = self.Owners.Count > 0 ? self.Owners[0] : playerId;
		(IDisposable scope, bool approved) = seam(clusterDepositPayer, self.StartingDeposit > 0 ? self.StartingDeposit : 0);
		s_deposit = scope;
		if (!approved) return false;
		// A refused acceptance leaves the reserved deposit to the finalizer, which refunds it.
		var accept = MyContractSeams.ClusterAccept;
		if (accept != null && !accept(self, playerId)) return false;
		s_accepted = self.Id;
		return true;
	}

	/// <summary>Called from the injected IL of Activate, right after Activate_Internal.</summary>
	public static void CommitDeposit()
	{
		s_accepted = 0;
		IDisposable scope = s_deposit;
		if (scope != null) MyContractSeams.ClusterCommitDeposit?.Invoke(scope);
	}

	/// <summary>Called from the injected IL of Finish/Fail. Returns false when another node settled first.</summary>
	public static bool TrySettle(MyContract self, string kind, bool punishOwner)
	{
		var seam = MyContractSeams.ClusterTrySettle;
		return seam == null || seam(self, kind, punishOwner);
	}

	[HarmonyPatch(typeof(MyContract), nameof(MyContract.Share))]
	private static class Share
	{
		[HarmonyPostfix]
		private static void Postfix(MyContract __instance, bool __result)
		{
			if (__result) MyContractSeams.ClusterChanged?.Invoke(__instance);
		}
	}

	// One owner leaves (abandon, or each owner in turn inside Fail, where the contract is no longer Active).
	[HarmonyPatch(typeof(MyContract), "FailFor")]
	private static class FailFor
	{
		[HarmonyPostfix]
		private static void Postfix(MyContract __instance, bool __result)
		{
			if (__result) MyContractSeams.ClusterChanged?.Invoke(__instance);
		}
	}

	[HarmonyPatch(typeof(MyContractCondition), nameof(MyContractCondition.FinalizeCondition))]
	private static class FinalizeCondition
	{
		[HarmonyPostfix]
		private static void Postfix(MyContractCondition __instance, bool __result)
		{
			var seam = MyContractSeams.ClusterChanged;
			if (!__result || seam == null) return;
			MyContract contract = Sandbox.Game.World.MySession.Static?.GetComponent<MySessionComponentContractSystem>()?.GetActiveContractById(__instance.ContractId);
			if (contract != null) seam(contract);
		}
	}

	// Vanilla deletes the contract's GPS entries in a loop that ends when the local collection has none left;
	// a regular node only forwards the delete to the World Authority, so the loop never ended (SE1-0247).
	[HarmonyPatch(typeof(MyContract), "RemoveAllContractGpsForPlayer")]
	private static class RemoveContractGps
	{
		[HarmonyPrefix]
		private static bool Prefix(MyContract __instance, long identityId)
		{
			var seam = MyContractSeams.ClusterRemoveContractGps;
			return seam == null || !seam(__instance, identityId);
		}
	}

	// The overrides of Update in the contract types that watch grids, triggers or positions. Mod contracts
	// (MyContractCustom) only add the mod's CustomUpdate event and keep running.
	[HarmonyPatch]
	private static class EntityContractUpdate
	{
		private static IEnumerable<MethodBase> TargetMethods() => typeof(MyContract).Assembly.GetTypes()
			.Where(type => type.IsSubclassOf(typeof(MyContract)) && type != typeof(MyContractCustom))
			.Select(type => type.GetMethod(nameof(MyContract.Update), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
				null, new[] { typeof(VRage.Library.Utils.MyTimeSpan) }, null))
			.Where(method => method != null);

		[HarmonyPrefix]
		private static bool Prefix()
		{
			var seam = MyContractSeams.ClusterEntityContractUpdates;
			return seam == null || seam();
		}
	}

	[HarmonyPatch(typeof(MySessionComponentContractSystem), nameof(MySessionComponentContractSystem.AddContract), typeof(IMyContract), typeof(long))]
	private static class AddContract
	{
		[HarmonyPostfix]
		private static void Postfix(MySessionComponentContractSystem __instance, MyAddContractResultWrapper __result)
		{
			if (__result.Success) MyContractSeams.ClusterPosted?.Invoke(__instance, __result.ContractId);
		}
	}
}
