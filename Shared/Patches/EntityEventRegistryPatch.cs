using System;
using System.Collections.Generic;
using HarmonyLib;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// SE-1313. MyEntityContainerEventExtensions keeps every entity-event listener in static collections
/// without a lock (RegisteredListeners, ExternalListeners, ExternalyListenedEntities), and the game
/// thread writes them whenever a component that listens is added or an entity that has listeners
/// closes. Vanilla's parallel world load keeps its workers out of them: MyEntities.Load sets
/// SkipProcessingEvents(true) around its Parallel.For, so the workers' registrations are postponed
/// and replayed on the main thread. The staged handover import builds entities on a background
/// thread while the game runs, where that switch would hit the game thread too: its own
/// registrations would be postponed and, after five nested events, its events dropped. So the
/// import's builder records the (un)registrations of the entity it builds instead
/// (<see cref="Recording"/>), and the game thread replays them when it activates that entity.
/// </summary>
[HarmonyPatch(typeof(MyEntityContainerEventExtensions))]
public static class EntityEventRegistryPatch
{
    [ThreadStatic]
    private static List<Action> t_recorded;

    /// <summary>Runs <paramref name="build"/> with this thread's entity-event (un)registrations recorded
    /// instead of applied. Returns them in call order for the game thread to replay, null if none.</summary>
    internal static List<Action> Recording(Action build)
    {
        if (MyUtils.MainThread == System.Threading.Thread.CurrentThread)
            throw new InvalidOperationException(
                "Entity-event registrations are recorded off the game thread only"
            );
        List<Action> recorded = t_recorded = new List<Action>();
        try
        {
            build();
        }
        finally
        {
            t_recorded = null;
        }
        return recorded.Count == 0 ? null : recorded;
    }

    [HarmonyPrefix]
    [HarmonyPatch(
        nameof(MyEntityContainerEventExtensions.RegisterForEntityEvent),
        typeof(MyEntityComponentBase),
        typeof(MyStringHash),
        typeof(MyEntityContainerEventExtensions.EntityEventHandler)
    )]
    private static bool RegisterPrefix(
        MyEntityComponentBase component,
        MyStringHash eventType,
        MyEntityContainerEventExtensions.EntityEventHandler handler
    )
    {
        if (t_recorded == null)
            return true;
        t_recorded.Add(() => component.RegisterForEntityEvent(eventType, handler));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(
        nameof(MyEntityContainerEventExtensions.RegisterForEntityEvent),
        typeof(MyEntityComponentBase),
        typeof(MyEntity),
        typeof(MyStringHash),
        typeof(MyEntityContainerEventExtensions.EntityEventHandler)
    )]
    private static bool RegisterOnPrefix(
        MyEntityComponentBase component,
        MyEntity entity,
        MyStringHash eventType,
        MyEntityContainerEventExtensions.EntityEventHandler handler
    )
    {
        if (t_recorded == null)
            return true;
        t_recorded.Add(() => component.RegisterForEntityEvent(entity, eventType, handler));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyEntityContainerEventExtensions.UnregisterForEntityEvent))]
    private static bool UnregisterPrefix(
        MyEntityComponentBase component,
        MyEntity entity,
        MyStringHash eventType
    )
    {
        if (t_recorded == null)
            return true;
        t_recorded.Add(() => component.UnregisterForEntityEvent(entity, eventType));
        return false;
    }
}
