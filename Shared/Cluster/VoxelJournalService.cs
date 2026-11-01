using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Sandbox.Game.Entities;
using Sandbox.Engine.Voxels;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Voxels;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRageMath;

namespace ClusterPlugin;

internal sealed class VoxelJournalService
{
	private readonly object m_lock = new();
	private readonly Dictionary<string, SortedDictionary<long, VoxelJournalCodec.Entry>> m_entries = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, long> m_applied = new(StringComparer.Ordinal);
	private readonly HashSet<Guid> m_operations = new();
	/// <summary>
	/// #1404: bumped under <see cref="m_lock"/> whenever <see cref="m_entries"/> changes, so a replica asking for the
	/// tails with the version it last saw gets a 304 instead of the table. The process token keeps a restarted World
	/// Authority's counter, which starts again at zero, from matching a version an earlier process handed out.
	/// </summary>
	private long m_tailsVersion;
	private static readonly string s_tailsProcess = Guid.NewGuid().ToString("N").Substring(0, 12);
	/// <summary>Durable outbound queue on a regular node; null on the World Authority, which appends directly.</summary>
	private readonly VoxelOutbox m_outbox;
	/// <summary>
	/// Append-only log of every operation id the World Authority has ever accepted; null on regular nodes.
	/// Journal files also carry their ids, but truncation deletes those files, and the dedupe must outlive them: a
	/// node crashing between a successful append and its outbox acknowledgement re-sends the entry, and without a
	/// retained id a truncated ancient edit would re-append under a fresh tail sequence - an absolute write of old
	/// content over newer, which is exactly the divergence class the journal exists to prevent.
	/// </summary>
	private readonly FileStream m_operationLog;
	private readonly ConcurrentQueue<VoxelJournalCodec.Entry> m_inbound = new();
	/// <summary>
	/// T-0364: storages whose entries this node skipped because it had not generated them. A stock procedural
	/// asteroid exists only on the nodes that generated it, so a replica skips its entries - and when the asteroid
	/// is generated here later it is pristine. The watermark had advanced, so nothing was ever fetched again and
	/// the damage "healed" for whoever this node served (play-test 2026-10-01, Asteroid_4_-3_3_16_1738998166).
	/// </summary>
	private readonly ConcurrentDictionary<string, byte> m_skippedStorages = new(StringComparer.Ordinal);
	/// <summary>
	/// The voxel instance each storage's watermark was applied to; another instance is a regenerated (pristine)
	/// storage. By reference, not entity id: a regenerated procedural asteroid has the same id.
	/// </summary>
	private readonly ConcurrentDictionary<string, WeakReference<MyVoxelBase>> m_appliedInstance = new(StringComparer.Ordinal);
	/// <summary>Storages being replayed from the World Authority after they appeared or were regenerated here.</summary>
	private readonly ConcurrentDictionary<string, byte> m_replaying = new(StringComparer.Ordinal);
	private long m_storageReplays;
	private int m_replayScanTick;
	/// <summary>True while the head outbox entry is failing to POST; it retries in place to preserve order.</summary>
	private volatile bool m_headRetrying;
	/// <summary>
	/// Operation ids this node produced from its own live storage this session. When such an entry is fetched back
	/// from the journal it must not be re-written: the drill has moved on, and stamping the captured snapshot over
	/// live storage reverts cells the drill already finished with, which is exactly how the owning node diverges
	/// from its replicas. The set is empty after a restart, so a genuine rebuild from a base still replays them.
	/// </summary>
	private readonly ConcurrentDictionary<Guid, (long Watermark, string Storage)> m_liveSelfEdits = new();

	/// <summary>
	/// Foreign Range entries applied while own live edits were still in flight, per storage. When a
	/// self entry returns, an interleaved foreign write overlapping its box means the LOCAL apply
	/// (done at capture time) ran out of total order - the returning snapshot must be re-applied to
	/// restore the World Authority's order. Bounded; pruned as self edits resolve.
	/// </summary>
	private readonly Dictionary<string, List<(long Sequence, VRageMath.Vector3I Min, VRageMath.Vector3I Max)>> m_foreignApplied = new(StringComparer.Ordinal);
	private readonly Dictionary<string, List<DirtyRange>> m_dirty = new(StringComparer.Ordinal);
	/// <summary>Ticks a dirty range may wait for two matching reads before it is published anyway.</summary>
	private const int MaxDirtyTicks = 8;
	/// <summary>Voxel count at which a dirty range is published without waiting, and the cap on merging two.</summary>
	private const int MaxDirtyVoxels = 262144;
	/// <summary>Disjoint dirty ranges tracked per storage before the oldest is forced out.</summary>
	private const int MaxDirtyRanges = 32;
	private readonly ConcurrentDictionary<string, byte> m_storages = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, BaseState> m_bases = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, byte> m_pendingWatermarks = new(StringComparer.Ordinal);
	private readonly HttpClient m_http = ClusterHttp.Client(TimeSpan.FromSeconds(10));
	private readonly string m_gateway;
	private readonly string m_root;
	private readonly string m_node;
	private long m_cutoutRelays;
	private long m_outboxDrops;
	private long m_truncatedEntries;
	private long m_unknownStorageSkips;
	/// <summary>
	/// The voxel maps whose RangeChanged this service observes, by INSTANCE. It was keyed by entity id, but a stock
	/// procedural asteroid gets the same id every time it is generated (GetAsteroidEntityId hashes the storage name):
	/// once an unmodified asteroid was unloaded and generated again, the new instance was never subscribed and no
	/// edit to it reached the journal (T-0364).
	/// </summary>
	private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MyVoxelBase, object> m_subscribed = new();
	/// <summary>Applied watermarks this node has successfully reported to the registry. Sync loop only.</summary>
	private readonly Dictionary<string, long> m_reportedApplied = new(StringComparer.Ordinal);
	/// <summary>
	/// Highest sequence fetched into <see cref="m_inbound"/> per storage, so a drain pass keeps fetching
	/// ahead of the game-thread apply instead of one entry per pass. Reset to the applied watermark
	/// whenever the queue is empty, which keeps the old self-healing property: an entry the apply
	/// rejected is fetched again rather than skipped past.
	/// </summary>
	private readonly Dictionary<string, long> m_fetched = new(StringComparer.Ordinal);
	/// <summary>#1404: the World Authority's last tails table, its endpoint, generation and version. Sync loop only.</summary>
	private Dictionary<string, long> m_tails;
	private string m_tailsEndpoint;
	private long m_tailsGeneration;
	private string m_tailsVersionSeen;
	/// <summary>A World Authority without the tails route (older build) is polled per storage until this tick, then asked again.</summary>
	private long m_tailsRetryTick;
	/// <summary>Storages whose journal this node has drained to the World Authority's tail at least once this session.</summary>
	private readonly ConcurrentDictionary<string, byte> m_drained = new(StringComparer.Ordinal);
	private readonly DateTime m_startedUtc = DateTime.UtcNow;
	private long m_appliedSinceStart;
	/// <summary>
	/// Entries fetched per storage per sync pass and the queue depth the fetch keeps ahead of the apply.
	/// T-0053: a fresh node used to fetch ONE entry per 250 ms pass, so a 3536-entry EarthLike journal
	/// took ~15 min to replay while the node was already importing partitions from ~1 min after Game
	/// ready. Grids handed to it that stood on edited terrain (the platform cuts at ground3/ground4)
	/// were placed against the un-edited voxels and fell through to the planet core: all 9 w29
	/// fall-throughs crossed the surface within one census poll of a handover, 7 of 8 onto a node
	/// (re)started 1-2 min earlier, against a 6-9% base rate.
	/// </summary>
	private const int MaxFetchPerPass = 512;
	private const int MaxInboundQueue = 1024;
	/// <summary>Entries applied per game-thread Update (250 ms), so a long replay is a few short hitches, not one long one.</summary>
	private const int MaxAppliesPerUpdate = 64;

	/// <summary>
	/// Set once the gateway has rejected a voxel-replica report because this node's epoch is closed.
	/// Permanent for the process: the epoch is assigned at registration and never reopens. See T-0028.
	/// </summary>
	private bool m_replicaEpochClosed;
	/// <summary>Update has enumerated the voxel maps of a Ready session at least once; see CaughtUp.</summary>
	private bool m_sweptReadySession;
	// Covers every capture path: Apply runs on the game thread and both its write kinds are synchronous - range
	// entries write storage directly, and a relayed cutout only broadcasts to clients without touching storage.
	[ThreadStatic] private static bool m_applying;
	public static VoxelJournalService Instance { get; private set; }

