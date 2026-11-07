using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using VRage.Game.ObjectBuilders.Components.BankingAndCurrency;

namespace ClusterPlugin;

public static class MyBankingSystemPatch
{
	private const string Subsystem = "banking";
	private static readonly object SnapshotSync = new object();
	private static readonly PropertyInfo OverallBalanceProperty = typeof(MyBankingSystem).GetProperty(nameof(MyBankingSystem.OverallBalance))
		?? throw new MissingMemberException(typeof(MyBankingSystem).FullName, nameof(MyBankingSystem.OverallBalance));
	private static readonly MethodInfo ChangeBalanceInternal = typeof(MyBankingSystem).GetMethod("ChangeBalanceInternal", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(MyBankingSystem).FullName, "ChangeBalanceInternal");
	private static string m_snapshot = BankingAuthorityCodec.WriteSnapshot(0, Array.Empty<BankingAuthorityEntry>());
	private static bool m_snapshotReady;
	private static volatile MySession m_readySession;
	[ThreadStatic] private static bool m_applying;
	private static double m_nextRefresh;
	private static long m_testDelayOwner;
	private static int m_testDelayMilliseconds;

	public static void Install()
	{
		MyBankingSystemSeams.ClusterCreateAccount = SeamCreateAccount;
		MyBankingSystemSeams.ClusterRemoveAccount = SeamRemoveAccount;
		MyBankingSystemSeams.ClusterChangeBalance = SeamChangeBalance;
		MyBankingSystemSeams.ClusterTransfer = SeamTransfer;
		GlobalStateChannel.RegisterCommandHandler(Subsystem, HandleCommand, journaled: true);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, CurrentSnapshot, ApplySnapshot);
	}

	public static void Update()
	{
		MySession session = MySession.Static;
		if (session?.Ready != true) return;
		m_readySession = session;
		if (!ClusterRuntime.IsWorldAuthority || MyBankingSystem.Static == null
			|| ClusterRuntime.LocalMilliseconds < m_nextRefresh) return;
		m_nextRefresh = ClusterRuntime.LocalMilliseconds + 1000;
		PublishSnapshot();
	}

	public static bool CreateAccountPrefix(MyBankingSystem __instance, long ownerIdentifier, long startingBalance)
	{
		if (RunOriginal()) return true;
		BankingAuthorityCodec.ReadResult(GlobalStateChannel.SendCommand(Subsystem,
			BankingAuthorityCodec.WriteRequest(new BankingAuthorityRequest("Create", ownerIdentifier, startingBalance))));
		return false;
	}

	public static bool RemoveAccountPrefix(MyBankingSystem __instance, long ownerIdentifier, ref bool __result)
	{
		if (RunOriginal()) return true;
		__result = BankingAuthorityCodec.ReadResult(GlobalStateChannel.SendCommand(Subsystem,
			BankingAuthorityCodec.WriteRequest(new BankingAuthorityRequest("Remove", ownerIdentifier, 0)))).Success;
		return false;
	}

	public static bool ChangeBalancePrefix(long identifierId, long amount, ref bool __result)
	{
		if (MyStoreBlockPatch.TryObserveBalanceChange(identifierId, amount, ref __result)) return false;
		if (RunOriginal())
		{
			PrepareClients(identifierId, amount < 0);
			return true;
		}
		__result = BankingAuthorityCodec.ReadResult(GlobalStateChannel.SendCommand(Subsystem,
			BankingAuthorityCodec.WriteRequest(new BankingAuthorityRequest("Change", identifierId, amount)))).Success;
		return false;
	}

	internal static bool ChangeBalanceWithOperation(long identifierId, long amount, Guid operationId)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority)
			throw new InvalidOperationException("Explicit banking operations must originate on a regular cluster node");
		return BankingAuthorityCodec.ReadResult(GlobalStateChannel.SendCommand(Subsystem,
			BankingAuthorityCodec.WriteRequest(new BankingAuthorityRequest("Change", identifierId, amount)), operationId)).Success;
	}

	private static bool SeamCreateAccount(MyBankingSystem instance, long ownerIdentifier, long startingBalance)
		=> !CreateAccountPrefix(instance, ownerIdentifier, startingBalance);
	private static bool? SeamRemoveAccount(MyBankingSystem instance, long ownerIdentifier)
	{
		bool result = false;
		return RemoveAccountPrefix(instance, ownerIdentifier, ref result) ? null : result;
	}
	private static bool? SeamChangeBalance(long identifierId, long amount)
	{
		bool result = false;
		return ChangeBalancePrefix(identifierId, amount, ref result) ? null : result;
	}

	/// <summary>
	/// Settles a two-party transfer (player trade, faction deposit/withdraw) atomically at the World Authority.
	/// The caller - vanilla's <c>Transfer_Internal</c> - has already run its node-local validation; the authority
	/// enforces the balance itself, since the local replica may lag. On success the same movement is applied to
	/// the local replica immediately so the caller's out-params and client broadcast carry the settled balances;
	/// the next snapshot confirms rather than corrects. The replica change raises no event of its own: the caller
	/// broadcasts the transfer, and clients handed both would apply it twice.
	/// </summary>
	private static bool? SeamTransfer(long fromIdentifier, long toIdentifier, long amount)
	{
		if (RunOriginal()) return null;
		bool success = BankingAuthorityCodec.ReadResult(GlobalStateChannel.SendCommand(Subsystem,
			BankingAuthorityCodec.WriteRequest(new BankingAuthorityRequest("Transfer", fromIdentifier, amount, toIdentifier)))).Success;
		if (success)
		{
			PrepareClients(fromIdentifier, true);
			PrepareClients(toIdentifier, false);
			ChangeBalanceInternal.Invoke(MyBankingSystem.Static, new object[] { fromIdentifier, -amount, null });
			ChangeBalanceInternal.Invoke(MyBankingSystem.Static, new object[] { toIdentifier, amount, null });
		}
		return success;
	}

	/// <summary>
	/// Vanilla's balance events throw on a client that has no such account or too little to debit, and the client
	/// disconnects ("Packet processing error"). A clustered client can miss what it would need: it keeps its loaded
	/// world when it switches nodes, and events raised during the switch reach it on neither node. So before an event
	/// that could fail, tell the clients the account and, for a debit, its balance. Both are no-ops on a client
	/// that is in sync. The cluster also serves unmodified clients, so this cannot wait for a client-side fix.
	/// </summary>
	private static void PrepareClients(long ownerId, bool debit)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority
			|| MyBankingSystem.Static?.TryGetAccountInfo(ownerId, out MyAccountInfo account) != true) return;
		MyMultiplayer.RaiseStaticEvent(x => MyBankingSystem.CreateAccount_Clients, ownerId, account.Balance);
		if (debit) MyMultiplayer.RaiseStaticEvent(x => MyBankingSystem.ChangeBalanceBroadcastToClients, ownerId, 0L, account.Balance);
	}

	private static string HandleCommand(string origin, string payload)
	{
		BankingAuthorityRequest request = BankingAuthorityCodec.ReadRequest(payload);
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		MySandboxGame game = MySandboxGame.Static ?? throw new InvalidOperationException("The World Authority game is unavailable");
		Action execute = () =>
		{
			try
			{
				// Chaos-test knob: hold the mutation in flight on the authority's game thread so a
				// lane can kill the WA mid-execution deterministically. Zero in production traffic.
				int delay = request.DelayMilliseconds;
				if (request.Kind == "Change" && Interlocked.CompareExchange(ref m_testDelayOwner, 0, request.OwnerId) == request.OwnerId)
					delay = Math.Max(delay, Volatile.Read(ref m_testDelayMilliseconds));
				if (delay > 0) Thread.Sleep(delay);
				bool success;
				switch (request.Kind)
				{
				case "Create":
						if (!MyBankingSystem.Static.TryGetAccountInfo(request.OwnerId, out var _)
							&& request.Amount > long.MaxValue - MyBankingSystem.Static.OverallBalance)
						{
							success = false;
							break;
						}
						MyBankingSystem.Static.CreateAccount(request.OwnerId, request.Amount);
						success = MyBankingSystem.Static.TryGetAccountInfo(request.OwnerId, out var _);
						break;
					case "Remove":
						success = MyBankingSystem.Static.RemoveAccount(request.OwnerId);
						break;
					case "Transfer":
						// The authority owns both balances, so this pair of checks plus vanilla's own
						// Transfer_Server is the atomic settlement - one game-thread action, no partial state.
						success = MyBankingSystem.Static.TryGetAccountInfo(request.OwnerId, out MyAccountInfo fromInfo)
							&& MyBankingSystem.Static.TryGetAccountInfo(request.ToId, out MyAccountInfo toInfo)
							&& fromInfo.Balance >= request.Amount
							&& request.Amount <= long.MaxValue - toInfo.Balance;
						if (success) MyBankingSystem.Static.Transfer_Server(request.OwnerId, request.ToId, request.Amount);
						break;
				default:
						if (!MyBankingSystem.Static.TryGetAccountInfo(request.OwnerId, out MyAccountInfo account)
							|| request.Amount > 0 && (request.Amount > long.MaxValue - account.Balance
								|| request.Amount > long.MaxValue - MyBankingSystem.Static.OverallBalance))
							success = false;
						else
							success = MyBankingSystem.ChangeBalance(request.OwnerId, request.Amount);
						break;
				}
				PublishSnapshot();
				completion.SetResult(BankingAuthorityCodec.WriteResult(success));
			}
			catch (Exception exception) { completion.SetException(exception); }
		};
		if (Thread.CurrentThread == game.UpdateThread) execute();
		else game.Invoke(execute, "ClusterPlugin banking command");
		return completion.Task.GetAwaiter().GetResult();
	}

	/// <summary>
	/// SE-1226 chaos-test knob: the next balance change of <paramref name="ownerId"/> waits this long on the authority's
	/// game thread before it applies, as the live World Authority stalled while a node waited for its answer. Zero in
	/// production traffic.
	/// </summary>
	internal static void DelayNextChange(long ownerId, int milliseconds)
	{
		Volatile.Write(ref m_testDelayMilliseconds, milliseconds);
		Interlocked.Exchange(ref m_testDelayOwner, ownerId);
	}

	private static void ApplySnapshot(string payload)
	{
		BankingAuthoritySnapshot snapshot = BankingAuthorityCodec.ReadSnapshot(payload);
		MyBankingSystem banking = MyBankingSystem.Static;
		if (banking == null) return;
		var desired = snapshot.Accounts.ToDictionary(x => x.OwnerId);
		MyObjectBuilder_BankingSystem local = (MyObjectBuilder_BankingSystem)banking.GetObjectBuilder();
		m_applying = true;
		try
		{
			foreach (long ownerId in local.Accounts?.Select(x => x.OwnerIdentifier).ToArray() ?? Array.Empty<long>())
				if (!desired.ContainsKey(ownerId)) banking.RemoveAccount(ownerId);
			foreach (BankingAuthorityEntry entry in snapshot.Accounts)
			{
				if (!banking.TryGetAccountInfo(entry.OwnerId, out MyAccountInfo account))
					banking.CreateAccount(entry.OwnerId, entry.Balance);
				else if (account.Balance != entry.Balance)
					MyBankingSystem.ChangeBalance(entry.OwnerId, entry.Balance - account.Balance);
			}
			OverallBalanceProperty.SetValue(banking, snapshot.OverallBalance);
		}
		finally { m_applying = false; }
	}

	private static void PublishSnapshot()
	{
		MyBankingSystem banking = MyBankingSystem.Static;
		MyObjectBuilder_BankingSystem builder = (MyObjectBuilder_BankingSystem)banking.GetObjectBuilder();
		string snapshot = BankingAuthorityCodec.WriteSnapshot(banking.OverallBalance,
			builder.Accounts?.Select(x => new BankingAuthorityEntry(x.OwnerIdentifier, x.Account.Balance))
			?? Enumerable.Empty<BankingAuthorityEntry>());
		lock (SnapshotSync)
		{
			if (m_snapshotReady && m_snapshot == snapshot) return;
			m_snapshot = snapshot;
			m_snapshotReady = true;
		}
		GlobalStateChannel.Publish(Subsystem, snapshot);
	}

	private static bool RunOriginal() => m_readySession != MySession.Static || m_applying || !ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority;
	private static string CurrentSnapshot() { lock (SnapshotSync) return m_snapshotReady ? m_snapshot : null; }

	internal static void ApplyLocally(Action action)
	{
		bool previous = m_applying;
		m_applying = true;
		try { action(); }
		finally { m_applying = previous; }
	}
}
