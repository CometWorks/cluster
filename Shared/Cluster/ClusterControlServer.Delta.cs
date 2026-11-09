using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// HandoverDelta.md: the partition is staged on the target BEFORE the freeze (presync) and the
/// freeze carries only a field-level patch. Source: /snapshot builds the members' builders on the
/// game thread without freezing, keeps the per-entity bytes as the BASELINE; a capture made with
/// ?presync=ID then byte-compares each frozen builder with the baseline and sends, per entity,
/// UNCHANGED or (changed paths + the new builder). Target: /presync pulls the snapshot and stages
/// dormant entities (the staged import's StageBegin, kept in m_presyncs); ImportDelta applies the
/// patch to those dormant entities through the tiers of DeltaClassifier - pose, the engine's own
/// MyComponentContainer.Deserialize, a scalar table - rebuilding an entity from its new builder
/// whenever a path is not coverable or an applier throws, then runs today's activate frame and
/// today's round-trip gate over the merged builders. Everything is live-switchable
/// (presync.enabled on the gateway document, delta.appliers on the nodes) and falls back to the
/// full staged import when the presync stage is missing (HTTP 424 -> the gateway re-captures full).
/// </summary>
internal sealed partial class ClusterControlServer
{

	// ---------------------------------------------------------------- source: baselines

	private sealed class Baseline
	{
		public Guid Id;
		public ulong Partition;
		public uint Generation;
		public DateTime Created;
		public long[] Order;
		public Dictionary<long, byte[]> Bytes = new Dictionary<long, byte[]>();
		public Dictionary<long, MyObjectBuilder_EntityBase> Builders = new Dictionary<long, MyObjectBuilder_EntityBase>();
	}

	private static readonly ConcurrentDictionary<Guid, Baseline> m_baselines = new ConcurrentDictionary<Guid, Baseline>();

	private sealed class SnapshotData
	{
		public PartitionManager.Partition Partition;
		public uint Generation;
		public MyObjectBuilder_EntityBase[] Builders;
	}

	/// <summary>Game thread: the members' builders, live, no freeze - what a periodic save serialises.</summary>
	private static SnapshotData SnapshotBuilders(ulong partitionId)
	{
		PartitionManager.Partition partition = m_partitions.Get(partitionId);
		if (partition.HasPending)
			throw new InvalidOperationException("Partition " + partitionId + " has members still initializing; snapshot refused");
		HashSet<long> members = partition.Members.ToHashSet();
		MyEntity[] entities = MyEntities.GetEntities().Where(entity => members.Contains(entity.EntityId)).ToArray();
		MyObjectBuilder_EntityBase[] builders = entities.Select(entity =>
		{
			entity.BeforeSave();
			MyObjectBuilder_EntityBase builder = entity.GetObjectBuilder();
			ClampFuelCapacity(entity, builder);
			ClampInventoryAmounts(entity, builder);
			return builder;
		}).ToArray();
		return new SnapshotData { Partition = partition, Generation = m_persistence.GetGeneration(partitionId), Builders = builders };
	}

	/// <summary>Handler thread: GET /cluster/snapshot?partition=P -> MPS1 file (kind 1) + X-Presync-Id.</summary>
	private static (byte[] File, Guid Id, int Entities) Snapshot(ulong partitionId)
	{
		SnapshotData data = InvokePhase("snapshot", null, partitionId, () => SnapshotBuilders(partitionId));
		MyObjectBuilder_EntityBase[] builders = data.Builders;
		byte[] body = PartitionSaveFile.SerializeEntities(builders);
		byte[] file = PartitionSaveFile.Write(data.Partition.Id, data.Generation, 1, 1, data.Partition.Sphere, data.Partition.Anchored,
			builders.Length, body, ClusterRuntime.CatalogClusterMilliseconds());
		Baseline baseline = new Baseline { Id = Guid.NewGuid(), Partition = partitionId, Generation = data.Generation, Created = DateTime.UtcNow, Order = builders.Select(x => x.EntityId).ToArray() };
		byte[][] each = new byte[builders.Length][];
		System.Threading.Tasks.Parallel.For(0, builders.Length, i => each[i] = PartitionSaveFile.SerializeEntities(new[] { builders[i] }));
		for (int i = 0; i < builders.Length; i++)
		{
			baseline.Bytes[builders[i].EntityId] = each[i];
			baseline.Builders[builders[i].EntityId] = builders[i];
		}
		m_baselines[baseline.Id] = baseline;
		SweepBaselines();
		return (file, baseline.Id, builders.Length);
	}

	private static void SweepBaselines()
	{
		TimeSpan ttl = TimeSpan.FromSeconds(Math.Max(10, ClusterConfig.GetInt("presync.ttlSeconds")));
		foreach (KeyValuePair<Guid, Baseline> pair in m_baselines)
			if (DateTime.UtcNow - pair.Value.Created > ttl) m_baselines.TryRemove(pair.Key, out _);
	}

	// ---------------------------------------------------------------- source: delta capture

