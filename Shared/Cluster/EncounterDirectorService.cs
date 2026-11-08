using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using SpaceEngineers.Game.SessionComponents;

namespace ClusterPlugin;

/// <summary>
/// WA-3: wires the vanilla global-encounter generator into the cluster.
/// <para>
/// Vanilla's spawn tick returns early when <c>Sync.Players.GetOnlineHumanPlayerCount()</c> is zero. Only the World
/// Authority may run a global spawner and it hosts no clients, so that count is always zero there and a cluster
/// never spawned an encounter. The WA also owns no partitions, so it cannot hold one: an encounter's grids,
/// voxels, GPS and removal timer all need a node that owns a partition.
/// </para>
/// <para>
/// The WA therefore decides only <em>which</em> encounter is due and <em>near whom</em>, and the owning node runs
/// vanilla's own position pickers and spawn helpers, where the planets and the player really are.
/// </para>
/// </summary>
public static class EncounterDirectorService
{
	private static readonly object Sync = new object();
	private static readonly Dictionary<Guid, ClusterEncounterResult> Executions = new();
	private static readonly Queue<Guid> ExecutionOrder = new();
	private static readonly List<object> Dispatches = new();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	private static double m_nextSweep;
	private static double m_nextMeteorCheck;
	private static readonly Dictionary<long, double> m_meteorDue = new();
	/// <summary>SE-1228: global, cargo and meteor dispatches wait on the target's node, so they run off the WA's game thread.</summary>
	internal static readonly NodeDispatchLane Lane = new();

	/// <summary>
	/// Interval between cluster encounter decisions. Vanilla derives its own from
	/// <c>MySession.Static.Settings.GlobalEncounterTimer</c> (minutes); the cluster keeps a separate schedule
	/// because it no longer runs vanilla's timer, and reads the same setting so a server's configuration still
	/// governs the rate.
	/// </summary>
	private const double SweepIntervalMilliseconds = 5000;

	/// <summary>Test-only sweep override; zero in production traffic. See <see cref="ConfigureTestInterval"/>.</summary>
	private static double m_testIntervalMilliseconds;
	private static double m_testIntervalUntil;

