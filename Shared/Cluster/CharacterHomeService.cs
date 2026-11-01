using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ClusterProtocol;
using ReapWork = ClusterProtocol.CharacterReapQueue.Work;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// Tell the registry which partition holds each hosted player's character, so a rejoin can be sent to
/// the node that has the body.
/// </summary>
/// <remarks>
/// Clustering §6.4 answers a join with "the node owning the partition containing the client's
/// character (or spawn flow if none)". Nothing durable could say where a character was: the Gateway's
/// client routes are deliberately soft (Gateway §6.3, "re-derived per client at rejoin"), and the
/// derivation had nothing to read. So <c>ResolveClientRoute</c> returned null for anyone who had been
/// offline across a restart and the join fell back to the default backend.
///
/// On that node, <c>MySpaceRespawnComponent.TryFindExistingCharacter</c> walks the identity's
/// <c>SavedCharacters</c> and <c>MyEntities.GetEntityById</c> - both node-local. A body sitting on
/// another node is invisible to both, so the player is given a fresh one and the old body stays in the
/// world holding their inventory. Two bodies for one identity, and the client then picks between them
/// inconsistently: control on one, camera on the other, which is what "first person was stuck and
/// third person orbited a copy of me" was.
///
/// Reporting the living player is enough to fix the offline case, because the last report is what
/// persists. The partition id is the right key to store rather than the node: ids outlive handovers -
/// ownership moves, the id does not - and partition ownership is itself durable, so the registry can
/// still resolve a node from it after any number of transfers or a Gateway restart.
///
/// ponytail: a poll rather than an event. There is no single "the player's body changed partition"
/// callback - it happens on respawn, on handover, on teleport and on partition splits - and the
/// registry write is skipped when nothing changed, so the steady state costs one comparison a second.
/// </remarks>
public static class CharacterHomeService
{
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	private static readonly HttpClient Client = AuthorizedClient(TimeSpan.FromSeconds(10));
	// T-0350: the report is a synchronous request per online player on the game thread; 3 s bounds the one attempt
	// that finds the registry gone (it then sets the hold and the pass stops).
	private static readonly HttpClient ReportClient = AuthorizedClient(TimeSpan.FromSeconds(3));
	private static double m_nextReport;
	private static readonly Dictionary<ulong, (long Version, long Revision)> HomeVersions = new();

	// /registry/character-homes is a Registry route: on a token-protected cluster (every managed
	// deployment) it answers 401 without the join token, so no home was ever recorded there and a
	// rejoin after a restart could not find the body. The bench runs without a token and never saw it.
	private static HttpClient AuthorizedClient(TimeSpan timeout)
	{
		HttpClient client = ClusterHttp.Client(timeout);
		ClusterRuntime.AuthorizeRegistryClient(client);
		return client;
	}

	// T-0237: bodies of players who are not online, checked against the registry's home.
	private const double StaleCheckMilliseconds = 30000;
	private static double m_nextStaleCheck;
	private static Task m_staleLookup;
	private static readonly CharacterReapQueue Reaps = new();
	// A body with no recorded home (an NPC-like identity, a player who never reported one) is kept, and asking
	// again every 30 s only fills the gateway log with its 400 - so the answer is remembered for 10 minutes.
	private const double NoHomeRecheckMilliseconds = 600000;
	private static readonly ConcurrentDictionary<long, double> NoHomeUntil = new();
	// T-0237: character entity -> the home version the registry answered for it and the partition that report
	// named. A save stamps each character it carries with this version - but only when the partition matches,
	// so a body that arrived here and is not yet re-reported is never stamped with its PREVIOUS home's version.
	private static readonly ConcurrentDictionary<long, (long Version, ulong Partition)> ReportedHomes = new();

