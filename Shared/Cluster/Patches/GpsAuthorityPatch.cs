using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ClusterPlugin;

public static class GpsAuthorityPatch
{
	private const string Subsystem = "gps";
	[ThreadStatic]
	private static bool m_applying;
	private static readonly object SnapshotSync = new object();
	private static string m_snapshot = GpsAuthorityCodec.WriteSnapshot(Array.Empty<GpsAuthorityEntry>());
	private static long m_snapshotGeneration;
	private static bool m_snapshotReady;
	private static double m_nextRefresh;
	private static bool m_dedupDone;
	// T-0236: an accepted change marks the table dirty; Update publishes it at most once per CoalesceMs. Publishing
	// inside every accepted change sent the WHOLE table per change: an 800-add burst was 800 multi-MB publishes.
	private const double CoalesceMs = 250;
	private static volatile bool m_dirty;
	private static double m_nextDirtyPublish;
	// #1405: the published table per identity, in identity order: its entries by hash, each with its JSON, and the
	// identity's joined JSON. A request prefix on the WA marks its identity, and a dirty publish re-reads only the marked
	// identities, serializing only entries that changed; every FullRefreshMs all identities are re-read the same way as
	// the safety net for changes no request prefix sees (DiscardOld, RemovePlayerGpss, container GPS colours). Each
	// Modify and each 1 s refresh used to read and serialize the whole table: 14.6 % of a core on the octahedron-1191d
	// WA's game thread.
	private const double FullRefreshMs = 5000;
	private static readonly SortedDictionary<long, (Dictionary<int, (GpsAuthorityEntry Entry, string Json)> ByHash, string Json)> m_published = new();
	private static readonly HashSet<long> m_dirtyIdentities = new();
	private static bool m_allDirty;
	private static readonly AccessTools.FieldRef<MyGpsCollection, Dictionary<long, Dictionary<int, MyGps>>> PlayerGpss =
		AccessTools.FieldRefAccess<MyGpsCollection, Dictionary<long, Dictionary<int, MyGps>>>("m_playerGpss");

