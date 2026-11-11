using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Utils;

namespace ClusterPlugin;

internal sealed class WorldAuthorityPersistence
{
	private readonly BlockingCollection<PendingSave> m_saves = new BlockingCollection<PendingSave>(1);
	private readonly string m_root;
	private readonly string m_gateway;
	private readonly string m_node;
	private readonly string m_controlEndpoint;
	private readonly object m_sync = new object();
	private DateTime m_nextCapture;
	private uint m_sequence;
	private PendingSave m_active;
	private volatile bool m_shutdownRequested;
	private volatile bool m_exitRequested;
	private volatile bool m_saveSoon;
	//: The World Authority holds the voxel journal replication target, so it must OUTLIVE the
	//: regular nodes that replicate into it. It used to exit the moment its own global Final
	//: landed, which is early: the WA owns no partitions and drains in a second, while regular
	//: nodes are still writing drain finals. Measured on both of w29's deploy stops (T-0048):
	//:
	//:   stop 1, 00:00      wa-1 Final 23:58:08, exit 69 ms later; drain records: node-2 only
	//:                      voxel_journal sync_failed n1..n5 = 470 / 0 / 470 / 469 / 101
	//:   stop 2, 07:04      same shape, node-5 the only one to drain; 460/460/460/460/41
	//:
	//: Those counts are each node retrying a wa-1 that was already dead. node-1 kept trying for
	//: two minutes. Deterministic in structure; only WHICH node escapes is a race.
	//:
	//: CONSTRAINT ON THE GRACE BUDGET, and it defeats this fix if ignored: `boot stop` waits its
	//: own --grace and then SIGKILLs everything (Cli/cluster.py stop()). That default is 30 s.
	//: If CLUSTER_WA_PEER_DRAIN_GRACE exceeds the stop's grace, the World Authority is killed
	//: while it is still waiting and this change does nothing - no worse than before, but not
	//: better. The observed deploy stops used --grace 120, which accommodates the 90 s default
	//: here. Real drain time has been measured at ~2 min (node-1 retried until 07:06:35 after a
	//: 07:04:38 kill), so a stop that genuinely wants clean drains needs --grace 180 with
	//: CLUSTER_WA_PEER_DRAIN_GRACE=120 or similar. Raising one without the other buys nothing.
	//:
	//: So hold the exit until the registry reports no live regular node, bounded by a deadline.
	//: FAIL OPEN in every uncertain case - a stop that hangs is worse than one that races - so a
	//: registry error, an unparseable answer or the deadline all exit immediately.
	private DateTime m_exitDeadline;
	private Thread m_peerWatch;
	private volatile bool m_peersDrained;
	private static readonly TimeSpan PeerDrainGrace =
		TimeSpan.FromSeconds(EnvSeconds("CLUSTER_WA_PEER_DRAIN_GRACE", 90));
	//: Short on purpose. The poll runs off the game thread, but a long timeout would keep the
	//: process alive past its usefulness if the gateway itself is going down at the same time.
	private static readonly HttpClient PeerTransport =
		ClusterHttp.Client(TimeSpan.FromSeconds(10));

