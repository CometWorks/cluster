using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRageMath.Spatial;

namespace ClusterPlugin;

/// <summary>
/// T-0401: what inside MyEntities.Add costs a handover import's activation (activate_add is 67-85 % of it
/// live, ~1.8 ms/entity p50, 3-8x a bench). ClusterControlServer.ActivateEntities runs each Add through
/// <see cref="Run"/>, which marks the add as an import activation on this thread (<see cref="IsImportAdd"/>,
/// read by GridAddTrace) and, with diag.importAddTimers (default true), times it into an
/// <see cref="ImportAddSlice"/>: the OnEntityAdd event (MyEntities.RaiseEntityAdd), physics activation
/// (MyPhysicsBody.Activate, called by MyEntity.OnAddedToScene for the entity and its children), the rest,
/// GC collections and pause time, and how often the physics cluster tree created or reordered clusters.
/// The hooks below do nothing unless an import Add is being timed on their thread.
/// </summary>
internal static class ImportAddDiag
{
	[ThreadStatic] private static object t_entity;
	[ThreadStatic] private static ImportAddSlice t_slice;
	[ThreadStatic] private static int t_eventDepth;
	[ThreadStatic] private static int t_physicsDepth;
	[ThreadStatic] private static long t_eventStart;
	[ThreadStatic] private static long t_physicsStart;
	[ThreadStatic] private static long t_eventCpuStart;
	[ThreadStatic] private static long t_physicsCpuStart;
	// T-0401 reorder diag (CH 17:36): MyClusterTree.ReorderClusters / CreateCluster wall time, outermost call only.
	[ThreadStatic] private static int t_reorderDepth;
	[ThreadStatic] private static int t_createDepth;
	[ThreadStatic] private static long t_reorderStart;
	[ThreadStatic] private static long t_createStart;
	// T-0401 follow-up (diag.importAddClusterTrace): the outermost body whose Activate() is running on this thread.
	[ThreadStatic] private static MyPhysicsBody t_activating;
	[ThreadStatic] private static int t_activatingDepth;
	// T-0404 release sub-timers (rel* inclusive parts of the close; ImportAddSlice.RelSubNames), outermost per kind.
	[ThreadStatic] private static int[] t_relSubDepth;
	[ThreadStatic] private static long[] t_relSubStart;
	/// <summary>Self-test only: this thread reads the CPU clock as unavailable (the -1 path) without touching other threads.</summary>
	[ThreadStatic] internal static bool t_cpuClockOffForTest;
	// 0 = not probed yet, 1 = clock_gettime works, -1 = unavailable (not Linux, or the P/Invoke failed once): addCpuMs=-1.
	private static int s_cpuClock;

	/// <summary>True while ActivateEntities is Adding exactly this entity on this thread.</summary>
	public static bool IsImportAdd(object entity) => entity != null && ReferenceEquals(t_entity, entity);

	/// <summary>True while any import Add runs on this thread (self-test).</summary>
	internal static bool InImportAdd => t_entity != null;

	/// <summary>
	/// Runs <paramref name="add"/> as the import activation of <paramref name="entity"/>; the mark is cleared
	/// afterwards whether or not it throws. <paramref name="slice"/> null = no timing (diag.importAddTimers false).
	/// </summary>
	public static void Run(object entity, ImportAddSlice slice, Action add)
	{
		object previousEntity = t_entity;
		t_entity = entity;
		try
		{
			Measure(slice, 1, add);
		}
		finally
		{
			t_entity = previousEntity;
		}
	}

	/// <summary>
	/// T-0401 release side: runs a source node's per-release close sequence (ClusterControlServer.CloseEntities: the
	/// MyEntities.Close marks, the attached-children close and MyEntities.DeleteRememberedEntities, where the per-entity
	/// work lands) exactly as it is, timed into <paramref name="slice"/> (a <see cref="ImportAddSlice.Release"/> slice)
	/// as <paramref name="entities"/> entities. Observes only: nothing is reordered, split or batched.
	/// </summary>
	public static void RunRelease(ImportAddSlice slice, int entities, Action close) => Measure(slice, entities, close);

