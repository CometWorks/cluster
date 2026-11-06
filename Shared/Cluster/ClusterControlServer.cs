using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Replication;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ModAPI;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;
using VRage.Voxels;
using VRageMath;

namespace ClusterPlugin;

internal sealed partial class ClusterControlServer
{
	private static PartitionManager m_partitions;
	private static PartitionPersistence m_persistence;
	private static WorldAuthorityPersistence m_worldAuthorityPersistence;
	private static readonly Dictionary<Guid, Transfer> m_sourceTransfers = new Dictionary<Guid, Transfer>();
	private static readonly Dictionary<Guid, Transfer> m_targetTransfers = new Dictionary<Guid, Transfer>();
	private static readonly NetworkIdImportGuard m_networkImport = new NetworkIdImportGuard();
	/// <summary>tee.async: transferId -> the durable tee's outcome, read by /tee-status off the game thread.</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task<bool>> m_tees = new System.Collections.Concurrent.ConcurrentDictionary<Guid, Task<bool>>();
	private static int m_dropTransferAfterBytes;
	/// <summary>T-0340 step 5: two save files served at once, off the listener thread (ClusterProtocol.SaveFileWorker).</summary>
	private static readonly ClusterProtocol.SaveFileWorker m_saveFiles = new ClusterProtocol.SaveFileWorker(2, e => Log("ERROR: Cluster control failed: " + e));
	// Partitioning.md 6.10: abort is the only safe default; a production operator may choose to
	// warn-and-commit instead, because a stay-put partition is an alarm they must act on either way.
	private static readonly bool m_importCheckWarn =
		string.Equals(Environment.GetEnvironmentVariable("CLUSTER_IMPORT_CHECK"), "warn", StringComparison.OrdinalIgnoreCase);
	private HttpListener m_listener = new HttpListener();
	private Thread m_thread;
	private static byte[] m_adminTokenHash;
	private volatile bool m_running;

	public ClusterControlServer(PartitionManager partitions, PartitionPersistence persistence, WorldAuthorityPersistence worldAuthorityPersistence)
	{
		m_partitions = partitions;
		m_persistence = persistence;
		m_worldAuthorityPersistence = worldAuthorityPersistence;
	}

	/// <summary>
	/// Orchestration §4.2: the listener binds before registration so a failing node stays diagnosable;
	/// the partition services attach once registration succeeded and the game runtime exists. Until
	/// then every route except /cluster/node-state answers 503.
	/// </summary>
	internal static void Attach(PartitionManager partitions, PartitionPersistence persistence, WorldAuthorityPersistence worldAuthorityPersistence)
	{
		m_partitions = partitions;
		m_persistence = persistence;
		m_worldAuthorityPersistence = worldAuthorityPersistence;
	}

	public void Start(int port)
	{
		if (m_running)
		{
			return;
		}

		IPAddress bind = IPAddress.Parse(Environment.GetEnvironmentVariable("CLUSTER_CONTROL_BIND") ?? "127.0.0.1");
		RequireInternalBind(bind);
		if (!IPAddress.IsLoopback(bind))
		{
			string token = Environment.GetEnvironmentVariable("CLUSTER_ADMIN_TOKEN");
			if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("CLUSTER_ADMIN_TOKEN is required for non-loopback control");
			m_adminTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
		}
		// T-0321: a LAN bind listens on the loopback prefix too, so local tools keep today's unauthenticated
		// access; the credential is decided per request from the prefix it arrived on (Handle).
		string[] prefixes = ClusterProtocol.ClusterNet.ControlPrefixes(bind, port, "/cluster/");
		string host = string.Join(" and ", prefixes.Select(prefix => new Uri(prefix).Authority));
		Bind(prefixes);
		m_running = true;
		m_thread = new Thread(ListenLoop)
		{
			Name = "ClusterControl",
			IsBackground = true
		};
		m_thread.Start();
		Log("Cluster control listening on " + host);
	}

	//: A control port freed moments ago is not free yet. The rotator kills a node and
	//: relaunches it, and the dying process can still hold the port when the new one binds:
	//: w26 2026-09-02 15:08:55 lost node-5 to exactly that, twice, and the cluster ran with
	//: no hot spare until a manual start_node. The harness side now waits for the port
	//: (cluster-regression 690ba97); this is the other half, because a node that cannot bind
	//: for a few hundred milliseconds should cost a few hundred milliseconds, not itself.
	private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan BindRetryDelay = TimeSpan.FromMilliseconds(250);

	/// <summary>
	/// Bind the control listener, waiting out a port a previous incarnation has not released.
	/// </summary>
	/// <remarks>
	/// The managed HttpListener does not report a contended bind as AddressAlreadyInUse:
	/// HttpEndPointListener's constructor starts accepting before its lock object is assigned,
	/// so the contended path surfaces as ArgumentNullException out of Monitor.Enter(null),
	/// which reads like a bug anywhere but the port. That is why ArgumentNullException is
	/// caught here alongside the two exceptions a bind failure is supposed to raise.
	///
	/// A failed Start leaves the listener in whatever state it got to, so each attempt gets a
	/// fresh one rather than reusing the object that just threw. After BindTimeout the
	/// exception is rethrown unchanged: under Magnetar an exception escaping plugin start
	/// exits the node, which is the right outcome for a port that is genuinely taken - it is
	/// only the transient case this exists to survive.
	/// </remarks>
	private void Bind(string[] prefixes)
	{
		string prefix = string.Join(" ", prefixes);
		Stopwatch waited = Stopwatch.StartNew();
		int attempts = 0;
		while (true)
		{
			attempts++;
			try
			{
				foreach (string each in prefixes) m_listener.Prefixes.Add(each);
				m_listener.Start();
				// T-0324: without TCP_NODELAY a 16-65 KB response (/capture, /save-file, /partitions ...) waited ~44 ms
				// for the caller's delayed ACK on a same-host kept-alive connection.
				ClusterProtocol.HttpListenerNoDelay.Apply(m_listener, Log);
				if (attempts > 1)
				{
					Log("Cluster control bound " + prefix + " on attempt " + attempts
						+ " after " + waited.ElapsedMilliseconds + " ms of contention");
				}
				return;
			}
			catch (Exception e) when (e is HttpListenerException
				|| e is System.Net.Sockets.SocketException
				|| e is ArgumentNullException)
			{
				if (waited.Elapsed >= BindTimeout)
				{
					Log("Cluster control could not bind " + prefix + " in "
						+ waited.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
						+ " s over " + attempts + " attempts: " + e.GetType().Name + ": " + e.Message);
					throw;
				}
				m_listener = new HttpListener();
				Thread.Sleep(BindRetryDelay);
			}
		}
	}

	private static void RequireInternalBind(IPAddress bind)
	{
		if (IPAddress.IsLoopback(bind)) return;
		string[] networks = (Environment.GetEnvironmentVariable("CLUSTER_INTERNAL_NETWORKS") ?? string.Empty)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (networks.Any(value => InNetwork(bind, value))) return;
		throw new InvalidOperationException("Non-loopback cluster control bind is outside CLUSTER_INTERNAL_NETWORKS");
	}

