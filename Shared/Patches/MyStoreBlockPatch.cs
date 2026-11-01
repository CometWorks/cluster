using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Network;
using VRage.ObjectBuilders;

namespace Shared.Patches;

/// <summary>
/// The physical-item, gas, ATM-withdrawal and prefab
/// settlement paths of MyStoreBlock. Each transpiler reserves the payer debit through the seam immediately
/// before the first inventory mutation, lets all original validation and mutation code run, and commits the
/// payee credit before the vanilla success result. All decisions and state live in
/// ClusterPlugin.MyStoreBlockPatch behind the MyStoreBlockSeams delegates; while they are null every helper
/// returns "approved" without allocating anything and vanilla runs byte for byte.
/// <para>
/// Refund on early return / exception: emitting a try/finally by transpiler would need branch-into-region fixes for the switch-case anchors (SellToPlayer,
/// SellToStation), so the scope is kept in a thread-static slot instead and disposed by a Harmony finalizer,
/// which runs on every exit of the six synchronous targets. The prefab purchase keeps its scope in a
/// ConditionalWeakTable keyed by the compiler-generated closure object, and settles or refunds it from the two spawn callbacks.
/// </para>
/// </summary>
public static class MyStoreBlockPatch
{
	[ThreadStatic] private static IDisposable s_scope;
	private static readonly ConditionalWeakTable<object, IDisposable> DeferredScopes = new ConditionalWeakTable<object, IDisposable>();

	private static readonly MethodInfo ChangeBalanceMethod = EconomyIl.Required(
		AccessTools.Method(typeof(MyBankingSystem), nameof(MyBankingSystem.ChangeBalance), new[] { typeof(long), typeof(long) }), "MyBankingSystem.ChangeBalance");
	private static readonly MethodInfo SendBuyItemResultShort = EconomyIl.Required(
		AccessTools.Method(typeof(MyStoreBlock), "SendBuyItemResult", new[] { typeof(long), typeof(int), typeof(MyStoreBuyItemResults) }), "MyStoreBlock.SendBuyItemResult(long, int, result)");
	private static readonly MethodInfo SendBuyItemResultLong = EconomyIl.Required(
		AccessTools.Method(typeof(MyStoreBlock), "SendBuyItemResult", new[] { typeof(long), typeof(string), typeof(long), typeof(int), typeof(MyStoreBuyItemResults) }), "MyStoreBlock.SendBuyItemResult(long, string, long, int, result)");
	private static readonly MethodInfo SendSellItemResultShort = EconomyIl.Required(
		AccessTools.Method(typeof(MyStoreBlock), "SendSellItemResult", new[] { typeof(long), typeof(int), typeof(MyStoreSellItemResults) }), "MyStoreBlock.SendSellItemResult(long, int, result)");
	private static readonly MethodInfo SendSellItemResultLong = EconomyIl.Required(
		AccessTools.Method(typeof(MyStoreBlock), "SendSellItemResult", new[] { typeof(long), typeof(string), typeof(long), typeof(int), typeof(MyStoreSellItemResults) }), "MyStoreBlock.SendSellItemResult(long, string, long, int, result)");
	private static readonly MethodInfo CanItemsBeAddedMethod = EconomyIl.Required(
		AccessTools.Method(typeof(MyInventory), nameof(MyInventory.CanItemsBeAdded), new[] { typeof(MyFixedPoint), typeof(MyDefinitionId) }), "MyInventory.CanItemsBeAdded");
	private static readonly MethodInfo AddItemsMethod = EconomyIl.Required(
		AccessTools.Method(typeof(MyInventoryBase), nameof(MyInventoryBase.AddItems), new[] { typeof(MyFixedPoint), typeof(MyObjectBuilder_Base) }), "MyInventoryBase.AddItems");
	private static readonly MethodInfo SpawnPrefabMethod = EconomyIl.Required(
		AccessTools.Method(typeof(MyPrefabManager), "SpawnPrefabInternalRandomColor"), "MyPrefabManager.SpawnPrefabInternalRandomColor");