	private static void Measure(ImportAddSlice slice, int entities, Action action)
	{
		ImportAddSlice previousSlice = t_slice;
		t_slice = slice;
		int gc0 = 0, gc1 = 0, gc2 = 0;
		long contention = 0;
		TimeSpan pause = TimeSpan.Zero;
		long start = 0, cpu = -1;
		if (slice != null)
		{
			gc0 = GC.CollectionCount(0);
			gc1 = GC.CollectionCount(1);
			gc2 = GC.CollectionCount(2);
			pause = PauseDuration();
			contention = LockContentionCount();
			// The CPU reads sit inside the wall window, so CPU <= wall for every Add.
			start = Stopwatch.GetTimestamp();
			cpu = ThreadCpuNs();
		}
		try
		{
			action();
		}
		finally
		{
			if (slice != null)
			{
				slice.AddCpu(cpu, ThreadCpuNs(), ref slice.CpuNs);
				slice.AddTicks += Stopwatch.GetTimestamp() - start;
				slice.LockContention += LockContentionCount() - contention;
				slice.Adds += entities;
				slice.Gc0 += GC.CollectionCount(0) - gc0;
				slice.Gc1 += GC.CollectionCount(1) - gc1;
				slice.Gc2 += GC.CollectionCount(2) - gc2;
				slice.GcPause += PauseDuration() - pause;
			}
			t_slice = previousSlice;
			if (previousSlice == null)
			{
				// A hook whose postfix never ran (the hooked method threw) must not leave a depth behind.
				t_eventDepth = 0;
				t_physicsDepth = 0;
				t_reorderDepth = 0;
				t_createDepth = 0;
			}
		}
	}

