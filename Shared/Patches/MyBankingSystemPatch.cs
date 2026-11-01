using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.GameSystems.BankingAndCurrency;

namespace Shared.Patches;

/// <summary>
/// Three banking prefixes plus the Transfer_Internal seam. All decisions live in
/// ClusterPlugin.MyBankingSystemPatch behind the MyBankingSystemSeams delegates; a null delegate runs vanilla.
/// </summary>
[HarmonyPatch(typeof(MyBankingSystem))]
public static class MyBankingSystemPatch
{
	private static readonly MethodInfo TransferSeam = EconomyIl.Required(
		AccessTools.Method(typeof(MyBankingSystemPatch), nameof(ClusterTransfer)), "MyBankingSystemPatch.ClusterTransfer");

	// ClusterPlugin patch point: MyBankingSystemPatch.CreateAccountPrefix
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyBankingSystem.CreateAccount), new[] { typeof(long), typeof(long) })]
	private static bool CreateAccountPrefix(MyBankingSystem __instance, long ownerIdentifier, long startingBalance)
		=> MyBankingSystemSeams.ClusterCreateAccount?.Invoke(__instance, ownerIdentifier, startingBalance) != true;

	// ClusterPlugin patch point: MyBankingSystemPatch.RemoveAccountPrefix
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyBankingSystem.RemoveAccount), new[] { typeof(long) })]
	private static bool RemoveAccountPrefix(MyBankingSystem __instance, long ownerIdentifier, ref bool __result)
	{
		bool? clusterResult = MyBankingSystemSeams.ClusterRemoveAccount?.Invoke(__instance, ownerIdentifier);
		if (!clusterResult.HasValue) return true;
		__result = clusterResult.Value;
		return false;
	}

	// ClusterPlugin patch point: MyBankingSystemPatch.ChangeBalancePrefix
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyBankingSystem.ChangeBalance), new[] { typeof(long), typeof(long) })]
	private static bool ChangeBalancePrefix(long identifierId, long amount, ref bool __result)
	{
		bool? clusterResult = MyBankingSystemSeams.ClusterChangeBalance?.Invoke(identifierId, amount);
		if (!clusterResult.HasValue) return true;
		__result = clusterResult.Value;
		return false;
	}

	/// <summary>
	/// ClusterPlugin seam in Transfer_Internal: after both account lookups succeeded and before vanilla's
	/// balance check, hand the pair to the authority. Injected:
	/// <code>
	/// bool? t = ClusterTransfer?.Invoke(from, to, amount);
	/// if (t.HasValue) { if (!t.Value) return false; fromAccount = value; ToAccount = value2; return true; }
	/// </code>
	/// Anchors: exactly two Dictionary&lt;long, MyAccount&gt;.TryGetValue calls (their ldloca operands are the
	/// two MyAccount locals) and the "return false" that follows the second one.
	/// </summary>
	[HarmonyTranspiler]
	[HarmonyPatch("Transfer_Internal")]
	private static IEnumerable<CodeInstruction> TransferInternalTranspiler(IEnumerable<CodeInstruction> instructions,
		ILGenerator generator, MethodBase original)
	{
		var code = instructions.ToList();
		List<int> lookups = EconomyIl.FindExactly(code, original, "Dictionary<long, MyAccount>.TryGetValue", 2, c =>
			c.opcode == OpCodes.Callvirt && c.operand is MethodInfo m && m.Name == "TryGetValue"
			&& m.DeclaringType is { IsGenericType: true } dictionary
			&& dictionary.GetGenericTypeDefinition() == typeof(Dictionary<,>)
			&& dictionary.GetGenericArguments()[0] == typeof(long)
			&& dictionary.GetGenericArguments()[1] == typeof(MyAccount));
		CodeInstruction loadFrom = EconomyIl.LoadOfAddress(code[lookups[0] - 1], original, "source account local");
		CodeInstruction loadTo = EconomyIl.LoadOfAddress(code[lookups[1] - 1], original, "target account local");
		int reject = EconomyIl.FindNext(code, original, "target-account-missing return", lookups[1], 12, c => c.opcode == OpCodes.Ret);
		EconomyIl.Require(EconomyIl.LoadsInt(code[reject - 1], 0), original, "target-account-missing return does not return false");

		LocalBuilder result = generator.DeclareLocal(typeof(bool));
		Label resume = generator.DefineLabel();
		var injected = new List<CodeInstruction>
		{
			CodeInstruction.LoadArgument(1),   // fromIdentifier
			CodeInstruction.LoadArgument(2),   // toIdentifier
			CodeInstruction.LoadArgument(3),   // amount
			loadFrom,                          // MyAccount value
			loadTo,                            // MyAccount value2
			CodeInstruction.LoadArgument(5),   // out MyAccount fromAccount (managed pointer)
			CodeInstruction.LoadArgument(6),   // out MyAccount ToAccount
			new CodeInstruction(OpCodes.Ldloca_S, result),
			new CodeInstruction(OpCodes.Call, TransferSeam),
			new CodeInstruction(OpCodes.Brfalse, resume),
			new CodeInstruction(OpCodes.Ldloc_S, result),
			new CodeInstruction(OpCodes.Ret)
		};
		EconomyIl.InsertAfter(code, reject, injected);
		code[reject + 1 + injected.Count].labels.Add(resume);
		return code;
	}

	/// <summary>Called from the injected IL. Returns true when the seam handled the transfer.</summary>
	public static bool ClusterTransfer(long fromIdentifier, long toIdentifier, long amount, MyAccount value, MyAccount value2,
		ref MyAccount fromAccount, ref MyAccount toAccount, ref bool result)
	{
		bool? clusterTransfer = MyBankingSystemSeams.ClusterTransfer?.Invoke(fromIdentifier, toIdentifier, amount);
		if (!clusterTransfer.HasValue) return false;
		if (!clusterTransfer.Value)
		{
			result = false;
			return true;
		}
		fromAccount = value;
		toAccount = value2;
		result = true;
		return true;
	}
}
