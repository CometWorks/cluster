using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// SE1-0252: admin safe zones (no block, <c>SafeZoneBlockId</c> 0) are World Authority state.
/// <para>
/// Every node loads the admin zones of the split world and keeps them (SE1-0251), but an admin who creates, edits or
/// deletes one in game changed only the node they were connected to, and that node's restart loaded the world file
/// again. Now a node forwards each such change to the World Authority, which keeps the set, persists it in the global
/// save and publishes it with a version that grows with each change. Every node creates, updates or closes its own
/// admin zones to match the newest set it has seen. The vanilla change runs first on the admin's node, so it shows at
/// once; a change the World Authority never takes is undone there by the next set.
/// </para>
/// <para>
/// The first World Authority of a cluster takes the set from its world file, which holds the same admin zones as every
/// node's. Any later one has it from its global save. The World Authority does not keep zone entities of its own in
/// step: nothing enforces a zone there, and <c>to-vanilla</c> takes the set from the global save. What a node forwards
/// are the vanilla admin paths (create, update, delete) and <see cref="MySessionComponentSafeZones.CrateSafeZone"/>,
/// which mods and the economy use as well; a zone some other code closes directly comes back with the next set.
/// </para>
/// </summary>
internal static partial class AdminSafeZones
{
	private const string Subsystem = "admin-safezones";
	private const int CommandTimeoutMilliseconds = 10000;

	internal sealed record Request(string Op, long Id, string Zone = null);

	/// <summary>A change in the outbox and the operation id every attempt at it is sent under (#1273).</summary>
	internal sealed record Queued(Request Request, Guid OperationId);

	internal static readonly object Gate = new object();

	// World Authority: entity id -> zone XML, null until restored from the global save or taken from the world file.
	private static SortedDictionary<long, string> s_zones;
	private static long s_version;

	// Regular node
	private static Set s_newest;
	private static (long Generation, long Version) s_applied;
	private static readonly List<Queued> Outbox = new List<Queued>();
	private static Task s_flushing = Task.CompletedTask;
	private static double s_nextFlush;
	private static bool s_applying;

	private static bool Active => ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority && Sync.IsServer;

