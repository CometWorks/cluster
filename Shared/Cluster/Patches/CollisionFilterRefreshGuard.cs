using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using VRage.Game.Entity;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-1491: a physics body queued for a collision filter refresh must not end the node when its refresh would throw.
///
/// octahedron-test, 2026-11-14 01:59:14Z: the keep-world deploy's drain on local-node-2 had unloaded partitions
/// 43165 and 44261 and was capturing 44262 when the next physics step threw:
///
///     NullReferenceException at MyPhysics.ProcessCollisionFilterRefreshes &lt;- MyPhysics.Simulate
///         &lt;- MySession.UpdateComponents &lt;- MySession.Update &lt;- MySandboxGame.Update
///
/// The exception unwound the game loop and the DS shut down before the drain took 44262 OFFLINE or released the
/// node's memberless partitions. Their leases expired, the registry recorded the node Dead, and the shutdown listed
/// "node:local-node-2" dirty, which held the update until it was resubmitted with acceptDirtyShutdown.
///
/// Vanilla queues a body in <c>MyPhysics.RefreshCollisionFilter</c> and refreshes it in the next step without checking
/// what happened to it in between. A drain closes whole grids in the middle of a frame, so by then a queued entry can
/// be null, or be IsInWorld with a rigid body while its <c>HavokWorld</c> (its own or its top-most parent's) is gone.
/// The vanilla loop throws on each of those. The game log has no line numbers, so which one it was live is not known;
/// this prefix drops every entry the vanilla loop would throw on and logs it, so a recurrence names its entity.
/// A body that is in no Havok world has no filter to refresh, so dropping it loses nothing.
/// </summary>
[HarmonyPatch(typeof(MyPhysics), "ProcessCollisionFilterRefreshes")]
internal static class CollisionFilterRefreshGuard
{
    private static long s_dropped;

    public static long Dropped => Interlocked.Read(ref s_dropped);

    /// <summary>CLUSTER_COLLISION_FILTER_GUARD=0 leaves a stale entry to end the node, as vanilla (the red arm).</summary>
    [HarmonyPrepare]
    private static bool Prepare() =>
        Environment.GetEnvironmentVariable("CLUSTER_COLLISION_FILTER_GUARD") != "0";

    [HarmonyPrefix]
    private static void Prefix()
    {
        HashSet<MyPhysicsBody> pending = MyPhysics.m_pendingCollisionFilterRefreshes;
        if (pending.Count != 0)
            pending.RemoveWhere(Stale);
    }

    /// <summary>True for an entry the vanilla loop would throw on. Follows its checks in order.</summary>
    internal static bool Stale(MyPhysicsBody body)
    {
        string reason;
        try
        {
            if (body == null)
                reason = "null entry";
            else if (!body.IsInWorld)
                return false; // vanilla skips it
            else if (
                body.RigidBody == null
                && body.RigidBody2 == null
                && HitRigidBody(body) == null
            )
                return false;
            else if (body.HavokWorld == null)
                reason = "IsInWorld but no HavokWorld";
            else
                return false;
        }
        catch (Exception exception)
        {
            reason = exception.GetType().Name + " reading the body";
        }
        long dropped = Interlocked.Increment(ref s_dropped);
        MyLog.Default?.WriteLineAndConsole(
            "[ClusterPlugin] collision filter refresh dropped: "
                + reason
                + "; "
                + Probe(body)
                + " (SE-1491, drop "
                + dropped
                + ")"
        );
        return true;
    }

    private static HkRigidBody HitRigidBody(MyPhysicsBody body) =>
        body.CharacterProxy?.GetHitRigidBody();

    private static string Probe(MyPhysicsBody body)
    {
        if (body == null)
            return "body=null";
        try
        {
            MyEntity entity = body.Entity as MyEntity;
            var top = entity?.GetTopMostParent();
            return "entity="
                + (
                    entity == null
                        ? "null"
                        : entity.EntityId
                            + " "
                            + entity.GetType().Name
                            + " '"
                            + entity.DisplayName
                            + "' marked="
                            + entity.MarkedForClose
                            + " closed="
                            + entity.Closed
                )
                + " top="
                + (
                    top == null || top == entity
                        ? "self"
                        : top.EntityId
                            + " "
                            + top.GetType().Name
                            + " physics="
                            + (top.Physics != null)
                )
                + " welded="
                + body.IsWelded
                + " rb="
                + (body.RigidBody != null)
                + " rb2="
                + (body.RigidBody2 != null)
                + " proxy="
                + (body.CharacterProxy != null);
        }
        catch (Exception probe)
        {
            return "probe failed: " + probe.GetType().Name + ": " + probe.Message;
        }
    }
}
