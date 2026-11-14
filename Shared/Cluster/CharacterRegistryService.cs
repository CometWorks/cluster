using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// WorldAuthority.md §5.5: cluster-wide player-character uniqueness and the character settings of record.
/// </summary>
/// <remarks>
/// Character existence used to be an emergent property of the join and handover code paths, and the Phase 3
/// flight found what that costs: a rejoining player whose body was on another node got a second one, and both
/// then existed forever, because every path that "spawns if no character is found" only ever looked node-local.
/// The registry makes existence arbitrated and audited instead.
///
/// Invariant (Partitioning.md §10 invariant 7): a logged-in player has at most one character instance, on at
/// most one node. Nodes report the player-character bodies they host once a second; the World Authority folds
/// the reports into a live-instance map, and any second body for an identity is despawned by registry order
/// with an alarm - the registered instance always wins, so the repair can never delete the body a player is
/// actually wearing.
///
/// The live-instance map is soft state, rebuilt from those reports after a WA restart (§9, §10). The settings
/// of record are durable and travel in the WA global save.
///
/// Toolbars and the rest of the per-PLAYER settings are absent from here because they belong to the player
/// rather than to the body: <see cref="PlayerProfileService"/> is their record. (They are NOT client-only
/// state - <c>MySession.GetCheckpoint</c> fills <c>AllPlayersData[pid].Toolbar</c> from
/// <c>MySession.Static.Toolbars</c> one line after <c>MyPlayer.GetObjectBuilder</c>.)
///
/// ponytail: one report, one merge, one snapshot - the same shape as PlayerStateService and PcuLedgerService
/// next door, rather than a new transport for a new subsystem.
/// </remarks>
public static class CharacterRegistryService
{
	private const string Subsystem = "characters";
	private const int MaxEntries = 4096;
	private const double ReportExpiryMilliseconds = 30000;

	private static readonly object Sync = new object();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	// WA state.
	private static readonly Dictionary<string, StampedReport> ReportsByOrigin = new(StringComparer.Ordinal);
	private static readonly Dictionary<long, Instance> Live = new();
	private static readonly Dictionary<long, Settings> Record = new();
	private static long m_spawnGeneration;

	// Node state.
	private static Snapshot m_cluster = new Snapshot(Array.Empty<Instance>(), Array.Empty<DespawnOrder>(), Array.Empty<Settings>());
	private static string m_snapshot = "";
	private static string m_pending;
	private static double m_nextReport;
	private static readonly HashSet<long> Seeded = new();
	// Settling is not a one-shot. A character's components finish initialising over several ticks
	// after the body first appears, and the game turns the radio broadcaster on as part of that - so
	// a default applied on first sight is silently overwritten, at random, depending on which tick
	// won. Re-apply until the body reads back what §5.5 asked for, or the window closes.
	private const double SettleWindowMilliseconds = 10000;
	private static readonly Dictionary<long, SettleState> Settling = new();

	private const double SettleStableMilliseconds = 3000;
	private sealed record SettleState(Settings Stored, double Until, double StableSince = 0);
	private static readonly List<string> Alarms = new();

	private sealed record StampedReport(double Received, Entry[] Entries);

	public sealed record Settings(long IdentityId, bool Helmet, bool Flashlight, bool Radio, bool Jetpack,
		bool Dampeners, string Model, float Health, float Energy, float Oxygen);
	/// <param name="Current">The body is its identity's current character (MyIdentity.Character) on the reporting node:
	/// the one the player controls. Defaults false, so a report from an older build elects as before.</param>
	public sealed record Entry(long IdentityId, long EntityId, ulong PartitionId, bool Dead, bool Online, Settings Settings,
		bool Current = false);
	public sealed record Report(string Origin, Entry[] Entries);
	public sealed record Instance(long IdentityId, string State, long CharacterEntityId, string OwningNode,
		ulong PartitionId, long SpawnGeneration);
	public sealed record DespawnOrder(string Node, long EntityId, long IdentityId, string Reason);
	public sealed record Snapshot(Instance[] Instances, DespawnOrder[] Despawn, Settings[] Record);