	internal static void Install()
	{
		GlobalStateChannel.RegisterCommandHandler(Subsystem, Handle, durable: true);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, Offer);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, Offer);
	}

	internal static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		if (ClusterRuntime.IsWorldAuthority)
		{
			if (s_zones == null) TakeWorldFile();
			return;
		}
		Set newest;
		lock (Gate)
		{
			// A change of this node's own goes first; applying a set before it would undo it here for a moment.
			if (Outbox.Count > 0)
			{
				if (s_flushing.IsCompleted && ClusterRuntime.LocalMilliseconds >= s_nextFlush) s_flushing = Task.Run(Flush);
				return;
			}
			newest = s_newest;
		}
		if (newest != null && (newest.Generation, newest.Version).CompareTo(s_applied) > 0)
		{
			Apply(newest);
			s_applied = (newest.Generation, newest.Version);
		}
	}

	// -- regular node: hooks called by Shared.Patches.MySessionComponentSafeZonesPatch -----------------------------

	/// <summary>CrateSafeZone postfix: a new admin zone.</summary>
	internal static void Created(MyEntity zone)
	{
		if (Active && !s_applying && zone is MySafeZone { SafeZoneBlockId: 0 } created) Send("Set", created);
	}

	/// <summary>UpdateSafeZone postfix: an admin zone's settings, shape or pose changed.</summary>
	internal static void Updated(long zoneId)
	{
		if (Active && !s_applying && MyEntities.TryGetEntityById(zoneId, out MySafeZone zone) && zone.SafeZoneBlockId == 0 && !zone.MarkedForClose)
			Send("Set", zone);
	}

	/// <summary>DeleteSafeZone_Implementation prefix: an admin deletes an admin zone.</summary>
	internal static void Deleting(long zoneId)
	{
		if (Active && !s_applying && MyEntities.TryGetEntityById(zoneId, out MySafeZone zone) && zone.SafeZoneBlockId == 0)
			Send("Remove", zone);
	}

	private static void Send(string op, MySafeZone zone)
	{
		var request = new Request(op, zone.EntityId, op == "Set" ? ToXml((MyObjectBuilder_SafeZone)zone.GetObjectBuilder()) : null);
		lock (Gate) Outbox.Add(new Queued(request, Guid.NewGuid()));
		Console.WriteLine("[ClusterPlugin] admin safe zone " + op + " id=" + zone.EntityId + " queued for the World Authority");
	}

	/// <summary>
	/// Background: the queued changes in order. One the World Authority could not be reached for is sent again under the
	/// operation id it was queued with, so if the first attempt was applied and only its answer lost, the World
	/// Authority's command ledger answers from that attempt (#1273). A new id would apply the change a second time, after
	/// a newer change from another node, and bring back a zone that node had deleted. One the World Authority refused is
	/// dropped, and the next set undoes it here.
	/// </summary>
	private static void Flush()
	{
		if (FlushOutbox(Outbox, (request, operationId) =>
				GlobalStateChannel.SendCommand(Subsystem, JsonSerializer.Serialize(request), operationId, CommandTimeoutMilliseconds), Offer))
			lock (Gate) s_nextFlush = ClusterRuntime.LocalMilliseconds + 5000;
	}

	/// <summary>Sends the outbox's changes in order; true when the World Authority could not be reached and the head stays queued.</summary>
	internal static bool FlushOutbox(List<Queued> outbox, Func<Request, Guid, string> send, Action<string> offer)
	{
		while (true)
		{
			Queued next;
			lock (Gate)
			{
				if (outbox.Count == 0) return false;
				next = outbox[0];
			}
			string result = null;
			try { result = send(next.Request, next.OperationId); }
			catch (Exception error) when (error is TimeoutException || error.Message.Contains("World Authority") || error.Message.Contains("node link"))
			{
				Console.WriteLine("[ClusterPlugin] admin safe zone " + next.Request.Op + " id=" + next.Request.Id + " deferred: "
					+ SecretRedactor.Redact(error.Message));
				return true;
			}
			catch (Exception error)
			{
				Console.WriteLine("[ClusterPlugin] admin safe zone " + next.Request.Op + " id=" + next.Request.Id + " refused by the World Authority: "
					+ SecretRedactor.Redact(error.Message));
			}
			// The answer is the set right after the change: no publish that crossed it on the way can undo it here. It is
			// offered while the change is still queued, which keeps Update from applying an older set in between (#1273).
			lock (Gate)
			{
				offer(result);
				outbox.RemoveAt(0);
			}
		}
	}

	/// <summary>A set from a publish, a snapshot or a command's answer; only the newest is kept.</summary>
	private static void Offer(string payload)
	{
		if (string.IsNullOrEmpty(payload)) return;
		Set set = ReadSet(payload);
		lock (Gate)
			if (s_newest == null || (set.Generation, set.Version).CompareTo((s_newest.Generation, s_newest.Version)) > 0) s_newest = set;
	}

	/// <summary>This node's admin zones made to match the set (game thread).</summary>
	private static void Apply(Set set)
	{
		var wanted = new Dictionary<long, (MyObjectBuilder_SafeZone Zone, string Xml)>();
		foreach (string xml in set.Zones)
		{
			MyObjectBuilder_SafeZone zone = FromXml(xml);
			wanted[zone.EntityId] = (zone, xml);
		}
		int created = 0, updated = 0, closed = 0;
		s_applying = true;
		try
		{
			foreach (MySafeZone zone in MyEntities.GetEntities().OfType<MySafeZone>()
				.Where(zone => zone.SafeZoneBlockId == 0 && !zone.MarkedForClose && !wanted.ContainsKey(zone.EntityId)).ToArray())
			{
				zone.Close();
				closed++;
			}
			foreach ((long id, (MyObjectBuilder_SafeZone zone, string xml)) in wanted)
			{
				if (!MyEntities.TryGetEntityById(id, out MyEntity entity))
				{
					MyEntities.CreateFromObjectBuilderAndAdd(zone, fadeIn: false);
					created++;
				}
				else if (entity is MySafeZone { SafeZoneBlockId: 0, MarkedForClose: false } local)
				{
					if (ToXml((MyObjectBuilder_SafeZone)local.GetObjectBuilder()) == xml) continue;
					MySessionComponentSafeZones.UpdateSafeZone(zone, sync: true);
					updated++;
				}
				else Console.WriteLine("[ClusterPlugin] admin safe zone " + id + " skipped: the entity id is taken by " + entity.GetType().Name);
			}
		}
		catch (Exception error)
		{
			// The next set tries again; a bad zone must not take the node down.
			Console.WriteLine("[ClusterPlugin] admin safe zones not applied: " + SecretRedactor.Redact(error.Message));
		}
		finally { s_applying = false; }
		if (created + updated + closed > 0)
			Console.WriteLine("[ClusterPlugin] admin safe zones applied generation=" + set.Generation + " version=" + set.Version
				+ " created=" + created + " updated=" + updated + " closed=" + closed);
	}

	// -- World Authority --------------------------------------------------------------------------------------------

	private static string Handle(string origin, string payload)
	{
		Request request = JsonSerializer.Deserialize<Request>(payload) ?? throw new InvalidOperationException("Invalid admin safe zone request");
		bool changed;
		lock (Gate)
		{
			if (s_zones == null) throw new InvalidOperationException("the World Authority has not loaded the admin safe zones yet");
			changed = Change(s_zones, request);
			if (changed)
			{
				s_version++;
				// Under the lock, so two changes cannot publish their sets in the opposite order.
				GlobalStateChannel.Publish(Subsystem, Snapshot());
			}
		}
		if (changed)
		{
			ClusterRuntime.RequestGlobalSave();
			Console.WriteLine("[ClusterPlugin] admin safe zone " + request.Op + " id=" + request.Id + " from " + origin + " version=" + s_version);
		}
		return Snapshot();
	}

	/// <summary>The request applied to the set; true when it changed it.</summary>
	internal static bool Change(SortedDictionary<long, string> zones, Request request)
	{
		switch (request.Op)
		{
			case "Set":
				MyObjectBuilder_SafeZone zone = FromXml(request.Zone ?? throw new InvalidOperationException("A set carries the zone"));
				if (zone.EntityId == 0 || zone.EntityId != request.Id || zone.SafeZoneBlockId != 0)
					throw new InvalidOperationException("Not an admin safe zone: " + request.Id);
				string xml = ToXml(zone);
				bool changed = !(zones.TryGetValue(zone.EntityId, out string current) && current == xml);
				zones[zone.EntityId] = xml;
				return changed;
			case "Remove":
				return zones.Remove(request.Id);
			default:
				throw new InvalidOperationException("Unknown admin safe zone operation " + request.Op);
		}
	}

	private static string Snapshot()
	{
		lock (Gate)
			return s_zones == null ? null : JsonSerializer.Serialize(new Set(ClusterRuntime.AuthorityGeneration, s_version, s_zones.Values.ToArray()));
	}

	/// <summary>The global save section, null until the set is known (the next World Authority then takes it from its world file).</summary>
	internal static string PersistedRecord() => Snapshot();

	/// <summary>World Authority load: the set from the global save, or null to take it from the world file once loaded.</summary>
	internal static void RestoreRecord(string record)
	{
		lock (Gate)
		{
			if (string.IsNullOrEmpty(record))
			{
				s_zones = null;
				return;
			}
			Set set = ReadSet(record);
			s_zones = new SortedDictionary<long, string>(set.Zones.ToDictionary(xml => FromXml(xml).EntityId));
			s_version = set.Version;
		}
		Console.WriteLine("[ClusterPlugin] admin safe zones restored from the global save: " + s_zones.Count + " zone(s), version " + s_version);
	}

	private static void TakeWorldFile()
	{
		var zones = new SortedDictionary<long, string>(MyEntities.GetEntities().OfType<MySafeZone>()
			.Where(zone => zone.SafeZoneBlockId == 0 && !zone.MarkedForClose)
			.ToDictionary(zone => zone.EntityId, zone => ToXml((MyObjectBuilder_SafeZone)zone.GetObjectBuilder())));
		lock (Gate)
		{
			s_zones = zones;
			s_version++;
			GlobalStateChannel.Publish(Subsystem, Snapshot());
		}
		ClusterRuntime.RequestGlobalSave();
		Console.WriteLine("[ClusterPlugin] admin safe zones taken from the world file: " + zones.Count + " zone(s)");
	}
}
