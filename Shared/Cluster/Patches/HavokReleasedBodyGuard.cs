using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Havok;
using HarmonyLib;
using Sandbox.Engine.Physics;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// T-0266: a node crashed twice in HkEntity.Dispose from MyPhysicsBody.CloseRigidBody (a
/// MyShipConnector phantom on node-3 at 03:1x, a procedural asteroid on node-3 at 07:35). In both
/// dumps the native hkpRigidBody was already destroyed before the managed Dispose ran: native
/// reference count 0 and a rewritten first qword, while its only managed wrapper was alive and
/// not disposed. Something dropped the wrapper's native reference outside the wrapper; that path is
/// not found yet.
///
/// This guard is defensive. The first qword of a live body is its vtable; the guard records it when
/// the body is assigned to a MyPhysicsBody (it was just created, so it is alive) and compares it at
/// close. A changed first qword, or a reference count of 0 or less on an undisposed wrapper, means the
/// native object is gone - freed, or freed and its memory already reused (the bench known positive
/// reads a positive count there, so the count alone misses it). Disposing it would call into freed
/// memory. For such a body the guard skips every native call and does the managed half of
/// HkRigidBody.Dispose itself: it removes the wrapper as an owner of its shape (a stale owner makes a
/// later HkShape.RemoveReference read the dead body's world), unregisters the handle (so a later
/// native object at the same address cannot resolve to this wrapper), clears the wrapper and nulls the
/// field, so vanilla CloseRigidBody finds no body to close. Each event logs
/// `havok_body_released_early` with the entity, so the path that released the body can be found.
/// The native listeners of the dead body leak; that is a few small objects per event.
/// </summary>
[HarmonyPatch(typeof(MyPhysicsBody), "CloseRigidBody")]
internal static class HavokReleasedBodyGuard
{
	private static readonly ConcurrentDictionary<IntPtr, HkHandle> RegisteredHandles =
		AccessTools.Field(typeof(HkHandle), "m_registeredHandles")?.GetValue(null) as ConcurrentDictionary<IntPtr, HkHandle>;

	private static readonly Dictionary<HkShape, (HkRigidBody, HashSet<HkRigidBody>, bool)> ShapeOwners =
		AccessTools.Field(typeof(HkShape), "m_owners")?.GetValue(null) as Dictionary<HkShape, (HkRigidBody, HashSet<HkRigidBody>, bool)>;

	/// <summary>The first native qword of each body, read when the body was assigned (alive).</summary>
	private static readonly ConditionalWeakTable<HkRigidBody, StrongBox<long>> AssignedHeaders = new ConditionalWeakTable<HkRigidBody, StrongBox<long>>();

	private static int total;

	internal static void Remember(HkRigidBody body, MyPhysicsBody owner = null)
	{
		if (body == null || body.IsDisposed)
			return;
		AssignedHeaders.AddOrUpdate(body, new StrongBox<long>(Marshal.ReadInt64(body.NativeObject)));
		if (owner != null) HavokBodyAudit.RememberOwner(body, owner);
	}

	internal static bool TryGetAssignedHeader(HkRigidBody body, out long header)
	{
		bool known = AssignedHeaders.TryGetValue(body, out StrongBox<long> box);
		header = known ? box.Value : 0;
		return known;
	}

	/// <summary>CLUSTER_HAVOK_GUARD=0 leaves vanilla CloseRigidBody alone (the red arm of the T-0266 harness);
	/// the assigned-header bookkeeping the audit reads stays on.</summary>
	[HarmonyPrepare]
	private static bool Prepare() => Environment.GetEnvironmentVariable("CLUSTER_HAVOK_GUARD") != "0";

	[HarmonyPrefix]
	private static void Prefix(MyPhysicsBody __instance)
	{
		try
		{
			// The fields, not the properties: RigidBody resolves to the weld parent's body, which this
			// entity does not own.
			if (IsReleased(__instance, __instance.m_rigidBody, "RigidBody"))
				__instance.m_rigidBody = null;
			if (IsReleased(__instance, __instance.m_rigidBody2, "RigidBody2"))
				__instance.m_rigidBody2 = null;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[ClusterPlugin] havok_body_guard_failed error={ex.GetType().Name}: {ex.Message}");
		}
	}

	private static bool IsReleased(MyPhysicsBody physics, HkRigidBody body, string field)
	{
		if (body == null || body.IsDisposed)
			return false;
		IntPtr handle = body.NativeObject;
		long header = Marshal.ReadInt64(handle);
		bool known = AssignedHeaders.TryGetValue(body, out StrongBox<long> assigned);
		int refCount = HkReferenceObject.HkReferenceObject_ReferenceCount(handle);
		if (refCount > 0 && (!known || assigned.Value == header))
			return false;

		int owners = RemoveShapeOwner(body);
		bool unregistered = RegisteredHandles != null
			&& ((ICollection<KeyValuePair<IntPtr, HkHandle>>)RegisteredHandles).Remove(new KeyValuePair<IntPtr, HkHandle>(handle, body));
		string history = HavokBodyAudit.History(body);
		body.ClearHandle();
		AssignedHeaders.Remove(body);

		MyEntity entity = physics.Entity as MyEntity;
		int count = Interlocked.Increment(ref total);
		Console.WriteLine(
			$"[ClusterPlugin] havok_body_released_early entity={entity?.GetType().Name ?? "null"}:{entity?.EntityId ?? 0} " +
			$"name={entity?.DisplayName ?? entity?.Name} field={field} refCount={refCount} header=0x{header:x} " +
			$"assigned={(known ? $"0x{assigned.Value:x}" : "unknown")} handle=0x{handle.ToInt64():x} shapeOwners={owners} " +
			$"unregistered={unregistered} inWorld={physics.IsInWorld} markedForClose={entity?.MarkedForClose} " +
			$"total={count} (native close skipped, T-0266) history=[{history}]");
		return true;
	}

	/// <summary>HkShape.RemoveOwner without the native GetShape() read: find the body among the owners.</summary>
	private static int RemoveShapeOwner(HkRigidBody body)
	{
		if (ShapeOwners == null)
			return -1;
		int removed = 0;
		using (HkShape.SharedCriticalSection.EnterUnique())
		{
			foreach (HkShape shape in ShapeOwners.Keys.ToArray())
			{
				(HkRigidBody single, HashSet<HkRigidBody> multiple, bool readOnly) = ShapeOwners[shape];
				if (single == body)
				{
					removed++;
					if (readOnly) ShapeOwners[shape] = (null, multiple, true);
					else ShapeOwners.Remove(shape);
				}
				else if (multiple != null && multiple.Remove(body))
				{
					removed++;
					if (multiple.Count == 0)
					{
						if (readOnly) ShapeOwners[shape] = (single, null, true);
						else ShapeOwners.Remove(shape);
					}
				}
			}
		}
		return removed;
	}
}

[HarmonyPatch(typeof(MyPhysicsBody), "RigidBody", MethodType.Setter)]
internal static class HavokReleasedBodyGuardRigidBody
{
	[HarmonyPostfix]
	private static void Postfix(MyPhysicsBody __instance) => HavokReleasedBodyGuard.Remember(__instance.m_rigidBody, __instance);
}

[HarmonyPatch(typeof(MyPhysicsBody), "RigidBody2", MethodType.Setter)]
internal static class HavokReleasedBodyGuardRigidBody2
{
	[HarmonyPostfix]
	private static void Postfix(MyPhysicsBody __instance) => HavokReleasedBodyGuard.Remember(__instance.m_rigidBody2, __instance);
}