	/// <summary>
	/// Handler thread, after Capture() froze the partition and built the full capture on the game
	/// thread: the delta frame for the same transfer - same format-2 framing, the sector replaced by
	/// a kind-3 delta body. The full capture stays on the transfer for a ?full=true retry.
	/// </summary>
	private static byte[] DeltaCapture(Transfer transfer, Baseline baseline, HandoverPhaseTimer timer)
	{
		Stopwatch wall = Stopwatch.StartNew();
		MyObjectBuilder_EntityBase[] builders = transfer.Builders;
		byte[][] bytes = new byte[builders.Length][];
		System.Threading.Tasks.Parallel.For(0, builders.Length, i => bytes[i] = PartitionSaveFile.SerializeEntities(new[] { builders[i] }));
		List<string>[] paths = new List<string>[builders.Length];
		bool[] changed = new bool[builders.Length];
		System.Threading.Tasks.Parallel.For(0, builders.Length, i =>
		{
			long id = builders[i].EntityId;
			if (baseline.Bytes.TryGetValue(id, out byte[] old) && old.AsSpan().SequenceEqual(bytes[i])) return;
			changed[i] = true;
			paths[i] = baseline.Builders.TryGetValue(id, out MyObjectBuilder_EntityBase oldBuilder)
				? ImportRoundTrip.DiffPaths(oldBuilder, builders[i])
				: new List<string> { "(new)" };
		});
		DeltaEntry[] entries = new DeltaEntry[builders.Length];
		for (int i = 0; i < builders.Length; i++)
			entries[i] = new DeltaEntry { Id = builders[i].EntityId, Changed = changed[i], Paths = paths[i], Bytes = changed[i] ? bytes[i] : null };
		byte[] deltaBody = DeltaCodec.Write(baseline.Id, entries);
		byte[] sector = PartitionSaveFile.Write(transfer.Partition, transfer.Generation, 1, DeltaCodec.Kind, transfer.Sphere, transfer.Anchored,
			builders.Length, deltaBody, ClusterRuntime.CatalogClusterMilliseconds());
		byte[] frame = FrameCapture(transfer.Clients, transfer.ClientStateBytes, transfer.NetworkStateBytes, sector);
		int changedCount = changed.Count(x => x);
		Log("handover_delta_capture transferId=" + transfer.Id + " partition=" + transfer.Partition + " presync=" + baseline.Id
			+ " entities=" + builders.Length + " unchanged=" + (builders.Length - changedCount) + " changed=" + changedCount
			+ " baselineAgeMs=" + (long)(DateTime.UtcNow - baseline.Created).TotalMilliseconds
			+ " deltaBytes=" + sector.Length + " fullBytes=" + transfer.Capture.Length
			+ " wallMs=" + wall.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));
		return frame;
	}

	/// <summary>Capture format 2 (multi-client) framing; shared by the full capture and the delta capture.</summary>
	private static byte[] FrameCapture(ulong[] clients, byte[][] clientState, byte[] networkState, byte[] sector)
	{
		using MemoryStream stream = new MemoryStream();
		using BinaryWriter writer = new BinaryWriter(stream);
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
		return stream.ToArray();
	}

	// ---------------------------------------------------------------- target: presync stages

	private sealed class PresyncStage
	{
		public Guid Id;
		public ulong Partition;
		public uint Generation;
		public DateTime Created;
		public StagePlan Plan;
		public long[] Order;
		public Dictionary<long, StagedEntity> Slots = new Dictionary<long, StagedEntity>();
		public Dictionary<long, MyObjectBuilder_EntityBase> Builders = new Dictionary<long, MyObjectBuilder_EntityBase>();
		public Dictionary<long, byte[]> Bytes = new Dictionary<long, byte[]>();
		public double StageWallMs;
	}

	private static readonly Dictionary<ulong, PresyncStage> m_presyncs = new Dictionary<ulong, PresyncStage>();

	private sealed class PresyncMissingException : Exception
	{
		public PresyncMissingException(string message) : base(message) { }
	}

	/// <summary>
	/// Handler thread: POST /cluster/presync?partition=P&source=host:port. Pulls the source's
	/// snapshot, stages dormant entities exactly as the staged import does, keeps them for the
	/// delta import. One stage per partition; a newer one replaces (and discards) the older.
	/// </summary>
	/// <summary>T-0321: N4 is a peer route; over a LAN prefix it needs the join token, which this call never sent.</summary>
	private static HttpClient Authorized(HttpClient client)
	{
		ClusterRuntime.AuthorizeRegistryClient(client);
		return client;
	}

	private static string Presync(ulong partitionId, string source)
	{
		Stopwatch wall = Stopwatch.StartNew();
		DiscardPresync(partitionId, "replaced");
		byte[] file;
		Guid presyncId;
		using (HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(30)))
		using (HttpResponseMessage response = Authorized(client).GetAsync("http://" + source + "/cluster/snapshot?partition=" + partitionId).GetAwaiter().GetResult())
		{
			if (!response.IsSuccessStatusCode)
				throw new InvalidOperationException("snapshot from " + source + " failed: " + (int)response.StatusCode + " " + response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
			file = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
			presyncId = Guid.Parse(response.Headers.GetValues("X-Presync-Id").First());
		}
		double fetchMs = wall.Elapsed.TotalMilliseconds;
		PartitionSaveFile.Parsed save = PartitionSaveFile.Read(file);
		PartitionSaveFile.ValidateEnvelope(save, partitionId, save.Generation);
		MyObjectBuilder_EntityBase[] entities = PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount);
		PresyncStage stage = new PresyncStage { Id = presyncId, Partition = partitionId, Generation = save.Generation, Created = DateTime.UtcNow, Order = entities.Select(x => x.EntityId).ToArray() };
		stage.Plan = new StagePlan { Workers = Math.Max(1, ClusterConfig.GetInt("import.stageWorkers")) };
		byte[][] bytes = new byte[entities.Length][];
		System.Threading.Tasks.Parallel.For(0, entities.Length, i => bytes[i] = PartitionSaveFile.SerializeEntities(new[] { entities[i] }));
		for (int i = 0; i < entities.Length; i++)
		{
			stage.Builders[entities[i].EntityId] = entities[i];
			stage.Bytes[entities[i].EntityId] = bytes[i];
		}
		Stopwatch staging = Stopwatch.StartNew();
		Invoke(() => { StageBegin(entities, stage.Plan); return 0; });
		try
		{
			if (stage.Plan.Pending > 0 && !stage.Plan.Done.Wait(TimeSpan.FromSeconds(120)))
				throw new TimeoutException("presync workers did not finish within 120 s");
			StagedEntity failed = stage.Plan.Staged.FirstOrDefault(x => x.Error != null);
			if (failed != null) throw new InvalidDataException("presync entity " + failed.Builder.EntityId + " could not be built: " + failed.Error.Message, failed.Error);
		}
		catch
		{
			Invoke(() => { StageAbandonPlan(stage.Plan); return 0; });
			throw;
		}
		// The dormant copies are built; the orchestrator can go back to parallel updates until the
		// delta import's own activate frame (DrainOutstandingEntityInitWork has nothing to wait for).
		Invoke(() => { ReleaseSerialUpdate(stage.Plan); return 0; });
		stage.StageWallMs = staging.Elapsed.TotalMilliseconds;
		foreach (StagedEntity slot in stage.Plan.Staged) stage.Slots[slot.Builder.EntityId] = slot;
		lock (m_presyncs)
		{
			if (m_presyncs.TryGetValue(partitionId, out PresyncStage older)) Invoke(() => { StageAbandonPlan(older.Plan); return 0; });
			m_presyncs[partitionId] = stage;
		}
		SweepPresyncs();
		Log("handover_presync partition=" + partitionId + " presync=" + presyncId + " source=" + source + " entities=" + entities.Length
			+ " staged=" + stage.Plan.Staged.Count + " kept=" + stage.Plan.Kept.Count + " bytes=" + file.Length
			+ " fetchMs=" + fetchMs.ToString("F1", CultureInfo.InvariantCulture)
			+ " stageWallMs=" + stage.StageWallMs.ToString("F1", CultureInfo.InvariantCulture)
			+ " stageInitMsMax=" + (stage.Plan.Staged.Count == 0 ? 0 : stage.Plan.Staged.Max(x => x.InitMs)).ToString("F1", CultureInfo.InvariantCulture)
			+ " totalWallMs=" + wall.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));
		return "{\"presyncId\":\"" + presyncId.ToString("D") + "\",\"partition\":" + partitionId + ",\"generation\":" + save.Generation
			+ ",\"entities\":" + entities.Length + ",\"staged\":" + stage.Plan.Staged.Count + ",\"kept\":" + stage.Plan.Kept.Count
			+ ",\"totalWallMs\":" + wall.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + "}";
	}

	/// <summary>
	/// Game thread: the staged import's classification + worker start for a builder set, incremental:
	/// only the slots added by THIS call are counted pending and started, so a plan can be grown
	/// (presync first, rebuilds at the delta import).
	/// </summary>
	private static void StageBegin(IEnumerable<MyObjectBuilder_EntityBase> builders, StagePlan plan)
	{
		List<StagedEntity> added = new List<StagedEntity>();
		foreach (MyObjectBuilder_EntityBase builder in builders)
		{
			if (MyEntities.TryGetEntityById(builder.EntityId, out _)) plan.Kept.Add(builder);
			else { StagedEntity slot = new StagedEntity { Builder = builder }; plan.Staged.Add(slot); added.Add(slot); }
		}
		if (plan.Workers <= 0 || added.Count == 0) return;
		ForceSerialUpdate(plan);
		Interlocked.Add(ref plan.Pending, added.Count);
		StartBuilders(added, plan);
	}

	/// <summary>Game thread: drop a plan's dormant copies (no transfer to fail).</summary>
	private static void StageAbandonPlan(StagePlan plan)
	{
		plan.Abandoned = true;
		ReleaseSerialUpdate(plan);
		if (plan.Workers > 0 && plan.Pending > 0)
		{
			Log("presync stage abandoned with " + plan.Pending + " workers still running; their dormant copies are dropped unregistered");
			return;
		}
		foreach (StagedEntity slot in plan.Staged) DiscardDormant(slot);
	}

	private static PresyncStage TakePresync(ulong partitionId, Guid presyncId)
	{
		lock (m_presyncs)
		{
			if (!m_presyncs.TryGetValue(partitionId, out PresyncStage stage)) return null;
			if (stage.Id != presyncId) return null;
			m_presyncs.Remove(partitionId);
			return stage;
		}
	}

	private static void DiscardPresync(ulong partitionId, string reason)
	{
		PresyncStage stage;
		lock (m_presyncs)
		{
			if (!m_presyncs.TryGetValue(partitionId, out stage)) return;
			m_presyncs.Remove(partitionId);
		}
		Invoke(() => { StageAbandonPlan(stage.Plan); return 0; });
		Log("presync stage discarded partition=" + partitionId + " presync=" + stage.Id + " reason=" + reason);
	}

	private static void SweepPresyncs()
	{
		TimeSpan ttl = TimeSpan.FromSeconds(Math.Max(10, ClusterConfig.GetInt("presync.ttlSeconds")));
		List<ulong> expired;
		lock (m_presyncs) expired = m_presyncs.Where(pair => DateTime.UtcNow - pair.Value.Created > ttl).Select(pair => pair.Key).ToList();
		foreach (ulong partition in expired) DiscardPresync(partition, "expired");
	}

	/// <summary>Any thread, on node close: nothing dormant survives the process anyway, but log it.</summary>
	internal static void DiscardAllPresyncs(string reason)
	{
		List<ulong> partitions;
		lock (m_presyncs) partitions = m_presyncs.Keys.ToList();
		foreach (ulong partition in partitions) DiscardPresync(partition, reason);
	}

	// ---------------------------------------------------------------- target: delta import

	/// <summary>
	/// Handler-thread driver of the delta import. Mirrors ImportStaged: parse, (rebuild what must be
	/// rebuilt, on workers), import_begin, import_activate (apply patches + today's activation),
	/// compare off-thread, import_finish.
	/// </summary>
	private static int ImportDelta(byte[] body, PartitionSaveFile.Parsed save, ulong partitionId, Guid? transferId, uint? generation)
	{
		Stopwatch wall = Stopwatch.StartNew();
		(Guid presyncId, DeltaEntry[] entries) = DeltaCodec.Parse(save.Body);
		PresyncStage stage = TakePresync(partitionId, presyncId);
		if (stage == null)
			throw new PresyncMissingException("No presync stage " + presyncId + " for partition " + partitionId + " on this node");
		StagePlan plan = stage.Plan;
		bool owned = false; // import_begin took the plan: from there ImportFail/StageAbandon own the cleanup
		HashSet<string> appliers = ClusterConfig.Get("delta.appliers").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToHashSet();
		try
		{
			// Merged builders in the source's order: the new builder for changed entities, the presync
			// one otherwise; entities that left the partition since the snapshot lose their copies.
			HashSet<long> present = entries.Select(x => x.Id).ToHashSet();
			List<StagedEntity> gone = plan.Staged.Where(slot => !present.Contains(slot.Builder.EntityId)).ToList();
			plan.Staged.RemoveAll(slot => !present.Contains(slot.Builder.EntityId));
			plan.Kept.RemoveAll(builder => !present.Contains(builder.EntityId));
			System.Threading.Tasks.Parallel.ForEach(entries.Where(x => x.Changed), entry =>
			{
				entry.Builder = PartitionSaveFile.DeserializeEntities(entry.Bytes, 1)[0];
				entry.Tier = DeltaClassifier.ClassifyEntity(entry.Paths, out _);
				if (entry.Paths.Count == 1 && entry.Paths[0] == "(new)") entry.Tier = DeltaClassifier.Tier.Rebuild;
				if (entry.Tier == DeltaClassifier.Tier.Pose && !appliers.Contains("pose")
					|| entry.Tier == DeltaClassifier.Tier.Component && !appliers.Contains("component")
					|| entry.Tier == DeltaClassifier.Tier.Scalar && !appliers.Contains("scalar"))
					entry.Tier = DeltaClassifier.Tier.Rebuild;
			});
			MyObjectBuilder_EntityBase[] merged = entries.Select(entry => entry.Changed ? entry.Builder
				: stage.Builders.TryGetValue(entry.Id, out MyObjectBuilder_EntityBase kept) ? kept
				: throw new InvalidDataException("Delta names entity " + entry.Id + " as unchanged but the presync stage does not hold it")).ToArray();
			// #1259: the gate's own copy of the merged builders, from the bytes that came over the wire. The
			// presync copies were staged from stage.Builders and the rebuilds are staged from entry.Builder,
			// and an entity may keep and mutate its builder (MyProjectorBase remaps its blueprint in place).
			MyObjectBuilder_EntityBase[] frozen = new MyObjectBuilder_EntityBase[entries.Length];
			System.Threading.Tasks.Parallel.For(0, entries.Length, index => frozen[index] = PartitionSaveFile.DeserializeEntities(
				entries[index].Changed ? entries[index].Bytes : stage.Bytes[entries[index].Id], 1)[0]);
			// The gate's source half and the byte fast path, built while the rebuilds run.
			System.Threading.Tasks.Task<ImportRoundTrip.PreparedSource> preparedSource = transferId.HasValue
				? System.Threading.Tasks.Task.Run(() => ImportRoundTrip.PrepareSource(frozen)) : null;
			byte[] mergedBody = PartitionSaveFile.SerializeEntities(frozen);
			// T-0147: the merged save record is built in place - the earlier Write + Read pair compressed
			// and decompressed the whole merged body only to obtain this record and its checksum. That
			// checksum is now over the UNCOMPRESSED merged body (it was over the LZ4 bytes); its only
			// reader is the idempotent-retry comparison in ImportBegin, which sees the same formula on
			// both sides.
			ImportContext context = new ImportContext
			{
				Save = new PartitionSaveFile.Parsed(partitionId, save.Generation, save.Sequence, 1, save.Sphere, save.Anchored, merged.Length, save.ClusterTime, PartitionSaveFile.BodyChecksum(mergedBody), mergedBody),
				ExpectedGeneration = generation ?? save.Generation, PartitionId = partitionId, Entities = merged,
			};
			PartitionSaveFile.ValidateEnvelope(context.Save, partitionId, context.ExpectedGeneration);

			// Rebuilds: discard the dormant copy, stage a fresh one from the new builder on a worker.
			// Entities that are new to the partition, or whose presync copy was Kept (present on this
			// node at presync time), go the same way - ImportActivate's appeared/kept rules decide.
			List<DeltaEntry> rebuilds = entries.Where(x => x.Changed && x.Tier == DeltaClassifier.Tier.Rebuild).ToList();
			List<DeltaEntry> patches = entries.Where(x => x.Changed && x.Tier != DeltaClassifier.Tier.Rebuild).ToList();
			Stopwatch rebuildWatch = Stopwatch.StartNew();
			bool begun = InvokePhase("import_begin", transferId, partitionId, () =>
			{
				if (!ImportBegin(context, transferId)) return false;
			ResolveGhostSeats(context);
				try
				{
					foreach (StagedEntity slot in gone) DiscardDormant(slot);
					List<MyObjectBuilder_EntityBase> fresh = new List<MyObjectBuilder_EntityBase>();
					foreach (DeltaEntry entry in rebuilds)
					{
						if (stage.Slots.TryGetValue(entry.Id, out StagedEntity old)) { DiscardDormant(old); plan.Staged.Remove(old); }
						plan.Kept.RemoveAll(b => b.EntityId == entry.Id);
						fresh.Add(entry.Builder);
					}
					foreach (DeltaEntry entry in patches)
					{
						// A patched entity the presync classified as Kept stays Kept with its new builder.
						int kept = plan.Kept.FindIndex(b => b.EntityId == entry.Id);
						if (kept >= 0) plan.Kept[kept] = entry.Builder;
					}
					plan.Done.Reset();
					StageBegin(fresh, plan);
					if (plan.Pending == 0) plan.Done.Set();
					ForceSerialUpdate(plan);
					owned = true;
				}
				catch { ImportFail(context); throw; }
				return true;
			});
			if (!begun) return context.Transfer.ImportCount;
			ImportComparison comparison = null;
			double compareMs = 0, applyMs = 0;
			int applied = 0, rebuiltLate = 0;
			try
			{
				if (plan.Workers > 0 && plan.Pending > 0 && !plan.Done.Wait(TimeSpan.FromSeconds(120)))
					throw new TimeoutException("Delta rebuild workers did not finish within 120 s (" + plan.Pending + " pending)");
				double rebuildMs = rebuildWatch.Elapsed.TotalMilliseconds;
				comparison = InvokePhase("import_activate", transferId, partitionId, () =>
				{
					try
					{
						HandoverPhaseTimer timer = HandoverPhaseTimer.Current;
						timer?.Step("import_apply");
						Stopwatch apply = Stopwatch.StartNew();
						foreach (DeltaEntry entry in patches)
						{
							if (!stage.Slots.TryGetValue(entry.Id, out StagedEntity slot) || slot.Entity == null) continue; // Kept: rules at activation
							try
							{
								DeltaAppliers.Apply(slot.Entity, stage.Builders[entry.Id], entry.Builder, entry.Paths, appliers);
								slot.Builder = entry.Builder;
								applied++;
							}
							catch (Exception e)
							{
								// The applier could not reproduce the new state: rebuild this one on the
								// game thread (the legacy construction path), as the kept rules would.
								Log("delta applier failed entity=" + entry.Id + " partition=" + partitionId + " tier=" + entry.Tier + ": " + e.Message + "; rebuilding");
								DiscardDormant(slot);
								slot.Builder = entry.Builder;
								slot.Init = null;
								StageOne(slot);
								if (slot.Error != null) throw new InvalidDataException("Delta entity " + entry.Id + " could not be rebuilt: " + slot.Error.Message, slot.Error);
								rebuiltLate++;
							}
						}
						applyMs = apply.Elapsed.TotalMilliseconds;
						return ImportActivate(context, plan);
					}
					catch { ImportFail(context); throw; }
				});
				Stopwatch compare = Stopwatch.StartNew();
				if (comparison != null)
				{
					ImportRoundTrip.PreparedSource prepared = null;
					if (preparedSource != null)
					{
						try { prepared = preparedSource.Result; }
						catch (Exception e) { Log("prepared source document failed, comparing the slow way: " + e.Message); }
					}
					try { CompareImportedState(context.Transfer, comparison.GatedSource, merged.Length, comparison.Imported, comparison.SourceBody, prepared); }
					catch { Invoke(() => { ImportFail(context); return 0; }); throw; }
				}
				compareMs = compare.Elapsed.TotalMilliseconds;
				int count = InvokePhase("import_finish", transferId, partitionId, () =>
				{
					try { return ImportFinish(context); }
					catch { ImportFail(context); throw; }
				});
				Dictionary<string, int> rebuildLeaves = new Dictionary<string, int>();
				// One full residual per distinct leaf (entity ids stripped, value kept, capped) so the
				// cause of a rebuild is readable from the log without reproducing it offline.
				Dictionary<string, string> rebuildSamples = new Dictionary<string, string>();
				foreach (DeltaEntry entry in rebuilds)
					foreach (string residual in entry.Paths.Where(p => DeltaClassifier.Classify(p) == DeltaClassifier.Tier.Rebuild))
					{
						string leaf = DeltaClassifier.Leaf(residual);
						if (!rebuildSamples.ContainsKey(leaf)) rebuildSamples[leaf] = DeltaAppliers.Shorten(residual, 160);
					}
				foreach (DeltaEntry entry in rebuilds)
					foreach (string path in entry.Paths.Where(p => DeltaClassifier.Classify(p) == DeltaClassifier.Tier.Rebuild).Select(DeltaClassifier.Leaf).Distinct())
						rebuildLeaves[path] = rebuildLeaves.TryGetValue(path, out int n) ? n + 1 : 1;
				Log("handover_delta transferId=" + (transferId?.ToString("D") ?? "-") + " partition=" + partitionId + " presync=" + presyncId
					+ " entities=" + entries.Length + " unchanged=" + entries.Count(x => !x.Changed) + " patched=" + applied
					+ " rebuilt=" + rebuilds.Count + " rebuiltLate=" + rebuiltLate + " gone=" + gone.Count
					+ " tiers=[" + string.Join(",", patches.GroupBy(x => x.Tier).Select(g => g.Key + "=" + g.Count())) + "]"
					+ " presyncAgeMs=" + (long)(DateTime.UtcNow - stage.Created).TotalMilliseconds
					+ " rebuildMs=" + rebuildMs.ToString("F1", CultureInfo.InvariantCulture)
					+ " applyMs=" + applyMs.ToString("F1", CultureInfo.InvariantCulture)
					+ " compareMs=" + compareMs.ToString("F1", CultureInfo.InvariantCulture)
					+ " totalWallMs=" + wall.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)
					+ (rebuildLeaves.Count == 0 ? "" : " rebuildLeaves=[" + string.Join(",", rebuildLeaves.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key + "=" + x.Value)) + "]"));
				foreach (KeyValuePair<string, int> leaf in rebuildLeaves.OrderByDescending(x => x.Value).Take(5))
					Log("handover_delta_rebuild_sample partition=" + partitionId + " leaf=" + leaf.Key + " n=" + leaf.Value + " residual=" + rebuildSamples[leaf.Key]);
				return count;
			}
			catch
			{
				try { Invoke(() => { StageAbandon(context, plan); return 0; }); }
				catch (Exception e) { Log("delta import cleanup failed transferId=" + transferId + " partition=" + partitionId + ": " + e.Message); }
				throw;
			}
		}
		catch (Exception) when (!owned)
		{
			// Failed before import_begin took ownership of the plan: the dormant copies are still ours.
			try { Invoke(() => { StageAbandonPlan(plan); return 0; }); } catch (Exception e) { Log("presync plan cleanup failed: " + e.Message); }
			throw;
		}
	}
}

