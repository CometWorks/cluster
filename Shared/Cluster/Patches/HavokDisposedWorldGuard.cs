using System;
using System.Threading;
using Havok;
using HarmonyLib;
using Sandbox.Engine.Physics;

namespace ClusterPlugin;

/// <summary>
/// T-0263: a batch call on a DISPOSED HkWorld writes into freed native memory.
///
/// HkWorld.Dispose calls HkWorld_Release, which free()s the add and remove batch buffers but leaves
/// m_addBatch/m_removeBatch and their sizes as they were. RemoveRigidBodyBatch has no disposed check:
/// EnsureBatchSizes returns the stale pointer (size 128 >= count) and HkWorld_SetBatchBody writes the
/// body pointer into it. Vanilla MyPhysicsBody.DeactivateBatch reaches that with a ship connector's
/// detector body whose m_world is a cluster world disposed before the detector was taken out
/// (log: havok_double_remove kind=batched-while-not-in-world world=0x0). The freed 176-byte chunk
/// is soon reused - by a PAL semaphore in both crash dumps - and the node dies minutes later in an
/// unrelated finalizer (FastResourceLock.Finalize -> CloseHandle -> call through the overwritten
/// vtable slot = rip 0): node-4 2026-09-27 06:08, node-2 2026-09-29 12:03 (three writes at 11:58:42,
/// the world's m_removeBatchCount 3, the three connector bodies in the semaphore's first three qwords).
///
/// The guard: (1) Dispose zeroes the batch pointers, sizes and counts after the release, so any later
/// EnsureBatchSizes allocates a fresh buffer instead of reusing freed memory; (2) the batch calls and
/// FinishBatch on a disposed world do nothing native: RemoveRigidBodyBatch still runs the managed
/// bookkeeping of a removal (events, lists), which is what vanilla does for the managed side. Each
/// skipped call logs `havok_disposed_world_batch` with a running total. CLUSTER_HAVOK_DISPOSED_WORLD_GUARD=0
/// disables it.
/// </summary>
internal static class HavokDisposedWorldGuard
{
	private static readonly AccessTools.FieldRef<HkWorld, IntPtr> AddBatch = AccessTools.FieldRefAccess<HkWorld, IntPtr>("m_addBatch");
	private static readonly AccessTools.FieldRef<HkWorld, IntPtr> RemoveBatch = AccessTools.FieldRefAccess<HkWorld, IntPtr>("m_removeBatch");
	private static readonly AccessTools.FieldRef<HkWorld, int> AddBatchSize = AccessTools.FieldRefAccess<HkWorld, int>("m_addBatchSize");
	private static readonly AccessTools.FieldRef<HkWorld, int> AddBatchCount = AccessTools.FieldRefAccess<HkWorld, int>("m_addBatchCount");
	private static readonly AccessTools.FieldRef<HkWorld, int> RemoveBatchSize = AccessTools.FieldRefAccess<HkWorld, int>("m_removeBatchSize");
	private static readonly AccessTools.FieldRef<HkWorld, int> RemoveBatchCount = AccessTools.FieldRefAccess<HkWorld, int>("m_removeBatchCount");
	private static readonly Action<HkWorld, HkRigidBody> RemoveInternal =
		AccessTools.MethodDelegate<Action<HkWorld, HkRigidBody>>(AccessTools.Method(typeof(HkWorld), "RemoveRigidBodyInternal"));

	private static long s_skipped;
	internal static long Skipped => Interlocked.Read(ref s_skipped);

	private static bool Enabled => Environment.GetEnvironmentVariable("CLUSTER_HAVOK_DISPOSED_WORLD_GUARD") != "0";

	/// <summary>The world's batch pointers, for the self-test: both Zero once it is disposed.</summary>
	internal static (IntPtr Add, IntPtr Remove) BatchPointers(HkWorld world) => (AddBatch(world), RemoveBatch(world));

	private static void Skip(string op, HkWorld world, HkRigidBody body)
	{
		long total = Interlocked.Increment(ref s_skipped);
		string entity = (body?.UserObject as MyPhysicsBody)?.Entity is { } e ? $"{e.GetType().Name} {e.EntityId}" : "?";
		Console.WriteLine($"[ClusterPlugin] havok_disposed_world_batch op={op} body=0x{(body?.NativeObject ?? IntPtr.Zero).ToInt64():x} "
			+ $"entity={entity} total={total} (T-0263: skipped, no write into the released batch buffer)");
	}

	[HarmonyPatch(typeof(HkWorld), "Dispose", typeof(bool))]
	private static class DisposePatch
	{
		private static bool Prepare() => Enabled;

		[HarmonyPostfix]
		private static void Postfix(HkWorld __instance)
		{
			// HkWorld_Release has freed both buffers: forget them so nothing can write through them.
			AddBatch(__instance) = IntPtr.Zero;
			RemoveBatch(__instance) = IntPtr.Zero;
			AddBatchSize(__instance) = 0;
			AddBatchCount(__instance) = 0;
			RemoveBatchSize(__instance) = 0;
			RemoveBatchCount(__instance) = 0;
			HavokBodyAudit.Forget(__instance);
		}
	}

	[HarmonyPatch(typeof(HkWorld), nameof(HkWorld.RemoveRigidBodyBatch))]
	private static class RemoveBatchPatch
	{
		private static bool Prepare() => Enabled;

		[HarmonyPrefix, HarmonyPriority(Priority.First)]
		private static bool Prefix(HkWorld __instance, HkRigidBody rigidBody)
		{
			if (!__instance.IsDisposed) return true;
			Skip("remove_batch", __instance, rigidBody);
			if (rigidBody != null) RemoveInternal(__instance, rigidBody);
			return false;
		}
	}

	[HarmonyPatch(typeof(HkWorld), nameof(HkWorld.AddRigidBodyBatch))]
	private static class AddBatchPatch
	{
		private static bool Prepare() => Enabled;

		[HarmonyPrefix, HarmonyPriority(Priority.First)]
		private static bool Prefix(HkWorld __instance, HkRigidBody rigidBody)
		{
			if (!__instance.IsDisposed) return true;
			Skip("add_batch", __instance, rigidBody);
			return false;
		}
	}

	[HarmonyPatch(typeof(HkWorld), nameof(HkWorld.EnsureBatchSizes))]
	private static class EnsurePatch
	{
		private static bool Prepare() => Enabled;

		[HarmonyPrefix, HarmonyPriority(Priority.First)]
		private static bool Prefix(HkWorld __instance) => !__instance.IsDisposed;
	}

	[HarmonyPatch(typeof(HkWorld), nameof(HkWorld.FinishBatch))]
	private static class FinishBatchPatch
	{
		private static bool Prepare() => Enabled;

		[HarmonyPrefix, HarmonyPriority(Priority.First)]
		private static bool Prefix(HkWorld __instance)
		{
			if (!__instance.IsDisposed) return true;
			HavokBodyAudit.Forget(__instance);
			return false;
		}
	}
}