	/// <summary>
	/// T-0237: "entity:version,..." for every character in <paramref name="entities"/> (standing, or seated in a
	/// cockpit) whose last home report named <paramref name="partition"/>. The registry orders a recovered copy
	/// against the character's home by these versions. Empty when none: a stamped save with no stamp for a
	/// character never overrides that character's home.
	/// </summary>
	internal static string HomeStamps(ulong partition, IEnumerable<MyObjectBuilder_EntityBase> entities)
	{
		var stamps = new List<string>();
		void Stamp(long entity)
		{
			if (ReportedHomes.TryGetValue(entity, out var home) && home.Partition == partition)
				stamps.Add(entity + ":" + home.Version);
		}
		foreach (MyObjectBuilder_EntityBase entity in entities)
		{
			if (entity is MyObjectBuilder_Character character) Stamp(character.EntityId);
			else if (entity is MyObjectBuilder_CubeGrid grid && grid.CubeBlocks != null)
				foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
					if (block is MyObjectBuilder_Cockpit { Pilot: { } pilot }) Stamp(pilot.EntityId);
		}
		return string.Join(",", stamps);
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		ReapStaleBodies();
		// T-0350: no report pass while the registry is unreachable. Nothing is lost: a report carries the CURRENT home
		// of every online player and is sent every pass whether or not it changed (there is no "changed" set to lose;
		// HomeVersions moves only on an answer), and m_nextReport is not advanced here, so the first tick after the
		// registry answers again reports them all.
		if (ClusterRuntime.RegistryUnreachable) return;
		if (ClusterRuntime.LocalMilliseconds < m_nextReport) return;
		m_nextReport = ClusterRuntime.LocalMilliseconds + 5000;
		string registry = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		if (string.IsNullOrEmpty(registry) || string.IsNullOrEmpty(Node)) return;
		MyPlayer[] online = MySession.Static.Players.GetOnlinePlayers().ToArray();
		var clients = online.Select(player => player.Id.SteamId).ToHashSet();
		foreach (ulong gone in HomeVersions.Keys.Where(client => !clients.Contains(client)).ToArray()) HomeVersions.Remove(gone);
		foreach (MyPlayer player in online)
		{
			try { Report(registry, player); }
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] character home report failed client=" + player?.Id.SteamId
					+ " error=" + SecretRedactor.Redact(exception.Message));
				// T-0350: no answer - set the hold and stop; every further player would wait out the same timeout.
				ClusterRuntime.NoteRegistryFailure(exception);
				if (ClusterRuntime.RegistryUnreachable) break;
			}
		}
	}

	/// <summary>T-0305: an asynchronous home answer is an observation, never permission to close.</summary>
	private static void ReapStaleBodies()
	{
		string registry = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		if (string.IsNullOrEmpty(registry) || string.IsNullOrEmpty(Node)) return;
		while (Reaps.TryTake(out ReapWork work))
		{
			CharacterReapPermit permit = work.Permit;
			try
			{
				MyCharacter character = PlayerStateService.FindCharacter(permit.Entity);
				ulong partition = character == null ? 0 : CharacterPartition(character);
				bool offline = character != null && character.GetPlayerIdentityId() == permit.Identity
					&& IsOfflineBody(character, out ulong client) && client == work.ClientId;
				ExecuteAuthorizedReap(permit, Node, ClusterRuntime.RegistryNodeEpoch, partition,
					ClusterControlServer.OwnedGeneration(partition), offline, () =>
					{
						object result = PlayerStateService.CloseUnclaimedCharacter(permit.Entity, force: true);
						Console.WriteLine("[ClusterPlugin] authorized stale body reap client=" + work.ClientId
							+ " entity=" + permit.Entity + " operation=" + permit.Operation + "; " + JsonSerializer.Serialize(result));
					});
			}
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] character reap execution failed: " + SecretRedactor.Redact(exception.Message));
			}
			finally { work.FinishExecution(); }
			// Completion and refusal both resolve the consumed permission. No network work on the game thread.
			ReapWork completedWork = work;
			_ = Task.Run(() => ResolveReap(registry, completedWork));
		}
		if (ClusterRuntime.LocalMilliseconds < m_nextStaleCheck || m_staleLookup is { IsCompleted: false }) return;
		if (!Reaps.FinishLookup()) return; // producer may have queued its final reply after the drain
		m_nextStaleCheck = ClusterRuntime.LocalMilliseconds + StaleCheckMilliseconds;
		double now = ClusterRuntime.LocalMilliseconds;
		foreach (long forgotten in NoHomeUntil.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) NoHomeUntil.TryRemove(forgotten, out _);
		var candidates = new List<(long Entity, long Identity, ulong Client, ulong Partition, long Generation)>();
		IEnumerable<MyCharacter> seated = MyEntities.GetEntities().OfType<MyCubeGrid>()
			.SelectMany(grid => grid.GetFatBlocks<MyCockpit>()).Select(cockpit => cockpit.Pilot).Where(pilot => pilot != null);
		foreach (MyCharacter character in MyEntities.GetEntities().OfType<MyCharacter>().Concat(seated).Distinct())
		{
			if (NoHomeUntil.ContainsKey(character.EntityId) || !IsOfflineBody(character, out ulong client)) continue;
			ulong partition = CharacterPartition(character);
			long generation = ClusterControlServer.OwnedGeneration(partition);
			if (generation != 0) candidates.Add((character.EntityId, character.GetPlayerIdentityId(), client, partition, generation));
		}
		long epoch = ClusterRuntime.RegistryNodeEpoch;
		Dictionary<ulong, long> owned = ClusterControlServer.OwnedSnapshot();
		m_staleLookup = Task.Run(() =>
		{
			foreach (ReapWork unresolved in Reaps.Unresolved())
				ResolveReap(registry, unresolved);
			// Recover pending permissions after a node/Registry restart, including absent candidates
			// and retired partitions whose lineage now assigns the candidate to this node.
			try
			{
				using HttpResponseMessage response = Client.GetAsync(registry + "/registry/character-reaps?node="
					+ Uri.EscapeDataString(Node) + "&epoch=" + epoch).GetAwaiter().GetResult();
				response.EnsureSuccessStatusCode();
				using JsonDocument pending = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
				foreach (JsonElement row in pending.RootElement.EnumerateArray())
				{
					ulong client = row.GetProperty("client").GetUInt64();
					if (Reaps.Contains(client)) continue;
					CharacterReapPermit permit = row.GetProperty("permit").Deserialize<CharacterReapPermit>();
					if (permit.Node == Node && permit.Epoch == epoch) QueueReap(client, permit);
					else if (owned.TryGetValue(row.GetProperty("partition").GetUInt64(), out long generation))
						AuthorizeReap(registry, client, permit.Identity, permit.Entity, row.GetProperty("partition").GetUInt64(),
							generation, epoch, row.GetProperty("homeVersion").GetInt64(), row.GetProperty("writeRevision").GetInt64());
				}
			}
			catch (Exception exception) { ReapFailure("pending", exception); }
			foreach (var group in candidates.GroupBy(candidate => candidate.Client))
			{
				if (Reaps.Contains(group.Key)) continue;
				try
				{
					using HttpResponseMessage response = Client.GetAsync(registry + "/registry/character-homes?client=" + group.Key)
						.GetAwaiter().GetResult();
					if (response.StatusCode == HttpStatusCode.BadRequest)
					{
						foreach (var candidate in group) NoHomeUntil[candidate.Entity] = now + NoHomeRecheckMilliseconds;
						continue;
					}
					response.EnsureSuccessStatusCode();
					using JsonDocument home = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
					long current = home.RootElement.GetProperty("Entity").GetInt64();
					var stale = group.FirstOrDefault(candidate => candidate.Entity != current);
					if (current != 0 && stale.Entity != 0)
						AuthorizeReap(registry, stale.Client, stale.Identity, stale.Entity, stale.Partition, stale.Generation, epoch,
							home.RootElement.GetProperty("Version").GetInt64(), home.RootElement.GetProperty("WriteRevision").GetInt64());
				}
				catch (Exception exception) { ReapFailure("lookup", exception); }
			}
		});
	}

	internal static bool ExecuteAuthorizedReap(CharacterReapPermit permit, string node, long epoch,
		ulong partition, long generation, bool offline, Action close)
	{
		if (!offline || permit.Operation == Guid.Empty || permit.Node != node || permit.Epoch != epoch
			|| permit.Partition != partition || permit.Generation != generation || generation == 0) return false;
		close();
		return true;
	}

	private static ulong CharacterPartition(MyCharacter character)
	{
		ulong partition = ClusterControlServer.StandingPartitionOfEntity(character.EntityId);
		if (partition == 0 && character.GetTopMostParent() is { } top && top != character)
			partition = ClusterControlServer.StandingPartitionOfEntity(top.EntityId);
		return partition;
	}

	private static void QueueReap(ulong client, CharacterReapPermit permit)
	{
        Reaps.Enqueue(client, permit);
	}

	private static void AuthorizeReap(string registry, ulong client, long identity, long entity,
		ulong partition, long generation, long epoch, long homeVersion, long writeRevision)
	{
		string query = $"?client={client}&identity={identity}&entity={entity}&node={Uri.EscapeDataString(Node)}"
			+ $"&epoch={epoch}&partition={partition}&generation={generation}&homeVersion={homeVersion}&writeRevision={writeRevision}";
		using var empty = new StringContent(string.Empty);
		using HttpResponseMessage response = Client.PostAsync(registry + "/registry/character-reaps" + query, empty).GetAwaiter().GetResult();
		if (response.StatusCode == HttpStatusCode.Conflict) return; // observation changed: keep the body
		response.EnsureSuccessStatusCode();
		QueueReap(client, JsonSerializer.Deserialize<CharacterReapPermit>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
	}

	private static void ResolveReap(string registry, ReapWork work)
	{
		if (work == null || !work.ReadyForResolve) return;
		try
		{
			using var body = new StringContent(JsonSerializer.Serialize(work.Permit), System.Text.Encoding.UTF8, "application/json");
			using HttpResponseMessage response = Client.PostAsync(registry + "/registry/character-reaps/resolve?client=" + work.ClientId, body)
				.GetAwaiter().GetResult();
			if (response.StatusCode != HttpStatusCode.Conflict) response.EnsureSuccessStatusCode();
			// A takeover conflict also means this old local operation must never execute again.
			Reaps.Resolved(work);
		}
		catch (Exception exception) { ReapFailure("resolve", exception); }
	}

	private static void ReapFailure(string phase, Exception exception) => Console.WriteLine(
		"[ClusterPlugin] character reap " + phase + " failed: " + SecretRedactor.Redact(exception.Message));

	/// <summary>A living, uncontrolled body (standing or seated) of a player (a Steam id) who is not online here and not joining.</summary>
	private static bool IsOfflineBody(MyCharacter character, out ulong client)
	{
		client = 0;
		MyEntity top = character.GetTopMostParent() ?? character;
		if (character.MarkedForClose || top.MarkedForClose || !top.InScene || character.IsDead) return false;
		if ((character.ControllerInfo?.ControllingIdentityId ?? 0) != 0) return false;
		if (ClusterRuntime.IsFrozen(top.EntityId)) return false;
		long identity = character.GetPlayerIdentityId();
		if (identity == 0 || MyPlayerCollectionPatch.AttachedIdentities().Contains(identity)) return false;
		var players = MySession.Static.Players;
		if (players.TryGetPlayerId(identity, out MyPlayer.PlayerId id) && players.IsPlayerOnline(ref id)) return false;
		client = players.TryGetSteamId(identity);
		if (client == 0) return false;
		// Online on ANOTHER node: the World Authority's election already settles a second body against the
		// registered one, and the home can trail a fresh respawn by one report (5 s) - a body arriving here by
		// handover inside that window is the CURRENT one. w32 2026-09-24 21:35:15, pilot-73: a duplicate was
		// reaped correctly on node-4 one second before its player's handover landed there; the same timing
		// with a stale home would have reaped the live body. Only a player offline cluster-wide is judged here.
		ulong steam = client;
		if (OnlinePlayersService.Cluster.Any(entry => entry.IdentityId == identity || entry.SteamId == steam)) return false;
		return true;
	}

	private static void Report(string registry, MyPlayer player)
	{
		if (player?.Identity == null || player.Id.SteamId == 0) return;
		if (player.Character is not MyCharacter character || character.MarkedForClose || character.IsDead) return;
		// A seated body is nested in its cockpit and is no standing-layout member of its own: the
		// holder is the grid it sits in. Asking for the character alone answered 0 for the whole
		// flight, so the home froze at the pre-boarding partition, the boarding merge retired that
		// id, and the rejoin after the next restart could not find the body (w29 2026-09-07
		// pilot-10, T-0065) - the registry then spawned a second one on the default backend.
		ulong partition = ClusterControlServer.StandingPartitionOfEntity(character.EntityId);
		if (partition == 0 && character.GetTopMostParent() is { } top && top != character)
			partition = ClusterControlServer.StandingPartitionOfEntity(top.EntityId);
		uint generation = ClusterControlServer.OwnedGeneration(partition);
		long epoch = ClusterRuntime.RegistryNodeEpoch;
		if (partition == 0 || generation == 0 || epoch == 0) return;
		// Bootstrap with zero; a conflict returns the current version. Capture a fresh body
		// on the next update rather than retrying this snapshot with a newly learned token.
		HomeVersions.TryGetValue(player.Id.SteamId, out var expected);
		string query = $"?client={player.Id.SteamId}&identity={player.Identity.IdentityId}"
			+ $"&entity={character.EntityId}&partition={partition}&node={Uri.EscapeDataString(Node)}"
			+ $"&epoch={epoch}&generation={generation}&expectedVersion={expected.Version}&expectedWriteRevision={expected.Revision}";
		using StringContent empty = new StringContent(string.Empty);
		using HttpResponseMessage response = ReportClient.PostAsync(registry + "/registry/character-homes" + query, empty)
			.GetAwaiter().GetResult();
		if (response.StatusCode == HttpStatusCode.Conflict)
		{
			using JsonDocument conflict = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
			HomeVersions[player.Id.SteamId] = (conflict.RootElement.GetProperty("version").GetInt64(),
				conflict.RootElement.GetProperty("writeRevision").GetInt64());
			return;
		}
		response.EnsureSuccessStatusCode();
		using JsonDocument stored = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
		if (stored.RootElement.TryGetProperty("version", out JsonElement version) && version.GetInt64() > 0)
		{
			HomeVersions[player.Id.SteamId] = (version.GetInt64(), stored.RootElement.GetProperty("writeRevision").GetInt64());
			ReportedHomes[character.EntityId] = (version.GetInt64(), partition);
		}
	}
}
