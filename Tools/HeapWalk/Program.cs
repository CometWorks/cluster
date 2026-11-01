using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

// T-0147: what keeps CLOSED MyCubeGrid instances (and their blocks) alive.
// Peeling: walls start as the closed grids. Each round:
//   L = reachable from the roots with the walls cut;  D = reachable without walls, minus L.
//   anchors = edges x -> y with x in L, y in D.
// An anchor whose SOURCE is grid-internal (a block, conveyor, grid system, replicable, sync field of
// the closed grid's own graph) is not an explanation - it is part of the dead grid - so every such
// source joins the walls and the round repeats. Stops when the anchors are external or after 8 rounds.
internal static class Program
{
	private static bool Internal(string t) =>
		t.StartsWith("Sandbox.Game.Entities.Cube.") || t.StartsWith("Sandbox.Game.GameSystems.")
		|| t.StartsWith("Sandbox.Game.Replication.") || t.StartsWith("VRage.Sync.")
		|| t.StartsWith("Sandbox.Game.EntityComponents.") || t.StartsWith("SpaceEngineers.Game.Entities.Blocks.")
		|| t.StartsWith("Sandbox.Game.Entities.Blocks.") || t.StartsWith("Entities.Blocks.")
		|| t == "Sandbox.Game.Entities.MyCubeGrid" || t.StartsWith("Sandbox.Game.Entities.My") && t.EndsWith("Block")
		|| t.StartsWith("System.Action") || t.StartsWith("System.Func")
		|| (t.StartsWith("Sandbox.Game.Entities.") && !t.Contains("MyEntities") && !t.Contains("Character"));

