using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// SE-1487: the Gateway's "forget player" admin operation, World Authority side. It removes an offline
/// player from the world the way vanilla's abandoned-identity cleanup does (faction membership, identity,
/// player binding, bank account, GPS, reputation), then drops what the cluster keeps for them: the
/// promotion entry, which would otherwise keep the player on admin lists, and the character registry's
/// record. The nodes follow through the replicated identity, faction, banking and promotion state.
/// Refused while the player is online, and while a loaded grid still has blocks the identity built (delete its
/// grids first, or their blocks would be left owned by nobody).
/// </summary>
public static class PlayerForgetService
{
	public sealed record ForgetRequest(ulong SteamId);
	public sealed record ForgetResult(ulong SteamId, long IdentityId, bool IdentityRemoved, bool PromotionRemoved, bool RecordRemoved);

	internal static void Install() => GlobalStateChannel.RegisterCommandHandler("player-forget", ForgetFromGateway, journaled: true);

	private static string ForgetFromGateway(string origin, string payload)
	{
		if (origin != "$gateway") throw new InvalidOperationException("Player administration must originate at the Gateway");
		ForgetRequest request = JsonSerializer.Deserialize<ForgetRequest>(payload);
		if (request == null || request.SteamId == 0) throw new ArgumentException("Invalid player");
		MySandboxGame game = MySandboxGame.Static ?? throw new InvalidOperationException("The World Authority game is unavailable");
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		void Apply()
		{
			try { completion.SetResult(JsonSerializer.Serialize(Forget(request.SteamId))); }
			catch (Exception e) { completion.SetException(e); }
		}
		if (game.UpdateThread == Thread.CurrentThread) Apply();
		else game.Invoke(Apply, "ClusterPlugin player forget");
		return completion.Task.GetAwaiter().GetResult();
	}

	private static ForgetResult Forget(ulong steamId)
	{
		MySession session = MySession.Static;
		if (!ClusterRuntime.IsWorldAuthority || ClusterRuntime.IsAuthorityFenced || session?.Ready != true)
			throw new InvalidOperationException("The World Authority is unavailable");
		if (OnlinePlayersService.Cluster.Any(player => player.SteamId == steamId))
			throw new InvalidOperationException("player_online");
		long identityId = session.Players.TryGetIdentityId(steamId);
		bool identityRemoved = false;
		MyIdentity identity = identityId == 0 ? null : session.Players.TryGetIdentity(identityId);
		if (identity != null)
		{
			// The WA loads no grids: its own count is only the start, the PCU ledger has what the regular nodes hold.
			// Grids in offline partitions count nowhere, so the operator deletes those first (SE-1487 runbook).
			if (identity.BlockLimits.BlocksBuilt + PcuLedgerService.RemoteUsage(identityId).BlocksBuilt != 0)
				throw new InvalidOperationException("player_has_blocks");
			MySessionComponentTrash trash = session.GetComponent<MySessionComponentTrash>()
				?? throw new InvalidOperationException("Trash component unavailable");
			identityRemoved = trash.ClusterForgetIdentity(identity, new MyPlayer.PlayerId(steamId));
			if (!identityRemoved) throw new InvalidOperationException("identity_not_removed");
		}
		bool promotionRemoved = PromotionService.Forget(steamId);
		bool recordRemoved = identityId != 0 && CharacterRegistryService.Forget(identityId);
		Console.WriteLine($"[ClusterPlugin] player forget client={steamId} identity={identityId} removed={identityRemoved} "
			+ $"promotion={promotionRemoved} record={recordRemoved}");
		return new ForgetResult(steamId, identityId, identityRemoved, promotionRemoved, recordRemoved);
	}
}