/// <summary>
/// Game-thread appliers for a patched dormant entity (HandoverDelta.md §3). Each call reproduces
/// the new builder's state for the paths the classifier assigned to its tier; anything it cannot do
/// throws, and the caller rebuilds the entity. Runs BEFORE MyEntities.Add: no client sees the
/// intermediate state, and the engine's own Deserialize paths are what a fresh Init would run.
/// </summary>
internal static class DeltaAppliers
{
	internal static void Apply(MyEntity entity, MyObjectBuilder_EntityBase oldBuilder, MyObjectBuilder_EntityBase newBuilder, List<string> paths, HashSet<string> enabled)
	{
		bool pose = false, components = false;
		HashSet<long> blockComponents = new HashSet<long>();
		List<string> scalars = new List<string>();
		foreach (string residual in paths)
		{
			switch (DeltaClassifier.Classify(residual))
			{
				case DeltaClassifier.Tier.Pose: pose = true; break;
				case DeltaClassifier.Tier.Derived: break;
				case DeltaClassifier.Tier.Component:
					long block = BlockIdOf(residual);
					if (block == 0) components = true; else blockComponents.Add(block);
					break;
				case DeltaClassifier.Tier.Scalar: scalars.Add(residual); break;
				default: throw new InvalidOperationException("path is not appliable: " + residual);
			}
		}
		if (pose) ApplyPose(entity, newBuilder);
		if (components) ApplyComponents((VRage.Game.Components.MyComponentContainer)entity.Components, newBuilder.ComponentContainer, "entity " + entity.EntityId);
		if (blockComponents.Count > 0)
		{
			MyCubeGrid grid = entity as MyCubeGrid ?? throw new InvalidOperationException("block component on a non-grid");
			MyObjectBuilder_CubeGrid gridBuilder = (MyObjectBuilder_CubeGrid)newBuilder;
			foreach (long blockId in blockComponents)
			{
				MyObjectBuilder_CubeBlock blockBuilder = gridBuilder.CubeBlocks.FirstOrDefault(b => b.EntityId == blockId)
					?? throw new InvalidOperationException("block " + blockId + " missing from the new builder");
				MyCubeBlock block = FindBlock(grid, blockId);
				ApplyComponents((VRage.Game.Components.MyComponentContainer)block.Components, blockBuilder.ComponentContainer, "block " + blockId);
			}
		}
		foreach (string residual in scalars) ApplyScalar(entity, newBuilder, residual);
	}