	private static int Main(string[] args)
	{
		using DataTarget target = DataTarget.LoadDump(args[0]);
		ClrRuntime runtime = target.ClrVersions[0].CreateRuntime();
		ClrHeap heap = runtime.Heap;
		if (args.Length > 1 && args[1] == "cut") return Cut(heap);
		if (args.Length > 1 && args[1] == "chars") return Characters(heap);
		if (args.Length > 1 && args[1] == "charpaths") return CharacterPaths(heap);
		if (args.Length > 1 && args[1] == "gridanchors") return GridAnchors(heap);
		if (args.Length > 1 && args[1] == "transfers")
		{
			// Bytes retained only through the ClusterControlServer transfer dictionaries, plus Transfer object census.
			var cutT = new HashSet<ulong>(); int transfers = 0, released = 0, discarded = 0;
			foreach (ClrObject o in heap.EnumerateObjects())
			{
				string t = o.Type?.Name ?? "";
				if (t == "System.Collections.Generic.Dictionary<System.Guid, ClusterPlugin.ClusterControlServer+Transfer>") cutT.Add(o.Address);
				else if (t == "ClusterPlugin.ClusterControlServer+Transfer")
				{
					transfers++;
					if (ReadBool(o, "Released")) released++;
					if (ReadBool(o, "Discarded")) discarded++;
				}
			}
			ulong[] rl = heap.EnumerateRoots().Select(r => r.Object.Address).Where(a => a != 0).Distinct().ToArray();
			ulong Reach2(HashSet<ulong> cs)
			{
				var seen = new HashSet<ulong>(1 << 24); var q = new Queue<ulong>(); ulong b = 0;
				foreach (ulong a in rl) if (!cs.Contains(a) && seen.Add(a)) q.Enqueue(a);
				while (q.Count > 0)
				{
					ClrObject o = heap.GetObject(q.Dequeue()); if (o.Type == null) continue; b += o.Size;
					foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
						if (c.Address != 0 && !cs.Contains(c.Address) && seen.Add(c.Address)) q.Enqueue(c.Address);
				}
				return b;
			}
			ulong reachAll = Reach2(new HashSet<ulong>()), reachWithout = Reach2(cutT);
			Console.WriteLine($"transfer dictionaries={cutT.Count} Transfer objects={transfers} released={released} discarded={discarded}; reachable={reachAll / 1048576} MB, without dictionaries={reachWithout / 1048576} MB, retained only via transfers={(reachAll - reachWithout) / 1048576} MB");
			return 0;
		}
		if (args.Length > 1 && args[1] == "lohowners")
		{
			// Owners of LIVE large byte[]/string/char[]: shortest root path, grouped by the 6 hops nearest the object.
			var parent = new Dictionary<ulong, ulong>(1 << 24); var q3 = new Queue<ulong>();
			foreach (ClrRoot r in heap.EnumerateRoots()) { ulong a = r.Object.Address; if (a != 0 && parent.TryAdd(a, 0)) q3.Enqueue(a); }
			while (q3.Count > 0)
			{
				ulong a = q3.Dequeue(); ClrObject o = heap.GetObject(a); if (o.Type == null) continue;
				foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
					if (c.Address != 0 && parent.TryAdd(c.Address, a)) q3.Enqueue(c.Address);
			}
			var groups = new Dictionary<string, (ulong Bytes, int Count)>();
			foreach (ClrSegment sg in heap.Segments)
			{
				if (sg.Kind.ToString() != "Large") continue;
				foreach (ClrObject o in sg.EnumerateObjects())
				{
					string n = o.Type?.Name ?? "";
					if (o.Type == null || o.Type.IsFree || !parent.ContainsKey(o.Address)) continue;
					if (n != "System.Byte[]" && n != "System.String" && n != "System.Char[]") continue;
					var sig = new List<string>(); ulong cur = o.Address; int d = 0;
					while (cur != 0 && d++ < 6) { sig.Add(heap.GetObject(cur).Type?.Name ?? "?"); cur = parent.GetValueOrDefault(cur); }
					string key = string.Join("  <-  ", sig);
					var g = groups.GetValueOrDefault(key); g.Bytes += o.Size; g.Count++; groups[key] = g;
				}
			}
			foreach (var kv in groups.OrderByDescending(x => x.Value.Bytes).Take(15))
				Console.WriteLine($"  {kv.Value.Bytes / 1048576,5} MB {kv.Value.Count,5} x  {kv.Key}");
			return 0;
		}
		if (args.Length > 1 && args[1] == "loh")
		{
			// Per segment kind: live bytes, dead (unreachable, not yet collected) bytes, Free bytes; and the
			// largest-object type histogram on the Large segments split live/dead, with a size histogram.
			var roots2 = heap.EnumerateRoots().Select(r => r.Object.Address).Where(a => a != 0).Distinct().ToArray();
			var live = new HashSet<ulong>(1 << 24); var q2 = new Queue<ulong>();
			foreach (ulong a in roots2) if (live.Add(a)) q2.Enqueue(a);
			while (q2.Count > 0)
			{
				ClrObject o = heap.GetObject(q2.Dequeue()); if (o.Type == null) continue;
				foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
					if (c.Address != 0 && live.Add(c.Address)) q2.Enqueue(c.Address);
			}
			var seg = new Dictionary<string, (ulong Live, ulong Dead, ulong Free)>();
			var lohTypes = new Dictionary<string, (ulong Live, ulong Dead, int Count)>();
			var sizeBuckets = new SortedDictionary<int, (int Live, int Dead)>();
			foreach (ClrSegment sg in heap.Segments)
			{
				string kind = sg.Kind.ToString();
				foreach (ClrObject o in sg.EnumerateObjects())
				{
					if (o.Type == null) continue;
					var v = seg.GetValueOrDefault(kind);
					if (o.Type.IsFree) v.Free += o.Size;
					else if (live.Contains(o.Address)) v.Live += o.Size;
					else v.Dead += o.Size;
					seg[kind] = v;
					if (kind == "Large" && !o.Type.IsFree)
					{
						string n = o.Type.Name ?? "?"; bool isLive = live.Contains(o.Address);
						var t = lohTypes.GetValueOrDefault(n);
						if (isLive) t.Live += o.Size; else t.Dead += o.Size; t.Count++;
						lohTypes[n] = t;
						int mb = (int)Math.Min(64, Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(1.0, o.Size / 1048576.0)))));
						var b = sizeBuckets.GetValueOrDefault(mb); if (isLive) b.Live++; else b.Dead++; sizeBuckets[mb] = b;
					}
				}
			}
			foreach (var kv in seg) Console.WriteLine($"{kv.Key,-12} live={kv.Value.Live / 1048576,6} MB dead={kv.Value.Dead / 1048576,6} MB free={kv.Value.Free / 1048576,6} MB");
			Console.WriteLine("Large-object types (live MB / dead MB / count):");
			foreach (var kv in lohTypes.OrderByDescending(x => x.Value.Live + x.Value.Dead).Take(12))
				Console.WriteLine($"  {kv.Value.Live / 1048576,6} / {kv.Value.Dead / 1048576,6} / {kv.Value.Count,6}  {kv.Key}");
			Console.WriteLine("Large-object sizes (<= N MB: live / dead count): " + string.Join(", ", sizeBuckets.Select(b => $"{b.Key}: {b.Value.Live}/{b.Value.Dead}")));
			return 0;
		}
		if (args.Length > 1 && args[1] == "totals")
		{
			// Split the managed heap: all objects on the heap (live + garbage not yet collected), per GC segment kind,
			// plus committed segment memory, so RSS can be compared against managed vs everything else.
			ulong objects = 0; long count = 0;
			var byType = new Dictionary<string, ulong>();
			foreach (ClrObject o in heap.EnumerateObjects())
			{
				if (o.Type == null) continue;
				objects += o.Size; count++;
				string n = o.Type.Name ?? "?"; byType[n] = byType.GetValueOrDefault(n) + o.Size;
			}
			ulong committed = 0, reserved = 0;
			var kinds = new Dictionary<string, ulong>();
			foreach (ClrSegment seg in heap.Segments)
			{
				committed += seg.CommittedMemory.Length; reserved += seg.ReservedMemory.Length;
				kinds[seg.Kind.ToString()] = kinds.GetValueOrDefault(seg.Kind.ToString()) + seg.CommittedMemory.Length;
			}
			Console.WriteLine($"heap objects={count} bytes={objects / 1048576} MB; segments committed={committed / 1048576} MB reserved={reserved / 1048576} MB; by kind: " + string.Join(", ", kinds.Select(k => $"{k.Key}={k.Value / 1048576} MB")));
			foreach (var kv in byType.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  {kv.Value / 1048576,6} MB {kv.Key}");
			return 0;
		}
		if (args.Length > 1 && args[1] == "retained")
		{
			// Bytes reachable only through CLOSED grids: one walk with nothing cut, one with every closed grid cut.
			var closedSet = new HashSet<ulong>();
			foreach (ClrObject o in heap.EnumerateObjects())
				if (o.Type?.Name == "Sandbox.Game.Entities.MyCubeGrid" && ReadBool(o, "<Closed>k__BackingField")) closedSet.Add(o.Address);
			ulong[] rootList = heap.EnumerateRoots().Select(r => r.Object.Address).Where(a => a != 0).Distinct().ToArray();
			ulong Reach(HashSet<ulong> cutSet)
			{
				var seen = new HashSet<ulong>(1 << 24); var q = new Queue<ulong>(); ulong bytes = 0;
				foreach (ulong a in rootList) if (!cutSet.Contains(a) && seen.Add(a)) q.Enqueue(a);
				while (q.Count > 0)
				{
					ClrObject o = heap.GetObject(q.Dequeue()); if (o.Type == null) continue; bytes += o.Size;
					foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
						if (c.Address != 0 && !cutSet.Contains(c.Address) && seen.Add(c.Address)) q.Enqueue(c.Address);
				}
				return bytes;
			}
			ulong full = Reach(new HashSet<ulong>()), without = Reach(closedSet);
			Console.WriteLine($"closed grids={closedSet.Count} reachable={full / 1048576} MB, without closed grids={without / 1048576} MB, retained only via closed grids={(full - without) / 1048576} MB");
			return 0;
		}

		var walls = new HashSet<ulong>();
		foreach (ClrObject o in heap.EnumerateObjects())
		{
			if (o.Type?.Name != "Sandbox.Game.Entities.MyCubeGrid") continue;
			for (ClrType t = o.Type; t != null; t = t.BaseType)
			{
				ClrInstanceField f = t.GetFieldByName("<Closed>k__BackingField");
				if (f != null) { if (f.Read<bool>(o.Address, false)) walls.Add(o.Address); break; }
			}
		}
		Console.WriteLine($"closed grids={walls.Count}");
		if (args.Contains("exclude"))
		{
			// Holders already sized by the cut run: treat them as walls so the owner report shows what ELSE retains.
			foreach (ClrObject o in heap.EnumerateObjects())
			{
				string t = o.Type?.Name ?? "";
				if (t.StartsWith("System.Collections.Generic.Stack<VRage.Groups.MyGroups<") || t == "Sandbox.Engine.Physics.MyFixedGrids"
					|| t == "Sandbox.Game.GameSystems.MyAntennaSystem" || t == "VRage.Game.SessionComponents.MySessionComponentAnimationSystem"
					|| t == "System.Collections.Generic.Dictionary<System.Int64, Sandbox.Game.Entities.Blocks.MyProjectorBase>"
					|| t == "VRage.Game.Components.Session.MyEntityComponentUpdater")
					walls.Add(o.Address);
			}
			Console.WriteLine($"walls with excluded holders={walls.Count}");
		}
		ulong[] roots = heap.EnumerateRoots().Select(r => r.Object.Address).Where(a => a != 0).Distinct().ToArray();

		HashSet<ulong> Walk(HashSet<ulong> cut)
		{
			var seen = new HashSet<ulong>(1 << 24);
			var queue = new Queue<ulong>();
			foreach (ulong a in roots) if (!cut.Contains(a) && seen.Add(a)) queue.Enqueue(a);
			while (queue.Count > 0)
			{
				ClrObject o = heap.GetObject(queue.Dequeue());
				if (o.Type == null) continue;
				foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
					if (c.Address != 0 && !cut.Contains(c.Address) && seen.Add(c.Address)) queue.Enqueue(c.Address);
			}
			return seen;
		}

		HashSet<ulong> all = Walk(new HashSet<ulong>());
		for (int round = 1; round <= 8; round++)
		{
			HashSet<ulong> live = Walk(walls);
			ulong deadBytes = 0; int deadCount = 0;
			foreach (ulong a in all) if (!live.Contains(a)) { deadCount++; deadBytes += heap.GetObject(a).Size; }
			var anchors = new Dictionary<string, int>();
			var internalSources = new List<ulong>();
			foreach (ulong a in live)
			{
				ClrObject o = heap.GetObject(a);
				if (o.Type == null) continue;
				bool isInternal = Internal(o.Type.Name ?? "");
				foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
				{
					if (c.Address == 0 || live.Contains(c.Address) || !all.Contains(c.Address)) continue;
					if (isInternal) { internalSources.Add(a); break; }
					string key = (o.Type.Name ?? "?") + "  ->  " + (c.Type?.Name ?? "?");
					anchors[key] = anchors.GetValueOrDefault(key) + 1;
				}
			}
			Console.WriteLine();
			Console.WriteLine($"round {round}: walls={walls.Count} dead={deadCount} objects {deadBytes / 1048576} MB; internal anchor sources={internalSources.Count}; external anchor edges={anchors.Values.Sum()}");
			foreach (var kv in anchors.OrderByDescending(x => x.Value).Take(25)) Console.WriteLine($"  {kv.Value,7} x {kv.Key}");
			if (internalSources.Count == 0)
			{
				// Owners: for each distinct anchor SOURCE object (a collection's entry array, mostly),
				// print the shortest path back to a root, grouped by the path's type signature.
				var parent = new Dictionary<ulong, ulong>(1 << 24);
				var rootKind = new Dictionary<ulong, string>();
				var queue = new Queue<ulong>();
				foreach (ClrRoot r in heap.EnumerateRoots())
				{
					ulong a = r.Object.Address;
					if (a == 0 || walls.Contains(a) || parent.ContainsKey(a)) continue;
					parent[a] = 0; rootKind[a] = r.RootKind + (r is ClrStackRoot ? "" : "") ; queue.Enqueue(a);
				}
				while (queue.Count > 0)
				{
					ClrObject o = heap.GetObject(queue.Dequeue());
					if (o.Type == null) continue;
					foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
						if (c.Address != 0 && !walls.Contains(c.Address) && !parent.ContainsKey(c.Address)) { parent[c.Address] = o.Address; queue.Enqueue(c.Address); }
				}
				var owners = new Dictionary<string, (int Sources, ulong Example)>();
				foreach (ulong a in live)
				{
					ClrObject o = heap.GetObject(a);
					if (o.Type == null) continue;
					bool anchors_ = o.EnumerateReferences(carefully: false, considerDependantHandles: true)
						.Any(c => c.Address != 0 && !live.Contains(c.Address) && all.Contains(c.Address));
					if (!anchors_) continue;
					var sig = new List<string>();
					ulong cur = a; int depth = 0;
					while (cur != 0 && depth++ < 400) { sig.Add(heap.GetObject(cur).Type?.Name ?? "?"); cur = parent.GetValueOrDefault(cur); }
					// Near end (3 hops at the anchor) and ROOT end (8 hops): the middle is the anchor's own collection plumbing.
					string key = sig.Count <= 11 ? string.Join("  <-  ", sig)
						: string.Join("  <-  ", sig.Take(3)) + "  <- ... " + (sig.Count - 11) + " hops ... <-  " + string.Join("  <-  ", sig.Skip(sig.Count - 8));
					var v = owners.GetValueOrDefault(key); owners[key] = (v.Sources + 1, v.Example == 0 ? a : v.Example);
				}
				Console.WriteLine($"anchor sources grouped by owner path ({owners.Count} paths):");
				foreach (var kv in owners.OrderByDescending(x => x.Value.Sources).Take(20))
					Console.WriteLine($"  {kv.Value.Sources,6} sources e.g. {kv.Value.Example:x}: {kv.Key}");
				break;
			}
			walls.UnionWith(internalSources);
		}
		return 0;
	}

	// Characters: how many are closed / marked for close / dead, and how many closed grids their replicables
	// still list in m_dependencyParents. Counts only, no graph walk.
	private static int Characters(ClrHeap heap)
	{
		var closedGrids = new HashSet<ulong>();
		var characters = new List<ClrObject>();
		var replicables = new List<ClrObject>();
		foreach (ClrObject o in heap.EnumerateObjects())
		{
			string t = o.Type?.Name ?? "";
			if (t == "Sandbox.Game.Entities.MyCubeGrid" && ReadBool(o, "<Closed>k__BackingField")) closedGrids.Add(o.Address);
			else if (t == "Sandbox.Game.Entities.Character.MyCharacter") characters.Add(o);
			else if (t == "Sandbox.Game.Replication.MyCharacterReplicable") replicables.Add(o);
		}
		int closed = characters.Count(c => ReadBool(c, "<Closed>k__BackingField"));
		int marked = characters.Count(c => ReadBool(c, "<MarkedForClose>k__BackingField"));
		Console.WriteLine($"characters={characters.Count} closed={closed} markedForClose={marked} replicables={replicables.Count}");
		int withStale = 0, staleEdges = 0, staleOnClosedChar = 0;
		foreach (ClrObject r in replicables)
		{
			ClrObject set = r.ReadObjectField("m_dependencyParents");
			if (set.IsNull) continue;
			int n = 0;
			foreach (ClrObject c in set.EnumerateReferences(carefully: false))
				if (c.Type?.Name?.EndsWith("+Entry[]") == true)
					foreach (ClrObject g in c.EnumerateReferences(carefully: false)) if (closedGrids.Contains(g.Address)) n++;
			if (n == 0) continue;
			withStale++; staleEdges += n;
			ClrObject instance = ReadInstance(r);
			if (!instance.IsNull && ReadBool(instance, "<Closed>k__BackingField")) staleOnClosedChar++;
		}
		Console.WriteLine($"replicables listing closed grids={withStale} closed-grid entries={staleEdges} of which the character itself is closed={staleOnClosedChar}");
		return 0;
	}

	// Shortest root path to every CLOSED MyCharacter, with the already-fixed holders cut (group pools, fixed
	// grids, blueprint tracker dictionary) and every closed grid cut, so a path cannot run through a dead grid.
	// No peeling: holders that also carry live state are NOT walled, which is what broke the exclude run.
	private static int CharacterPaths(ClrHeap heap)
	{
		var cut = new HashSet<ulong>();
		var closedCharacters = new List<ulong>();
		foreach (ClrObject o in heap.EnumerateObjects())
		{
			string t = o.Type?.Name ?? "";
			if (t == "Sandbox.Game.Entities.MyCubeGrid" && ReadBool(o, "<Closed>k__BackingField")) cut.Add(o.Address);
			else if (t == "Sandbox.Game.Entities.Character.MyCharacter" && ReadBool(o, "<Closed>k__BackingField")) closedCharacters.Add(o.Address);
			else if (t.StartsWith("System.Collections.Generic.Stack<VRage.Groups.MyGroups<") || t == "Sandbox.Engine.Physics.MyFixedGrids"
				|| t == "System.Collections.Generic.Dictionary<System.Int64, Sandbox.Game.Entities.Blocks.MyProjectorBase>"
				|| (Environment.GetEnvironmentVariable("CHARPATHS_DEEP") == "1"
					&& (t == "VRage.Game.SessionComponents.MySessionComponentAnimationSystem" || t == "VRage.Game.Components.Session.MyEntityComponentUpdater")))
				cut.Add(o.Address);
		}
		Console.WriteLine($"closed characters={closedCharacters.Count} cut={cut.Count} deep={Environment.GetEnvironmentVariable("CHARPATHS_DEEP")}");
		var parent = new Dictionary<ulong, ulong>(1 << 24);
		var queue = new Queue<ulong>();
		foreach (ClrRoot r in heap.EnumerateRoots())
		{
			ulong a = r.Object.Address;
			if (a == 0 || cut.Contains(a) || parent.ContainsKey(a)) continue;
			parent[a] = 0; queue.Enqueue(a);
		}
		while (queue.Count > 0)
		{
			ClrObject o = heap.GetObject(queue.Dequeue());
			if (o.Type == null) continue;
			foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
				if (c.Address != 0 && !cut.Contains(c.Address) && !parent.ContainsKey(c.Address)) { parent[c.Address] = o.Address; queue.Enqueue(c.Address); }
		}
		var groups = new Dictionary<string, (int Count, ulong Example)>();
		int unreachable = 0;
		foreach (ulong ch in closedCharacters)
		{
			if (!parent.ContainsKey(ch)) { unreachable++; continue; }
			var sig = new List<string>();
			ulong cur = ch; int depth = 0;
			while (cur != 0 && depth++ < 400) { sig.Add(heap.GetObject(cur).Type?.Name ?? "?"); cur = parent.GetValueOrDefault(cur); }
			string key = string.Join("  <-  ", sig);
			var v = groups.GetValueOrDefault(key); groups[key] = (v.Count + 1, v.Example == 0 ? ch : v.Example);
		}
		Console.WriteLine($"closed characters unreachable with the cut={unreachable}; paths={groups.Count}");
		foreach (var kv in groups.OrderByDescending(x => x.Value.Count))
			Console.WriteLine($"  {kv.Value.Count,4} e.g. {kv.Value.Example:x}: {kv.Key}");
		return 0;
	}

	// Every edge from an object that is live WITHOUT any closed grid (all closed grids cut) into a closed grid,
	// grouped by the full root path of its source. No peeling, so no live hub can be walled by mistake. The
	// number beside a path is distinct closed grids it anchors.
	private static int GridAnchors(ClrHeap heap)
	{
		var closed = new HashSet<ulong>();
		foreach (ClrObject o in heap.EnumerateObjects())
			if (o.Type?.Name == "Sandbox.Game.Entities.MyCubeGrid" && ReadBool(o, "<Closed>k__BackingField")) closed.Add(o.Address);
		var parent = new Dictionary<ulong, ulong>(1 << 24);
		var queue = new Queue<ulong>();
		foreach (ClrRoot r in heap.EnumerateRoots())
		{
			ulong a = r.Object.Address;
			if (a == 0 || closed.Contains(a) || parent.ContainsKey(a)) continue;
			parent[a] = 0; queue.Enqueue(a);
		}
		var anchors = new List<(ulong Source, ulong Grid)>();
		while (queue.Count > 0)
		{
			ulong a = queue.Dequeue();
			ClrObject o = heap.GetObject(a);
			if (o.Type == null) continue;
			foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
			{
				if (c.Address == 0) continue;
				if (closed.Contains(c.Address)) { anchors.Add((a, c.Address)); continue; }
				if (parent.TryAdd(c.Address, a)) queue.Enqueue(c.Address);
			}
		}
		var groups = new Dictionary<string, (HashSet<ulong> Grids, ulong Example)>();
		foreach (var (source, grid) in anchors)
		{
			var sig = new List<string>();
			ulong cur = source; int depth = 0;
			while (cur != 0 && depth++ < 400) { sig.Add(heap.GetObject(cur).Type?.Name ?? "?"); cur = parent.GetValueOrDefault(cur); }
			string key = string.Join("  <-  ", sig);
			if (!groups.TryGetValue(key, out var g)) groups[key] = g = (new HashSet<ulong>(), source);
			g.Grids.Add(grid);
		}
		int anchored = anchors.Select(x => x.Grid).Distinct().Count();
		Console.WriteLine($"closed grids={closed.Count} directly anchored from live objects={anchored} anchor edges={anchors.Count} paths={groups.Count}");
		foreach (var kv in groups.OrderByDescending(x => x.Value.Grids.Count).Take(30))
			Console.WriteLine($"  {kv.Value.Grids.Count,5} grids e.g. src {kv.Value.Example:x}: {kv.Key}");
		return 0;
	}

	private static bool ReadBool(ClrObject o, string field)
	{
		for (ClrType t = o.Type; t != null; t = t.BaseType)
		{
			ClrInstanceField f = t.GetFieldByName(field);
			if (f != null) return f.Read<bool>(o.Address, false);
		}
		return false;
	}

	private static ClrObject ReadInstance(ClrObject replicable)
	{
		for (ClrType t = replicable.Type; t != null; t = t.BaseType)
		{
			ClrInstanceField f = t.GetFieldByName("<Instance>k__BackingField") ?? t.GetFieldByName("m_instance");
			if (f != null) return f.ReadObject(replicable.Address, false);
		}
		return default;
	}

	// What a fix would free: cut cumulative sets of holder objects out of the graph and report reachable
	// bytes plus how many CLOSED grids are still reachable. Holders that also carry live state (the antenna
	// system, the animation system) give an upper bound, not the fix's effect.
	private static int Cut(ClrHeap heap)
	{
		var closed = new HashSet<ulong>();
		var category = new Dictionary<string, List<ulong>>
		{
			["group-pools"] = new(), ["fixed-grids"] = new(), ["antenna-system"] = new(),
			["blueprint-tracker"] = new(), ["animation-system"] = new(), ["component-updater"] = new(), ["closed-characters"] = new(), ["terminal-systems"] = new(), ["distributor-systems"] = new(), ["external-replicables"] = new(), ["character-replicables"] = new(),
		};
		foreach (ClrObject o in heap.EnumerateObjects())
		{
			string t = o.Type?.Name ?? "";
			if (t == "Sandbox.Game.Entities.MyCubeGrid")
			{
				for (ClrType ty = o.Type; ty != null; ty = ty.BaseType)
				{
					ClrInstanceField f = ty.GetFieldByName("<Closed>k__BackingField");
					if (f != null) { if (f.Read<bool>(o.Address, false)) closed.Add(o.Address); break; }
				}
			}
			else if (t.StartsWith("System.Collections.Generic.Stack<VRage.Groups.MyGroups<")) category["group-pools"].Add(o.Address);
			else if (t == "Sandbox.Engine.Physics.MyFixedGrids") category["fixed-grids"].Add(o.Address);
			else if (t == "Sandbox.Game.GameSystems.MyAntennaSystem") category["antenna-system"].Add(o.Address);
			else if (t == "System.Collections.Generic.Dictionary<System.Int64, Sandbox.Game.Entities.Blocks.MyProjectorBase>") category["blueprint-tracker"].Add(o.Address);
			else if (t == "VRage.Game.SessionComponents.MySessionComponentAnimationSystem") category["animation-system"].Add(o.Address);
			else if (t == "VRage.Game.Components.Session.MyEntityComponentUpdater") category["component-updater"].Add(o.Address);
			else if (t == "Sandbox.Game.Entities.Character.MyCharacter" && ReadBool(o, "<Closed>k__BackingField")) category["closed-characters"].Add(o.Address);
			else if (t == "Sandbox.Game.GameSystems.MyGridTerminalSystem") category["terminal-systems"].Add(o.Address);
			else if (t == "Sandbox.Game.Entities.Cube.MyGridResourceDistributorSystem") category["distributor-systems"].Add(o.Address);
			else if (t == "VRage.Collections.MyConcurrentDictionary<System.Object, Sandbox.Game.Replication.MyExternalReplicable>") category["external-replicables"].Add(o.Address);
			else if (t == "Sandbox.Game.Replication.MyCharacterReplicable") category["character-replicables"].Add(o.Address);
		}
		Console.WriteLine($"closed grids={closed.Count}; " + string.Join(", ", category.Select(kv => $"{kv.Key}={kv.Value.Count}")));
		ulong[] roots = heap.EnumerateRoots().Select(r => r.Object.Address).Where(a => a != 0).Distinct().ToArray();
		var cut = new HashSet<ulong>();
		foreach (string step in new[] { "none" }.Concat(category.Keys))
		{
			if (step != "none") cut.UnionWith(category[step]);
			var seen = new HashSet<ulong>(1 << 24);
			var queue = new Queue<ulong>();
			ulong bytes = 0; int closedReached = 0;
			foreach (ulong a in roots) if (!cut.Contains(a) && seen.Add(a)) queue.Enqueue(a);
			while (queue.Count > 0)
			{
				ClrObject o = heap.GetObject(queue.Dequeue());
				if (o.Type == null) continue;
				bytes += o.Size;
				if (closed.Contains(o.Address)) closedReached++;
				foreach (ClrObject c in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
					if (c.Address != 0 && !cut.Contains(c.Address) && seen.Add(c.Address)) queue.Enqueue(c.Address);
			}
			Console.WriteLine($"{DateTime.Now:HH:mm:ss} cut through {step,-18} reachable {bytes / 1048576,6} MB, closed grids reachable {closedReached}");
		}
		return 0;
	}
}
