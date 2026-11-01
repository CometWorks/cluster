using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Havok;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace ClusterTestPlugin;

/// <summary>
/// T-0274 oracle: is a CLOSED entity still reachable? Track weak references to entities, close them,
/// then force a full blocking GC and ask which are still alive. A leaked closed grid stays alive; a
/// released one is collected. Also reports the Havok wrapper's registered handle count
/// (HkHandle.GetHandlesAmount), which a body orphaned by vanilla MyPhysicsBody.Deactivate keeps high.
/// </summary>
internal static class WeakRefRoutes
{
    private static readonly Dictionary<long, WeakReference> Tracked = new Dictionary<long, WeakReference>();

    public static void Register()
    {
        // POST /weakref-track?entity= - weak-reference the entity AND every grid physically attached to it
        TestRoutes.Add("POST", "/weakref-track", context =>
        {
            long entity = long.Parse(TestRoutes.Query(context, "entity"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!MyEntities.TryGetEntityById(entity, out MyEntity root))
                    throw new ArgumentException("Unknown entity " + entity);
                lock (Tracked) Tracked[entity] = new WeakReference(root);
                return (object)new { entity, tracked = Tracked.Count, registeredHandles = HkHandle.GetHandlesAmount() };
            }));
        });
        // POST /weakref-flush - one no-op through the orchestrator's InvokeLater queue. ProcessInvokeLater
        // ENUMERATES its ConcurrentQueue before dequeuing, which freezes the segment ("preserved for
        // observation"): dequeued slots keep their references until new callbacks move the queue on. An
        // idle bench enqueues nothing, so the last batch (a closed grid's sink callbacks) stays reachable.
        // Call this twice, a frame or more apart, before /weakref-state.
        TestRoutes.Add("POST", "/weakref-flush", context =>
        {
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                MyEntities.Orchestrator.InvokeLater(() => { }, "weakref-flush");
                return (object)new { queued = true };
            }));
        });
        // POST /test-close-physics-first?entity= - T-0274 live order: the grid leaves the physics world
        // FIRST (a lone grid's cluster world is destroyed with it), then the grid closes, so a child body
        // (a connector's detector phantom) is deactivated after its parent's HavokWorld is gone.
        TestRoutes.Add("POST", "/test-close-physics-first", context =>
        {
            long entity = long.Parse(TestRoutes.Query(context, "entity"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!MyEntities.TryGetEntityById(entity, out MyEntity root))
                    throw new ArgumentException("Unknown entity " + entity);
                int before = HkHandle.GetHandlesAmount();
                root.Physics?.Deactivate();
                root.Close();
                return (object)new { entity, handlesBefore = before, handlesAfter = HkHandle.GetHandlesAmount() };
            }));
        });
        // GET /weakref-state - full blocking GC (twice, around the finalizers), then which tracked entities live
        TestRoutes.Add("GET", "/weakref-state", context =>
        {
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                lock (Tracked)
                {
                    var rows = Tracked.Select(pair => new
                    {
                        entity = pair.Key,
                        alive = pair.Value.IsAlive,
                        closed = (pair.Value.Target as MyEntity)?.Closed,
                    }).ToArray();
                    return (object)new { entities = rows, registeredHandles = HkHandle.GetHandlesAmount() };
                }
            }));
        });
    }
}