	private VoxelJournalService()
	{
		m_gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		m_node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
		string data = Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT");
		m_root = Path.Combine(string.IsNullOrWhiteSpace(data) ? AppContext.BaseDirectory : Path.GetFullPath(data), "cluster", "voxel");
		ClusterRuntime.AuthorizeRegistryClient(m_http);
		if (ClusterRuntime.IsWorldAuthority)
		{
			Directory.CreateDirectory(m_root);
			Load();
			m_operationLog = LoadOperationLog(Path.Combine(m_root, "operations.oplog"));
			LoadCatalogBases();
			new Thread(CatalogLoop) { IsBackground = true, Name = "ClusterVoxelCatalog" }.Start();
		}
		else
		{
			m_outbox = new VoxelOutbox(Path.Combine(m_root, "outbox"));
			LoadCatalogBases();
			new Thread(SyncLoop) { IsBackground = true, Name = "ClusterVoxelJournal" }.Start();
		}
	}

	public static void Start()
	{
		Instance = new VoxelJournalService();
		MyPlanetSeams.ClusterStorageInit = PlanetStoragePatch.InitPrefix;
		MyVoxelMapSeams.ClusterStorageInit = AsteroidStoragePatch.InitPrefix;
		MyPlanetSeams.ClusterRangeChanged = PlanetStoragePatch.Postfix;
		MyVoxelBaseSeams.ClusterVoxelCutoutSphere = MyVoxelBasePatch.RequestVoxelCutoutSpherePostfix;
	}

	public bool TrySelectBase(MyObjectBuilder_EntityBase builder, out IMyStorage storage, out long tailSequence)
	{
		storage = null;
		tailSequence = -1;
		if (builder is not MyObjectBuilder_Planet planet || string.IsNullOrWhiteSpace(planet.StorageName)
			|| MySession.Static?.Ready == true || !m_bases.TryGetValue(planet.StorageName, out BaseState basis)) return false;
		byte[] bytes = VerifiedBaseBytes(planet.StorageName, ref basis);
		MyStorageBase loaded = MyStorageBase.Load(bytes, out bool oldFormat);
		MyPlanetStorageProvider provider = loaded?.DataProvider as MyPlanetStorageProvider;
		if (oldFormat || provider == null || provider.Generator?.Id.SubtypeName != planet.PlanetGenerator
			|| Math.Abs(provider.Radius - planet.Radius) > 0.01f || provider.StorageSize != loaded.Size)
		{
			loaded?.Close();
			throw new InvalidDataException("Catalogued voxel base does not match planet " + planet.StorageName);
		}

		storage = loaded;
		tailSequence = basis.TailSequence;
		return true;
	}

	/// <summary>
	/// SE1-0114: the base of an asteroid saved in the world, for its world-load Init. Only a base that carries
	/// edits (tail above zero) replaces the world's file: a compaction catalogues every voxel map, and the
	/// untouched ones (the 2,000 pre-placed asteroids of Octahedron, nearly all of them) are their world file.
	/// An immutable storage is renamed per entity at Init, so its journal never keyed on the builder's name.
	/// </summary>
	public bool TrySelectAsteroidBase(MyObjectBuilder_EntityBase builder, out IMyStorage storage, out long tailSequence)
	{
		storage = null;
		tailSequence = -1;
		if (builder is not MyObjectBuilder_VoxelMap map || !map.MutableStorage || string.IsNullOrWhiteSpace(map.StorageName)
			|| MySession.Static?.Ready == true || !m_bases.TryGetValue(map.StorageName, out BaseState basis)
			|| basis.TailSequence <= 0) return false;
		byte[] bytes = VerifiedBaseBytes(map.StorageName, ref basis);
		MyStorageBase loaded = MyStorageBase.Load(bytes, out bool oldFormat);
		if (oldFormat || loaded == null)
		{
			loaded?.Close();
			throw new InvalidDataException("Catalogued voxel base does not load for asteroid " + map.StorageName);
		}

		storage = loaded;
		tailSequence = basis.TailSequence;
		return true;
	}