	/// <summary>Process-wide count of contended Monitor (lock) acquisitions; 0 where the runtime lacks it.</summary>
	private static long LockContentionCount()
	{
#if NETCOREAPP3_0_OR_GREATER
		return System.Threading.Monitor.LockContentionCount;
#else
		return 0;
#endif
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Timespec
	{
		public long Seconds;
		public long Nanoseconds;
	}

	private const int ClockThreadCpuTimeId = 3; // CLOCK_THREAD_CPUTIME_ID (Linux)

	[DllImport("libc", EntryPoint = "clock_gettime")]
	private static extern int ClockGetTime(int clock, out Timespec time);

	/// <summary>
	/// This thread's CPU time in ns (clock_gettime(CLOCK_THREAD_CPUTIME_ID)), -1 when unavailable. A failed
	/// P/Invoke or a non-zero return disables the clock for the process (addCpuMs=-1); it never throws.
	/// </summary>
	internal static long ThreadCpuNs()
	{
		if (t_cpuClockOffForTest || s_cpuClock < 0) return -1;
		try
		{
			if (s_cpuClock == 0 && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				s_cpuClock = -1;
				return -1;
			}
			if (ClockGetTime(ClockThreadCpuTimeId, out Timespec time) != 0)
			{
				s_cpuClock = -1;
				return -1;
			}
			s_cpuClock = 1;
			return time.Seconds * 1_000_000_000L + time.Nanoseconds;
		}
		catch
		{
			s_cpuClock = -1;
			return -1;
		}
	}

	private static TimeSpan PauseDuration()
	{
#if NET7_0_OR_GREATER
		return GC.GetTotalPauseDuration();
#else
		return TimeSpan.Zero;
#endif
	}

	/// <summary>Counts the entity's kind and size into the slice (after its Add; not timed).</summary>
	public static void CountEntity(ImportAddSlice slice, MyEntity entity)
	{
		if (slice == null) return;
		if (entity is MyCubeGrid grid)
		{
			slice.Grids++;
			int blocks = grid.BlocksCount;
			slice.BlocksSum += blocks;
			slice.BlocksMax = Math.Max(slice.BlocksMax, blocks);
			slice.FatBlocksSum += grid.GetFatBlocks().Count;
		}
		else
		{
			slice.Others++;
		}
	}

	/// <summary>The slice being timed on this thread, if it is of the given kind (Add or Release).</summary>
	private static ImportAddSlice Timed(bool release) => t_slice is ImportAddSlice slice && slice.Release == release ? slice : null;

	internal static void EventEnter(bool release = false)
	{
		if (Timed(release) == null) return;
		if (t_eventDepth++ == 0)
		{
			t_eventStart = Stopwatch.GetTimestamp();
			t_eventCpuStart = ThreadCpuNs();
		}
	}

	internal static void EventExit(bool release = false)
	{
		ImportAddSlice slice = Timed(release);
		if (slice == null || t_eventDepth <= 0) return;
		if (--t_eventDepth == 0)
		{
			slice.AddCpu(t_eventCpuStart, ThreadCpuNs(), ref slice.EventCpuNs);
			slice.EventTicks += Stopwatch.GetTimestamp() - t_eventStart;
			slice.EventCalls++;
		}
	}

	// Physics activated from inside an OnEntityAdd handler stays in the event's time: the three parts never overlap.
	internal static void PhysicsEnter(bool release = false)
	{
		if (Timed(release) == null || t_eventDepth > 0) return;
		if (t_physicsDepth++ == 0)
		{
			t_physicsStart = Stopwatch.GetTimestamp();
			t_physicsCpuStart = ThreadCpuNs();
		}
	}

	internal static void PhysicsExit(bool release = false)
	{
		ImportAddSlice slice = Timed(release);
		if (slice == null || t_eventDepth > 0 || t_physicsDepth <= 0) return;
		if (--t_physicsDepth == 0)
		{
			slice.AddCpu(t_physicsCpuStart, ThreadCpuNs(), ref slice.PhysicsCpuNs);
			slice.PhysicsTicks += Stopwatch.GetTimestamp() - t_physicsStart;
			slice.PhysicsCalls++;
		}
	}

	/// <summary>T-0404: a release sub-timer's method entered (kind = index into ImportAddSlice.RelSubNames); inert
	/// unless a release close sequence is timed on this thread. Outermost call per kind only.</summary>
	internal static void RelSubEnter(int kind)
	{
		if (Timed(true) is not ImportAddSlice) return;
		t_relSubDepth ??= new int[ImportAddSlice.RelSubNames.Length];
		t_relSubStart ??= new long[ImportAddSlice.RelSubNames.Length];
		if (t_relSubDepth[kind]++ == 0) t_relSubStart[kind] = Stopwatch.GetTimestamp();
	}

	internal static void RelSubExit(int kind)
	{
		if (Timed(true) is not ImportAddSlice slice || t_relSubDepth == null || t_relSubDepth[kind] == 0) return;
		if (--t_relSubDepth[kind] == 0)
		{
			slice.RelSubTicks[kind] += Stopwatch.GetTimestamp() - t_relSubStart[kind];
			slice.RelSubCalls[kind]++;
		}
	}

	internal static void CountClusterCreate()
	{
		if (Timed(false) is ImportAddSlice slice) slice.ClusterCreates++;
	}

	internal static void CountClusterReorder()
	{
		if (Timed(false) is ImportAddSlice slice) slice.ClusterReorders++;
	}

	/// <summary>
	/// T-0401 reorder diag: MyClusterTree.ReorderClusters entered inside a timed import Add (counted as a reorder, as
	/// <see cref="CountClusterReorder"/>); the outermost call's wall time goes to addReorderMs. Its exit is a Harmony
	/// finalizer, so a throwing reorder still closes the interval.
	/// </summary>
	internal static void ReorderEnter()
	{
		if (Timed(false) is not ImportAddSlice slice) return;
		slice.ClusterReorders++;
		if (t_reorderDepth == 0 && t_createDepth == 0) TraceClusterOp(slice, "reorder");
		if (t_reorderDepth++ == 0) t_reorderStart = Stopwatch.GetTimestamp();
	}

	internal static void ReorderExit()
	{
		if (Timed(false) is not ImportAddSlice slice || t_reorderDepth == 0) return;
		if (--t_reorderDepth == 0) slice.ReorderTicks += Stopwatch.GetTimestamp() - t_reorderStart;
	}

	/// <summary>T-0401 reorder diag: an object (re)assigned to a cluster (MyClusterTree.AddObjectToCluster) while a reorder runs.</summary>
	internal static void CountReorderMove()
	{
		if (t_reorderDepth > 0 && Timed(false) is ImportAddSlice slice) slice.ReorderMoved++;
	}

	/// <summary>T-0401 reorder diag: MyClusterTree.CreateCluster (a new HkWorld) inside a timed import Add, counted and timed.</summary>
	internal static void CreateEnter()
	{
		if (Timed(false) is not ImportAddSlice slice) return;
		slice.ClusterCreates++;
		if (t_createDepth == 0 && t_reorderDepth == 0) TraceClusterOp(slice, "create");
		if (t_createDepth++ == 0) t_createStart = Stopwatch.GetTimestamp();
	}

	internal static void ActivatingEnter(MyPhysicsBody body)
	{
		if (t_slice == null) return;
		if (t_activatingDepth++ == 0) t_activating = body;
	}

	internal static void ActivatingExit()
	{
		if (t_activatingDepth > 0 && --t_activatingDepth == 0) t_activating = null;
	}

	/// <summary>
	/// T-0401 follow-up (diag.importAddClusterTrace): one line per outermost cluster create / reorder inside a timed import
	/// Add or thaw, naming the entity being Added and the body whose activation caused it. Diagnostics never throw.
	/// </summary>
	private static void TraceClusterOp(ImportAddSlice slice, string op)
	{
		try
		{
			if (!ClusterConfig.GetBool("diag.importAddClusterTrace")) return;
			MyEntity added = t_entity as MyEntity;
			MyPhysicsBody body = t_activating;
			MyEntity owner = body?.Entity as MyEntity;
			MyEntity top = owner?.GetTopMostParent() as MyEntity;
			string Describe(MyEntity e) => e == null ? "-" : e.GetType().Name + "#" + e.EntityId;
			VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] import_add_cluster_op op=" + op
				+ " phase=" + (added != null ? "add" : "thaw") + " added=" + Describe(added)
				+ " body=" + (body == null ? "-" : body.GetType().Name) + " bodyEntity=" + Describe(owner)
				+ " subpart=" + (body?.IsSubpart == true ? "true" : "false")
				+ " bodyEnabled=" + (body?.Enabled == true ? "true" : "false")
				+ " top=" + Describe(top) + " topEnabled=" + (top?.Physics?.Enabled == true ? "true" : "false")
				+ " addedEnabled=" + (added?.Physics?.Enabled == true ? "true" : "false"));
		}
		catch
		{
		}
	}

	internal static void CreateExit()
	{
		if (Timed(false) is not ImportAddSlice slice || t_createDepth == 0) return;
		if (--t_createDepth == 0) slice.CreateTicks += Stopwatch.GetTimestamp() - t_createStart;
	}
}

