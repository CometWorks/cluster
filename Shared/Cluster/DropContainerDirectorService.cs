using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// WA-3: wires the vanilla drop-container generator into the cluster.
/// <para>
/// Vanilla drives drops from <c>MySessionComponentContainerDropSystem.UpdateContainerSpawner</c>, whose player set
/// comes from <c>Sync.Players.GetOnlinePlayers()</c>. Only the World Authority may run a global generator, and it
/// hosts no clients, so that collection is empty there and no drop is ever decided in a cluster. The vanilla loop
/// is left running as the no-op it already is; this service supplies the decision from the cluster-wide player set
/// that <see cref="PositionRegistryService"/> already maintains, and dispatches each due drop to the node owning
/// that player. Execution has to happen there: the grid, its <c>MyContainerDropComponent</c>, its GPS and its
/// removal timer all live with the player, and the WA owns no partitions to hold them.
/// </para>
/// </summary>
public static class DropContainerDirectorService
{
	private static readonly object Sync = new object();
	private static readonly Dictionary<long, double> Deadlines = new();
	private static readonly Dictionary<Guid, ClusterDropContainerResult> Executions = new();
	private static readonly Queue<Guid> ExecutionOrder = new();
	private static readonly List<object> Dispatches = new();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	private static double m_nextSweep;

	/// <summary>
	/// Interval between drops for one player. Vanilla randomises this between the drop system definition's min and
	/// max respawn times; the cluster keeps its own schedule because it no longer runs vanilla's per-player timer.
	/// </summary>
	private const double DropIntervalMilliseconds = 15 * 60 * 1000;
	private const double SweepIntervalMilliseconds = 5000;

	/// <summary>Test-only schedule override; zero in production traffic. See <see cref="ConfigureTestInterval"/>.</summary>
	private static double m_testIntervalMilliseconds;
	private static double m_testIntervalUntil;

	/// <summary>
	/// Shrinks the per-player drop interval so a lane can observe the PRODUCTION decision loop fire on
	/// its own - vanilla-derived intervals are 15+ minutes, far beyond a lane run. Arming reschedules
	/// every existing deadline to one shortened interval out; the override expires on its own after ten
	/// minutes (or with ms=0) so a leaked knob cannot flood a world with containers.
	/// </summary>
	public static object ConfigureTestInterval(int ms)
	{
		if (ms is < 0 or > 120000) throw new ArgumentOutOfRangeException(nameof(ms));
		double now = ClusterRuntime.LocalMilliseconds;
		lock (Sync)
		{
			if (ms == 0)
			{
				m_testIntervalMilliseconds = 0;
				m_testIntervalUntil = 0;
			}
			else
			{
				m_testIntervalMilliseconds = ms;
				m_testIntervalUntil = now + 10 * 60 * 1000;
				foreach (long identity in Deadlines.Keys.ToArray())
					Deadlines[identity] = now + ms;
			}
			return new { node = Node, intervalMs = m_testIntervalMilliseconds, scheduled = Deadlines.Count };
		}
	}

	public static object TestIntervalStatus()
	{
		lock (Sync)
			return new { node = Node, intervalMs = EffectiveInterval(ClusterRuntime.LocalMilliseconds), scheduled = Deadlines.Count };
	}

	/// <summary>Must be called under <see cref="Sync"/>.</summary>
	private static double EffectiveInterval(double now)
		=> m_testIntervalMilliseconds > 0 && now < m_testIntervalUntil ? m_testIntervalMilliseconds : DropIntervalMilliseconds;

