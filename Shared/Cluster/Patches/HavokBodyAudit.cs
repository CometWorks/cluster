using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Havok;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// T-0266 instrument: find the path that destroys a live rigid body's native object.
///
/// HavokReleasedBodyGuard only survives the dead body at its close, possibly minutes later. This
/// audit finds it within a second and says how it got there:
/// - every native membership change of a body goes through a few HkWorld methods (AddRigidBody,
///   RemoveRigidBody, the Batch variants and FinishBatch), and an explicit RemoveReference is the only
///   other managed way to drop a reference; each is recorded in a short per-body history (removals
///   with a short managed stack);
/// - once a second (game thread) every registered, undisposed HkRigidBody is checked for the dump
///   signature: first native qword no longer the vtable it had when assigned, or reference count
///   <= 0. A hit logs `havok_body_release_detected` with the owning entity and that history;
/// - a body removed twice from one world - directly while it waits in the world's remove batch, or
///   batched when it is no longer in a world - drops one reference too many. That is logged the
///   moment it happens, as `havok_double_remove`.
/// </summary>
internal static class HavokBodyAudit
{
	private sealed class Ring
	{
		private readonly string[] m_items = new string[16];
		private int m_next;
		public void Add(string item) { lock (this) m_items[m_next++ % m_items.Length] = item; }
		public string Join()
		{
			lock (this)
			{
				int count = Math.Min(m_next, m_items.Length);
				return string.Join(" | ", Enumerable.Range(m_next - count, count).Select(i => m_items[i % m_items.Length]));
			}
		}
	}

	private static readonly ConcurrentDictionary<IntPtr, HkHandle> Registered =
		AccessTools.Field(typeof(HkHandle), "m_registeredHandles")?.GetValue(null) as ConcurrentDictionary<IntPtr, HkHandle>;

	private static readonly ConditionalWeakTable<HkRigidBody, Ring> Histories = new ConditionalWeakTable<HkRigidBody, Ring>();
	private static readonly ConditionalWeakTable<HkRigidBody, MyPhysicsBody> Owners = new ConditionalWeakTable<HkRigidBody, MyPhysicsBody>();
	private static readonly ConditionalWeakTable<HkRigidBody, object> Reported = new ConditionalWeakTable<HkRigidBody, object>();
	private static readonly Dictionary<HkWorld, HashSet<HkRigidBody>> PendingRemovals = new Dictionary<HkWorld, HashSet<HkRigidBody>>(ReferenceEqualityComparer.Instance);

	private static long s_lastScan;
	private static long s_lastStats;
	private static long s_adds, s_removes, s_batchRemoves, s_flushes, s_voxelCloses, s_gridCloses, s_otherCloses;
	private static int s_detected;
	private static int s_doubleRemoves;

	internal static void RememberOwner(HkRigidBody body, MyPhysicsBody owner) => Owners.AddOrUpdate(body, owner);

	internal static string History(HkRigidBody body) => Histories.TryGetValue(body, out Ring ring) ? ring.Join() : "";

	/// <summary>T-0263: a disposed world never flushes its batch; drop its pending set (it held the world alive).</summary>
	internal static void Forget(HkWorld world)
	{
		lock (PendingRemovals) PendingRemovals.Remove(world);
	}

	private static string Describe(HkRigidBody body)
	{
		if (body == null) return "null";
		MyEntity entity = Owners.TryGetValue(body, out MyPhysicsBody owner) ? owner.Entity as MyEntity : null;
		return entity == null ? $"body=0x{body.NativeObject.ToInt64():x} entity=unknown"
			: $"body=0x{body.NativeObject.ToInt64():x} entity={entity.GetType().Name}:{entity.EntityId} name={entity.DisplayName ?? entity.Name} closed={entity.Closed} markedForClose={entity.MarkedForClose}";
	}

	private static string ShortStack()
	{
		var frames = new StackTrace(3, false).GetFrames();
		var text = new StringBuilder();
		foreach (StackFrame frame in frames.Take(9))
		{
			var method = frame.GetMethod();
			if (method == null) continue;
			if (text.Length > 0) text.Append(" < ");
			text.Append(method.DeclaringType?.Name).Append('.').Append(method.Name);
		}
		return text.ToString();
	}

	private static void Record(HkRigidBody body, string op, HkWorld world, bool stack)
	{
		if (body == null) return;
		try
		{
			string state = body.IsDisposed ? "disposed" : $"inWorld={body.InWorldInternal} rc={HkReferenceObject.HkReferenceObject_ReferenceCount(body.NativeObject)}";
			Histories.GetValue(body, _ => new Ring()).Add(
				$"{DateTime.Now:HH:mm:ss.fff} f{MySandboxGame.Static?.SimulationFrameCounter} {op} w=0x{(world?.NativeObject ?? IntPtr.Zero).ToInt64():x} {state}"
				+ (stack ? " at " + ShortStack() : ""));
		}
		catch (Exception ex)
		{
			Histories.GetValue(body, _ => new Ring()).Add($"{op} record_failed {ex.GetType().Name}");
		}
	}

	private static void DoubleRemove(HkRigidBody body, HkWorld world, string kind)
	{
		int count = ++s_doubleRemoves;
		Console.WriteLine($"[ClusterPlugin] havok_double_remove kind={kind} {Describe(body)} world=0x{(world?.NativeObject ?? IntPtr.Zero).ToInt64():x} "
			+ $"total={count} at {ShortStack()} history=[{History(body)}] (T-0266)");
	}

