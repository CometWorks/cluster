using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>Only an authenticated client's explicit replicable request can wake a radio grid.
/// Relay enumeration, HUD publication and IGC never enter this path.</summary>
public static class RemotePullService
{
	private static readonly object Sync = new();
	private static readonly Dictionary<(Endpoint, long), Demand> Pending = new();
	private static readonly Dictionary<(ulong, long), long> LastDemand = new();
	private static readonly Dictionary<ulong, Queue<long>> WakeTimes = new();
	private static readonly Dictionary<string, long> Counters = new();
	private static readonly HttpClient Gateway = ClusterHttp.Client(TimeSpan.FromSeconds(120));
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	private sealed class Demand
	{
		public Endpoint Sender;
		public long Entity, Medical, Interacted;
		public byte Flags;
		public bool Cancelled, Offline;
		public long Started = Environment.TickCount64, LocalSince;
	}

	internal static void Install()
	{
		ClusterRuntime.AuthorizeRegistryClient(Gateway);
		ClusterReplicationDemand.Request = Request;
	}

	private static void Count(string outcome)
	{
		lock (Sync) Counters[outcome] = Counters.GetValueOrDefault(outcome) + 1;
	}

	internal static object Status()
	{
		lock (Sync) return new { pending = Pending.Count, outcomes = new Dictionary<string, long>(Counters) };
	}

	internal static (ulong User, long Entity)[] WaitingUsers()
	{
		lock (Sync) return Pending.Values.Where(p => !p.Cancelled && p.Medical == 0 && p.Offline)
			.Select(p => (p.Sender.Id.Value, p.Entity)).ToArray();
	}

