using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// WA-4/WA-6 on-demand partition pull. A client's replication request for a grid this node does not hold
/// is vanilla's silent no-op; when the grid is antenna-published by another node (its proxy exists here),
/// the demand is reported to the Gateway, which runs a fenced clientless handover of the grid's partition
/// to this node. Vanilla's on-demand replication then serves the client's next attempt - rights checks
/// (ownership, mutual antenna reachability) stay exactly where vanilla runs them, after the grid is local.
///
/// SE1-0122: a demand from the respawn screen's spawn point preview (it names a medical room) moves the dead
/// client instead of the grid. See <see cref="Preview"/>.
/// </summary>
public static class RemotePullService
{
	private const double DemandCooldownMilliseconds = 10000;
	private const int PreviewAttempts = 60;
	private static readonly object Sync = new();
	private static readonly Dictionary<(long Entity, ulong Client), double> LastDemand = new();
	private static readonly HttpClient Gateway = ClusterHttp.Client(TimeSpan.FromSeconds(120));
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	internal static void Install()
	{
		ClusterRuntime.AuthorizeRegistryClient(Gateway);
		ClusterReplicationDemand.AbsentReplicable = Demand;
	}

	private static void Demand(Endpoint sender, long entityId, long medicalRoomId)
	{
		try
		{
			if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority || entityId == 0) return;
			ulong client = sender.Id.Value;
			// A pull serves every client that wants the grid, a preview only the client that asked.
			(long, ulong) key = (entityId, medicalRoomId != 0 ? client : 0);
			lock (Sync)
			{
				if (LastDemand.TryGetValue(key, out double last)
					&& ClusterRuntime.LocalMilliseconds - last < DemandCooldownMilliseconds) return;
				LastDemand[key] = ClusterRuntime.LocalMilliseconds;
				if (LastDemand.Count > 1024) LastDemand.Clear();
			}
			if (medicalRoomId != 0) Task.Run(() => Preview(entityId, medicalRoomId, client));
			else Task.Run(() => Pull(entityId, client));
		}
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] remote pull demand failed: " + SecretRedactor.Redact(exception.Message));
		}
	}

	private static void Pull(long gridEntityId, ulong client)
	{
		try
		{
			// The demanded id is a grid id: the remote-control take and the remote terminal request the grid
			// replicable. A grid another node antenna-publishes has a proxy here, which authorizes the pull (the
			// medical screen's spawn point preview goes to Preview instead). The demand is edge-triggered by a
			// single client packet while the publication takes a few capture cycles to propagate after a
			// handover, so the resolution polls instead of sampling once.
			string origin = null;
			for (int attempt = 0; origin == null && attempt < 30; attempt++)
			{
				if (attempt > 0) System.Threading.Thread.Sleep(1000);
				foreach (AntennaGraphEntry entry in AntennaGraphService.Entries())
					if (entry.GridEntityId == gridEntityId && entry.Origin != Node) { origin = entry.Origin; break; }
			}
			if (origin == null)
			{
				Console.WriteLine($"[ClusterPlugin] remote pull grid={gridEntityId} origin=unknown skipped=true"
					+ $" entries={AntennaGraphService.Entries().Length}");
				return;
			}
			(_, string control) = SpawnDirectorService.ResolveNodeControl(origin);
			string payload = Gateway.GetStringAsync(control.TrimEnd('/') + "/cluster/partition-of?entity=" + gridEntityId)
				.GetAwaiter().GetResult();
			ulong partition = System.Text.Json.JsonSerializer.Deserialize<PartitionOf>(payload)?.partition ?? 0;
			if (partition == 0)
			{
				Console.WriteLine($"[ClusterPlugin] remote pull grid={gridEntityId} origin={origin} partition=0 skipped=true");
				return;
			}
			string registry = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
			using StringContent empty = new StringContent(string.Empty);
			using HttpResponseMessage response = Gateway.PostAsync(
				registry + "/registry/pull-demands?partition=" + partition + "&source=" + Uri.EscapeDataString(origin)
				+ "&target=" + Uri.EscapeDataString(Node) + "&grid=" + gridEntityId + "&client=" + client, empty)
				.GetAwaiter().GetResult();
			string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
			// The gateway answers HTTP 200 for polite rejections (cooldown, ownership) - accepted must
			// reflect the body, not the status, or every rejection logs as an accept.
			bool accepted = response.IsSuccessStatusCode && body.Contains("\"accepted\":true");
			Console.WriteLine($"[ClusterPlugin] remote pull grid={gridEntityId} origin={origin} via=antenna partition={partition}"
				+ $" client={client} accepted={accepted} response={SecretRedactor.Redact(body).Trim()}");
			if (accepted) PushWhenLocal(gridEntityId, client);
		}
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] remote pull failed: " + SecretRedactor.Redact(exception.Message));
		}
	}

	/// <summary>
	/// SE1-0122: the respawn screen previews a spawn point by streaming its grid, and enables the respawn button only
	/// once the grid is there. When the grid lives on another node, the dead client moves to that node
	/// (<see cref="RespawnRegistryService.SwitchForPreview"/>). That leaves everyone else alone, while pulling the
	/// grid here would move a whole station, and the Gateway refuses that whenever someone is aboard. An offline
	/// spawn point is loaded onto this node first. Resolution polls because the registry lags a handover or a load
	/// by a few capture cycles.
	/// </summary>
	private static void Preview(long gridEntityId, long medicalRoomId, ulong client)
	{
		try
		{
			for (int attempt = 0; attempt < PreviewAttempts; attempt++)
			{
				if (attempt > 0) System.Threading.Thread.Sleep(1000);
				ClusterRespawnPoint point = RespawnRegistryService.Points.FirstOrDefault(x => x.RespawnEntityId == medicalRoomId
					&& x.GridEntityId == gridEntityId && x.Working && MaySpawnAt(x, client));
				if (point == null) continue;
				if (OfflinePartitionService.IsOfflineOrigin(point.Origin))
				{
					// Re-asserted now and then: the request is fire-and-forget.
					if (attempt % 15 == 0)
					{
						Console.WriteLine($"[ClusterPlugin] spawn preview grid={gridEntityId} client={client} loads offline partition {point.PartitionId}");
						OfflinePartitionService.RequestLoad(point.PartitionId, Node, "spawn");
					}
					continue;
				}
				if (point.Origin == Node)
				{
					// Loaded here: replay the request the client made while the grid was absent.
					if (!GrantWhenLocal(gridEntityId, medicalRoomId, client)) continue;
					Console.WriteLine($"[ClusterPlugin] spawn preview grid={gridEntityId} client={client} granted locally after={attempt}s");
					return;
				}
				RespawnRegistryService.SwitchForPreview(client, medicalRoomId, point);
				return;
			}
			Console.WriteLine($"[ClusterPlugin] spawn preview grid={gridEntityId} client={client} unresolved=true");
		}
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] spawn preview failed: " + SecretRedactor.Redact(exception.Message));
		}
	}

	private static bool GrantWhenLocal(long gridEntityId, long medicalRoomId, ulong client)
	{
		bool granted = false;
		using var done = new System.Threading.ManualResetEventSlim();
		Sandbox.MySandboxGame.Static.Invoke(() =>
		{
			try
			{
				granted = (Sandbox.Engine.Multiplayer.MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer)
					?.RequestSpawnPreviewForClient(gridEntityId, medicalRoomId, new Endpoint(new EndpointId(client), 0)) == true;
			}
			finally
			{
				done.Set();
			}
		}, "RemotePullService.GrantWhenLocal");
		return done.Wait(TimeSpan.FromSeconds(10)) && granted;
	}

	/// <summary>
	/// The demanding client's replicable request was consumed while the grid was absent: once the
	/// pulled partition lands here, push the grid replicable to that client instead of waiting for
	/// it to ask again.
	/// </summary>
	private static void PushWhenLocal(long gridEntityId, ulong client)
	{
		for (int attempt = 0; attempt < 60; attempt++)
		{
			System.Threading.Thread.Sleep(1000);
			bool present = false;
			using var done = new System.Threading.ManualResetEventSlim();
			Sandbox.MySandboxGame.Static.Invoke(() =>
			{
				try
				{
					if (MyEntities.GetEntityById(gridEntityId) != null)
					{
						present = true;
						(Sandbox.Engine.Multiplayer.MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer)
							?.ForceReplicableForClient(gridEntityId, new Endpoint(new EndpointId(client), 0));
					}
				}
				finally
				{
					done.Set();
				}
			}, "RemotePullService.PushWhenLocal");
			if (!done.Wait(TimeSpan.FromSeconds(10)) || !present) continue;
			Console.WriteLine($"[ClusterPlugin] remote pull push grid={gridEntityId} client={client} after={attempt + 1}s");
			return;
		}
		Console.WriteLine($"[ClusterPlugin] remote pull push timed out grid={gridEntityId} client={client}");
	}

	/// <summary>
	/// The same gate AppendRemoteRespawnPoints applies before listing the point on this player's
	/// medical screen: admins, spawnee-faction points, and friendly-or-shared ownership qualify. A
	/// hostile player must not be able to preview someone else's medbay by requesting its grid id.
	/// </summary>
	private static bool MaySpawnAt(ClusterRespawnPoint point, ulong userId)
	{
		try
		{
			if (point.SetFactionToSpawnee) return true;
			if (MySession.Static?.RemoteAdminSettings.TryGetValue(userId, out AdminSettingsEnum settings) == true
				&& (settings & AdminSettingsEnum.UseTerminals) != 0) return true;
			long identityId = MySession.Static?.Players.TryGetIdentityId(userId) ?? 0;
			return identityId > 0 && MyIDModule.GetRelationPlayerBlock(point.OwnerIdentityId, identityId,
				(MyOwnershipShareModeEnum)point.ShareMode).IsFriendly();
		}
		catch
		{
			return false;
		}
	}

	private sealed record PartitionOf(ulong partition);
}
