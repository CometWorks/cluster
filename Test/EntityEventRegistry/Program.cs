using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.Utils;

// SE-1313: the staged handover import built entities on parallel worker threads, and every component that
// listens to entity events (the physics body of an inventory bag, for one) registered itself in
// MyEntityContainerEventExtensions' static Dictionary from there, racing the other workers and the game
// thread. Phase 1 registers from 16 threads at once the old way (the known positive: the Dictionary breaks).
// Phase 2 does it the fixed way: the workers register inside EntityEventRegistryPatch.Recording while the
// game thread keeps adding and closing listeners of its own, then the game thread replays what they recorded.
// It checks that nothing threw, that no worker wrote the registry, and that every listener is registered
// after the replay.

string dedicated64 = typeof(EntityEventRegistryChecks)
    .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .First(a => a.Key == "Dedicated64Dir")
    .Value;
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    string path = Path.Combine(dedicated64, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
};
return EntityEventRegistryChecks.Run();

/// <summary>An entity with an id and nothing else, which is all the registry reads.</summary>
public class FakeEntity : DispatchProxy
{
    public long Id;

    protected override object Invoke(MethodInfo method, object[] args) =>
        method.Name == "get_EntityId" ? Id
        : method.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType)
        : null;

    public static IMyEntity Create(long id)
    {
        IMyEntity entity = Create<IMyEntity, FakeEntity>();
        ((FakeEntity)(object)entity).Id = id;
        return entity;
    }
}

public class Probe : MyEntityComponentBase
{
    public override string ComponentTypeDebugString => "SE-1313 probe";
}

static class EntityEventRegistryChecks
{
    const int Threads = 16;
    const int PerThread = 2_000;