	/// <summary>
	/// Re-applies component state to the EXISTING component instances only. The engine's own
	/// MyComponentContainer.Deserialize is an init path, not a re-apply: a missing or differently
	/// typed component is created and Add-ed (replacing what the block's Init wired up), and
	/// MyInventoryAggregate.Deserialize ADDS its child inventories again - a production block ended
	/// up with four inventories and InventoryIdx walked into GetInventoryBase(2) →
	/// InvalidBranchException on every import of partition 6458 (w22, 2026-08-21 20:42). So only
	/// components whose Deserialize is idempotent are re-applied in place (MyTimerComponent assigns
	/// fields; MyInventory.Init clears first); an aggregate is applied child by child in order;
	/// anything else throws and the entity is rebuilt.
	/// </summary>
	private static void ApplyComponents(VRage.Game.Components.MyComponentContainer container, MyObjectBuilder_ComponentContainer builder, string what)
	{
		if (builder?.Components == null) return;
		foreach (MyObjectBuilder_ComponentContainer.ComponentData data in builder.Components)
		{
			if (data.Component == null) continue;
			Type instanceType = data.Component is MyObjectBuilder_ModCustomComponent custom
				? VRage.Game.Components.MyComponentTypeFactory.Static.GetType(custom.ComponentType)
				: VRage.Game.Components.MyComponentFactory.Static.GetCreatedInstanceType(data.Component.TypeId);
			if (instanceType == null) throw new InvalidOperationException(what + ": unknown component " + data.TypeId);
			Type keyType = VRage.Game.Components.MyComponentTypeFactory.Static.GetType(data.TypeId);
			Type registered = VRage.Game.Components.MyComponentTypeFactory.Static.GetComponentType(instanceType);
			if (registered != null) keyType = registered;
			bool found = typeof(VRage.Game.Components.Interfaces.IMySubtypedComponent).IsAssignableFrom(keyType)
				? container.TryGet(keyType, VRage.Utils.MyStringHash.GetOrCompute(data.Component.SubtypeName), out VRage.Game.Components.MyComponentBase component)
				: container.TryGet(keyType, out component);
			if (!found) throw new InvalidOperationException(what + ": component " + data.TypeId + " is not on the live entity");
			if (component.GetType() != instanceType) throw new InvalidOperationException(what + ": component " + data.TypeId + " is a " + component.GetType().Name + " live, " + instanceType.Name + " in the builder");
			ApplyComponent(component, data.Component, what);
		}
	}