	public static void Install()
	{
		MyGpsCollectionSeams.ClusterGpsRequestPrefix = SeamPrefix;
		MyGpsCollectionSeams.ClusterGpsAcceptedPostfix = AcceptedPostfix;
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ExecuteAtAuthority);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, CurrentSnapshot, ApplySnapshot);
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsWorldAuthority || MySession.Static?.Ready != true) return;
		double now = ClusterRuntime.LocalMilliseconds;
		bool full = now >= m_nextRefresh || m_snapshotGeneration != ClusterRuntime.AuthorityGeneration;
		if (!full && !(m_dirty && now >= m_nextDirtyPublish)) return;
		if (full) m_nextRefresh = now + FullRefreshMs;
		m_nextDirtyPublish = now + CoalesceMs;
		m_dirty = false;
		if (!m_dedupDone) { m_dedupDone = true; DeduplicateAtBoot(); full = true; }
		PublishIfChanged(full);
	}

	/// <summary>
	/// T-0236: a one-shot clean-up of duplicate GPS entries on the World Authority, before its first publish of a
	/// boot. Opt-in by CLUSTER_GPS_DEDUP_PATTERN (a regex on the entry name): per identity, entries whose name
	/// matches are grouped by name and all but one are deleted through the game's own delete. The regression
	/// harness added "A/B Pilot-NNN" at every login and never removed them (7,834 entries on w32, 72 pairs for
	/// one pilot), so the WA published a multi-MB table on every change. A saved file cannot be edited in place
	/// safely - the registry holds its checksum and replicas on other holders - so the clean-up runs here, at
	/// boot while no client is connected, and the next global save persists it. Unset = nothing happens.
	/// </summary>
	private static void DeduplicateAtBoot()
	{
		string pattern = Environment.GetEnvironmentVariable("CLUSTER_GPS_DEDUP_PATTERN");
		if (string.IsNullOrWhiteSpace(pattern)) return;
		Regex names;
		try { names = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
		catch (ArgumentException exception)
		{
			Console.WriteLine("[ClusterPlugin] gps dedup skipped: invalid CLUSTER_GPS_DEDUP_PATTERN: " + exception.Message);
			return;
		}
		var all = CurrentEntries();
		var duplicates = all.Where(x => x.Gps.Name != null && names.IsMatch(x.Gps.Name))
			.GroupBy(x => (x.IdentityId, x.Gps.Name))
			.SelectMany(group => group.OrderBy(x => x.Hash).Skip(1)).ToList();
		m_applying = true;
		try
		{
			foreach (var duplicate in duplicates)
				MySession.Static.Gpss.SendDeleteGpsRequest(duplicate.IdentityId, duplicate.Hash);
		}
		finally { m_applying = false; }
		Console.WriteLine("[ClusterPlugin] gps dedup at boot: pattern '" + pattern + "' deleted " + duplicates.Count
			+ " duplicate(s) of " + all.Count + " entries; " + CurrentEntries().Count + " remain (T-0236)");
	}

	public static bool AddPrefix(long identityId, MyGps gps, long entityId, bool playSound)
		=> Forward(new GpsAuthorityRequest("Add", From(gps, identityId, entityId), 0, playSound, 0));

	public static bool DeletePrefix(long identityId, int hash)
		=> Forward(new GpsAuthorityRequest("Delete", Empty(identityId), hash, false, 0));

	/// <summary>
	/// SE-1146: vanilla deletes a drop container's GPS (identity 0 = every identity) only on the node holding the drop, so the
	/// World Authority kept it and its next table put the marker back everywhere: a Strong Unknown Signal outlived its drop.
	/// </summary>
	public static bool DeleteContainerPrefix(long entityId, long identityId)
		=> Forward(new GpsAuthorityRequest("DeleteContainer", Empty(identityId) with { EntityId = entityId }, 0, false, 0));

	public static bool ModifyPrefix(long identityId, MyGps gps)
		=> Forward(new GpsAuthorityRequest("Modify", From(gps, identityId, gps.EntityId), gps.Hash, false, 0));

	public static bool ShowOnHudPrefix(long identityId, int hash, bool show)
		=> Forward(new GpsAuthorityRequest("ShowOnHud", Empty(identityId), hash, show, 0));

	public static bool AlwaysVisiblePrefix(long identityId, int hash, bool visible)
		=> Forward(new GpsAuthorityRequest("AlwaysVisible", Empty(identityId), hash, visible, 0));

	public static bool ColorPrefix(long identityId, int hash, Color color)
		=> Forward(new GpsAuthorityRequest("Color", Empty(identityId), hash, false, color.PackedValue));

	public static bool AddServerPrefix(MyGpsCollection.AddMsg msg)
	{
		long identity = MySession.Static.Players.TryGetIdentityId(VRage.Network.MyEventContext.Current.Sender.Value);
		return Forward(new GpsAuthorityRequest("Add", From(msg, identity), 0, msg.PlaySoundOnCreation, 0));
	}

	public static bool DeleteServerPrefix(int hash)
		=> DeletePrefix(MySession.Static.Players.TryGetIdentityId(VRage.Network.MyEventContext.Current.Sender.Value), hash);

	public static bool ModifyServerPrefix(MyGpsCollection.ModifyMsg msg)
		=> Forward(new GpsAuthorityRequest("Modify", From(msg, MySession.Static.Players.TryGetIdentityId(VRage.Network.MyEventContext.Current.Sender.Value)), msg.Hash, false, 0));

	public static bool ShowOnHudServerPrefix(int hash, bool show)
		=> ShowOnHudPrefix(MySession.Static.Players.TryGetIdentityId(VRage.Network.MyEventContext.Current.Sender.Value), hash, show);

	public static bool AlwaysVisibleServerPrefix(int hash, bool visible)
		=> AlwaysVisiblePrefix(MySession.Static.Players.TryGetIdentityId(VRage.Network.MyEventContext.Current.Sender.Value), hash, visible);

	public static bool ColorServerPrefix(int hash, Color color)
		=> ColorPrefix(MySession.Static.Players.TryGetIdentityId(VRage.Network.MyEventContext.Current.Sender.Value), hash, color);

	public static bool SeamPrefix(string operation, object first, object second, object third, object fourth)
	{
		switch (operation)
		{
			case "Add": return AddPrefix((long)first, (MyGps)second, (long)third, (bool)fourth);
			case "AddServer": return AddServerPrefix((MyGpsCollection.AddMsg)first);
			case "Delete": return DeletePrefix((long)first, (int)second);
			case "DeleteServer": return DeleteServerPrefix((int)first);
			case "DeleteContainer": return DeleteContainerPrefix((long)first, (long)second);
			case "Modify": return ModifyPrefix((long)first, (MyGps)second);
			case "ModifyServer": return ModifyServerPrefix((MyGpsCollection.ModifyMsg)first);
			case "ShowOnHud": return ShowOnHudPrefix((long)first, (int)second, (bool)third);
			case "ShowOnHudServer": return ShowOnHudServerPrefix((int)first, (bool)second);
			case "AlwaysVisible": return AlwaysVisiblePrefix((long)first, (int)second, (bool)third);
			case "AlwaysVisibleServer": return AlwaysVisibleServerPrefix((int)first, (bool)second);
			case "Color": return ColorPrefix((long)first, (int)second, (Color)third);
			case "ColorServer": return ColorServerPrefix((int)first, (Color)second);
			default: throw new ArgumentOutOfRangeException(nameof(operation));
		}
	}

	public static void AcceptedPostfix()
	{
		if (ClusterRuntime.IsWorldAuthority && !m_applying && MySession.Static?.Ready == true) m_dirty = true;
	}

	private static bool Forward(GpsAuthorityRequest request)
	{
		if (ClusterRuntime.IsWorldAuthority && !m_applying) MarkDirty(request.Entry.IdentityId);
		if (m_applying || !ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority) return true;
		if (!GlobalStateChannel.SendRequest(Subsystem, GpsAuthorityCodec.WriteRequest(request)))
		{
			Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=gps action=dropped");
			GlobalStateChannel.AuthorityChanged(ClusterRuntime.AuthorityGeneration);
		}
		return false;
	}

	private static void ExecuteAtAuthority(string origin, string payload)
	{
		GpsAuthorityRequest request = GpsAuthorityCodec.ReadRequest(payload);
		MySandboxGame.Static.Invoke(() => Execute(request), "ClusterPlugin World Authority GPS request");
	}

	private static void Execute(GpsAuthorityRequest request)
	{
		MyGpsCollection gpss = MySession.Static.Gpss;
		switch (request.Operation)
		{
			case "Add":
				MyGps added = ToGps(request.Entry);
				gpss.SendAddGpsRequest(request.Entry.IdentityId, ref added, request.Entry.EntityId, request.Value);
				break;
			case "Delete": gpss.SendDeleteGpsRequest(request.Entry.IdentityId, request.Hash); break;
			case "DeleteContainer": gpss.SendDeleteContainerGpsRequest(request.Entry.EntityId, request.Entry.IdentityId); break;
			case "Modify":
				MyGps existing = gpss.GetGps(request.Entry.IdentityId, request.Hash);
				if (existing == null || existing.IsLocal) { m_dirty = true; break; }
				Apply(request.Entry, existing);
				gpss.SendModifyGpsRequest(request.Entry.IdentityId, existing);
				break;
			case "ShowOnHud": gpss.SendChangeShowOnHudRequest(request.Entry.IdentityId, request.Hash, request.Value); break;
			case "AlwaysVisible": gpss.SendChangeAlwaysVisibleRequest(request.Entry.IdentityId, request.Hash, request.Value); break;
			case "Color": gpss.SendChangeColorRequest(request.Entry.IdentityId, request.Hash, new Color(request.Color)); break;
		}
	}

	private static void ApplySnapshot(string payload)
	{
		GpsAuthoritySnapshot snapshot = GpsAuthorityCodec.ReadSnapshot(payload);
		Dictionary<(long, int), GpsAuthorityEntry> wanted = snapshot.Entries.ToDictionary(x => (x.IdentityId, x.Hash));
		Dictionary<(long, int), (GpsAuthorityEntry Entry, MyGps Gps)> current = CurrentEntries().ToDictionary(x => (x.IdentityId, x.Hash), x => (x.Entry, x.Gps));
		m_applying = true;
		try
		{
			foreach (var item in current)
				if (!wanted.TryGetValue(item.Key, out GpsAuthorityEntry entry) || entry != item.Value.Entry)
					MySession.Static.Gpss.SendDeleteGpsRequest(item.Key.Item1, item.Key.Item2);
			foreach (var item in wanted)
				if (!current.TryGetValue(item.Key, out var existing) || existing.Entry != item.Value)
			{
				MyGps gps = ToGps(item.Value);
				MySession.Static.Gpss.SendAddGpsRequest(item.Key.Item1, ref gps, item.Value.EntityId, playSoundOnCreation: false);
			}
		}
		finally { m_applying = false; }
		// #1405: changes made while applying mark no identity; a WA that applied a table re-reads all of it.
		if (ClusterRuntime.IsWorldAuthority) { MarkDirty(0); m_dirty = true; }
	}

	/// <summary>#1405: identity 0 (a container GPS modified for every identity) marks the whole table.</summary>
	private static void MarkDirty(long identityId)
	{
		lock (m_dirtyIdentities)
		{
			if (identityId == 0) m_allDirty = true;
			else m_dirtyIdentities.Add(identityId);
		}
	}

	private static void PublishIfChanged(bool full)
	{
		bool changed = RefreshPublished(full);
		if (!changed && m_snapshotReady && m_snapshotGeneration == ClusterRuntime.AuthorityGeneration) return;
		string snapshot = GpsAuthorityCodec.WriteSnapshotFromParts(m_published.Values.Select(x => x.Json));
		lock (SnapshotSync)
		{
			if (m_snapshotReady && m_snapshotGeneration == ClusterRuntime.AuthorityGeneration && m_snapshot == snapshot) return;
			m_snapshotGeneration = ClusterRuntime.AuthorityGeneration;
			m_snapshot = snapshot;
			m_snapshotReady = true;
		}
		try { GlobalStateChannel.Publish(Subsystem, snapshot); }
		catch (Exception exception) { Console.WriteLine("[ClusterPlugin] global_state unavailable=true subsystem=gps error=" + SecretRedactor.Redact(exception.Message)); }
	}

	private static string CurrentSnapshot()
	{
		lock (SnapshotSync) return m_snapshotReady ? m_snapshot : null;
	}

	/// <summary>
	/// #1405: re-reads the marked identities (all of them when <paramref name="full"/>) into <see cref="m_published"/>;
	/// true when any identity's entries changed. A full pass first drops expired temporary entries, which SaveGpss did
	/// on every 1 s refresh before.
	/// </summary>
	private static bool RefreshPublished(bool full)
	{
		MyGpsCollection gpss = MySession.Static.Gpss;
		Dictionary<long, Dictionary<int, MyGps>> table = PlayerGpss(gpss);
		long[] identities;
		lock (m_dirtyIdentities)
		{
			full |= m_allDirty;
			identities = full ? null : m_dirtyIdentities.ToArray();
			m_dirtyIdentities.Clear();
			m_allDirty = false;
		}
		if (full)
		{
			gpss.DiscardOld();
			identities = table.Keys.Union(m_published.Keys).ToArray();
		}
		bool changed = false;
		foreach (long identityId in identities)
		{
			GpsAuthorityEntry[] entries = table.TryGetValue(identityId, out Dictionary<int, MyGps> gpsByHash)
				? gpsByHash.Values.Where(x => !x.IsLocal).Select(x => From(x, identityId, x.EntityId)).OrderBy(x => x.Hash).ToArray()
				: Array.Empty<GpsAuthorityEntry>();
			if (entries.Length == 0) { changed |= m_published.Remove(identityId); continue; }
			Dictionary<int, (GpsAuthorityEntry Entry, string Json)> previous = m_published.TryGetValue(identityId, out var published) ? published.ByHash : null;
			var byHash = new Dictionary<int, (GpsAuthorityEntry Entry, string Json)>(entries.Length);
			bool identityChanged = previous == null || previous.Count != entries.Length;
			foreach (GpsAuthorityEntry entry in entries)
			{
				if (previous != null && previous.TryGetValue(entry.Hash, out var known) && known.Entry == entry) byHash[entry.Hash] = known;
				else { byHash[entry.Hash] = (entry, GpsAuthorityCodec.WriteEntry(entry)); identityChanged = true; }
			}
			if (!identityChanged) continue;
			m_published[identityId] = (byHash, string.Join(",", entries.Select(x => byHash[x.Hash].Json)));
			changed = true;
		}
		return changed;
	}

	private static List<(long IdentityId, int Hash, GpsAuthorityEntry Entry, MyGps Gps)> CurrentEntries()
	{
		var checkpoint = new MyObjectBuilder_Checkpoint();
		MySession.Static.Gpss.SaveGpss(checkpoint);
		var result = new List<(long, int, GpsAuthorityEntry, MyGps)>();
		if (checkpoint.Gps == null) return result;
		foreach (long identityId in checkpoint.Gps.Dictionary.Keys)
		{
			var entries = new List<IMyGps>();
			MySession.Static.Gpss.GetGpsList(identityId, entries);
			foreach (MyGps gps in entries.Cast<MyGps>().Where(x => !x.IsLocal))
				result.Add((identityId, gps.Hash, From(gps, identityId, gps.EntityId), gps));
		}
		return result;
	}

	private static GpsAuthorityEntry Empty(long identityId) => new(identityId, 0, null, null, null, 0, 0, 0, true, false, false, 0, 0, false, false, 0, false);

	private static GpsAuthorityEntry From(MyGps gps, long identityId, long entityId) => new(identityId, gps.Hash, gps.Name, gps.DisplayName,
		gps.Description, gps.Coords.X, gps.Coords.Y, gps.Coords.Z, !gps.DiscardAt.HasValue, gps.ShowOnHud, gps.AlwaysVisible,
		gps.GPSColor.PackedValue, entityId, gps.IsContainerGPS, gps.IsObjective, gps.ContractId, gps.IsGlobalEncounterGPS);

	private static GpsAuthorityEntry From(MyGpsCollection.AddMsg msg, long identityId) => new(identityId, 0, msg.Name, msg.DisplayName,
		msg.Description, msg.Coords.X, msg.Coords.Y, msg.Coords.Z, msg.IsFinal, msg.ShowOnHud, msg.AlwaysVisible,
		msg.GPSColor.PackedValue, msg.EntityId, msg.IsContainerGPS, msg.IsObjective, msg.ContractId, msg.IsGlobalEncounterGPS);

	private static GpsAuthorityEntry From(MyGpsCollection.ModifyMsg msg, long identityId) => new(identityId, msg.Hash, msg.Name, null,
		msg.Description, msg.Coords.X, msg.Coords.Y, msg.Coords.Z, true, false, false, msg.GPSColor.PackedValue,
		msg.EntityId, false, false, msg.ContractId, false);

	private static MyGps ToGps(GpsAuthorityEntry entry)
	{
		var gps = new MyGps();
		Apply(entry, gps);
		gps.ShowOnHud = entry.ShowOnHud;
		gps.AlwaysVisible = entry.AlwaysVisible;
		gps.IsContainerGPS = entry.IsContainerGps;
		gps.IsObjective = entry.IsObjective;
		gps.IsGlobalEncounterGPS = entry.IsGlobalEncounterGps;
		if (entry.IsFinal) gps.DiscardAt = null;
		else gps.SetDiscardAt();
		gps.UpdateHash();
		return gps;
	}

	private static void Apply(GpsAuthorityEntry entry, MyGps gps)
	{
		gps.Name = entry.Name;
		gps.DisplayName = entry.DisplayName;
		gps.Description = entry.Description;
		gps.Coords = new Vector3D(entry.X, entry.Y, entry.Z);
		gps.GPSColor = new Color(entry.Color);
		gps.EntityId = entry.EntityId;
		gps.ContractId = entry.ContractId;
	}
}