	internal static bool InNetwork(IPAddress address, string value)
	{
		string[] parts = value.Split('/');
		if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress network) || !int.TryParse(parts[1], out int prefix)) return false;
		byte[] candidate = address.GetAddressBytes(), bytes = network.GetAddressBytes();
		if (candidate.Length != bytes.Length || prefix < 0 || prefix > bytes.Length * 8) return false;
		int whole = prefix / 8, bits = prefix % 8;
		if (!candidate.AsSpan(0, whole).SequenceEqual(bytes.AsSpan(0, whole))) return false;
		return bits == 0 || (candidate[whole] & (0xff << (8 - bits))) == (bytes[whole] & (0xff << (8 - bits)));
	}

	private void ListenLoop()
	{
		while (m_running)
		{
			try
			{
				HttpListenerContext context = m_listener.GetContext();
				long started = Stopwatch.GetTimestamp();
				// T-0340 step 5: a save file read (grant validation and the copy to the requester) runs on the save-file
				// worker; this thread goes back to accepting, so the node's other control requests do not wait behind it.
				if (m_saveFiles.Dispatch(context.Request.HttpMethod, context.Request.Url.AbsolutePath, () =>
					{
						Handle(context);
						CountServed(context, started);
					}))
					continue;
				Handle(context);
				CountServed(context, started);
			}
			catch (HttpListenerException)
			{
				return;
			}
			catch (Exception e)
			{
				Log("ERROR: Cluster control failed: " + e);
			}
		}
	}

	private static Func<HttpListenerContext, string, bool>[] m_routeExtensions = Array.Empty<Func<HttpListenerContext, string, bool>>();

	/// <summary>Register a route extension: called with the request and its trimmed path after
	/// authorization; returns true when it wrote the response. Used by the test utility plugin to
	/// serve test-only routes without shipping them in the product.</summary>
	internal static void AddRouteExtension(Func<HttpListenerContext, string, bool> extension)
	{
		lock (typeof(ClusterControlServer))
		{
			m_routeExtensions = m_routeExtensions.Append(extension ?? throw new ArgumentNullException(nameof(extension))).ToArray();
		}
	}

	private static void Handle(HttpListenerContext context)
	{
		// T-0411: a request the listener already answered with 411 runs nothing - its caller was told it failed (the gateway's
		// control listener applied a cost-model override behind such a 411, 2026-10-09 19:40:46).
		if (ClusterProtocol.HttpListenerLengthRequired.AlreadyAnswered(context.Request.HttpMethod,
				context.Request.Headers["Content-Length"], context.Request.Headers["Transfer-Encoding"]))
		{
			Log("Cluster control request refused: "
				+ ClusterProtocol.HttpListenerLengthRequired.Describe(context.Request.HttpMethod, context.Request.Url?.AbsolutePath));
			return;
		}
		try
		{
			string path = context.Request.Url.AbsolutePath.TrimEnd('/');
			if (!RequestAuthorized(context, path)) return;
			RequireRegistryGrant(context, path);
			// Extension routes (the test utility plugin, Test/ClusterTestPlugin): tried after the
			// authorization above and before the product routes, under the same error handling.
			foreach (Func<HttpListenerContext, string, bool> extension in m_routeExtensions)
			{
				if (extension(context, path))
					return;
			}
			// Orchestration §4.2: the node's lifecycle, readable without the game thread - the probe
			// P3-REC-01 sends while registration is failing, and the last thing a Failed node reports.
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/node-state", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
				{
					state = ClusterRuntime.NodeState,
					detail = ClusterRuntime.NodeStateDetail,
					pid = Environment.ProcessId,
					attached = m_partitions != null
				})));
				return;
			}
			// HandoverImprovements.md §2/§3: the runtime config plane and the instrumentation reads never
			// touch the game thread, so they answer while it is loading, stalled or mid-handover.
			if (path.EndsWith("/config", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod is "GET" or "PUT")
			{
				if (context.Request.HttpMethod == "PUT")
				{
					using StreamReader reader = new StreamReader(context.Request.InputStream);
					string body = reader.ReadToEnd();
					try
					{
						ClusterConfig.Apply(ClusterConfig.Parse(body));
					}
					catch (ArgumentException bad)
					{
						// A typo or a knob this build does not know is the caller's mistake, not a node
						// fault: answer 400 with just the message so the gateway's fan-out detail stays
						// readable (a 500 dumped a whole stack trace into every node's entry).
						Write(context, 400, "text/plain", Encoding.UTF8.GetBytes(bad.Message));
						return;
					}
				}
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(ClusterConfig.Json()));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/handover-stats", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(HandoverPhaseTimer.StatsJson()));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/hitches", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
				{
					thresholdMs = HitchRecorder.ThresholdMs, logThresholdMs = HitchRecorder.LogThresholdMs,
					total = HitchRecorder.Sequence, hitches = HitchRecorder.Snapshot()
				})));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/tee-status", StringComparison.OrdinalIgnoreCase))
			{
				Guid teeTransfer = RequiredTransferId(context);
				// Absent = captured with the tee awaited inside capture (tee.async off) or never captured
				// here; either way there is nothing outstanding for the gateway to wait for.
				Task<bool> tee = m_tees.TryGetValue(teeTransfer, out Task<bool> pendingTee) ? pendingTee : null;
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
				{
					known = tee != null, completed = tee == null || tee.IsCompleted,
					succeeded = tee == null || tee.IsCompletedSuccessfully && tee.Result,
					error = tee?.IsFaulted == true ? tee.Exception?.GetBaseException().Message : null
				})));
				return;
			}
			if (m_partitions == null)
			{
				Write(context, 503, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
				{
					error = "node is not serving", state = ClusterRuntime.NodeState, detail = ClusterRuntime.NodeStateDetail
				})));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/authority-state", StringComparison.OrdinalIgnoreCase))
			{
				long proposed = long.TryParse(context.Request.QueryString["generation"], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out long generation) ? generation : ClusterRuntime.AuthorityGeneration;
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
				{
					role = ClusterRuntime.IsWorldAuthority ? "WA" : "Regular",
					generation = ClusterRuntime.AuthorityGeneration,
					accepts = ClusterRuntime.AcceptAuthorityMessage(proposed),
					clockMilliseconds = ClusterRuntime.AuthorityClockMilliseconds
				})));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/global-state", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GlobalStateChannel.Status())));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/respawn-points", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(RespawnRegistryService.Status())));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/respawn-cooldown", StringComparison.OrdinalIgnoreCase))
			{
				ulong userId = ulong.Parse(context.Request.QueryString["userId"] ?? throw new ArgumentException("Missing userId query parameter"), CultureInfo.InvariantCulture);
				int serialId = int.Parse(context.Request.QueryString["serialId"] ?? "0", CultureInfo.InvariantCulture);
				string ship = context.Request.QueryString["ship"];
				string result = Invoke(() => JsonSerializer.Serialize(RespawnCooldownService.Status(userId, serialId, ship)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/self-test", StringComparison.OrdinalIgnoreCase))
			{
				// Pure functions of their input - no game state, so deliberately not marshalled onto
				// the game thread: a Tier 1 check must not be able to stall the simulation.
				Write(context, 200, "application/json",
					Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ClusterSelfTest.Run(context.Request.QueryString["section"]))));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/online-players", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(OnlinePlayersService.Status()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			// T-0178: what this node has actually installed on each of its own clients. The F3 list
			// itself is client-side GUI and cannot be asserted on from here, so the testable claim is
			// the state behind it - which remote players each attached client has been given a member
			// and a MyPlayer for. Read it together with /online-players: that says what the cluster
			// knows, this says what got delivered.
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/player-mirror", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(RemotePlayerMirrorService.Status()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/time-of-day", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(TimeOfDayService.Status()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			// The scenario-facing setter (P3-SUN-01): routes through the same node->WA path as the
			// vanilla admin slider, minus the client-side event. Off the game thread on purpose - a
			// node's forward is a blocking WA round trip.
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/time-of-day", StringComparison.OrdinalIgnoreCase))
			{
				if (!float.TryParse(context.Request.QueryString["minutes"], NumberStyles.Float, CultureInfo.InvariantCulture, out float minutes)
					|| !float.IsFinite(minutes))
					throw new ArgumentException("Missing or invalid query parameter 'minutes'");
				if (!TimeOfDayService.Set(minutes))
					throw new InvalidOperationException("Time of day is cluster state only on a clustered server");
				string result = Invoke(() => JsonSerializer.Serialize(TimeOfDayService.Status()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			// The replication layer's per-event validation drops are recorded only in an in-memory
			// ring (MyMultiplayerServerBase.ValidationFailed) - this is the one window into them.
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/validation-failures", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(
					Sandbox.Engine.Multiplayer.MyMultiplayerServerBase.GetFailedValidations()
						.Select(record => new { record.Id, record.PlayerId, record.TimeAgo, record.Explanation })));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/antenna-graph", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(AntennaGraphService.Describe()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/ship-antennas", StringComparison.OrdinalIgnoreCase))
			{
				// What ShipAntennaService is doing, which /antenna-graph cannot answer: the graph
				// shows the antennas that are ON, so "no ship antennas" reads the same whether the
				// rule is off, inert, or working on a fleet that happens to be parked.
				string result = Invoke(() => JsonSerializer.Serialize(ShipAntennaService.Status()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/import-guard", StringComparison.OrdinalIgnoreCase))
			{
				// T-0392 diagnostics: how often the sliced-activation guard (not the frozen mark) made each
				// world-writing service skip. Interlocked counters and a ConcurrentDictionary count - no game
				// state, so not marshalled onto the game thread.
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ImportActivationGuard.Status())));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/characters", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(PlayerStateService.Characters()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/close-character", StringComparison.OrdinalIgnoreCase))
			{
				long orphanEntityId = long.Parse(context.Request.QueryString["entityId"]
					?? throw new ArgumentException("Missing entityId query parameter"), CultureInfo.InvariantCulture);
				bool forceClose = string.Equals(context.Request.QueryString["force"], "true", StringComparison.OrdinalIgnoreCase);
				string result = Invoke(() => JsonSerializer.Serialize(PlayerStateService.CloseUnclaimedCharacter(orphanEntityId, forceClose)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/grid-move", StringComparison.OrdinalIgnoreCase))
			{
				// T-0037: relocate an unpiloted wreck off a berth. Refusals come back as moved=false with a reason.
				long moveEntityId = long.Parse(context.Request.QueryString["entityId"]
					?? throw new ArgumentException("Missing entityId query parameter"), CultureInfo.InvariantCulture);
				double moveX = double.Parse(context.Request.QueryString["x"] ?? throw new ArgumentException("Missing x query parameter"), CultureInfo.InvariantCulture);
				double moveY = double.Parse(context.Request.QueryString["y"] ?? throw new ArgumentException("Missing y query parameter"), CultureInfo.InvariantCulture);
				double moveZ = double.Parse(context.Request.QueryString["z"] ?? throw new ArgumentException("Missing z query parameter"), CultureInfo.InvariantCulture);
				string result = Invoke(() => JsonSerializer.Serialize(GridMoveService.Move(moveEntityId, moveX, moveY, moveZ)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/grid-stop", StringComparison.OrdinalIgnoreCase))
			{
				// T-0064: park a runaway unpiloted grid where it is (overrides off, dampeners on, speed cleared).
				long stopEntityId = long.Parse(context.Request.QueryString["entityId"]
					?? throw new ArgumentException("Missing entityId query parameter"), CultureInfo.InvariantCulture);
				// aiOnly=1: only switch off an active AI flight block (and dampeners on), even with a seat taken.
				bool aiOnly = context.Request.QueryString["aiOnly"] is "1" or "true";
				// force=1: the full stop even with a body seated (a pilot whose inputs no longer reach the ship).
				bool force = context.Request.QueryString["force"] is "1" or "true";
				string result = Invoke(() => JsonSerializer.Serialize(GridStopService.Stop(stopEntityId, aiOnly, force)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/grid-delete", StringComparison.OrdinalIgnoreCase))
			{
				// #1334: remove an unpiloted leftover grid on its owning node. name must equal its display name.
				long deleteEntityId = long.Parse(context.Request.QueryString["entityId"]
					?? throw new ArgumentException("Missing entityId query parameter"), CultureInfo.InvariantCulture);
				string deleteName = context.Request.QueryString["name"] ?? throw new ArgumentException("Missing name query parameter");
				string result = Invoke(() => JsonSerializer.Serialize(GridDeleteService.Delete(deleteEntityId, deleteName, m_partitions, m_persistence)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/autopilot-route", StringComparison.OrdinalIgnoreCase))
			{
				// T-0232: replace an active AI leg's route from the owning node (the pilot stands 2.5 km off,
				// out of the block's access range). Body {"positions": [[x,y,z],...], "names": [...]}.
				long routeEntityId = long.Parse(context.Request.QueryString["entityId"]
					?? throw new ArgumentException("Missing entityId query parameter"), CultureInfo.InvariantCulture);
				using StreamReader routeReader = new StreamReader(context.Request.InputStream);
				using JsonDocument routeBody = JsonDocument.Parse(routeReader.ReadToEnd());
				Vector3D[] routePositions = routeBody.RootElement.GetProperty("positions").EnumerateArray()
					.Select(position => new Vector3D(position[0].GetDouble(), position[1].GetDouble(), position[2].GetDouble()))
					.ToArray();
				string[] routeNames = routeBody.RootElement.GetProperty("names").EnumerateArray()
					.Select(name => name.GetString() ?? "").ToArray();
				string result = Invoke(() => JsonSerializer.Serialize(AutopilotRouteService.Replace(routeEntityId, routePositions, routeNames)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/autopilot-threshold", StringComparison.OrdinalIgnoreCase))
			{
				// T-0054: waypoint advance distance for a grid's AI Flight autopilot on this node.
				long thresholdGridId = long.Parse(context.Request.QueryString["entityId"]
					?? throw new ArgumentException("Missing entityId query parameter"), CultureInfo.InvariantCulture);
				float thresholdMeters = float.Parse(context.Request.QueryString["meters"]
					?? throw new ArgumentException("Missing meters query parameter"), CultureInfo.InvariantCulture);
				string result = Invoke(() => JsonSerializer.Serialize(AutopilotStateService.SetThreshold(thresholdGridId, thresholdMeters)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/autopilot-state", StringComparison.OrdinalIgnoreCase))
			{
				// T-0054: the AI Flight autopilot's server-side decisions for a grid on this node.
				long autopilotGridId = long.Parse(context.Request.QueryString["entityId"]
					?? throw new ArgumentException("Missing entityId query parameter"), CultureInfo.InvariantCulture);
				string result = Invoke(() => JsonSerializer.Serialize(AutopilotStateService.Status(autopilotGridId)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/client-view", StringComparison.OrdinalIgnoreCase))
			{
				// T-0225: what this node believes a client holds of one grid's replicable subtree. Read-only.
				ulong viewClient = ulong.Parse(context.Request.QueryString["client"]
					?? throw new ArgumentException("Missing client query parameter"), CultureInfo.InvariantCulture);
				long viewGridId = long.Parse(context.Request.QueryString["entityId"]
					?? throw new ArgumentException("Missing entityId query parameter"), CultureInfo.InvariantCulture);
				string result = Invoke(() => JsonSerializer.Serialize(ClientViewService.Status(viewClient, viewGridId)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/player-state", StringComparison.OrdinalIgnoreCase))
			{
				long stateIdentityId = long.Parse(context.Request.QueryString["identityId"] ?? throw new ArgumentException("Missing identityId query parameter"), CultureInfo.InvariantCulture);
				string result = Invoke(() => JsonSerializer.Serialize(PlayerStateService.Status(stateIdentityId)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/reassert-control", StringComparison.OrdinalIgnoreCase))
			{
				ulong controlClient = ulong.Parse(context.Request.QueryString["client"]
					?? throw new ArgumentException("Missing client query parameter"), CultureInfo.InvariantCulture);
				string result = Invoke(() => JsonSerializer.Serialize(ControlReassertService.RepairClient(controlClient)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/respawn-execute", StringComparison.OrdinalIgnoreCase))
			{
				ClusterRespawnExecution request = JsonSerializer.Deserialize<ClusterRespawnExecution>(context.Request.InputStream,
					new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
					?? throw new ArgumentException("Request body is required");
				ClusterSeams.BeforeRespawnExecute?.Invoke(request);
				string result = Invoke(() => JsonSerializer.Serialize(RespawnRegistryService.ExecuteExactRespawn(request)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/global-state/publish", StringComparison.OrdinalIgnoreCase))
			{
				string subsystem = context.Request.QueryString["subsystem"] ?? throw new ArgumentException("Missing subsystem query parameter");
				string payload = context.Request.QueryString["payload"] ?? string.Empty;
				long? generation = long.TryParse(context.Request.QueryString["generation"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
				bool skip = bool.TryParse(context.Request.QueryString["skipSequence"], out bool parsedSkip) && parsedSkip;
				GlobalStateChannel.Publish(subsystem, payload, generation, skip);
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GlobalStateChannel.Status())));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/chat-send", StringComparison.OrdinalIgnoreCase))
			{
				// Production cluster-chat entry for the Gateway admin API (Docs/Gateway/AdminAPI.md).
				// Same accept path as a player message; never a side channel.
				ulong chatSender = ulong.Parse(context.Request.QueryString["sender"] ?? throw new ArgumentException("Missing sender query parameter"), CultureInfo.InvariantCulture);
				string chatText = context.Request.QueryString["text"] ?? throw new ArgumentException("Missing text query parameter");
				bool accepted = ChatAuthorityPatch.SendFromControl(chatSender, chatText);
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { accepted })));
				return;
			}
			// #1316: the World Authority's held ship storage takes and the admin settle of a stranded one, for the
			// Gateway's /admin/v1/ship-storage routes. A refused settle is a 409 with the reason.
			if (path.EndsWith("/ship-storage/takes", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith("/ship-storage/settle", StringComparison.OrdinalIgnoreCase))
			{
				if (!ClusterRuntime.IsWorldAuthority) throw new InvalidOperationException("Ship storage takes are held at the World Authority");
				if (context.Request.HttpMethod == "GET" && path.EndsWith("/takes", StringComparison.OrdinalIgnoreCase))
				{
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ShipStorageService.HeldTakes(DateTime.UtcNow))));
					return;
				}
				if (context.Request.HttpMethod == "POST" && path.EndsWith("/settle", StringComparison.OrdinalIgnoreCase))
				{
					Guid ship = Guid.Parse(context.Request.QueryString["id"] ?? throw new ArgumentException("Missing id query parameter"));
					Guid take = Guid.Parse(context.Request.QueryString["take"] ?? throw new ArgumentException("Missing take query parameter"));
					string action = context.Request.QueryString["action"];
					if (action is not ("release" or "consume")) throw new ArgumentException("action is release or consume");
					// Off the game thread, like the World Authority's command handlers.
					try { ShipStorageService.Settle(ship, take, action == "consume"); }
					catch (InvalidOperationException refused)
					{
						WriteText(context, 409, refused.Message + "\n");
						return;
					}
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { settled = action })));
					return;
				}
			}
			// T-0347: the cluster stop's fallback when the registry is unreachable (PartitionPersistence.RequestLocalStop).
			// Only sets flags; the game thread writes the Finals and ends the process.
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/local-stop", StringComparison.OrdinalIgnoreCase))
			{
				if (m_persistence == null) throw new InvalidOperationException("The node has no partition persistence yet");
				// It owns no partitions and would exit at once, under the nodes it serves; the stop does not ask it.
				if (ClusterRuntime.IsWorldAuthority) throw new InvalidOperationException("The World Authority has no local stop");
				double stopBudget = double.TryParse(context.Request.QueryString["budget"], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedBudget)
					? parsedBudget : 60;
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m_persistence.RequestLocalStop(stopBudget))));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/shutdown-notice", StringComparison.OrdinalIgnoreCase))
			{
				// Graceful cluster shutdown, step 2 (ClusterRegistry DirectGateway.NotifyAndReturnPlayers):
				// a server-authored global chat line, sent at the World Authority like ClusterPresenceAnnouncer.
				string noticeText = context.Request.QueryString["text"] ?? throw new ArgumentException("Missing text query parameter");
				if (!ClusterRuntime.IsWorldAuthority)
					throw new InvalidOperationException("shutdown-notice is sent at the World Authority");
				bool accepted = ChatAuthorityPatch.SendFromControl(Sync.MyId, noticeText);
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { accepted })));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/return-to-menu", StringComparison.OrdinalIgnoreCase))
			{
				// Graceful cluster shutdown, step 3: every client attached here goes back to the main menu.
				// Answers the ids sent by this call, space-separated (empty: nobody new).
				ulong frontServerId = ulong.TryParse(context.Request.QueryString["serverId"], NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsedFront) ? parsedFront : 0;
				List<ulong> returned = Invoke(() => ShutdownReturnToMenu.ReturnAttachedClients(frontServerId));
				Write(context, 200, "text/plain", Encoding.UTF8.GetBytes(string.Join(" ", returned.Select(id => id.ToString(CultureInfo.InvariantCulture)))));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/network-id-prefetch", StringComparison.OrdinalIgnoreCase))
			{
				// T-0268 test trigger (admin token, like every route here): the network-id block prefetch NextId runs at
				// 75% of a block, now. Off the game thread: the generator has its own lock, and NextId takes it too.
				NetworkIdRangeGenerator generator = ClusterRuntime.NetworkIds ?? throw new InvalidOperationException("Node is not registered yet");
				NetworkIdBlock? block = generator.PrefetchNow();
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(block is NetworkIdBlock drawn
					? new { allocated = true, start = drawn.Start, end = drawn.End }
					: new { allocated = false, start = 0u, end = 0u })));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/test/character-item", StringComparison.OrdinalIgnoreCase))
			{
				// T-0265 test trigger: add (amount=1) or remove (amount=-1) one hand-tool item in a client's OWN character
				// inventory, server-side. That inventory syncs to its owner always (MyEntityInventoryStateGroup
				// GetGroupPriority), so the change reaches the client as one inventory delta; the client applies it only
				// if its expected message id matches. Remote's POST /character/equip <subtype> searches the CLIENT's
				// inventory, so it is the client-view observable (ITEM_NOT_FOUND = delta discarded).
				ulong client = ulong.Parse(context.Request.QueryString["client"] ?? throw new ArgumentException("Missing client"), CultureInfo.InvariantCulture);
				string subtype = context.Request.QueryString["subtype"] ?? throw new ArgumentException("Missing subtype");
				int amount = int.Parse(context.Request.QueryString["amount"] ?? "1", CultureInfo.InvariantCulture);
				object result = Invoke(() => TestCharacterItem(client, subtype, amount));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result)));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/chat-history", StringComparison.OrdinalIgnoreCase))
			{
				long chatCursor = long.TryParse(context.Request.QueryString["cursor"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedChatCursor) ? parsedChatCursor : 0;
				int chatLimit = int.TryParse(context.Request.QueryString["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedChatLimit) ? parsedChatLimit : 100;
				(IReadOnlyList<ChatAuthorityPatch.HistoryEntry> entries, long nextCursor) = ChatAuthorityPatch.ReadHistory(chatCursor, chatLimit);
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
				{
					messages = entries.Select(entry => new { seq = entry.Seq, at = entry.At, author = entry.Author, channel = entry.Channel, text = entry.Text }).ToArray(),
					nextCursor,
				})));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/spawn-execute", StringComparison.OrdinalIgnoreCase))
			{
				using StreamReader spawnReader = new StreamReader(context.Request.InputStream);
				ClusterSpawnRequest spawnRequest = SpawnDirectorCodec.ReadRequest(spawnReader.ReadToEnd());
				string result = Invoke(() => JsonSerializer.Serialize(SpawnDirectorService.Execute(spawnRequest)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/igc-deliver", StringComparison.OrdinalIgnoreCase))
			{
				using StreamReader igcReader = new StreamReader(context.Request.InputStream);
				ClusterIgcDelivery[] igcDeliveries = IgcDeliveryCodec.ReadRequest(igcReader.ReadToEnd());
				string result = Invoke(() => JsonSerializer.Serialize(igcDeliveries.Select(IgcDeliveryService.Deliver).ToArray()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/igc-status", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(IgcDeliveryService.Status())));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/mod-deliver", StringComparison.OrdinalIgnoreCase))
			{
				using StreamReader modReader = new StreamReader(context.Request.InputStream);
				ClusterModDelivery modDelivery = ClusterModChannelCodec.Read(modReader.ReadToEnd());
				string result = Invoke(() => JsonSerializer.Serialize(ClusterModChannelService.Deliver(modDelivery)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			// #1280: one node's view of an entity for the mod channel's owner routing (owner-of, to-owner).
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/mod-locate", StringComparison.OrdinalIgnoreCase))
			{
				long modEntity = long.Parse(context.Request.QueryString["entity"]
					?? throw new ArgumentException("Missing entity query parameter"), System.Globalization.CultureInfo.InvariantCulture);
				string located = Invoke(() => JsonSerializer.Serialize(new { entity = modEntity, residence = ClusterModChannelService.ResidenceOf(modEntity) }));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(located));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/mod-status", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ClusterModChannelService.Status())));
				return;
			}
			// T-0242 (#260): per-mod class, placement and load record of this process, and its mod file-write counters.
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/mod-compat", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ModCompatService.Status())));
				return;
			}
			// Operational A/B tooling (NodeAPI.md), not a test route: ~/ws/se/se1/cluster-regression/bin/antenna-ab
			// drives the regression cluster with it. Stays in the product on purpose - CleanupPlan.md's
			// deviation note, alongside /self-test.
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/igc-test-antenna", StringComparison.OrdinalIgnoreCase))
			{
				long antennaGrid = QueryLong(context, "grid");
				bool antennaEnabled = (context.Request.QueryString["enabled"] ?? throw new ArgumentException("Missing enabled query parameter")) == "true";
				string result;
				try
				{
					result = Invoke(() => JsonSerializer.Serialize(AntennaGraphService.SetAntennaEnabled(antennaGrid, antennaEnabled)));
				}
				catch (InvalidOperationException e) when (e.InnerException is KeyNotFoundException
					|| e.InnerException is InvalidOperationException inner && inner.Message.EndsWith("has no antenna", StringComparison.Ordinal))
				{
					// The two expected outcomes of a config sweep - the grid moved to another node since the
					// sweep's snapshot, or it has no antenna - are answered, not logged: harness-up's
					// `antenna-ab config` made ~930 of these stack traces per start (08:09:11 2026-09-26:
					// 238 'No grid', 226 'no antenna' on one node) and they inflated fleet-sag's node_exc.
					// antenna-ab retries a 404 and counts a body with "has no antenna" as nothing to set.
					WriteText(context, e.InnerException is KeyNotFoundException ? 404 : 409, e.InnerException.Message + "\n");
					return;
				}
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/encounter-dispatch", StringComparison.OrdinalIgnoreCase))
			{
				string encounterGroup = context.Request.QueryString["spawnGroup"];
				ulong.TryParse(context.Request.QueryString["client"], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong encounterClient);
				// Dispatch blocks on the owning node's execution, so it must not hold the WA game thread.
				string encounterKind = context.Request.QueryString["kind"] ?? ClusterEncounterKinds.Global;
				Guid encounterOperation = Guid.TryParse(context.Request.QueryString["operationId"], out Guid parsedEncounterOperation)
					? parsedEncounterOperation : Guid.Empty;
				string result = JsonSerializer.Serialize(EncounterDirectorService.DispatchNow(encounterGroup, encounterClient, encounterKind, encounterOperation));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/encounter-execute", StringComparison.OrdinalIgnoreCase))
			{
				using StreamReader encounterReader = new StreamReader(context.Request.InputStream);
				ClusterEncounterRequest encounterRequest = EncounterDirectorCodec.ReadRequest(encounterReader.ReadToEnd());
				string result = Invoke(() => JsonSerializer.Serialize(EncounterDirectorService.Execute(encounterRequest)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/position-registry", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json",
					Encoding.UTF8.GetBytes(JsonSerializer.Serialize(PositionRegistryService.Players)));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/transfer-offer-execute", StringComparison.OrdinalIgnoreCase))
			{
				using StreamReader offerReader = new StreamReader(context.Request.InputStream);
				string offerPayload = offerReader.ReadToEnd();
				string result = Invoke(() => JsonSerializer.Serialize(GridTransferService.ExecuteOffer(offerPayload)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/transfer-accept-execute", StringComparison.OrdinalIgnoreCase))
			{
				using StreamReader acceptReader = new StreamReader(context.Request.InputStream);
				string acceptPayload = acceptReader.ReadToEnd();
				string result = Invoke(() => JsonSerializer.Serialize(GridTransferService.ExecuteAccept(acceptPayload)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/encounter-status", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(EncounterDirectorService.Status())));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/encounter-count", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(EncounterDirectorService.Encounters()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/weather", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(WeatherAuthorityService.Status()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/encounter-grids", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(EncounterDirectorService.EncounterGrids()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/drop-container-dispatch", StringComparison.OrdinalIgnoreCase))
			{
				if (!long.TryParse(context.Request.QueryString["identity"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long dropIdentity) || dropIdentity == 0)
					throw new ArgumentException("Invalid identity query parameter");
				bool dropCompetitive = context.Request.QueryString["competitive"] == "true";
				// Dispatch blocks on the owning node's execution, so it must not hold the WA game thread.
				string result = JsonSerializer.Serialize(DropContainerDirectorService.DispatchNow(dropIdentity, dropCompetitive));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/drop-container-execute", StringComparison.OrdinalIgnoreCase))
			{
				using StreamReader dropReader = new StreamReader(context.Request.InputStream);
				ClusterDropContainerRequest dropRequest = DropContainerDirectorCodec.ReadRequest(dropReader.ReadToEnd());
				string result = Invoke(() => JsonSerializer.Serialize(DropContainerDirectorService.Execute(dropRequest)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/drop-container-status", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(DropContainerDirectorService.Status())));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/drop-container-grids", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(DropContainerDirectorService.Grids()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/drop-container-removals", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(DropContainerRemovalService.Removals()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/drop-container-gps", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(ContainerGpsSweep.Status()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/drop-container-result", StringComparison.OrdinalIgnoreCase))
			{
				if (!Guid.TryParse(context.Request.QueryString["operation"], out Guid dropOperation))
					throw new ArgumentException("Invalid operation query parameter");
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(DropContainerDirectorService.Result(dropOperation))));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/spawn-result", StringComparison.OrdinalIgnoreCase))
			{
				if (!Guid.TryParse(context.Request.QueryString["operation"], out Guid spawnOperation))
					throw new ArgumentException("Invalid operation query parameter");
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(SpawnDirectorService.Result(spawnOperation))));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/spawn-grid", StringComparison.OrdinalIgnoreCase))
			{
				if (!long.TryParse(context.Request.QueryString["grid"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long spawnGridId) || spawnGridId == 0)
					throw new ArgumentException("Invalid grid query parameter");
				// Proves a dispatched spawn produced a real grid on the node that executed it. block-integrity
				// cannot answer this: it reports exists=false both for a missing grid and for a grid with no
				// block at the probed position.
				string result = Invoke(() => JsonSerializer.Serialize(
					MyEntities.TryGetEntityById(spawnGridId, out MyEntity spawnEntity) && spawnEntity is MyCubeGrid spawnGrid
						? new { exists = true, blocks = spawnGrid.BlocksCount, name = spawnGrid.DisplayName }
						: new { exists = false, blocks = 0, name = (string)null }));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/spawn-prefabs", StringComparison.OrdinalIgnoreCase))
			{
				int prefabLimit = int.TryParse(context.Request.QueryString["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit) ? parsedLimit : 16;
				// Read the definitions directly: they are immutable once loaded, and marshalling to a busy game
				// thread only invites the timeout this endpoint has no reason to risk.
				string result = JsonSerializer.Serialize(SpawnDirectorService.Prefabs(prefabLimit));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/spawn-status", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(SpawnDirectorService.Status())));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/voxel-journal/append", StringComparison.OrdinalIgnoreCase))
			{
				using MemoryStream stream = new MemoryStream();
				context.Request.InputStream.CopyTo(stream);
				VoxelJournalCodec.Entry entry = VoxelJournalService.Instance.Accept(stream.ToArray());
				WriteText(context, 200, entry.Sequence.ToString(CultureInfo.InvariantCulture) + "\n");
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/voxel-journal", StringComparison.OrdinalIgnoreCase))
			{
				string storage = context.Request.QueryString["storage"] ?? throw new ArgumentException("Missing storage query parameter");
				long after = long.Parse(context.Request.QueryString["after"] ?? "0", CultureInfo.InvariantCulture);
				byte[] body = VoxelJournalService.Instance.Next(storage, after);
				Write(context, body == null ? 204 : 200, "application/octet-stream", body ?? Array.Empty<byte>());
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/voxel-journal-tails", StringComparison.OrdinalIgnoreCase))
			{
				string tails = VoxelJournalService.Instance.Tails(context.Request.QueryString["version"]);
				Write(context, tails == null ? 304 : 200, "application/json", tails == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(tails));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/global-save-file", StringComparison.OrdinalIgnoreCase))
			{
				uint generation = uint.Parse(context.Request.QueryString["generation"] ?? throw new ArgumentException("Missing generation query parameter"), CultureInfo.InvariantCulture);
				uint sequence = uint.Parse(context.Request.QueryString["sequence"] ?? throw new ArgumentException("Missing sequence query parameter"), CultureInfo.InvariantCulture);
				// T-0340: the node's saves directory is its host's store when the registry said so (ClusterRuntime.SaveRoot).
				// A global save that was not moved into the store is still in this node's data dir, and catalogued there.
				string file = ClusterProtocol.HostSaveStore.Held(ClusterProtocol.HostSaveStore.GlobalSave(ClusterRuntime.SaveRoot(), generation, sequence),
					ClusterProtocol.HostSaveStore.GlobalSave(ClusterRuntime.LegacySaveRoot(), generation, sequence));
				if (!File.Exists(file)) throw new InvalidOperationException("This node does not hold global save " + generation + "/" + sequence);
				// T-0397: streamed, as the partition save route is (T-0340 step 5). File.ReadAllBytes put every ~290 KB
				// global on the large-object heap of the serving process, once per copy: ~1,000-2,800 copies per recycle.
				using (FileStream body = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920))
				{
					long length = body.Length;
					context.Response.StatusCode = 200;
					context.Response.ContentType = "application/octet-stream";
					context.Response.ContentLength64 = length;
					try
					{
						ClusterProtocol.SaveFileStream.Copy(body, context.Response.OutputStream, length);
						context.Response.Close();
					}
					catch (Exception failed) when (failed is not HttpListenerException)
					{
						// Headers are on the wire: cut the connection so the fetcher sees a short body and takes the next holder.
						Log("global-save-file: " + generation + "/" + sequence + " failed mid-body: " + failed.Message);
						context.Response.Abort();
					}
				}
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/voxel-base-file", StringComparison.OrdinalIgnoreCase))
			{
				string storage = context.Request.QueryString["storage"] ?? throw new ArgumentException("Missing storage query parameter");
				long version = long.Parse(context.Request.QueryString["version"] ?? throw new ArgumentException("Missing version query parameter"), CultureInfo.InvariantCulture);
				Write(context, 200, "application/octet-stream", VoxelJournalService.Instance.ReadBaseFile(storage, version));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/voxel-compact", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(Invoke(VoxelJournalService.Instance.Compact)));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/voxel-storages", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(Invoke(VoxelStoragesJson)));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/voxel-journal-entries", StringComparison.OrdinalIgnoreCase))
			{
				string entriesStorage = context.Request.QueryString["storage"] ?? throw new ArgumentException("Missing storage query parameter");
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(VoxelJournalService.Instance.EntriesFor(entriesStorage)));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/voxel-journal-status", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(VoxelJournalService.Instance.Status()));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/voxel-range-hash", StringComparison.OrdinalIgnoreCase))
			{
				string storage = context.Request.QueryString["storage"] ?? throw new ArgumentException("Missing storage query parameter");
				Vector3I min = QueryVector(context);
				int size = int.Parse(context.Request.QueryString["size"] ?? "4", CultureInfo.InvariantCulture);
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(Invoke(() => VoxelRangeHash(storage, min, size))));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/save-file", StringComparison.OrdinalIgnoreCase))
			{
				ulong partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition query parameter"));
				uint generation = uint.Parse(context.Request.QueryString["generation"] ?? throw new ArgumentException("Missing generation query parameter"));
				uint sequence = uint.Parse(context.Request.QueryString["sequence"] ?? throw new ArgumentException("Missing sequence query parameter"));
				long offset = long.Parse(context.Request.QueryString["offset"] ?? "0");
				// T-0323: location= - this node is the proxy of a stopped holder on its host; RequireRegistryGrant has
				// checked that the grant binds exactly this location to this save and this node.
				// T-0340: a location that is this host's store path of the save is a host grant's - the file is read by save
				// id, its path computed here; any other location is a stopped node's (the realpath-checked proxy read).
				string proxied = context.Request.QueryString["location"];
				// T-0340 step 5: streamed from the open file, on the save-file worker's thread (ListenLoop), not read whole.
				FileStream body;
				try
				{
					body = proxied != null && !m_persistence.IsStoreLocation(proxied, partition, generation, sequence)
						? m_persistence.OpenProxiedSave(proxied, offset) : m_persistence.OpenSave(partition, generation, sequence, offset);
				}
				catch (IOException missing) when (ClusterProtocol.ProxiedSavePath.IsMissing(missing))
				{
					// T-0348: the catalogued file is not on this disk. That was a generic 500, which the requester could
					// not tell from any other failure: it reported nothing and was handed the same save again every poll.
					Log("save-file: " + (proxied ?? partition + "/" + generation + "/" + sequence) + " is not on disk; answering "
						+ ClusterProtocol.ProxiedSavePath.MissingStatus);
					WriteText(context, ClusterProtocol.ProxiedSavePath.MissingStatus, "save file not found\n");
					return;
				}
				using (body)
				{
					long length = body.Length - offset;
					int drop = Interlocked.Exchange(ref m_dropTransferAfterBytes, 0);
					context.Response.StatusCode = 200;
					context.Response.ContentType = "application/octet-stream";
					context.Response.ContentLength64 = length;
					try
					{
						if (drop > 0 && drop < length)
						{
							ClusterProtocol.SaveFileStream.Copy(body, context.Response.OutputStream, drop);
							context.Response.Abort();
						}
						else
						{
							ClusterProtocol.SaveFileStream.Copy(body, context.Response.OutputStream, length);
							context.Response.Close();
						}
					}
					catch (Exception failed) when (failed is not HttpListenerException)
					{
						// The headers are on the wire: no error answer can follow them. Cut the connection, so the requester
						// sees a short body at once (it resumes or takes the next holder) instead of waiting out its timeout.
						Log("save-file: " + (proxied ?? partition + "/" + generation + "/" + sequence) + " failed mid-body: " + failed.Message);
						context.Response.Abort();
					}
				}
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/capture", StringComparison.OrdinalIgnoreCase))
			{
				bool clientless = bool.TryParse(context.Request.QueryString["clientless"], out bool parsedClientless) && parsedClientless;
				if (clientless && context.Request.QueryString["clients"] != null) throw new ArgumentException("Clientless capture cannot include a client");
				ulong[] clients = clientless ? new ulong[0] : ParseClients(context.Request.QueryString["clients"]
					?? throw new ArgumentException("Missing clients query parameter"));
				if (!clientless && clients.Length == 0) throw new ArgumentException("Capture needs at least one client unless clientless");
				ulong partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition query parameter"));
				Guid? transferId = OptionalTransferId(context);
				uint? generation = OptionalGeneration(context, transferId);
				bool allowRemoteUse = context.Request.QueryString["allowRemoteUse"] == "true";
				byte[] body = InvokePhase("capture", transferId, partition, () => Capture(clients, partition, transferId, generation, allowRemoteUse));
				// HandoverDelta: the target staged this partition from baseline ?presync=ID; answer with the
				// patch against it. A missing baseline (expired, or a different partition) means the full
				// capture, logged, never an error: the freeze already happened.
				string presync = context.Request.QueryString["presync"];
				bool full = bool.TryParse(context.Request.QueryString["full"], out bool parsedFull) && parsedFull;
				if (!full && presync != null && transferId.HasValue && Guid.TryParse(presync, out Guid presyncId)
					&& m_sourceTransfers.TryGetValue(transferId.Value, out Transfer deltaTransfer))
				{
					if (deltaTransfer.DeltaCapture != null) body = deltaTransfer.DeltaCapture;
					else if (m_baselines.TryGetValue(presyncId, out Baseline baseline) && baseline.Partition == partition && deltaTransfer.Builders != null)
						body = deltaTransfer.DeltaCapture = DeltaCapture(deltaTransfer, baseline, null);
					else Log("capture presync=" + presync + " partition=" + partition + " has no baseline on this node; answering with the full capture");
				}
				Write(context, 200, "application/octet-stream", body);
				return;
			}

			if (context.Request.HttpMethod == "GET" && path.EndsWith("/snapshot", StringComparison.OrdinalIgnoreCase))
			{
				ulong partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition query parameter"));
				(byte[] file, Guid id, int entities) = Snapshot(partition);
				context.Response.Headers["X-Presync-Id"] = id.ToString("D");
				context.Response.Headers["X-Presync-Entities"] = entities.ToString(CultureInfo.InvariantCulture);
				Write(context, 200, "application/octet-stream", file);
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.EndsWith("/presync", StringComparison.OrdinalIgnoreCase))
			{
				ulong partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition query parameter"));
				string source = context.Request.QueryString["source"] ?? throw new ArgumentException("Missing source query parameter");
				string result = Presync(partition, source);
				Write(context, 200, "application/json", System.Text.Encoding.UTF8.GetBytes(result));
				return;
			}

			if (context.Request.HttpMethod == "GET" && path.EndsWith("/partitions", StringComparison.OrdinalIgnoreCase))
			{
				string report = Invoke(m_partitions.ReportJson);
				Write(context, 200, "application/json", System.Text.Encoding.UTF8.GetBytes(report));
				return;
			}


			if (context.Request.HttpMethod == "GET" && path.EndsWith("/control-liveness", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ControlReassertService.LivenessStatus())));
				return;
			}

			// WorldAuthority.md 5.5: the character registry as this node sees it - the live-instance map, the
			// settings of record, and the duplicate audit's verdict.
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/character-registry", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(CharacterRegistryService.Status())));
				return;
			}

			// The per-player settings of record as this node sees them: the World Authority's own record on
			// the WA, the replicated copy on a regular node, plus which players this node has reconciled.
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/player-profiles", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(PlayerProfileService.Status())));
				return;
			}

			// Orchestration.md 11.1: this node's section of the cluster diag snapshot, captured on the game
			// thread at a frame boundary. Read-only by construction - there is no mutating verb here.
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/diag", StringComparison.OrdinalIgnoreCase))
			{
				string depth = context.Request.QueryString["depth"] ?? DiagSnapshot.DepthPartitions;
				ulong? diagPartition = ulong.TryParse(context.Request.QueryString["partition"], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out ulong parsedPartition) ? parsedPartition : null;
				long? diagPlayer = long.TryParse(context.Request.QueryString["player"], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out long parsedPlayer) ? parsedPlayer : null;
				long? diagEntity = long.TryParse(context.Request.QueryString["entity"], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out long parsedEntity) ? parsedEntity : null;
				int diagLimit = int.TryParse(context.Request.QueryString["limit"], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out int parsedLimit) ? parsedLimit : 0;
				int diagCursor = int.TryParse(context.Request.QueryString["cursor"], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out int parsedCursor) ? parsedCursor : 0;
				string result = Invoke(() => JsonSerializer.Serialize(DiagSnapshot.Capture(m_partitions, m_persistence,
					depth, diagPartition, diagPlayer, diagEntity, diagLimit, diagCursor)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}

			if (context.Request.HttpMethod == "GET" && path.EndsWith("/offline/admin", StringComparison.OrdinalIgnoreCase))
			{
				string raw = context.Request.QueryString["partition"];
				string result;
				if (raw == null) result = Invoke(() => JsonSerializer.Serialize(new { capability = "v1", cache = OfflineRateCache.Status() }));
				else
				{
					ulong partition = ulong.Parse(raw, CultureInfo.InvariantCulture);
					byte[] profiles = null;
					if (context.Request.QueryString["offline"] == "true")
						profiles = m_persistence.ReadOfflineFinal(partition).Profiles ?? Encoding.UTF8.GetBytes("[]");
					result = Invoke(() => JsonSerializer.Serialize(OfflineCompensation.AdminRead(partition, m_partitions, profiles), OfflineCompensation.Json));
				}
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/profiles/cache/clear", StringComparison.OrdinalIgnoreCase))
			{
				string expected = context.Request.QueryString["expectedEpoch"];
				string result = expected == null ? OfflineRateCache.Clear(context.Request.QueryString["key"])
					: OfflineRateCache.Clear(context.Request.QueryString["key"], int.Parse(expected, CultureInfo.InvariantCulture),
						Guid.Parse(context.Request.QueryString["operation"] ?? throw new ArgumentException("Missing operation id")));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/profiles", StringComparison.OrdinalIgnoreCase))
			{
				ulong partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition"), CultureInfo.InvariantCulture);
				bool online = Invoke(() => m_partitions.Evaluate().Any(p => p.Id == partition));
				string result = online ? Invoke(() => JsonSerializer.Serialize(OfflineCompensation.Profiles(partition, m_partitions), OfflineCompensation.Json))
					: Encoding.UTF8.GetString(m_persistence.ReadOfflineFinal(partition).Profiles ?? Encoding.UTF8.GetBytes("[]"));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && (path.EndsWith("/trigger/compensate-dry-run", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith("/compensate-dry-run", StringComparison.OrdinalIgnoreCase)))
			{
				double hours = double.Parse(context.Request.QueryString["hours"] ?? throw new ArgumentException("Missing hours"), CultureInfo.InvariantCulture);
				if (!double.IsFinite(hours) || hours < 0 || hours > 168) throw new ArgumentException("hours must be between 0 and 168");
				PartitionSaveFile.Parsed source;
				if (context.Request.HasEntityBody)
				{
					// Same bounded admission as other admin file uploads; no path supplied by a caller is opened.
					const int limit = 512 * 1024 * 1024;
					if (context.Request.ContentLength64 > limit) throw new ArgumentException("Save upload exceeds 512 MiB");
					using var stream = new MemoryStream();
					byte[] chunk = new byte[65536]; int read;
					while ((read = context.Request.InputStream.Read(chunk, 0, chunk.Length)) > 0)
					{
						if (stream.Length + read > limit) throw new ArgumentException("Save upload exceeds 512 MiB");
						stream.Write(chunk, 0, read);
					}
					source = PartitionSaveFile.Read(stream.ToArray());
				}
				else source = m_persistence.ReadOfflineFinal(ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition"), CultureInfo.InvariantCulture), context.Request.QueryString["save"]);
				string result = Invoke(() => JsonSerializer.Serialize(OfflineCompensation.DryRun(source, hours), OfflineCompensation.Json));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			// Orchestration.md 11.2: run a scheduled maintenance task now. POST because it mutates; the
			// admin-token gate above already covers it.
			if (context.Request.HttpMethod == "POST" && path.Contains("/trigger/", StringComparison.OrdinalIgnoreCase))
			{
				string task = path.Substring(path.LastIndexOf('/') + 1);
				ulong? triggerPartition = ulong.TryParse(context.Request.QueryString["partition"], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out ulong triggerParsed) ? triggerParsed : null;
				string expectedGeneration = context.Request.QueryString["generation"];
				string result = Invoke(() =>
				{
					if (task == "profile" && expectedGeneration != null && (!triggerPartition.HasValue
						|| m_persistence.OwnedGeneration(triggerPartition.Value) != uint.Parse(expectedGeneration, CultureInfo.InvariantCulture)))
						throw new InvalidOperationException("Profile ownership generation changed");
					return JsonSerializer.Serialize(MaintenanceTriggers.Run(task, m_partitions, m_persistence, triggerPartition));
				});
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}

			if (context.Request.HttpMethod == "GET" && path.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(m_persistence.QuarantineStatus()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/quarantine-repair", StringComparison.OrdinalIgnoreCase))
			{
				ulong repairPartition = ulong.Parse(context.Request.QueryString["partition"]
					?? throw new ArgumentException("Missing partition query parameter"), System.Globalization.CultureInfo.InvariantCulture);
				string repairPath = context.Request.QueryString["path"];
				byte[] repairBytes = null;
				if (repairPath == null)
				{
					// Cross-host repair: the replacement save arrives as the request body instead of a
					// node-local path.
					using MemoryStream repairStream = new MemoryStream();
					context.Request.InputStream.CopyTo(repairStream);
					repairBytes = repairStream.ToArray();
					if (repairBytes.Length == 0) throw new ArgumentException("Missing path query parameter or uploaded save body");
				}
				string result = Invoke(() => JsonSerializer.Serialize(repairPath != null
					? m_persistence.RepairQuarantined(repairPartition, repairPath)
					: m_persistence.RepairQuarantinedFromUpload(repairPartition, repairBytes)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/restore-partition", StringComparison.OrdinalIgnoreCase))
			{
				// Operator restore of an OWNED partition from an uploaded save file, in place. Two calls:
				// phase=close first (the current incarnations are live entities; closing is deferred a
				// frame, so the import cannot share their invoke), then a second call with the save as
				// the body once the close has drained.
				ulong restorePartition = ulong.Parse(context.Request.QueryString["partition"]
					?? throw new ArgumentException("Missing partition query parameter"), System.Globalization.CultureInfo.InvariantCulture);
				if (string.Equals(context.Request.QueryString["phase"], "close", StringComparison.OrdinalIgnoreCase))
				{
					string closed = Invoke(() => JsonSerializer.Serialize(m_persistence.RestoreCloseMembers(restorePartition)));
					Write(context, 200, "application/json", Encoding.UTF8.GetBytes(closed));
					return;
				}
				using MemoryStream restoreStream = new MemoryStream();
				context.Request.InputStream.CopyTo(restoreStream);
				byte[] restoreBytes = restoreStream.ToArray();
				if (restoreBytes.Length == 0) throw new ArgumentException("Missing uploaded save body");
				// collisions=strip imports without the sub-entities whose ids are already live. Absent, a
				// collision refuses the restore rather than letting the engine drop them silently.
				bool stripCollisions = string.Equals(context.Request.QueryString["collisions"], "strip", StringComparison.OrdinalIgnoreCase);
				string result = Invoke(() => JsonSerializer.Serialize(m_persistence.RestoreOwnedPartition(restorePartition, restoreBytes, stripCollisions)));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/offline", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(new { owner = m_persistence.OfflineStatus(), cluster = OfflinePartitionService.Status(), callUps = RemotePullService.Status() }, OfflineCompensation.Json));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/concealment", StringComparison.OrdinalIgnoreCase))
			{
				string result = Invoke(() => JsonSerializer.Serialize(m_persistence.ConcealmentStatus()));
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/teleport", StringComparison.OrdinalIgnoreCase))
			{
				// WA-6 teleport ownership arbitration: the server-side move is deliberately just a
				// position change - ownership follows through the cluster's own topology machinery.
				// The next partition evaluation splits the teleported character away from its old
				// partition-mates, the gateway's automatic handover moves the resulting client
				// partition to the node owning the destination region, and the topology merge
				// absorbs it there. An unknown client or a missing character is a clear refusal.
				ulong moveClient = ulong.Parse(context.Request.QueryString["client"]
					?? throw new ArgumentException("Missing client query parameter"), System.Globalization.CultureInfo.InvariantCulture);
				double moveX = double.Parse(context.Request.QueryString["x"]
					?? throw new ArgumentException("Missing x query parameter"), System.Globalization.CultureInfo.InvariantCulture);
				double moveY = double.Parse(context.Request.QueryString["y"]
					?? throw new ArgumentException("Missing y query parameter"), System.Globalization.CultureInfo.InvariantCulture);
				double moveZ = double.Parse(context.Request.QueryString["z"]
					?? throw new ArgumentException("Missing z query parameter"), System.Globalization.CultureInfo.InvariantCulture);
				string moveResult = Invoke(() =>
				{
					MyPlayer movePlayer = MySession.Static.Players.GetOnlinePlayers()
						.SingleOrDefault(candidate => candidate.Id.SteamId == moveClient)
						?? throw new KeyNotFoundException("Online player " + moveClient);
					MyCharacter moveCharacter = movePlayer.Character
						?? throw new InvalidOperationException("Player " + moveClient + " has no character to teleport");
					ulong sourcePartition = m_partitions.Evaluate()
						.FirstOrDefault(candidate => candidate.Members.Contains(moveCharacter.EntityId))?.Id ?? 0;
					// T-0060: a seated character moved by position alone leaves the cockpit's m_pilot pointing
					// at it. The partitioner then splits the far-away character into its own partition and
					// hands it to the destination's owner, while the cockpit here keeps saving it as its
					// Pilot - one character id seated in two grids on two nodes (Pilot-029, w29), which the
					// handover validator rejects (T-0051). Leave the seat the way the game does first.
					long unseatedFrom = 0;
					if (moveCharacter.UsingEntity is MyShipController moveSeat)
					{
						unseatedFrom = moveSeat.EntityId;
						if (moveSeat is MyCockpit moveCockpit) moveCockpit.RemovePilot();
						if (moveCharacter.UsingEntity == moveSeat) moveCharacter.UsingEntity = null;
					}
					moveCharacter.PositionComp.SetPosition(new Vector3D(moveX, moveY, moveZ));
					if (moveCharacter.Physics != null)
					{
						moveCharacter.Physics.LinearVelocity = Vector3.Zero;
						moveCharacter.Physics.AngularVelocity = Vector3.Zero;
					}
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] teleport arbitrated client " + moveClient
						+ " character " + moveCharacter.EntityId + " from partition " + sourcePartition
						+ (unseatedFrom != 0 ? " unseated_from=" + unseatedFrom : "")
						+ " to " + moveX.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
						+ "," + moveY.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
						+ "," + moveZ.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
					return JsonSerializer.Serialize(new
					{
						client = moveClient,
						character = moveCharacter.EntityId,
						sourcePartition,
						unseatedFrom,
						position = new[] { moveX, moveY, moveZ }
					});
				});
				// Partitioning.md §7.4: the character travels to the destination's owner. Register the
				// directed-move intent so the co-location resolving the jump moves the character side
				// instead of pricing the encounter. Best effort: without it the §7.5 chooser still
				// co-locates, just possibly by pulling the destination partition here.
				string intentGateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
				if (!string.IsNullOrEmpty(intentGateway))
					try
					{
						using System.Net.Http.HttpClient intentClient = ClusterHttp.Client(TimeSpan.FromSeconds(5));
						ClusterRuntime.AuthorizeRegistryClient(intentClient);
						intentClient.PostAsync(intentGateway + "/registry/teleport-intents?client=" + moveClient, null)
							.GetAwaiter().GetResult().EnsureSuccessStatusCode();
					}
					catch (Exception intentError)
					{
						MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] teleport intent registration failed client="
							+ moveClient + " error=" + SecretRedactor.Redact(intentError.Message));
					}
				Write(context, 200, "application/json", Encoding.UTF8.GetBytes(moveResult));
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/partition-of", StringComparison.OrdinalIgnoreCase))
			{
				long memberEntity = long.Parse(context.Request.QueryString["entity"]
					?? throw new ArgumentException("Missing entity query parameter"), System.Globalization.CultureInfo.InvariantCulture);
				string membership = Invoke(() =>
				{
					ulong found = PartitionOfEntity(memberEntity);
					return "{\"entity\":" + memberEntity + ",\"partition\":" + found + "}";
				});
				Write(context, 200, "application/json", System.Text.Encoding.UTF8.GetBytes(membership));
				return;
			}

			if (context.Request.HttpMethod == "GET" && path.EndsWith("/block-integrity", StringComparison.OrdinalIgnoreCase))
			{
				long gridId = long.Parse(context.Request.QueryString["grid"] ?? throw new ArgumentException("Missing grid query parameter"));
				int x = int.Parse(context.Request.QueryString["x"] ?? "0"), y = int.Parse(context.Request.QueryString["y"] ?? "0"), z = int.Parse(context.Request.QueryString["z"] ?? "0");
				string result = Invoke(() => BlockIntegrity(gridId, new VRageMath.Vector3I(x, y, z)));
				Write(context, 200, "application/json", System.Text.Encoding.UTF8.GetBytes(result));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/prepare-import", StringComparison.OrdinalIgnoreCase))
			{
				// A dormant spare's first inbound transfer: full frame rate before the phase runs.
				ClusterRuntime.Wake();
				using StreamReader reader = new StreamReader(context.Request.InputStream);
				string state = reader.ReadToEnd();
				Guid? transferId = OptionalTransferId(context);
				ulong? partition = OptionalPartition(context, transferId);
				uint? generation = OptionalGeneration(context, transferId);
				bool clientless = bool.TryParse(context.Request.QueryString["clientless"], out bool parsedClientless) && parsedClientless;
				ulong[] expected = ParseClients(context.Request.QueryString["clients"]);
				InvokePhase("prepare_import", transferId, partition ?? 0, () => { PrepareImport(state, transferId, partition, generation, clientless, expected); return 0; });
				WriteText(context, 200, "ready\n");
				return;
			}

			if (context.Request.HttpMethod == "GET" && path.EndsWith("/export", StringComparison.OrdinalIgnoreCase))
			{
				byte[] body = Invoke(Export);
				Write(context, 200, "application/xml", body);
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.EndsWith("/import", StringComparison.OrdinalIgnoreCase))
			{
				byte[] body;
				using (MemoryStream ms = new MemoryStream())
				{
					context.Request.InputStream.CopyTo(ms);
					body = ms.ToArray();
				}
				ulong partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition query parameter"));
				Guid? transferId = OptionalTransferId(context);
				uint? generation = OptionalGeneration(context, transferId);
				int count = Import(body, partition, transferId, generation);
				WriteText(context, 200, "imported " + count + " entities\n");
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.EndsWith("/adopt", StringComparison.OrdinalIgnoreCase))
			{
				ulong client = ulong.Parse(context.Request.QueryString["client"] ?? throw new ArgumentException("Missing client query parameter"));
				string state = context.Request.QueryString["state"] ?? string.Empty;
				Guid? transferId = OptionalTransferId(context);
				ulong? partition = OptionalPartition(context, transferId);
				uint? generation = OptionalGeneration(context, transferId);
				int count = InvokePhase("adopt", transferId, partition ?? 0, () => AdoptClient(client, state, transferId, partition, generation));
				WriteText(context, 200, "adopted " + count + " replicables\n");
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.EndsWith("/adopt-ready", StringComparison.OrdinalIgnoreCase))
			{
				ulong client = ulong.Parse(context.Request.QueryString["client"] ?? throw new ArgumentException("Missing client query parameter"));
				string state = context.Request.QueryString["state"] ?? string.Empty;
				Invoke(() => { PrepareClientAdoption(client, state); return 0; });
				WriteText(context, 200, "ready\n");
				return;
			}

			if (context.Request.HttpMethod == "GET" && path.EndsWith("/client-state", StringComparison.OrdinalIgnoreCase))
			{
				ulong client = ulong.Parse(context.Request.QueryString["client"] ?? throw new ArgumentException("Missing client query parameter"));
				ulong partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition query parameter"));
				string state = Invoke(() => ExportClientState(client, m_partitions.Get(partition).Members.ToHashSet()));
				WriteText(context, 200, state + "\n");
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.EndsWith("/remove", StringComparison.OrdinalIgnoreCase))
			{
				ulong partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition query parameter"));
				int count = Invoke(() => RemoveCapturedEntities(partition));
				WriteText(context, 200, "removed " + count + " entities\n");
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.EndsWith("/release", StringComparison.OrdinalIgnoreCase))
			{
				Guid transferId = RequiredTransferId(context);
				ulong partition = RequiredPartition(context);
				uint generation = RequiredGeneration(context);
				int count = Release(transferId, partition, generation);
				WriteText(context, 200, "released " + count + " entities\n");
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.EndsWith("/thaw", StringComparison.OrdinalIgnoreCase))
			{
				Guid transferId = RequiredTransferId(context);
				ulong partition = RequiredPartition(context);
				uint generation = RequiredGeneration(context);
				int count = InvokePhase("thaw", transferId, partition, () => ThawSource(transferId, partition, generation));
				WriteText(context, 200, "thawed " + count + " entities\n");
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.EndsWith("/discard", StringComparison.OrdinalIgnoreCase))
			{
				Guid transferId = RequiredTransferId(context);
				ulong partition = RequiredPartition(context);
				uint generation = RequiredGeneration(context);
				int count;
				try
				{
					count = InvokePhase("discard", transferId, partition, () => Discard(transferId, partition, generation));
				}
				catch (Exception error) when (IsNotFound(error))
				{
					// T-0029: discarding a transfer that does not exist is a NO-OP, not an error.
					// Discard is the cleanup path for a failed prepare-import, and when the prepare
					// is what failed there is no transfer to clean up - so the caller did exactly
					// the right thing and got a 500 for it. On 2026-09-03 that produced 103
					// prepare-import failures paired with 103 discard failures, exactly one each,
					// which doubles the error volume and buries the real cause: the reader has to
					// know that half the 500s are cleanup noise before the log means anything.
					//
					// Idempotency argument, since swallowing a "not found" is otherwise a smell:
					// the requested END STATE is "this transfer is not present and its entities are
					// gone". If the transfer was never created, that state already holds. Note the
					// same reasoning is already applied one level down - Discard() returns the
					// previous count for an ALREADY-discarded transfer rather than throwing. This
					// only extends it to the never-created case.
					//
					// Deliberately narrow: only IsNotFound. A mismatched partition or generation
					// still throws InvalidOperationException from Validate(), and a committed
					// transfer still refuses to be discarded. Logged rather than silent, so a
					// discard for a transfer that SHOULD have existed remains visible.
					Console.WriteLine("[ClusterPlugin] discard no-op: unknown transfer " + transferId
						+ " partition=" + partition + " generation=" + generation
						+ " (prepare-import never created it)");
					WriteText(context, 200, "discarded 0 entities (unknown transfer)\n");
					return;
				}
				WriteText(context, 200, "discarded " + count + " entities\n");
				return;
			}

			if (context.Request.HttpMethod == "POST" && path.EndsWith("/commit", StringComparison.OrdinalIgnoreCase))
			{
				Guid transferId = RequiredTransferId(context);
				ulong partition = RequiredPartition(context);
				uint generation = RequiredGeneration(context);
				uint committedGeneration = uint.Parse(context.Request.QueryString["committedGeneration"] ?? throw new ArgumentException("Missing committedGeneration query parameter"));
				InvokePhase("commit", transferId, partition, () => { Commit(transferId, partition, generation, committedGeneration); return 0; });
				WriteText(context, 200, "committed\n");
				return;
			}
			if (context.Request.HttpMethod == "GET" && path.EndsWith("/save-status", StringComparison.OrdinalIgnoreCase))
			{
				string status = Invoke(m_persistence.SaveStatusJson);
				Write(context, 200, "application/json", System.Text.Encoding.UTF8.GetBytes(status));
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/save-capture", StringComparison.OrdinalIgnoreCase))
			{
				ulong partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition query parameter"));
				bool queued = Invoke(() => m_persistence.QueueTestCapture(partition));
				WriteText(context, queued ? 200 : 409, queued ? "queued\n" : "busy\n");
				return;
			}
			if (context.Request.HttpMethod == "POST" && path.EndsWith("/global-save-capture", StringComparison.OrdinalIgnoreCase))
			{
				bool queued = Invoke(() => m_worldAuthorityPersistence?.QueueTestCapture() == true);
				WriteText(context, queued ? 200 : 409, queued ? "queued\n" : "busy\n");
				return;
			}

			if (context.Request.HttpMethod == "GET" && path.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
			{
				Guid transferId = RequiredTransferId(context);
				ulong partition = RequiredPartition(context);
				uint generation = RequiredGeneration(context);
				string status = Invoke(() => Status(transferId, partition, generation));
				Write(context, 200, "application/json", System.Text.Encoding.UTF8.GetBytes(status));
				return;
			}

			WriteText(context, 404, "unknown cluster control route\n");
		}
		catch (Exception e) when (e is RegistryApplyPendingException || e.InnerException is RegistryApplyPendingException)
		{
			// T-0354b: an expected, retryable refusal - one line, no stack; 409 tells the gateway to retry it.
			string message = (e as RegistryApplyPendingException ?? e.InnerException).Message;
			Log("control request refused path=" + context.Request.Url?.AbsolutePath + ": " + message);
			try { WriteText(context, 409, message + "\n"); }
			catch (Exception) { }
		}
		catch (Exception e)
		{
			Log("control request failed path=" + context.Request.Url?.AbsolutePath + " error=" + e);
			// A peer that vanishes mid-body (HttpListenerException "Unable to write
			// data to the transport connection", seen on /cluster/save-file) leaves the
			// response already submitted, so answering it here throws
			// InvalidOperationException - which escapes to the accept loop and is logged
			// as "Cluster control failed" with a full stack, burying the real cause on
			// the line above. There is also nobody left to read the answer. Every one of
			// w16's "after the response has been submitted" errors was this, 1:1 with a
			// dropped save-file transfer.
			if (e is HttpListenerException) return;
			try
			{
				WriteText(context, e is UnauthorizedAccessException ? 401 : e is PresyncMissingException ? 424 : IsNotFound(e) ? 404 : 500, e + "\n");
			}
			catch (Exception secondary)
			{
				// The body was already on the wire for some other reason; the request is
				// lost either way, and one line beats a stack trace.
				Log("control request could not be answered path=" + context.Request.Url?.AbsolutePath
					+ " secondary=" + secondary.Message);
			}
		}
	}

	private static string BlockIntegrity(long gridId, VRageMath.Vector3I position)
	{
		if (!MyEntities.TryGetEntityById(gridId, out MyEntity entity) || entity is not MyCubeGrid grid) return "{\"exists\":false}";
		var block = grid.GetCubeBlock(position);
		return block == null ? "{\"exists\":false}" : string.Format(System.Globalization.CultureInfo.InvariantCulture,
			"{{\"exists\":true,\"integrity\":{0:R},\"maxIntegrity\":{1:R}}}", block.Integrity, block.MaxIntegrity);
	}

	private static string VoxelStoragesJson() => JsonSerializer.Serialize(MySession.Static.VoxelMaps.Instances.OfType<MyPlanet>()
		.Select(planet => new { storage = planet.StorageName, center = new[] { planet.PositionComp.GetPosition().X, planet.PositionComp.GetPosition().Y, planet.PositionComp.GetPosition().Z }, size = planet.Storage == null ? null
			: new[] { planet.Storage.Size.X, planet.Storage.Size.Y, planet.Storage.Size.Z } }).ToArray());

	private static string VoxelTestPlacePlayer(ulong client, string storage, Vector3D direction, bool align)
	{
		if (!direction.IsValid() || direction.LengthSquared() == 0) throw new ArgumentOutOfRangeException(nameof(direction));
		direction.Normalize();
		MyPlanet planet = MySession.Static.VoxelMaps.TryGetVoxelMapByName(storage) as MyPlanet ?? throw new KeyNotFoundException(storage);
		MyPlayer player = MySession.Static.Players.GetOnlinePlayers().SingleOrDefault(value => value.Id.SteamId == client)
			?? throw new KeyNotFoundException("Online player " + client);
		MyCharacter character = player.Character ?? throw new InvalidOperationException("Player has no character");
		if (character.CurrentWeapon is not MyHandDrill) throw new InvalidOperationException("Player has no hand drill equipped");
		Vector3D surface = planet.GetClosestSurfacePointGlobal(planet.PositionComp.GetPosition() + direction * planet.MaximumRadius);
		Vector3D position = align
			? surface - (character.WeaponPosition.LogicalPositionWorld - character.PositionComp.GetPosition()) - character.WeaponPosition.LogicalOrientationWorld * 0.5
			: surface + direction * 0.25;
		character.PositionComp.SetPosition(position);
		if (character.Physics != null)
		{
			character.Physics.LinearVelocity = Vector3.Zero;
			character.Physics.AngularVelocity = Vector3.Zero;
		}
		return JsonSerializer.Serialize(new { client, character = character.EntityId, storage,
			position = new[] { position.X, position.Y, position.Z } });
	}

	private static string VoxelTestPlayerState(ulong client)
	{
		MyPlayer player = MySession.Static.Players.GetOnlinePlayers().SingleOrDefault(value => value.Id.SteamId == client)
			?? throw new KeyNotFoundException("Online player " + client);
		MyCharacter character = player.Character ?? throw new InvalidOperationException("Player has no character");
		var position = character.PositionComp.GetPosition();
		var forward = character.WorldMatrix.Forward;
		var headForward = character.GetHeadMatrix(false).Forward;
		var logicalPosition = character.WeaponPosition.LogicalPositionWorld;
		var logicalForward = character.WeaponPosition.LogicalOrientationWorld;
		var drillHits = new List<MyPhysics.HitInfo>();
		MyPhysics.CastRay(logicalPosition, logicalPosition + logicalForward * 2.7, drillHits, 24);
		var weapon = character.CurrentWeapon;
		object weaponState = weapon is MyHandDrill drill
			? new { type = weapon.GetType().Name, weapon.IsShooting, drill.IsDrillingAnObject,
				drilledEntity = drill.DrilledEntity?.GetType().Name,
				position = new[] { drill.WorldMatrix.Translation.X, drill.WorldMatrix.Translation.Y, drill.WorldMatrix.Translation.Z },
				forward = new[] { drill.WorldMatrix.Forward.X, drill.WorldMatrix.Forward.Y, drill.WorldMatrix.Forward.Z }, drill.MaximumShotLength }
			: weapon == null ? null : new { type = weapon.GetType().Name, weapon.IsShooting };
		return JsonSerializer.Serialize(new { client, position = new[] { position.X, position.Y, position.Z },
			forward = new[] { forward.X, forward.Y, forward.Z }, headForward = new[] { headForward.X, headForward.Y, headForward.Z },
			logicalPosition = new[] { logicalPosition.X, logicalPosition.Y, logicalPosition.Z },
			logicalForward = new[] { logicalForward.X, logicalForward.Y, logicalForward.Z },
			drillHitCount = drillHits.Count,
			drillHitTypes = drillHits.Select(hit => ((IHitInfo)hit).HitEntity?.GetTopMostParent()?.GetType().Name).ToArray(),
			drillHitPositions = drillHits.Select(hit => new[] { hit.Position.X, hit.Position.Y, hit.Position.Z }).ToArray(),
			shooting = character.IsShooting(MyShootActionEnum.PrimaryAction), weapon = weaponState });
	}

	private static Vector3I QueryVector(HttpListenerContext context) => new Vector3I(
		int.Parse(context.Request.QueryString["x"] ?? throw new ArgumentException("Missing x query parameter"), CultureInfo.InvariantCulture),
		int.Parse(context.Request.QueryString["y"] ?? throw new ArgumentException("Missing y query parameter"), CultureInfo.InvariantCulture),
		int.Parse(context.Request.QueryString["z"] ?? throw new ArgumentException("Missing z query parameter"), CultureInfo.InvariantCulture));

	private static string VoxelTestWrite(string storage, Vector3I min, int size, byte content, byte material)
	{
		if (size < 1 || size > 64) throw new ArgumentOutOfRangeException(nameof(size));
		MyVoxelBase voxel = MySession.Static.VoxelMaps.TryGetVoxelMapByName(storage) ?? throw new KeyNotFoundException(storage);
		Vector3I max = min + size - 1;
		MyStorageData data = new(max - min + 1, Enumerable.Repeat(content, size * size * size).Select(value => (byte)value).ToArray(),
			Enumerable.Repeat(material, size * size * size).Select(value => (byte)value).ToArray());
		voxel.Storage.WriteRange(data, MyStorageDataTypeFlags.ContentAndMaterial, min, max);
		return VoxelRangeHash(storage, min, size);
	}

	/// <summary>
	/// T-0363: a fueled power producer can sit above its maximum while it runs (MyGasFueledPowerProducer.UpdateCapacity
	/// adds the delivered gas without a clamp) and writes that raw value into its builder, but Init clamps to
	/// GetMaxCapacity. The import then reads 100000 where the capture said 100000.27, the round trip fails, and since a
	/// frozen source never changes every retry fails the same way (r126 21:20:59, 4 hydrogen engines, partition 301765).
	/// The capture writes what Init will restore - what a vanilla save and load does to the same block.
	/// </summary>
	internal static void ClampFuelCapacity(MyEntity entity, MyObjectBuilder_EntityBase builder)
	{
		if (entity is not MyCubeGrid grid || builder is not MyObjectBuilder_CubeGrid gridBuilder || gridBuilder.CubeBlocks == null) return;
		Dictionary<long, float> maximum = null;
		foreach (MyCubeBlock block in grid.GetFatBlocks())
			if (block is MyFueledPowerProducer producer) (maximum ??= new())[producer.EntityId] = producer.GetMaxCapacity();
		if (maximum == null) return;
		foreach (MyObjectBuilder_CubeBlock block in gridBuilder.CubeBlocks)
			if (block is MyObjectBuilder_FueledPowerProducer fueled && maximum.TryGetValue(fueled.EntityId, out float max))
				fueled.Capacity = ImportRoundTrip.ClampCapacity(fueled.Capacity, max);
	}

	/// <summary>
	/// SE-1155: a block inventory can hold a stack larger than the inventory loads back. MyInventory.InitItems clamps
	/// each saved stack to what fits an empty inventory, in float arithmetic (ImportRoundTrip.LoadedAmount), while a
	/// refill that sizes its stack as (int)((float)free / volume), as a station refuel does, overshoots it: a station
	/// reactor held 461538.469939 kg of uranium where 461538.46674 load, and every handover of its partition failed on
	/// that one Amount (octahedron-test 2026-11-05, partition 4519). The capture writes what the load restores, as
	/// <see cref="ClampFuelCapacity"/> does. Only nearly full inventories are looked at: no other stack can be over.
	/// </summary>
	internal static void ClampInventoryAmounts(MyEntity entity, MyObjectBuilder_EntityBase builder)
	{
		if (entity is not MyCubeGrid grid || builder is not MyObjectBuilder_CubeGrid gridBuilder || gridBuilder.CubeBlocks == null) return;
		foreach (MyCubeBlock block in grid.GetFatBlocks())
			for (int index = 0; index < block.InventoryCount; index++)
			{
				if (block.GetInventory(index) is not MyInventory inventory || !inventory.IsConstrained
					|| (double)inventory.CurrentVolume < 0.999 * (double)inventory.MaxVolume && (double)inventory.CurrentMass < 0.999 * (double)inventory.MaxMass) continue;
				foreach (MyPhysicalInventoryItem item in inventory.GetItems())
				{
					Sandbox.Game.Entities.Inventory.MyInventoryItemAdapter adapter = Sandbox.Game.Entities.Inventory.MyInventoryItemAdapter.Static;
					if (item.Content == null || !adapter.TryAdapt(item.Content.GetObjectId())) continue;
					MyFixedPoint loaded = ImportRoundTrip.LoadedAmount(item.Amount, inventory.MaxVolume, inventory.MaxMass, adapter.Volume, adapter.Mass, adapter.HasIntegralAmounts);
					if (loaded >= item.Amount) continue;
					MyObjectBuilder_CubeBlock saved = gridBuilder.CubeBlocks.FirstOrDefault(candidate => candidate.EntityId == block.EntityId);
					foreach (MyObjectBuilder_InventoryItem stack in SavedInventories(saved?.ComponentContainer)
						.SelectMany(savedInventory => savedInventory.Items ?? new List<MyObjectBuilder_InventoryItem>())
						.Where(stack => stack.ItemId == item.ItemId && stack.Amount == item.Amount && stack.PhysicalContent?.GetObjectId() == item.Content.GetObjectId()))
						stack.Amount = loaded;
				}
			}
	}

	private static IEnumerable<MyObjectBuilder_Inventory> SavedInventories(MyObjectBuilder_ComponentContainer container)
	{
		static IEnumerable<MyObjectBuilder_Inventory> Of(MyObjectBuilder_ComponentBase component) => component switch
		{
			MyObjectBuilder_Inventory inventory => new[] { inventory },
			MyObjectBuilder_InventoryAggregate aggregate => (aggregate.Inventories ?? new List<MyObjectBuilder_InventoryBase>()).SelectMany(Of),
			_ => Enumerable.Empty<MyObjectBuilder_Inventory>(),
		};
		return (container?.Components ?? new List<MyObjectBuilder_ComponentContainer.ComponentData>()).SelectMany(data => Of(data.Component));
	}

	private static string VoxelTestCutout(string storage, Vector3I center, float radius)
	{
		if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
		if (MySession.Static.Players.GetOnlinePlayerCount() != 0)
			throw new InvalidOperationException("The cutout relay probe requires a client-free node");
		MyVoxelBase voxel = MySession.Static.VoxelMaps.TryGetVoxelMapByName(storage) ?? throw new KeyNotFoundException(storage);
		Vector3D worldCenter = voxel.PositionLeftBottomCorner + center;
		voxel.RootVoxel.RequestVoxelCutoutSphere(worldCenter, radius, createDebris: false, damage: false);
		return JsonSerializer.Serialize(new { storage, center = new[] { worldCenter.X, worldCenter.Y, worldCenter.Z }, radius, requested = true });
	}

	private static string VoxelRangeHash(string storage, Vector3I min, int size)
	{
		if (size < 1 || size > 64) throw new ArgumentOutOfRangeException(nameof(size));
		MyVoxelBase voxel = MySession.Static.VoxelMaps.TryGetVoxelMapByName(storage) ?? throw new KeyNotFoundException(storage);
		Vector3I max = min + size - 1;
		MyStorageData data = new(MyStorageDataTypeFlags.ContentAndMaterial);
		data.Resize(min, max);
		voxel.Storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, 0, min, max);
		int count = data.Size3D.Size;
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		hash.AppendData(data[MyStorageDataTypeEnum.Content], 0, count);
		hash.AppendData(data[MyStorageDataTypeEnum.Material], 0, count);
		return JsonSerializer.Serialize(new { storage, min = new[] { min.X, min.Y, min.Z }, size, hash = Convert.ToHexString(hash.GetHashAndReset()) });
	}

	private static byte[] Export()
	{
		if (MySession.Static == null)
		{
			throw new InvalidOperationException("No active session");
		}

		MyObjectBuilder_Sector sector = MySession.Static.GetSector(includeEntities: true);
		using MemoryStream ms = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(ms, sector))
		{
			throw new InvalidOperationException("Failed to serialize sector");
		}
		return ms.ToArray();
	}

	/// <param name="clients">
	/// Every client riding this partition, captured in ONE freeze. A multi-client handover exists so
	/// that all of them leave the source at the same instant: their replication views are exported
	/// from a single frozen state, never one freeze per client. Empty means a clientless capture.
	/// </param>
	private static byte[] Capture(ulong[] clients, ulong partitionId, Guid? transferId, uint? generation, bool allowRemoteUse = false)
	{
		if (transferId.HasValue && m_sourceTransfers.TryGetValue(transferId.Value, out Transfer existing))
		{
			Validate(existing, partitionId, generation.Value);
			if (!SameClients(existing.Clients, clients))
			{
				throw new InvalidOperationException("Transfer clients do not match capture clients");
			}
			// T-0147: a finished transfer's payload is dropped after a grace period; a capture retried
			// that late is a protocol error, not something to answer with an empty body.
			return existing.Capture ?? throw new InvalidOperationException("Transfer " + existing.Id + " finished and its capture was dropped");
		}
		// T-0350: a capture ends in a handover tee the registry must catalogue; with the registry unreachable it would
		// freeze the partition and then wait on the game thread. Refused before anything is frozen - the gateway aborts.
		if (transferId.HasValue && m_persistence.RegistryHeld)
			throw new InvalidOperationException("Registry unreachable: capture refused");
		// T-0354: a topology change over this partition is applied in memory and not yet answered by the registry.
		// Its generation and member set are not the registry's until then; the gateway retries the aborted transfer.
		// T-0354b: answered 409 (RegistryApplyPendingException), not 500: nothing is frozen yet and it resolves by itself.
		// A layout made a frame ago around a spawned or pasted grid waits only for its id claim; on df that claim was
		// answered inside the pass, and a handover asked right after the spawn (P3-HND-02) is retried by the gateway.
		if (transferId.HasValue && m_persistence.RegistryPending(partitionId))
			throw new RegistryApplyPendingException(m_persistence.RegistryClaimOnly(partitionId)
				? "Partition " + partitionId + " is not claimed yet (its id claim is unanswered): capture refused, retry"
				: "Partition " + partitionId + " has an unanswered registry apply: capture refused");
		PartitionManager.Partition partition = m_partitions.Get(partitionId);
		// ClientPartitions lists every ONLINE player controlling an entity here, and a disconnected
		// player's body re-binds as an online player on every warm boot - which made any partition
		// holding an abandoned character permanently uncapturable clientlessly. Only a player with a
		// live connection (Sync.Clients) has session state to lose; a resident body is cargo.
		if (clients.Length == 0 && m_partitions.ClientPartitions(new[] { partition })
			.Any(route => route.Partition == partitionId && Sync.Clients?.HasClient(route.Client) == true))
		{
			throw new InvalidOperationException("Clientless capture requires a partition with no connected clients");
		}
		uint actualGeneration = m_persistence.GetGeneration(partitionId);
		if (generation.HasValue && generation.Value != actualGeneration)
		{
			throw new InvalidOperationException("Transfer generation does not match local partition generation");
		}
		if (partition.HasPending)
		{
			// Members adopted from a save are still in the parallel entity-init pipeline; the
			// serializer below would silently omit them and the handover would move an incomplete
			// partition. The abort is retried by the gateway, and the pin drains within
			// PartitionMembership.PinSeconds at the worst.
			throw new InvalidOperationException("Partition " + partitionId
				+ " has members still initializing; capture refused until they materialize");
		}
		HashSet<long> members = partition.Members.ToHashSet();
		MyEntity[] entities = MyEntities.GetEntities().Where(entity => members.Contains(entity.EntityId)).ToArray();
		Transfer transfer = transferId.HasValue ? new Transfer(transferId.Value, partitionId, actualGeneration) { Clients = clients, Members = members } : null;
		HandoverPhaseTimer timer = HandoverPhaseTimer.Current;
		try
		{
			timer?.Step("freeze");
			if (transfer != null)
			{
				// Same game-thread operation as Freeze: a terminal/camera opened after the gateway's
				// report/presync must still refuse a clientless pull. Only deliberate co-location wins.
				if (clients.Length == 0 && !allowRemoteUse && RemoteUse.Capture(new[] { partition }).ContainsKey(partitionId))
					throw new InvalidOperationException("remote-use");
				Freeze(transfer, entities);
				// Attached children (a seated pilot above all) serialize INSIDE their parent's
				// object builder. Vanilla re-tops the pilot character when the switching client
				// detaches mid-transfer, so the release must know these ids to close the copy.
				HashSet<VRage.ModAPI.IMyEntity> children = new HashSet<VRage.ModAPI.IMyEntity>();
				foreach (MyEntity entity in entities) entity.Hierarchy?.GetChildrenRecursive(children);
				transfer.Descendants = children.Select(child => child.EntityId).ToHashSet();
			}
			// Export every view before pruning any of them: a prune empties one client's view on this
			// node, and doing it between two exports would let the second client's export observe a
			// half-dismantled node state.
			timer?.Step("client_export");
			Dictionary<ulong, string> encodedClientStates = new Dictionary<ulong, string>();
			foreach (ulong each in clients) encodedClientStates[each] = ExportClientState(each, members);
			byte[][] clientState = clients.Select(each => System.Text.Encoding.UTF8.GetBytes(encodedClientStates[each])).ToArray();
			// T-0256: plugins' per-grid states ride the network-state string to the target (after a newline).
			string pluginStates = null;
			if (transfer != null)
			{
				timer?.Step("plugin_leaving");
				transfer.PluginGrids = PluginHandoverService.Grids(entities);
				// T-0292: the fast members travel with their state (PluginHandoverService.FastKey).
				Dictionary<long, double> fast = members.Where(m_partitions.Fast.IsFast).ToDictionary(id => id, m_partitions.Fast.SpeedOf);
				pluginStates = PluginHandoverService.Leaving(transfer.PluginGrids, fast);
			}
			byte[] networkState = System.Text.Encoding.UTF8.GetBytes(PluginHandoverService.Append(ExportNetworkIds(members), pluginStates));
			timer?.Step("ob_build");
			MyObjectBuilder_EntityBase[] builders = entities.Select(entity =>
			{
				entity.BeforeSave();
				MyObjectBuilder_EntityBase builder = entity.GetObjectBuilder();
				ClampFuelCapacity(entity, builder);
				ClampInventoryAmounts(entity, builder);
				if (transfer != null && transfer.Frozen.TryGetValue(entity.EntityId, out FrozenState frozen)) ApplyVelocity(builder, frozen);
				return builder;
			}).ToArray();
			byte[] sector;
			if (transfer == null)
			{
				timer?.Step("serialize");
				sector = PartitionSaveFile.Write(partition.Id, actualGeneration, 1, 1, partition.Sphere, partition.Anchored, builders.Length,
					PartitionSaveFile.SerializeEntities(builders), ClusterRuntime.CatalogClusterMilliseconds());
			}
			else if (ClusterConfig.GetBool("tee.async"))
			{
				// §4 tee.async: serialise here, hand the bytes to the write worker and return without
				// waiting for the previous save or this write. Durability becomes a post-condition the
				// gateway checks through /tee-status before it activates the transfer.
				timer?.Step("serialize");
				(sector, Task<bool> tee) = m_persistence.QueueHandoverTeeAsync(partition, builders);
				m_tees[transfer.Id] = tee;
			}
			else
			{
				timer?.Step("tee_wait");
				sector = m_persistence.QueueHandoverTee(partition, builders);
			}
			timer?.Step("frame");
			// T-0147: every length is known here, so the frame is written straight into its final
			// array. A growing MemoryStream doubled its buffer up through the large-object heap
			// (log2(15 MB / 85 KB) ~ 8 dead LOH arrays per capture) and ToArray() copied it once
			// more; on the lattice world a capture is ~15 MB and the LOH was 72-74 % non-live.
			long frameLength = 4 + 4 + 4 + clients.Length * (8L + 4) + 4 + 4
				+ clientState.Sum(state => (long)state.Length) + networkState.Length + sector.Length;
			byte[] capture = new byte[frameLength];
			using MemoryStream stream = new MemoryStream(capture, 0, capture.Length, writable: true, publiclyVisible: true);
			using BinaryWriter writer = new BinaryWriter(stream);
			// Capture format 2 (multi-client). The leading -1 is a marker, not a length: a format-1
			// reader parses that first word as the client-state length and rejects it as invalid, so
			// a gateway and a plugin that ever ship out of step fail loudly instead of silently
			// mis-slicing the payload.
			writer.Write(-1);
			writer.Write(2);
			writer.Write(clients.Length);
			for (int i = 0; i < clients.Length; i++)
			{
				writer.Write(clients[i]);
				writer.Write(clientState[i].Length);
			}
			writer.Write(networkState.Length);
			writer.Write(sector.Length);
			foreach (byte[] state in clientState) writer.Write(state);
			writer.Write(networkState);
			writer.Write(sector);
			writer.Flush();
			if (stream.Position != capture.Length)
				throw new InvalidOperationException("capture frame length " + stream.Position + " != computed " + capture.Length);
			if (timer != null) { timer.Entities = entities.Length; timer.Bytes = capture.Length; }
			if (transferId.HasValue)
			{
				transfer.Capture = capture;
				transfer.Builders = builders;
				transfer.Sphere = partition.Sphere;
				transfer.Anchored = partition.Anchored;
				transfer.ClientStateBytes = clientState;
				transfer.NetworkStateBytes = networkState;
				foreach (ulong each in clients)
				{
					PruneClientState(each, encodedClientStates[each]);
					transfer.PrunedClients.Add(each);
				}
				m_sourceTransfers.Add(transferId.Value, transfer);
			}
			return capture;
		}
		catch
		{
			if (transfer != null)
			{
				foreach (ulong each in transfer.PrunedClients) ResumeClientState(each);
				RestoreFrozen(transfer);
				PluginHandoverService.Stayed(transfer.PluginGrids); // the plugins were told it was leaving
			}
			throw;
		}
	}

	/// <summary>Parses the comma-separated `clients` query parameter; null or empty means none.</summary>
	private static ulong[] ParseClients(string value)
	{
		if (string.IsNullOrWhiteSpace(value)) return new ulong[0];
		ulong[] clients = value.Split(',').Select(part => ulong.Parse(part.Trim(), CultureInfo.InvariantCulture)).ToArray();
		if (clients.Distinct().Count() != clients.Length) throw new ArgumentException("Duplicate client in clients query parameter");
		return clients;
	}

	/// <summary>Set comparison over the client lists of a transfer, tolerating null and order.</summary>
	private static bool SameClients(ulong[] left, ulong[] right)
	{
		HashSet<ulong> a = new HashSet<ulong>(left ?? new ulong[0]);
		HashSet<ulong> b = new HashSet<ulong>(right ?? new ulong[0]);
		return a.SetEquals(b);
	}

	private static string ExportNetworkIds(IEnumerable<long> entityIds)
	{
		return ReplicationServerPatch.ExportNetworkIds(entityIds.ToHashSet());
	}

	private static void PrepareNetworkIds(string state)
	{
		ReplicationServerPatch.PrepareNetworkIds(state);
	}

	private static void DiscardNetworkIds()
	{
		ReplicationServerPatch.DiscardNetworkIds();
	}

	/// <param name="clients">
	/// The clients the gateway will hand over with this partition. Recorded here so the target can
	/// refuse to commit until every one of them has been adopted, rather than trusting the gateway
	/// to have asked for all of them - a partial adoption is exactly the half-switched state a
	/// multi-client handover must never leave behind.
	/// </param>
	private static void PrepareImport(string state, Guid? transferId, ulong? partitionId, uint? generation, bool clientless, ulong[] clients)
	{
		Guid importId = transferId ?? Guid.Empty;
		// prepare_import was 365 ms at p95 on w22 with no sub-step to say where it went - the second
		// largest game-thread hold in the whole handover and the only phase the timer could not
		// explain. The work is the network-id import, so name it and the wait to acquire it.
		HandoverPhaseTimer prepareTimer = HandoverPhaseTimer.Current;
		if (!transferId.HasValue)
		{
			prepareTimer?.Step("network_import_acquire");
			m_networkImport.Acquire(importId);
			prepareTimer?.Step("prepare_network_ids");
			try { PrepareNetworkIds(PluginHandoverService.Split(state, out _)); }
			catch { DiscardNetworkIds(); m_networkImport.Release(importId); throw; }
			return;
		}
		if (m_targetTransfers.TryGetValue(transferId.Value, out Transfer existing))
		{
			prepareTimer?.Step("prepare_revalidate");
			Validate(existing, partitionId.Value, generation.Value);
			if (existing.NetworkState != state || existing.Clientless != clientless || !SameClients(existing.Clients, clients))
			{
				throw new InvalidOperationException("Transfer import preparation changed");
			}
			if (!existing.Imported) m_networkImport.Require(importId);
			return;
		}
		prepareTimer?.Step("network_import_acquire");
		m_networkImport.Acquire(importId);
		try
		{
			prepareTimer?.Step("prepare_network_ids");
			PrepareNetworkIds(PluginHandoverService.Split(state, out string pluginStates));
			m_targetTransfers.Add(transferId.Value, new Transfer(transferId.Value, partitionId.Value, generation.Value) { NetworkState = state, PluginStates = pluginStates, Prepared = true, Clientless = clientless, Clients = clients });
		}
		catch
		{
			DiscardNetworkIds();
			m_networkImport.Release(importId);
			throw;
		}
	}

	/// <summary>
	/// Handler-thread driver. With import.entitiesPerFrame=0 (default) the whole import is one game-thread
	/// Invoke exactly as before. With N>0 the payload is parsed and deserialised HERE, off the game thread,
	/// and the entities are created N per frame (each slice one Invoke), then validated and adopted in a
	/// final frame - the transaction is unchanged, only the single 1-3 s stall becomes many short frames.
	/// </summary>
	private static int Import(byte[] body, ulong partitionId, Guid? transferId, uint? generation)
	{
		// T-0147: route on the header alone; ImportParse (or the delta path) decompresses the body ONCE.
		PartitionSaveFile.Parsed envelope = PartitionSaveFile.ReadHeader(body);
		if (envelope.Kind == DeltaCodec.Kind) return ImportDelta(body, PartitionSaveFile.Read(body), partitionId, transferId, generation);
		if (ClusterConfig.Get("import.mode") == "staged") return ImportStaged(body, partitionId, transferId, generation);
		int perFrame = ClusterConfig.GetInt("import.entitiesPerFrame");
		if (perFrame <= 0)
			return InvokePhase("import", transferId, partitionId, () =>
			{
				HandoverPhaseTimer timer = HandoverPhaseTimer.Current;
				timer?.Step("import_deserialize");
				ImportContext context = ImportParse(body, partitionId, generation);
				timer?.Step("import_begin");
				if (!ImportBegin(context, transferId)) return context.Transfer.ImportCount;
				ResolveGhostSeats(context);
				try
				{
					timer?.Step("import_create");
					ImportStep(context, int.MaxValue);
					timer?.Step("validate");
					ImportValidate(context);
				}
				catch { ImportFail(context); throw; }
				timer?.Step("import_finish");
				return ImportFinish(context);
			});
		ImportContext chunked = ImportParse(body, partitionId, generation);
		if (!InvokePhase("import_begin", transferId, partitionId, () => ImportBegin(chunked, transferId))) return chunked.Transfer.ImportCount;
		try
		{
			while (chunked.Next < chunked.Entities.Length)
				InvokePhase("import_create", transferId, partitionId, () => { ImportStep(chunked, perFrame); return 0; });
			return InvokePhase("import_finish", transferId, partitionId, () =>
			{
				try { ImportValidate(chunked); }
				catch { ImportFail(chunked); throw; }
				return ImportFinish(chunked);
			});
		}
		catch
		{
			// A slice that threw has already run ImportFail on the game thread; a slice that could not
			// be invoked at all (game-thread timeout) must still undo what earlier slices created.
			if (!chunked.Failed) { try { Invoke(() => { ImportFail(chunked); return 0; }); } catch { } }
			throw;
		}
	}

	/// <summary>Import continuation state across frames (import.entitiesPerFrame).</summary>
	private sealed class ImportContext
	{
		public PartitionSaveFile.Parsed Save;
		public uint ExpectedGeneration;
		public ulong PartitionId;
		public Guid ImportId;
		public MyObjectBuilder_EntityBase[] Entities;
		public Transfer Transfer;
		public HashSet<long> Created;
		public int Next;
		public int Count;
		public bool Failed;
	}

	/// <summary>Thread-agnostic: parses the envelope and deserialises the entities.</summary>
	private static ImportContext ImportParse(byte[] body, ulong partitionId, uint? generation)
	{
		PartitionSaveFile.Parsed save = PartitionSaveFile.Read(body);
		uint expectedGeneration = generation ?? save.Generation;
		PartitionSaveFile.ValidateEnvelope(save, partitionId, expectedGeneration);
		return new ImportContext
		{
			Save = save, ExpectedGeneration = expectedGeneration, PartitionId = partitionId,
			Entities = PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount)
		};
	}

	/// <summary>Game thread. False = already imported (idempotent retry), answer with the recorded count.</summary>
	/// <summary>
	/// Game thread, before staging (T-0051). A character can be SEATED IN TWO COCKPITS at once across the
	/// cluster: the hull a pilot left keeps it as its saved pilot when the leave never ran RemovePilot on
	/// that copy (the half-unseated / restore-re-seats-ghost family), and the pilot's current ship carries
	/// the live one. w29: character 72057594272811162 (Pilot-029) sat in cockpit 72057595363344525 of its
	/// old Visitor at the ground3 platform AND in cockpit 72057595598223651 of its current ship; every
	/// handover of either partition onto a node holding the other copy aborted - MyCockpit.Init finds the
	/// id already "using" another controller, seats nothing, and the round-trip gate reads the dropped
	/// Pilot as a residual (signatures af00f18e98b84b40 and 2ef0ed58c4e081a7, 35 aborts over 10
	/// partitions, and the block/dissolve that followed).
	///
	/// Rules, for each arriving cockpit whose pilot already exists here seated in ANOTHER cockpit:
	///  * a client connected to this node controls the local copy or its seat: the local copy is live,
	///    the arriving one is the ghost - leave it unseated and strip it from the comparison;
	///  * the character registry names the local seat's partition as the character's live partition:
	///    same;
	///  * otherwise the arriving partition is the registry-blessed newest state: unseat the local ghost
	///    (vanilla RemovePilot; it stands beside its old seat, nothing is deleted) so MyCockpit.Init
	///    adopts the character into the arriving cockpit at thaw.
	/// </summary>
	private static void ResolveGhostSeats(ImportContext context)
	{
		if (context.Entities == null) return;
		StripStaleSeatedBodies(context);
		if (context.Transfer == null) return;
		HashSet<long> liveControlled = null;
		foreach (MyObjectBuilder_EntityBase builder in context.Entities)
		{
			if (builder is not MyObjectBuilder_CubeGrid grid || grid.CubeBlocks == null) continue;
			foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
			{
				if (block is not MyObjectBuilder_Cockpit cockpit || cockpit.Pilot == null) continue;
				long pilotId = cockpit.Pilot.EntityId;
				if (!MyEntities.TryGetEntityById(pilotId, out MyEntity existing) || existing is not MyCharacter local) continue;
				if (local.UsingEntity is not MyShipController seat || seat.EntityId == cockpit.EntityId) continue;
				liveControlled ??= LiveControlledEntityIds();
				ulong seatPartition = StandingPartitionOfEntity(seat.CubeGrid?.EntityId ?? seat.EntityId);
				CharacterRegistryService.Instance record = CharacterRegistryService.LiveInstanceOf(pilotId);
				bool liveHere = liveControlled.Contains(pilotId) || liveControlled.Contains(seat.EntityId)
					|| (seat.CubeGrid != null && liveControlled.Contains(seat.CubeGrid.EntityId));
				bool registrySaysLocal = record != null && record.PartitionId != 0 && record.PartitionId == seatPartition
					&& record.PartitionId != context.PartitionId;
				string where = "pilot=" + pilotId + " arrivingCockpit=" + cockpit.EntityId + " arrivingGrid=" + grid.EntityId
					+ " localSeat=" + seat.EntityId + " localGrid=" + (seat.CubeGrid?.EntityId ?? 0) + " localPartition=" + seatPartition
					+ " registryPartition=" + (record?.PartitionId ?? 0) + " partition=" + context.PartitionId + " transferId=" + context.Transfer.Id;
				if (liveHere || registrySaysLocal)
				{
					context.Transfer.DisplacedPilots.Add(pilotId);
					Log("ghost_seat arriving_copy_ignored=true reason=" + (liveHere ? "live-controlled-here" : "registry-names-local-seat") + " " + where);
					continue;
				}
				try
				{
					// A remote control has no seat to leave; only a cockpit holds a pilot.
					if (seat is MyCockpit localCockpit) localCockpit.RemovePilot();
					if (local.UsingEntity == seat) local.UsingEntity = null;
					Log("ghost_seat local_copy_unseated=true " + where);
				}
				catch (Exception e)
				{
					// A ghost that cannot be unseated is still not the arriving cockpit's pilot: keep the
					// handover alive by ignoring the arriving copy rather than aborting on it.
					context.Transfer.DisplacedPilots.Add(pilotId);
					Log("ghost_seat unseat_failed=true error=" + SecretRedactor.Redact(e.Message) + " " + where);
				}
			}
		}
	}

	/// <summary>
	/// A grid save taken while a pilot sat in it carries that body inside the cockpit. When the
	/// same identity has since been given another body - the registry names a live one under a
	/// different entity id and has never seen this one - the seated copy is a stale save artefact,
	/// not a player: it has no client, blocks the seat for the body the client does control, and
	/// the client's session picks between the two inconsistently (control on one, "sitting" read
	/// from the other), which is the half-seated pilot that never re-seats (w29 2026-09-07 pilot-10,
	/// T-0065). Drop it from the builder before the grid is created, on handover imports and on
	/// recovery imports alike - recovery after a restart is exactly where the fork is born.
	/// </summary>
	private static void StripStaleSeatedBodies(ImportContext context)
	{
		foreach (MyObjectBuilder_EntityBase builder in context.Entities)
		{
			if (builder is not MyObjectBuilder_CubeGrid grid || grid.CubeBlocks == null) continue;
			foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
			{
				if (block is not MyObjectBuilder_Cockpit cockpit || cockpit.Pilot == null) continue;
				long pilotId = cockpit.Pilot.EntityId;
				long identityId = cockpit.Pilot.OwningPlayerIdentityId ?? 0;
				if (identityId == 0 || pilotId == 0) continue;
				CharacterRegistryService.Instance live = CharacterRegistryService.LiveBodyOfIdentity(identityId);
				if (live == null || live.CharacterEntityId == pilotId) continue;
				// A body the registry knows is a real one mid-move (the snapshot lags a report or two);
				// only a body it has never seen is a save artefact.
				if (CharacterRegistryService.LiveInstanceOf(pilotId) != null) continue;
				cockpit.Pilot = null;
				// The seat is serialised twice: MyObjectBuilder_Cockpit.Pilot AND the cockpit's hierarchy
				// component, whose Children carry the same character builder. MyHierarchyComponentBase
				// .Deserialize creates every child that does not exist yet, so a Pilot-only strip still
				// re-created the ghost as an unseated child of the cockpit, and the cockpit then emitted
				// PilotJetpackEnabled nil against the source's true - the residual that blocked 29128
				// twice (w29 2026-09-08 05:14, signature 59719bb25e04e1db). Drop both copies.
				int children = RemoveHierarchyChild(cockpit, pilotId);
				// The frozen source still carries the pilot: the round-trip validator must skip this seat
				// or the transfer blocks on it (28397, the Earth base, 2026-09-08 00:38).
				context.Transfer?.DisplacedPilots.Add(pilotId);
				Log("ghost_seat stale_body_stripped=true pilot=" + pilotId + " identity=" + identityId
					+ " liveBody=" + live.CharacterEntityId + " liveNode=" + live.OwningNode + " livePartition=" + live.PartitionId
					+ " cockpit=" + cockpit.EntityId + " grid=" + grid.EntityId + " partition=" + context.PartitionId
					+ " hierarchyChildrenRemoved=" + children
					+ " transferId=" + (context.Transfer?.Id.ToString() ?? "recovery"));
			}
		}
	}

	/// <summary>Removes every hierarchy child of a block builder with this id; returns how many were removed.</summary>
	internal static int RemoveHierarchyChild(MyObjectBuilder_CubeBlock entity, long childId)
	{
		int removed = 0;
		if (entity.ComponentContainer?.Components == null) return removed;
		foreach (MyObjectBuilder_ComponentContainer.ComponentData data in entity.ComponentContainer.Components)
			if (data.Component is VRage.Game.ObjectBuilders.Components.MyObjectBuilder_HierarchyComponentBase hierarchy && hierarchy.Children != null)
				removed += hierarchy.Children.RemoveAll(child => child != null && child.EntityId == childId);
		return removed;
	}

	private static bool ImportBegin(ImportContext context, Guid? transferId)
	{
		context.ImportId = transferId ?? Guid.Empty;
		if (transferId.HasValue)
		{
			Transfer transfer = GetTarget(transferId.Value, context.PartitionId, context.ExpectedGeneration);
			if (transfer.Discarded)
			{
				throw new InvalidOperationException("Transfer was discarded");
			}
			if (!transfer.Prepared)
			{
				throw new InvalidOperationException("Transfer import was not prepared");
			}
			if (transfer.Imported)
			{
				if (transfer.BodyChecksum != context.Save.BodyChecksum)
				{
					throw new InvalidOperationException("Transfer import body changed");
				}
				context.Transfer = transfer;
				return false;
			}
			if (transfer.Import != null && !transfer.Import.Failed)
				throw new InvalidOperationException("Transfer import is already in progress");
			transfer.Import = context;
			context.Transfer = transfer;
		}
		m_networkImport.Require(context.ImportId);
		context.Created = context.Transfer?.CreatedMembers ?? new HashSet<long>();
		return true;
	}

	/// <summary>Game thread: creates up to maxEntities of the remaining entities.</summary>
	private static void ImportStep(ImportContext context, int maxEntities)
	{
		ulong partitionId = context.PartitionId;
		Transfer transfer = context.Transfer;
		HashSet<long> created = context.Created;
		// Recomputed per slice, not taken from ImportBegin's snapshot. With import.entitiesPerFrame=0
		// the two are the same frame and this is exactly today's behaviour; with N>0 the slices span
		// frames, and a client that takes control of an entity between them would be missing from a
		// snapshot taken at Begin - so the branch below would freeze and reseat a LIVE body onto the
		// source's frozen pose. For a seated pilot that is a teleport out from under the cockpit, and
		// it is the mechanism behind w14 session 7's fleet-wide ejection storm (see the comment on
		// the kept-entity branch). The scan is over online players only, so per-slice cost is noise.
		HashSet<long> liveControlled = LiveControlledEntityIds();
		int count = 0;
		bool allocationSuspended = VRage.MyEntityIdentifier.AllocationSuspended;
		VRage.MyEntityIdentifier.AllocationSuspended = true;
		try
		{
			int last = (int)Math.Min((long)context.Next + maxEntities, context.Entities.Length);
			for (; context.Next < last; context.Next++)
			{
				MyObjectBuilder_EntityBase entity = context.Entities[context.Next];

				// True = kept (skip creation); false = a fenced duplicate was purged and the
				// arriving copy is created below.
				if (MyEntities.TryGetEntityById(entity.EntityId, out MyEntity existingEntity)
					&& ImportKeepExisting(context, entity, existingEntity, liveControlled))
					continue;
				MyEntity createdEntity = MyEntities.CreateFromObjectBuilderAndAdd(entity, fadeIn: false);
				created.Add(entity.EntityId);
				MySessionComponentTrashPatch.ProtectArrival(entity.EntityId); // T-0254: no trash sweep mid-import
				if (transfer != null) Freeze(transfer, new[] { createdEntity });
				ReseatImported(createdEntity, entity);
				JetpackResyncService.Remember(createdEntity, entity);
				count++;
			}
			context.Count += count;
			HandoverPhaseTimer timer = HandoverPhaseTimer.Current;
			if (timer != null) timer.Entities += count;
			// A slice that leaves entities in the world across a frame must also leave them in the
			// partition, or the topology sweep mints a fresh partition around them before the next
			// slice (the same duplicate-incarnation mechanism as the 330/333 case, see Adopt).
			if (context.Next < context.Entities.Length)
				m_partitions.Adopt(partitionId, context.Entities.Take(context.Next).Select(x => x.EntityId));
		}
		catch
		{
			context.Count += count;
			ImportFail(context);
			throw;
		}
		finally
		{
			VRage.MyEntityIdentifier.AllocationSuspended = allocationSuspended;
		}
	}


	/// <summary>
	/// Game thread: the rules for an arriving entity that already exists on this node - duplicate
	/// incarnation abort, fenced-duplicate purge, live client-controlled keep, reseat + re-enter the
	/// scene for a kept copy. Shared by the legacy and the staged import; every rule cites the
	/// incident it came from.
	/// </summary>
	private static bool ImportKeepExisting(ImportContext context, MyObjectBuilder_EntityBase entity, MyEntity existingEntity, HashSet<long> liveControlled)
	{
		ulong partitionId = context.PartitionId;
		// A same-id entity held by ANOTHER partition on this node is a duplicate incarnation
		// (two live copies of one entity in the cluster). Proceeding would freeze the local
		// copy and fail the §6.10 round-trip gate with a baffling residual diff of two
		// genuinely different states (2026-08-10: a phantom of the drill ship in partition 31
		// bounced every co-location handover of partition 30 until both sides blocked). Name
		// the conflict instead, with a stable signature so the gateway's abort counting and
		// HandoverBlocked escalation apply as they would to any validation abort.
		ulong holder = PartitionOfEntity(entity.EntityId);
		if (holder != 0 && holder != partitionId)
		{
			// Unless the holder is a startup-fenced leftover this node does not own in the
			// registry: a fresh incarnation of a slot loads its previously assigned
			// partitions fenced, and until the three fenced-registration strikes purge them
			// (~seconds) an import racing that window would abort on their stale content
			// for nothing. The arriving body is the registry-blessed newest copy of the
			// entity - the gateway drives handovers off registry ownership - so the local
			// stale copy loses, exactly as the strike purge would decide moments later
			// (partitions 489/523 vs fenced 4, every node-2 replacement boot, 2026-08-15).
			// T-0169: a CHARACTER that a client connected to this node controls is the live body by
			// definition (a client controls one body), so an arriving copy of it is a stale duplicate -
			// Pilot-018 on w32 2026-09-17: a Falling, uncontrolled copy recovered from an old save was
			// re-captured into a fresh partition and refused here every ~3 min for 50 min. Keep the live
			// copy, drop the arriving one: validation already excludes live controlled entities, the
			// commit's Adopt moves the live body into the arriving partition, and the source closes its
			// stale copy on release - one incarnation left.
			if (entity is MyObjectBuilder_Character && existingEntity is Sandbox.Game.Entities.Character.MyCharacter
				&& liveControlled.Contains(entity.EntityId))
			{
				Log("import dropped stale duplicate of live client-controlled character " + entity.EntityId
					+ " arriving with partition " + partitionId + " (live copy held by partition " + holder
					+ " on this node, T-0169)");
				if (context.Transfer != null) context.Transfer.KeptLive[entity.EntityId] = holder;
				return true;
			}
			if (m_persistence == null || m_persistence.OwnsPartition(holder))
				throw new InvalidDataException("Duplicate incarnation: entity " + entity.EntityId
					+ " arriving with partition " + partitionId + " is already held by partition " + holder
					+ " on this node; residual-signature="
					+ ImportRoundTrip.Signature(new List<string> { "duplicate/" + entity.EntityId + "@" + holder })
					+ " residual-entities=" + entity.EntityId);
			PartitionPersistence.CloseWithHierarchy(new[] { existingEntity });
			ClusterRuntime.ClearFrozen(entity.EntityId);
			Log("purged fenced duplicate: entity " + entity.EntityId + " held by unowned partition "
				+ holder + "; import of partition " + partitionId + " supplies the current copy");
			return false;
		}
		else
		{
			// An arriving entity a client CONNECTED TO THIS NODE is controlling has had its
			// authority moved by the client path already; ValidateImportedState excludes it
			// from the comparison for exactly that reason (partitions 555/682, 2026-08-16).
			// But excluding it from validation while still freezing and reseating it here
			// yanked the LIVE body onto the source's stale pose - for a seated pilot that is
			// a teleport out from under the cockpit, and with the abort-retry cycle it became
			// the fleet-wide "character left the cockpit" ejection storm of w14 session 7
			// (partition 1469's stale falling copy of a character whose live body sat piloting
			// in partition 1498, 2026-08-18). The live copy is the one adopted: leave its
			// physics, updates and pose alone.
			if (liveControlled.Contains(entity.EntityId))
			{
				Log("import keeps live client-controlled entity " + entity.EntityId
					+ " partition=" + partitionId + "; not freezing or reseating it");
				if (context.Transfer != null) context.Transfer.KeptLive[entity.EntityId] = holder;
				return true;
			}
			if (context.Transfer != null) Freeze(context.Transfer, new[] { existingEntity });
			// Reseat the entity we are KEEPING onto the pose the source froze, exactly as
			// the created branch below does. Without this an entity that is already
			// replicated here keeps whatever pose this node's own copy had drifted to,
			// and the round-trip gate then compares the source's frozen orientation
			// against that drift and aborts the handover. Seen on partition 2 (a slowly
			// rotating one-block grid): six residuals of 1e-4 on Forward/Up/Orientation,
			// every handover it was offered, 2026-08-15. ReseatImported only moves what
			// physics moved and is a no-op when the matrices already agree.
			ReseatImported(existingEntity, entity);
			// A kept local copy that fell OUT OF THE SCENE is the ghost the fleet keeps
			// tripping over: a seated-pilot import whose deferred attach never ran (the
			// cockpit was destroyed, or an abort cycle closed the grid around it) leaves
			// the character existing but invisible and unsimulated, and every later
			// import of a partition carrying it re-emits PersistentFlags without InScene
			// against a source that has it - one residual, repeated to HandoverBlocked
			// (partition 1526, w14, 2026-08-18: pilot-6's orphaned pilot character). The
			// source builder is the authority here exactly as it is for the pose above:
			// re-enter the kept copy into the scene. OnAddedToScene is what
			// MyEntities.Add(entity, insertIntoScene: true) would run; the entity is
			// already registered, so Add itself must not be repeated.
			if (!existingEntity.InScene
				&& (entity.PersistentFlags & VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene) != 0)
			{
				existingEntity.OnAddedToScene(existingEntity);
				Log("import re-entered out-of-scene entity " + entity.EntityId
					+ " into the scene partition=" + partitionId);
			}
			return true;
		}
	}

	/// <summary>Game thread: the §6.10 round-trip gate over everything created so far.</summary>
	private static void ImportValidate(ImportContext context)
	{
		if (context.Transfer != null) ValidateImportedState(context.Transfer, context.Entities, context.Save.Body);
	}

	/// <summary>Game thread: undo a failed import - close what was created, thaw what was frozen.</summary>
	private static void ImportFail(ImportContext context)
	{
		if (context.Failed) return;
		context.Failed = true;
		if (context.Transfer != null)
		{
			CloseEntities(context.Created);
			RestoreFrozen(context.Transfer, context.Created);
			context.Transfer.Import = null;
		}
	}

	/// <summary>Game thread: network ids, membership and the transfer's import record.</summary>
	private static int ImportFinish(ImportContext context)
	{
		Transfer transfer = context.Transfer;
		MyObjectBuilder_EntityBase[] entities = context.Entities;
		try
		{
			ReplicationServerPatch.FinalizeNetworkIds();
			m_partitions.Adopt(context.PartitionId, entities.Select(entity => entity.EntityId));
			// Imported content is legitimate: lift any fence taint an assigned save left on these
			// ids, or a later layout dissolve would hide the real entities from the topology sweep.
			PartitionPersistence.UntaintEntities(entities.Select(entity => entity.EntityId));
			if (transfer != null)
			{
				transfer.Members = entities.Select(entity => entity.EntityId).ToHashSet();
				transfer.BodyChecksum = context.Save.BodyChecksum;
				transfer.ImportCount = context.Count;
				transfer.Imported = true;
				transfer.Import = null;
			}
			m_networkImport.Release(context.ImportId);
		}
		catch
		{
			if (transfer == null)
			{
				DiscardNetworkIds();
				m_networkImport.Release(context.ImportId);
			}
			throw;
		}
		return context.Count;
	}

	/// <summary>
	/// Puts an imported entity back where the source said it was. A live character's Init hands its position
	/// to the Havok character proxy, which re-grounds it against the target node's terrain and sets it down a
	/// couple of centimetres away - far past any tolerance the round-trip check can carry, so the handover was
	/// rejected and rejected again on every retry, freezing and thawing the player each cycle. The source is
	/// the authority on where a member is; the target's physics is not. Grids already round-trip bit-exact, so
	/// this only ever moves what physics moved.
	/// </summary>
	private static void ReseatImported(MyEntity entity, MyObjectBuilder_EntityBase builder)
	{
		if (entity?.PositionComp == null || builder.PositionAndOrientation is not VRage.MyPositionAndOrientation expected) return;
		MatrixD matrix = expected.GetMatrix();
		if (entity.PositionComp.WorldMatrixRef == matrix) return;
		entity.PositionComp.SetWorldMatrix(ref matrix, null, forceUpdate: true);
	}

	private static void ValidateImportedState(Transfer transfer, MyObjectBuilder_EntityBase[] source, byte[] sourceBody)
	{
		ImportComparison comparison = CollectImportedState(transfer, source, sourceBody);
		CompareImportedState(transfer, comparison.GatedSource, source.Length, comparison.Imported, comparison.SourceBody);
	}

	/// <summary>Game thread half of the round-trip gate: the imported entities' builders.</summary>
	private static ImportComparison CollectImportedState(Transfer transfer, MyObjectBuilder_EntityBase[] source, byte[] sourceBody)
	{
		// An arriving entity that a client CONNECTED TO THIS NODE is already controlling has had
		// its authority moved by the client path (reconnect resolve or respawn switch landed the
		// pilot here before the partition import committed): the local copy is live and simulating,
		// so the frozen source snapshot is stale by construction - the pose drifts kilometres
		// between attempts and the round-trip gate can never pass (partitions 555/682, 2026-08-16,
		// a fresh residual signature on every retry). During a NORMAL handover the pilot is still
		// attached to the source when validation runs, so this exclusion self-selects the anomaly
		// and leaves the gate strict for everything else. The live copy is the one adopted.
		HashSet<long> liveControlled = LiveControlledEntityIds();
		MyObjectBuilder_EntityBase[] gatedSource = liveControlled.Count == 0 ? source
			: source.Where(expected => !liveControlled.Contains(expected.EntityId)).ToArray();
		if (gatedSource.Length != source.Length)
			Log("handover validation excludes " + (source.Length - gatedSource.Length)
				+ " live client-controlled entities transferId=" + transfer.Id + " partition=" + transfer.Partition);
		MyObjectBuilder_EntityBase[] imported = gatedSource.Select(expected =>
		{
			if (!MyEntities.TryGetEntityById(expected.EntityId, out MyEntity entity) || entity.MarkedForClose)
				throw new InvalidDataException("Imported entity is missing: " + expected.EntityId);
			entity.BeforeSave();
			MyObjectBuilder_EntityBase builder = entity.GetObjectBuilder();
			if (transfer.Frozen.TryGetValue(entity.EntityId, out FrozenState frozen)) ApplyVelocity(builder, frozen);
			return builder;
		}).ToArray();
		return new ImportComparison { GatedSource = gatedSource, Imported = imported, SourceBody = sourceBody };
	}

	/// <summary>
	/// Thread-agnostic half of the round-trip gate: serialise, compare, dump and decide. Holds no
	/// game state, so the staged import runs it on the handler thread while the game thread goes on.
	/// </summary>
	/// <param name="gatedSource">The gated source entries, read for their ids only (#1259).</param>
	/// <param name="sourceBody">The transfer body: the frozen source the slow path and the dump read.</param>
	/// <param name="preparedSource">The source half built ahead from a copy of the body, or null.</param>
	private static void CompareImportedState(Transfer transfer, MyObjectBuilder_EntityBase[] gatedSource, int sourceCount, MyObjectBuilder_EntityBase[] imported, byte[] sourceBody, ImportRoundTrip.PreparedSource preparedSource = null)
	{
		byte[] importedBody = PartitionSaveFile.SerializeEntities(imported);
		// #1259: the source side is never the builders the entities were created from. An entity may keep
		// and mutate its builder (MyProjectorBase remaps its blueprint in place), so a dump of those showed
		// the post-import state on both sides and could not reproduce the residuals it was written for.
		MyObjectBuilder_EntityBase[] frozen = null;
		MyObjectBuilder_EntityBase[] FrozenSource() => frozen ??= GatedFrozenSource(gatedSource, preparedSource?.Source ?? FrozenSourceCopy(sourceBody, sourceCount));
		// P3-HND-03's perturbation happens inside the document comparison, so while the test plugin has
		// it armed neither shortcut below may answer for it: raw byte equality never looks at the
		// document, and the prepared path compares chunks the seam is not applied to.
		bool perturbed = ClusterSeams.CorruptImportedDocument != null;
		// T-0051: a displaced pilot is stripped from both documents, which the prepared (already
		// normalised) source cannot do - compare the slow way for those transfers.
		HashSet<long> displaced = transfer.DisplacedPilots.Count == 0 ? null : transfer.DisplacedPilots;
		List<string> residuals = !perturbed && gatedSource.Length == sourceCount
				&& sourceBody.AsSpan().SequenceEqual(importedBody)
			? new List<string>()
			: preparedSource != null && !perturbed && displaced == null && preparedSource.Count == imported.Length
				? ImportRoundTrip.ResidualsAgainstPrepared(preparedSource, imported)
			: ImportRoundTrip.Residuals(FrozenSource(), imported, displaced);
		if (residuals.Count > 0)
		{
			string signature = ImportRoundTrip.Signature(residuals);
			foreach (string residual in residuals)
				Log("handover residual transferId=" + transfer.Id + " partition=" + transfer.Partition + " " + residual);
			string directory = Path.Combine(Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT") ?? Path.GetTempPath(), "cluster", "handover-diff");
			Directory.CreateDirectory(directory);
			string prefix = transfer.Partition + "-" + transfer.Id.ToString("N");
			// #1397: gzipped (a tenth of the size); `gunzip -k` restores the plain XML ResidualReplay reads.
			string sourcePath = Path.Combine(directory, prefix + "-source.xml.gz"), targetPath = Path.Combine(directory, prefix + "-target.xml.gz");
			// A mismatch the world cannot resolve on its own repeats every handover rotation - a second and
			// a half apart, hundreds of megabytes an hour. Four pairs are all anyone diagnoses it from.
			string retained;
			if (Directory.GetFiles(directory, transfer.Partition + "-*-source.xml*").Length >= 4)
			{
				retained = "earlier dumps for partition " + transfer.Partition + " are retained in " + directory;
			}
			else
			{
				MyObjectBuilderSerializerKeen.SerializeXML(sourcePath, true, new MyObjectBuilder_Sector { SectorObjects = FrozenSource().ToList() });
				MyObjectBuilderSerializerKeen.SerializeXML(targetPath, true, new MyObjectBuilder_Sector { SectorObjects = imported.ToList() });
				retained = "retained " + sourcePath + " and " + targetPath;
				int evicted = TrimHandoverDiffs(directory, prefix, HandoverDiffBudgetBytes);
				if (evicted > 0) Log("handover-diff budget: deleted the " + evicted + " oldest dump files in " + directory);
			}
			string message = "Frozen source and imported target object builders differ; residuals=" + residuals.Count
				+ " residual-signature=" + signature + ResidualEntities(residuals) + "; " + retained;
			// Partitioning.md 6.10: abort (default) keeps a lossy import out of the world; warn is the
			// production-only escape hatch and still logs, dumps and alarms exactly like the abort.
			if (!m_importCheckWarn) throw new InvalidDataException(message);
			Log("WARNING: import round-trip mismatch tolerated (CLUSTER_IMPORT_CHECK=warn): " + message);
		}
		transfer.StateVerified = true;
		int programmableBlocks = imported.OfType<MyObjectBuilder_CubeGrid>().Sum(grid => grid.CubeBlocks.Count(block => block is MyObjectBuilder_MyProgrammableBlock));
		Log("handover state verified transferId=" + transfer.Id + " partition=" + transfer.Partition + " entities=" + imported.Length
			+ " pbs=" + programmableBlocks + " residuals=" + residuals.Count);
	}

	/// <summary>
	/// #1397: bytes of handover-diff dumps a node keeps. The per-partition cap alone let them reach 0.7-4 GB per
	/// node in a day of octahedron-1191 (71 partitions), and every cluster backup copied them.
	/// </summary>
	internal const long HandoverDiffBudgetBytes = 256L << 20;

	/// <summary>
	/// SE-1134: " residual-entities=ID,ID" for the top-level entities the residual paths start in
	/// (MyObjectBuilder_Sector/SectorObjects/MyObjectBuilder_EntityBase#ID/...), or "" when none does. The gateway keeps
	/// a HandoverBlocked partition's backoff through a membership change that leaves these entities in it.
	/// </summary>
	internal static string ResidualEntities(IEnumerable<string> residuals)
	{
		string[] ids = residuals
			.Select(residual => System.Text.RegularExpressions.Regex.Match(residual, "^[^/]+/SectorObjects/[^/#]+#([0-9]+)"))
			.Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct().ToArray();
		return ids.Length == 0 ? "" : " residual-entities=" + string.Join(",", ids);
	}

	/// <summary>
	/// Deletes the oldest files in <paramref name="directory"/> until it holds at most <paramref name="budget"/>
	/// bytes, sparing the files that start with <paramref name="keepPrefix"/> (the pair just written). Returns
	/// how many it deleted. Two handler threads may trim at once; deleting a file the other one deleted is a no-op.
	/// </summary>
	internal static int TrimHandoverDiffs(string directory, string keepPrefix, long budget)
	{
		FileInfo[] files = new DirectoryInfo(directory).GetFiles().OrderBy(file => file.LastWriteTimeUtc).ToArray();
		long total = files.Sum(file => file.Length);
		int deleted = 0;
		foreach (FileInfo file in files)
		{
			if (total <= budget) break;
			if (file.Name.StartsWith(keepPrefix + "-", StringComparison.Ordinal)) continue;
			long length = file.Length; // FileInfo refuses Length once its file is deleted.
			try { file.Delete(); }
			catch (IOException) { continue; }
			total -= length;
			deleted++;
		}
		return deleted;
	}

	/// <summary>
	/// Thread-agnostic: a copy of a transfer's source builders for the round-trip gate (#1259), deserialised
	/// from the body again because the import creates its entities from the first copy.
	/// </summary>
	private static MyObjectBuilder_EntityBase[] FrozenSourceCopy(byte[] body, int entityCount)
		=> PartitionSaveFile.DeserializeEntities(body, entityCount);

	/// <summary>
	/// The entries of <paramref name="copy"/> (all source entities, in source order) that the gate kept in
	/// <paramref name="gatedSource"/>. Ids are unique in a partition, and an import keeps every id.
	/// </summary>
	internal static MyObjectBuilder_EntityBase[] GatedFrozenSource(MyObjectBuilder_EntityBase[] gatedSource, MyObjectBuilder_EntityBase[] copy)
	{
		if (gatedSource.Length == copy.Length) return copy;
		HashSet<long> gated = gatedSource.Select(entity => entity.EntityId).ToHashSet();
		return copy.Where(entity => gated.Contains(entity.EntityId)).ToArray();
	}

	/// <summary>
	/// Entity ids under live control of clients CONNECTED TO THIS NODE: each online player's
	/// character, its controlled entity, and their top-most parents (the seated character's ship).
	/// Game-thread callers only.
	/// </summary>
	private static HashSet<long> LiveControlledEntityIds()
	{
		HashSet<long> ids = new HashSet<long>();
		var players = Sandbox.Game.Multiplayer.Sync.Players?.GetOnlinePlayers();
		if (players == null) return ids;
		foreach (Sandbox.Game.World.MyPlayer player in players)
		{
			if (player.Character != null)
			{
				ids.Add(player.Character.EntityId);
				ids.Add(player.Character.GetTopMostParent().EntityId);
			}
			if (player.Controller?.ControlledEntity is MyEntity controlled)
			{
				ids.Add(controlled.EntityId);
				ids.Add(controlled.GetTopMostParent().EntityId);
			}
		}
		return ids;
	}

	/// <summary>Which partition currently holds an entity, or 0. Game-thread callers only.</summary>
	public static ulong PartitionOfEntity(long entityId)
		=> m_partitions.Evaluate().FirstOrDefault(partition => partition.Members.Contains(entityId))?.Id ?? 0;

	/// <summary>Which partition holds an entity in the standing layout (no sweep), or 0. Game-thread callers only.</summary>
	public static ulong StandingPartitionOfEntity(long entityId) => m_partitions.Holder(entityId);
	internal static uint OwnedGeneration(ulong partition) => m_persistence?.OwnedGeneration(partition) ?? 0;

	/// <summary>T-0307: on a live registry node the topology lock (PartitionManager.Owned) must be installed
	/// and agree with this node's ownership. Null when correct; the checked-id count goes to the caller.</summary>
	internal static string TopologyOwnershipWiring(out int checkedOwned)
	{
		checkedOwned = 0;
		if (m_partitions == null || m_persistence == null) return null;
		(bool registry, ulong[] owned) = m_persistence.OwnershipSnapshot();
		if (!registry) return m_partitions.Owned == null ? null : "Owned installed without a registry";
		if (m_partitions.Owned == null) return "PartitionManager.Owned is not installed";
		if (m_partitions.Owned(ulong.MaxValue)) return "Owned answers true for an id nobody owns";
		foreach (ulong id in owned)
		{
			if (!m_partitions.Owned(id)) return "Owned answers false for owned partition " + id;
			checkedOwned++;
		}
		return null;
	}
	internal static Dictionary<ulong, long> OwnedSnapshot() => m_persistence?.PluginOwnershipSnapshot() ?? new();

	/// <summary>Member -> partition over the standing layout (no sweep). Game-thread callers only.</summary>
	public static Dictionary<long, ulong> StandingPartitionsOfEntities() => m_partitions.Holders();

	private static void ApplyVelocity(MyObjectBuilder_EntityBase builder, FrozenState frozen)
	{
		if (builder is MyObjectBuilder_CubeGrid grid)
		{
			grid.LinearVelocity = frozen.LinearVelocity;
			grid.AngularVelocity = frozen.AngularVelocity;
		}
		else if (builder is MyObjectBuilder_Character character) character.LinearVelocity = frozen.LinearVelocity;
	}


	private static string ExportClientState(ulong client, HashSet<long> members)
	{
		ClientHandoverState snapshot = ReplicationServerPatch.Export(client, members);
		string encoded = ClientHandoverCodec.Write(snapshot);
		Log("client-view export client=" + client + " replicables=" + snapshot.Replicables.Count + " pending=" + snapshot.Replicables.Count(x => x.IsPending) + " streaming=" + snapshot.Replicables.Count(x => x.IsStreaming) + " permanent=" + snapshot.Replicables.Count(x => x.PermanentLayer.HasValue) + " cachedVoxels=" + snapshot.CachedVoxelNames.Count + " knownPlanets=" + snapshot.KnownSectors.Count);
		// A pending replicable is what keeps MyGuiScreenMedicals' batch confirmation - and with it
		// the spawn button - waiting forever (2026-08-11). Name the culprits.
		foreach (ClientReplicableState stuck in snapshot.Replicables.Where(x => x.IsPending))
			Log("client-view pending client=" + client + " networkId=" + stuck.NetworkId
				+ " streaming=" + stuck.IsStreaming + " entity=" + ReplicationServerPatch.DescribeNetworkObject(stuck.NetworkId));
		return encoded;
	}

	private static int PruneClientState(ulong client, string state)
	{
		int count = ReplicationServerPatch.Prune(client, ClientHandoverCodec.Read(state));
		Log("client-view prune client=" + client + " removed=" + count);
		return count;
	}

	private static void ResumeClientState(ulong client)
	{
		ReplicationServerPatch.Resume(client);
	}

	private static void FinishClientState(ulong client)
	{
		ReplicationServerPatch.Finish(client);
	}

	private static int AdoptClient(ulong client, string state, Guid? transferId, ulong? partitionId, uint? generation)
	{
		Transfer transfer = transferId.HasValue ? GetTarget(transferId.Value, partitionId.Value, generation.Value) : null;
		if (transfer != null)
		{
			if (transfer.Clientless)
			{
				throw new InvalidOperationException("Clientless transfer cannot adopt a client");
			}
			if (transfer.Discarded)
			{
				throw new InvalidOperationException("Transfer was discarded");
			}
			if (!transfer.Imported)
			{
				throw new InvalidOperationException("Transfer was not imported");
			}
			if (transfer.Clients.Length != 0 && !transfer.Clients.Contains(client))
			{
				throw new InvalidOperationException("Client is not part of this transfer");
			}
			// Adoption is retried per client, so it must be idempotent per client rather than per
			// transfer: a retry for one client must not be answered with another client's count.
			if (transfer.AdoptCounts.TryGetValue(client, out int already))
			{
				if (!transfer.ClientStates.TryGetValue(client, out string adoptedState) || adoptedState != state)
				{
					throw new InvalidOperationException("Transfer client adoption changed");
				}
				return already;
			}
		}
		ClientHandoverState snapshot = ClientHandoverCodec.Read(state);
		int count = ReplicationServerPatch.Adopt(client, snapshot);
		if (snapshot.ControlledEntityId != 0)
		{
			if (!MyEntities.TryGetEntityById(snapshot.ControlledEntityId, out MyEntity controlled)
				|| !MySession.Static.Players.TryGetPlayerBySteamId(client, out var player))
				throw new InvalidOperationException("Transferred controlled entity is not ready");
			// The snapshot's controlled entity is REPLICATION-layer truth: a seated player's controlled
			// replicable is the whole grid, because a cockpit block has no replicable of its own. The
			// game-layer controller must point at the cockpit block instead - otherwise
			// GetControllingPlayer(cockpit) is null and RemovePilot's switch-to-character relay is never
			// sent, which strands the client without a character the moment it stands up (P3-HND-02
			// seated cell, 2026-08-01).
			if (controlled is MyCubeGrid
				&& MyEntities.TryGetEntityById(snapshot.CharacterEntityId, out MyEntity transferred)
				&& transferred is MyCharacter pilotCharacter
				&& pilotCharacter.Parent is MyCockpit seat && seat.Pilot == pilotCharacter)
				controlled = seat;
			// Never vanilla SetControlledEntity here: the client carried this entity through the transfer
			// and still holds it even when the server-side controller is not wired yet, so the broadcast
			// would remove the client's own map entry and then no-op in TakeControl, leaving every input
			// packet from this client silently dropped (Defect 4 - the discriminator used to be the
			// server controller, which is the wrong half to ask).
			if (player.Controller?.ControlledEntity?.Entity?.EntityId == controlled.EntityId)
				MySession.Static.Players.ClusterEnsureControlledEntity(player.Id, controlled);
			else
				MySession.Static.Players.ClusterTakeControlNoSync(player.Id, controlled);
			// That assignment is a one-shot the client can drop: it lands mid-adopt, and the client only
			// acts on it if the entity is resolvable there at that instant. Repeat it once the transfer
			// has settled - see ControlReassertService for why nothing else picks the client back up.
			ControlReassertService.Schedule(client, controlled.EntityId);
		}
		// The adopted character starts with an empty broadcasters-in-range cache, and the far-broadcaster
		// replicables that carry the HUD signal markers are derived from it. Filling it here instead of
		// waiting for the character's next 10th-frame tick gets the markers back on the next server frame
		// rather than leaving the player looking at a beacon-less HUD after the switch.
		if (MyEntities.TryGetEntityById(snapshot.CharacterEntityId, out MyEntity adopted) && adopted is MyCharacter character)
		{
			character.RadioReceiver?.UpdateBroadcastersInRange();
			// NOT part of the log line. This MUTATES replication state: it sets DependenciesDirty on every
			// online player, which zeroes their OUTERMOST update layer's timer on the next server frame
			// (MyReplicationServer.cs:441-444) and forces that layer - the largest radius, the biggest
			// GetReplicablesInBox - to rebuild instead of waiting out its 960-frame period. Evaluated
			// inside the Log(...) argument it read as reporting, so making the log conditional, cheap or
			// verbose-only would have silently deleted the invalidation; the symptom would be HUD markers
			// intermittently failing to return after a handover, with no visible connection to a logging
			// change. Hoisted so the mutation is a statement and the log only reports it.
			int invalidatedClients = MyCharacterReplicableSeams.InvalidateBroadcasterDependencies();
			Log("client-view adopt broadcasters client=" + client + " inRange=" + (character.RadioReceiver?.BroadcastersInRange.Count ?? 0)
				+ " invalidatedClients=" + invalidatedClients);
		}
		Log("client-view adopt client=" + client + " replicables=" + count + " controlled=" + snapshot.ControlledEntityId
			+ " character=" + snapshot.CharacterEntityId + " pending=" + snapshot.Replicables.Count(x => x.IsPending) + " streaming=" + snapshot.Replicables.Count(x => x.IsStreaming)
			+ " permanent=" + snapshot.Replicables.Count(x => x.PermanentLayer.HasValue) + " cachedVoxels=" + snapshot.CachedVoxelNames.Count + " knownPlanets=" + snapshot.KnownSectors.Count);
		if (transfer != null)
		{
			transfer.ClientStates[client] = state;
			transfer.AdoptCounts[client] = count;
		}
		return count;
	}

	private static void PrepareClientAdoption(ulong client, string state)
	{
		ReplicationServerPatch.PrepareAdoption(client, ClientHandoverCodec.Read(state));
	}

	/// <summary>
	/// Empties the client's replication view on this node and returns the encoded remainder so another node can
	/// adopt the client without a fresh join handshake. Used by a cross-node respawn, where the client keeps its
	/// loaded world but every replicable it holds belongs to the node it is leaving.
	/// </summary>
	internal static string DetachClientView(ulong client)
	{
		// KnownSectors are deliberately left empty so the destination node re-streams its own planet sectors.
		ClientHandoverState snapshot = ReplicationServerPatch.ExportDetached(client);
		string encoded = ClientHandoverCodec.Write(snapshot);
		int destroyed = ReplicationServerPatch.Prune(client, snapshot);
		ReplicationServerPatch.Finish(client);
		Log("client-view detach client=" + client + " destroyed=" + destroyed);
		return encoded;
	}

	internal static int AdoptClientView(ulong client, string state)
	{
		int count = ReplicationServerPatch.Adopt(client, ClientHandoverCodec.Read(state));
		Log("client-view attach client=" + client + " replicables=" + count);
		return count;
	}

	/// <summary>
	/// Handler-thread driver. release.closePerFrame=0 (default): one game-thread Invoke as before.
	/// N>0: the released roots are closed N per frame (hierarchy children go with their root), then
	/// the bookkeeping runs in a final frame - the close stall of a large grid becomes short frames.
	/// </summary>
	private static int Release(Guid transferId, ulong partitionId, uint generation)
	{
		int perFrame = ClusterConfig.GetInt("release.closePerFrame");
		if (perFrame <= 0)
			return InvokePhase("release", transferId, partitionId, () =>
			{
				Transfer transfer = ReleaseBegin(transferId, partitionId, generation);
				if (transfer == null) return GetSource(transferId, partitionId, generation).ReleaseCount;
				HandoverPhaseTimer.Current?.Step("release_close");
				int releaseIndex = NodeAgeCounter.Releases.Next();
				ImportAddSlice timed = ReleaseTimers();
				int closed = CloseEntities(transfer.Members, timed) + CloseEntities(transfer.Descendants, timed);
				AppendReleaseFields(timed, releaseIndex);
				HandoverPhaseTimer.Current?.Step("release_finish");
				return ReleaseFinish(transfer, closed);
			});
		Transfer chunked = InvokePhase("release_begin", transferId, partitionId, () => ReleaseBegin(transferId, partitionId, generation));
		if (chunked == null) return Invoke(() => GetSource(transferId, partitionId, generation).ReleaseCount);
		int chunkedIndex = NodeAgeCounter.Releases.Next();
		try
		{
			int closed = 0;
			// Roots first, then the descendants vanilla may have re-topped (see ReleaseFinish's caller
			// history): a seated pilot closes with its grid, a detached one in its own slice.
			long[][] slices = chunked.Members.Concat(chunked.Descendants).Distinct().Chunk(perFrame).ToArray();
			foreach (long[] slice in slices)
				closed += InvokePhase("release_close", transferId, partitionId, () =>
				{
					ImportAddSlice timed = ReleaseTimers();
					int count = CloseEntities(slice, timed);
					AppendReleaseFields(timed, chunkedIndex);
					return count;
				});
			return InvokePhase("release_finish", transferId, partitionId, () => ReleaseFinish(chunked, closed));
		}
		catch
		{
			// Half-closed is not a state the transfer can stay in: the gateway retries the release,
			// and CloseEntities skips what is already MarkedForClose.
			try { Invoke(() => { chunked.Releasing = false; return 0; }); } catch { }
			throw;
		}
	}

	/// <summary>Game thread. Null = already released (idempotent retry).</summary>
	private static Transfer ReleaseBegin(Guid transferId, ulong partitionId, uint generation)
	{
		Transfer transfer = GetSource(transferId, partitionId, generation);
		if (transfer.Released)
		{
			return null;
		}
		if (transfer.Thawed)
		{
			throw new InvalidOperationException("Transfer was thawed");
		}
		if (transfer.Releasing)
		{
			throw new InvalidOperationException("Transfer release is in progress");
		}
		transfer.Releasing = true;
		return transfer;
	}

	/// <summary>Game thread: everything after the closes.</summary>
	private static int ReleaseFinish(Transfer transfer, int closed)
	{
		// A seated pilot travels inside its grid's object builder but vanilla re-tops the
		// character entity when the switching client detaches mid-transfer - closing only
		// the member roots leaves a live duplicate that spawns its own partition and an
		// endless failing co-location retry against the real character on the target.
		// (Descendants are therefore closed alongside Members by every caller.)
		transfer.ReleaseCount = closed;
		foreach (ulong pruned in transfer.PrunedClients) FinishClientState(pruned);
		ClearFrozen(transfer);
		m_persistence.ForgetOwnership(transfer.Partition);
		transfer.Released = true;
		transfer.FinishedTimestamp = Stopwatch.GetTimestamp();
		transfer.Releasing = false;
		m_tees.TryRemove(transfer.Id, out _);
		IgcDeliveryService.HandoverReleased(transfer.Members);
		PluginHandoverService.Left(transfer.PluginGrids);
		return transfer.ReleaseCount;
	}

	private static int Discard(Guid transferId, ulong partitionId, uint generation)
	{
		Transfer transfer = GetTarget(transferId, partitionId, generation);
		if (transfer.CommittedGeneration != 0)
		{
			throw new InvalidOperationException("Committed transfer cannot be discarded");
		}
		if (transfer.Discarded)
		{
			return transfer.DiscardCount;
		}
		transfer.DiscardCount = CloseEntities(transfer.CreatedMembers);
		RestoreFrozen(transfer, transfer.CreatedMembers);
		// T-0296 r3: the import's Adopt moved every kept live body into the arriving layout, which this node does
		// not own after a discard. Left there, its saves are refused as fenced and the fenced-orphan purge closes
		// it as a leftover incarnation - the body a connected client is controlling.
		if (transfer.KeptLive.Count != 0)
		{
			m_partitions.ReturnKept(transfer.Partition, transfer.KeptLive);
			Log("discard handed " + transfer.KeptLive.Count + " kept live entit" + (transfer.KeptLive.Count == 1 ? "y" : "ies")
				+ " back from discarded partition " + transfer.Partition + ": " + string.Join(",", transfer.KeptLive.Keys));
		}
		if (m_networkImport.IsOwnedBy(transferId))
		{
			DiscardNetworkIds();
			m_networkImport.Release(transferId);
		}
		transfer.Discarded = true;
		transfer.FinishedTimestamp = Stopwatch.GetTimestamp();
		return transfer.DiscardCount;
	}

	/// <summary>Elapsed milliseconds between two <see cref="Stopwatch"/> timestamps, one decimal.</summary>
	private static string Ms(long from, long to)
		=> ((to - from) * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture);

	private static void Commit(Guid transferId, ulong partitionId, uint generation, uint committedGeneration)
	{
		Transfer transfer = GetTarget(transferId, partitionId, generation);
		if (!transfer.Imported || (!transfer.Clientless && !transfer.Adopted) || transfer.Discarded)
		{
			throw new InvalidOperationException("Transfer is not ready to commit");
		}
		if (committedGeneration != generation + 1)
		{
			throw new InvalidOperationException("Committed generation must follow the source generation");
		}
		if (transfer.CommittedGeneration != 0 && transfer.CommittedGeneration != committedGeneration)
		{
			throw new InvalidOperationException("Transfer committed generation changed");
		}
		if (transfer.CommittedGeneration == committedGeneration)
		{
			return;
		}
		// MEASUREMENT: the `commit` phase timer accounts for only 0.8-10.6% of the stall it sits inside
		// (factory-fa, 2026-08-22, n=7 hitches from 1 to 200 entities), and a sampling profiler cannot
		// aim at it - a commit is ~2 s and dotnet-trace takes ~10 s to attach, so a reactive trace
		// missed it outright. These sub-steps split the in-phase half; whatever the hitch line still
		// reports beyond their total is post-phase work, by subtraction.
		long t0 = Stopwatch.GetTimestamp();
		// T-0150: once only. The gateway retries a failed commit, and by then the regular pass may have
		// split the adopted partition and retired its id at the registry; a second SetGeneration put the
		// retired id back into m_owned and its next split was answered "ownership is fenced".
		long tGeneration = t0, tAdopt = t0;
		if (!transfer.LocallyAdopted)
		{
			m_persistence.SetGeneration(partitionId, committedGeneration);
			tGeneration = Stopwatch.GetTimestamp();
			// T-0292: BEFORE the mature adoption. A mature arrival may merge on the next pass, and a fast
			// member re-measured from scratch reads slow for ~3 s: the source's fast state is carried instead.
			m_partitions.SeedFast(PluginHandoverService.FastArrivals(transfer.PluginStates)
				.Where(x => transfer.Members.Contains(x.Key)), "handover " + transferId);
			m_partitions.Adopt(partitionId, transfer.Members, true);
			transfer.LocallyAdopted = true;
			tAdopt = Stopwatch.GetTimestamp();
		}
		// T-0401: the thaw is where an imported body gets its real Havok cluster placement (with import.deferPhysics its
		// only one) - timed like the import Adds (reorders, creates, physics), its own handover_thaw line.
		ImportAddSlice thaw = ClusterConfig.GetBool("diag.importAddTimers") ? new ImportAddSlice() : null;
		int restored = 0;
		string ensure = "";
		// T-0404 (import.thawEnsureSpace): one cluster placement for the bodies about to be enabled, inside the timed slice.
		ImportAddDiag.Run(null, thaw, () => { ensure = ThawEnsureSpace(transfer); restored = RestoreFrozen(transfer); });
		if (thaw != null)
			Log("handover_thaw transferId=" + transferId + " partition=" + partitionId + " restored=" + restored
				// the TRANSFER's own mode (its entities were evaluated for deferral), not the knob at thaw time: an import
				// Added before a flip and thawed after it logged the new value (r121i 06:04:57, node-720) - knobAtThaw is that
				+ " deferPhysics=" + (transfer.PhysicsDeferEvaluated > 0 ? "true" : "false")
				+ " knobAtThaw=" + ClusterConfig.GetBool("import.deferPhysics").ToString().ToLowerInvariant()
				+ " deferred=" + transfer.PhysicsDeferred + " deferredChildren=" + transfer.PhysicsDeferredChildren + " notDeferred=" + transfer.PhysicsNotDeferred.Values.Sum()
				+ " notDeferredBy=" + (transfer.PhysicsNotDeferred.Count == 0 ? "-"
					: string.Join(",", transfer.PhysicsNotDeferred.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value))) + ensure + thaw.Fields());
		long tRestore = Stopwatch.GetTimestamp();
		// T-0150: held, so this pass cannot split the partition being committed - a split here threw
		// below, and the gateway's retry split the retired id again into a fence that killed the node.
		PartitionManager.Partition[] evaluated = m_partitions.EvaluateHolding(partitionId).ToArray();
		// On a retry the regular pass may already have split it: the arriving members are then spread
		// over its outputs, and the clients need all of them.
		HashSet<long> committedMembers = evaluated.SingleOrDefault(x => x.Id == partitionId)?.Members
			?? evaluated.Where(x => x.Members.Overlaps(transfer.Members)).SelectMany(x => x.Members).ToHashSet();
		long tEvaluate = Stopwatch.GetTimestamp();
		if (!transfer.Clientless)
			foreach (ulong adoptedClient in transfer.Clients) ReplicationServerPatch.AddEntities(adoptedClient, committedMembers);
		long tAddEntities = Stopwatch.GetTimestamp();
		string report = m_partitions.ReportJson();
		long tReportJson = Stopwatch.GetTimestamp();
		Log("post-commit partitions " + report);
		long tLog = Stopwatch.GetTimestamp();
		// One line per commit. Commits are per handover, not per frame, so this cannot flood; and a
		// threshold would hide the cheap ones, which are the control for the expensive ones.
		Log("commit_steps partition=" + partitionId + " members=" + transfer.Members.Count
			+ " clients=" + (transfer.Clientless ? 0 : transfer.Clients.Count()) + " restored=" + restored
			+ " setGenerationMs=" + Ms(t0, tGeneration) + " adoptMs=" + Ms(tGeneration, tAdopt)
			+ " restoreFrozenMs=" + Ms(tAdopt, tRestore) + " evaluateMs=" + Ms(tRestore, tEvaluate)
			+ " addEntitiesMs=" + Ms(tEvaluate, tAddEntities) + " reportJsonMs=" + Ms(tAddEntities, tReportJson)
			+ " logMs=" + Ms(tReportJson, tLog) + " reportBytes=" + report.Length
			+ " totalMs=" + Ms(t0, tLog));
		transfer.CommittedGeneration = committedGeneration;
		transfer.FinishedTimestamp = Stopwatch.GetTimestamp();
		ImportRecoveryProbe.Note(transferId, partitionId, transfer.Members.Count,
			transfer.Clientless ? 0 : transfer.Clients.Count());
		ClientLayerRefresh.NoteImport(committedMembers);   // T-0300: offer the new grids to clients now, not at the next 16 s layer refresh
		MySessionComponentTrashPatch.ProtectArrivals(committedMembers); // T-0254: 60 s for links to re-form
		PluginHandoverService.Arrived(transfer.Members, transfer.PluginStates); // T-0256
		transfer.PluginStates = null;
	}

	private static int ThawSource(Guid transferId, ulong partitionId, uint generation)
	{
		Transfer transfer = GetSource(transferId, partitionId, generation);
		if (transfer.Released || transfer.Thawed)
		{
			return 0;
		}
		int count = RestoreFrozen(transfer);
		foreach (ulong pruned in transfer.PrunedClients) ResumeClientState(pruned);
		transfer.Thawed = true;
		transfer.FinishedTimestamp = Stopwatch.GetTimestamp();
		m_tees.TryRemove(transfer.Id, out _);
		IgcDeliveryService.HandoverThawed(transfer.Members);
		PluginHandoverService.Stayed(transfer.PluginGrids);
		return count;
	}

	/// <summary>
	/// T-0401 fix (import.deferPhysics): disables the physics of <paramref name="entity"/> and of every hierarchy child
	/// with a body (a connector's detector, a block subpart) BEFORE its MyEntities.Add, so the Add's activation
	/// (MyEntity.OnAddedToScene -> Physics.Activate, gated on Enabled) places nothing in the Havok cluster tree; returns
	/// {entityId: Enabled as it was} for Freeze to record, or null when the entity is left as before: not a grid, no
	/// physics, already in the scene, or a seated pilot (in the builder or as a hierarchy child: a pilot activates into
	/// its grid's HavokWorld - the 2026-08-10 signal 11 class Freeze's comment describes - so such a grid keeps the
	/// vanilla order). Children are disabled before the parent, as Freeze does; the thaw (RestoreFrozen, parents first)
	/// then enables exactly what Freeze would have recorded, so the thawed state is the same as without the knob - only
	/// the activation inside the Add and its teardown by the freeze are skipped.
	/// </summary>
	private static Dictionary<long, DeferredBody> DeferPhysics(MyEntity entity, MyObjectBuilder_EntityBase builder, Transfer transfer)
	{
		transfer.PhysicsDeferEvaluated++;
		string reason = DeferPhysicsRefusal(entity, builder, out List<MyEntity> bodies);
		if (reason != null)
		{
			transfer.PhysicsNotDeferred.TryGetValue(reason, out int count);
			transfer.PhysicsNotDeferred[reason] = count + 1;
			return null;
		}
		// Read before disabling: MyPhysicsBody's LinearVelocity/AngularVelocity getters return zero while Enabled is false,
		// so a body disabled here would make Freeze record a moving grid at rest (r121h 2026-10-08 00:51: the builder
		// validation aborted 7 handovers on 'LinearVelocity present on the source side, absent on the target side', and
		// a thaw would have stopped the ship). MyCubeGrid.Init has already applied the builder's velocities to the body.
		Dictionary<long, DeferredBody> states = new Dictionary<long, DeferredBody>();
		foreach (MyEntity body in bodies)
			states[body.EntityId] = new DeferredBody(body.Physics.Enabled, body.Physics.LinearVelocity, body.Physics.AngularVelocity);
		for (int index = bodies.Count - 1; index >= 0; index--) bodies[index].Physics.Enabled = false;
		transfer.PhysicsDeferred++;
		transfer.PhysicsDeferredChildren += bodies.Count - 1;
		return states;
	}

	/// <summary>Why <see cref="DeferPhysics"/> leaves an entity as before (a handover_thaw notDeferredBy key), or null with
	/// <paramref name="bodies"/> = the entity, then its hierarchy children that have a body.</summary>
	private static string DeferPhysicsRefusal(MyEntity entity, MyObjectBuilder_EntityBase builder, out List<MyEntity> bodies)
	{
		bodies = null;
		if (entity is not MyCubeGrid) return "notGrid";
		if (entity.Physics == null) return "noPhysics";
		if (entity.InScene) return "inScene";
		if (builder is MyObjectBuilder_CubeGrid grid && grid.CubeBlocks.OfType<MyObjectBuilder_Cockpit>().Any(seat => seat.Pilot != null))
			return "pilot";
		bodies = new List<MyEntity> { entity };
		if (entity.Hierarchy != null)
		{
			HashSet<VRage.ModAPI.IMyEntity> children = new HashSet<VRage.ModAPI.IMyEntity>();
			entity.Hierarchy.GetChildrenRecursive(children);
			foreach (VRage.ModAPI.IMyEntity child in children)
			{
				if (child is Sandbox.Game.Entities.Character.MyCharacter) return "pilot";
				if (child is MyEntity member && member.Physics != null) bodies.Add(member);
			}
		}
		return null;
	}

	/// <summary>T-0401 fix: a body's state as <see cref="DeferPhysics"/> found it, before it disabled the body.</summary>
	private readonly record struct DeferredBody(bool Enabled, Vector3 LinearVelocity, Vector3 AngularVelocity);

	/// <param name="deferred">T-0401 fix: the Enabled state and velocities an entity had before <see cref="DeferPhysics"/>
	/// turned its physics off ahead of its Add; recorded instead of the current ones (false, zero), so the builder
	/// validation sees the source's velocities and the thaw enables and moves it as the source had it.</param>
	private static void Freeze(Transfer transfer, IEnumerable<MyEntity> entities, IReadOnlyDictionary<long, DeferredBody> deferred = null)
	{
		OfflineCompensation.CancelScope(transfer.Partition);
		foreach (MyEntity entity in entities)
		{
			if (entity == null || transfer.Frozen.ContainsKey(entity.EntityId)) continue;
			// A pilot seated in a cockpit is a hierarchy child with its own physics body in the same
			// Havok world as its grid. Freezing only the grid pulls that world out from under the
			// pilot: removing the grid - the cluster's last object - destroys the HkWorld while the
			// child's body still references it, and the cockpit's deferred AttachPilot later
			// deactivates the pilot against the freed native world (signal 11 on node-2/node-3,
			// 2026-08-10: imported Respawn Rover carrying a disconnected player's seated character).
			// Freeze the whole hierarchy: children are disabled before the parent so each child
			// detaches while the world it lives in is still alive, and recorded after the parent so
			// RestoreFrozen brings the grid back into a world before its children re-activate into it.
			List<MyEntity> hierarchy = new List<MyEntity> { entity };
			if (entity.Hierarchy != null)
			{
				HashSet<VRage.ModAPI.IMyEntity> children = new HashSet<VRage.ModAPI.IMyEntity>();
				entity.Hierarchy.GetChildrenRecursive(children);
				foreach (VRage.ModAPI.IMyEntity child in children)
					if (child is MyEntity member && !transfer.Frozen.ContainsKey(member.EntityId))
						hierarchy.Add(member);
			}
			foreach (MyEntity member in hierarchy)
			{
				ClusterRuntime.MarkFrozen(member.EntityId, transfer.Id);
				FrozenState frozen = deferred != null && deferred.TryGetValue(member.EntityId, out DeferredBody was)
					? new FrozenState(member.NeedsUpdate, member.Physics != null, was.Enabled, was.LinearVelocity, was.AngularVelocity)
					: new FrozenState(member.NeedsUpdate, member.Physics != null, member.Physics?.Enabled == true,
						member.Physics?.LinearVelocity ?? Vector3.Zero, member.Physics?.AngularVelocity ?? Vector3.Zero);
				transfer.Frozen.Add(member.EntityId, frozen);
			}
			for (int index = hierarchy.Count - 1; index >= 0; index--)
			{
				MyEntity member = hierarchy[index];
				member.NeedsUpdate = MyEntityUpdateEnum.NONE;
				if (member.Physics != null) member.Physics.Enabled = false;
			}
		}
	}

	private static int RestoreFrozen(Transfer transfer, HashSet<long> clearOnly = null)
	{
		int count = 0;
		// Parents before children: a hierarchy child re-activates into its topmost parent's Havok
		// world, so the grid must be back in one first. Dictionary enumeration order is insertion
		// order only by accident; sort explicitly (OrderBy is stable, so the recorded order is kept
		// within each group).
		foreach ((long id, FrozenState state) in transfer.Frozen
			.OrderBy(pair => MyEntities.TryGetEntityById(pair.Key, out MyEntity frozen) && frozen.Parent != null ? 1 : 0))
		{
			if (clearOnly == null || !clearOnly.Contains(id))
			{
				if (MyEntities.TryGetEntityById(id, out MyEntity entity) && !entity.MarkedForClose)
				{
					entity.NeedsUpdate = state.NeedsUpdate;
					if (state.HadPhysics && entity.Physics != null)
					{
						entity.Physics.Enabled = state.PhysicsEnabled;
						entity.Physics.LinearVelocity = state.LinearVelocity;
						entity.Physics.AngularVelocity = state.AngularVelocity;
					}
					// A hierarchy child is a seated pilot; its walk proxy is AttachPilot/RemovePilot's
					// business, and forcing UpdateCharacterPhysics here would rebuild standing physics
					// under a seated character.
					if (entity.Parent == null) RestoreCharacterProxy(entity);
					count++;
				}
			}
			ClusterRuntime.ClearFrozen(id, transfer.Id);
		}
		transfer.Frozen.Clear();
		return count;
	}

	/// <summary>
	/// Give a thawed character back its Havok character controller.
	/// </summary>
	/// <remarks>
	/// A character's ability to walk lives in <c>Physics.CharacterProxy</c>, not in the rigid body, and
	/// <c>MyPhysicsBody.Deactivate</c> has a branch that discards it outright rather than deactivating it.
	/// Re-enabling physics then activates nothing: the entity is in the right partition at the right
	/// position, the server can still teleport it, and it cannot move a millimetre under its own power.
	/// That is the "stuck, but I can still rotate" report, and only entering a cockpit fixed it - because
	/// <c>OnControlAcquired</c> happens to call <c>UpdateCharacterPhysics</c>.
	///
	/// Nothing calls that after a thaw, so this does. Vanilla's own precedent for a missing proxy is a
	/// forced rebuild (<c>MyCharacter</c> does exactly this when crouching finds a null proxy), and the
	/// rebuild only runs once physics is enabled again - which is why this is called after the restore
	/// rather than before it.
	/// </remarks>
	private static void RestoreCharacterProxy(MyEntity entity)
	{
		if (entity is not MyCharacter character || character.Physics == null) return;
		// Vanilla's own test for "this character's physics is wrong" is `Physics == null || IsStatic`,
		// and a missing proxy belongs with it: both produce a character that rotates and teleports but
		// will not walk. A static body is the network-proxy shape UpdateCharacterPhysics builds for a
		// character it does not simulate - mass 0, RBF_STATIC - which self-propels no better than a
		// missing controller does.
		string reason = character.Physics.CharacterProxy == null ? "no-proxy"
			: character.Physics.IsStatic ? "static" : null;
		if (reason == null) return;
		character.UpdateCharacterPhysics(forceUpdate: true);
		Log("character_physics_rebuilt entityId=" + character.EntityId + " reason=" + reason
			+ " proxy=" + (character.Physics?.CharacterProxy != null)
			+ " static=" + (character.Physics?.IsStatic ?? false));
	}

	private static void ClearFrozen(Transfer transfer)
	{
		foreach (long id in transfer.Frozen.Keys) ClusterRuntime.ClearFrozen(id, transfer.Id);
		transfer.Frozen.Clear();
	}

	/// <summary>T-0401 (diag.importAddTimers): a release-side timing slice for this frame's closes, null when off.</summary>
	private static ImportAddSlice ReleaseTimers() => ClusterConfig.GetBool("diag.importAddTimers") ? new ImportAddSlice { Release = true } : null;

	/// <summary>T-0401: the frame's rel* fields, then nodeAgeS and releaseIndex, last on its release / release_close line.</summary>
	private static void AppendReleaseFields(ImportAddSlice timed, int releaseIndex)
	{
		if (timed != null && HandoverPhaseTimer.Current is HandoverPhaseTimer timer)
			timer.Fields += timed.ReleaseFields() + NodeAgeCounter.Releases.Fields(releaseIndex);
	}

	/// <param name="release">T-0401: when set (a source release with diag.importAddTimers), the close sequence below
	/// runs unchanged inside ImportAddDiag.RunRelease, which times it; null runs it exactly as before.</param>
	private static int CloseEntities(IEnumerable<long> ids, ImportAddSlice release = null)
	{
		HashSet<long> selected = ids.ToHashSet();
		int count = 0;
		// Was MyEntities.GetEntities().Where(e => selected.Contains(e.EntityId) ...), a full
		// world scan per call - and release.closePerFrame calls this ONCE PER SLICE, so a chunked release
		// re-scanned the whole world for every chunk. Looking each id up directly is O(ids) instead of
		// O(world x chunks), and it stops enumerating MyConcurrentHashSet, which was 678.6 ms of contended
		// lock wait in the one 30 s trace that happened to span a handover (n4b, 2026-08-22).
		// The predicate is preserved EXACTLY, including the membership test: GetEntityByIdOrDefault consults
		// MyEntityIdentifier, which can hold ids that are not in the world entity set, and closing one of
		// those is precisely the stray-incarnation class of bug this method exists to avoid. Contains on a
		// MyConcurrentHashSet is O(1), so keeping the test costs nothing.
		var world = MyEntities.GetEntities();
		MyEntity[] targets = selected
			.Select(id => MyEntities.GetEntityByIdOrDefault(id))
			.Where(entity => entity != null && !entity.MarkedForClose && world.Contains(entity))
			.ToArray();
		// Closing a grid ejects its seated pilot (grid close -> RemovePilot) into a live top-level
		// entity that outlives this sweep and gets adopted as a stray duplicate (partition 28,
		// 2026-08-10). Hierarchy children go down with the entity they are attached to.
		HashSet<VRage.ModAPI.IMyEntity> children = new HashSet<VRage.ModAPI.IMyEntity>();
		foreach (MyEntity target in targets) target.Hierarchy?.GetChildrenRecursive(children);
		void CloseAll() => ClusterCloseScope.Run(() =>
		{
			foreach (MyEntity target in targets)
			{
				MyEntities.Close(target);
				count++;
			}
			PartitionPersistence.CloseAttachedChildren(children);
			MyEntities.DeleteRememberedEntities();
		});
		if (release == null)
		{
			CloseAll();
			return count;
		}
		// Sizes read before the close (the blocks are gone after it); the close itself is the same call sequence.
		foreach (MyEntity target in targets) ImportAddDiag.CountEntity(release, target);
		ImportAddDiag.RunRelease(release, targets.Length, CloseAll);
		return count;
	}

	private static string Status(Guid transferId, ulong partitionId, uint generation)
	{
		m_sourceTransfers.TryGetValue(transferId, out Transfer source);
		m_targetTransfers.TryGetValue(transferId, out Transfer target);
		if (source == null && target == null)
		{
			throw new KeyNotFoundException("Unknown transfer " + transferId);
		}
		if (source != null) Validate(source, partitionId, generation);
		if (target != null) Validate(target, partitionId, generation);
		return "{\"transferId\":\"" + transferId + "\",\"partition\":" + partitionId + ",\"generation\":" + generation
			+ ",\"captured\":" + Json(source != null && (source.Capture != null || source.PayloadDropped)) + ",\"prepared\":" + Json(target?.Prepared == true)
			+ ",\"imported\":" + Json(target?.Imported == true) + ",\"stateVerified\":" + Json(target?.StateVerified == true) + ",\"adopted\":" + Json(target?.Adopted == true)
			+ ",\"clientless\":" + Json(target?.Clientless == true) + ",\"committedGeneration\":" + (target?.CommittedGeneration ?? 0)
			+ ",\"released\":" + Json(source?.Released == true) + ",\"thawed\":" + Json(source?.Thawed == true)
			+ ",\"discarded\":" + Json(target?.Discarded == true) + "}";
	}

	private static string Json(bool value) => value ? "true" : "false";

	/// <summary>Seconds after a transfer finishes before its payload is dropped. 0 disables the sweep.</summary>
	private static readonly double FinishedPayloadSeconds = double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_TRANSFER_PAYLOAD_SECONDS"),
		NumberStyles.Float, CultureInfo.InvariantCulture, out double payloadSeconds) ? payloadSeconds : 600.0;
	/// <summary>Seconds after a transfer finishes before its record leaves the table entirely. 0 keeps records.</summary>
	private static readonly double FinishedRecordSeconds = double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_TRANSFER_RECORD_SECONDS"),
		NumberStyles.Float, CultureInfo.InvariantCulture, out double recordSeconds) ? recordSeconds : 3600.0;
	private static long m_nextTransferSweep;

	/// <summary>
	/// T-0147: the transfer tables were never pruned ("Transfers are never pruned from the table", above), so
	/// every handover a node ever took part in kept its capture bytes, frozen object builders, delta capture,
	/// exported client and network states. On the node dumps those payloads were the largest LIVE owners of
	/// large-object-heap strings and byte arrays, and the per-import churn of those buffers is what fragments
	/// the LOH. A finished transfer (released, thawed, committed or discarded) keeps its small record for
	/// retried status/commit/release calls; after FinishedPayloadSeconds its payload is dropped, and after
	/// FinishedRecordSeconds the record itself is removed - a gateway retry that late is already a failure.
	/// GAME-THREAD ONLY, like every other access to both tables.
	/// </summary>
	internal static void SweepFinishedTransfers()
	{
		if (FinishedPayloadSeconds <= 0) return;
		long now = Stopwatch.GetTimestamp();
		if (now < m_nextTransferSweep) return;
		m_nextTransferSweep = now + 30 * Stopwatch.Frequency;
		int dropped = 0, removed = 0;
		long payloadBytes = 0;
		foreach (Dictionary<Guid, Transfer> table in new[] { m_sourceTransfers, m_targetTransfers })
		{
			List<Guid> expired = null;
			foreach (KeyValuePair<Guid, Transfer> entry in table)
			{
				Transfer transfer = entry.Value;
				if (transfer.FinishedTimestamp == 0 || transfer.Releasing || transfer.Import != null) continue;
				double age = (now - transfer.FinishedTimestamp) / (double)Stopwatch.Frequency;
				if (FinishedRecordSeconds > 0 && age >= FinishedRecordSeconds)
				{
					(expired ??= new List<Guid>()).Add(entry.Key);
					continue;
				}
				if (transfer.PayloadDropped || age < FinishedPayloadSeconds) continue;
				payloadBytes += (transfer.Capture?.LongLength ?? 0) + (transfer.DeltaCapture?.LongLength ?? 0)
					+ (transfer.NetworkStateBytes?.LongLength ?? 0) + (transfer.NetworkState?.Length ?? 0) * 2L
					+ (transfer.ClientStateBytes?.Sum(bytes => bytes?.LongLength ?? 0) ?? 0)
					+ transfer.ClientStates.Values.Sum(state => (state?.Length ?? 0) * 2L);
				transfer.Capture = null;
				transfer.DeltaCapture = null;
				transfer.Builders = null;
				transfer.ClientStateBytes = null;
				transfer.NetworkStateBytes = null;
				transfer.NetworkState = null;
				transfer.ClientStates.Clear();
				transfer.PayloadDropped = true;
				dropped++;
			}
			if (expired != null) foreach (Guid id in expired) table.Remove(id);
			removed += expired?.Count ?? 0;
		}
		if (dropped > 0 || removed > 0)
			Log("transfer_sweep payloadsDropped=" + dropped + " payloadMB=" + (payloadBytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)
				+ " recordsRemoved=" + removed + " source=" + m_sourceTransfers.Count + " target=" + m_targetTransfers.Count);
	}

	private static Transfer GetSource(Guid id, ulong partition, uint generation) => Get(m_sourceTransfers, id, partition, generation);
	private static Transfer GetTarget(Guid id, ulong partition, uint generation) => Get(m_targetTransfers, id, partition, generation);

	/// <summary>An inbound transfer that is underway but not yet committed or discarded. The
	/// stale-ownership purge consults this: closing "leftover" entities while the partition is
	/// mid-import destroys the returning incarnation on a fast ping-pong handover. Transfers are
	/// never pruned from the table, so the predicate is scoped to the uncommitted window rather
	/// than mere existence. Game-thread only, like every other m_targetTransfers access.</summary>
	/// <summary>Every transfer still in flight, for the stop record. GAME-THREAD ONLY, like
	/// every other m_targetTransfers/m_sourceTransfers access.
	///
	/// T-0006 and T-0028 both ended up asking the same question and neither could answer it:
	/// "what was this node in the middle of when it stopped?". The logs end when the process
	/// dies, so a force-kill leaves nothing. Live means the same thing here as in
	/// HasLiveInboundTransfer - not discarded and not yet committed.</summary>
	internal static string DescribeLiveTransfers()
	{
		List<string> parts = new List<string>();
		foreach (KeyValuePair<Guid, Transfer> entry in m_targetTransfers)
		{
			Transfer transfer = entry.Value;
			if (transfer.Discarded || transfer.CommittedGeneration != 0) continue;
			parts.Add("in:" + entry.Key.ToString("N").Substring(0, 8) + "/p" + transfer.Partition
				+ "/g" + transfer.Generation + "/members=" + transfer.Members.Count
				+ "/created=" + transfer.CreatedMembers.Count + "/clients=" + transfer.Clients.Length
				+ "/imported=" + transfer.ImportCount);
		}
		foreach (KeyValuePair<Guid, Transfer> entry in m_sourceTransfers)
		{
			Transfer transfer = entry.Value;
			if (transfer.Discarded || transfer.CommittedGeneration != 0) continue;
			parts.Add("out:" + entry.Key.ToString("N").Substring(0, 8) + "/p" + transfer.Partition
				+ "/g" + transfer.Generation + "/members=" + transfer.Members.Count
				+ "/clients=" + transfer.Clients.Length);
		}
		return parts.Count == 0 ? "none" : string.Join(", ", parts);
	}

	/// <summary>
	/// T-0179: is this client riding a handover INTO this node right now? True between
	/// /prepare-import recording the transfer and its commit or discard, which is the window the
	/// Gateway replays the client's CLIENT_CONNECTED packet in. The join toast is suppressed for
	/// exactly that window, so a traversal stops looking like a login.
	///
	/// Deliberately FAILS SAFE: no marker means "not a traversal", and the caller then does the
	/// vanilla thing (announce). Announcing a traversal is a cosmetic bug; swallowing a real
	/// login is a lie about who is in the world, so the two are not symmetric.
	///
	/// GAME-THREAD ONLY, like every other m_targetTransfers access.
	/// </summary>
	internal static bool IsArrivingByHandover(ulong clientId) =>
		m_targetTransfers.Values.Any(transfer => !transfer.Discarded && transfer.CommittedGeneration == 0
			&& Array.IndexOf(transfer.Clients, clientId) >= 0);

	/// <summary>
	/// T-0179: is this client riding a handover OUT of this node right now? True from Capture
	/// recording the transfer until the SOURCE side of it ends - which is ReleaseFinish or
	/// ThawSource, not a commit: a source transfer never receives CommittedGeneration (only the
	/// target's Commit sets that) and is never Discarded (target-side too), so those two tests
	/// alone stayed true until SweepFinishedTransfers dropped the record an hour later
	/// (FinishedRecordSeconds). For that hour a client that rode out under rotation and came back,
	/// or whose handover was thawed, had its REAL disconnect suppressed on this node and stayed in
	/// m_members for everyone still here - and the mirror cannot repair that one, because a player
	/// local to this node is in nobody's Mirrored set (T-0204).
	///
	/// Fails safe exactly as <see cref="IsArrivingByHandover"/> does.
	///
	/// GAME-THREAD ONLY, like every other m_sourceTransfers access.
	/// </summary>
	internal static bool IsLeavingByHandover(ulong clientId) =>
		m_sourceTransfers.Values.Any(transfer => !transfer.Discarded && !transfer.Released && !transfer.Thawed
			&& transfer.CommittedGeneration == 0 && Array.IndexOf(transfer.Clients, clientId) >= 0);

	internal static bool HasLiveInboundTransfer(ulong partitionId) =>
		m_targetTransfers.Values.Any(transfer => transfer.Partition == partitionId
			&& !transfer.Discarded && transfer.CommittedGeneration == 0);

	private static Transfer Get(Dictionary<Guid, Transfer> transfers, Guid id, ulong partition, uint generation)
	{
		if (!transfers.TryGetValue(id, out Transfer transfer))
		{
			throw new KeyNotFoundException("Unknown transfer " + id);
		}
		Validate(transfer, partition, generation);
		return transfer;
	}

	private static bool IsNotFound(Exception error) => error is KeyNotFoundException || error.InnerException != null && IsNotFound(error.InnerException);

	private static void Validate(Transfer transfer, ulong partition, uint generation)
	{
		if (transfer.Partition != partition || transfer.Generation != generation)
		{
			throw new InvalidOperationException("Transfer partition or generation does not match");
		}
	}

	private static int RemoveCapturedEntities(ulong partitionId)
	{
		HashSet<long> captured = m_partitions.Get(partitionId).Members.ToHashSet();
		MyEntity[] members = MyEntities.GetEntities()
			.Where(entity => captured.Contains(entity.EntityId) && !entity.MarkedForClose).ToArray();
		// T-0213: this runs on the SOURCE side of every handover release, so it is the highest-traffic
		// of the three membership-filtered closes. Closing a grid without its hierarchy ejects a
		// seated pilot into a live top-level character that outlives the release and is later adopted
		// as a duplicate - see PartitionPersistence.CloseWithHierarchy and FinishReconceal.
		PartitionPersistence.CloseWithHierarchy(members);
		// The count stays the number of CAPTURED entities closed, not the hierarchy children swept
		// with them: callers compare it against the partition's member count.
		return members.Length;
	}

	private static Guid? OptionalTransferId(HttpListenerContext context)
	{
		string value = context.Request.QueryString["transferId"];
		if (value == null) return null;
		return Guid.TryParse(value, out Guid id) && id != Guid.Empty ? id : throw new ArgumentException("Invalid transferId query parameter");
	}

	private static void RequireRegistryGrant(HttpListenerContext context, string path)
	{
		string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		bool handover = path.EndsWith("/prepare-import", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith("/import", StringComparison.OrdinalIgnoreCase);
		bool save = path.EndsWith("/save-file", StringComparison.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(gateway) || !handover && !save) return;
		string authorization = context.Request.Headers["Authorization"];
		if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
			throw new UnauthorizedAccessException("Registry transfer grant is required");
		string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
		if (string.IsNullOrWhiteSpace(node) || ClusterRuntime.RegistryNodeEpoch <= 0)
			throw new UnauthorizedAccessException("Receiver is not registered");
		Guid? transfer = handover ? OptionalTransferId(context) : null;
		if (handover && !transfer.HasValue) throw new UnauthorizedAccessException("Fenced handover grant is required");
		var validation = new
		{
			Token = authorization.Substring(7), Operation = handover ? "handover" : "save",
			TransferId = transfer?.ToString("D") ?? string.Empty,
			Partition = ulong.Parse(context.Request.QueryString["partition"] ?? throw new ArgumentException("Missing partition query parameter")),
			Generation = long.Parse(context.Request.QueryString["generation"] ?? throw new ArgumentException("Missing generation query parameter")),
			Sequence = save ? long.Parse(context.Request.QueryString["sequence"] ?? throw new ArgumentException("Missing sequence query parameter")) : 0,
			Receiver = node, ReceiverEpoch = ClusterRuntime.RegistryNodeEpoch,
			// T-0323: a proxied save fetch; the registry refuses it unless the grant binds this exact location.
			Location = save ? context.Request.QueryString["location"] : null
		};
		using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
		using StringContent content = new StringContent(JsonSerializer.Serialize(validation), System.Text.Encoding.UTF8, "application/json");
		using HttpResponseMessage response = client.PostAsync(gateway + "/registry/grants/validate", content).GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode) throw new UnauthorizedAccessException("Registry rejected transfer grant");
	}

	private static long QueryLong(HttpListenerContext context, string name)
	{
		if (!long.TryParse(context.Request.QueryString[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value == 0)
			throw new ArgumentException("Invalid " + name + " query parameter");
		return value;
	}

	/// <summary>The join tokens this node accepts from a peer: the same set it accepts on its node link.</summary>
	private static string[] JoinTokens() => new[]
	{
		Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_CURRENT"), Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN"),
		Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_PREVIOUS"),
	};

	/// <summary>
	/// T-0321: the per-request credential (ClusterNet.NodeControlAccess). A request on the loopback prefix is served as
	/// before; on the LAN prefix the peer routes take the join token and the rest the admin token. A refusal is
	/// answered here as JSON carrying the code and this node's identity, so a caller can tell a stale credential
	/// (credential_rejected: the token matches nothing this node knows) from a missing one or a scope mismatch.
	/// </summary>
	private static bool RequestAuthorized(HttpListenerContext context, string path)
	{
		bool loopback = context.Request.LocalEndPoint == null || IPAddress.IsLoopback(context.Request.LocalEndPoint.Address);
		ClusterProtocol.ClusterNet.ControlAccess access = ClusterProtocol.ClusterNet.NodeControlAccess(loopback, m_adminTokenHash != null,
			path, context.Request.HttpMethod);
		if (access is ClusterProtocol.ClusterNet.ControlAccess.Open or ClusterProtocol.ClusterNet.ControlAccess.Grant) return true;
		string token = ClusterProtocol.ClusterNet.Bearer(context.Request.Headers["Authorization"]);
		bool admin = Authorized(context);
		bool join = ClusterProtocol.ClusterNet.TokenMatches(token, JoinTokens());
		if (admin || access == ClusterProtocol.ClusterNet.ControlAccess.JoinOrAdmin && join) return true;
		string code = token == null ? ClusterProtocol.ClusterNet.CredentialRequired : join ? "admin_scope_required" : ClusterProtocol.ClusterNet.CredentialRejected;
		Log("control credential refused path=" + path + " code=" + code + " remote=" + context.Request.RemoteEndPoint
			+ " caller=" + (context.Request.Headers[ClusterProtocol.ClusterNet.NodeHeader] ?? "?")
			+ "/" + (context.Request.Headers[ClusterProtocol.ClusterNet.EpochHeader] ?? "?")
			+ "@" + (context.Request.Headers[ClusterProtocol.ClusterNet.HostHeader] ?? "?")
			+ " pid=" + (context.Request.Headers[ClusterProtocol.ClusterNet.PidHeader] ?? "?")
			+ " token_fp=" + ClusterProtocol.ClusterNet.Fingerprint(token));
		context.Response.Headers["WWW-Authenticate"] = "Bearer";
		Write(context, token != null && join ? 403 : 401, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
		{
			error = "cluster control credential refused", code,
			node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"), epoch = ClusterRuntime.RegistryNodeEpoch,
		})));
		return false;
	}

	/// <summary>T-0321 P6: the served half of the per-channel path counters.</summary>
	private static void CountServed(HttpListenerContext context, long started)
	{
		try
		{
			bool fromNode = context.Request.Headers[ClusterProtocol.ClusterNet.NodeHeader] != null;
			long bytes = Math.Max(0, context.Request.ContentLength64) + Math.Max(0, context.Response.ContentLength64);
			ClusterProtocol.TransportCounters.Count(
				ClusterProtocol.ClusterNet.ChannelOf(false, fromNode, context.Request.Url.AbsolutePath, context.Request.HttpMethod), incoming: true,
				ClusterProtocol.TransportCounters.PathTo(context.Request.RemoteEndPoint?.Address.ToString()), bytes, Stopwatch.GetElapsedTime(started));
		}
		catch (Exception) { }
	}

	private static bool Authorized(HttpListenerContext context)
	{
		string value = context.Request.Headers["Authorization"] ?? string.Empty;
		const string prefix = "Bearer ";
		if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
		byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(value.Substring(prefix.Length)));
		return CryptographicOperations.FixedTimeEquals(supplied, m_adminTokenHash);
	}

	private static Guid RequiredTransferId(HttpListenerContext context) => OptionalTransferId(context)
		?? throw new ArgumentException("Missing transferId query parameter");

	private static ulong? OptionalPartition(HttpListenerContext context, Guid? transferId)
	{
		string value = context.Request.QueryString["partition"];
		if (value == null) return transferId.HasValue ? throw new ArgumentException("Missing partition query parameter") : null;
		return ulong.Parse(value);
	}

	private static ulong RequiredPartition(HttpListenerContext context) => OptionalPartition(context, Guid.Empty).Value;

	private static uint? OptionalGeneration(HttpListenerContext context, Guid? transferId)
	{
		string value = context.Request.QueryString["generation"];
		if (value == null) return transferId.HasValue ? throw new ArgumentException("Missing generation query parameter") : null;
		return uint.Parse(value);
	}

	private static uint RequiredGeneration(HttpListenerContext context) => OptionalGeneration(context, Guid.Empty).Value;

	private sealed class Transfer
	{
		public readonly Guid Id;
		public readonly ulong Partition;
		public readonly uint Generation;
		/// <summary>Every client that rides this partition across the transfer; empty when clientless.</summary>
		public ulong[] Clients = new ulong[0];
		/// <summary>Client id -> the replication view exported for it at capture, keyed for re-adoption.</summary>
		public Dictionary<ulong, string> ClientStates = new Dictionary<ulong, string>();
		/// <summary>Clients whose view was pruned on the source and must be resumed or finished.</summary>
		public HashSet<ulong> PrunedClients = new HashSet<ulong>();
		public byte[] Capture;
		/// <summary>
		/// T-0051: pilot ids whose ARRIVING seated copy this node does not seat, because the same character
		/// is already seated here in a different cockpit that is the live one. MyCockpit.Init drops such a
		/// pilot (the character is "using" another controller); the round-trip gate strips the pilot fields
		/// on both sides for these ids instead of aborting the handover on the drop.
		/// </summary>
		public readonly HashSet<long> DisplacedPilots = new HashSet<long>();
		/// <summary>HandoverDelta: the frozen builders and the framing inputs, for the delta capture.</summary>
		public MyObjectBuilder_EntityBase[] Builders;
		public byte[][] ClientStateBytes;
		public byte[] NetworkStateBytes;
		public byte[] DeltaCapture;
		public BoundingSphereD Sphere;
		public bool Anchored;
		public HashSet<long> Members = new HashSet<long>();
		public HashSet<long> Descendants = new HashSet<long>();
		public string NetworkState;
		/// <summary>T-0256: source - the grids the plugins were told are leaving; target - their states until commit.</summary>
		public HashSet<long> PluginGrids;
		/// <summary>T-0401 fix: imported entities Added with physics deferred, and per reason those left as before.</summary>
		/// <summary>T-0401: entities evaluated by DeferPhysics (import.deferPhysics was on when they were imported).</summary>
		public int PhysicsDeferEvaluated;
		public int PhysicsDeferred;
		public int PhysicsDeferredChildren;
		public readonly Dictionary<string, int> PhysicsNotDeferred = new Dictionary<string, int>();
		public string PluginStates;
		public HashSet<long> CreatedMembers = new HashSet<long>();
		/// <summary>
		/// T-0296 r3, target: the live client-controlled entities the import KEPT (not created, not frozen), with the
		/// local partition that held each before the import's Adopt moved it into the arriving one (0 for none).
		/// A discard hands them back; left in the discarded, unowned layout they are closed by the fenced-orphan purge.
		/// </summary>
		public Dictionary<long, ulong> KeptLive = new Dictionary<long, ulong>();
		public Dictionary<long, FrozenState> Frozen = new Dictionary<long, FrozenState>();
		public ulong BodyChecksum;
		public int ImportCount;
		/// <summary>Client id -> replicables adopted for it, one entry per client that has adopted.</summary>
		public Dictionary<ulong, int> AdoptCounts = new Dictionary<ulong, int>();
		/// <summary>Every client this transfer carries has been adopted on the target.</summary>
		public bool Adopted => Clients.Length != 0 && Clients.All(client => AdoptCounts.ContainsKey(client));
		public int ReleaseCount;
		public int DiscardCount;
		public uint CommittedGeneration;
		/// <summary>SetGeneration + Adopt ran for this commit; a retried commit must not repeat them (T-0150).</summary>
		public bool LocallyAdopted;
		public bool Prepared;
		public bool Clientless;
		public bool Imported;
		public bool StateVerified;
		public bool Released;
		public bool Discarded;
		public bool Thawed;
		/// <summary>import.entitiesPerFrame: the import continuation between frames.</summary>
		public ImportContext Import;
		/// <summary>release.closePerFrame: a chunked release is in progress on the handler thread.</summary>
		public bool Releasing;
		/// <summary>T-0147: Stopwatch timestamp of the terminal step (release, thaw, commit or discard); 0 while live.</summary>
		public long FinishedTimestamp;
		/// <summary>T-0147: the capture, builders and exported states were dropped by <see cref="SweepFinishedTransfers"/>.</summary>
		public bool PayloadDropped;

		public Transfer(Guid id, ulong partition, uint generation)
		{
			Id = id;
			Partition = partition;
			Generation = generation;
		}
	}

	private readonly record struct FrozenState(MyEntityUpdateEnum NeedsUpdate, bool HadPhysics, bool PhysicsEnabled, Vector3 LinearVelocity, Vector3 AngularVelocity);

	/// <summary>Invoke with a HandoverPhaseTimer around the body: the game-thread hold of one phase.</summary>
	private static T InvokePhase<T>(string phase, Guid? transferId, ulong partition, Func<T> action)
	{
		return Invoke(() =>
		{
			using HandoverPhaseTimer timer = HandoverPhaseTimer.Phase(phase, transferId, partition);
			try { return action(); }
			catch (Exception e) { timer.Error = (e is InvalidOperationException && e.InnerException != null ? e.InnerException : e).GetType().Name; throw; }
		});
	}

	private static T Invoke<T>(Func<T> action)
	{
		if (MySandboxGame.Static == null)
		{
			throw new InvalidOperationException("Game is not running");
		}

		using ManualResetEventSlim done = new ManualResetEventSlim();
		T result = default;
		Exception error = null;
		// T-0336: a dormant spare runs 4 frames a second, so a request with several game-thread calls took
		// seconds on the one listener thread and every other request queued behind it. While a call is
		// pending the loop runs at its normal rate (WaitForTargetFrameRatePatch); dormancy itself stays.
		Interlocked.Increment(ref WaitForTargetFrameRateSeams.PendingControlCalls);
		try
		{
			MySandboxGame.Static.Invoke(delegate
			{
				try
				{
					result = action();
				}
				catch (Exception e)
				{
					error = e;
				}
				finally
				{
					done.Set();
				}
			}, "ClusterPlugin control request");
			if (!done.Wait(TimeSpan.FromSeconds(30)))
			{
				throw new TimeoutException("Timed out waiting for the game thread");
			}
		}
		finally
		{
			Interlocked.Decrement(ref WaitForTargetFrameRateSeams.PendingControlCalls);
		}
		if (error != null)
		{
			// Name the real failure in the FIRST line: the gateway logs DirectGateway's
			// FirstLine(body), and e.ToString() puts the inner exception on line 2, so six
			// aborted handovers on w22 2026-08-21 07:12 (partitions 486 and 513) recorded
			// nothing but "Game-thread cluster operation failed" and the node-side detail
			// was lost with the rotated stdout. The inner exception stays attached.
			throw new InvalidOperationException(
				"Game-thread cluster operation failed: " + error.GetType().Name + ": " + error.Message, error);
		}
		return result;
	}

	private static void Log(string message)
	{
		// The control listener binds before MyLog.Default exists; those lines (e.g. T-0324's TCP_NODELAY result)
		// were dropped silently. Fall back to stdout, which the launcher captures in <node>.log.
		string line = "[ClusterPlugin] " + SecretRedactor.Redact(message);
		if (MyLog.Default != null) MyLog.Default.WriteLineAndConsole(line);
		else Console.WriteLine(line);
	}

	private static void WriteText(HttpListenerContext context, int status, string text)
	{
		Write(context, status, "text/plain", System.Text.Encoding.UTF8.GetBytes(text));
	}

	private static void Write(HttpListenerContext context, int status, string contentType, byte[] body)
	{
		context.Response.StatusCode = status;
		context.Response.ContentType = contentType;
		context.Response.ContentLength64 = body.Length;
		context.Response.OutputStream.Write(body, 0, body.Length);
		context.Response.Close();
	}

	/// <summary>T-0265 test trigger body (game thread): see the /test/character-item route.</summary>
	private static object TestCharacterItem(ulong client, string subtype, int amount)
	{
		MyPlayer player = MySession.Static.Players.GetPlayerById(new MyPlayer.PlayerId(client, 0))
			?? throw new InvalidOperationException("No player " + client + " on this node");
		MyCharacter character = player.Character ?? throw new InvalidOperationException("Player " + client + " has no character here");
		MyInventory inventory = character.GetInventory() ?? throw new InvalidOperationException("Character has no inventory");
		var id = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalGunObject), subtype);
		if (amount > 0)
		{
			var item = (MyObjectBuilder_PhysicalGunObject)MyObjectBuilderSerializerKeen.CreateNewObject(id);
			if (!inventory.CanItemsBeAdded(1, id)) throw new InvalidOperationException("Inventory cannot take " + subtype);
			inventory.AddItems(1, item);
		}
		else
		{
			var held = inventory.GetItems().FirstOrDefault(x => x.Content.GetId() == id);
			if (held.Content == null) throw new InvalidOperationException("No " + subtype + " in the inventory");
			inventory.RemoveItems(held.ItemId, 1);
		}
		string[] items = inventory.GetItems().Select(x => x.Content.GetId().SubtypeName + "x" + x.Amount).ToArray();
		Console.WriteLine($"[ClusterPlugin] test character_item client={client} subtype={subtype} amount={amount} items=[{string.Join(",", items)}]");
		return new { client, subtype, amount, character = character.EntityId, items };
	}
}