/// <summary>
/// T-0401 node-age tags (CH: split the live Add cost by node age; the bench's first import on a fresh node cost 3.70
/// ms/entity, the second 1.45, warm 0.86): a 1-based count of staged imports this process has begun activating (and
/// of releases it has begun closing), and nodeAgeS = seconds since the PROCESS started (as the hitch lines' age; the
/// node serves some tens of seconds later).
/// </summary>
internal sealed class NodeAgeCounter
{
	public static readonly NodeAgeCounter Imports = new NodeAgeCounter("importIndex");
	public static readonly NodeAgeCounter Releases = new NodeAgeCounter("releaseIndex");
	private readonly string m_name;
	private int m_count;

	public NodeAgeCounter(string name) => m_name = name;

	/// <summary>The next 1-based index (thread-safe).</summary>
	public int Next() => Interlocked.Increment(ref m_count);

	/// <summary>" nodeAgeS=… importIndex=…" (or releaseIndex) for a handover_phase line.</summary>
	public string Fields(int index) => string.Format(CultureInfo.InvariantCulture, " nodeAgeS={0:F1} {1}={2}", HitchRecorder.ProcessAgeSeconds, m_name, index);
}

/// <summary>T-0401: one activation slice's (or one-frame activate's) MyEntities.Add breakdown. Game thread only.</summary>
internal sealed class ImportAddSlice
{
	/// <summary>False: an import activation's Adds (add* fields). True: a source release's closes (rel* fields).</summary>
	public bool Release;
	public long AddTicks;
	public long EventTicks;
	public long PhysicsTicks;
	public int Adds;
	public int EventCalls;
	public int PhysicsCalls;
	public int ClusterCreates;
	public int ClusterReorders;
	/// <summary>T-0401 reorder diag: wall ticks inside MyClusterTree.ReorderClusters / CreateCluster (outermost), objects moved by reorders.</summary>
	public long ReorderTicks;
	public long CreateTicks;
	public int ReorderMoved;
	public int Grids;
	public int Others;
	public long BlocksSum;
	public int BlocksMax;
	public long FatBlocksSum;
	public int Gc0;
	public int Gc1;
	public int Gc2;
	public TimeSpan GcPause;
	public long CpuNs;
	public long EventCpuNs;
	public long PhysicsCpuNs;
	/// <summary>False once any CPU read of this slice was unavailable: the CPU fields print -1.</summary>
	public bool CpuValid = true;
	public long LockContention;
	/// <summary>T-0404: inclusive wall ms of parts of a release's close, each its outermost calls: grid BeforeDelete (contains
	/// sceneRemove for the grid and systems), MyEntities.Remove (scene removal: OnRemovedFromScene of the entity and its
	/// blocks, RaiseEntityRemove = relEventMs), MyCubeGridSystems.UnregisterFromSystems (per fat block), the OnClose
	/// handlers (MyEntity.CallAndClearOnClose: game, mods and plugins). Overlapping, so they do not sum to relTotalMs.
	/// T-0404 release look (CH 01:36): then our MyTerminalBlockClientRemovedPatch.Unsubscribe (per terminal block's Closing),
	/// MyReplicationServer.Destroy (per replicable: grid, terminal block, inventory, ...) and three of its parts:
	/// RemoveForClients (per client + the destroy message), RemoveStateGroups and MyReplicationLayer.RemoveNetworkedObject
	/// (also called inside RemoveStateGroups, per state group). The kinds from index <see cref="FirstCountedRelSub"/> on also
	/// print their outermost call count (RelSubCountNames).</summary>
	public static readonly string[] RelSubNames = { "relGridBeforeDeleteMs", "relSceneRemoveMs", "relSystemsMs", "relOnCloseMs",
		"relUnsubscribeMs", "relReplDestroyMs", "relReplForClientsMs", "relReplStateGroupsMs", "relReplNetObjMs" };
	public const int FirstCountedRelSub = 4;
	public static readonly string[] RelSubCountNames = { "relUnsubscribeN", "relReplDestroyN", "relReplForClientsN", "relReplStateGroupsN", "relReplNetObjN" };
	public readonly long[] RelSubTicks = new long[RelSubNames.Length];
	public readonly int[] RelSubCalls = new int[RelSubNames.Length];