	private static void Request(Endpoint sender, long entity, bool add, long medical, long interacted, byte flags)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority || entity == 0) return;
		lock (Sync)
		{
			var key = (sender, entity);
			if (!add)
			{
				if (Pending.Remove(key, out Demand cancelled)) cancelled.Cancelled = true;
				return;
			}
			if (Pending.ContainsKey(key)) return;
			if (Pending.Values.Any(x => x.Sender.Id.Value == sender.Id.Value)) { Count("refused-rate"); return; }
			long now = Environment.TickCount64;
			var cooldownKey = (sender.Id.Value, entity);
			if (LastDemand.TryGetValue(cooldownKey, out long last) && now - last < 10000) return;
			foreach (var old in LastDemand.Where(x => now - x.Value >= 60000).Select(x => x.Key).ToArray()) LastDemand.Remove(old);
			LastDemand[cooldownKey] = now;
			Demand demand = new() { Sender = sender, Entity = entity, Medical = medical, Interacted = interacted, Flags = flags };
			Pending[key] = demand;
			_ = Run(demand);
		}
	}

	// Every read of the engine and the final cancellation check/replay run together on the game thread.
	private static async Task<T> OnGame<T>(Demand demand, Func<T> action)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		Sandbox.MySandboxGame.Static.Invoke(() =>
		{
			try
			{
				lock (Sync)
				{
					if (demand.Cancelled || !Pending.TryGetValue((demand.Sender, demand.Entity), out var current) || current != demand
						|| MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer server
						|| !server.m_clientStates.ContainsKey(demand.Sender)) throw new OperationCanceledException();
					completion.TrySetResult(action());
				}
			}
			catch (Exception e) { completion.TrySetException(e); }
		}, "RemotePullService");
		return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}

	private static async Task Run(Demand demand)
	{
		try
		{
			int seconds = Math.Clamp(ClusterConfig.GetInt("offline.callUpSeconds"), 1, 1200);
			long nextLoad = 0, nextNotice = demand.Started + ClusterConfig.GetInt("offline.callUpNoticeSeconds") * 1000L;
			bool pulling = false;
			while (Environment.TickCount64 - demand.Started < seconds * 1000L)
			{
				int replay = await OnGame(demand, () =>
				{
					var server = (MyReplicationServer)MyMultiplayer.Static.ReplicationLayer;
					if (ClusterRuntime.IsFrozen(demand.Entity) || server.m_callback.GetReplicableByEntityId(demand.Entity) == null) return 0;
					return server.ReplayRequest(demand.Entity, demand.Medical, demand.Interacted, demand.Flags, demand.Sender) ? 1 : 2;
				});
				if (replay == 1) { Count("accepted"); return; }
				if (replay == 2)
				{
					if (demand.LocalSince == 0) demand.LocalSince = Environment.TickCount64;
					// A newly registered grid's antenna receivers can lag its replicable by Update100.
					if (demand.Medical == 0 && Environment.TickCount64 - demand.LocalSince >= 3000)
					{ Count("refused-rights"); await Notice(demand, true); return; }
				}
				AntennaGraphEntry entry = AntennaGraphService.Entries().FirstOrDefault(x => x.GridEntityId == demand.Entity && x.Origin != Node);
				if (demand.Medical != 0)
				{
					ClusterRespawnPoint point = await OnGame(demand, () => RespawnRegistryService.Points.FirstOrDefault(x =>
						x.RespawnEntityId == demand.Medical && x.GridEntityId == demand.Entity && x.Working && MaySpawnAt(x, demand.Sender.Id.Value)));
					if (point != null && !OfflinePartitionService.IsOfflineOrigin(point.Origin) && point.Origin != Node)
					{
						await OnGame(demand, () => { RespawnRegistryService.SwitchForPreview(demand.Sender.Id.Value, demand.Medical, point); return true; });
						return;
					}
					if (point != null && OfflinePartitionService.IsOfflineOrigin(point.Origin) && Environment.TickCount64 >= nextLoad)
					{
						await OnGame(demand, () => { OfflinePartitionService.RequestLoad(point.PartitionId, Node, "spawn"); return true; });
						nextLoad = Environment.TickCount64 + 15000;
					}
				}
				else if (entry != null && OfflinePartitionService.IsOfflineOrigin(entry.Origin))
				{
					if (!demand.Offline)
					{
						string refusal = await OnGame(demand, () => Admit(demand, entry));
						if (refusal != null) { Count(refusal); await Notice(demand, true); return; }
					}
					if (Environment.TickCount64 >= nextLoad)
					{
						// Revalidate reach/access on every renewed demand: a stale click is not an authorization lease.
						bool reachable = await OnGame(demand, () => CanReach(demand.Sender.Id.Value, entry));
						if (!reachable) { Count("refused-reach"); await Notice(demand, true); return; }
						await OnGame(demand, () => { OfflinePartitionService.RequestLoad(OfflinePartitionService.PartitionOfOrigin(entry.Origin), Node, "remote"); return true; });
						nextLoad = Environment.TickCount64 + 15000;
					}
				}
				else if (demand.Medical == 0 && entry != null && !pulling)
				{
					pulling = true;
					string refusal = await Pull(demand, entry.Origin);
					if (refusal != null) { Count(refusal); await Notice(demand, true); return; }
				}
				if (demand.Medical == 0 && Environment.TickCount64 >= nextNotice)
				{
					await Notice(demand, false);
					nextNotice = Environment.TickCount64 + 10000;
				}
				await Task.Delay(1000);
			}
			Count("timeout");
			if (demand.Medical == 0) await Notice(demand, true);
		}
		catch (OperationCanceledException) { Count("cancelled"); }
		catch (Exception e)
		{
			Count("failed");
			Console.WriteLine("[ClusterPlugin] remote request failed: " + SecretRedactor.Redact(e.Message));
			try { if (demand.Medical == 0) await Notice(demand, true); } catch (Exception) { /* client left or game thread unavailable */ }
		}
		finally
		{
			lock (Sync)
			{
				demand.Cancelled = true;
				if (Pending.TryGetValue((demand.Sender, demand.Entity), out var current) && current == demand)
					Pending.Remove((demand.Sender, demand.Entity));
			}
		}
	}

	private static string Admit(Demand demand, AntennaGraphEntry entry)
	{
		if (!ClusterConfig.GetBool("offline.callUp")) return "disabled";
		if (!CanReach(demand.Sender.Id.Value, entry)) return "refused-reach";
		lock (Sync)
		{
			ulong user = demand.Sender.Id.Value;
			if (Pending.Values.Any(x => x != demand && x.Sender.Id.Value == user && x.Offline)) return "refused-rate";
			if (!WakeTimes.TryGetValue(user, out var times)) WakeTimes[user] = times = new();
			long now = Environment.TickCount64;
			while (times.Count > 0 && now - times.Peek() >= 60000) times.Dequeue();
			if (times.Count >= ClusterConfig.GetInt("offline.callUpPerMinute")) return "refused-rate";
			times.Enqueue(now);
			demand.Offline = true;
			foreach (ulong old in WakeTimes.Where(x => x.Value.Count == 0 || now - x.Value.Last() >= 60000).Select(x => x.Key).ToArray()) WakeTimes.Remove(old);
			return null;
		}
	}

	internal static bool CanReach(ulong user, AntennaGraphEntry entry)
	{
		if (PromotionService.IsModerator(user)) return true;
		MySession.Static.Players.TryGetPlayerBySteamId(user, out MyPlayer player);
		if (player?.Identity == null) return false;
		var origins = new List<MyEntity>();
		if (player.Character != null) origins.Add(player.Character);
		if (player.Controller?.ControlledEntity?.Entity is MyEntity controlled) origins.Add(controlled);
		foreach (var broadcaster in MyAntennaSystem.Static.GetAllRelayedBroadcasters(origins, player.Identity.IdentityId, mutual: true))
		{
			if (!broadcaster.CanBeUsedByPlayer(player.Identity.IdentityId)) continue;
			var reachable = AntennaGraphService.Entries().FirstOrDefault(e => e.GridEntityId == entry.GridEntityId
				&& e.Origin == entry.Origin && e.AntennaEntityId == broadcaster.AntennaEntityId);
			if (reachable == null) continue;
			var proxy = AntennaGraphCodec.ReadBuilderShared(reachable);
			if (MyIDModule.GetRelationPlayerBlock(proxy.Owner, player.Identity.IdentityId, proxy.Share).IsFriendly()
				|| proxy.HasRemote && MyIDModule.GetRelationPlayerBlock(proxy.MainRemoteOwner ?? 0, player.Identity.IdentityId, proxy.MainRemoteSharing).IsFriendly()) return true;
		}
		return false;
	}

	private static Task<bool> Notice(Demand demand, bool failed) => OnGame(demand, () =>
	{
		long identity = MySession.Static.Players.TryGetIdentityId(demand.Sender.Id.Value);
		if (identity != 0) MyVisualScriptLogicProvider.ShowNotification(failed ? "Remote connection failed" : "Establishing remote connection...", 5000, failed ? "Red" : "White", identity);
		return true;
	});

	private static async Task<string> Pull(Demand demand, string origin)
	{
		(_, string control) = SpawnDirectorService.ResolveNodeControl(origin);
		string payload = await Gateway.GetStringAsync(control.TrimEnd('/') + "/cluster/partition-of?entity=" + demand.Entity);
		ulong partition = JsonSerializer.Deserialize<PartitionOf>(payload)?.partition ?? 0;
		if (partition == 0) return "partition-unknown";
		// Cancellation can stop before dispatch. A handover already accepted by the gateway completes normally.
		await OnGame(demand, () => true);
		string registry = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		using var empty = new StringContent(string.Empty);
		using var response = await Gateway.PostAsync(registry + "/registry/pull-demands?partition=" + partition
			+ "&source=" + Uri.EscapeDataString(origin) + "&target=" + Uri.EscapeDataString(Node)
			+ "&grid=" + demand.Entity + "&client=" + demand.Sender.Id.Value, empty);
		using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		bool accepted = response.IsSuccessStatusCode && answer.RootElement.GetProperty("accepted").GetBoolean();
		Console.WriteLine($"[ClusterPlugin] remote pull grid={demand.Entity} origin={origin} via=antenna partition={partition} client={demand.Sender.Id.Value} accepted={accepted}");
		return accepted ? null
			: answer.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : "refused";
	}

	private static bool MaySpawnAt(ClusterRespawnPoint point, ulong user)
	{
		if (point.SetFactionToSpawnee || PromotionService.IsModerator(user)) return true;
		if (MySession.Static.RemoteAdminSettings.TryGetValue(user, out AdminSettingsEnum settings) && (settings & AdminSettingsEnum.UseTerminals) != 0) return true;
		long identity = MySession.Static.Players.TryGetIdentityId(user);
		return identity > 0 && MyIDModule.GetRelationPlayerBlock(point.OwnerIdentityId, identity, (MyOwnershipShareModeEnum)point.ShareMode).IsFriendly();
	}

	private sealed record PartitionOf(ulong partition);
}
