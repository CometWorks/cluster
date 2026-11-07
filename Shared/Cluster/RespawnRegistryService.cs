using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.GUI;
using SpaceEngineers.Game.World;
using VRage.Game;
using VRage.Game.Components;
using VRage.Network;
using VRageMath;

namespace ClusterPlugin;

public static class RespawnRegistryService
{
	private const string Subsystem = "respawns";
	private static readonly object Sync = new object();
	private static readonly HttpClient Gateway = ClusterHttp.Client(TimeSpan.FromSeconds(30));
	private static readonly Dictionary<string, Report> Reports = new Dictionary<string, Report>(StringComparer.Ordinal);
	private static readonly Dictionary<Guid, (ClusterRespawnExecution Request, ClusterRespawnExecutionResult Result)> Executions = new();
	private static readonly Queue<Guid> ExecutionOrder = new();
	private static readonly HashSet<Guid> AdoptedOperations = new();
	private static readonly HashSet<Guid> InvokedOperations = new();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	private static ClusterRespawnPoint[] m_points = Array.Empty<ClusterRespawnPoint>();
	private static string m_snapshot = RespawnRegistryCodec.Write(Array.Empty<ClusterRespawnPoint>());
	private static PartitionManager m_partitions;
	private static PartitionPersistence m_persistence;
	private static double m_nextReport;
	private static int m_testDelayMilliseconds;
	private static int m_testHeldCount;
	private static readonly List<PendingRespawn> m_pendingRespawns = new();
	private static double m_nextPendingAttempt;
	private static int m_pendingAttemptInFlight;
	// Same-point re-arbitration stays free (idempotent retries); a different point for the same
	// identity inside 10s is refused - two transactions racing toward two medbays. Game thread only.
	private static readonly RespawnArbitrationWindow m_arbitrationWindow = new RespawnArbitrationWindow(10000);
	// SE-1150: repaired or dropped points are logged once, not on every report. m_receivedProblems under Sync.
	private static readonly RespawnProblemLog m_capturedProblems = new RespawnProblemLog();
	private static readonly RespawnProblemLog m_receivedProblems = new RespawnProblemLog();
	private static ClusterRespawnPoint[] m_testRawPoints = Array.Empty<ClusterRespawnPoint>();
	private static double m_testRawPointsUntil;
	private const double PendingRetryIntervalMilliseconds = 5000;
	private const double PendingDeadlineMilliseconds = 300000;
	// SE-1124: clients whose switch failed after this node emptied their view, waiting to be taken in again.
	private static readonly List<Restore> m_restores = new();
	private const double RestoreDeadlineMilliseconds = 20000;
	// A medbay respawn whose switch failed this often is answered as refused instead of retried.
	private const int MaxSwitchFailures = 3;

	public static IReadOnlyList<ClusterRespawnPoint> Points
	{
		get { lock (Sync) return m_points.ToArray(); }
	}

