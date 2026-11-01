using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// WA-3: global spawners run only on the World Authority, which hosts no clients, so anything that picks targets
/// from the local online-player list decides nothing in a cluster. The director takes cluster-wide positions from
/// <see cref="PositionRegistryService"/>, resolves the node that owns the target player through the Gateway
/// registry, and dispatches the spawn there. The WA owns no partitions, so it must never spawn locally.
/// </summary>
public static class SpawnDirectorService
{
	private static readonly object Sync = new object();
	private static readonly HttpClient Gateway = ClusterHttp.Client(TimeSpan.FromSeconds(30));
	private static readonly Dictionary<Guid, ClusterSpawnResult> Executions = new();
	private static readonly Dictionary<Guid, double> PendingDeadlines = new();
	private static readonly Queue<Guid> ExecutionOrder = new();
	private static readonly ClusterSpawnResult PendingResult = new(false, Array.Empty<long>(), null, Pending: true);
	private static readonly ClusterSpawnResult UnknownResult = new(false, Array.Empty<long>(), "unknown operation");
	private const double PendingTimeoutMilliseconds = 30000;
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	private static readonly Dictionary<string, (string Control, double Expires)> NodeControlCache = new(StringComparer.Ordinal);
	private const double NodeControlCacheMilliseconds = 5000;

	internal static void Install() => ClusterRuntime.AuthorizeRegistryClient(Gateway);

	/// <summary>
	/// Picks a cluster-wide target for a spawn triggered "near a player". Returns null when no player is online
	/// anywhere, which is the correct no-op rather than spawning at the origin.
	/// </summary>
	public static ClusterPlayerPosition SelectTarget(Func<ClusterPlayerPosition, bool> eligible = null)
	{
		IReadOnlyList<ClusterPlayerPosition> players = PositionRegistryService.Players;
		// ClientId 0 entries are demand-only positions (viewed cameras), never spawn targets.
		return players.FirstOrDefault(player => player.ClientId != 0 && (eligible == null || eligible(player)));
	}

