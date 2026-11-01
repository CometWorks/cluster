using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Sandbox.Game.World;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// WA-2: cross-node grid ownership (PCU authorship) transfer. Vanilla's flow breaks twice in a cluster: the
/// confirm-dialog client event is raised on the grid's node with a directed endpoint, but the new owner's client
/// may be attached elsewhere (the event drops silently); and the acceptance raises the server event at the
/// ACCEPTING client's node, where the grid and the old owner's transfer transaction do not exist (silent return).
/// Identities and steam-id bindings already replicate cluster-wide through the identity authority, so the request
/// side needs no help. This service forwards the offer to the node hosting the new owner's client and the
/// acceptance back to the node hosting the grid - where vanilla's own transaction check
/// (<c>IsTransferInProgress</c> / <c>FinishBlockTransfer</c>) provides the exactly-once guarantee.
/// </summary>
public static class GridTransferService
{
	private sealed record TransferOffer(long GridEntityId, string GridName, long OldOwner, long NewOwner,
		int BlocksCount, int Pcu, ulong TargetSteamId);

	private sealed record TransferAccept(long GridEntityId, long OldOwner, long NewOwner, ulong NewOwnerSteamId);

	internal static void Install()
	{
		// ClusterGridTransfer, not MyBlockLimits: touching MyBlockLimits statics here runs its static
		// constructor, which dereferences MySession.Static before any session exists.
		ClusterGridTransfer.Offer = ForwardOffer;
		ClusterGridTransfer.ForwardAccept = ForwardAccept;
	}

	/// <summary>Grid node: sends the offer to the node really hosting the new owner's client.</summary>
	private static bool ForwardOffer(long gridEntityId, string gridName, long oldOwner, long newOwner,
		int blocksCount, int pcu, ulong targetSteamId)
	{
		if (!ClusterRuntime.IsClustered || ReplicationServerPatch.HasClient(targetSteamId)) return false;
		try
		{
			// T-0350: two registry lookups (30 s each) on the game thread; fail as a failed lookup does.
			if (ClusterRuntime.RegistryUnreachable) throw new InvalidOperationException("Registry unreachable: owner not resolved");
			(string node, string control) = SpawnDirectorService.ResolveOwner(targetSteamId);
			using StringContent content = new StringContent(JsonSerializer.Serialize(
				new TransferOffer(gridEntityId, gridName, oldOwner, newOwner, blocksCount, pcu, targetSteamId)),
				Encoding.UTF8, "application/json");
			using HttpResponseMessage response = SpawnDirectorService.RegistryClient
				.PostAsync(control.TrimEnd('/') + "/cluster/transfer-offer-execute", content).GetAwaiter().GetResult();
			Console.WriteLine($"[ClusterPlugin] transfer offer grid={gridEntityId} old={oldOwner} new={newOwner}"
				+ $" client={targetSteamId} node={node} delivered={response.IsSuccessStatusCode}");
			return true;
		}
		catch (Exception exception)
		{
			// Same outcome as vanilla's silently dropped directed event, but visibly.
			Console.WriteLine("[ClusterPlugin] transfer offer forward failed grid=" + gridEntityId
				+ " error=" + SecretRedactor.Redact(exception.Message));
			return true;
		}
	}

	/// <summary>Target node: raises the vanilla confirm dialog to its locally attached client.</summary>
	public static object ExecuteOffer(string payload)
	{
		TransferOffer offer = JsonSerializer.Deserialize<TransferOffer>(payload,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
			?? throw new ArgumentException("Transfer offer body is required");
		if (offer.GridEntityId == 0 || offer.OldOwner == 0 || offer.NewOwner == 0 || offer.TargetSteamId == 0
			|| offer.GridName?.Length > 1024)
			throw new ArgumentException("Invalid transfer offer");
		if (!ReplicationServerPatch.HasClient(offer.TargetSteamId))
			return new { delivered = false, reason = "client-not-here" };
		MyBlockLimitsSeams.ClusterRaiseTransferOffer(offer.GridEntityId, offer.GridName ?? string.Empty, offer.OldOwner,
			offer.NewOwner, offer.BlocksCount, offer.Pcu, offer.TargetSteamId);
		Console.WriteLine($"[ClusterPlugin] transfer offer raised grid={offer.GridEntityId} client={offer.TargetSteamId}");
		return new { delivered = true };
	}

	/// <summary>Accepting client's node: forwards the acceptance to the node hosting the grid.</summary>
	private static bool ForwardAccept(long gridEntityId, long oldOwner, long newOwner)
	{
		if (!ClusterRuntime.IsClustered) return false;
		try
		{
			// The transfer transaction lives on the node that ran SendTransferRequestMessage - the node
			// hosting the OLD owner's client, which vanilla requires to also hold the grid.
			ulong oldOwnerSteamId = MySession.Static.Players.TryGetSteamId(oldOwner);
			if (oldOwnerSteamId == 0) throw new InvalidOperationException("Old owner has no cluster binding");
			ulong newOwnerSteamId = MyEventContext.Current.Sender.Value;
			// T-0350: as ForwardOffer.
			if (ClusterRuntime.RegistryUnreachable) throw new InvalidOperationException("Registry unreachable: owner not resolved");
			(string node, string control) = SpawnDirectorService.ResolveOwner(oldOwnerSteamId);
			using StringContent content = new StringContent(JsonSerializer.Serialize(
				new TransferAccept(gridEntityId, oldOwner, newOwner, newOwnerSteamId)),
				Encoding.UTF8, "application/json");
			using HttpResponseMessage response = SpawnDirectorService.RegistryClient
				.PostAsync(control.TrimEnd('/') + "/cluster/transfer-accept-execute", content).GetAwaiter().GetResult();
			Console.WriteLine($"[ClusterPlugin] transfer accept grid={gridEntityId} old={oldOwner} new={newOwner}"
				+ $" node={node} forwarded={response.IsSuccessStatusCode}");
			return true;
		}
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] transfer accept forward failed grid=" + gridEntityId
				+ " error=" + SecretRedactor.Redact(exception.Message));
			return true;
		}
	}

	/// <summary>
	/// Grid node: runs the vanilla acceptance body under the accepting client's event context, so vanilla's own
	/// sender validation and transfer-transaction check decide - a replay finds the transaction already finished
	/// and returns without moving anything twice.
	/// </summary>
	public static object ExecuteAccept(string payload)
	{
		TransferAccept accept = JsonSerializer.Deserialize<TransferAccept>(payload,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
			?? throw new ArgumentException("Transfer accept body is required");
		if (accept.GridEntityId == 0 || accept.OldOwner == 0 || accept.NewOwner == 0 || accept.NewOwnerSteamId == 0)
			throw new ArgumentException("Invalid transfer accept");
		using (MyEventContext.Set(new EndpointId(accept.NewOwnerSteamId), null, isInvokedLocally: false))
			MyBlockLimits.TransferBlocksBuiltByID(accept.GridEntityId, accept.OldOwner, accept.NewOwner);
		Console.WriteLine($"[ClusterPlugin] transfer executed grid={accept.GridEntityId}"
			+ $" old={accept.OldOwner} new={accept.NewOwner}");
		return new { executed = true };
	}
}