	private static readonly MethodInfo BeginBuyPhysicalMethod = Helper(nameof(BeginBuyPhysical));
	private static readonly MethodInfo BeginBuyGasMethod = Helper(nameof(BeginBuyGas));
	private static readonly MethodInfo BeginSellToPlayerMethod = Helper(nameof(BeginSellToPlayer));
	private static readonly MethodInfo BeginSellToStationMethod = Helper(nameof(BeginSellToStation));
	private static readonly MethodInfo BeginWithdrawalMethod = Helper(nameof(BeginWithdrawal));
	private static readonly MethodInfo BeginBuyPrefabMethod = Helper(nameof(BeginBuyPrefab));
	private static readonly MethodInfo CommitMethod = Helper(nameof(Commit));
	private static readonly MethodInfo SettleDeferredMethod = Helper(nameof(SettleDeferred));

	private static MethodInfo Helper(string name)
		=> EconomyIl.Required(AccessTools.Method(typeof(MyStoreBlockPatch), name), "MyStoreBlockPatch." + name);

	private static readonly CodeInstruction[] Return = { new CodeInstruction(OpCodes.Ret) };

	private static CodeInstruction[] Args(params int[] indices) => indices.Select(index => CodeInstruction.LoadArgument(index)).ToArray();

	private static bool IsChangeBalance(CodeInstruction code) => EconomyIl.Calls(code, ChangeBalanceMethod);

	// ---------------------------------------------------------------------------------------------------------
	// The six synchronous settlement paths: begin/commit transpiler plus the refund finalizer.
	// ---------------------------------------------------------------------------------------------------------