	private static void ApplyComponent(VRage.Game.Components.MyComponentBase component, MyObjectBuilder_ComponentBase builder, string what)
	{
		switch (component)
		{
			case Sandbox.Game.Entities.Inventory.MyInventoryAggregate aggregate:
			{
				MyObjectBuilder_InventoryAggregate aggregateBuilder = builder as MyObjectBuilder_InventoryAggregate
					?? throw new InvalidOperationException(what + ": aggregate builder is " + builder.GetType().Name);
				VRage.Collections.ListReader<VRage.Game.Components.MyComponentBase> children = aggregate.ChildList.Reader;
				int count = aggregateBuilder.Inventories?.Count ?? 0;
				if (count != children.Count) throw new InvalidOperationException(what + ": aggregate has " + children.Count + " inventories live, " + count + " in the builder");
				for (int i = 0; i < count; i++) ApplyComponent(children[i], aggregateBuilder.Inventories[i], what);
				return;
			}
			case Sandbox.Game.MyInventory inventory:
				if (builder is not MyObjectBuilder_Inventory inventoryBuilder) throw new InvalidOperationException(what + ": inventory builder is " + builder.GetType().Name);
				inventory.Init(inventoryBuilder);
				return;
			case Sandbox.Game.Components.MyUseObjectsComponent useObjects:
				// Deserialize only stores the builder; the detectors are built in OnAddedToScene,
				// which has not run for a dormant entity (and is ContainsKey-guarded anyway).
				if (builder is not MyObjectBuilder_UseObjectsComponent) throw new InvalidOperationException(what + ": use-objects builder is " + builder.GetType().Name);
				useObjects.Deserialize(builder);
				return;
			case Sandbox.Game.Components.MyTimerComponent timer:
				if (builder is not MyObjectBuilder_TimerComponent) throw new InvalidOperationException(what + ": timer builder is " + builder.GetType().Name);
				timer.Deserialize(builder);
				return;
			default:
				throw new InvalidOperationException(what + ": no in-place applier for " + component.GetType().Name);
		}
	}