	/// <summary>Adds one CPU interval to <paramref name="sum"/>; an unavailable read (-1) invalidates the slice's CPU fields.</summary>
	public void AddCpu(long startNs, long endNs, ref long sum)
	{
		if (startNs < 0 || endNs < 0) CpuValid = false;
		else sum += endNs - startNs;
	}

	private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
	private string CpuMs(double ms) => CpuValid ? ms.ToString("F2", CultureInfo.InvariantCulture) : "-1";

	/// <summary>
	/// T-0401 release side, appended at the END of the source's release (or release_close) handover_phase line; they
	/// cover the close sequence of THAT frame (marks, attached children, DeleteRememberedEntities - where Delete,
	/// physics Close/Deactivate, OnRemovedFromScene and RaiseEntityRemove run). relN = the transfer's entities closed
	/// (roots); relTotalMs also covers any entity someone else queued for delete before it; a Pinned entity vanilla
	/// defers to the next frame is not covered. relEventMs = MyEntities.RaiseEntityRemove (OnEntityRemove handlers),
	/// relPhysicsMs = MyPhysicsBody.Deactivate/Close and MyGridPhysics.Close (rigid body Dispose, cluster tree removal),
	/// relOtherMs = total - event - physics. CPU fields -1 without the thread CPU clock.
	/// </summary>
	public string ReleaseFields() => string.Format(CultureInfo.InvariantCulture,
		" relN={0} relTotalMs={1:F2} relCpuMs={2} relBlockedMs={3} relLockContention={4} relEventMs={5:F2} relEventCpuMs={6}"
		+ " relPhysicsMs={7:F2} relPhysicsCpuMs={8} relOtherMs={9:F2} relGrids={10} relBlocksSum={11} relGc0={12} relGc1={13} relGc2={14}"
		+ " relGcPauseMs={15:F2} relOthers={16} relBlocksMax={17} relFatBlocksSum={18} relEventCalls={19} relPhysicsCalls={20}",
		Adds, Ms(AddTicks), CpuMs(CpuNs / 1e6), CpuMs(Ms(AddTicks) - CpuNs / 1e6), LockContention, Ms(EventTicks), CpuMs(EventCpuNs / 1e6),
		Ms(PhysicsTicks), CpuMs(PhysicsCpuNs / 1e6), Ms(AddTicks - EventTicks - PhysicsTicks), Grids, BlocksSum, Gc0, Gc1, Gc2,
		GcPause.TotalMilliseconds, Others, BlocksMax, FatBlocksSum, EventCalls, PhysicsCalls) + RelSubFields();

