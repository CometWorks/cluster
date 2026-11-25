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
    private static readonly Dictionary<long, WeakReference> Tracked =
        new Dictionary<long, WeakReference>();

    private static readonly Dictionary<long, MyEntity> Held = new();
    private static readonly Dictionary<long, (WeakReference Block, WeakReference Physics)> Parts =
        new();

    private static readonly Dictionary<long, int> ClosedEvents = new();

    private static void Closed(VRage.ModAPI.IMyEntity target)
    {
        lock (Tracked)
            ClosedEvents[target.EntityId]++;
        target.OnClose -= Closed;
    }

    public static void Register()
    {
        // Track the looked-up instance; a handover probe can also retain an explicit strong root.
        TestRoutes.Add(
            "POST",
            "/weakref-track",
            context =>
            {
                long entity = long.Parse(
                    TestRoutes.Query(context, "entity"),
                    CultureInfo.InvariantCulture
                );
                bool hold = TestRoutes.Query(context, "strong", "false") == "true";
                bool parts = TestRoutes.Query(context, "parts", "false") == "true";
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        if (!MyEntities.TryGetEntityById(entity, out MyEntity root))
                            throw new ArgumentException("Unknown entity " + entity);
                        lock (Tracked)
                        {
                            Tracked[entity] = new WeakReference(root);
                            if (parts)
                            {
                                ClosedEvents[entity] = 0;
                                var modEntity = (VRage.ModAPI.IMyEntity)root;
                                modEntity.OnClose -= Closed;
                                modEntity.OnClose += Closed;
                            }
                            if (hold)
                                Held[entity] = root;
                            if (parts && root is MyCubeGrid grid)
                                Parts[entity] = (
                                    new WeakReference(grid.GetBlocks().FirstOrDefault()),
                                    new WeakReference(grid.Physics)
                                );
                        }
                        return (object)
                            new
                            {
                                entity,
                                tracked = Tracked.Count,
                                registeredHandles = HkHandle.GetHandlesAmount(),
                            };
                    })
                );
            }
        );
        TestRoutes.Add(
            "POST",
            "/weakref-release",
            context =>
            {
                long entity = TestRoutes.Long(context, "entity");
                lock (Tracked)
                    TestRoutes.Json(context, 200, new { removed = Held.Remove(entity) });
            }
        );
        TestRoutes.Add(
            "GET",
            "/weakref-lookup",
            context =>
            {
                long entity = TestRoutes.Long(context, "entity");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        bool found = MyEntities.TryGetEntityById(entity, out MyEntity local);
                        lock (Tracked)
                        {
                            Tracked.TryGetValue(entity, out var weak);
                            var cached = weak?.Target as MyEntity;
                            Parts.TryGetValue(entity, out var parts);
                            return (object)
                                new
                                {
                                    process = Environment.ProcessId,
                                    entity,
                                    found,
                                    localId = local?.EntityId,
                                    localType = local?.GetType().Name,
                                    fatBlocks = (local as MyCubeGrid)
                                        ?.GetBlocks()
                                        .Where(block => block.FatBlock != null)
                                        .Select(block => block.FatBlock.EntityId)
                                        .ToArray(),
                                    localClosed = local?.Closed,
                                    localBlocks = (local as MyCubeGrid)?.BlocksCount,
                                    cachedId = cached?.EntityId,
                                    cachedType = cached?.GetType().Name,
                                    cachedClosed = cached?.Closed,
                                    cachedBlocks = (cached as MyCubeGrid)?.BlocksCount,
                                    sampledBlockAlive = parts.Block?.IsAlive,
                                    sampledPhysicsAlive = parts.Physics?.IsAlive,
                                    sameLocalInstance = found
                                        && cached != null
                                        && ReferenceEquals(local, cached),
                                    held = Held.ContainsKey(entity),
                                    sourceCloseEvents = ClosedEvents.TryGetValue(
                                        entity,
                                        out var count
                                    )
                                        ? (int?)count
                                        : null,
                                };
                        }
                    })
                );
            }
        );
        // POST /weakref-flush - one no-op through the orchestrator's InvokeLater queue. ProcessInvokeLater
        // ENUMERATES its ConcurrentQueue before dequeuing, which freezes the segment ("preserved for
        // observation"): dequeued slots keep their references until new callbacks move the queue on. An
        // idle bench enqueues nothing, so the last batch (a closed grid's sink callbacks) stays reachable.
        // Call this twice, a frame or more apart, before /weakref-state.
        TestRoutes.Add(
            "POST",
            "/weakref-flush",
            context =>
            {
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        MyEntities.Orchestrator.InvokeLater(() => { }, "weakref-flush");
                        return (object)new { queued = true };
                    })
                );
            }
        );
        // POST /test-close-physics-first?entity= - T-0274 live order: the grid leaves the physics world
        // FIRST (a lone grid's cluster world is destroyed with it), then the grid closes, so a child body
        // (a connector's detector phantom) is deactivated after its parent's HavokWorld is gone.
        TestRoutes.Add(
            "POST",
            "/test-close-physics-first",
            context =>
            {
                long entity = long.Parse(
                    TestRoutes.Query(context, "entity"),
                    CultureInfo.InvariantCulture
                );
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        if (!MyEntities.TryGetEntityById(entity, out MyEntity root))
                            throw new ArgumentException("Unknown entity " + entity);
                        int before = HkHandle.GetHandlesAmount();
                        root.Physics?.Deactivate();
                        root.Close();
                        return (object)
                            new
                            {
                                entity,
                                handlesBefore = before,
                                handlesAfter = HkHandle.GetHandlesAmount(),
                            };
                    })
                );
            }
        );
        // GET /weakref-state - full blocking GC (twice, around the finalizers), then which tracked entities live
        TestRoutes.Add(
            "GET",
            "/weakref-state",
            context =>
            {
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                        lock (Tracked)
                        {
                            var rows = Tracked
                                .Select(pair => new
                                {
                                    entity = pair.Key,
                                    alive = pair.Value.IsAlive,
                                    closed = (pair.Value.Target as MyEntity)?.Closed,
                                })
                                .ToArray();
                            return (object)
                                new
                                {
                                    entities = rows,
                                    registeredHandles = HkHandle.GetHandlesAmount(),
                                };
                        }
                    })
                );
            }
        );
    }
}