	internal static void Install()
	{
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ReceiveSnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, () => { lock (Sync) return m_snapshot; }, ReceiveSnapshot);
	}

	public static void Update()
	{
		// Settle BEFORE reporting: a just-spawned returning body carries fresh-spawn defaults
		// until ApplyPending restores its settings of record, and a report captured first sends
		// those defaults to the WA, whose merge overwrites the stored record with them (the
		// §5.5 relog restore then has nothing left to restore). Settling first means the report
		// only ever carries the post-restore state.
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		ApplyPending();
		if (ClusterRuntime.LocalMilliseconds >= m_nextReport)
		{
			m_nextReport = ClusterRuntime.LocalMilliseconds + 1000;
			string payload = JsonSerializer.Serialize(new Report(Node, CaptureLocal()));
			if (ClusterRuntime.IsWorldAuthority) ReceiveReport(Node, payload);
			else GlobalStateChannel.SendRequest(Subsystem, payload);
		}
	}

	// -- node half ---------------------------------------------------------

	/// <summary>
	/// Every player-character body this node hosts, keyed by the identity that OWNS it.
	/// </summary>
	/// <remarks>
	/// <c>SavedCharacters</c> first, and never <c>MyIdentity.Character</c> alone: an offline player's identity
	/// usually has no current character, so keying on that reads their only body as unowned - and the audit
	/// below would then delete an offline player's inventory.
	/// </remarks>
	private static Entry[] CaptureLocal()
	{
		MySession session = MySession.Static;
		Dictionary<long, long> owner = new Dictionary<long, long>();
		HashSet<long> current = new HashSet<long>();
		foreach (MyIdentity identity in session.Players.GetAllIdentities())
		{
			foreach (long saved in identity.SavedCharacters) owner[saved] = identity.IdentityId;
			if (identity.Character != null)
			{
				owner[identity.Character.EntityId] = identity.IdentityId;
				current.Add(identity.Character.EntityId);
			}
		}
		HashSet<long> online = session.Players.GetOnlinePlayers()
			.Where(player => player.Identity != null).Select(player => player.Identity.IdentityId).ToHashSet();
		// One member -> partition map for the whole capture: asking per character walked every partition
		// per character, which is O(characters x partitions) once a second on the game thread.
		Dictionary<long, ulong> holders = ClusterControlServer.StandingPartitionsOfEntities();
		// A body neither list names still belongs to somebody: its own owner module says which
		// identity (MyCharacter.GetPlayerIdentityId). That is the seated copy a grid save carries
		// after a fork - loaded with the ship on a node where the identity's SavedCharacters never
		// listed it and whose current body is elsewhere. Unreported, it was invisible to the
		// election, so the "duplicate" despawn that exists for exactly this never fired, and the
		// client kept reading "sitting" from a seat the server did not control (w29 2026-09-08
		// 05:15, pilot-10, T-0065). Reported under its owner, it loses the election to the
		// registered body and is despawned; a single body per identity is elected unchanged.
		long OwnerOf(MyCharacter character) => owner.TryGetValue(character.EntityId, out long known) ? known : character.GetPlayerIdentityId();
		// A seated body is not in MyEntities at all: MyCockpit.AttachPilot removes it (MyCockpit.cs:2200)
		// and only RemovePilot adds it back. Enumerating MyEntities alone therefore never reported a
		// pilot in a seat - the live player's while flying, and the ghost copy forever - which is
		// how the seated ghost stayed outside the election even once it had an owner. Walk the
		// cockpits too; a seated body's partition is its grid's (no standing-layout row of its own).
		IEnumerable<MyCharacter> seated = MyEntities.GetEntities().OfType<MyCubeGrid>()
			.SelectMany(grid => grid.GetFatBlocks<MyCockpit>())
			.Select(cockpit => cockpit.Pilot).Where(pilot => pilot != null);
		ulong PartitionOf(MyCharacter character) =>
			holders.TryGetValue(character.EntityId, out ulong holder) ? holder
			: character.GetTopMostParent() is { } top && top != character && holders.TryGetValue(top.EntityId, out ulong grid) ? grid
			: 0;
		return MyEntities.GetEntities().OfType<MyCharacter>().Concat(seated).Distinct()
			.Where(character => !character.MarkedForClose && OwnerOf(character) != 0)
			.Select(character => new Entry(OwnerOf(character), character.EntityId,
				PartitionOf(character), character.IsDead,
				online.Contains(OwnerOf(character)), Capture(OwnerOf(character), character),
				current.Contains(character.EntityId)))
			.OrderBy(entry => entry.IdentityId).ThenBy(entry => entry.EntityId).Take(MaxEntries).ToArray();
	}

	internal static Settings Capture(long identityId, MyCharacter character) => new Settings(
		identityId,
		character.OxygenComponent?.HelmetEnabled ?? false,
		character.LightEnabled,
		character.RadioBroadcaster?.Enabled ?? false,
		character.JetpackComp?.TurnedOn ?? false,
		character.JetpackComp?.DampenersTurnedOn ?? false,
		character.ModelName ?? string.Empty,
		character.StatComp?.HealthRatio ?? 1f,
		character.SuitEnergyLevel,
		character.OxygenComponent?.SuitOxygenLevel ?? 1f);

	/// <summary>
	/// Applies the replicated snapshot: carry out despawn orders addressed here, and settle a body the first
	/// time this node sees it - restoring the settings of record for a returning player, or applying the §5.5
	/// fresh-spawn defaults (helmet CLOSED, flashlight OFF, radio OFF) when the registry has no record.
	/// </summary>
	/// <remarks>
	/// Deliberately driven off "first time this node has seen this body" rather than a spawn seam: a body
	/// arrives here by spawn, by respawn, by handover import and by crash recovery, and the settle has to be
	/// the same in all four. It is idempotent, so a re-seen body costs one set lookup.
	/// </remarks>
	/// <summary>Last logged despawn result per entity, so an unchanged outcome is not re-raised
	/// every pass. Bounded by the despawn order set and pruned with it. See ApplyPending.</summary>
	private static readonly Dictionary<long, string> m_despawnLogged = new Dictionary<long, string>();

	private static void ApplyPending()
	{
		string payload;
		lock (Sync)
		{
			payload = m_pending;
			m_pending = null;
		}
		if (payload != null)
		{
			try
			{
				Snapshot snapshot = JsonSerializer.Deserialize<Snapshot>(payload);
				if (snapshot?.Instances != null && snapshot.Despawn != null && snapshot.Record != null
					&& snapshot.Instances.Length <= MaxEntries && snapshot.Despawn.Length <= MaxEntries)
					m_cluster = snapshot;
			}
			catch (Exception error) { Console.WriteLine("[ClusterPlugin] character registry snapshot rejected: " + error.Message); }
		}
		Snapshot cluster = m_cluster;
		// Drop remembered results for orders that are no longer present, so the table cannot
		// grow and a returning order is reported afresh rather than silently suppressed.
		if (m_despawnLogged.Count != 0)
		{
			HashSet<long> live = new HashSet<long>();
			foreach (DespawnOrder order in cluster.Despawn) live.Add(order.EntityId);
			foreach (long stale in m_despawnLogged.Keys.Where(id => !live.Contains(id)).ToArray())
				m_despawnLogged.Remove(stale);
		}
		foreach (DespawnOrder order in cluster.Despawn)
		{
			if (!string.Equals(order.Node, Node, StringComparison.Ordinal)) continue;
			object result = PlayerStateService.CloseUnclaimedCharacter(order.EntityId, force: true);
			// The CLOSE is still attempted every pass - it succeeds occasionally (139 times
			// against 346,439 failures on 2026-09-04), so giving up would lose those. Only the
			// ALARM is throttled, to the first attempt and to any change in the result.
			//
			// Unthrottled this raised ~32 lines PER SECOND, about 2.7 million a day, and was by
			// a wide margin the largest thing the cluster wrote - 48 identities at a median of
			// 6,152 lines each, one worst case at 29,105. Same shape as T-0028's voxel-replica
			// storm: a condition that does not change, retried at loop frequency, logged every
			// time. A log line that repeats unchanged thousands of times says nothing the first
			// one did not, and buries what else is in the file.
			string serialized = JsonSerializer.Serialize(result);
			if (!m_despawnLogged.TryGetValue(order.EntityId, out string previous) || previous != serialized)
			{
				m_despawnLogged[order.EntityId] = serialized;
				Raise($"duplicate_character_despawned identity={order.IdentityId} entity={order.EntityId} "
					+ $"reason={order.Reason} result={serialized}");
			}
		}
		Dictionary<long, Settings> record = cluster.Record.ToDictionary(entry => entry.IdentityId);
		foreach (MyCharacter character in MyEntities.GetEntities().OfType<MyCharacter>().ToArray())
		{
			// T-0392: a body whose sliced import activation is open is not "seen" yet - helmet, light and
			// broadcasting are round-trip fields; first-seen runs on the first pass after the guard.
			if (character.MarkedForClose || ImportActivationGuard.IsOpenFor(GuardUser.CharacterSeed, character) || !Seeded.Add(character.EntityId)) continue;
			long identityId = MySession.Static.Players.GetAllIdentities()
				.FirstOrDefault(identity => identity.Character?.EntityId == character.EntityId
					|| identity.SavedCharacters.Contains(character.EntityId))?.IdentityId ?? 0;
			if (identityId == 0) continue;
			// First-seen is the one moment the stored record is captured for this body; when a
			// returning player's restore misfires, the answer is HERE, not in the settle loop -
			// P2-CHR-03 failed two sweeps with restored=false and nothing recorded which link
			// (identity resolution, snapshot staleness, record absence) broke.
			Console.WriteLine($"[ClusterPlugin] character_seen entity={character.EntityId} name='{character.DisplayName}' identity={identityId} "
				+ $"recordFound={record.ContainsKey(identityId).ToString().ToLowerInvariant()} records={record.Count} "
				+ $"recordIdentities=[{string.Join(",", record.Keys)}]");
			Settling[character.EntityId] = new SettleState(record.GetValueOrDefault(identityId),
				ClusterRuntime.LocalMilliseconds + SettleWindowMilliseconds);
		}
		foreach (long entityId in Settling.Keys.ToArray())
		{
			SettleState state = Settling[entityId];
			if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyCharacter body
				|| body.MarkedForClose)
			{
				Settling.Remove(entityId);
				continue;
			}
			if (ImportActivationGuard.IsOpenFor(GuardUser.CharacterSettle, body)) continue;   // T-0392: settle after the sliced activation
			// Stability, not first agreement: the broadcaster's own initialisation can land AFTER the
			// values first match, and stopping there leaves the default overwritten. Require the body
			// to read back what it was asked for continuously, then stop - with the window as a cap so
			// a player toggling their own radio early is not fought forever.
			bool agrees = Settle(body, state.Stored);
			double now = ClusterRuntime.LocalMilliseconds;
			if (!agrees) Settling[entityId] = state with { StableSince = 0 };
			else if (state.StableSince == 0) Settling[entityId] = state with { StableSince = now };
			else if (now - state.StableSince >= SettleStableMilliseconds) Settling.Remove(entityId);
			if (Settling.TryGetValue(entityId, out SettleState current) && now >= current.Until)
				Settling.Remove(entityId);
		}
		Seeded.RemoveWhere(entityId => !MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity.MarkedForClose);
	}

	/// <summary>Applies the settings of record, returning true once the body already reads them back.</summary>
	private static bool Settle(MyCharacter character, Settings stored)
	{
		// A closed helmet is safe everywhere, so the fresh-spawn defaults are unconditional - never derived
		// from the spawn site's atmosphere (§5.5). A returning player's stored record overrides them.
		bool helmet = stored?.Helmet ?? true;
		bool light = stored?.Flashlight ?? false;
		bool radio = stored?.Radio ?? false;
		bool settled = true;
		if ((character.OxygenComponent?.HelmetEnabled ?? helmet) != helmet)
		{
			character.OxygenComponent.SwitchHelmet();
			settled = false;
		}
		if (character.LightEnabled != light)
		{
			character.EnableLights(light);
			settled = false;
		}
		// A broadcaster that does not exist yet is not a broadcaster that is off: keep settling until
		// the component is there, or the radio default lands on whichever tick happens to run first.
		if (character.RadioBroadcaster == null) settled = false;
		else if (character.RadioBroadcaster.Enabled != radio)
		{
			character.EnableBroadcasting(radio);
			settled = false;
		}
		if (settled)
			Console.WriteLine($"[ClusterPlugin] character_settled entity={character.EntityId} name='{character.DisplayName}' "
				+ $"restored={(stored != null).ToString().ToLowerInvariant()} helmet={helmet} light={light} radio={radio}");
		return settled;
	}

	// -- World Authority half ----------------------------------------------

	private static void ReceiveReport(string origin, string payload)
	{
		Report report = JsonSerializer.Deserialize<Report>(payload)
			?? throw new InvalidOperationException("Invalid character report");
		if (string.IsNullOrEmpty(report.Origin) || report.Origin.Length > 128 || report.Entries == null
			|| report.Entries.Length > MaxEntries || report.Entries.Any(entry => entry == null))
			throw new InvalidOperationException("Invalid character report");
		string snapshot;
		lock (Sync)
		{
			double now = ClusterRuntime.LocalMilliseconds;
			ReportsByOrigin[origin] = new StampedReport(now, report.Entries);
			foreach (string stale in ReportsByOrigin.Where(entry => now - entry.Value.Received >= ReportExpiryMilliseconds)
				.Select(entry => entry.Key).ToArray())
				ReportsByOrigin.Remove(stale);
			snapshot = Merge();
			if (snapshot == m_snapshot) return;
			m_snapshot = snapshot;
			m_pending = snapshot;
		}
		GlobalStateChannel.Publish(Subsystem, snapshot);
	}

	/// <summary>
	/// Folds every live report into the instance map, elects one body per identity, and orders the rest
	/// despawned. Election prefers the instance already registered - so a handover, which briefly has the
	/// body on neither node and then on the new one, re-elects the same entity id rather than treating the
	/// arrival as a duplicate of the departure.
	/// </summary>
	private static string Merge()
	{
		var byIdentity = new Dictionary<long, List<(string Node, Entry Entry)>>();
		foreach ((string origin, StampedReport live) in ReportsByOrigin.OrderBy(entry => entry.Key, StringComparer.Ordinal))
			foreach (Entry entry in live.Entries)
			{
				if (!byIdentity.TryGetValue(entry.IdentityId, out List<(string, Entry)> bodies))
					byIdentity[entry.IdentityId] = bodies = new List<(string, Entry)>();
				bodies.Add((origin, entry));
			}
		var despawn = new List<DespawnOrder>();
		foreach ((long identityId, List<(string Node, Entry Entry)> bodies) in byIdentity.OrderBy(pair => pair.Key))
		{
			Live.TryGetValue(identityId, out Instance registered);
			// The body the player controls wins over a merely registered one. Registered-first alone elected
			// a body recovered from a save over the one its player had just rejoined in: w32 2026-09-25 21:45,
			// pilot-85's old body 72057608080589363 came back with partition 251540, the rejoin at 21:57:53
			// spawned 72057608265050339, and every pass then ordered the LIVE body despawned (refused: "is
			// being controlled", "belongs to an online player") while the orphan stayed. Registered still
			// breaks ties, which keeps a handover (same entity id on both sides) re-electing the same body.
			List<(string Node, Entry Entry)> candidates = bodies.Where(body => body.Entry.Current).ToList();
			if (candidates.Count == 0) candidates = bodies;
			(string Node, Entry Entry) elected = candidates.FirstOrDefault(body =>
				registered != null && body.Entry.EntityId == registered.CharacterEntityId);
			if (elected.Entry == null)
				// Nothing registered survives: adopt the newest body. Ties break on the entity id so two
				// nodes reporting in either order elect the same one.
				elected = candidates.OrderByDescending(body => body.Entry.EntityId).First();
			Live[identityId] = new Instance(identityId,
				elected.Entry.Dead ? "Offline" : elected.Entry.Online ? "Live" : "Offline",
				elected.Entry.EntityId, elected.Node, elected.Entry.PartitionId,
				registered?.CharacterEntityId == elected.Entry.EntityId ? registered.SpawnGeneration : ++m_spawnGeneration);
			if (elected.Entry.Settings != null) Record[identityId] = elected.Entry.Settings;
			foreach ((string node, Entry entry) in bodies)
				if (entry.EntityId != elected.Entry.EntityId)
					despawn.Add(new DespawnOrder(node, entry.EntityId, identityId, "duplicate"));
		}
		foreach (long gone in Live.Keys.Where(identityId => !byIdentity.ContainsKey(identityId)).ToArray())
			Live[gone] = Live[gone] with { State = "Offline", OwningNode = null, CharacterEntityId = 0 };
		return JsonSerializer.Serialize(new Snapshot(
			Live.Values.OrderBy(instance => instance.IdentityId).ToArray(),
			despawn.ToArray(),
			Record.Values.OrderBy(settings => settings.IdentityId).ToArray()));
	}

	private static void ReceiveSnapshot(string payload)
	{
		if (payload == null || payload.Length > 8 * 1024 * 1024)
			throw new InvalidOperationException("Invalid character registry snapshot");
		lock (Sync) m_pending = payload;
	}

	private static void Raise(string message)
	{
		Console.WriteLine("[ClusterPlugin] ALARM " + message);
		lock (Sync)
		{
			Alarms.Add(message);
			if (Alarms.Count > 256) Alarms.RemoveAt(0);
		}
	}

	// -- durable settings of record ---------------------------------------

	/// <summary>The settings store as it goes into the WA global save (§9); the live map is not persisted.</summary>
	/// <summary>The registry's live instance for a character body, or null when it names none (node view of the WA snapshot).</summary>
	public static Instance LiveInstanceOf(long characterEntityId)
	{
		lock (Sync) return m_cluster.Instances.FirstOrDefault(instance => instance.CharacterEntityId == characterEntityId);
	}

	/// <summary>The registry's live body for an identity, or null when it has none (node view of the WA snapshot).</summary>
	public static Instance LiveBodyOfIdentity(long identityId)
	{
		lock (Sync)
			return m_cluster.Instances.FirstOrDefault(instance => instance.IdentityId == identityId
				&& instance.State == "Live" && instance.CharacterEntityId != 0);
	}

	/// <summary>SE-1487 forget player, World Authority: drops the identity's settings of record and registry instance.</summary>
	internal static bool Forget(long identityId)
	{
		lock (Sync) return Record.Remove(identityId) | Live.Remove(identityId);
	}

	public static string PersistedRecord()
	{
		lock (Sync) return JsonSerializer.Serialize(Record.Values.OrderBy(settings => settings.IdentityId).ToArray());
	}

	public static void RestoreRecord(string payload)
	{
		if (string.IsNullOrEmpty(payload)) return;
		Settings[] restored = JsonSerializer.Deserialize<Settings[]>(payload) ?? Array.Empty<Settings>();
		lock (Sync)
		{
			foreach (Settings settings in restored) Record[settings.IdentityId] = settings;
		}
		Console.WriteLine("[ClusterPlugin] character settings of record restored count=" + restored.Length);
	}

	// -- test/operator surface ---------------------------------------------

	public static object Status()
	{
		lock (Sync)
			return new
			{
				node = Node,
				isAuthority = ClusterRuntime.IsWorldAuthority,
				instances = m_cluster.Instances,
				settings = m_cluster.Record,
				pendingDespawn = m_cluster.Despawn,
				alarms = Alarms.ToArray(),
				// The audit result, straight out of the replicated map: any identity the cluster believes has
				// more than one body. Empty is invariant 7 holding.
				duplicates = m_cluster.Despawn.Select(order => order.IdentityId).Distinct().OrderBy(id => id).ToArray()
			};
	}
}