	private string RelSubFields()
	{
		System.Text.StringBuilder fields = new System.Text.StringBuilder();
		for (int i = 0; i < RelSubNames.Length; i++)
			fields.Append(' ').Append(RelSubNames[i]).Append('=').Append(Ms(RelSubTicks[i]).ToString("F2", CultureInfo.InvariantCulture));
		for (int i = FirstCountedRelSub; i < RelSubNames.Length; i++)
			fields.Append(' ').Append(RelSubCountNames[i - FirstCountedRelSub]).Append('=').Append(RelSubCalls[i]);
		return fields.ToString();
	}

	/// <summary>
	/// The " key=value" fields appended at the END of the handover_phase line. addSceneOtherMs = addTotalMs -
	/// addEventMs - addPhysicsMs. addEventCalls/addPhysicsCalls are the hooks' outermost hits: addEventCalls below
	/// addN means the RaiseEntityAdd hook did not see every Add (the times are then a lower bound).
	/// Then (T-0401 lock hypothesis): addCpuMs (game-thread CPU inside the Adds), addBlockedMs = addTotalMs - addCpuMs
	/// (off CPU: lock waits, I/O, page faults, preemption), addLockContention (process-wide Monitor contention during the
	/// Adds: an upper bound for the Adds' own), addEventCpuMs and addPhysicsCpuMs. The CPU fields are -1 when the
	/// thread CPU clock is unavailable.
	/// </summary>
	public string Fields() => string.Format(CultureInfo.InvariantCulture,
		" addN={0} addTotalMs={1:F2} addEventMs={2:F2} addPhysicsMs={3:F2} addSceneOtherMs={4:F2} addEventCalls={5} addPhysicsCalls={6}"
		+ " addClusterCreates={7} addClusterReorders={8} addGrids={9} addOthers={10} addBlocksSum={11} addBlocksMax={12} addFatBlocksSum={13}"
		+ " addGc0={14} addGc1={15} addGc2={16} addGcPauseMs={17:F2}"
		+ " addCpuMs={18} addBlockedMs={19} addLockContention={20} addEventCpuMs={21} addPhysicsCpuMs={22}"
		+ " addReorderMs={23:F2} addReorderMoved={24} addCreateMs={25:F2}",
		Adds, Ms(AddTicks), Ms(EventTicks), Ms(PhysicsTicks), Ms(AddTicks - EventTicks - PhysicsTicks), EventCalls, PhysicsCalls,
		ClusterCreates, ClusterReorders, Grids, Others, BlocksSum, BlocksMax, FatBlocksSum,
		Gc0, Gc1, Gc2, GcPause.TotalMilliseconds,
		CpuMs(CpuNs / 1e6), CpuMs(Ms(AddTicks) - CpuNs / 1e6), LockContention, CpuMs(EventCpuNs / 1e6), CpuMs(PhysicsCpuNs / 1e6),
		Ms(ReorderTicks), ReorderMoved, Ms(CreateTicks));
}