	[HarmonyPatch(typeof(HkWorld), nameof(HkWorld.AddRigidBody))]
	private static class AddPatch
	{
		[HarmonyPostfix] private static void Postfix(HkWorld __instance, HkRigidBody rigidBody) { s_adds++; Record(rigidBody, "add", __instance, false); }
	}

	[HarmonyPatch(typeof(HkWorld), nameof(HkWorld.AddRigidBodyBatch))]
	private static class AddBatchPatch
	{
		[HarmonyPostfix] private static void Postfix(HkWorld __instance, HkRigidBody rigidBody) { s_adds++; Record(rigidBody, "add_batch", __instance, false); }
	}

	[HarmonyPatch(typeof(HkWorld), nameof(HkWorld.RemoveRigidBody))]
	private static class RemovePatch
	{
		[HarmonyPrefix]
		private static void Prefix(HkWorld __instance, HkRigidBody rigidBody)
		{
			lock (PendingRemovals)
				if (PendingRemovals.TryGetValue(__instance, out HashSet<HkRigidBody> pending) && pending.Contains(rigidBody))
					DoubleRemove(rigidBody, __instance, "direct-while-batched");
			s_removes++;
			Record(rigidBody, "remove", __instance, true);
		}
	}

	[HarmonyPatch(typeof(HkWorld), nameof(HkWorld.RemoveRigidBodyBatch))]
	private static class RemoveBatchPatch
	{
		[HarmonyPrefix]
		private static void Prefix(HkWorld __instance, HkRigidBody rigidBody)
		{
			if (rigidBody != null && !rigidBody.IsDisposed && !rigidBody.InWorldInternal)
				DoubleRemove(rigidBody, __instance, "batched-while-not-in-world");
			if (__instance.IsDisposed) return; // T-0263: HavokDisposedWorldGuard skips it; nothing is pending
			lock (PendingRemovals)
			{
				if (!PendingRemovals.TryGetValue(__instance, out HashSet<HkRigidBody> pending))
					PendingRemovals[__instance] = pending = new HashSet<HkRigidBody>(ReferenceEqualityComparer.Instance);
				pending.Add(rigidBody);
			}
			s_batchRemoves++;
			Record(rigidBody, "remove_batch", __instance, true);
		}
	}

	[HarmonyPatch(typeof(HkWorld), nameof(HkWorld.FinishBatch))]
	private static class FinishBatchPatch
	{
		[HarmonyPrefix]
		private static void Prefix(HkWorld __instance)
		{
			HashSet<HkRigidBody> pending;
			lock (PendingRemovals)
				if (!PendingRemovals.Remove(__instance, out pending)) return;
			s_flushes++;
			foreach (HkRigidBody body in pending)
			{
				if (body == null) continue;
				if (!body.IsDisposed && !body.InWorldInternal)
					DoubleRemove(body, __instance, "flush-of-removed");
				Record(body, "flush", __instance, false);
			}
		}
	}

	[HarmonyPatch(typeof(HkReferenceObject), nameof(HkReferenceObject.RemoveReference))]
	private static class RemoveReferencePatch
	{
		[HarmonyPrefix]
		private static void Prefix(HkReferenceObject __instance)
		{
			if (__instance is HkRigidBody body) Record(body, "remove_reference", null, true);
		}
	}

	/// <summary>Counts the closes by owner kind, so a harness can show that the voxel path ran at all.</summary>
	[HarmonyPatch(typeof(MyPhysicsBody), "CloseRigidBody")]
	private static class ClosePatch
	{
		[HarmonyPrefix]
		private static void Prefix(MyPhysicsBody __instance)
		{
			if (__instance.m_rigidBody == null) return;
			if (__instance is MyVoxelPhysicsBody) s_voxelCloses++;
			else if (__instance.Entity is Sandbox.Game.Entities.MyCubeGrid) s_gridCloses++;
			else s_otherCloses++;
		}
	}

	/// <summary>Once a second, on the game thread: any registered, undisposed body with the dump signature.
	/// Once a minute the path counters, so a harness green can show which paths actually ran.</summary>
	internal static void Update()
	{
		long now = Stopwatch.GetTimestamp();
		if (now - s_lastStats >= 60 * Stopwatch.Frequency)
		{
			s_lastStats = now;
			Console.WriteLine($"[ClusterPlugin] havok_body_audit_stats adds={s_adds} removes={s_removes} batchRemoves={s_batchRemoves} flushes={s_flushes} "
				+ $"voxelCloses={s_voxelCloses} gridCloses={s_gridCloses} otherCloses={s_otherCloses} detected={s_detected} doubleRemoves={s_doubleRemoves} (T-0266)");
		}
		if (now - s_lastScan < Stopwatch.Frequency) return;
		s_lastScan = now;
		if (Registered == null) return;
		try
		{
			foreach (HkHandle handle in Registered.Values)
			{
				if (handle is not HkRigidBody body || body.IsDisposed || Reported.TryGetValue(body, out _)) continue;
				IntPtr native = body.NativeObject;
				int refCount = HkReferenceObject.HkReferenceObject_ReferenceCount(native);
				bool known = HavokReleasedBodyGuard.TryGetAssignedHeader(body, out long assigned);
				long header = Marshal.ReadInt64(native);
				if (refCount > 0 && (!known || header == assigned)) continue;
				Reported.AddOrUpdate(body, null);
				int count = ++s_detected;
				Console.WriteLine($"[ClusterPlugin] havok_body_release_detected {Describe(body)} refCount={refCount} header=0x{header:x} "
					+ $"assigned={(known ? $"0x{assigned:x}" : "unknown")} total={count} history=[{History(body)}] (T-0266)");
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[ClusterPlugin] havok_body_audit_failed error={ex.GetType().Name}: {ex.Message}");
		}
	}
}