	private static double EnvSeconds(string name, double fallback)
		=> double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float,
			CultureInfo.InvariantCulture, out double value) && value >= 0 ? value : fallback;

	public WorldAuthorityPersistence()
	{
		string data = Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT");
		string world = Environment.GetEnvironmentVariable("CLUSTER_WORLD");
		m_root = !string.IsNullOrWhiteSpace(data) ? Path.Combine(Path.GetFullPath(data), "cluster")
			: !string.IsNullOrWhiteSpace(world) ? Path.Combine(Path.GetFullPath(world), "cluster") : null;
		m_gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		m_node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
		// T-0321: the same advertised endpoint the node registers (ClusterRuntime.AdvertisedControlEndpoint).
		m_controlEndpoint = int.TryParse(Environment.GetEnvironmentVariable("CLUSTER_CONTROL_PORT"), out int port)
			? ClusterProtocol.ClusterNet.AdvertisedControlEndpoint(Environment.GetEnvironmentVariable("CLUSTER_NODE_CONTROL_ENDPOINT"),
				Environment.GetEnvironmentVariable("CLUSTER_CONTROL_BIND"), port)
			: Environment.GetEnvironmentVariable("CLUSTER_NODE_CONTROL_ENDPOINT");
		if (m_root != null)
			new Thread(WriteLoop) { IsBackground = true, Name = "ClusterGlobalSave" }.Start();
	}

	public void Update()
	{
		if (m_exitRequested)
		{
			if (m_exitDeadline == default)
			{
				m_exitDeadline = DateTime.UtcNow + PeerDrainGrace;
				StartPeerWatch();
			}
			// Never block the game thread here: the WA has to keep SERVING the peers it is
			// waiting for. The watch thread sets the flag; this only reads it.
			if (DateTime.UtcNow < m_exitDeadline && !m_peersDrained) return;
			m_exitRequested = false;
			string why = DateTime.UtcNow >= m_exitDeadline
				? $"peer drain grace {PeerDrainGrace.TotalSeconds:F0}s expired"
				: "no live regular nodes remain";
			MyLog.Default?.WriteLineAndConsole(
				"[ClusterPlugin] global final registered; World Authority Empty (" + why + ")");
			ClusterRuntime.ExitProcess(0, "World Authority Empty");
			return;
		}
		if (!Enabled) return;
		lock (m_sync)
			if (m_active != null) return;
		if (m_shutdownRequested)
		{
			QueueCapture("Final");
			return;
		}
		if (DateTime.UtcNow < m_nextCapture && !m_saveSoon) return;
		// Cleared before the capture: a request made while it runs is for a change it may not hold (#1270).
		bool soon = m_saveSoon;
		m_saveSoon = false;
		if (QueueCapture("Periodic")) m_nextCapture = DateTime.UtcNow.AddSeconds(Math.Max(1, ClusterConfig.GetInt("wa.globalSaveSeconds")));
		else if (soon) m_saveSoon = true;
	}

	public bool QueueTestCapture() => Enabled && !m_shutdownRequested && QueueCapture("Periodic");

	/// <summary>
	/// The next periodic save as soon as no save is in flight: for a change whose loss in a World Authority crash
	/// would not stay a loss, like a stored ship whose grid is already gone (SE1-0233).
	/// </summary>
	public void SaveSoon() => m_saveSoon = true;

	/// <summary>
	/// This World Authority writes and registers global saves, so a command's answer can wait for the save that holds
	/// its result (#1270). Without a data root or a Gateway nothing would ever release it.
	/// </summary>
	public bool Persists => m_root != null && !string.IsNullOrWhiteSpace(m_gateway) && !string.IsNullOrWhiteSpace(m_node);

	public void RequestShutdown() => m_shutdownRequested = true;

	/// <summary>
	/// Poll the registry off the game thread until no live REGULAR node remains, then release the
	/// exit. FAILS OPEN in every uncertain case - no gateway, a registry error, an unparseable
	/// answer - because a stop that hangs is worse than one that races, and the deadline in
	/// Update() bounds it regardless of what this thread does.
	/// </summary>
	private void StartPeerWatch()
	{
		if (m_peerWatch != null) return;
		if (string.IsNullOrWhiteSpace(m_gateway) || string.IsNullOrWhiteSpace(m_node))
		{
			m_peersDrained = true;
			return;
		}
		ClusterRuntime.AuthorizeRegistryClient(PeerTransport);
		m_peerWatch = new Thread(() =>
		{
			int waited = 0;
			int refused = 0;
			while (DateTime.UtcNow < m_exitDeadline)
			{
				bool live;
				try
				{
					live = AnyLiveRegularNode();
					refused = 0;
				}
				catch (HttpRequestException error) when (error.InnerException is SocketException
					{ SocketErrorCode: SocketError.ConnectionRefused } && ++refused >= 3)
				{
					// T-0162: nothing listens on the registry port any more. The Gateway leaves only
					// after the graceful shutdown saw every node Empty or Dead, and a regular node
					// cannot register a final without it, so there is nothing left to wait for. A
					// busy or slow Gateway (the T-0048 case below) times out instead of refusing.
					MyLog.Default?.WriteLineAndConsole(
						$"[ClusterPlugin] registry refused {refused} peer drain checks in a row; the Gateway is gone, World Authority may exit");
					break;
				}
				catch (Exception error)
				{
					// A failed check is not "no peers left": at the 08:50 stop the gateway was inside
					// its save-catalogue GC, the 3 s request timed out and the WA left 60 s into a
					// drain four nodes were still running (T-0048). Keep holding to the deadline.
					if (waited % 10 == 0)
						MyLog.Default?.WriteLineAndConsole(
							"[ClusterPlugin] peer drain check failed, still holding the World Authority exit: "
							+ error.Message);
					Thread.Sleep(1000);
					waited++;
					continue;
				}
				if (!live)
				{
					MyLog.Default?.WriteLineAndConsole(
						$"[ClusterPlugin] all regular nodes drained after {waited}s; World Authority may exit");
					break;
				}
				Thread.Sleep(1000);
				waited++;
			}
			m_peersDrained = true;
		})
		{ IsBackground = true, Name = "ClusterWaPeerDrainWatch" };
		m_peerWatch.Start();
		MyLog.Default?.WriteLineAndConsole(
			$"[ClusterPlugin] World Authority final registered; holding exit up to "
			+ $"{PeerDrainGrace.TotalSeconds:F0}s for regular nodes to drain (T-0048)");
	}

	/// <summary>True while the registry lists a live node that is not us and not a World Authority.</summary>
	private bool AnyLiveRegularNode()
	{
		using JsonDocument nodes = ClusterRuntime.Get(PeerTransport, m_gateway + "/registry/nodes");
		JsonElement root = nodes?.RootElement ?? default;
		if (root.ValueKind != JsonValueKind.Array) return false;
		foreach (JsonElement node in root.EnumerateArray())
		{
			// T-0342: the rows carry Node and State. This read NodeId and Alive, which do not exist, so every regular
			// row counted as live and the World Authority never saw its peers drained (0 times in every log).
			if (node.TryGetProperty("Node", out JsonElement id)
				&& string.Equals(id.GetString(), m_node, StringComparison.Ordinal)) continue;
			// Role comes back as either the string "WA" or the number 1, depending on the
			// registry build - ClusterRuntime.Register handles both and so must this. Missing
			// the string form would count a World Authority as a regular node and hold the exit
			// to the deadline: harmless but wrong, and it would look like the fix not working.
			if (node.TryGetProperty("Role", out JsonElement role)
				&& (role.ValueKind == JsonValueKind.String
						? string.Equals(role.GetString(), "WA", StringComparison.Ordinal)
						: role.ValueKind == JsonValueKind.Number && role.GetInt32() == 1)) continue;
			// Live = Active (0) or draining (Closed, 1); Dead and Empty rows are processes that are gone.
			if (node.TryGetProperty("State", out JsonElement state)
				&& (state.ValueKind == JsonValueKind.Number ? state.GetInt32() > 1
					: state.ValueKind == JsonValueKind.String && state.GetString() is "Dead" or "Empty")) continue;
			return true;
		}
		return false;
	}

	private bool Enabled => ClusterRuntime.IsWorldAuthority && m_root != null && !string.IsNullOrWhiteSpace(m_gateway)
		&& !string.IsNullOrWhiteSpace(m_node) && ClusterRuntime.RegistryNodeEpoch > 0 && ClusterRuntime.AuthorityGeneration > 0;

	private bool QueueCapture(string kind)
	{
		lock (m_sync)
			if (m_active != null) return false;
		MySession session = MySession.Static;
		if (session?.Ready != true) return false;
		if (!PromotionService.Ready) return false;
		string shipStorage = null, adminSafeZones = null, contracts = null, promotions = null;
		long ledgerOrder;
		if (!GlobalCommandLedger.TryCapture(() =>
		{
			MyObjectBuilder_Checkpoint captured = session.GetCheckpoint(session.Name, isClientRequest: false);
			MyPlayerCollectionPatch.AddBindingsToCheckpoint(captured);
			// With the ledger: a command the ledger does not know yet must not be in the record either.
			shipStorage = ShipStorageService.PersistedRecord();
			adminSafeZones = AdminSafeZones.PersistedRecord();
			contracts = ContractLedger.PersistedRecord();
			promotions = PromotionService.PersistedRecord();
			return captured;
		}, out MyObjectBuilder_Checkpoint checkpoint, out string commandLedger, out ledgerOrder)) return false;
		PendingSave save;
		lock (m_sync)
		{
			if (m_active != null) return false;
			save = new PendingSave(checked((uint)ClusterRuntime.AuthorityGeneration), ++m_sequence, kind, checkpoint, commandLedger,
				ClusterRuntime.CatalogClusterMilliseconds(), CharacterRegistryService.PersistedRecord(),
				TimeOfDayService.PersistedRecord(), shipStorage, adminSafeZones, contracts, OfflineRateCache.PersistedRecord(), promotions) { LedgerOrder = ledgerOrder };
			m_active = save;
		}
		m_saves.Add(save);
		return true;
	}

	private void WriteLoop()
	{
		foreach (PendingSave save in m_saves.GetConsumingEnumerable())
		{
			while (true)
			{
				try
				{
					if (!save.Prepared) Prepare(save);
					ClusterSeams.BeforeGlobalSaveRegister?.Invoke(save.Generation, save.Sequence);
					Register(save, save.Parsed, save.Path);
					// Persist before answer (#1270): the commands this save holds may now be answered.
					if (GlobalStateChannel.HoldsResponsesThrough(save.LedgerOrder) && Recoverable(save))
						GlobalCommandLedger.MarkDurable(save.LedgerOrder);
					if (save.Kind == "Final") MarkEmpty();
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] global save registered: generation " + save.Generation
						+ " sequence " + save.Sequence + " kind " + save.Kind);
					break;
				}
				catch (Exception exception)
				{
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: global save failed; retry=true error=" + SecretRedactor.Redact(exception.Message));
					Thread.Sleep(1000);
				}
			}
			save.Completed.Set();
			lock (m_sync) m_active = null;
			if (save.Kind == "Final") m_exitRequested = true;
		}
	}

	private void Prepare(PendingSave save)
	{
		save.File = GlobalSaveFile.Write(save.Checkpoint, save.Generation, save.Sequence,
			save.Kind == "Final" ? (byte)2 : (byte)0, save.ClusterTime, save.CommandLedger, save.CharacterRecord,
			save.TimeOfDayRecord, save.ShipStorageRecord, save.AdminSafeZonesRecord, save.ContractRecord, save.OfflineRatesRecord, save.PromotionRecord);
		// T-0340: the gateway host's save store when the registry said so at registration, else m_root/saves as always.
		save.Path = ClusterProtocol.HostSaveStore.GlobalSave(ClusterRuntime.HostStore ? ClusterRuntime.SaveRoot() : Path.Combine(m_root, "saves"),
			save.Generation, save.Sequence);
		PartitionSaveFile.WriteFile(save.Path, save.File, storeOwner: ClusterRuntime.HostStore ? m_node : null);
		save.Parsed = PartitionSaveFile.ReadFile(save.Path);
		GlobalSaveFile.Read(File.ReadAllBytes(save.Path), save.Generation, save.Sequence);
		AppendManifest(save, save.Parsed);
		save.Prepared = true;
	}

	private void AppendManifest(PendingSave save, PartitionSaveFile.Parsed parsed)
	{
		string manifest = JsonSerializer.Serialize(new { op = "write", key = "GLOBAL", gen = save.Generation,
			seq = save.Sequence, kind = save.Kind, size = save.File.Length, xxh3 = parsed.BodyChecksum.ToString("X16"),
			clusterTime = parsed.ClusterTime, wallTime = DateTimeOffset.UtcNow }) + Environment.NewLine;
		Directory.CreateDirectory(m_root);
		using FileStream stream = new FileStream(Path.Combine(m_root, "manifest.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
		using StreamWriter writer = new StreamWriter(stream);
		writer.Write(manifest);
		writer.Flush();
		stream.Flush(true);
	}

	private void Register(PendingSave save, PartitionSaveFile.Parsed parsed, string path)
	{
		using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
		ClusterRuntime.AuthorizeRegistryClient(client);
		string url = m_gateway + "/registry/global-saves?node=" + Uri.EscapeDataString(m_node)
			+ "&epoch=" + ClusterRuntime.RegistryNodeEpoch.ToString(CultureInfo.InvariantCulture)
			+ "&generation=" + save.Generation.ToString(CultureInfo.InvariantCulture)
			+ "&sequence=" + save.Sequence.ToString(CultureInfo.InvariantCulture) + "&kind=" + save.Kind
			+ "&size=" + save.File.Length.ToString(CultureInfo.InvariantCulture) + "&checksum=" + parsed.BodyChecksum.ToString("X16")
			+ "&clusterTime=" + parsed.ClusterTime.ToString(CultureInfo.InvariantCulture)
			+ "&location=" + Uri.EscapeDataString(path) + "&control=" + Uri.EscapeDataString(m_controlEndpoint ?? string.Empty);
		using HttpResponseMessage response = client.PostAsync(url, null).GetAwaiter().GetResult();
		if (response.IsSuccessStatusCode) return;
		// SE-843: the answer says why. It is all an operator gets while WriteLoop retries this save.
		string answer = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
		throw new InvalidOperationException((response.StatusCode == HttpStatusCode.Conflict
			? "Global save registration was fenced: " : $"Global save registration answered {(int)response.StatusCode}: ") + answer);
	}

	/// <summary>
	/// #1270: a registered save is not yet one a replacement restores. Recovery skips a save whose only holders are dead
	/// (GatewayRegistry.NewestGlobalSaveForRecovery), and until a regular node has replicated it - typically 0.2-1.2 s
	/// on the bench - this World Authority is its only holder. So ask the registry what it would restore without us,
	/// until that is this save or a newer one. Bounded: false after the deadline, the answers stay held and the next
	/// save tries again. A registry before #1270 ignores ?without= and answers at once, as registration alone did.
	/// </summary>
	private bool Recoverable(PendingSave save)
	{
		DateTime deadline = DateTime.UtcNow + RecoverableWait;
		using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(5));
		ClusterRuntime.AuthorizeRegistryClient(client);
		string url = m_gateway + "/registry/global-saves/recovery?without=" + Uri.EscapeDataString(m_node);
		while (true)
		{
			try
			{
				using JsonDocument answer = ClusterRuntime.Get(client, url);
				JsonElement id = answer.RootElement.GetProperty("SaveId");
				long generation = id.GetProperty("Generation").GetInt64(), sequence = id.GetProperty("Sequence").GetInt64();
				if (generation > save.Generation || generation == save.Generation && sequence >= save.Sequence) return true;
			}
			catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException
				or InvalidOperationException or TaskCanceledException)
			{
				// No catalogued save for recovery yet, or the registry is busy: keep asking to the deadline.
			}
			if (DateTime.UtcNow >= deadline)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] global save " + save.Generation + "/" + save.Sequence
					+ " has no holder besides this World Authority after " + RecoverableWait.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)
					+ " s; its command answers stay held (#1270)");
				return false;
			}
			Thread.Sleep(200);
		}
	}

	private static readonly TimeSpan RecoverableWait = TimeSpan.FromSeconds(8);

	private void MarkEmpty()
	{
		using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
		ClusterRuntime.AuthorizeRegistryClient(client);
		string url = m_gateway + "/registry/nodes/empty?node=" + Uri.EscapeDataString(m_node)
			+ "&epoch=" + ClusterRuntime.RegistryNodeEpoch.ToString(CultureInfo.InvariantCulture);
		using HttpResponseMessage response = client.PostAsync(url, null).GetAwaiter().GetResult();
		response.EnsureSuccessStatusCode();
	}

	private sealed record PendingSave(uint Generation, uint Sequence, string Kind, MyObjectBuilder_Checkpoint Checkpoint,
		string CommandLedger, long ClusterTime, string CharacterRecord = null, string TimeOfDayRecord = null,
		string ShipStorageRecord = null, string AdminSafeZonesRecord = null, string ContractRecord = null,
		string OfflineRatesRecord = null, string PromotionRecord = null)
	{
		public byte[] File { get; set; }
		public string Path { get; set; }
		public PartitionSaveFile.Parsed Parsed { get; set; }
		public bool Prepared { get; set; }
		public long LedgerOrder { get; init; }
		public ManualResetEventSlim Completed { get; } = new ManualResetEventSlim();
	}
}