/// <summary>T-0401 hooks; each is a thread-static read unless <see cref="ImportAddDiag"/> is timing an import Add.</summary>
[HarmonyPatch(typeof(MyEntities), nameof(MyEntities.RaiseEntityAdd))]
public static class ImportAddEventTimerPatch
{
	[HarmonyPrefix]
	private static void Prefix() => ImportAddDiag.EventEnter();

	[HarmonyPostfix]
	private static void Postfix() => ImportAddDiag.EventExit();
}

[HarmonyPatch(typeof(MyPhysicsBody), nameof(MyPhysicsBody.Activate), new Type[0])]
public static class ImportAddPhysicsTimerPatch
{
	[HarmonyPrefix]
	private static void Prefix() => ImportAddDiag.PhysicsEnter();

	[HarmonyPostfix]
	private static void Postfix() => ImportAddDiag.PhysicsExit();
}

/// <summary>T-0401 follow-up (diag.importAddClusterTrace): which body's Activate() is running, for the cluster-op trace.</summary>
[HarmonyPatch(typeof(MyPhysicsBody), nameof(MyPhysicsBody.Activate), new Type[0])]
public static class ImportAddActivatingBodyPatch
{
	[HarmonyPrefix]
	private static void Prefix(MyPhysicsBody __instance) => ImportAddDiag.ActivatingEnter(__instance);

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.ActivatingExit();
		return __exception;
	}
}

[HarmonyPatch(typeof(MyClusterTree), "CreateCluster")]
public static class ImportAddClusterCreatePatch
{
	[HarmonyPrefix]
	private static void Prefix() => ImportAddDiag.CreateEnter();

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.CreateExit();
		return __exception;
	}
}

/// <summary>T-0401 reorder diag (CH 17:36): ReorderClusters counted AND timed (outermost), the exit as a finalizer.</summary>
[HarmonyPatch(typeof(MyClusterTree), nameof(MyClusterTree.ReorderClusters))]
public static class ImportAddClusterReorderPatch
{
	[HarmonyPrefix]
	private static void Prefix() => ImportAddDiag.ReorderEnter();

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.ReorderExit();
		return __exception;
	}
}

/// <summary>T-0401 reorder diag: each object a reorder (re)assigns to a cluster - the objects it moves.</summary>
[HarmonyPatch(typeof(MyClusterTree), "AddObjectToCluster")]
public static class ImportAddReorderMovePatch
{
	[HarmonyPrefix]
	private static void Prefix() => ImportAddDiag.CountReorderMove();
}