	[HarmonyPatch]
	private static class Synchronous
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			yield return Target("BuyPhysicalItemFromPlayer", typeof(long), typeof(int), typeof(MyPlayer), typeof(MyInventory), typeof(MyStoreItem), typeof(long));
			yield return Target("BuyPhysicalItemInternal", typeof(long), typeof(int), typeof(MyPlayer), typeof(MyFaction), typeof(long), typeof(MyInventory), typeof(MyStoreItem), typeof(long));
			yield return Target("BuyGasInternal", typeof(MyStoreItem), typeof(int), typeof(MyPlayer), typeof(MyFaction), typeof(MyEntity), typeof(long), typeof(MyDefinitionId), typeof(bool));
			yield return Target("SellToPlayer", typeof(long), typeof(int), typeof(long), typeof(MyPlayer));
			yield return Target("SellToStation", typeof(long), typeof(int), typeof(MyPlayer), typeof(IMyFactionStation), typeof(long), typeof(double), typeof(bool));
			yield return Target("ChangeBalance", typeof(int), typeof(long));
		}

		private static MethodBase Target(string name, params Type[] parameters)
			=> EconomyIl.Required(AccessTools.Method(typeof(MyStoreBlock), name, parameters), "MyStoreBlock." + name);

		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
		{
			var code = instructions.ToList();
			switch (original.Name)
			{
				case "BuyPhysicalItemFromPlayer": BuyPhysicalItemFromPlayer(code, generator, original); break;
				case "BuyPhysicalItemInternal": BuyPhysicalItemInternal(code, generator, original); break;
				case "BuyGasInternal": BuyGasInternal(code, generator, original); break;
				case "SellToPlayer": SellToPlayer(code, generator, original); break;
				case "SellToStation": SellToStation(code, generator, original); break;
				case "ChangeBalance": ChangeBalance(code, generator, original); break;
				default: throw EconomyIl.Mismatch(original, "unexpected transpiler target");
			}
			return code;
		}

		// Stands in for a `using` scope: refund on every exit after the reservation.
		[HarmonyFinalizer]
		private static void Finalizer()
		{
			IDisposable scope = s_scope;
			s_scope = null;
			scope?.Dispose();
		}
	}

	// Transpiler seam: MyStoreBlockPatch.BuyPhysicalItemFromPlayer
	// Reserves payment before the first inventory mutation and commits after vanilla settlement calls.
	// Begin anchor: the unique CanItemsBeAdded guard followed by the NotEnoughInventorySpace return.
	// Commit anchor: the second (owner credit) ChangeBalance call, followed by the success result.
	private static void BuyPhysicalItemFromPlayer(List<CodeInstruction> code, ILGenerator generator, MethodBase original)
	{
		int guard = EconomyIl.FindUnique(code, original, "CanItemsBeAdded guard", i => EconomyIl.Calls(code[i], CanItemsBeAddedMethod));
		int reject = EconomyIl.FindNext(code, original, "NotEnoughInventorySpace return", guard, 8, c => c.opcode == OpCodes.Ret);
		EconomyIl.Require(EconomyIl.Calls(code[reject - 1], SendBuyItemResultShort)
			&& EconomyIl.LoadsInt(code[reject - 2], (int)MyStoreBuyItemResults.NotEnoughInventorySpace),
			original, "NotEnoughInventorySpace result does not precede the guard return");
		EconomyIl.InjectGateAfter(code, reject, generator, BeginBuyPhysicalMethod, Args(0, 1, 2, 3, 4, 5, 6), Return);
		InjectCommitAfterLastBalanceCall(code, original, 2, SendBuyItemResultLong);
	}

	// Transpiler seam: MyStoreBlockPatch.BuyPhysicalItemInternal
	// Begin anchor: the StoreNotOwnedByFaction return (unique), followed by the item-creation loop.
	// Commit anchor: the adjacent player-debit / faction-credit pair followed by the success result.
	private static void BuyPhysicalItemInternal(List<CodeInstruction> code, ILGenerator generator, MethodBase original)
	{
		int reject = ResultReturn(code, original, "StoreNotOwnedByFaction", (int)MyStoreBuyItemResults.StoreNotOwnedByFaction, SendBuyItemResultShort);
		EconomyIl.InjectGateAfter(code, reject, generator, BeginBuyPhysicalMethod, Args(0, 1, 2, 3, 6, 7, 8), Return);
		InjectCommitAfterLastBalanceCall(code, original, 2, SendBuyItemResultLong);
	}

	// Transpiler seam: MyStoreBlockPatch.BuyGasInternal
	// Begin anchor: the unique NotEnougGasInTanks return, followed by the character/oxygen branch. Every
	// remaining early return (NotEnoughSpaceInTank, WrongInventory) happens after the reservation and refunds.
	// Commit anchor: the third ChangeBalance call (owner credit), the branch target before the success result.
	private static void BuyGasInternal(List<CodeInstruction> code, ILGenerator generator, MethodBase original)
	{
		int reject = ResultReturn(code, original, "NotEnougGasInTanks", (int)MyStoreBuyItemResults.NotEnougGasInTanks, SendBuyItemResultShort);
		EconomyIl.InjectGateAfter(code, reject, generator, BeginBuyGasMethod, Args(0, 1, 2, 3, 6), Return);
		InjectCommitAfterLastBalanceCall(code, original, 3, SendBuyItemResultLong);
	}

	// Transpiler seam: MyStoreBlockPatch.SellToPlayer
	// Reserves the store owner's payment before the first inventory mutation.
	// Begin anchor: the physical-item case's NotEnoughAmount guard (its `break` compiles to ret or br).
	// Commit anchor: the adjacent seller-credit / owner-debit pair; the price local is the one the first call loads.
	private static void SellToPlayer(List<CodeInstruction> code, ILGenerator generator, MethodBase original)
	{
		int reject = ResultReturn(code, original, "NotEnoughAmount", (int)MyStoreSellItemResults.NotEnoughAmount, SendSellItemResultShort);
		List<int> balance = EconomyIl.FindExactly(code, original, "ChangeBalance", 2, IsChangeBalance);
		CodeInstruction price = EconomyIl.CloneLoad(code[balance[0] - 1], original, "sale price local");
		EconomyIl.InjectGateAfter(code, reject, generator, BeginSellToPlayerMethod,
			new[] { CodeInstruction.LoadArgument(0), CodeInstruction.LoadArgument(1), CodeInstruction.LoadArgument(2), price }, Return);
		InjectCommitAfterLastBalanceCall(code, original, 2, SendSellItemResultLong);
	}

	// Transpiler seam: MyStoreBlockPatch.SellToStation
	// Reserves the faction payment before the first inventory mutation.
	// Begin anchor: the physical case's NotEnoughAmount guard. Commit anchor: the seller-credit / faction-debit
	// pair; the faction id and price locals are the ones the second call loads (ldloc num; ldloc num3; neg; call).
	private static void SellToStation(List<CodeInstruction> code, ILGenerator generator, MethodBase original)
	{
		int reject = ResultReturn(code, original, "NotEnoughAmount", (int)MyStoreSellItemResults.NotEnoughAmount, SendSellItemResultShort);
		List<int> balance = EconomyIl.FindExactly(code, original, "ChangeBalance", 2, IsChangeBalance);
		int debit = balance[1];
		EconomyIl.Require(code[debit - 1].opcode == OpCodes.Neg, original, "faction debit is not negated");
		CodeInstruction price = EconomyIl.CloneLoad(code[debit - 2], original, "sale price local");
		CodeInstruction faction = EconomyIl.CloneLoad(code[debit - 3], original, "faction id local");
		EconomyIl.InjectGateAfter(code, reject, generator, BeginSellToStationMethod,
			new[] { CodeInstruction.LoadArgument(0), CodeInstruction.LoadArgument(1), CodeInstruction.LoadArgument(2), faction, price }, Return);
		InjectCommitAfterLastBalanceCall(code, original, 2, SendSellItemResultLong);
	}

	// Transpiler seam: MyStoreBlockPatch.BeginPayment (ATM withdrawal)
	// `account` was read before the deposit/withdraw split and is only compared in the withdrawal branch, so
	// two nodes can both pass the comparison and both debit. Reserve the withdrawal at the World Authority at
	// the head of the branch, before any inventory change; the unchecked debit below is then observed and
	// suppressed, and every early exit refunds. The deposit branch is deliberately unpatched: a credit cannot
	// overdraw and every MyBankingSystem.ChangeBalance call is already WA-ordered through MyBankingSystemPatch.
	// Begin anchor: the unique `ldarg amount; neg; stloc num2` opening the else branch.
	// Commit anchor: the unique inventory.AddItems call in this method.
	private static void ChangeBalance(List<CodeInstruction> code, ILGenerator generator, MethodBase original)
	{
		int store = EconomyIl.FindUnique(code, original, "withdrawal amount negation", i =>
			i + 2 < code.Count && code[i].opcode == OpCodes.Ldarg_1 && code[i + 1].opcode == OpCodes.Neg && EconomyIl.IsStloc(code[i + 2])) + 2;
		CodeInstruction withdrawal = EconomyIl.LoadOfStore(code[store], original, "withdrawal amount local");
		List<int> balance = EconomyIl.FindExactly(code, original, "ChangeBalance", 2, IsChangeBalance);
		int debit = balance[1];
		EconomyIl.Require(code[debit - 1].opcode == OpCodes.Conv_I8 && code[debit - 2].opcode == OpCodes.Ldarg_1,
			original, "withdrawal debit does not load (identity, (long)amount)");
		CodeInstruction identity = EconomyIl.CloneLoad(code[debit - 3], original, "identity id local");
		EconomyIl.InjectGateAfter(code, store, generator, BeginWithdrawalMethod,
			new[] { CodeInstruction.LoadArgument(0), identity, withdrawal }, Return);
		int add = EconomyIl.FindUnique(code, original, "inventory.AddItems", i => EconomyIl.Calls(code[i], AddItemsMethod));
		EconomyIl.Require(code[add + 1].opcode == OpCodes.Pop, original, "AddItems result is not discarded");
		// Plain insert: the commit stays INSIDE the `if (MyBankingSystem.ChangeBalance(num, amount))` block, so
		// the labels of the join point that follows (deposit path, declined debit) must not move onto it.
		code.Insert(add + 2, new CodeInstruction(OpCodes.Call, CommitMethod));
	}

	/// <summary>Index of the return of "SendXxxItemResult(id, amount, result); return;" for the unique result constant.</summary>
	private static int ResultReturn(List<CodeInstruction> code, MethodBase original, string result, int value, MethodInfo send)
	{
		int constant = EconomyIl.FindUnique(code, original, result + " result", i =>
			i + 2 < code.Count && EconomyIl.LoadsInt(code[i], value) && EconomyIl.Calls(code[i + 1], send) && EconomyIl.IsBranchOrReturn(code[i + 2]));
		return constant + 2;
	}

	/// <summary>Inserts the commit call after the pop of the last of exactly <paramref name="count"/> ChangeBalance calls.</summary>
	private static void InjectCommitAfterLastBalanceCall(List<CodeInstruction> code, MethodBase original, int count, MethodInfo success)
	{
		List<int> balance = EconomyIl.FindExactly(code, original, "ChangeBalance", count, IsChangeBalance);
		int last = balance[count - 1];
		EconomyIl.Require(code[last + 1].opcode == OpCodes.Pop, original, "last ChangeBalance result is not discarded");
		EconomyIl.FindNext(code, original, "success result after the last ChangeBalance", last, 24, c => EconomyIl.Calls(c, success));
		EconomyIl.InsertAfter(code, last + 1, new[] { new CodeInstruction(OpCodes.Call, CommitMethod) });
	}

	// ---------------------------------------------------------------------------------------------------------
	// The deferred (prefab) settlement path: reservation before the asynchronous spawn, settle/refund in the
	// two compiler-generated callbacks of BuyPrefabInternal.
	// ---------------------------------------------------------------------------------------------------------

	private static (MethodInfo Success, MethodInfo Failure)? s_callbacks;

	/// <summary>The two closure methods, in the order BuyPrefabInternal passes them (success, then failure).</summary>
	private static (MethodInfo Success, MethodInfo Failure) Callbacks()
	{
		if (s_callbacks.HasValue) return s_callbacks.Value;
		MethodBase original = BuyPrefab.Target();
		List<MethodInfo> callbacks = PatchProcessor.ReadMethodBody(original)
			.Where(pair => pair.Key == OpCodes.Ldftn)
			.Select(pair => pair.Value as MethodInfo)
			.Where(method => method != null && method.DeclaringType != null
				&& method.DeclaringType.DeclaringType == typeof(MyStoreBlock)
				&& method.Name.Contains("<BuyPrefabInternal>b__"))
			.ToList();
		if (callbacks.Count != 2)
			throw EconomyIl.Mismatch(original, "expected exactly two spawn callback closures, found " + callbacks.Count);
		s_callbacks = (callbacks[0], callbacks[1]);
		return s_callbacks.Value;
	}

	// Transpiler seam: MyStoreBlockPatch.BuyPrefabInternal
	// Reserves payment before the asynchronous spawn; the spawn callbacks settle it or refund it.
	// Begin anchor: the statement `MyPrefabManager.Static.SpawnPrefabInternalRandomColor(...)` - its first
	// instruction loads MyPrefabManager.Static; the closure local is the one loaded before the first ldftn.
	[HarmonyPatch]
	private static class BuyPrefab
	{
		internal static MethodBase Target()
			=> EconomyIl.Required(AccessTools.Method(typeof(MyStoreBlock), "BuyPrefabInternal"), "MyStoreBlock.BuyPrefabInternal");

		private static MethodBase TargetMethod() => Target();

		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
		{
			var code = instructions.ToList();
			int spawn = EconomyIl.FindUnique(code, original, "SpawnPrefabInternalRandomColor call", i => EconomyIl.Calls(code[i], SpawnPrefabMethod));
			int start = EconomyIl.FindUnique(code, original, "MyPrefabManager.Static load", i => LoadsPrefabManager(code[i]));
			EconomyIl.Require(start < spawn && spawn - start <= 12, original, "MyPrefabManager.Static load is not the head of the spawn statement");
			int ldftn = EconomyIl.FindNext(code, original, "success callback ldftn", start, spawn - start, c => c.opcode == OpCodes.Ldftn);
			CodeInstruction closure = EconomyIl.CloneLoad(code[ldftn - 1], original, "closure local");
			EconomyIl.InjectGateBefore(code, start, generator, BeginBuyPrefabMethod,
				new[] { CodeInstruction.LoadArgument(0), CodeInstruction.LoadArgument(1), CodeInstruction.LoadArgument(2), CodeInstruction.LoadArgument(3), CodeInstruction.LoadArgument(9), closure },
				Return);
			return code;
		}

		private static bool LoadsPrefabManager(CodeInstruction code)
			=> (code.opcode == OpCodes.Ldsfld && code.operand is FieldInfo field && field.DeclaringType == typeof(MyPrefabManager) && field.Name == "Static")
				|| (code.opcode == OpCodes.Call && code.operand is MethodInfo getter && getter.DeclaringType == typeof(MyPrefabManager) && getter.Name == "get_Static");
	}

	// Success callback: the three settlement statements (storeItem.Amount -= amount; debit; credit) are replaced
	// by SettleDeferred(...), which runs them inside the deferred scope and commits; the success result follows.
	[HarmonyPatch]
	private static class BuyPrefabSucceeded
	{
		private static MethodBase TargetMethod() => Callbacks().Success;

		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			var code = instructions.ToList();
			List<int> balance = EconomyIl.FindExactly(code, original, "ChangeBalance", 2, IsChangeBalance);
			int end = balance[1] + 1;
			EconomyIl.Require(code[end].opcode == OpCodes.Pop && end + 1 < code.Count, original, "settlement does not end with a discarded credit");
			for (int i = 1; i <= end; i++)
				EconomyIl.Require(code[i].labels.Count == 0 && code[i].blocks.Count == 0, original, "settlement statements are not straight-line code");
			Type closure = original.DeclaringType;
			var fields = new Dictionary<string, FieldInfo>();
			for (int i = 0; i <= end; i++)
				if (code[i].opcode == OpCodes.Ldfld && code[i].operand is FieldInfo field && field.DeclaringType == closure)
					fields[field.Name] = field;
			FieldInfo Captured(string name)
				=> fields.TryGetValue(name, out FieldInfo field) ? field : throw EconomyIl.Mismatch(original, "captured '" + name + "' not loaded by the settlement");
			var replacement = new List<CodeInstruction>
			{
				new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, Captured("storeItem")),
				new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, Captured("amount")),
				new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, Captured("player")),
				new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, Captured("faction")),
				new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, Captured("totalPrice")),
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Call, SettleDeferredMethod)
			};
			replacement[0].labels.AddRange(code[0].labels);
			replacement[0].blocks.AddRange(code[0].blocks);
			code.RemoveRange(0, end + 1);
			code.InsertRange(0, replacement);
			return code;
		}
	}

	// Failure callback: dispose (refund) the deferred scope before the SpawnFailed result.
	[HarmonyPatch]
	private static class BuyPrefabFailed
	{
		private static MethodBase TargetMethod() => Callbacks().Failure;

		[HarmonyPrefix]
		private static void Prefix(object __instance) => DisposeDeferred(__instance);
	}

	// ---------------------------------------------------------------------------------------------------------
	// Helpers called from the injected IL. Public because the patched game methods call them directly.
	// ---------------------------------------------------------------------------------------------------------

	private static bool Begin(long payerId, long amount)
	{
		var seam = MyStoreBlockSeams.ClusterBeginPayment;
		if (seam == null) return true;
		(IDisposable scope, bool approved) = seam(payerId, amount);
		s_scope = scope;
		return approved;
	}

	public static bool BeginBuyPhysical(MyStoreBlock self, long id, int amount, MyPlayer player, MyInventory inventory, MyStoreItem storeItem, long totalPrice)
	{
		if (!Begin(player.Identity.IdentityId, totalPrice))
		{
			self.SendBuyItemResult(id, amount, MyStoreBuyItemResults.NotEnoughMoney);
			return false;
		}
		// Second capacity check inside the scope: a refusal here refunds the reservation.
		if (!inventory.CanItemsBeAdded(amount, storeItem.Item.Value))
		{
			self.SendBuyItemResult(id, amount, MyStoreBuyItemResults.NotEnoughInventorySpace);
			return false;
		}
		return true;
	}

	public static bool BeginBuyGas(MyStoreBlock self, MyStoreItem storeItem, int amount, MyPlayer player, long totalPrice)
	{
		if (Begin(player.Identity.IdentityId, totalPrice)) return true;
		self.SendBuyItemResult(storeItem.Id, amount, MyStoreBuyItemResults.NotEnoughMoney);
		return false;
	}

	public static bool BeginSellToPlayer(MyStoreBlock self, long id, int amount, long price)
	{
		if (Begin(self.OwnerId, price)) return true;
		self.SendSellItemResult(id, amount, MyStoreSellItemResults.NotEnoughMoney);
		return false;
	}

	public static bool BeginSellToStation(MyStoreBlock self, long id, int amount, long factionId, long price)
	{
		if (Begin(factionId, price)) return true;
		self.SendSellItemResult(id, amount, MyStoreSellItemResults.NotEnoughMoney);
		return false;
	}

	public static bool BeginWithdrawal(MyStoreBlock self, long identityId, int amount)
	{
		if (Begin(identityId, amount)) return true;
		MyMultiplayer.RaiseEvent(self, (MyStoreBlock x) => x.OnChangeBalanceResult, MyStoreBuyItemResults.NotEnoughMoney, MyEventContext.Current.Sender);
		return false;
	}

	public static void Commit()
	{
		IDisposable scope = s_scope;
		if (scope != null) MyStoreBlockSeams.ClusterCommitPayment?.Invoke(scope);
	}

	public static bool BeginBuyPrefab(MyStoreBlock self, MyStoreItem storeItem, int amount, MyPlayer player, long totalPrice, object closure)
	{
		var seam = MyStoreBlockSeams.ClusterBeginDeferredPayment;
		if (seam == null) return true;
		(IDisposable scope, bool approved) = seam(player.Identity.IdentityId, totalPrice);
		if (!approved)
		{
			self.SendBuyItemResult(storeItem.Id, amount, MyStoreBuyItemResults.NotEnoughMoney);
			return false;
		}
		if (scope != null)
		{
			DeferredScopes.Remove(closure);
			DeferredScopes.Add(closure, scope);
		}
		return true;
	}

	private static IDisposable TakeDeferred(object closure)
	{
		if (!DeferredScopes.TryGetValue(closure, out IDisposable scope)) return null;
		DeferredScopes.Remove(closure);
		return scope;
	}

	public static void SettleDeferred(MyStoreItem storeItem, int amount, MyPlayer player, MyFaction faction, long totalPrice, object closure)
	{
		Action settlement = delegate
		{
			storeItem.Amount -= amount;
			MyBankingSystem.ChangeBalance(player.Identity.IdentityId, -totalPrice);
			MyBankingSystem.ChangeBalance(faction.FactionId, totalPrice);
		};
		IDisposable scope = TakeDeferred(closure);
		var seam = MyStoreBlockSeams.ClusterSettlePayment;
		if (seam == null || scope == null) settlement();
		else seam(scope, settlement);
	}

	public static void DisposeDeferred(object closure) => TakeDeferred(closure)?.Dispose();
}