	/// <summary>
	/// Runs the World Authority's drop decision. Every player the cluster reports is scheduled a first drop one
	/// interval out, so a joining player never receives one instantly, and re-scheduled after each dispatch
	/// whether or not the spawn succeeded - a failed placement must not turn into a dispatch storm.
	/// </summary>
	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority) return;
		if (MySession.Static == null || !MySession.Static.EnableContainerDrops) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (now < m_nextSweep) return;
		m_nextSweep = now + SweepIntervalMilliseconds;

		IReadOnlyList<ClusterPlayerPosition> players = PositionRegistryService.Players;
		var due = new List<ClusterPlayerPosition>();
		lock (Sync)
		{
			var live = new HashSet<long>(players.Select(player => player.IdentityId));
			foreach (long identity in Deadlines.Keys.Where(identity => !live.Contains(identity)).ToArray())
				Deadlines.Remove(identity);
			double interval = EffectiveInterval(now);
			foreach (ClusterPlayerPosition player in players)
			{
				if (player.ClientId == 0) continue;
				if (!Deadlines.TryGetValue(player.IdentityId, out double deadline))
				{
					Deadlines[player.IdentityId] = now + interval;
					continue;
				}
				if (deadline > now) continue;
				Deadlines[player.IdentityId] = now + interval;
				due.Add(player);
			}
		}
		// #1443: a drop due while the cluster is at drops.maxActive is skipped; the player was rescheduled above, so nothing queues.
		int cap = ClusterConfig.GetInt("drops.maxActive");
		foreach (ClusterPlayerPosition player in due)
			if (DropContainerCap.Allow(cap, now)) TryDispatch(player);
		DropContainerCap.Report(cap, now);
	}

	/// <summary>
	/// Dispatches a drop for one cluster player immediately, bypassing the schedule. This is the seam the
	/// integration lane drives: waiting out a real drop interval would make the assertion a timing test rather
	/// than a routing one. It bypasses the drop cap too (#1443), but a drop it spawns counts against the cap.
	/// </summary>
	public static ClusterDropContainerResult DispatchNow(long identityId, bool competitive)
	{
		ClusterPlayerPosition player = PositionRegistryService.Players
			.FirstOrDefault(entry => entry.IdentityId == identityId)
			?? throw new KeyNotFoundException("No cluster player with identity " + identityId);
		lock (Sync) Deadlines[identityId] = ClusterRuntime.LocalMilliseconds + DropIntervalMilliseconds;
		return Dispatch(new ClusterDropContainerRequest(Guid.NewGuid(), null, player.IdentityId, player.ClientId, competitive));
	}

	/// <summary>Dispatches a decided drop to the node owning the target client's partition.</summary>
	public static ClusterDropContainerResult Dispatch(ClusterDropContainerRequest request)
	{
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority)
			throw new InvalidOperationException("Drop container dispatch must originate at the World Authority");
		DropContainerDirectorCodec.ValidateDecision(request);
		(string node, string control) = SpawnDirectorService.ResolveOwner(request.TargetClient);
		using StringContent content = new StringContent(
			DropContainerDirectorCodec.WriteRequest(request with { Origin = node }), Encoding.UTF8, "application/json");
		using HttpResponseMessage response = SpawnDirectorService.RegistryClient
			.PostAsync(control.TrimEnd('/') + "/cluster/drop-container-execute", content).GetAwaiter().GetResult();
		string payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException("Drop container dispatch failed: " + SecretRedactor.Redact(payload));
		ClusterDropContainerResult result = JsonSerializer.Deserialize<ClusterDropContainerResult>(payload)
			?? throw new InvalidOperationException("Owning node returned an empty drop container result");
		RecordDispatch(request, node, result);
		return result;
	}

	/// <summary>
	/// Executes a dispatched drop on the owning node, exactly once per operation id. Unlike a prefab spawn this is
	/// synchronous from the caller's point of view: vanilla returns its own success flag before the prefab manager
	/// finishes, and that flag is what decides whether the player is rescheduled, so it is what the WA needs back.
	/// </summary>
	public static ClusterDropContainerResult Execute(ClusterDropContainerRequest request)
	{
		DropContainerDirectorCodec.Validate(request);
		if (request.Origin != Node)
			throw new InvalidOperationException("Drop container spawn was dispatched to the wrong node");
		lock (Sync)
			if (Executions.TryGetValue(request.OperationId, out ClusterDropContainerResult replay)) return replay;
		ClusterDropContainerResult result;
		try
		{
			MySessionComponentContainerDropSystem system = MySession.Static?.GetComponent<MySessionComponentContainerDropSystem>()
				?? throw new InvalidOperationException("Drop container system is not loaded");
			result = new ClusterDropContainerResult(system.ClusterSpawnContainerDrop(request.IdentityId, request.Competitive), null);
		}
		catch (Exception exception)
		{
			result = new ClusterDropContainerResult(false, SecretRedactor.Redact(exception.Message));
		}
		Record(request.OperationId, result);
		return result;
	}

	/// <summary>Reports a dispatched operation's recorded outcome, so a lane can assert exactly-once replay.</summary>
	public static ClusterDropContainerResult Result(Guid operationId)
	{
		lock (Sync)
			return Executions.TryGetValue(operationId, out ClusterDropContainerResult recorded)
				? recorded : new ClusterDropContainerResult(false, "unknown operation");
	}

	/// <summary>
	/// Names the drop-container grids this node holds. The dispatch result only reports that vanilla accepted the
	/// spawn - the prefab manager finishes asynchronously - so this is what proves the container materialised on
	/// the owning node and nowhere else. Must run on the game thread: it walks the entity list.
	/// </summary>
	public static object Grids()
	{
		var ids = new List<long>();
		foreach (MyEntity entity in MyEntities.GetEntities())
			// Untyped GetFatBlocks() walks the maintained fat-block list; GetFatBlocks<T>() would walk every
			// slim block on the grid, armour included. Same predicate, same short-circuit.
			if (entity is MyCubeGrid grid
				&& grid.GetFatBlocks().Any(block => block is MyTerminalBlock terminal
					&& terminal.Components.Contains(typeof(MyContainerDropComponent))))
				ids.Add(grid.EntityId);
		return new { node = Node, count = ids.Count, grids = ids.ToArray() };
	}

	public static object Status()
	{
		lock (Sync)
			return new
			{
				node = Node,
				authority = ClusterRuntime.IsWorldAuthority,
				scheduled = Deadlines.Count,
				executions = Executions.Count,
				dispatches = Dispatches.ToArray(),
				cap = DropContainerCap.Status(ClusterConfig.GetInt("drops.maxActive"), ClusterRuntime.LocalMilliseconds)
			};
	}

	private static void TryDispatch(ClusterPlayerPosition player)
	{
		try
		{
			Dispatch(new ClusterDropContainerRequest(Guid.NewGuid(), null, player.IdentityId, player.ClientId, Competitive: false));
		}
		catch (Exception exception)
		{
			// A player between partitions has no route yet. The next sweep retries; failing the WA tick would not.
			Console.WriteLine("[ClusterPlugin] drop_container dispatch failed identity=" + player.IdentityId
				+ " client=" + player.ClientId + " error=" + SecretRedactor.Redact(exception.Message));
		}
	}

	private static void Record(Guid operationId, ClusterDropContainerResult result)
	{
		lock (Sync)
		{
			// Record failures too: a dispatch retry must not drop a second container because the first was refused.
			if (Executions.TryAdd(operationId, result))
			{
				ExecutionOrder.Enqueue(operationId);
				if (ExecutionOrder.Count > 1024) Executions.Remove(ExecutionOrder.Dequeue());
			}
		}
		Console.WriteLine("[ClusterPlugin] drop_container execute operation=" + operationId
			+ " spawned=" + result.Spawned + " error=" + (result.Error ?? "none"));
	}

	private static void RecordDispatch(ClusterDropContainerRequest request, string node, ClusterDropContainerResult result)
	{
		if (result.Spawned) DropContainerCap.Spawned(ClusterRuntime.LocalMilliseconds);
		lock (Sync)
		{
			Dispatches.Add(new
			{
				operation = request.OperationId,
				identity = request.IdentityId,
				client = request.TargetClient,
				node,
				competitive = request.Competitive,
				spawned = result.Spawned,
				error = result.Error
			});
			if (Dispatches.Count > 64) Dispatches.RemoveAt(0);
		}
		Console.WriteLine("[ClusterPlugin] drop_container dispatch operation=" + request.OperationId
			+ " identity=" + request.IdentityId + " client=" + request.TargetClient
			+ " node=" + node + " spawned=" + result.Spawned + " error=" + (result.Error ?? "none"));
	}
}