/// <summary>T-0401 release-side hooks (source node): inert unless a release close sequence is timed on their thread.</summary>
[HarmonyPatch(typeof(MyEntities), nameof(MyEntities.RaiseEntityRemove))]
public static class ReleaseEventTimerPatch
{
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.EventEnter(release: true);

	[HarmonyPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Postfix() => ImportAddDiag.EventExit(release: true);
}

/// <summary>
/// Physics teardown of a released entity: Deactivate (from OnRemovedFromScene; HavokOrphanBodyFix also patches it -
/// these run first/last around it, so its fix is inside the time) and Close (rigid body Dispose; MyGridPhysics.Close
/// wraps MyPhysicsBody.Close, outermost counted once). Prefix/postfix only: the order of the game's work is untouched.
/// </summary>
[HarmonyPatch]
public static class ReleasePhysicsTimerPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(MyPhysicsBody), nameof(MyPhysicsBody.Deactivate), new Type[0]);
		yield return AccessTools.Method(typeof(MyPhysicsBody), nameof(MyPhysicsBody.Close), new Type[0]);
		yield return AccessTools.Method(typeof(Sandbox.Game.Entities.Cube.MyGridPhysics), nameof(Sandbox.Game.Entities.Cube.MyGridPhysics.Close), new Type[0]);
	}

	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.PhysicsEnter(release: true);

	[HarmonyPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Postfix() => ImportAddDiag.PhysicsExit(release: true);
}

/// <summary>T-0404 release sub-timers (see ImportAddSlice.RelSubNames). Prefix + finalizer only, the game's work is untouched.</summary>
[HarmonyPatch(typeof(MyCubeGrid), "BeforeDelete")]
public static class ReleaseGridBeforeDeleteTimerPatch
{
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.RelSubEnter(0);

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.RelSubExit(0);
		return __exception;
	}
}

[HarmonyPatch(typeof(MyEntities), nameof(MyEntities.Remove), new[] { typeof(MyEntity) })]
public static class ReleaseSceneRemoveTimerPatch
{
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.RelSubEnter(1);

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.RelSubExit(1);
		return __exception;
	}
}

[HarmonyPatch(typeof(Sandbox.Game.Entities.Cube.MyCubeGridSystems), nameof(Sandbox.Game.Entities.Cube.MyCubeGridSystems.UnregisterFromSystems))]
public static class ReleaseSystemsTimerPatch
{
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.RelSubEnter(2);

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.RelSubExit(2);
		return __exception;
	}
}

[HarmonyPatch(typeof(MyEntity), "CallAndClearOnClose")]
public static class ReleaseOnCloseTimerPatch
{
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.RelSubEnter(3);

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.RelSubExit(3);
		return __exception;
	}
}

/// <summary>T-0404 release look (CH 01:36): MyReplicationServer.Destroy and its parts, release sub-timers 5-8. Observe-only.</summary>
[HarmonyPatch(typeof(VRage.Network.MyReplicationServer), nameof(VRage.Network.MyReplicationServer.Destroy))]
public static class ReleaseReplDestroyTimerPatch
{
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.RelSubEnter(5);

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.RelSubExit(5);
		return __exception;
	}
}

[HarmonyPatch(typeof(VRage.Network.MyReplicationServer), "RemoveForClients")]
public static class ReleaseReplForClientsTimerPatch
{
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.RelSubEnter(6);

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.RelSubExit(6);
		return __exception;
	}
}

[HarmonyPatch(typeof(VRage.Network.MyReplicationServer), "RemoveStateGroups")]
public static class ReleaseReplStateGroupsTimerPatch
{
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.RelSubEnter(7);

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.RelSubExit(7);
		return __exception;
	}
}

[HarmonyPatch(typeof(VRage.Network.MyReplicationLayer), "RemoveNetworkedObject")]
public static class ReleaseReplNetObjTimerPatch
{
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void Prefix() => ImportAddDiag.RelSubEnter(8);

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception)
	{
		ImportAddDiag.RelSubExit(8);
		return __exception;
	}
}