	/// <summary>
	/// Dispatches a decided spawn from the World Authority to the node owning the target client's partition.
	/// Blocking by design: it runs from the WA's authoritative spawn tick, mirroring the respawn arbitration path.
	/// </summary>
	public static ClusterSpawnResult Dispatch(ClusterSpawnRequest request)
	{
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority)
			throw new InvalidOperationException("Spawn dispatch must originate at the World Authority");
		SpawnDirectorCodec.ValidateDecision(request);
		(string node, string control) = ResolveOwner(request.TargetClient);
		using StringContent content = new StringContent(SpawnDirectorCodec.WriteRequest(request with { Origin = node }),
			Encoding.UTF8, "application/json");
		using HttpResponseMessage response = Gateway.PostAsync(control.TrimEnd('/') + "/cluster/spawn-execute", content)
			.GetAwaiter().GetResult();
		string payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException("Spawn dispatch failed: " + SecretRedactor.Redact(payload));
		return JsonSerializer.Deserialize<ClusterSpawnResult>(payload)
			?? throw new InvalidOperationException("Owning node returned an empty spawn result");
	}

	/// <summary>Executes a dispatched spawn on the owning node, exactly once per operation id.</summary>
	/// <remarks>
	/// Prefab spawning is asynchronous: <c>MyPrefabManager.SpawnPrefabInternal</c> hands the work to
	/// <c>Parallel.Start</c> and fills the result list from its completion callback, so reading the list on
	/// return always sees an empty one. The operation is therefore recorded when that callback fires, and this
	/// method reports it as pending until then. Callers poll <see cref="Result"/>.
	/// </remarks>
	public static ClusterSpawnResult Execute(ClusterSpawnRequest request)
	{
		SpawnDirectorCodec.Validate(request);
		if (request.Origin != Node)
			throw new InvalidOperationException("Spawn was dispatched to the wrong node");
		lock (Sync)
		{
			if (Executions.TryGetValue(request.OperationId, out ClusterSpawnResult replay)) return replay;
			if (PendingDeadlines.ContainsKey(request.OperationId)) return PendingResult;
			PendingDeadlines[request.OperationId] = ClusterRuntime.LocalMilliseconds + PendingTimeoutMilliseconds;
		}
		var grids = new List<MyCubeGrid>();
		try
		{
			// Pushed before the spawn so the manager's own callbacks sit above it and run first; the stack is
			// popped, so this one completes the operation only after the grids are registered and configured.
			var callbacks = new Stack<Action>();
			callbacks.Push(() => Complete(request.OperationId, grids, null));
			MyPrefabManager.Static.SpawnPrefab(grids, request.PrefabName,
				new Vector3D(request.X, request.Y, request.Z), Vector3.Forward, Vector3.Up,
				beaconName: request.BeaconName, ownerId: request.OwnerId, updateSync: true, callbacks: callbacks);
		}
		catch (Exception exception)
		{
			Complete(request.OperationId, grids, SecretRedactor.Redact(exception.Message));
		}
		lock (Sync)
			return Executions.TryGetValue(request.OperationId, out ClusterSpawnResult settled) ? settled : PendingResult;
	}

	/// <summary>Reports a dispatched operation's outcome, or a pending result while the spawn is still running.</summary>
	public static ClusterSpawnResult Result(Guid operationId)
	{
		lock (Sync)
		{
			if (Executions.TryGetValue(operationId, out ClusterSpawnResult recorded)) return recorded;
			return PendingDeadlines.ContainsKey(operationId) ? PendingResult : UnknownResult;
		}
	}

	/// <summary>
	/// Resolves spawns whose completion callback never arrived. <c>OnGridsCreated</c> returns early without
	/// running any callback when grid creation failed outright, so without this an operation could stay pending
	/// forever and a retry would never be allowed to record a different outcome.
	/// </summary>
	public static void Update()
	{
		if (!ClusterRuntime.IsClustered) return;
		Guid[] expired;
		lock (Sync)
		{
			if (PendingDeadlines.Count == 0) return;
			double now = ClusterRuntime.LocalMilliseconds;
			expired = PendingDeadlines.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray();
		}
		foreach (Guid operationId in expired)
			Record(operationId, new ClusterSpawnResult(false, Array.Empty<long>(), "spawn did not complete"));
	}

	private static void Complete(Guid operationId, List<MyCubeGrid> grids, string error)
	{
		long[] entityIds = grids.Where(grid => grid != null).Select(grid => grid.EntityId).ToArray();
		Record(operationId, new ClusterSpawnResult(error == null && entityIds.Length > 0, entityIds, error));
	}

	private static void Record(Guid operationId, ClusterSpawnResult result)
	{
		lock (Sync)
		{
			PendingDeadlines.Remove(operationId);
			// Record failures too: a dispatch retry must not spawn a second copy just because the first partly ran.
			if (Executions.TryAdd(operationId, result))
			{
				ExecutionOrder.Enqueue(operationId);
				if (ExecutionOrder.Count > 1024) Executions.Remove(ExecutionOrder.Dequeue());
			}
		}
		Console.WriteLine("[ClusterPlugin] spawn execute operation=" + operationId
			+ " success=" + result.Success + " grids=" + result.GridEntityIds.Length
			+ " error=" + (result.Error ?? "none"));
	}

	public static object Status()
	{
		lock (Sync) return new { node = Node, executions = Executions.Count, pending = PendingDeadlines.Count, players = PositionRegistryService.Players.Count };
	}

	/// <summary>
	/// Names prefabs the definitions really carry, so a dispatch gate can assert that a grid actually appears
	/// rather than only that routing and idempotence work.
	/// Deliberately reads nothing but the keys: <see cref="MyPrefabDefinition.CubeGrids"/> lazily reloads the
	/// prefab from disk, so filtering on it would deserialize every prefab file on the caller's thread.
	/// Callers should try the names in order, since whether a given prefab spawns depends on placement.
	/// </summary>
	public static object Prefabs(int limit)
	{
		string[] names = MyDefinitionManager.Static.GetPrefabDefinitions()
			.Where(entry => !string.IsNullOrEmpty(entry.Key))
			.Select(entry => entry.Key).OrderBy(name => name, StringComparer.Ordinal)
			.Take(Math.Max(1, Math.Min(limit, 256))).ToArray();
		return new { count = names.Length, prefabs = names };
	}

	/// <summary>
	/// Resolves the node that owns a client's partition and its control endpoint. Shared with
	/// <see cref="DropContainerDirectorService"/>: every WA-decided, owner-executed spawn needs the same lookup.
	/// </summary>
	internal static (string Node, string Control) ResolveOwner(ulong client)
	{
		string registry = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/')
			?? throw new InvalidOperationException("Gateway registry is not configured");
		string node = ResolveOwningNode(registry, client)
			?? throw new InvalidOperationException("Target client has no owning node route");
		string control = ResolveControlEndpoint(registry, node)
			?? throw new InvalidOperationException("Owning node has no control endpoint");
		return (node, control);
	}

	internal static HttpClient RegistryClient => Gateway;

	/// <summary>
	/// Resolves a named node's control endpoint. Cross-node IGC already knows which node holds the addressee from
	/// the endpoint registry, so it needs the endpoint lookup without the client-route step.
	/// </summary>
	internal static (string Node, string Control) ResolveNodeControl(string node)
	{
		// Cached: cross-node IGC resolves this per message, and a registry round trip per script send would cost
		// more than the send itself. The TTL is short enough that a node's endpoint moving is picked up quickly.
		// T-0350: while the registry is unreachable the last endpoint serves whatever its age, and with none the
		// lookup fails at once - cross-node IGC and the mod channel resolve this per message, IGC on the game thread.
		lock (NodeControlCache)
		{
			bool known = NodeControlCache.TryGetValue(node, out (string Control, double Expires) cached);
			switch (RegistryHoldPolicy.CachedLookup(ClusterRuntime.RegistryUnreachable, known, ClusterRuntime.LocalMilliseconds, cached.Expires))
			{
				case RegistryHoldPolicy.Lookup.Cached: return (node, cached.Control);
				case RegistryHoldPolicy.Lookup.Fail: throw new InvalidOperationException("Registry unreachable: no control endpoint known for node " + node);
			}
		}
		string registry = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/')
			?? throw new InvalidOperationException("Gateway registry is not configured");
		string control;
		try
		{
			control = ResolveControlEndpoint(registry, node)
				?? throw new InvalidOperationException("Node " + node + " has no control endpoint");
		}
		catch (Exception exception) when (ClusterRuntime.NoteRegistryFailure(exception)) { throw; }
		lock (NodeControlCache)
			NodeControlCache[node] = (control, ClusterRuntime.LocalMilliseconds + NodeControlCacheMilliseconds);
		return (node, control);
	}

	private static string ResolveOwningNode(string registry, ulong client)
	{
		if (client == 0) return null;
		string payload = Gateway.GetStringAsync(registry + "/registry/client-routes?client=" + client)
			.GetAwaiter().GetResult();
		using JsonDocument document = JsonDocument.Parse(payload);
		return document.RootElement.TryGetProperty("Node", out JsonElement node) ? node.GetString() : null;
	}

	private static string ResolveControlEndpoint(string registry, string node)
	{
		string payload = Gateway.GetStringAsync(registry + "/registry/nodes").GetAwaiter().GetResult();
		using JsonDocument document = JsonDocument.Parse(payload);
		foreach (JsonElement entry in document.RootElement.EnumerateArray())
			if (entry.TryGetProperty("Node", out JsonElement name) && name.GetString() == node
				&& entry.TryGetProperty("ControlEndpoint", out JsonElement control))
			{
				string value = control.GetString();
				if (!string.IsNullOrWhiteSpace(value)) return value;
			}
		return null;
	}
}