	internal static void Install(PartitionManager partitions, PartitionPersistence persistence)
	{
		m_partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
		m_persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
		ClusterRuntime.AuthorizeRegistryClient(Gateway);
		MyGuiScreenMedicalsSeams.ClusterAppendRespawnPoints = AppendRemoteRespawnPoints;
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterCommandHandler(Subsystem, HandleCommand);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ApplySnapshot);
	}

	public static void AppendRemoteRespawnPoints(ulong userId, long identityId,
		List<MySpaceRespawnComponent.MyRespawnPointInfo> respawns)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority || respawns == null) return;
		bool admin = MySession.Static.RemoteAdminSettings.TryGetValue(userId, out AdminSettingsEnum settings)
			&& (settings & AdminSettingsEnum.UseTerminals) != 0;
		foreach (ClusterRespawnPoint point in Points)
		{
			if (point.Origin == Node || !point.Working || !point.SpawnWithoutOxygen && point.OxygenLevel <= 0f
				|| !admin && !point.SetFactionToSpawnee && !MyIDModule.GetRelationPlayerBlock(point.OwnerIdentityId, identityId,
					(MyOwnershipShareModeEnum)point.ShareMode).IsFriendly()
				|| respawns.Any(x => x.MedicalRoomId == point.RespawnEntityId)) continue;
			var position = new Vector3D(point.X, point.Y, point.Z);
			respawns.Add(new MySpaceRespawnComponent.MyRespawnPointInfo
			{
				MedicalRoomId = point.RespawnEntityId,
				MedicalRoomGridId = point.GridEntityId,
				MedicalRoomName = point.Name,
				OxygenLevel = point.OxygenLevel,
				OwnerId = point.OwnerIdentityId,
				PrefferedCameraPosition = position + new Vector3D(20),
				MedicalRoomPosition = position,
				MedicalRoomUp = Vector3D.Up,
				MedicalRoomVelocity = Vector3.Zero,
			});
		}
	}

	public static bool RouteExactRespawn(MyPlayerCollection.RespawnMsg msg)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority) return true;
		// Pod respawns legitimately carry NewIdentity (vanilla sets it from FirstSpawnDone), so the
		// ship check comes first; the identity recreation runs on the executing node through the
		// same seams the local pod spawn uses.
		if (!string.IsNullOrEmpty(msg.RespawnShipId)) return RoutePodSpawn(msg);
		if (msg.NewIdentity || msg.RespawnEntityId <= 0) return true;
		bool remote = false;
		ulong userId = MyEventContext.Current.Sender.Value;
		Guid operationId = Guid.NewGuid();
		try
		{
			var playerId = new MyPlayer.PlayerId(userId, msg.PlayerSerialId);
			MyPlayer player = MySession.Static?.Players.GetPlayerById(playerId);
			if (player?.Identity == null) return true;
			ClusterRespawnDecision decision = Arbitrate(
				new ClusterRespawnRequest(userId, msg.PlayerSerialId, player.Identity.IdentityId, msg.RespawnEntityId), operationId);
			Console.WriteLine($"[ClusterPlugin] respawn route operation={operationId:D} target={decision.Origin} partition={decision.PartitionId} generation={decision.Generation}");
			if (decision.Origin == Node) return true;
			if (OfflinePartitionService.IsOfflineOrigin(decision.Origin))
			{
				// T-0193 spawn trigger: the medical room is in an offline partition. Load it onto this node
				// and hold the request pending - the client keeps its spinner, and the retry re-arbitrates
				// once the loaded partition reports the room live, which routes it like any other respawn.
				OfflinePartitionService.RequestLoad(decision.PartitionId, Node, "spawn");
				QueuePendingRespawn(operationId, userId, msg);
				Console.WriteLine($"[ClusterPlugin] respawn operation={operationId:D} waits for offline partition {decision.PartitionId} to load");
				return false;
			}
			// T-0350: the switch is a 30 s registry request on the game thread, made after the client's view is
			// detached. While the registry is unreachable fail BEFORE the dispatch: the request stays pending
			// (below) and completes once the registry answers, instead of the wait and the disconnect.
			if (ClusterRuntime.RegistryUnreachable) throw new InvalidOperationException("Registry unreachable: respawn switch not attempted");
			remote = true;
			FinishRemoteRespawn(operationId, userId, msg.PlayerSerialId, player.Identity.IdentityId, decision,
				msg.JoinGame, msg.ModelName, msg.Color.PackedValue);
			return false;
		}
		catch (Exception exception)
		{
			// Post-dispatch failures already disconnected the client inside FinishRemoteRespawn; the message
			// stays consumed. A PRE-dispatch failure (the arbitration could not run - a World Authority outage)
			// must not fall through to the vanilla refusal: the stock medical screen never recovers from a
			// refused medbay respawn (m_respawning is never reset), so this node keeps the request pending and
			// completes it itself once arbitration works again. Nothing was spawned and nothing was detached,
			// so the retry is safe and the client just keeps waiting on its spinner until the spawn arrives.
			if (remote) return false;
			Console.WriteLine("[ClusterPlugin] remote respawn failed: " + SecretRedactor.Redact(exception.Message));
			QueuePendingRespawn(operationId, userId, msg);
			return false;
		}
	}

	/// <summary>
	/// WA-6 pod placement: a respawn-ship spawn creates its own fresh partition, so the WA-arbitrated
	/// choice is WHERE it executes - the gateway names the least-loaded node. Same node (or any
	/// pre-dispatch failure): fall back to the vanilla local spawn, which a pod always supports.
	/// </summary>
	private static bool RoutePodSpawn(MyPlayerCollection.RespawnMsg msg)
	{
		bool remote = false;
		ulong userId = MyEventContext.Current.Sender.Value;
		Guid operationId = Guid.NewGuid();
		try
		{
			var playerId = new MyPlayer.PlayerId(userId, msg.PlayerSerialId);
			MyPlayer player = MySession.Static?.Players.GetPlayerById(playerId);
			if (player?.Identity == null) return true;
			// T-0350: no placement request (30 s, game thread) while the registry is unreachable: the pod spawns locally.
			if (ClusterRuntime.RegistryUnreachable) throw new InvalidOperationException("Registry unreachable: pod placement not asked");
			string payload = Gateway.GetStringAsync(
				Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/') + "/registry/pod-placement")
				.GetAwaiter().GetResult();
			PodPlacement placement = JsonSerializer.Deserialize<PodPlacement>(payload,
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
			if (string.IsNullOrEmpty(placement?.Node) || placement.Node == Node) return true;
			// Lost while the placement was in flight: the switch would wait on it next.
			if (ClusterRuntime.RegistryUnreachable) throw new InvalidOperationException("Registry unreachable: respawn switch not attempted");
			Console.WriteLine($"[ClusterPlugin] pod route operation={operationId:D} target={placement.Node} ship={msg.RespawnShipId}");
			remote = true;
			FinishRemoteRespawn(operationId, userId, msg.PlayerSerialId, player.Identity.IdentityId,
				new ClusterRespawnDecision(0, 0, placement.Node, 0, 0),
				msg.JoinGame, msg.ModelName, msg.Color.PackedValue, msg.RespawnShipId, msg.NewIdentity);
			return false;
		}
		catch (Exception exception)
		{
			// Post-dispatch failures already disconnected the client inside FinishRemoteRespawn; the
			// message stays consumed. Pre-dispatch failures spawn the pod locally like vanilla would.
			if (remote) return false;
			ClusterRuntime.NoteRegistryFailure(exception);   // T-0350: the placement request got no answer
			Console.WriteLine("[ClusterPlugin] pod route failed, spawning locally: " + SecretRedactor.Redact(exception.Message));
			return true;
		}
	}

	private sealed record PodPlacement(string Node);

	private static void FinishRemoteRespawn(Guid operationId, ulong userId, int serialId, long identityId,
		ClusterRespawnDecision decision, bool joinGame, string modelName, uint packedColor, string respawnShipId = null,
		bool newIdentity = false, int failures = 0)
	{
		string clientState = null;
		try
		{
			// The client keeps its loaded world across the backend switch, so the destination node has to adopt its
			// replication view instead of replaying a join. Everything this node streamed belongs to this node, so the
			// view is emptied here and the destination re-streams its own copy plus the newly respawned character.
			clientState = ClusterControlServer.DetachClientView(userId);
			var request = new ClusterRespawnExecution(operationId, userId, serialId, identityId,
				decision.RespawnEntityId, decision.GridEntityId, decision.Origin, decision.PartitionId, decision.Generation,
				joinGame, modelName, packedColor, clientState, respawnShipId, newIdentity);
			ClusterRespawnExecutionResult result = PostSwitch(request);
			Console.WriteLine($"[ClusterPlugin] respawn route operation={operationId:D} success={result.Success} ready={result.Ready} controlled={result.ControlledEntityId}");
		}
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] remote respawn failed: " + SecretRedactor.Redact(exception.Message));
			// The client's view was already emptied here. It comes back here (the Gateway rolls the switch back), so
			// this node takes it in again; a medbay respawn is then retried from the pending queue.
			if (clientState != null)
				RestoreClient(new Restore(operationId, userId, serialId, identityId, clientState,
					ClusterRuntime.LocalMilliseconds + RestoreDeadlineMilliseconds,
					string.IsNullOrEmpty(respawnShipId) ? new PendingRespawn(operationId, userId, serialId, decision.RespawnEntityId,
						joinGame, modelName, packedColor, ClusterRuntime.LocalMilliseconds + PendingDeadlineMilliseconds, failures + 1) : null,
					!string.IsNullOrEmpty(respawnShipId)));
			throw;
		}
	}

	/// <summary>
	/// SE1-0122: moves a dead client to the node holding the spawn point it previews, so that node streams it the
	/// grid (see RemotePullService.Preview). The same Gateway switch as a cross-node respawn, minus the respawn: the
	/// client keeps its loaded world, this node hands its emptied view over, and the medbay's node adopts it,
	/// creates the player and replays the preview request. The client's later respawn at that medbay is then a
	/// local one. Never blocks the game thread: the checks and the detach run there, the switch on a worker.
	/// </summary>
	internal static void SwitchForPreview(ulong userId, long medicalRoomId, ClusterRespawnPoint point)
	{
		MySandboxGame.Static?.Invoke(() =>
		{
			Guid operationId = Guid.NewGuid();
			string clientState = null;
			long identityId = 0;
			ClusterRespawnExecution request;
			try
			{
				MyPlayer player = MySession.Static?.Players.GetPlayerById(new MyPlayer.PlayerId(userId, 0));
				// #1287: "client-detached" - a cross-node respawn already detached the view and is switching the client
				// away; it stays connected here until the Gateway moves it, so HasClient alone still says yes.
				string refusal = player?.Identity == null ? "no-player"
					: player.Character is { IsDead: false } ? "alive"
					: !ReplicationServerPatch.HasClient(userId) ? "client-absent"
					: !ReplicationServerPatch.IsClientReady(userId) ? "client-detached"
					: MyEntities.EntityExists(point.GridEntityId) ? "grid-local"
					: HasPendingRespawn(userId) ? "respawn-pending"
					: ClusterRuntime.RegistryUnreachable ? "registry-unreachable"
					: null;
				if (refusal != null)
				{
					Console.WriteLine($"[ClusterPlugin] spawn preview switch operation={operationId:D} user={userId} skipped={refusal}");
					return;
				}
				identityId = player.Identity.IdentityId;
				clientState = ClusterControlServer.DetachClientView(userId);
				request = new ClusterRespawnExecution(operationId, userId, 0, identityId, medicalRoomId,
					point.GridEntityId, point.Origin, point.PartitionId, point.Generation, false, null, 0, clientState, Preview: true);
			}
			catch (Exception exception)
			{
				// #1287: an exception out of a game-thread Invoke ends the game loop, and the node quit with every client on it.
				Console.WriteLine($"[ClusterPlugin] spawn preview switch operation={operationId:D} user={userId} failed before the switch: "
					+ SecretRedactor.Redact(exception.Message));
				if (clientState != null) RestoreClient(PreviewRestore(operationId, userId, identityId, clientState));
				return;
			}
			Console.WriteLine($"[ClusterPlugin] spawn preview switch operation={operationId:D} user={userId} target={point.Origin} partition={point.PartitionId}");
			Task.Run(() =>
			{
				try
				{
					ClusterSeams.BeforeSwitchPost?.Invoke(request);
					ClusterRespawnExecutionResult result = PostSwitch(request);
					Console.WriteLine($"[ClusterPlugin] spawn preview switch operation={operationId:D} success={result.Success}");
				}
				catch (Exception exception)
				{
					// SE-1124: the Gateway rolled the client back here. It is still in its world on the medical screen, so
					// this node takes it in again; disconnecting it stranded a Steam client "Paused".
					Console.WriteLine($"[ClusterPlugin] spawn preview switch operation={operationId:D} failed: " + SecretRedactor.Redact(exception.Message));
					RestoreClient(PreviewRestore(operationId, userId, identityId, clientState));
				}
			});
		}, "ClusterPlugin spawn preview switch");
	}

	private static Restore PreviewRestore(Guid operationId, ulong userId, long identityId, string clientState) =>
		new Restore(operationId, userId, 0, identityId, clientState, ClusterRuntime.LocalMilliseconds + RestoreDeadlineMilliseconds,
			null, false);

	private static void RestoreClient(Restore restore)
	{
		lock (Sync) m_restores.Add(restore);
		Console.WriteLine($"[ClusterPlugin] spawn switch restore queued operation={restore.OperationId:D} user={restore.UserId}");
	}

	/// <summary>
	/// SE-1124: takes a client back whose switch failed after this node emptied its view. The client never left its world
	/// and sends no join, so the DisconnectClient this replaced stranded a Steam client: the Gateway re-opened its session
	/// to this node, which then waited for a join forever, and the client paused itself after 5 s without server time
	/// (octahedron-test 2026-11-05). The client comes back on the link it never left (a refusal before the Gateway switched
	/// the backend) or on the Gateway's rollback attach; either way its view here is empty and not ready, and it gets the
	/// same adoption a switch target gives it. Game thread.
	/// </summary>
	private static void RestoreClients()
	{
		Restore[] due;
		lock (Sync)
		{
			if (m_restores.Count == 0) return;
			due = m_restores.ToArray();
		}
		foreach (Restore restore in due)
		{
			string notReady;
			try
			{
				bool ready = ReplicationServerPatch.IsClientReady(restore.UserId);
				// The planet sectors went with the view; adoption needs an empty sector set.
				if (!ready) ReplicationServerPatch.StateFor(restore.UserId)?.KnownSectors.Clear();
				notReady = AdoptSwitchedClient(restore.OperationId, restore.UserId, restore.SerialId, restore.IdentityId,
					ready ? null : restore.ClientState, out _, ownBindings: true);
			}
			catch (Exception exception)
			{
				notReady = "failed: " + SecretRedactor.Redact(exception.Message);
			}
			if (notReady != null && !notReady.StartsWith("failed", StringComparison.Ordinal)
				&& ClusterRuntime.LocalMilliseconds < restore.Deadline) continue;
			lock (Sync) m_restores.Remove(restore);
			if (notReady != null)
			{
				// Last resort, the old behaviour: a client this node cannot take in rejoins.
				Console.WriteLine($"[ClusterPlugin] spawn switch restore abandoned operation={restore.OperationId:D} user={restore.UserId} reason={notReady}");
				if (ReplicationServerPatch.HasClient(restore.UserId)) MyMultiplayer.Static?.DisconnectClient(restore.UserId);
				continue;
			}
			RemotePullService.ForgetDemands(restore.UserId);
			Console.WriteLine($"[ClusterPlugin] spawn switch restored operation={restore.OperationId:D} user={restore.UserId}"
				+ (restore.Retry != null ? $" failures={restore.Retry.Failures}" : restore.Pod ? " pod" : " preview"));
			if (restore.Retry != null && restore.Retry.Failures < MaxSwitchFailures)
			{
				lock (Sync)
				{
					m_pendingRespawns.RemoveAll(x => x.UserId == restore.UserId && x.SerialId == restore.SerialId);
					m_pendingRespawns.Add(restore.Retry);
				}
				Console.WriteLine($"[ClusterPlugin] respawn pending queued operation={restore.OperationId:D} user={restore.UserId}"
					+ $" respawn={restore.Retry.RespawnEntityId} after failed switch");
			}
			else if (restore.Retry != null || restore.Pod)
				MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyPlayerCollection.OnRespawnRequestFailure, restore.SerialId,
					new EndpointId(restore.UserId));
		}
	}

	private static bool HasPendingRespawn(ulong userId)
	{
		lock (Sync) return m_pendingRespawns.Any(x => x.UserId == userId);
	}

	private static ClusterRespawnExecutionResult PostSwitch(ClusterRespawnExecution request)
	{
		using StringContent content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
		using HttpResponseMessage response = Gateway.PostAsync(
			Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/') + "/registry/respawns/switch", content)
			.GetAwaiter().GetResult();
		string payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException("Gateway respawn switch failed: " + payload);
		return JsonSerializer.Deserialize<ClusterRespawnExecutionResult>(payload)
			?? throw new InvalidOperationException("Gateway returned an empty respawn result");
	}

	private static void QueuePendingRespawn(Guid operationId, ulong userId, MyPlayerCollection.RespawnMsg msg)
	{
		var pending = new PendingRespawn(operationId, userId, msg.PlayerSerialId, msg.RespawnEntityId,
			msg.JoinGame, msg.ModelName, msg.Color.PackedValue,
			ClusterRuntime.LocalMilliseconds + PendingDeadlineMilliseconds);
		lock (Sync)
		{
			m_pendingRespawns.RemoveAll(candidate => candidate.UserId == userId && candidate.SerialId == msg.PlayerSerialId);
			m_pendingRespawns.Add(pending);
		}
		Console.WriteLine($"[ClusterPlugin] respawn pending queued operation={operationId:D} user={userId} respawn={msg.RespawnEntityId}");
	}

	/// <summary>
	/// Completes respawns whose arbitration failed transiently (a World Authority outage). The blocking
	/// arbitration runs on a worker thread - during an outage every attempt waits out the full command
	/// timeout, and doing that on the game thread starves the node enough to drop its clients. Only the
	/// completion (the same flow the original request used) hops back to the game thread. The pending entry
	/// is dropped when the player leaves, spawns some other way, or the deadline passes.
	/// </summary>
	private static void RetryPendingRespawns()
	{
		PendingRespawn pending;
		lock (Sync)
		{
			if (m_pendingRespawns.Count == 0 || ClusterRuntime.LocalMilliseconds < m_nextPendingAttempt
				|| Volatile.Read(ref m_pendingAttemptInFlight) != 0) return;
			m_nextPendingAttempt = ClusterRuntime.LocalMilliseconds + PendingRetryIntervalMilliseconds;
			pending = m_pendingRespawns[0];
		}
		var playerId = new MyPlayer.PlayerId(pending.UserId, pending.SerialId);
		MyPlayer player = MySession.Static?.Players.GetPlayerById(playerId);
		if (pending.Deadline < ClusterRuntime.LocalMilliseconds || player?.Character is { IsDead: false })
		{
			lock (Sync) m_pendingRespawns.Remove(pending);
			Console.WriteLine($"[ClusterPlugin] respawn pending abandoned operation={pending.OperationId:D} user={pending.UserId}"
				+ (player?.Character is { IsDead: false } ? " reason=character-alive" : " reason=deadline"));
			return;
		}
		// A null player here is TRANSIENT during an authority replacement (the binding refresh detaches and
		// re-attaches the local player); keep the entry pending instead of abandoning a recoverable respawn.
		if (player?.Identity == null) return;
		long identityId = player.Identity.IdentityId;
		Interlocked.Exchange(ref m_pendingAttemptInFlight, 1);
		Task.Run(() =>
		{
			try
			{
				// Arbitration is a pure decision, so every retry asks under a FRESH operation id: the command
				// ledger records failed outcomes too, and a transient failure (identity not yet re-bound after
				// an authority replacement) would poison a reused id forever. Only the execution below keeps
				// pending.OperationId — that is the side-effectful step the target node must dedup.
				ClusterRespawnDecision decision = Arbitrate(new ClusterRespawnRequest(pending.UserId, pending.SerialId,
					identityId, pending.RespawnEntityId), Guid.NewGuid());
				Console.WriteLine($"[ClusterPlugin] respawn pending arbitrated operation={pending.OperationId:D} target={decision.Origin} partition={decision.PartitionId} generation={decision.Generation}");
				if (OfflinePartitionService.IsOfflineOrigin(decision.Origin))
				{
					// Still offline (or loading): re-assert the demand and stay pending until the deadline.
					OfflinePartitionService.RequestLoad(decision.PartitionId, Node, "spawn");
					return;
				}
				// T-0350: a remote completion ends in the registry switch on the game thread; stay pending while it cannot answer.
				if (decision.Origin != Node && ClusterRuntime.RegistryUnreachable)
					throw new InvalidOperationException("Registry unreachable: respawn switch not attempted");
				lock (Sync) m_pendingRespawns.Remove(pending);
				MySandboxGame.Static?.Invoke(() =>
				{
					try
					{
						if (decision.Origin == Node)
						{
							using (MyEventContext.Set(new EndpointId(pending.UserId), null, isInvokedLocally: false))
								MyPlayerCollection.OnRespawnRequest(pending.JoinGame, newIdentity: false, pending.RespawnEntityId, null,
									null, null, null, null, realPlayer: true, pending.SerialId, pending.ModelName, new Color(pending.PackedColor));
							Console.WriteLine($"[ClusterPlugin] respawn pending completed operation={pending.OperationId:D} target=local");
						}
						else
						{
							FinishRemoteRespawn(pending.OperationId, pending.UserId, pending.SerialId, identityId,
								decision, pending.JoinGame, pending.ModelName, pending.PackedColor, failures: pending.Failures);
							Console.WriteLine($"[ClusterPlugin] respawn pending completed operation={pending.OperationId:D} target={decision.Origin}");
						}
					}
					catch (Exception exception)
					{
						Console.WriteLine("[ClusterPlugin] respawn pending completion failed: " + SecretRedactor.Redact(exception.Message));
					}
				}, "ClusterPlugin pending respawn completion");
			}
			catch (Exception exception)
			{
				// Pre-dispatch failure: the entry stays pending for the next interval.
				Console.WriteLine("[ClusterPlugin] respawn pending retry failed: " + SecretRedactor.Redact(exception.Message));
			}
			finally
			{
				Interlocked.Exchange(ref m_pendingAttemptInFlight, 0);
			}
		});
	}

	public static ClusterRespawnExecutionResult ExecuteExactRespawn(ClusterRespawnExecution request)
	{
		bool pod = !string.IsNullOrEmpty(request?.RespawnShipId);
		if (request == null || request.OperationId == Guid.Empty || request.UserId == 0 || request.SerialId < 0
			|| request.IdentityId <= 0 || request.Origin != Node || request.ModelName?.Length > 256
			|| request.RespawnShipId?.Length > 256
			|| !pod && (request.RespawnEntityId <= 0 || request.GridEntityId <= 0
				|| request.PartitionId == 0 || request.Generation == 0))
			throw new InvalidOperationException("Invalid remote respawn execution");
		if (Executions.TryGetValue(request.OperationId, out var executed))
		{
			if (executed.Request != request) throw new InvalidOperationException("Respawn operation payload changed");
			return executed.Result;
		}
		// A pod has no medbay anchor: it creates its own fresh partition here, claimed by the ordinary
		// ownership protocol after the spawn - there is no pre-existing ownership to validate.
		if (!pod)
		{
			string refusal = MedbayRefusal(request);
			if (refusal == "partition-moving")
			{
				Console.WriteLine($"[ClusterPlugin] respawn execute operation={request.OperationId:D} ready=false reason={refusal}");
				return new ClusterRespawnExecutionResult(false, false, 0);
			}
			if (refusal != null) throw new InvalidOperationException("Respawn partition ownership changed");
		}
		var playerId = new MyPlayer.PlayerId(request.UserId, request.SerialId);
		string notReady = AdoptSwitchedClient(request.OperationId, request.UserId, request.SerialId, request.IdentityId,
			request.ClientState, out MyPlayer player);
		if (notReady != null)
		{
			Console.WriteLine($"[ClusterPlugin] respawn execute operation={request.OperationId:D} ready=false reason={notReady}");
			return new ClusterRespawnExecutionResult(false, false, 0);
		}
		if (request.Preview)
		{
			// SE1-0122: the client is here to watch the medbay, not to respawn yet. Replay the preview request its
			// screen sent to the node it came from; its respawn click comes here and spawns locally.
			bool granted = (MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer)?.RequestSpawnPreviewForClient(
				request.GridEntityId, request.RespawnEntityId, new Endpoint(new EndpointId(request.UserId), 0)) == true;
			Console.WriteLine($"[ClusterPlugin] respawn execute operation={request.OperationId:D} preview granted={granted}");
			var previewed = new ClusterRespawnExecutionResult(true, true, 0);
			Remember(request, previewed);
			return previewed;
		}
		if (!InvokedOperations.Contains(request.OperationId))
		{
			Console.WriteLine($"[ClusterPlugin] respawn execute operation={request.OperationId:D}"
				+ (pod ? $" invoking-ship={request.RespawnShipId}" : $" invoking={request.RespawnEntityId}"));
			using (MyEventContext.Set(new EndpointId(request.UserId), null, isInvokedLocally: false))
				MyPlayerCollection.OnRespawnRequest(request.JoinGame, pod && request.NewIdentity,
					pod ? 0 : request.RespawnEntityId, pod ? request.RespawnShipId : null,
					null, null, null, null, realPlayer: true, request.SerialId, request.ModelName, new Color(request.PackedColor));
			InvokedOperations.Add(request.OperationId);
		}
		player = MySession.Static.Players.GetPlayerById(playerId);
		long controlledEntity = player?.Controller?.ControlledEntity?.Entity?.EntityId ?? player?.Character?.EntityId ?? 0;
		Console.WriteLine($"[ClusterPlugin] respawn execute operation={request.OperationId:D} invoked=true controlled={controlledEntity}");
		if (pod && controlledEntity == 0)
		{
			// The pod prefab spawns asynchronously over later frames - unlike a medbay respawn there is
			// no character to report yet. Not ready rather than failed: the Gateway keeps polling and the
			// invoke guard above makes those polls pure controlled-entity checks, never a second pod.
			Console.WriteLine($"[ClusterPlugin] respawn execute operation={request.OperationId:D} ready=false reason=pod-spawn-pending");
			return new ClusterRespawnExecutionResult(false, false, 0);
		}
		var result = new ClusterRespawnExecutionResult(true, player?.Identity?.IsDead == false && controlledEntity > 0, controlledEntity);
		Remember(request, result);
		return result;
	}

	/// <summary>
	/// SE-1124: why this node will not execute at the medbay's grid, or null when it holds the grid. The Gateway resolved
	/// the request's partition a moment ago, but a split or merge on this node renumbers that partition without moving the
	/// grid: a ship leaving the station splits it off (4448 into 4475/4476, octahedron-test 2026-11-05). Any partition
	/// this node owns will do. "partition-moving" while the grid is here but its new partition is not owned yet, so the
	/// Gateway's ready loop asks again instead of failing the switch.
	/// </summary>
	private static string MedbayRefusal(ClusterRespawnExecution request)
	{
		if (m_partitions.EntityPartitions().TryGetValue(request.GridEntityId, out ulong partition)
			&& m_persistence.OwnedGeneration(partition) != 0)
		{
			if (partition != request.PartitionId)
				Console.WriteLine($"[ClusterPlugin] respawn execute operation={request.OperationId:D} grid={request.GridEntityId}"
					+ $" partition-moved from={request.PartitionId} to={partition}");
			return null;
		}
		return MyEntities.EntityExists(request.GridEntityId) ? "partition-moving" : "grid-absent";
	}

	/// <summary>
	/// Gives a client that the Gateway switched to this node without a join handshake its replication view and its
	/// player here. Null when both are in place, else why not yet. Game thread. Used by the node a switch goes to,
	/// and since SE-1124 by the node a failed switch comes back to.
	/// </summary>
	/// <param name="ownBindings">SE-1189: the node a failed switch comes back to adopts the client with the bindings it has
	/// here. A switch that failed before the Gateway moved the backend leaves the player in place, its body still its
	/// character, and the detached state's empty bindings refused the restore: the client was disconnected and a Steam
	/// client sat "Paused" (octahedron-test 2026-11-06 19:07Z).</param>
	private static string AdoptSwitchedClient(Guid operationId, ulong userId, int serialId, long identityId, string clientState,
		out MyPlayer player, bool ownBindings = false)
	{
		var playerId = new MyPlayer.PlayerId(userId, serialId);
		player = MySession.Static?.Players.GetPlayerById(playerId);
		if (!MyPlayerCollectionPatch.IsCanonicalBinding(userId, serialId, identityId))
			throw new InvalidOperationException("Respawn player binding changed");
		// Adopted before the character exists; otherwise nothing is ever streamed to the client.
		if (!string.IsNullOrEmpty(clientState) && !AdoptedOperations.Contains(operationId))
		{
			if (!ReplicationServerPatch.HasClient(userId)) return "client-view-absent";
			int adopted = ClusterControlServer.AdoptClientView(userId, clientState, ownBindings);
			AdoptedOperations.Add(operationId);
			Console.WriteLine($"[ClusterPlugin] respawn execute operation={operationId:D} adopted={adopted}");
		}
		if (player == null)
		{
			MyIdentity identity = MySession.Static?.Players.TryGetIdentity(identityId);
			if (identity == null) return "identity-missing";
			player = MySession.Static.Players.CreateNewPlayer(identity, playerId, identity.DisplayPlatformName,
				realPlayer: true, initialPlayer: false, newIdentity: false);
			Console.WriteLine($"[ClusterPlugin] respawn execute operation={operationId:D} player-created={player != null}");
		}
		if (player.Identity?.IdentityId != identityId)
			throw new InvalidOperationException("Respawn player binding changed");
		return null;
	}

	private static void Remember(ClusterRespawnExecution request, ClusterRespawnExecutionResult result)
	{
		Executions.Add(request.OperationId, (request, result));
		ExecutionOrder.Enqueue(request.OperationId);
		if (ExecutionOrder.Count > 1024) Executions.Remove(ExecutionOrder.Dequeue());
		if (AdoptedOperations.Count > 1024) AdoptedOperations.Clear();
		if (InvokedOperations.Count > 1024) InvokedOperations.Clear();
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		if (!ClusterRuntime.IsWorldAuthority)
		{
			RestoreClients();
			RetryPendingRespawns();
		}
		if (ClusterRuntime.LocalMilliseconds < m_nextReport) return;
		m_nextReport = ClusterRuntime.LocalMilliseconds + 1000;
		ClusterRespawnPoint[] local = CaptureLocal();
		if (ClusterRuntime.IsWorldAuthority)
		{
			lock (Sync) Reports[Node] = new Report(ClusterRuntime.LocalMilliseconds, local);
			PublishMerged();
		}
		else
		{
			if (ClusterRuntime.LocalMilliseconds < m_testRawPointsUntil) local = local.Concat(Volatile.Read(ref m_testRawPoints)).ToArray();
			GlobalStateChannel.SendRequest(Subsystem, RespawnRegistryCodec.Write(local));
		}
	}

	public static void AuthorityChanged(long generation)
	{
		lock (Sync)
		{
			Reports.Clear();
			m_points = Array.Empty<ClusterRespawnPoint>();
			m_snapshot = RespawnRegistryCodec.Write(m_points);
		}
	}

	public static object Status()
	{
		lock (Sync) return new { count = m_points.Length, points = m_points.ToArray(), pending = m_pendingRespawns.Count };
	}

	/// <summary>
	/// Chaos-test knob mirroring the banking DelayMilliseconds one: the authority sleeps this long on its
	/// game thread inside every arbitration command, serializing later commands behind it, so a lane can
	/// kill the WA with a respawn arbitration deterministically in flight. Dies with the process.
	/// </summary>
	public static object ConfigureTestDelay(int milliseconds)
	{
		if (!ClusterRuntime.IsWorldAuthority)
			throw new InvalidOperationException("The respawn arbitration delay is a World Authority chaos knob");
		if (milliseconds is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
		Interlocked.Exchange(ref m_testDelayMilliseconds, milliseconds);
		return TestDelayStatus();
	}

	/// <summary>
	/// SE-1150 test knob: for <paramref name="seconds"/>, every report this node sends to the World Authority also carries
	/// <paramref name="points"/> as they are, past the capture's checks: what a node sent when one of its medbays
	/// misreported. Dies with the process.
	/// </summary>
	public static object ConfigureTestRawPoints(ClusterRespawnPoint[] points, double seconds)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority)
			throw new InvalidOperationException("Only a regular cluster node reports respawn points to the World Authority");
		Volatile.Write(ref m_testRawPoints, points ?? Array.Empty<ClusterRespawnPoint>());
		m_testRawPointsUntil = ClusterRuntime.LocalMilliseconds + seconds * 1000;
		return new { count = m_testRawPoints.Length, seconds };
	}

	public static object TestDelayStatus()
		=> new { delayMs = Volatile.Read(ref m_testDelayMilliseconds), held = Volatile.Read(ref m_testHeldCount) };

	public static ClusterRespawnDecision Arbitrate(ClusterRespawnRequest request, Guid operationId)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority)
			throw new InvalidOperationException("Respawn arbitration must originate at a regular cluster node");
		return RespawnRegistryCodec.ReadDecision(GlobalStateChannel.SendCommand(Subsystem,
			RespawnRegistryCodec.WriteRequest(request), operationId));
	}

	private static ClusterRespawnPoint[] CaptureLocal()
	{
		IReadOnlyDictionary<long, ulong> partitions = m_partitions.EntityPartitions();
		var points = new List<ClusterRespawnPoint>();
		var problems = new List<RespawnPointProblem>();
		foreach (MyRespawnComponent respawn in MyRespawnComponent.GetAllRespawns())
		{
			var entity = respawn.Entity;
			if (entity == null || entity.Closed || entity.CubeGrid == null
				|| !partitions.TryGetValue(entity.CubeGrid.EntityId, out ulong partition)) continue;
			uint generation = m_persistence.OwnedGeneration(partition);
			if (generation == 0) continue;
			entity.CubeGrid.GridSystems?.UpdatePower();
			entity.UpdateIsWorking();
			var position = entity.WorldMatrix.Translation;
			// SE-1150: one misreporting block must not cost the node its whole list at the World Authority.
			ClusterRespawnPoint point = RespawnRegistryCodec.Sanitize(new ClusterRespawnPoint(Node, entity.EntityId, entity.CubeGrid.EntityId,
				partition, generation, entity.CustomName?.ToString() ?? entity.Name ?? entity.ToString(), entity.IDModule?.Owner ?? 0,
				(int)(entity.IDModule?.ShareMode ?? VRage.Game.MyOwnershipShareModeEnum.None), entity.IsWorking,
				respawn.SpawnWithoutOxygen, entity is MyMedicalRoom { SetFactionToSpawnee: not false },
				respawn.GetOxygenLevel(), position.X, position.Y, position.Z), out RespawnPointProblem? problem);
			if (problem.HasValue) problems.Add(problem.Value);
			if (point != null) points.Add(point);
		}
		lock (m_capturedProblems)
			foreach (RespawnPointProblem problem in m_capturedProblems.Fresh("capture", problems))
				Console.WriteLine(RespawnProblemLog.Line("capture", Node, problem));
		return points.GroupBy(x => x.RespawnEntityId).Select(x => x.First()).OrderBy(x => x.RespawnEntityId).ToArray();
	}

	/// <summary>T-0193: the respawn points of one partition as this node reports them now - what an offline
	/// partition keeps on every respawn screen while no node hosts it.</summary>
	internal static ClusterRespawnPoint[] CaptureFor(ulong partition) => CaptureLocal().Where(x => x.PartitionId == partition).ToArray();

	private static void ReceiveReport(string origin, string payload)
	{
		var problems = new List<RespawnPointProblem>();
		ClusterRespawnSnapshot snapshot = RespawnRegistryCodec.Read(payload, problems);
		ClusterRespawnPoint[] points = snapshot.Points.Select(x => x with { Origin = origin }).ToArray();
		lock (Sync)
		{
			Reports[origin] = new Report(ClusterRuntime.LocalMilliseconds, points);
			foreach (RespawnPointProblem problem in m_receivedProblems.Fresh(origin, problems))
				Console.WriteLine(RespawnProblemLog.Line("authority", origin, problem));
		}
		PublishMerged();
	}

	private static string HandleCommand(string origin, string payload)
	{
		ClusterRespawnRequest request = RespawnRegistryCodec.ReadRequest(payload);
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		MySandboxGame game = MySandboxGame.Static ?? throw new InvalidOperationException("The World Authority game is unavailable");
		Action arbitrate = () =>
		{
			try
			{
				int delay = Volatile.Read(ref m_testDelayMilliseconds);
				if (delay > 0)
				{
					Interlocked.Increment(ref m_testHeldCount);
					Thread.Sleep(delay);
				}
				if (!MyPlayerCollectionPatch.IsCanonicalBinding(request.UserId, request.SerialId, request.IdentityId))
					throw new InvalidOperationException("Player identity is not bound at the World Authority");
				m_arbitrationWindow.Admit(request.IdentityId, request.RespawnEntityId, ClusterRuntime.LocalMilliseconds);
				ClusterRespawnPoint[] points;
				lock (Sync) points = m_points.ToArray();
				bool admin = MySession.Static.RemoteAdminSettings.TryGetValue(request.UserId, out AdminSettingsEnum settings)
					&& (settings & AdminSettingsEnum.UseTerminals) != 0;
				ClusterRespawnDecision decision = RespawnArbiter.Select(points, request, point => admin || point.SetFactionToSpawnee
					|| MyIDModule.GetRelationPlayerBlock(point.OwnerIdentityId, request.IdentityId,
						(VRage.Game.MyOwnershipShareModeEnum)point.ShareMode).IsFriendly());
				m_arbitrationWindow.Record(request.IdentityId, request.RespawnEntityId, ClusterRuntime.LocalMilliseconds);
				completion.SetResult(RespawnRegistryCodec.WriteDecision(decision));
			}
			catch (Exception exception) { completion.SetException(exception); }
		};
		if (Thread.CurrentThread == game.UpdateThread) arbitrate();
		else game.Invoke(arbitrate, "ClusterPlugin respawn arbitration");
		return completion.Task.GetAwaiter().GetResult();
	}

	private static void PublishMerged()
	{
		string snapshot;
		lock (Sync)
		{
			double cutoff = ClusterRuntime.LocalMilliseconds - 5000;
			foreach (string origin in Reports.Where(x => x.Value.Received < cutoff).Select(x => x.Key).ToArray()) Reports.Remove(origin);
			// T-0193: offline partitions' points FIRST, so a live report of the same room (the partition loaded)
			// wins the Last() below.
			m_points = OfflinePartitionService.RespawnPoints().Concat(Reports.OrderBy(x => x.Value.Received).ThenBy(x => x.Key, StringComparer.Ordinal)
				.SelectMany(x => x.Value.Points)).GroupBy(x => x.RespawnEntityId).Select(x => x.Last())
				.OrderBy(x => x.RespawnEntityId).ToArray();
			snapshot = RespawnRegistryCodec.Write(m_points);
			if (snapshot == m_snapshot) return;
			m_snapshot = snapshot;
		}
		try { GlobalStateChannel.Publish(Subsystem, snapshot); }
		catch (Exception exception) { Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=respawns error=" + SecretRedactor.Redact(exception.Message)); }
	}

	private static void ApplySnapshot(string payload)
	{
		var problems = new List<RespawnPointProblem>();
		ClusterRespawnSnapshot snapshot = RespawnRegistryCodec.Read(payload, problems);
		lock (Sync)
		{
			foreach (RespawnPointProblem problem in m_receivedProblems.Fresh("snapshot", problems))
				Console.WriteLine(RespawnProblemLog.Line("replica", "world-authority", problem));
			m_points = snapshot.Points;
			m_snapshot = payload;
		}
	}

	private static string Snapshot() { lock (Sync) return m_snapshot; }
	private sealed record Report(double Received, ClusterRespawnPoint[] Points);
	private sealed record PendingRespawn(Guid OperationId, ulong UserId, int SerialId, long RespawnEntityId,
		bool JoinGame, string ModelName, uint PackedColor, double Deadline, int Failures = 0);
	/// <summary>A client to take in again after a failed switch: <see cref="Retry"/> requeues a medbay respawn, a pod's
	/// failure is answered as a refused respawn, and a preview needs nothing more.</summary>
	private sealed record Restore(Guid OperationId, ulong UserId, int SerialId, long IdentityId, string ClientState,
		double Deadline, PendingRespawn Retry, bool Pod);
}