	private static readonly System.Text.RegularExpressions.Regex EntityIdRef = new System.Text.RegularExpressions.Regex(@"#-?\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

	internal static string Shorten(string residual, int max)
	{
		string text = EntityIdRef.Replace(residual.Replace("MyObjectBuilder_Sector/SectorObjects/", ""), "");
		return text.Length <= max ? text : text.Substring(0, max) + "…";
	}

	private static readonly System.Text.RegularExpressions.Regex BlockRef = new System.Text.RegularExpressions.Regex(@"/CubeBlocks/MyObjectBuilder_CubeBlock#(-?\d+)/", System.Text.RegularExpressions.RegexOptions.Compiled);
	private static readonly System.Text.RegularExpressions.Regex PilotRef = new System.Text.RegularExpressions.Regex(@"/Pilot/", System.Text.RegularExpressions.RegexOptions.Compiled);

	private static long BlockIdOf(string residual)
	{
		System.Text.RegularExpressions.Match m = BlockRef.Match(residual);
		return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
	}

	private static MyCubeBlock FindBlock(MyCubeGrid grid, long blockId)
	{
		foreach (MyCubeBlock block in grid.GetFatBlocks()) if (block.EntityId == blockId) return block;
		throw new InvalidOperationException("block " + blockId + " not on grid " + grid.EntityId + " (armour blocks have no entity)");
	}

	private static void ApplyPose(MyEntity entity, MyObjectBuilder_EntityBase builder)
	{
		if (builder.PositionAndOrientation.HasValue)
		{
			MatrixD matrix = builder.PositionAndOrientation.Value.GetMatrix();
			entity.PositionComp.SetWorldMatrix(ref matrix, null, forceUpdate: true);
		}
		if (entity.Physics == null) return;
		switch (builder)
		{
			case MyObjectBuilder_CubeGrid grid:
				entity.Physics.LinearVelocity = grid.LinearVelocity;
				entity.Physics.AngularVelocity = grid.AngularVelocity;
				break;
			case MyObjectBuilder_Character character:
				entity.Physics.LinearVelocity = character.LinearVelocity;
				break;
			case MyObjectBuilder_FloatingObject:
				break;
		}
	}

	private static void ApplyScalar(MyEntity entity, MyObjectBuilder_EntityBase newBuilder, string residual)
	{
		int split = residual.IndexOf(": ", StringComparison.Ordinal);
		string path = split < 0 ? residual : residual.Substring(0, split);
		string leaf = DeltaClassifier.Leaf(path);
		long blockId = BlockIdOf(path);
		if (PilotRef.IsMatch(path)) throw new InvalidOperationException("pilot scalar not appliable yet: " + leaf);
		if (blockId != 0)
		{
			MyCubeGrid grid = entity as MyCubeGrid ?? throw new InvalidOperationException("block scalar on a non-grid");
			MyObjectBuilder_CubeBlock blockBuilder = ((MyObjectBuilder_CubeGrid)newBuilder).CubeBlocks.FirstOrDefault(b => b.EntityId == blockId)
				?? throw new InvalidOperationException("block " + blockId + " missing from the new builder");
			MyCubeBlock block = FindBlock(grid, blockId);
			ApplyBlockScalar(block, blockBuilder, leaf);
			return;
		}
		switch (entity, newBuilder, leaf)
		{
			case (MyCharacter character, MyObjectBuilder_Character cb, "MovementState"): character.SetCurrentMovementState(cb.MovementState); return;
			case (MyCharacter character, MyObjectBuilder_Character cb, "EnvironmentOxygenLevel"): character.EnvironmentOxygenLevelSync.SetLocalValue(cb.EnvironmentOxygenLevel); return;
			case (_, _, "DisplayName"): entity.DisplayName = newBuilder.Name; return;
			case (_, _, "Name"): entity.Name = newBuilder.Name; return;
		}
		throw new InvalidOperationException("entity scalar not appliable: " + leaf + " on " + entity.GetType().Name);
	}

	private static void ApplyBlockScalar(MyCubeBlock block, MyObjectBuilder_CubeBlock builder, string leaf)
	{
		switch (block, builder, leaf)
		{
			case (MyGasTank tank, MyObjectBuilder_GasTank gb, "FilledRatio"): ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(gb.FilledRatio, false); return;
			case (MyBatteryBlock battery, MyObjectBuilder_BatteryBlock bb, "CurrentStoredPower"): battery.CurrentStoredPower = bb.CurrentStoredPower; return;
			case (MyTerminalBlock terminal, MyObjectBuilder_TerminalBlock tb, "CustomName"): terminal.SetCustomName(tb.CustomName ?? string.Empty); return;
			case (MyTerminalBlock terminal, MyObjectBuilder_TerminalBlock tb, "ShowOnHUD"): terminal.ShowOnHUD = tb.ShowOnHUD; return;
			case (MyTerminalBlock terminal, MyObjectBuilder_TerminalBlock tb, "ShowInTerminal"): terminal.ShowInTerminal = tb.ShowInTerminal; return;
			case (MyTerminalBlock terminal, MyObjectBuilder_TerminalBlock tb, "ShowInToolbarConfig"): terminal.ShowInToolbarConfig = tb.ShowInToolbarConfig; return;
			case (MyTerminalBlock terminal, MyObjectBuilder_TerminalBlock tb, "ShowInInventory"): terminal.ShowInInventory = tb.ShowInInventory; return;
			case (MyFunctionalBlock functional, MyObjectBuilder_FunctionalBlock fb, "Enabled"): functional.Enabled = fb.Enabled; return;
		}
		throw new InvalidOperationException("block scalar not appliable: " + leaf + " on " + block.GetType().Name);
	}
}