	/// <summary>
	/// The bytes of a storage's catalogued base, fetched into this node's root when another node holds it,
	/// checked against the catalogued size and checksum.
	/// </summary>
	private byte[] VerifiedBaseBytes(string storageName, ref BaseState basis)
	{
		// A node only trusts base paths under its own data root. A catalogued path on another
		// node's root is fetched from the holder's control endpoint into this root instead of
		// being read through a shared filesystem that production topologies do not have.
		if (!IsUnderRoot(basis.Path))
		{
			string fetched = FetchBase(storageName, basis,
				canFallback: Path.IsPathFullyQualified(basis.Path) && File.Exists(basis.Path));
			if (fetched != null) m_bases[storageName] = basis = basis with { Path = fetched };
			else if (Path.IsPathFullyQualified(basis.Path) && File.Exists(basis.Path))
				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"[ClusterPlugin] voxel_base foreign_path_fallback=true storage={0} version={1} path={2}",
					storageName, basis.BaseVersion, basis.Path));
		}
		if (!Path.IsPathFullyQualified(basis.Path) || !File.Exists(basis.Path))
			throw new InvalidDataException("Catalogued voxel base is unavailable: " + basis.Path);

		byte[] bytes = File.ReadAllBytes(basis.Path);
		if (bytes.LongLength != basis.SizeBytes || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(basis.Checksum, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("Catalogued voxel base is corrupt: " + basis.Path);
		return bytes;
	}

	public void CommitBase(string storageName, long tailSequence)
	{
		if (tailSequence < 0) return;
		m_applied[storageName] = tailSequence;
		BaseState basis = m_bases[storageName];
		Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] voxel_catalog base_installed=true storage={0} version={1} tail={2}",
			storageName, basis.BaseVersion, tailSequence));
	}

	/// <summary>
	/// Records that a range changed, to be captured once it stops changing.
	/// <para>
	/// Reading the range here, from the notification, is a race: a hand drill cuts through
	/// <c>CutOutShapeWithPropertiesAsync</c>, so the write is still in flight when the range-changed notification
	/// arrives. A read taken then can catch a partly-written range, and because the notification for that range has
	/// already fired, nothing re-covers the remainder - the replicas are handed a snapshot the owning node never
	/// held, and diverge from it permanently. Ranges are therefore unioned here and captured in
	/// <see cref="FlushDirty"/> once two consecutive reads agree.
	/// </para>
	/// </summary>
	public void Observe(MyVoxelBase planet, Vector3I min, Vector3I max, MyStorageDataTypeFlags flags)
	{
		if (m_applying || planet?.Storage == null || string.IsNullOrWhiteSpace(planet.StorageName)) return;
		// The notification understates the write by one voxel on every side.
		// MyVoxelGenerator.CutOutShapeWithProperties caches and writes the range [minCorner-1, maxCorner+1] with
		// notify:false and later raises NotifyChanged(minCorner, maxCorner) - the inner box only - while
		// RemoveSmallVoxelsUsingChachedVoxels zeroes isolated small voxels across the whole cached window,
		// including that outer shell. A shell cell it clears is written to this node's storage but never notified,
		// so an unpadded capture publishes a journal that replays to different content than the origin holds:
		// the exact stable site1 divergence, node2 and the WA agreeing with each other against the owning node.
		min -= 1;
		max += 1;
		// Clamped here rather than only in FlushDirty: the flush retires a range only when the stored bounds equal
		// the clamped bounds it read, so an out-of-bounds range would republish forever instead of settling.
		planet.Storage.ClampVoxelCoord(ref min);
		planet.Storage.ClampVoxelCoord(ref max);
		m_storages.TryAdd(planet.StorageName, 0);
		lock (m_dirty)
		{
			if (!m_dirty.TryGetValue(planet.StorageName, out List<DirtyRange> ranges))
				m_dirty[planet.StorageName] = ranges = new List<DirtyRange>();
			// Merge only into a range this one is genuinely near. A planet sees changes far from any drill - world
			// generation, physics prefetch - and folding those into one box would grow it to planet scale, which
			// both blows up the read buffer and publishes megabytes of untouched voxels.
			foreach (DirtyRange candidate in ranges)
			{
				Vector3I mergedMin = Vector3I.Min(candidate.Min, min);
				Vector3I mergedMax = Vector3I.Max(candidate.Max, max);
				if (Volume(mergedMin, mergedMax) > MaxDirtyVoxels) continue;
				candidate.Min = mergedMin;
				candidate.Max = mergedMax;
				candidate.Flags |= flags;
				candidate.Hash = null;
				return;
			}
			ranges.Add(new DirtyRange { Min = min, Max = max, Flags = flags });
			// Too many disjoint ranges to track: force the oldest out rather than growing without bound.
			if (ranges.Count > MaxDirtyRanges) ranges[0].Ticks = MaxDirtyTicks;
		}
	}

	private static long Volume(Vector3I min, Vector3I max)
		=> (long)(max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1);

	/// <summary>
	/// Captures every range that has settled, and any that has waited too long. Runs on the game thread.
	/// <para>
	/// "Settled" is two consecutive reads with the same content, which is what makes the published snapshot one the
	/// owning node actually held. Continuous drilling never settles, so a range is also published once it has waited
	/// <see cref="MaxDirtyTicks"/> - that snapshot may be mid-write, but the range stays dirty and the *last* entry
	/// for it is always a settled one, which is what the replicas converge to.
	/// </para>
	/// </summary>
	private void FlushDirty()
	{
		(string Storage, DirtyRange Range)[] pending;
		lock (m_dirty)
			pending = m_dirty.SelectMany(entry => entry.Value.Select(range => (entry.Key, range))).ToArray();
		foreach ((string storage, DirtyRange dirty) in pending)
		{
			MyVoxelBase voxel = MySession.Static?.VoxelMaps.TryGetVoxelMapByName(storage);
			if (voxel?.Storage == null) continue;
			Vector3I min, max;
			MyStorageDataTypeFlags flags;
			string expected;
			int ticks;
			// The notification can arrive on a worker thread, so read the range's identity under the lock and
			// verify it is still the same afterwards: a union that lands mid-read must not be retired away.
			lock (m_dirty) { min = dirty.Min; max = dirty.Max; flags = dirty.Flags; expected = dirty.Hash; ticks = dirty.Ticks; }
			voxel.Storage.ClampVoxelCoord(ref min);
			voxel.Storage.ClampVoxelCoord(ref max);
			MyStorageData data = new(MyStorageDataTypeFlags.ContentAndMaterial);
			data.Resize(min, max);
			voxel.Storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, 0, min, max);
			int count = data.Size3D.Size;
			byte[] content = data[MyStorageDataTypeEnum.Content].AsSpan(0, count).ToArray();
			byte[] material = data[MyStorageDataTypeEnum.Material].AsSpan(0, count).ToArray();
			string hash = Convert.ToHexString(SHA256.HashData(content)) + Convert.ToHexString(SHA256.HashData(material));
			bool settled = expected != null && hash == expected;
			bool publish = settled || ticks >= MaxDirtyTicks || count >= MaxDirtyVoxels;
			lock (m_dirty)
			{
				bool tracked = m_dirty.TryGetValue(storage, out List<DirtyRange> ranges) && ranges.Contains(dirty);
				bool intact = tracked && dirty.Min == min && dirty.Max == max && dirty.Hash == expected;
				if (publish && settled && intact)
				{
					ranges.Remove(dirty);
					if (ranges.Count == 0) m_dirty.Remove(storage);
				}
				// Published early, or dirtied again mid-read: keep the range and let it settle before it retires.
				else if (publish) { dirty.Hash = hash; dirty.Ticks = 0; }
				else if (intact) { dirty.Hash = hash; dirty.Ticks = ticks + 1; }
			}
			if (!publish) continue;
			var entry = new VoxelJournalCodec.Entry(Guid.NewGuid(), ClusterRuntime.AuthorityGeneration, 0, storage,
				min, max, flags, content, material);
			if (ClusterRuntime.IsWorldAuthority) Append(entry, apply: false);
			else EnqueueLive(entry, "planet edit");
		}
	}

	private sealed class DirtyRange
	{
		public Vector3I Min;
		public Vector3I Max;
		public MyStorageDataTypeFlags Flags;
		public string Hash;
		public int Ticks;
	}

	public void ObserveCutoutSphere(MyVoxelBase voxel, Vector3D center, float radius, bool createDebris, bool damage)
	{
		if (m_applying || voxel?.RootVoxel is not MyPlanet planet || string.IsNullOrWhiteSpace(planet.StorageName)) return;
		var entry = new VoxelJournalCodec.Entry(Guid.NewGuid(), ClusterRuntime.AuthorityGeneration, 0, planet.StorageName,
			default, default, default, Array.Empty<byte>(), Array.Empty<byte>(), VoxelJournalCodec.EntryKind.CutoutSphere,
			m_node, center, radius, createDebris, damage);
		m_storages.TryAdd(planet.StorageName, 0);
		if (ClusterRuntime.IsWorldAuthority) Append(entry, apply: false);
		else Enqueue(entry, "cutout broadcast");
	}

	/// <summary>
	/// Enqueues an entry produced from this node's own live storage, recording its operation id so the fetch-back
	/// does not re-write it. Recorded before the send and rolled back if the send is refused, so a lost edit never
	/// leaves a phantom entry in the set.
	/// </summary>
	private void EnqueueLive(VoxelJournalCodec.Entry entry, string what)
	{
		m_liveSelfEdits[entry.OperationId] = (m_applied.GetValueOrDefault(entry.StorageName), entry.StorageName);
		try { Enqueue(entry, what); }
		catch { m_liveSelfEdits.TryRemove(entry.OperationId, out _); throw; }
	}

	/// <summary>
	/// Persists an entry to the durable outbox. This runs <em>after</em> the storage has already been written - the
	/// vanilla seam is a range-changed notification - so a lost entry is not a refused edit, it is a local edit
	/// the replicas will never see, and the owning node diverges permanently and silently. The disk write is what
	/// makes the edit survive a crash before the sync loop delivers it; only a storage failure loses it, loudly.
	/// </summary>
	private void Enqueue(VoxelJournalCodec.Entry entry, string what)
	{
		try { m_outbox.Add(VoxelJournalCodec.Write(entry)); }
		catch (Exception exception)
		{
			Interlocked.Increment(ref m_outboxDrops);
			throw new InvalidOperationException("Voxel journal outbox could not persist an applied " + what
				+ " and this node has diverged", exception);
		}
	}

	public void Update()
	{
		if (MySession.Static?.Ready != true) return;
		foreach (MyVoxelBase voxel in MySession.Static.VoxelMaps.Instances)
		{
			if (string.IsNullOrWhiteSpace(voxel.StorageName) || voxel != voxel.RootVoxel) continue;
			if (voxel is MyPlanet) { m_storages.TryAdd(voxel.StorageName, 0); continue; }
			// Asteroids have no vanilla seam; the public RangeChanged event carries the same
			// notification, and the m_applying guard in Observe suppresses journal replays.
			if (voxel is MyVoxelMap && !m_subscribed.TryGetValue(voxel, out object _))
			{
				m_subscribed.Add(voxel, null);
				voxel.RangeChanged += AsteroidRangeChanged;
				m_storages.TryAdd(voxel.StorageName, 0);
			}
		}
		Volatile.Write(ref m_sweptReadySession, true);
		MaterializeCataloguedAsteroids();
		int applied = 0;
		while (applied < MaxAppliesPerUpdate && m_inbound.TryDequeue(out VoxelJournalCodec.Entry entry))
		{
			Apply(entry);
			applied++;
			Interlocked.Increment(ref m_appliedSinceStart);
		}
		// After the inbound applies, so a captured snapshot reflects the newest local state.
		FlushDirty();
		if (++m_replayScanTick >= 60)
		{
			m_replayScanTick = 0;
			ReplayAppearedStorages();
		}
	}

	/// <summary>
	/// T-0364, game thread. A storage this node skipped entries for has been generated here, or the storage a
	/// watermark was applied to has been replaced by a new voxel entity: its content is the pristine shape, so the
	/// watermark goes back to the catalogued base tail (zero without a base) and the sync loop fetches the journal
	/// again. Entries of such a storage that are still queued with a later sequence are dropped in Apply and
	/// fetched again in order.
	/// </summary>
	private void ReplayAppearedStorages()
	{
		if (ClusterRuntime.IsWorldAuthority || m_outbox == null) return;
		foreach (string storage in m_skippedStorages.Keys.Concat(m_appliedInstance.Keys).Distinct(StringComparer.Ordinal).ToArray())
		{
			MyVoxelBase voxel = MySession.Static.VoxelMaps.TryGetVoxelMapByName(storage);
			if (voxel?.Storage == null || voxel is MyPlanet) continue;
			bool skipped = m_skippedStorages.ContainsKey(storage);
			bool? sameInstance = m_appliedInstance.TryGetValue(storage, out WeakReference<MyVoxelBase> reference)
				? reference.TryGetTarget(out MyVoxelBase appliedTo) && ReferenceEquals(appliedTo, voxel) : null;
			if (!VoxelReplayPolicy.NeedsReplay(skipped, sameInstance)) continue;
			long from = VoxelReplayPolicy.ReplayFrom(m_bases.TryGetValue(storage, out BaseState basis) ? basis.TailSequence : null);
			long was = m_applied.GetValueOrDefault(storage);
			m_skippedStorages.TryRemove(storage, out byte _);
			m_appliedInstance[storage] = new WeakReference<MyVoxelBase>(voxel);
			if (was <= from) continue;
			m_replaying[storage] = 0;
			m_applied[storage] = from;
			m_storages.TryAdd(storage, 0);
			Interlocked.Increment(ref m_storageReplays);
			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
				"[ClusterPlugin] voxel_journal storage_replay=true storage={0} reason={1} from={2} was={3}",
				storage, skipped ? "generated_after_skip" : "regenerated", from, was));
		}
	}

	public string Status()
	{
		int retainedCutouts;
		lock (m_lock)
			retainedCutouts = m_entries.Values.SelectMany(entries => entries.Values)
				.Count(entry => entry.Kind == VoxelJournalCodec.EntryKind.CutoutSphere);
		return JsonSerializer.Serialize(new
		{
			cutoutRelays = Interlocked.Read(ref m_cutoutRelays),
			retainedCutouts,
			// A lane asserting convergence needs to tell "not replicated yet" from "never will be": anything but
			// zero drops means this node holds an edit the cluster will never see.
			outboundQueued = m_outbox?.Count ?? 0,
			outboundRetrying = m_headRetrying ? 1 : 0,
			outboxDrops = Interlocked.Read(ref m_outboxDrops),
			truncatedEntries = Interlocked.Read(ref m_truncatedEntries),
			unknownStorageSkips = Interlocked.Read(ref m_unknownStorageSkips),
			storageReplays = Interlocked.Read(ref m_storageReplays),
			replaying = m_replaying.Count,
			// Own live edits not yet fetched back. Should settle to zero once the journal round-trips; a stuck
			// non-zero means this node's own edits are not returning and it will not fold them into its base.
			liveSelfPending = m_liveSelfEdits.Count,
			dirtyRanges = DirtyCount(),
			applied = m_applied.OrderBy(item => item.Key, StringComparer.Ordinal)
				.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
		});
	}

	private int DirtyCount() { lock (m_dirty) return m_dirty.Values.Sum(ranges => ranges.Count); }

	public VoxelJournalCodec.Entry Accept(byte[] bytes)
	{
		if (!ClusterRuntime.IsWorldAuthority) throw new InvalidOperationException("This node is not the World Authority");
		VoxelJournalCodec.Entry entry = VoxelJournalCodec.Read(bytes);
		if (!ClusterRuntime.AcceptAuthorityMessage(entry.AuthorityGeneration))
			throw new InvalidOperationException("Voxel journal authority generation is fenced");
		// Chaos-test knob: hold the append in flight BEFORE the durable write so a lane can kill the
		// WA mid-mutation deterministically. Zero in production traffic.
		int testDelay = Volatile.Read(ref m_testDelayMilliseconds);
		if (testDelay > 0)
		{
			Interlocked.Increment(ref m_testHeldCount);
			Thread.Sleep(testDelay);
		}
		return Append(entry, apply: true);
	}

	public static object ConfigureTestDelay(int milliseconds)
	{
		if (!ClusterRuntime.IsWorldAuthority)
			throw new InvalidOperationException("The voxel append delay is a World Authority chaos knob");
		if (milliseconds is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
		Interlocked.Exchange(ref m_testDelayMilliseconds, milliseconds);
		return TestDelayStatus();
	}

	public static object TestDelayStatus()
		=> new { delayMs = Volatile.Read(ref m_testDelayMilliseconds), held = Volatile.Read(ref m_testHeldCount) };

	private static int m_testDelayMilliseconds;
	private static int m_testHeldCount;

	/// <summary>
	/// WA-5 total-order evidence: every retained journal entry for a storage, in sequence order.
	/// Works on any role - a replica's retained window must list the shared operation ids in the
	/// SAME sequence order the WA assigned, which is what makes the order total.
	/// </summary>
	public string EntriesFor(string storage)
	{
		lock (m_lock)
		{
			if (!m_entries.TryGetValue(storage, out SortedDictionary<long, VoxelJournalCodec.Entry> entries))
				return "[]";
			return JsonSerializer.Serialize(entries.Values.OrderBy(entry => entry.Sequence)
				.Select(entry => new { sequence = entry.Sequence, operationId = entry.OperationId, kind = entry.Kind.ToString() })
				.ToList());
		}
	}

	public byte[] Next(string storage, long after)
	{
		if (!ClusterRuntime.IsWorldAuthority) throw new InvalidOperationException("This node is not the World Authority");
		lock (m_lock)
		{
			if (!m_entries.TryGetValue(storage, out SortedDictionary<long, VoxelJournalCodec.Entry> entries)) return null;
			VoxelJournalCodec.Entry entry = entries.Values.FirstOrDefault(value => value.Sequence > after);
			return entry == null ? null : VoxelJournalCodec.Write(entry with { AuthorityGeneration = ClusterRuntime.AuthorityGeneration });
		}
	}

	/// <summary>
	/// #1404: the highest held sequence of every storage with retained entries, and the version of that table; null
	/// when <paramref name="version"/> is still current. One request per replica pass replaces one GET per tracked
	/// storage: on Octahedron that was ~2,008 storages per node, 4 of them with entries, 32.7k requests/s into the
	/// World Authority. A storage missing from the table has nothing to fetch, which is what the per-storage 204 said.
	/// </summary>
	public string Tails(string version)
	{
		if (!ClusterRuntime.IsWorldAuthority) throw new InvalidOperationException("This node is not the World Authority");
		lock (m_lock)
		{
			long generation = ClusterRuntime.AuthorityGeneration;
			string current = s_tailsProcess + "." + generation.ToString(CultureInfo.InvariantCulture) + "." + m_tailsVersion.ToString(CultureInfo.InvariantCulture);
			if (current == version) return null;
			return JsonSerializer.Serialize(new
			{
				version = current,
				generation,
				tails = m_entries.Where(item => item.Value.Count > 0).ToDictionary(item => item.Key, item => item.Value.Keys.Max(), StringComparer.Ordinal)
			});
		}
	}

	public string Compact()
	{
		if (!ClusterRuntime.IsWorldAuthority) throw new InvalidOperationException("This node is not the World Authority");
		List<object> bases = new();
		foreach (MyVoxelBase planet in MySession.Static.VoxelMaps.Instances
			.Where(voxel => (voxel is MyPlanet || voxel is MyVoxelMap) && voxel == voxel.RootVoxel && !string.IsNullOrWhiteSpace(voxel.StorageName))
			.GroupBy(voxel => voxel.StorageName, StringComparer.Ordinal).Select(group => group.First()))
		{
			long sequence;
			byte[] bytes;
			BaseState reused = null;
			lock (m_lock)
			{
				sequence = m_applied.GetValueOrDefault(planet.StorageName);
				if (m_bases.TryGetValue(planet.StorageName, out BaseState current) && current.TailSequence == sequence)
				{
					m_pendingWatermarks[planet.StorageName] = 0;
					reused = current;
					bytes = null;
				}
				else planet.Storage.Save(out bytes);
			}
			if (reused != null)
			{
				bases.Add(new { storage = planet.StorageName, baseVersion = reused.BaseVersion, tailSequence = sequence,
					path = reused.Path, bytes = new FileInfo(reused.Path).Length, checksum = reused.Checksum,
					truncated = TruncateJournal(planet.StorageName, sequence) });
				continue;
			}
			string map = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(planet.StorageName)));
			string directory = Path.Combine(m_root, "base", map);
			Directory.CreateDirectory(directory);
			// The directory name is a hash, so a catalog rebuild scanning surviving roots cannot recover
			// the storage name from the path alone; the manifest is its only name source.
			string manifest = Path.Combine(directory, "storage-name.txt");
			if (!File.Exists(manifest)) File.WriteAllText(manifest, planet.StorageName);
			string path = Path.Combine(directory, "base-" + sequence.ToString("D20", CultureInfo.InvariantCulture) + ".vx2");
			string temporary = path + ".tmp";
			using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				stream.Write(bytes);
				stream.Flush(true);
			}
			File.Move(temporary, path, overwrite: true);
			string checksum = Convert.ToHexString(SHA256.HashData(bytes));
			RegisterBase(planet.StorageName, sequence, sequence, path, bytes.Length, checksum);
			m_bases[planet.StorageName] = new BaseState(sequence, sequence, path, bytes.Length, checksum);
			bases.Add(new { storage = planet.StorageName, baseVersion = sequence, tailSequence = sequence, path, bytes = bytes.Length, checksum,
				truncated = TruncateJournal(planet.StorageName, sequence) });
		}
		return JsonSerializer.Serialize(bases);
	}

	/// <summary>
	/// Deletes journal entries no consumer can still need after a compaction catalogued a base at
	/// <paramref name="tail"/>. The cut is the minimum of the tail and every active regular node's reported
	/// applied watermark, with an unreported node counting as zero: a node that has not said where it is might
	/// still need everything, so it blocks truncation rather than being truncated past. New consumers always
	/// start from the newest catalogued base, whose tail is at or above any cut taken here. Operation ids of
	/// truncated entries stay in the durable operation log, so a re-sent ancient edit still deduplicates instead
	/// of re-appending old content under a fresh sequence.
	/// </summary>
	private long TruncateJournal(string storage, long tail)
	{
		long cut = tail;
		try
		{
			using JsonDocument nodes = ClusterRuntime.Get(m_http, m_gateway + "/registry/nodes");
			using JsonDocument replicas = ClusterRuntime.Get(m_http, m_gateway + "/registry/voxel-replicas");
			Dictionary<string, long> applied = new(StringComparer.Ordinal);
			foreach (JsonElement row in replicas.RootElement.EnumerateArray())
				if (string.Equals(row.GetProperty("StorageName").GetString(), storage, StringComparison.Ordinal))
					applied[row.GetProperty("Node").GetString()] = row.GetProperty("Applied").GetInt64();
			foreach (JsonElement node in nodes.RootElement.EnumerateArray())
			{
				// The registry serializes enums as numbers: Role 0 is Regular, State 0 is Active.
				if (node.GetProperty("Role").GetInt32() != 0 || node.GetProperty("State").GetInt32() != 0) continue;
				cut = Math.Min(cut, applied.GetValueOrDefault(node.GetProperty("Node").GetString(), 0L));
			}
		}
		catch (Exception exception)
		{
			// Truncating too little is always safe; truncating on guesswork is not. Keep everything this round.
			Console.WriteLine("[ClusterPlugin] voxel_journal truncation_skipped=true error=" + SecretRedactor.Redact(exception.Message));
			return 0;
		}
		List<VoxelJournalCodec.Entry> removed = new();
		lock (m_lock)
		{
			if (cut <= 0 || !m_entries.TryGetValue(storage, out SortedDictionary<long, VoxelJournalCodec.Entry> entries)) return 0;
			foreach (long sequence in entries.Keys.Where(value => value <= cut).ToArray())
			{
				removed.Add(entries[sequence]);
				entries.Remove(sequence);
			}
			if (removed.Count > 0) m_tailsVersion++;
		}
		foreach (VoxelJournalCodec.Entry entry in removed) DeleteFile(entry);
		if (removed.Count > 0)
		{
			Interlocked.Add(ref m_truncatedEntries, removed.Count);
			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
				"[ClusterPlugin] voxel_journal truncated={0} storage={1} cut={2}", removed.Count, storage, cut));
		}
		return removed.Count;
	}

	private VoxelJournalCodec.Entry Append(VoxelJournalCodec.Entry proposed, bool apply)
	{
		VoxelJournalCodec.Entry entry;
		lock (m_lock)
		{
			if (m_operations.Contains(proposed.OperationId))
				// A re-sent operation whose entry was already truncated is fully applied everywhere - that is what
				// made the truncation safe - so acknowledging it with the proposal itself loses nothing.
				return m_entries.Values.SelectMany(value => value.Values).SingleOrDefault(value => value.OperationId == proposed.OperationId)
					?? proposed;
			if (!m_entries.TryGetValue(proposed.StorageName, out SortedDictionary<long, VoxelJournalCodec.Entry> entries))
				m_entries[proposed.StorageName] = entries = new();
			long sequence = VoxelJournalCodec.NextSequence(entries.Keys.DefaultIfEmpty(0).Max(), m_applied.GetValueOrDefault(proposed.StorageName));
			entry = proposed with { AuthorityGeneration = ClusterRuntime.AuthorityGeneration, Sequence = sequence };
			WriteFile(entry);
			entries[sequence] = entry;
			m_tailsVersion++;
			m_operations.Add(entry.OperationId);
			m_operationLog.Write(entry.OperationId.ToByteArray());
			m_operationLog.Flush();
		}
		if (apply) m_inbound.Enqueue(entry);
		else
		{
			m_applied[entry.StorageName] = entry.Sequence;
			ReportWatermark(entry.StorageName);
		}
		return entry;
	}

	private void Apply(VoxelJournalCodec.Entry entry)
	{
		if (!ClusterRuntime.IsWorldAuthority && !ClusterRuntime.AcceptAuthorityMessage(entry.AuthorityGeneration)) return;
		long expected = m_applied.GetValueOrDefault(entry.StorageName) + 1;
		if (entry.Sequence < expected) return;
		// T-0364: fetched before the replay reset the watermark; the sync loop fetches it again in order.
		if (entry.Sequence != expected && m_replaying.ContainsKey(entry.StorageName)) return;
		if (entry.Sequence != expected) throw new InvalidDataException($"Voxel journal gap for {entry.StorageName}: expected {expected}, got {entry.Sequence}");
		MyVoxelBase voxel = MySession.Static.VoxelMaps.TryGetVoxelMapByName(entry.StorageName);
		if (voxel?.Storage == null)
		{
			// A storage this node never loaded (a runtime asteroid materialized elsewhere) must not
			// kill the sync loop; the skip is counted so divergence stays visible, and the applied
			// watermark still advances - the entry can never apply here.
			Interlocked.Increment(ref m_unknownStorageSkips);
			Console.WriteLine("[ClusterPlugin] voxel_journal unknown_storage_skipped=true storage=" + entry.StorageName
				+ " sequence=" + entry.Sequence.ToString(CultureInfo.InvariantCulture));
			m_applied[entry.StorageName] = entry.Sequence;
			m_skippedStorages[entry.StorageName] = 0;
			return;
		}
		// T-0364: a replay rebuilds a pristine storage, so this node's own entries are written too, and the
		// client-only cutout relay is not repeated (the range entries carry the content).
		bool replaying = m_replaying.ContainsKey(entry.StorageName);
		bool self = false;
		(long Watermark, string Storage) captured = default;
		m_applying = true;
		try
		{
			if (entry.Kind == VoxelJournalCodec.EntryKind.CutoutSphere)
			{
				if (!replaying && !string.Equals(entry.OriginNode, m_node, StringComparison.Ordinal))
				{
					// Clients attached to this node carve the sphere themselves, exactly as the origin's own clients
					// did. This server's storage is NOT touched: re-executing the carve here would land its write
					// asynchronously, outside journal order, over range entries that already carried the origin's true
					// content - which is precisely the replica divergence this relay used to cause. The range entries
					// that follow the cutout in the journal are the content authority; the relay is client-only.
					voxel.RootVoxel.ClusterBroadcastVoxelCutoutSphere(entry.Center, entry.Radius, entry.CreateDebris, entry.Damage);
					Interlocked.Increment(ref m_cutoutRelays);
				}
			}
			else if (!(self = m_liveSelfEdits.TryRemove(entry.OperationId, out captured)) || replaying)
			{
				MyStorageData data = new(entry.Max - entry.Min + 1, entry.Content, entry.Material);
				voxel.Storage.WriteRange(data, MyStorageDataTypeFlags.ContentAndMaterial, entry.Min, entry.Max);
				ReplicationClientViewPatch.MarkVoxelStale(voxel);
				lock (m_lock)
				{
					if (!m_foreignApplied.TryGetValue(entry.StorageName, out List<(long, VRageMath.Vector3I, VRageMath.Vector3I)> boxes))
						m_foreignApplied[entry.StorageName] = boxes = new();
					boxes.Add((entry.Sequence, entry.Min, entry.Max));
					if (boxes.Count > 256) boxes.RemoveAt(0);
				}
			}
			else
			{
				// This node produced the entry live: its storage already holds the true result and the drill has
				// moved on, so re-writing the snapshot would normally revert newer cuts. EXCEPT when a foreign
				// entry overlapping this box interleaved between the local apply and this return - then the local
				// apply ran out of the World Authority's total order and the snapshot must win again.
				bool interleaved;
				lock (m_lock)
					interleaved = m_foreignApplied.TryGetValue(entry.StorageName, out List<(long Sequence, VRageMath.Vector3I Min, VRageMath.Vector3I Max)> boxes)
						&& boxes.Any(box => box.Sequence > captured.Watermark && box.Sequence < entry.Sequence
							&& box.Min.X <= entry.Max.X && entry.Min.X <= box.Max.X
							&& box.Min.Y <= entry.Max.Y && entry.Min.Y <= box.Max.Y
							&& box.Min.Z <= entry.Max.Z && entry.Min.Z <= box.Max.Z);
				if (interleaved)
				{
					Console.WriteLine("[ClusterPlugin] voxel self entry re-applied out-of-order operation=" + entry.OperationId
						+ " sequence=" + entry.Sequence + " storage=" + entry.StorageName);
					MyStorageData data = new(entry.Max - entry.Min + 1, entry.Content, entry.Material);
					voxel.Storage.WriteRange(data, MyStorageDataTypeFlags.ContentAndMaterial, entry.Min, entry.Max);
					ReplicationClientViewPatch.MarkVoxelStale(voxel);
				}
			}
		}
		finally { m_applying = false; }
		m_applied[entry.StorageName] = entry.Sequence;
		if (!m_appliedInstance.TryGetValue(entry.StorageName, out WeakReference<MyVoxelBase> known)
			|| !known.TryGetTarget(out MyVoxelBase knownVoxel) || !ReferenceEquals(knownVoxel, voxel))
			m_appliedInstance[entry.StorageName] = new WeakReference<MyVoxelBase>(voxel);
		ReportWatermark(entry.StorageName);
	}

	public bool HasBase(string storage) => m_bases.ContainsKey(storage);

	/// <summary>
	/// T-0368, game thread: what a handover needs to know about a storage to carry its replica (VoxelCarryPolicy).
	/// Applied: the journal watermark. Unsettled: the watermark overstates the storage - entries were skipped, or
	/// are being replayed (T-0364). OwnEdits: this node's storage holds an edit that has no journal sequence yet,
	/// so no watermark covers it - a range still dirty, or an entry sent and not fetched back.
	/// </summary>
	public (long Applied, bool Unsettled, bool OwnEdits) CarryState(string storage)
	{
		bool dirty;
		lock (m_dirty) dirty = m_dirty.ContainsKey(storage);
		return (m_applied.GetValueOrDefault(storage), m_skippedStorages.ContainsKey(storage) || m_replaying.ContainsKey(storage),
			dirty || m_liveSelfEdits.Values.Any(edit => edit.Storage == storage));
	}

	/// <summary>
	/// Game thread. A catalogued base whose spec-encoding storage name is absent from the session is
	/// a runtime asteroid this process never saw spawn (restart, replacement WA): re-materialize it
	/// from the base content at its name-encoded position and start the journal at the base tail.
	/// Runs synchronously, so a foreign-held base uses a readable path directly rather than the
	/// blocking fetch; an unreachable base is skipped and retried next update.
	/// </summary>
	private readonly ConcurrentDictionary<string, byte> m_baseFetches = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, long> m_baseFetchFailed = new(StringComparer.Ordinal);

	private void FetchBaseInBackground(string storage, BaseState basis)
	{
		if (m_root == null || IsUnderRoot(basis.Path)
			|| m_baseFetchFailed.TryGetValue(storage, out long failed) && Environment.TickCount64 - failed < 60_000
			|| !m_baseFetches.TryAdd(storage, 0)) return;
		ThreadPool.QueueUserWorkItem(_ =>
		{
			try
			{
				string fetched = FetchBase(storage, basis, canFallback: false);
				if (fetched == null) m_baseFetchFailed[storage] = Environment.TickCount64;
				else if (m_bases.TryUpdate(storage, basis with { Path = fetched }, basis))
					Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
						"[ClusterPlugin] voxel_base fetched_for_install=true storage={0} version={1}", storage, basis.BaseVersion));
			}
			catch (Exception exception)
			{
				m_baseFetchFailed[storage] = Environment.TickCount64;
				Console.WriteLine("[ClusterPlugin] voxel_base fetch_for_install failed storage=" + storage + ": " + SecretRedactor.Redact(exception.Message));
			}
			finally { m_baseFetches.TryRemove(storage, out byte _); }
		});
	}

	private void MaterializeCataloguedAsteroids()
	{
		foreach (KeyValuePair<string, BaseState> pair in m_bases)
		{
			(int Seed, int Radius, VRageMath.Vector3D Position)? spec = AsteroidSpawnService.TryParse(pair.Key);
			if (spec == null || MySession.Static.VoxelMaps.TryGetVoxelMapByName(pair.Key) != null) continue;
			BaseState basis = pair.Value;
			if (!Path.IsPathFullyQualified(basis.Path) || !File.Exists(basis.Path))
			{
				// T-0321: a base held on another HOST has no readable path here, ever - retrying the path every update
				// never installed the asteroid on a remote node. Fetch it into this root off the game thread; the next
				// update finds it local.
				FetchBaseInBackground(pair.Key, basis);
				continue;
			}
			byte[] bytes;
			try { bytes = File.ReadAllBytes(basis.Path); }
			catch (Exception) { continue; }
			if (bytes.LongLength != basis.SizeBytes
				|| !Convert.ToHexString(SHA256.HashData(bytes)).Equals(basis.Checksum, StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine("[ClusterPlugin] voxel_catalog asteroid_base_corrupt=true storage=" + pair.Key);
				continue;
			}
			MyStorageBase loaded = MyStorageBase.Load(bytes, out bool oldFormat);
			if (oldFormat || loaded == null) { loaded?.Close(); continue; }
			if (!IsUnderRoot(basis.Path))
				Console.WriteLine("[ClusterPlugin] voxel_base foreign_path_fallback=true storage=" + pair.Key
					+ " version=" + basis.BaseVersion + " path=" + basis.Path);
			VRageMath.Vector3D corner = spec.Value.Position
				- new VRageMath.Vector3D(loaded.Size.X, loaded.Size.Y, loaded.Size.Z) * 0.5;
			Sandbox.Game.World.MyWorldGenerator.AddVoxelMap(pair.Key, loaded, corner);
			m_applied[pair.Key] = basis.TailSequence;
			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
				"[ClusterPlugin] voxel_catalog asteroid_installed=true storage={0} version={1} tail={2}",
				pair.Key, basis.BaseVersion, basis.TailSequence));
		}
	}

	private void AsteroidRangeChanged(MyVoxelBase voxel, Vector3I min, Vector3I max, MyStorageDataTypeFlags flags)
	{
		if (voxel is MyPlanet) return;
		Observe(voxel, min, max, flags);
	}

	private bool IsUnderRoot(string path)
		=> Path.IsPathFullyQualified(path) && Path.GetFullPath(path).StartsWith(m_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

	/// <summary>
	/// Serves this node's copy of a catalogued voxel base to a peer that does not hold it. Only the
	/// newest base per storage is retained, which is the only one a fresh consumer ever selects.
	/// </summary>
	public byte[] ReadBaseFile(string storage, long version)
	{
		if (!m_bases.TryGetValue(storage, out BaseState basis) || basis.BaseVersion != version
			|| !IsUnderRoot(basis.Path) || !File.Exists(basis.Path))
			throw new InvalidOperationException("This node does not hold voxel base " + storage + "/" + version);
		return File.ReadAllBytes(basis.Path);
	}

	/// <summary>
	/// Fetches a catalogued base from its holder's control endpoint into this node's own root,
	/// verified against the catalog size and checksum. The holder endpoint is resolved fresh from
	/// the registry on every attempt because the holder may still be registering (cluster boot,
	/// registry rebuild). Returns null when no attempt succeeds before the deadline; the caller
	/// may then fall back to a readable foreign path (shared filesystem) with a distinct log line. A
	/// registry that advertises NO endpoint (the holder is dead) combined with an available fallback
	/// short-circuits immediately - waiting out the deadline would only stall the load.
	/// </summary>
	private string FetchBase(string storage, BaseState basis, bool canFallback)
	{
		string map = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(storage)));
		string directory = Path.Combine(m_root, "base", map);
		string local = Path.Combine(directory, "base-" + basis.BaseVersion.ToString("D20", CultureInfo.InvariantCulture) + ".vx2");
		try
		{
			if (File.Exists(local))
			{
				byte[] cached = File.ReadAllBytes(local);
				if (cached.LongLength == basis.SizeBytes
					&& Convert.ToHexString(SHA256.HashData(cached)).Equals(basis.Checksum, StringComparison.OrdinalIgnoreCase))
					return local;
				File.Delete(local);
			}
		}
		catch (Exception) { }
		using HttpClient direct = ClusterHttp.Client(TimeSpan.FromSeconds(120));
		ClusterRuntime.AuthorizeRegistryClient(direct);
		DateTime deadline = DateTime.UtcNow.AddSeconds(45);
		while (DateTime.UtcNow < deadline)
		{
			try
			{
				string endpoint = null;
				using (JsonDocument catalog = ClusterRuntime.Get(m_http, m_gateway + "/registry/voxel-bases"))
					foreach (JsonElement item in catalog.RootElement.EnumerateArray())
						if (item.GetProperty("StorageName").GetString() == storage
							&& item.GetProperty("BaseVersion").GetInt64() == basis.BaseVersion
							&& item.TryGetProperty("HolderEndpoint", out JsonElement value) && value.ValueKind == JsonValueKind.String)
							endpoint = value.GetString();
				if (endpoint == null && canFallback)
				{
					Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
						"[ClusterPlugin] voxel_base holder_unavailable=true storage={0} version={1}", storage, basis.BaseVersion));
					return null;
				}
				if (endpoint != null)
				{
					byte[] bytes = direct.GetByteArrayAsync(endpoint.TrimEnd('/') + "/cluster/voxel-base-file?storage="
						+ Uri.EscapeDataString(storage) + "&version=" + basis.BaseVersion.ToString(CultureInfo.InvariantCulture))
						.GetAwaiter().GetResult();
					if (bytes.LongLength != basis.SizeBytes
						|| !Convert.ToHexString(SHA256.HashData(bytes)).Equals(basis.Checksum, StringComparison.OrdinalIgnoreCase))
						throw new InvalidDataException("Fetched voxel base does not match the catalog");
					Directory.CreateDirectory(directory);
					string manifest = Path.Combine(directory, "storage-name.txt");
					if (!File.Exists(manifest)) File.WriteAllText(manifest, storage);
					string temporary = local + ".tmp";
					using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
					{
						stream.Write(bytes);
						stream.Flush(true);
					}
					File.Move(temporary, local, overwrite: true);
					Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
						"[ClusterPlugin] voxel_base fetched=true storage={0} version={1} bytes={2} from={3}",
						storage, basis.BaseVersion, bytes.Length, endpoint));
					return local;
				}
			}
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] voxel_base fetch_failed=true error=" + SecretRedactor.Redact(exception.Message));
			}
			Thread.Sleep(2000);
		}
		return null;
	}

	private void RegisterBase(string storage, long baseVersion, long tailSequence, string path, long size, string checksum)
	{
		using JsonDocument ignored = Post("/registry/voxel-bases?storage=" + Uri.EscapeDataString(storage)
			+ "&baseVersion=" + baseVersion.ToString(CultureInfo.InvariantCulture) + "&tailSeq=" + tailSequence.ToString(CultureInfo.InvariantCulture)
			+ "&node=" + Uri.EscapeDataString(Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty)
			+ "&epoch=" + ClusterRuntime.RegistryNodeEpoch.ToString(CultureInfo.InvariantCulture)
			+ "&generation=" + ClusterRuntime.AuthorityGeneration.ToString(CultureInfo.InvariantCulture)
			+ "&size=" + size.ToString(CultureInfo.InvariantCulture) + "&checksum=" + Uri.EscapeDataString(checksum)
			+ "&location=" + Uri.EscapeDataString(path));
		ReportWatermark(storage, baseVersion, tailSequence);
	}

	private void ReportWatermark(string storage)
	{
		if (m_bases.ContainsKey(storage)) m_pendingWatermarks[storage] = 0;
	}

	private void ReportWatermark(string storage, long baseVersion, long tailSequence)
	{
		using JsonDocument ignored = Post("/registry/voxel-journals?storage=" + Uri.EscapeDataString(storage)
			+ "&baseVersion=" + baseVersion.ToString(CultureInfo.InvariantCulture) + "&tailSeq=" + tailSequence.ToString(CultureInfo.InvariantCulture)
			+ "&node=" + Uri.EscapeDataString(Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty)
			+ "&epoch=" + ClusterRuntime.RegistryNodeEpoch.ToString(CultureInfo.InvariantCulture)
			+ "&generation=" + ClusterRuntime.AuthorityGeneration.ToString(CultureInfo.InvariantCulture));
	}

	private JsonDocument Post(string path)
	{
		using HttpResponseMessage response = m_http.PostAsync(m_gateway + path, null).GetAwaiter().GetResult();
		response.EnsureSuccessStatusCode();
		return JsonDocument.Parse(response.Content.ReadAsStream());
	}

	private void CatalogLoop()
	{
		while (true)
		{
			foreach (string storage in m_pendingWatermarks.Keys)
			{
				if (!m_pendingWatermarks.TryRemove(storage, out _) || !m_bases.TryGetValue(storage, out BaseState basis)) continue;
				try { ReportWatermark(storage, basis.BaseVersion, m_applied.GetValueOrDefault(storage)); }
				catch (Exception exception)
				{
					m_pendingWatermarks[storage] = 0;
					Console.WriteLine("[ClusterPlugin] voxel_catalog report_failed=true error=" + SecretRedactor.Redact(exception.Message));
				}
			}
			Thread.Sleep(250);
		}
	}

	private void SyncLoop()
	{
		while (true)
		{
			try
			{
				(string endpoint, long generation) = Authority();
				// The node-to-WA chaos partition severs this HTTP path too - packet loss does not
				// pick transports. The enclosing catch treats it like any WA outage: the outbox head
				// stays until acknowledged and everything retries in order after the window.
				if (GlobalStateChannel.IsTestPartitionActive)
					throw new InvalidOperationException("Chaos partition active: the World Authority is unreachable");
				// Journal entries are absolute range writes, so two edits that overlap produce a different result
				// in a different order. A failed POST therefore retries in place - the head file stays until it is
				// acknowledged, and nothing behind it is attempted - so this node's own edits can never reorder
				// against the replicas. Entries left from a previous session sort first and drain the same way;
				// the WA deduplicates by operation id, so a crash between append and acknowledge is harmless.
				foreach ((long id, byte[] envelope) in m_outbox.Pending())
				{
					VoxelJournalCodec.Entry outbound = VoxelJournalCodec.Read(envelope);
					try
					{
						using ByteArrayContent content = new(VoxelJournalCodec.Write(outbound with { AuthorityGeneration = generation }));
						using HttpResponseMessage response = m_http.PostAsync(endpoint + "/cluster/voxel-journal/append", content).GetAwaiter().GetResult();
						response.EnsureSuccessStatusCode();
					}
					catch { m_headRetrying = true; throw; }
					m_outbox.Acknowledge(id);
					m_headRetrying = false;
				}
				m_headRetrying = false;
				Dictionary<string, long> tails = Tails(endpoint, generation);
				foreach (string storage in m_storages.Keys)
				{
					// Drain: keep fetching until the World Authority answers 204 (caught up) or the queue
					// is full. With the queue empty the fetch cursor restarts at the applied watermark.
					long applied = m_applied.GetValueOrDefault(storage);
					if (m_inbound.IsEmpty || !m_fetched.TryGetValue(storage, out long cursor) || cursor < applied) m_fetched[storage] = cursor = applied;
					// #1404: the tails table already says the World Authority holds nothing past the cursor - the
					// 204 a GET would get - so only storages with something new cost a request.
					if (tails != null && (!tails.TryGetValue(storage, out long tail) || tail <= cursor))
					{
						NoteDrained(storage, cursor);
						continue;
					}
					int fetched = 0;
					while (fetched < MaxFetchPerPass && m_inbound.Count < MaxInboundQueue)
					{
						using HttpResponseMessage response = m_http.GetAsync(endpoint + "/cluster/voxel-journal?storage=" + Uri.EscapeDataString(storage)
							+ "&after=" + cursor.ToString(CultureInfo.InvariantCulture)).GetAwaiter().GetResult();
						if (response.StatusCode == HttpStatusCode.NoContent)
						{
							NoteDrained(storage, cursor);
							break;
						}
						response.EnsureSuccessStatusCode();
						VoxelJournalCodec.Entry entry = VoxelJournalCodec.Read(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
						if (entry.AuthorityGeneration != generation) throw new InvalidDataException("Stale voxel journal generation");
						m_inbound.Enqueue(entry);
						m_fetched[storage] = cursor = entry.Sequence;
						fetched++;
					}
				}
				// Report how far this node has applied each journal. The WA truncates only below the slowest
				// live replica's report, so a failed report never risks anything - it just delays truncation.
				//
				// T-0028: STOP once the gateway says this node's epoch is closed. A failure here does not
				// advance m_reportedApplied, which is right for a transient error - the next pass retries -
				// but a CLOSED EPOCH is permanent: RegistryNodeEpoch is set once per process at registration
				// and the gateway never reopens it, so every later POST returns 409 for the life of the
				// process and the retry can only spin. This loop wakes every 250 ms, so a retired node that
				// lingers after its drain hammers the gateway at ~3-4 rejected POSTs a second until it exits:
				// measured 1999 rejections over 10m45s (node-2, epoch 19, 2026-09-03 18:43-18:54) and 780
				// over 5m22s (node-5, 06:50), together 97% of every control-request failure the gateway
				// logged that day. Dropping the report is safe for exactly the reason stated above - it only
				// delays truncation, and a node whose epoch is closed is not a live replica holding it back.
				if (!m_replicaEpochClosed)
				{
					foreach (KeyValuePair<string, long> watermark in m_applied)
					{
						if (m_reportedApplied.TryGetValue(watermark.Key, out long reported) && reported >= watermark.Value) continue;
						// T-0364: a skipped entry is not an applied one - a watermark that advanced by skipping must
						// not let the World Authority truncate entries this node may still have to replay.
						if (m_skippedStorages.ContainsKey(watermark.Key) || m_replaying.ContainsKey(watermark.Key)) continue;
						try
						{
							using JsonDocument ignored = Post("/registry/voxel-replicas?storage=" + Uri.EscapeDataString(watermark.Key)
								+ "&node=" + Uri.EscapeDataString(m_node)
								+ "&epoch=" + ClusterRuntime.RegistryNodeEpoch.ToString(CultureInfo.InvariantCulture)
								+ "&applied=" + watermark.Value.ToString(CultureInfo.InvariantCulture));
							m_reportedApplied[watermark.Key] = watermark.Value;
						}
						catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
						{
							// Log ONCE, not four times a second: the whole point of the storm was that the
							// node kept saying the same thing. Everything else still retries as before.
							m_replicaEpochClosed = true;
							Console.WriteLine("[ClusterPlugin] voxel_replica reporting_stopped=true reason=epoch_closed"
								+ " node=" + m_node + " epoch=" + ClusterRuntime.RegistryNodeEpoch.ToString(CultureInfo.InvariantCulture)
								+ " error=" + SecretRedactor.Redact(exception.Message));
						}
						catch (Exception exception)
						{
							Console.WriteLine("[ClusterPlugin] voxel_replica report_failed=true error=" + SecretRedactor.Redact(exception.Message));
						}
					}
				}
			}
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] voxel_journal sync_failed=true error=" + SecretRedactor.Redact(exception.Message));
			}
			Thread.Sleep(250);
		}
	}

	/// <summary>
	/// #1404: the World Authority's tails table (storage to highest held sequence), from the cache while it answers 304;
	/// null when it has no tails route, and the sync pass then asks per storage as before.
	/// </summary>
	private Dictionary<string, long> Tails(string endpoint, long generation)
	{
		if (m_tailsEndpoint == endpoint && Environment.TickCount64 < m_tailsRetryTick) return null;
		bool known = m_tails != null && m_tailsEndpoint == endpoint && m_tailsGeneration == generation;
		using HttpResponseMessage response = m_http.GetAsync(endpoint + "/cluster/voxel-journal-tails"
			+ (known ? "?version=" + Uri.EscapeDataString(m_tailsVersionSeen) : string.Empty)).GetAwaiter().GetResult();
		if (known && response.StatusCode == HttpStatusCode.NotModified) return m_tails;
		m_tails = null;
		m_tailsEndpoint = endpoint;
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			if (m_tailsRetryTick == 0)
				Console.WriteLine("[ClusterPlugin] voxel_journal tails_route=false endpoint=" + endpoint + " (per-storage polling)");
			m_tailsRetryTick = Environment.TickCount64 + 60_000;
			return null;
		}
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(response.Content.ReadAsStream());
		JsonElement root = document.RootElement;
		// Mid-failover the registry and the WA may name different generations for a moment: poll per storage this pass,
		// where an entry of the wrong generation fails the pass exactly as it always did.
		if (root.GetProperty("generation").GetInt64() != generation) return null;
		Dictionary<string, long> tails = new(StringComparer.Ordinal);
		foreach (JsonProperty item in root.GetProperty("tails").EnumerateObject()) tails[item.Name] = item.Value.GetInt64();
		m_tailsGeneration = generation;
		m_tailsVersionSeen = root.GetProperty("version").GetString();
		m_tailsRetryTick = 0;
		return m_tails = tails;
	}

	/// <summary>The World Authority has no entry beyond <paramref name="tail"/> for this storage: this replica is at the tail.</summary>
	private void NoteDrained(string storage, long tail)
	{
		// T-0364: a replay ends when the applied watermark has reached the World Authority's tail.
		if (m_replaying.ContainsKey(storage) && m_applied.GetValueOrDefault(storage) >= tail) m_replaying.TryRemove(storage, out byte _);
		if (!m_drained.TryAdd(storage, 0)) return;
		// A summary at most once a minute, not a line per storage: a busy node catches up on thousands
		// of distinct storages (node-5: 3,416 lines in two hours, w32 2026-09-23) and nothing reads them.
		// catch_up_done still reports the state CaughtUp is gated on.
		DateTime now = DateTime.UtcNow;
		lock (m_drained)
		{
			m_drainedSinceLine++;
			if (now - m_drainedLineUtc < TimeSpan.FromMinutes(1)) return;
			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
				"[ClusterPlugin] voxel_replica caught_up=true storages={0} (+{1} since the last line) last={2} tail={3} applied_since_start={4} after_s={5:F1}",
				m_drained.Count, m_drainedSinceLine, storage, tail, Interlocked.Read(ref m_appliedSinceStart), (now - m_startedUtc).TotalSeconds));
			m_drainedLineUtc = now;
			m_drainedSinceLine = 0;
		}
	}

	private DateTime m_drainedLineUtc = DateTime.MinValue;
	private int m_drainedSinceLine;

	/// <summary>
	/// True once every planet storage this node has loaded has been fetched to the World Authority's tail
	/// at least once and nothing fetched is still waiting to be applied. True on the World Authority
	/// itself, which has no replica to catch up, and on a world with no voxel storage at all; false
	/// while the first sweep of the ready session has not run yet.
	/// </summary>
	public bool CaughtUp
	{
		get
		{
			if (ClusterRuntime.IsWorldAuthority) return true;
			if (!m_inbound.IsEmpty) return false;
			// No storage after a full sweep of a ready session: the world has no planet and no
			// asteroid, so there is no replica to catch up (the packaged Cluster-Simple world).
			// Before that first sweep an empty set only means nothing is known yet.
			if (m_storages.IsEmpty) return Volatile.Read(ref m_sweptReadySession);
			return m_storages.Keys.All(m_drained.ContainsKey);
		}
	}

	/// <summary>
	/// Blocks (off the game thread) until <see cref="CaughtUp"/> or the deadline; logs either outcome.
	/// T-0053: a node that starts serving partitions before its voxel replica holds the edits under
	/// them places imported grids against un-edited terrain, and they fall through to the core. The
	/// wait is bounded and fails open - a node that cannot reach the World Authority still serves.
	/// </summary>
	public void WaitForCatchUp(TimeSpan timeout)
	{
		if (ClusterRuntime.IsWorldAuthority) return;
		DateTime deadline = DateTime.UtcNow + timeout;
		Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] voxel_replica catch_up_wait=true timeout_s={0:F0}", timeout.TotalSeconds));
		while (!CaughtUp && DateTime.UtcNow < deadline) Thread.Sleep(250);
		string behind = string.Join(",", m_storages.Keys.Where(storage => !m_drained.ContainsKey(storage)));
		Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] voxel_replica catch_up_done=true caught_up={0} queued={1} behind={2} applied_since_start={3} after_s={4:F1}",
			CaughtUp, m_inbound.Count, behind.Length == 0 ? "-" : behind, Interlocked.Read(ref m_appliedSinceStart),
			(DateTime.UtcNow - m_startedUtc).TotalSeconds));
	}

	private (string Endpoint, long Generation) Authority()
	{
		using JsonDocument authority = ClusterRuntime.Get(m_http, m_gateway + "/registry/world-authority");
		JsonElement root = authority.RootElement;
		string endpoint = root.GetProperty("ControlEndpoint").GetString();
		long generation = root.GetProperty("Generation").GetInt64();
		if (string.IsNullOrWhiteSpace(endpoint) || generation <= 0) throw new InvalidOperationException("World Authority is unavailable");
		ClusterRuntime.SetAuthorityGeneration(generation);
		return (endpoint.TrimEnd('/'), generation);
	}

	private void WriteFile(VoxelJournalCodec.Entry entry)
	{
		string map = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.StorageName)));
		string directory = Path.Combine(m_root, map);
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, entry.Sequence.ToString("D20", CultureInfo.InvariantCulture) + ".vjr");
		string temporary = path + ".tmp";
		using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			stream.Write(VoxelJournalCodec.Write(entry));
			stream.Flush(true);
		}
		File.Move(temporary, path, overwrite: true);
	}

	private void DeleteFile(VoxelJournalCodec.Entry entry)
	{
		string map = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.StorageName)));
		File.Delete(Path.Combine(m_root, map, entry.Sequence.ToString("D20", CultureInfo.InvariantCulture) + ".vjr"));
	}

	/// <summary>
	/// Restores the durable operation-id set and returns the open append stream. Runs after <see cref="Load"/>:
	/// the retained journal files re-add their own ids there, and the log then contributes the truncated ones.
	/// A torn 16-byte tail is ignored - if the append it belonged to completed, its journal file already carries
	/// the id; if not, the operation was never acknowledged and will be re-sent and re-recorded.
	/// </summary>
	private FileStream LoadOperationLog(string path)
	{
		if (File.Exists(path))
		{
			byte[] bytes = File.ReadAllBytes(path);
			for (int offset = 0; offset + 16 <= bytes.Length; offset += 16)
				m_operations.Add(new Guid(bytes.AsSpan(offset, 16)));
		}
		return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
	}

	private void Load()
	{
		foreach (string path in Directory.EnumerateFiles(m_root, "*.vjr", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.Ordinal))
		{
			VoxelJournalCodec.Entry entry = VoxelJournalCodec.Read(File.ReadAllBytes(path));
			if (!m_entries.TryGetValue(entry.StorageName, out SortedDictionary<long, VoxelJournalCodec.Entry> entries))
				m_entries[entry.StorageName] = entries = new();
			if (!entries.TryAdd(entry.Sequence, entry)) throw new InvalidDataException("Duplicate voxel journal sequence");
			if (!m_operations.Add(entry.OperationId)) throw new InvalidDataException("Duplicate voxel journal operation");
		}
		foreach (VoxelJournalCodec.Entry entry in m_entries.OrderBy(value => value.Key, StringComparer.Ordinal).SelectMany(value => value.Value.Values))
			m_inbound.Enqueue(entry);
	}

	private void LoadCatalogBases()
	{
		using JsonDocument catalog = ClusterRuntime.Get(m_http, m_gateway + "/registry/voxel-bases");
		foreach (JsonElement item in catalog.RootElement.EnumerateArray().GroupBy(value => value.GetProperty("StorageName").GetString(), StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(value => value.GetProperty("BaseVersion").GetInt64()).First()))
		{
			string storage = item.GetProperty("StorageName").GetString();
			m_bases[storage] = new BaseState(item.GetProperty("BaseVersion").GetInt64(), item.GetProperty("TailSequence").GetInt64(),
				item.GetProperty("Location").GetString(), item.GetProperty("SizeBytes").GetInt64(), item.GetProperty("Checksum").GetString());
			if (ClusterRuntime.IsWorldAuthority) m_pendingWatermarks[storage] = 0;
		}
	}

	private sealed record BaseState(long BaseVersion, long TailSequence, string Path, long SizeBytes, string Checksum);
}
