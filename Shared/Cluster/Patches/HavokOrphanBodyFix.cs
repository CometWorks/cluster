using System;
using Havok;
using HarmonyLib;
using Sandbox.Engine.Physics;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// T-0274, second holder: a vanilla leak that keeps whole CLOSED GRIDS alive.
///
/// MyPhysicsBody.Deactivate(), for a body that is not a physics-cluster object of its own (a child
/// body such as a ship connector's detector phantom), ends in a branch that runs when the top-most
/// parent's HavokWorld is already null:
///
///     RigidBody = null; RigidBody2 = null; CharacterProxy = null;
///
/// with no Dispose. Close() then calls CloseRigidBody(), which finds nothing to dispose. The
/// HkRigidBody stays in the wrapper's static HkHandle.m_registeredHandles map, and its user data
/// points back to the MyPhysicsBody. So the entity (the connector) and, through its sync and
/// replicable, the whole closed MyCubeGrid with every block stay reachable forever. Dump pair
/// node-3 2026-09-29 00:38/01:16: MyCubeGrid 526 -> 761 in 38 min with 169 -> 178 grids that have
/// physics; +240 MyShipConnector. gcroot of a closed MyThrust led to exactly this body: m_rigidBody null,
/// the HkRigidBody's handle live, its HkWorld disposed.
///
/// The trigger: the grid leaves its physics cluster and the cluster world is destroyed before the
/// connector's BeforeDelete closes its detector. The cluster closes grids all the time (the source
/// side of every handover), so it is hit far more often than in a vanilla world.
///
/// Fix: remember the fields before Deactivate. If the method orphaned a body (the field went from a
/// live body to null), put it back and run CloseRigidBody(), the vanilla close path. The T-0266
/// HavokReleasedBodyGuard prefix on CloseRigidBody still screens a natively dead body. The world is
/// already gone, so CheckRBNotInWorld does nothing and Dispose releases the wrapper's own reference
/// and unregisters the handle. If the body's HkWorld outlived the parent (other objects keep the cluster),
/// the body is first removed from it properly, so the world's managed lists do not keep the disposed
/// body. A welded body is left alone: its weld group owns the close.
/// CharacterProxy, the third field that branch drops, is only counted.
/// </summary>
[HarmonyPatch(typeof(MyPhysicsBody), nameof(MyPhysicsBody.Deactivate), new Type[0])]
internal static class HavokOrphanBodyFix
{
	private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);
	private static DateTime s_nextReport = DateTime.UtcNow + ReportInterval;
	private static long s_disposed;
	private static long s_proxiesOrphaned;
	private static long s_failed;
	private static long s_worldRemovals;

	/// <summary>CLUSTER_HAVOK_ORPHAN_FIX=0 leaves vanilla Deactivate alone (the red arm).</summary>
	[HarmonyPrepare]
	private static bool Prepare() => Environment.GetEnvironmentVariable("CLUSTER_HAVOK_ORPHAN_FIX") != "0";

	[HarmonyPrefix]
	private static void Prefix(MyPhysicsBody __instance, out (HkRigidBody, HkRigidBody, MyCharacterProxy) __state)
	{
		// The fields, not the properties: RigidBody resolves to a weld parent's body.
		__state = (__instance.m_rigidBody, __instance.m_rigidBody2, __instance.CharacterProxy);
	}

	[HarmonyPostfix]
	private static void Postfix(MyPhysicsBody __instance, (HkRigidBody, HkRigidBody, MyCharacterProxy) __state)
	{
		(HkRigidBody body, HkRigidBody body2, MyCharacterProxy proxy) = __state;
		bool orphan1 = body != null && __instance.m_rigidBody == null && !body.IsDisposed;
		bool orphan2 = body2 != null && __instance.m_rigidBody2 == null && !body2.IsDisposed;
		if (proxy != null && __instance.CharacterProxy == null)
			s_proxiesOrphaned++;
		if (!orphan1 && !orphan2) return;
		if (__instance.IsWelded || __instance.WeldInfo.Children.Count != 0) return;
		try
		{
			// If the body's world outlived the parent's cluster (other objects keep it), take the body out
			// of it properly first. The vanilla branch skipped that, and CheckRBNotInWorld's
			// HkRigidBody.RemoveFromWorld only removes natively: the managed HkWorld lists
			// (m_rigidBodies, m_activeRigidBodies) would keep the disposed body -> MyPhysicsBody -> grid
			// (bench P1-HVK-ORPHAN 2026-09-29 04:2x, gcroot through HkWorld's active set).
			HkWorld world = __instance.m_world;
			if (world != null && !world.IsDisposed)
			{
				if (orphan1) RemoveFromWorld(world, body);
				if (orphan2) RemoveFromWorld(world, body2);
			}
			if (orphan1) __instance.m_rigidBody = body;
			if (orphan2) __instance.m_rigidBody2 = body2;
			__instance.CloseRigidBody();
			s_disposed += (orphan1 ? 1 : 0) + (orphan2 ? 1 : 0);
		}
		catch (Exception ex)
		{
			s_failed++;
			__instance.m_rigidBody = null;
			__instance.m_rigidBody2 = null;
			MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] havok_orphan_body_close_failed error={ex.GetType().Name}: {ex.Message}");
		}
	}

	private static readonly System.Reflection.MethodInfo RemoveRigidBodyInternal =
		AccessTools.Method(typeof(HkWorld), "RemoveRigidBodyInternal");

	private static void RemoveFromWorld(HkWorld world, HkRigidBody body)
	{
		if (!world.RigidBodies.Contains(body)) return;
		if (body.InWorld)
			world.RemoveRigidBody(body);   // native removal + the managed lists
		else
			RemoveRigidBodyInternal?.Invoke(world, new object[] { body });   // managed lists only
		s_worldRemovals++;
	}

	/// <summary>Game thread, from ClusterRuntime.Update: one count line every 5 minutes. The oracle:
	/// registered_handles should follow the live content, not climb with every closed grid.</summary>
	public static void Update()
	{
		DateTime now = DateTime.UtcNow;
		if (now < s_nextReport) return;
		s_nextReport = now + ReportInterval;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] havok_orphan_bodies disposed=" + s_disposed
			+ " world_removals=" + s_worldRemovals + " proxies_orphaned=" + s_proxiesOrphaned + " failed=" + s_failed
			+ " registered_handles=" + HkHandle.GetHandlesAmount());
	}
}