	/// <summary>
	/// Shrinks the encounter sweep interval so a lane can observe the PRODUCTION decision loop fire on
	/// its own - the vanilla-derived interval is GlobalEncounterTimer minutes (15 by default), far beyond
	/// a lane run. The override expires on its own after ten minutes (or with ms=0) so a leaked knob
	/// cannot flood a world with hostile encounters.
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
				m_nextSweep = now + ms;
			}
			return new { node = Node, intervalMs = m_testIntervalMilliseconds };
		}
	}

	public static object TestIntervalStatus()
	{
		lock (Sync)
			return new
			{
				node = Node,
				intervalMs = m_testIntervalMilliseconds > 0 && ClusterRuntime.LocalMilliseconds < m_testIntervalUntil
					? m_testIntervalMilliseconds : 0
			};
	}

	/// <summary>Decides at the World Authority whether an encounter is due, and dispatches it if so.</summary>
	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority) return;
		if (MySession.Static?.Ready != true) return;
		UpdateMeteorSchedule(ClusterRuntime.LocalMilliseconds);
		if (MySession.Static.Settings.GlobalEncounterCap == 0) return;
		double now = ClusterRuntime.LocalMilliseconds;
		lock (Sync) if (now < m_nextSweep) return;
		double interval = Math.Max(1, MySession.Static.Settings.GlobalEncounterTimer) * 60000d;
		double next = Math.Max(SweepIntervalMilliseconds, interval);
		lock (Sync)
		{
			if (m_testIntervalMilliseconds > 0 && now < m_testIntervalUntil) next = m_testIntervalMilliseconds;
			bool first = m_nextSweep == 0;
			m_nextSweep = now + next;
			// T-0167: the first sweep of a World Authority process waits one interval, as vanilla's
			// timer does; sweeping at once spawned an encounter on every WA start and failover.
			if (first) return;
		}

		// EntityId != 0: a client that is admitted but controls nothing (sitting at the respawn screen) has no
		// meaningful position to spawn "near" - and the owning node would refuse the dispatch anyway.
		ClusterPlayerPosition target = SpawnDirectorService.SelectTarget(player => player.ClientId != 0 && player.EntityId != 0);
		if (target == null) return;
		string spawnGroup = PickSpawnGroup();
		if (spawnGroup == null) return;
		var request = new ClusterEncounterRequest(Guid.NewGuid(), null, spawnGroup, target.ClientId);
		Lane.Run(() =>
		{
			try { Dispatch(request); }
			catch (Exception exception)
			{
				// A player between partitions has no route yet. The next sweep retries.
				Console.WriteLine("[ClusterPlugin] encounter dispatch failed client=" + target.ClientId
					+ " error=" + SecretRedactor.Redact(exception.Message));
			}
		});
	}

	/// <summary>
	/// Dispatches a decided encounter immediately. The integration lane drives this. A caller-supplied
	/// operation id makes the dispatch replayable: the decision runs through the global command ledger
	/// (journaled into WA global saves), so retrying the SAME id - including against a replacement World
	/// Authority after a crash - re-issues the SAME decision instead of re-arbitrating a fresh one, and
	/// the owning node's execution record turns the re-issued POST into a replay rather than a second
	/// spawn.
	/// </summary>
	public static ClusterEncounterResult DispatchNow(string spawnGroup, ulong client, string kind = ClusterEncounterKinds.Global,
		Guid operationId = default)
	{
		if (operationId != Guid.Empty)
		{
			string ledgerPayload = JsonSerializer.Serialize(new { spawnGroup, client, kind });
			GlobalCommandResult ledgered = GlobalCommandLedger.Execute("encounters", operationId, ledgerPayload,
				() => new GlobalCommandResult(true,
					JsonSerializer.Serialize(DispatchDecided(spawnGroup, client, kind, operationId)), string.Empty));
			if (!ledgered.Success) throw new InvalidOperationException(ledgered.Error ?? "Encounter dispatch failed");
			return JsonSerializer.Deserialize<ClusterEncounterResult>(ledgered.Payload)
				?? throw new InvalidOperationException("Recorded encounter dispatch result is empty");
		}
		return DispatchDecided(spawnGroup, client, kind, Guid.NewGuid());
	}

	private static ClusterEncounterResult DispatchDecided(string spawnGroup, ulong client, string kind, Guid operationId)
	{
		if (client == 0)
			client = SpawnDirectorService.SelectTarget(player => player.ClientId != 0 && player.EntityId != 0)?.ClientId
				?? throw new InvalidOperationException("No cluster player to spawn an encounter near; registry: "
					+ string.Join(", ", PositionRegistryService.Players.Select(player =>
						$"identity={player.IdentityId} client={player.ClientId} entity={player.EntityId}"))
					+ "; reports: " + PositionRegistryService.DescribeReports());
		if (kind == ClusterEncounterKinds.Planetary || kind == ClusterEncounterKinds.Meteor)
			return Dispatch(new ClusterEncounterRequest(operationId, null, spawnGroup, client, kind));
		if (kind == ClusterEncounterKinds.Cargo)
			return Dispatch(new ClusterEncounterRequest(operationId, null, spawnGroup ?? PickCargoSpawnGroup()
				?? throw new InvalidOperationException("No cargo spawn group is defined"), client, kind));
		return Dispatch(new ClusterEncounterRequest(operationId, null, spawnGroup ?? PickSpawnGroup()
			?? throw new InvalidOperationException("No global encounter spawn group is defined"), client, kind));
	}

	public static ClusterEncounterResult Dispatch(ClusterEncounterRequest request)
	{
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority)
			throw new InvalidOperationException("Encounter dispatch must originate at the World Authority");
		EncounterDirectorCodec.ValidateDecision(request);
		(string node, string control) = SpawnDirectorService.ResolveOwner(request.TargetClient);
		using StringContent content = new StringContent(
			EncounterDirectorCodec.WriteRequest(request with { Origin = node }), Encoding.UTF8, "application/json");
		using HttpResponseMessage response = SpawnDirectorService.RegistryClient
			.PostAsync(control.TrimEnd('/') + "/cluster/encounter-execute", content).GetAwaiter().GetResult();
		string payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException("Encounter dispatch failed: " + SecretRedactor.Redact(payload));
		ClusterEncounterResult result = JsonSerializer.Deserialize<ClusterEncounterResult>(payload)
			?? throw new InvalidOperationException("Owning node returned an empty encounter result");
		RecordDispatch(request, node, result);
		return result;
	}

	/// <summary>
	/// Runs a dispatched encounter on the owning node, exactly once per operation id. This is what keeps an
	/// encounter from appearing once per node: a retry replays the recorded outcome instead of spawning again.
	/// </summary>
	public static ClusterEncounterResult Execute(ClusterEncounterRequest request)
	{
		EncounterDirectorCodec.Validate(request);
		if (request.Origin != Node)
			throw new InvalidOperationException("Encounter was dispatched to the wrong node");
		lock (Sync)
			if (Executions.TryGetValue(request.OperationId, out ClusterEncounterResult replay)) return replay;
		ClusterEncounterResult result;
		try
		{
			result = request.Kind switch
			{
				ClusterEncounterKinds.Planetary => ExecutePlanetary(request),
				ClusterEncounterKinds.Cargo => ExecuteCargo(request),
				ClusterEncounterKinds.Meteor => ExecuteMeteor(request),
				_ => ExecuteGlobal(request)
			};
		}
		catch (Exception exception)
		{
			result = new ClusterEncounterResult(false, request.SpawnGroup, 0, 0, SecretRedactor.Redact(exception.Message));
		}
		Record(request.OperationId, result);
		return result;
	}

	/// <summary>
	/// Attempts a refused placement this many times per execution. Vanilla's position selection
	/// legitimately fails (TestPlaceInSpace refusal near the picked sphere point); each attempt
	/// re-rolls the position and a failed attempt spawns nothing, so retrying inside one execution
	/// keeps the operation exactly-once.
	/// </summary>
	private const int PlacementAttempts = 3;

	/// <summary>Remaining test-forced placement refusals; see <see cref="ConfigureTestPlacementFailures"/>.</summary>
	private static int m_testPlacementFailures;

	/// <summary>
	/// Arms N simulated placement refusals so a lane can exercise the retry loop: the next N
	/// attempts are treated as vanilla returning false without calling the generator.
	/// </summary>
	public static object ConfigureTestPlacementFailures(int count)
	{
		if (count < 0 || count > 10) throw new ArgumentOutOfRangeException(nameof(count));
		lock (Sync) m_testPlacementFailures = count;
		return new { node = Node, armed = count };
	}

	private static ClusterEncounterResult ExecuteGlobal(ClusterEncounterRequest request)
	{
		MyGlobalEncountersGenerator generator = MySession.Static?.GetComponent<MyGlobalEncountersGenerator>()
			?? throw new InvalidOperationException("Global encounter generator is not loaded");
		int before = generator.ClusterActiveEncounters();
		bool spawned = false;
		for (int attempt = 1; !spawned && attempt <= PlacementAttempts; attempt++)
		{
			bool forcedFailure;
			lock (Sync)
			{
				forcedFailure = m_testPlacementFailures > 0;
				if (forcedFailure) m_testPlacementFailures--;
			}
			spawned = !forcedFailure && generator.ClusterSpawnGlobalEncounter(request.SpawnGroup);
			if (!spawned && attempt < PlacementAttempts)
				Console.WriteLine("[ClusterPlugin] encounter placement retry group=" + request.SpawnGroup
					+ " attempt=" + attempt + (forcedFailure ? " forced=true" : string.Empty));
		}
		return new ClusterEncounterResult(spawned, request.SpawnGroup, before, generator.ClusterActiveEncounters(), null);
	}

	/// <summary>Reports the executing node's encounter grids so a lane can probe their partitions.</summary>
	public static object EncounterGrids()
	{
		MyGlobalEncountersGenerator generator = MySession.Static?.GetComponent<MyGlobalEncountersGenerator>();
		return new
		{
			node = Node,
			grids = generator == null
				? Array.Empty<object>()
				: generator.ClusterEncounterGrids()
					.Select(grid => (object)new { encounterId = grid.EncounterId, entityId = grid.EntityId }).ToArray()
		};
	}

	/// <summary>
	/// Runs a planetary installation spawn on the owning node. <c>SpawnAroundPosition</c> is already public, so
	/// unlike the global generator this needs no new vanilla seam: the WA supplies only the target, and vanilla
	/// picks the planet, the spawn group and the site from the player's own position - all of which need the
	/// planet loaded, which is true here and never on the WA.
	/// </summary>
	private static ClusterEncounterResult ExecutePlanetary(ClusterEncounterRequest request)
	{
		MyPlanetaryEncountersGenerator generator = MySession.Static?.GetComponent<MyPlanetaryEncountersGenerator>()
			?? throw new InvalidOperationException("Planetary encounter generator is not loaded");
		MyPlayer player = MySession.Static.Players.GetOnlinePlayers()
			.FirstOrDefault(candidate => candidate.Id.SteamId == request.TargetClient)
			?? throw new KeyNotFoundException("Target client " + request.TargetClient + " is not online here");
		MyEntity controlled = player.Controller?.ControlledEntity?.Entity
			?? throw new InvalidOperationException("Target client controls no entity");
		generator.SpawnAroundPosition(controlled.PositionComp.GetPosition());
		return new ClusterEncounterResult(true, request.SpawnGroup, 0, 0, null);
	}

	/// <summary>
	/// Runs a dispatched cargo-ship spawn on the owning node. Vanilla's <c>Spawn</c> picks a random local player;
	/// the pin makes it pick exactly the WA-chosen target, so the ship really appears near the dispatched client.
	/// The prefab spawns in an asynchronous callback, so the tracked-ship count is the signal the lane polls -
	/// this result only proves the dispatch executed.
	/// </summary>
	private static ClusterEncounterResult ExecuteCargo(ClusterEncounterRequest request)
	{
		MyPlayer player = MySession.Static.Players.GetOnlinePlayers()
			.FirstOrDefault(candidate => candidate.Id.SteamId == request.TargetClient)
			?? throw new KeyNotFoundException("Target client " + request.TargetClient + " is not online here");
		if (player.Character == null)
			throw new InvalidOperationException("Target client has no character");
		int before = MyNeutralShipSpawnerSeams.ClusterShipsInProgress();
		MyNeutralShipSpawnerSeams.ClusterPinnedTarget = request.TargetClient;
		try { MyNeutralShipSpawner.SpawnGroup(request.SpawnGroup); }
		finally { MyNeutralShipSpawnerSeams.ClusterPinnedTarget = 0; }
		return new ClusterEncounterResult(true, request.SpawnGroup, before, MyNeutralShipSpawnerSeams.ClusterShipsInProgress(), null);
	}

	/// <summary>
	/// Runs a dispatched meteor wave on the owning node at the target player's real position, with vanilla's own
	/// restriction check, radii, and amount. The meteor count rides the EncountersAfter field.
	/// </summary>
	private static ClusterEncounterResult ExecuteMeteor(ClusterEncounterRequest request)
	{
		MyPlayer player = MySession.Static.Players.GetOnlinePlayers()
			.FirstOrDefault(candidate => candidate.Id.SteamId == request.TargetClient)
			?? throw new KeyNotFoundException("Target client " + request.TargetClient + " is not online here");
		MyEntity controlled = player.Controller?.ControlledEntity?.Entity
			?? throw new InvalidOperationException("Target client controls no entity");
		int spawned = MyMeteorShowerSeams.ClusterTryStartWave(controlled.PositionComp.GetPosition());
		if (spawned < 0)
			return new ClusterEncounterResult(false, null, 0, 0, "position is meteor-restricted");
		return new ClusterEncounterResult(spawned > 0, null, 0, spawned, null);
	}

	public static ClusterEncounterResult Result(Guid operationId)
	{
		lock (Sync)
			return Executions.TryGetValue(operationId, out ClusterEncounterResult recorded)
				? recorded : new ClusterEncounterResult(false, null, 0, 0, "unknown operation");
	}

	/// <summary>Reports how many encounters this node holds, so a lane can require exactly one node to hold it.</summary>
	public static object Encounters()
	{
		MyGlobalEncountersGenerator generator = MySession.Static?.GetComponent<MyGlobalEncountersGenerator>();
		return new
		{
			node = Node,
			count = generator?.ClusterActiveEncounters() ?? 0,
			// Cargo prefabs spawn in an asynchronous callback, so the lane polls this after a cargo dispatch.
			cargoShips = MyNeutralShipSpawnerSeams.ClusterShipsInProgress()
		};
	}

	public static object Status()
	{
		lock (Sync)
			return new
			{
				node = Node,
				authority = ClusterRuntime.IsWorldAuthority,
				executions = Executions.Count,
				dispatches = Dispatches.ToArray(),
				spawnGroups = SpawnGroups().Take(8).ToArray()
			};
	}

	/// <summary>
	/// Picks a global-encounter spawn group. Definitions are immutable once loaded and identical on every node, so
	/// the World Authority can choose without consulting anyone.
	/// </summary>
	private static string PickSpawnGroup() => SpawnGroups().FirstOrDefault();

	/// <summary>
	/// Picks a cargo spawn group for the lane's group-less dispatch. Vanilla's Spawn silently returns when the
	/// group's owning NPC identity does not resolve, so only groups whose owner resolves here (faction state is
	/// replicated cluster-wide) are candidates - the cargo owner lookup is read-only.
	/// </summary>
	private static string PickCargoSpawnGroup()
		=> MyDefinitionManager.Static.GetSpawnGroupDefinitions()
			.Where(group => group.IsCargoShip && group.Enabled && !string.IsNullOrEmpty(group.Id.SubtypeName)
				&& group.TryGetOwnerId(out _))
			.Select(group => group.Id.SubtypeName).OrderBy(name => name, StringComparer.Ordinal).FirstOrDefault();

	/// <summary>
	/// The World Authority's per-player meteor schedule. Vanilla schedules meteor waves as per-player global
	/// events, which only activate when a player joins - never true on the WA - so the WA keeps its own timers
	/// from the position registry and dispatches each due wave to the node owning that player. A wave resets the
	/// timers of every player inside the disable radius of its target, mirroring vanilla's RescheduleAll.
	/// </summary>
	private static void UpdateMeteorSchedule(double now)
	{
		if (now < m_nextMeteorCheck) return;
		m_nextMeteorCheck = now + 1000;
		// SE-1228: one wave in flight at a time; the timers move when its answer is back on the game thread.
		if (Lane.Busy) return;
		if (MySession.Static.EnvironmentHostility == MyEnvironmentHostilityEnum.SAFE
			|| !Sandbox.Engine.Utils.MyFakes.ENABLE_METEOR_SHOWERS) return;
		ClusterPlayerPosition[] players = PositionRegistryService.Players
			.Where(player => player.ClientId != 0 && player.EntityId != 0).ToArray();
		foreach (long stale in m_meteorDue.Keys.Except(players.Select(player => player.IdentityId)).ToArray())
			m_meteorDue.Remove(stale);
		foreach (ClusterPlayerPosition player in players)
		{
			if (!m_meteorDue.TryGetValue(player.IdentityId, out double due))
			{
				m_meteorDue[player.IdentityId] = now + NextWaveDelay();
				continue;
			}
			if (now < due) continue;
			// Asked again in 10 s unless the wave spawns: a player between partitions has no route yet.
			m_meteorDue[player.IdentityId] = now + 10000;
			Lane.Run(() =>
			{
				ClusterEncounterResult result;
				try { result = Dispatch(new ClusterEncounterRequest(Guid.NewGuid(), null, null, player.ClientId, ClusterEncounterKinds.Meteor)); }
				catch (Exception exception)
				{
					Console.WriteLine("[ClusterPlugin] meteor dispatch failed client=" + player.ClientId
						+ " error=" + SecretRedactor.Redact(exception.Message));
					return;
				}
				if (result.Spawned) Sandbox.MySandboxGame.Static?.Invoke(() => WaveSpawned(player, players), "ClusterPlugin meteor wave");
			});
			// One wave per check: the next due player may be within this one's disable radius.
			return;
		}
	}

	/// <summary>A wave resets the timers of every player inside the disable radius of its target. Game thread.</summary>
	private static void WaveSpawned(ClusterPlayerPosition target, ClusterPlayerPosition[] players)
	{
		double now = ClusterRuntime.LocalMilliseconds;
		double disableRadius = MyDefinitionManager.Static.GetMeteorEventDisableRadius() ?? 3000f;
		foreach (ClusterPlayerPosition nearby in players)
		{
			double dx = nearby.X - target.X, dy = nearby.Y - target.Y, dz = nearby.Z - target.Z;
			if (Math.Sqrt(dx * dx + dy * dy + dz * dz) <= disableRadius)
				m_meteorDue[nearby.IdentityId] = now + NextWaveDelay();
		}
	}

	private static double NextWaveDelay()
		=> MyMeteorShower.CalculateShowerTime(MySession.Static.EnvironmentHostility).TotalMilliseconds;

	private static string[] SpawnGroups()
		=> MyDefinitionManager.Static.GetSpawnGroupDefinitions()
			.Where(group => group.IsGlobalEncounter && !string.IsNullOrEmpty(group.Id.SubtypeName))
			.Select(group => group.Id.SubtypeName).OrderBy(name => name, StringComparer.Ordinal).ToArray();

	private static void Record(Guid operationId, ClusterEncounterResult result)
	{
		lock (Sync)
		{
			// Record failures too: a retry must not spawn a second encounter because the first partly ran.
			if (Executions.TryAdd(operationId, result))
			{
				ExecutionOrder.Enqueue(operationId);
				if (ExecutionOrder.Count > 1024) Executions.Remove(ExecutionOrder.Dequeue());
			}
		}
		Console.WriteLine("[ClusterPlugin] encounter execute operation=" + operationId
			+ " spawnGroup=" + result.SpawnGroup + " spawned=" + result.Spawned
			+ " before=" + result.EncountersBefore + " after=" + result.EncountersAfter
			+ " error=" + (result.Error ?? "none"));
	}

	private static void RecordDispatch(ClusterEncounterRequest request, string node, ClusterEncounterResult result)
	{
		lock (Sync)
		{
			Dispatches.Add(new
			{
				operation = request.OperationId,
				spawnGroup = request.SpawnGroup,
				client = request.TargetClient,
				node,
				spawned = result.Spawned,
				error = result.Error
			});
			if (Dispatches.Count > 64) Dispatches.RemoveAt(0);
		}
		Console.WriteLine("[ClusterPlugin] encounter dispatch operation=" + request.OperationId
			+ " spawnGroup=" + request.SpawnGroup + " client=" + request.TargetClient
			+ " node=" + node + " spawned=" + result.Spawned + " error=" + (result.Error ?? "none"));
	}
}
