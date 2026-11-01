using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Sandbox.Game.Replication;
using VRage;
using VRage.Network;
using VRage.Replication;

namespace ClusterPlugin;

public static class ReplicationNetworkIdPatch
{
	private sealed class ImportIds
	{
		public readonly string Type;
		public readonly Queue<uint> Ids;
		public ImportIds(string type, Queue<uint> ids) { Type = type; Ids = ids; }
	}

	private static readonly ConditionalWeakTable<MyReplicationServer, Dictionary<string, ImportIds>> Imports = new();

	// Network ids the import deliberately let go of, because the replicable they were captured
	// for no longer exists on the target (see Finalize). The client views transferred alongside
	// still name those ids: the adopt has to skip those entries rather than refuse the client,
	// which is what detached Reg04 on 2026-08-14 after this class stopped failing the import.
	private static readonly ConditionalWeakTable<MyReplicationServer, HashSet<uint>> DroppedIds = new();

	public static bool WasDropped(MyReplicationServer server, uint networkId) =>
		DroppedIds.GetOrCreateValue(server).Contains(networkId);

	public static string Export(MyReplicationServer server, HashSet<long> entityIds)
	{
		List<string> entries = new List<string>();
		HashSet<string> identities = new HashSet<string>();
		HashSet<IMyReplicable> visited = new HashSet<IMyReplicable>();
		server.m_replicables.IterateRoots(replicable => Walk(replicable, false));
		return string.Join(';', entries);

		void Walk(IMyReplicable replicable, bool included)
		{
			// The same object can be reachable both as a root and as another replicable's child
			// while it re-parents; visit it once.
			if (!visited.Add(replicable)) return;
			if (replicable is IMyEntityReplicable entity) included |= entityIds.Contains(entity.EntityId);
			if (included)
			{
				string identity = Identity(server, replicable, true);
				// A component that re-registers its event proxy leaves the dying replicable in the
				// tree beside its replacement: same identity, distinct objects. The wire format keys
				// by identity, so keep the first and skip the double - the copy that was dying anyway
				// simply gets a fresh network id on the target, exactly as vanilla treats any
				// replicable it has never seen (the drop container's maintenance panel aborted every
				// capture of partition 127, 2026-08-11).
				if (identity == null)
				{
					// Detached (T-0142), already reported once by IsDetached: nothing to transfer.
				}
				else if (!identities.Add(identity))
				{
					Console.WriteLine("[ClusterPlugin] duplicate replicable identity skipped in export: " + identity);
				}
				else
				{
					List<uint> ids = new List<uint> { server.GetNetworkIdByObject(replicable).Value };
					if (server.m_replicableGroups.TryGetValue(replicable, out List<IMyStateGroup> groups)) ids.AddRange(groups.Select(group => server.GetNetworkIdByObject(group).Value));
					entries.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(identity)) + ":" + replicable.GetType().AssemblyQualifiedName + ":" + string.Join(',', ids));
				}
			}
			foreach (IMyReplicable child in server.m_replicables.GetChildren(replicable)) Walk(child, included);
		}
	}

	public static void Prepare(MyReplicationServer server, string state)
	{
		Dictionary<string, ImportIds> imports = Imports.GetOrCreateValue(server);
		imports.Clear();
		DroppedIds.GetOrCreateValue(server).Clear();
		// One walk of the target's replicable tree, not one per transferred identity. The per-entry
		// walk was O(entries x replicables on the node): prepare_network_ids held the game thread for
		// 319 ms at p95 and 1674 ms at worst on w22 (2026-08-21) with ZERO entities created - the
		// second largest handover hold and the only one with no knob. Finalize below already walks
		// once and builds the same first-found map; Prepare now does the same.
		// T-0404 (import.prepareScope): the one walk still built the identity string of EVERY replicable on the node,
		// ~27 ms per transfer on r121i, flat in transfer size. An identity starts with its root's EntityId, so only the
		// roots the transfer names can match: "scoped" walks those; "full" is the whole tree; "check" builds both, keeps
		// the full map and logs the comparison (prepare_scope_check).
		string scope = ClusterConfig.Get("import.prepareScope");
		HashSet<long> roots = scope == "full" ? null : TransferredRoots(state);
		Dictionary<string, IMyReplicable> present = Present(server, roots);
		if (scope == "check") present = CheckScope(server, state, roots, present);
		foreach (string entry in state.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] pair = entry.Split(':', 3);
			if (pair.Length != 3) throw new InvalidDataException("Malformed transferred replicable identity");
			string identity = Encoding.UTF8.GetString(Convert.FromBase64String(pair[0]));
			string expectedType = pair[1];
			Queue<uint> ids = new Queue<uint>(pair[2].Split(',').Select(uint.Parse));
			// T-0029. These three conditions used to share one message, "Duplicate or invalid
			// transferred replicable identity", and that cost us the diagnosis: the incident was
			// read as a DUPLICATE for a day when the branch actually firing was the zero id. They
			// get three messages now, and only the genuinely malformed one is still fatal.
			if (ids.Count == 0) throw new InvalidDataException("Transferred replicable carries no network ids " + identity);
			if (imports.ContainsKey(identity))
			{
				// Export() already de-duplicates on this exact string, so a repeat here means a
				// wire from an exporter that did not. Take the same position Export does - keep
				// the first, skip the double - rather than failing the whole partition over it.
				Console.WriteLine("[ClusterPlugin] duplicate transferred replicable identity skipped in prepare: " + identity);
				continue;
			}
			if (ids.Any(id => id == 0))
			{
				// THE PRIMARY DEFECT OF T-0029, and the reason one abandoned wreck could block
				// every handover of partition 12445 for four and a half hours (2026-09-03 23:31).
				//
				// MyReplicationLayer.GetNetworkIdByObject returns NetworkId.Invalid for any object
				// that is not in m_objectToNetworkID, and NetworkId.Invalid is new NetworkId(0u).
				// So a replicable that is still in the replicable TREE but no longer registered
				// with the replication layer exports as id 0 - and refusing the import over it
				// took down the import of EVERY other entity in the partition. 202 failures in a
				// day, all naming the same entity, because the wreck stays in the partition and
				// is re-exported identically on every retry: a permanent, self-renewing block
				// that only a force-kill of the node cleared.
				//
				// A zero is not a usable id, so there is nothing to transfer. Do exactly what
				// Finalize() already does for a replicable that is missing on the target: give up
				// the ids, record them so the client-view adopt skips them (WasDropped), and let
				// the target mint its own - which is what vanilla does for any replicable it has
				// not seen. The whole entry goes, groups included, because the group ids are
				// positional and a partial drop would desynchronise the queue.
				Console.WriteLine("[ClusterPlugin] transferred replicable has an unregistered network id, "
					+ "keeping the target's own ids: " + identity);
				HashSet<uint> droppedIds = DroppedIds.GetOrCreateValue(server);
				foreach (uint id in ids) droppedIds.Add(id);
				continue;
			}
			present.TryGetValue(identity, out IMyReplicable existing);
			if (existing == null)
			{
				imports.Add(identity, new ImportIds(expectedType, ids));
				continue;
			}
			if (existing.GetType().AssemblyQualifiedName != expectedType) throw new InvalidOperationException("Transferred replicable type mismatch for " + identity);
			Reassign(server, existing, ids.Dequeue());
			if (!server.m_replicableGroups.TryGetValue(existing, out List<IMyStateGroup> existingGroups)) throw new InvalidOperationException("Transferred replicable has no target state groups " + identity);
			foreach (IMyStateGroup group in existingGroups)
			{
				if (ids.Count == 0) throw new InvalidOperationException("State-group count mismatch for existing replicable " + identity);
				Reassign(server, group, ids.Dequeue());
			}
			if (ids.Count != 0) throw new InvalidOperationException("State-group count mismatch for existing replicable " + identity);
		}
	}

	/// <summary>T-0404: the root EntityIds named by a transferred network state's identities (the text before the first '/').</summary>
	internal static HashSet<long> TransferredRoots(string state)
	{
		HashSet<long> roots = new HashSet<long>();
		foreach (string entry in state.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			int colon = entry.IndexOf(':');
			if (colon <= 0) continue;
			string identity;
			try { identity = Encoding.UTF8.GetString(Convert.FromBase64String(entry.Substring(0, colon))); }
			catch (FormatException) { continue; }   // the entry loop reports a malformed entry
			int slash = identity.IndexOf('/');
			if (long.TryParse(slash < 0 ? identity : identity.Substring(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out long root))
				roots.Add(root);
		}
		return roots;
	}

	/// <summary>The target's identity -> replicable map, first found in tree order; <paramref name="roots"/> null = every
	/// root, else only the trees whose root replicable has one of these EntityIds.</summary>
	private static Dictionary<string, IMyReplicable> Present(MyReplicationServer server, HashSet<long> roots)
	{
		Dictionary<string, IMyReplicable> present = new Dictionary<string, IMyReplicable>();
		server.m_replicables.IterateRoots(root =>
		{
			if (roots == null || root is IMyEntityReplicable entity && roots.Contains(entity.EntityId)) Walk(root);
		});
		return present;

		void Walk(IMyReplicable replicable)
		{
			// Same tolerance as Export: a re-registered event proxy leaves two objects with one
			// identity; the first found carries the transferred ids, the double keeps its own.
			string identity = Identity(server, replicable, false);
			if (identity != null && !present.TryAdd(identity, replicable))
				Console.WriteLine("[ClusterPlugin] duplicate target replicable identity skipped in prepare: " + identity);
			foreach (IMyReplicable child in server.m_replicables.GetChildren(replicable)) Walk(child);
		}
	}

	/// <summary>
	/// T-0404 equivalence guard (import.prepareScope=check): the full map is what Prepare uses; the scoped map must
	/// give every transferred identity the SAME replicable (or none). Two known positives:
	///  * kpForeign (always evaluable on a node with any other replicable): one identity of a root the transfer does
	///    NOT name, planted among the compared identities, must count as a mismatch (scoping really excludes it and the
	///    comparator sees it) - kpForeignMismatches &gt;= 1, "-" only on a node with no foreign identity at all.
	///  * kpDropped: the scoped map rebuilt without one root that has a present transferred identity must differ
	///    (kpMismatches &gt; 0); "-" when no transferred identity is present on the target (bench: always; live: kept copies).
	/// </summary>
	private static Dictionary<string, IMyReplicable> CheckScope(MyReplicationServer server, string state, HashSet<long> roots, Dictionary<string, IMyReplicable> scoped)
	{
		long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
		Dictionary<string, IMyReplicable> full = Present(server, null);
		double fullMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		List<string> identities = new List<string>();
		foreach (string entry in state.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			int colon = entry.IndexOf(':');
			if (colon <= 0) continue;
			try { identities.Add(Encoding.UTF8.GetString(Convert.FromBase64String(entry.Substring(0, colon)))); }
			catch (FormatException) { }
		}
		int matched = identities.Count(id => full.ContainsKey(id));
		int mismatches = Mismatches(identities, full, scoped);
		string kpRoot = "-", kpMismatches = "-";
		string probe = identities.FirstOrDefault(id => full.ContainsKey(id));
		if (probe != null)
		{
			int slash = probe.IndexOf('/');
			long dropped = long.Parse(slash < 0 ? probe : probe.Substring(0, slash), CultureInfo.InvariantCulture);
			HashSet<long> without = new HashSet<long>(roots);
			without.Remove(dropped);
			kpRoot = dropped.ToString(CultureInfo.InvariantCulture);
			kpMismatches = Mismatches(identities, full, Present(server, without)).ToString(CultureInfo.InvariantCulture);
		}
		string kpForeign = "-";
		string foreign = full.Keys.FirstOrDefault(id =>
		{
			int cut = id.IndexOf('/');
			return long.TryParse(cut < 0 ? id : id.Substring(0, cut), NumberStyles.Integer, CultureInfo.InvariantCulture, out long root) && !roots.Contains(root);
		});
		if (foreign != null) kpForeign = Mismatches(identities.Append(foreign), full, scoped).ToString(CultureInfo.InvariantCulture);
		// MyLog: the live readers parse the dated SpaceEngineersDedicated log, not the console.
		VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] prepare_scope_check entries=" + identities.Count + " roots=" + roots.Count
			+ " presentFull=" + full.Count + " presentScoped=" + scoped.Count + " matched=" + matched + " mismatches=" + mismatches
			+ " kpForeignMismatches=" + kpForeign + " kpRoot=" + kpRoot + " kpMismatches=" + kpMismatches
			+ " fullMs=" + fullMs.ToString("F1", CultureInfo.InvariantCulture));
		return full;
	}

	internal static int Mismatches<T>(IEnumerable<string> identities, Dictionary<string, T> full, Dictionary<string, T> scoped) where T : class
	{
		int count = 0;
		foreach (string identity in identities)
		{
			full.TryGetValue(identity, out T a);
			scoped.TryGetValue(identity, out T b);
			if (!ReferenceEquals(a, b)) count++;
		}
		return count;
	}

	public static void Finalize(MyReplicationServer server)
	{
		Dictionary<string, ImportIds> imports = Imports.GetOrCreateValue(server);
		if (imports.Count == 0) return;
		Dictionary<string, IMyReplicable> target = new Dictionary<string, IMyReplicable>();
		server.m_replicables.IterateRoots(Walk);
		void Walk(IMyReplicable replicable)
		{
			string identity = Identity(server, replicable, false);
			// Same tolerance as Export: the imported entity can register a component's event proxy
			// twice; the first found carries the transferred ids, the double mints its own.
			if (identity != null && imports.ContainsKey(identity) && !target.TryAdd(identity, replicable))
				Console.WriteLine("[ClusterPlugin] duplicate target replicable identity skipped in finalize: " + identity);
			foreach (IMyReplicable child in server.m_replicables.GetChildren(replicable)) Walk(child);
		}
		foreach ((string identity, ImportIds imported) in imports.ToArray())
		{
			if (!target.TryGetValue(identity, out IMyReplicable replicable))
			{
				// The replicable the ids were captured for is not on the target. A block
				// destroyed between the export and the import does this, and in a survival
				// world that is ordinary: a Visitor clipping a hillside loses a thruster,
				// and the thruster's MyTerminalReplicable goes with it. Refusing the whole
				// import over one of them takes the partition's handover down, retries it
				// forever, and - because the blocked handover is exactly the one that would
				// co-locate two converging partitions - defeats the invariant that keeps
				// interacting grids on a single node (2026-08-13/14: thrusters, a reactor
				// and a battery, on four separate partitions).
				//
				// So drop the transferred ids and let the target mint its own, which is what
				// vanilla does for any replicable it has not seen before. Export already
				// takes this position for the duplicate-identity case a few lines above.
				Console.WriteLine("[ClusterPlugin] transferred replicable missing on target, keeping the target's own ids: " + identity);
				HashSet<uint> dropped = DroppedIds.GetOrCreateValue(server);
				foreach (uint id in imported.Ids) dropped.Add(id);
				imports.Remove(identity);
				continue;
			}
			Apply(server, identity, imported, replicable);
			imports.Remove(identity);
		}
	}

	/// <summary>Finalize's reassignment of one transferred identity onto the target's replicable.</summary>
	private static void Apply(MyReplicationServer server, string identity, ImportIds imported, IMyReplicable replicable)
	{
		if (replicable.GetType().AssemblyQualifiedName != imported.Type) throw new InvalidOperationException("Transferred replicable type mismatch for " + identity);
		Queue<uint> ids = imported.Ids;
		Reassign(server, replicable, ids.Dequeue());
		if (!server.m_replicableGroups.TryGetValue(replicable, out List<IMyStateGroup> groups)) throw new InvalidOperationException("Transferred replicable has no target state groups " + identity);
		foreach (IMyStateGroup group in groups)
		{
			if (ids.Count == 0) throw new InvalidOperationException("State-group count mismatch for transferred replicable " + identity);
			Reassign(server, group, ids.Dequeue());
		}
		if (ids.Count != 0) throw new InvalidOperationException("State-group count mismatch for transferred replicable " + identity);
	}

	/// <summary>
	/// T-0392 (import.activatePerFrame): Finalize for the replicables under these entities only, and
	/// WITHOUT Finalize's drop. A sliced staged import Adds a partition over several frames; each slice
	/// must hand its entities their transferred ids before the frame ends, or the replication layer can
	/// stream them to a client on this node under a temporary id (Reassign does not tell a client).
	/// Finalize itself cannot run per slice: an identity it does not find is DROPPED, and the
	/// entities of later slices are not there yet. Here an identity not found simply stays pending;
	/// the full Finalize in the import's final frame resolves or drops whatever is left, as today.
	///
	/// Same walk shape as Export: the tree is traversed, but Identity (the costly part: a parent
	/// climb and base64 type names per replicable) is computed only under a replicable whose entity
	/// is in <paramref name="entityIds"/>, so a slice pays for its own entities, not the node's.
	/// Identities are applied in the import table's order, as Finalize does.
	/// </summary>
	public static void FinalizeEntities(MyReplicationServer server, HashSet<long> entityIds)
	{
		Dictionary<string, ImportIds> imports = Imports.GetOrCreateValue(server);
		if (imports.Count == 0 || entityIds.Count == 0) return;
		Dictionary<string, IMyReplicable> target = new Dictionary<string, IMyReplicable>();
		HashSet<IMyReplicable> visited = new HashSet<IMyReplicable>();
		server.m_replicables.IterateRoots(replicable => Walk(replicable, false));
		void Walk(IMyReplicable replicable, bool included)
		{
			if (!visited.Add(replicable)) return;
			if (replicable is IMyEntityReplicable entity) included |= entityIds.Contains(entity.EntityId);
			if (included)
			{
				string identity = Identity(server, replicable, false);
				if (identity != null && imports.ContainsKey(identity) && !target.TryAdd(identity, replicable))
					Console.WriteLine("[ClusterPlugin] duplicate target replicable identity skipped in finalize: " + identity);
			}
			foreach (IMyReplicable child in server.m_replicables.GetChildren(replicable)) Walk(child, included);
		}
		if (target.Count == 0) return;
		foreach ((string identity, ImportIds imported) in imports.ToArray())
		{
			if (!target.TryGetValue(identity, out IMyReplicable replicable)) continue;
			Apply(server, identity, imported, replicable);
			imports.Remove(identity);
		}
	}

	public static void Discard(MyReplicationServer server)
	{
		Imports.GetOrCreateValue(server).Clear();
		DroppedIds.GetOrCreateValue(server).Clear();
	}

	public static void RequireComplete(MyReplicationServer server)
	{
		int count = Imports.GetOrCreateValue(server).Count;
		if (count != 0) throw new InvalidOperationException("Transferred replicable hierarchy is incomplete: " + count + " identities unresolved");
	}

	internal static void Reassign(MyReplicationServer server, IMyNetObject obj, uint explicitId)
	{
		NetworkId requested = new NetworkId(explicitId);
		using (server.m_networkObjectLock.AcquireExclusiveUsing())
		{
			if (server.m_objectToNetworkID.TryGetValue(obj, out NetworkId current) && current.Equals(requested)) return;
			// Names the holder so a recurrence explains itself (T-0143: it used to be an id this node reissued).
			if (server.m_networkIDToObject.TryGetValue(requested, out IMyNetObject existing) && existing != obj)
				throw new InvalidOperationException("NetworkId " + requested + " belongs to a different replicated object: "
					+ existing.GetType().Name + (existing is IMyEntityReplicable entity ? "/" + entity.EntityId : "")
					+ (existing is IMyReplicable replicable ? " valid=" + replicable.IsValid : ""));
			if (server.m_objectToNetworkID.TryGetValue(obj, out NetworkId oldId)) server.m_networkIDToObject.Remove(oldId);
			server.m_objectToNetworkID[obj] = requested;
			server.m_networkIDToObject[requested] = obj;
		}
	}

	// T-0142. An event-proxy component replicable can outlive its component's attachment: the
	// component leaves its container (Instance.Entity becomes null) while the replicable stays in
	// the server's tree. Its GetParent() is MyExternalReplicable.FindByObject(Instance.Entity), a
	// dictionary lookup, so it throws ArgumentNullException('key') - and because the object never
	// leaves the tree, EVERY later walk on that node threw with it: on w31 (2026-09-13) six node
	// lifetimes across node-1/3/4 went from their first failure to their restart with 311 failed
	// prepare-imports and not one success.
	// Such a replicable has no identity to transfer and nothing to receive; skip it and its subtree.
	private static readonly ConditionalWeakTable<IMyReplicable, object> ReportedDetached = new();
	private static readonly object Reported = new object();

	internal static bool IsDetachedProxy(IMyReplicable replicable) =>
		replicable is MyEventProxyEntityComponentReplicable proxy && proxy.Instance?.Entity == null;

	/// <summary>GetParent() that answers null for a detached replicable instead of throwing.</summary>
	internal static IMyReplicable ParentOf(MyReplicationServer server, IMyReplicable replicable) =>
		IsDetached(server, replicable) ? null : replicable.GetParent();

	internal static bool IsDetached(MyReplicationServer server, IMyReplicable replicable)
	{
		if (!IsDetachedProxy(replicable)) return false;
		MyEventProxyEntityComponentReplicable proxy = (MyEventProxyEntityComponentReplicable)replicable;
		if (!ReportedDetached.TryGetValue(replicable, out _))
		{
			ReportedDetached.AddOrUpdate(replicable, Reported);
			// Once per object: which component, and whether the replication layer still has it
			// registered, name the vanilla path that left it behind.
			NetworkId id = server.GetNetworkIdByObject(replicable);
			Console.WriteLine("[ClusterPlugin] detached replicable skipped: type=" + replicable.GetType().Name
				+ " component=" + (proxy.Instance?.GetType().FullName ?? "null")
				+ " networkId=" + (id.IsInvalid ? "unregistered" : id.Value.ToString(CultureInfo.InvariantCulture))
				+ " valid=" + replicable.IsValid
				+ " children=" + server.m_replicables.GetChildren(replicable).Count);
		}
		return true;
	}

	private static string Identity(MyReplicationServer server, IMyReplicable replicable, bool failIfRootHasNoEntity)
	{
		List<IMyReplicable> path = new List<IMyReplicable>();
		IMyReplicable current = replicable;
		while (true)
		{
			if (IsDetached(server, current)) return null;
			IMyReplicable parent = current.GetParent();
			if (parent == null) break;
			path.Add(current);
			current = parent;
		}
		if (current is not IMyEntityReplicable root)
		{
			if (failIfRootHasNoEntity) throw new InvalidOperationException("Transferred replicable root has no EntityId: " + current.GetType().FullName);
			return null;
		}
		StringBuilder identity = new StringBuilder(root.EntityId.ToString(CultureInfo.InvariantCulture));
		for (int i = path.Count - 1; i >= 0; i--)
		{
			IMyReplicable child = path[i];
			identity.Append('/').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(child.GetType().AssemblyQualifiedName))).Append('.').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(StableChildIdentity(child))));
		}
		return identity.ToString();
	}

	private static string StableChildIdentity(IMyReplicable replicable)
	{
		string typeName = replicable.GetType().FullName;
		if (typeName == "Sandbox.Game.Replication.MyEventProxyEntityComponentReplicable")
		{
			// A block can carry several components of the SAME type, each with its own event
			// proxy: a Prototech Gyroscope holds three MyRotatingSubpartComponent instances,
			// one per rotating subpart. Their InstanceName is just the component type name,
			// so all of them collapsed to one identity and the export silently dropped all
			// but the first from the transferred network state - the client-view adopt then
			// aborted on the unmapped ids (KI-3, 2026-08-12 06:45). The component's own
			// subtype tells the instances apart; append it when there is one.
			string subtype = SubtypeOf(InstanceOf(replicable));
			string baseName = replicable.InstanceName ?? string.Empty;
			return subtype == null ? baseName : baseName + ":" + subtype;
		}
		if (typeName != "Sandbox.Game.Replication.MyInventoryReplicable") return replicable.InstanceName ?? string.Empty;
		object inventory = InstanceOf(replicable);
		object owner = inventory?.GetType().GetProperty("Owner")?.GetValue(inventory);
		if (owner == null) return replicable.InstanceName ?? string.Empty;
		object entityId = owner.GetType().GetProperty("EntityId")?.GetValue(owner);
		object inventoryIndex = inventory.GetType().GetProperty("InventoryIdx")?.GetValue(inventory);
		if (entityId != null && inventoryIndex != null) return entityId + ":" + inventoryIndex;
		return replicable.InstanceName ?? string.Empty;
	}

	private static object InstanceOf(IMyReplicable replicable)
	{
		for (Type type = replicable.GetType(); type != null; type = type.BaseType)
		{
			PropertyInfo instanceProperty = type.GetProperty("Instance",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
			if (instanceProperty != null) return instanceProperty.GetValue(replicable);
		}
		return null;
	}

	private static string SubtypeOf(object component)
	{
		if (component == null) return null;
		Type type = component.GetType();
		object value = type.GetProperty("SubtypeId", BindingFlags.Instance | BindingFlags.Public)?.GetValue(component)
			?? (object)type.GetField("SubtypeId", BindingFlags.Instance | BindingFlags.Public)?.GetValue(component)
			?? type.GetProperty("SubtypeName", BindingFlags.Instance | BindingFlags.Public)?.GetValue(component);
		string subtype = value?.ToString();
		return string.IsNullOrEmpty(subtype) ? null : subtype;
	}
}