    static Probe Listener(long id)
    {
        var probe = new Probe();
        probe.SetContainer(new MyEntityComponentContainer(FakeEntity.Create(id)));
        return probe;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run()
    {
        Thread game = Thread.CurrentThread;
        MyUtils.MainThread = game;
        // Locals, not static fields: the class must load before Main hooks up the game's assemblies
        MyStringHash eventType = MyStringHash.GetOrCompute("SE1313");
        MyEntityContainerEventExtensions.EntityEventHandler Handler = _ => { };
        FieldInfo listenersField = AccessTools.Field(
            typeof(MyEntityContainerEventExtensions),
            "RegisteredListeners"
        );
        MethodInfo onClose = AccessTools.Method(
            typeof(MyEntityContainerEventExtensions),
            "RegisteredEntityOnClose"
        );
        Type patch = typeof(Shared.Patches.EntityEventRegistryPatch);
        var recording =
            (Func<Action, List<Action>>)
                Delegate.CreateDelegate(
                    typeof(Func<Action, List<Action>>),
                    AccessTools.Method(patch, "Recording")
                );

        // Phase 1, the known positive: unrecorded registrations from parallel threads
        MyEntityContainerEventExtensions.InitEntityEvents();
        Probe[] red = Enumerable
            .Range(0, Threads * PerThread)
            .Select(i => Listener(1_000_000 + i))
            .ToArray();
        int redFailures = OnWorkers(
            t =>
            {
                for (int i = t * PerThread; i < (t + 1) * PerThread; i++)
                    red[i].RegisterForEntityEvent(eventType, Handler);
            },
            out string redFirst
        );
        int redRegistered = Count(listenersField, red, out bool redCorrupted);
        bool raced = redFailures > 0 || redCorrupted || redRegistered != red.Length;
        Console.WriteLine(
            $"phase 1, unrecorded: {redFailures} exceptions, {(redCorrupted ? "registry corrupted, " : "")}{redRegistered}/{red.Length} registered"
                + (redFirst != null ? $" (first: {redFirst})" : "")
                + (raced ? " - the race the fix closes" : " - no race seen this run")
        );

        // Phase 2, the fix: Harmony patch on, workers record, the game thread churns its own listeners meanwhile
        new Harmony("SE-1313.test")
            .CreateClassProcessor(patch)
            .Patch();
        MyEntityContainerEventExtensions.InitEntityEvents();
        Probe[] staged = Enumerable
            .Range(0, Threads * PerThread)
            .Select(i => Listener(2_000_000 + i))
            .ToArray();
        var recorded = new List<Action>[Threads];
        int done = 0;
        int churned = 0,
            gameFailures = 0;
        string gameFirst = null;
        var workers = new Thread(() =>
        {
            int failures = OnWorkers(
                t =>
                    recorded[t] = recording(() =>
                    {
                        for (int i = t * PerThread; i < (t + 1) * PerThread; i++)
                            staged[i].RegisterForEntityEvent(eventType, Handler);
                    }),
                out string first
            );
            gameFailures += failures;
            gameFirst ??= first;
            Volatile.Write(ref done, 1);
        });
        workers.Start();
        while (Volatile.Read(ref done) == 0)
        {
            try
            {
                Probe own = Listener(3_000_000 + churned);
                own.RegisterForEntityEvent(eventType, Handler);
                if (churned % 2 == 0)
                    onClose.Invoke(null, [own.Entity]);
                churned++;
            }
            catch (Exception e)
            {
                if (++gameFailures == 1)
                    gameFirst = "game thread: " + e.GetType().Name + ": " + e.Message;
            }
        }
        workers.Join();
        IDictionary listeners = (IDictionary)listenersField.GetValue(null);
        int leaked = staged.Count(p => listeners.Contains(p.Entity.EntityId));
        int gameKept = listeners.Count;
        foreach (List<Action> list in recorded)
        foreach (Action replay in list ?? [])
            replay();
        int registered = Count(listenersField, staged, out bool corrupted);
        int replayed = recorded.Sum(list => list?.Count ?? 0);

        bool ok =
            gameFailures == 0
            && !corrupted
            && leaked == 0
            && replayed == staged.Length
            && registered == staged.Length
            && listeners.Count == gameKept + staged.Length
            && gameKept == churned / 2;
        Console.WriteLine(
            $"{(ok ? "PASS" : "FAIL")}: phase 2, recorded: {Threads} threads x {PerThread} listeners, {gameFailures} exceptions, "
                + $"{leaked} written by a worker, {replayed} recorded and replayed on the game thread, {registered}/{staged.Length} registered, "
                + $"game thread added {churned} listeners and closed half meanwhile ({gameKept} kept), {(corrupted ? "registry corrupted" : "registry intact")}"
        );
        if (gameFirst != null)
            Console.WriteLine($"  first exception: {gameFirst}");
        return ok ? 0 : 1;
    }

    /// <summary>Own threads, not Parallel.For: that one also runs work on the calling (game) thread.</summary>
    static int OnWorkers(Action<int> work, out string firstFailure)
    {
        int failures = 0;
        string first = null;
        using var start = new Barrier(Threads);
        Thread[] threads = Enumerable
            .Range(0, Threads)
            .Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    work(t);
                }
                catch (Exception e)
                {
                    if (Interlocked.Increment(ref failures) == 1)
                        first = e.GetType().Name + ": " + e.Message;
                }
            }))
            .ToArray();
        foreach (Thread thread in threads)
            thread.Start();
        foreach (Thread thread in threads)
            thread.Join();
        firstFailure = first;
        return failures;
    }

    static int Count(FieldInfo listenersField, Probe[] probes, out bool corrupted)
    {
        corrupted = false;
        try
        {
            var listeners = (IDictionary)listenersField.GetValue(null);
            return probes.Count(p => listeners.Contains(p.Entity.EntityId));
        }
        catch (Exception)
        {
            // Racing writers can leave a cycle in the Dictionary's buckets, which even reads detect
            corrupted = true;
            return -1;
        }
    }
}
