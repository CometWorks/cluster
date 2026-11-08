using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using ClusterPlugin;
using Entities.Blocks;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRage.Game;

// SE-1231: EntityIdProjectorPatch.RemapPrefix ran on the staged handover import's worker threads and
// wrote MyBlueprintIdTracker's static Dictionary there (OnRemap, and SetNewBlueprint's OnRemove and
// OnAdded), racing the other workers and the game thread. Calls the real prefix from parallel threads
// with a distinct projector each, then runs the game thread's invoke queue, and checks that every
// remapped id is tracked for its projector, nothing threw, and the tracker and SetNewBlueprint ran
// on the game thread only.

string dedicated64 = typeof(ProjectorRemapChecks)
    .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .First(a => a.Key == "Dedicated64Dir")
    .Value;
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    string path = Path.Combine(dedicated64, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
};
return ProjectorRemapChecks.Run();

static class ProjectorRemapChecks
{
    const int Threads = 16;
    const int PerThread = 2_000;
    const int BlocksPerGrid = 4;

    static Thread game;
    static int blueprintsSet,
        offThreadBlueprints,
        events,
        offThreadEvents;

    // Constructors would need a running game: SetNewBlueprint only counts here, OnRemap and the
    // prefix use nothing else of the projector
    static bool SetNewBlueprintStub()
    {
        Interlocked.Increment(ref blueprintsSet);
        if (Thread.CurrentThread != game)
            Interlocked.Increment(ref offThreadBlueprints);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run()
    {
        game = Thread.CurrentThread;
        VRage.Utils.MyUtils.MainThread = game;
        EntityIdAllocator.Configure(
            1L << 40,
            (1L << 40) + (1L << 24) - 1,
            () => throw new InvalidOperationException("range exhausted")
        );
        AccessTools.Method(typeof(MyBlueprintIdTracker), "Init").Invoke(null, null);
        var tracked =
            (Dictionary<long, MyProjectorBase>)
                AccessTools
                    .Field(typeof(MyBlueprintIdTracker), "TrackedIdToProjector")
                    .GetValue(null);
        MyBlueprintIdTracker.OnTrackedIdChanged += (_, _) =>
        {
            Interlocked.Increment(ref events);
            if (Thread.CurrentThread != game)
                Interlocked.Increment(ref offThreadEvents);
        };
        new Harmony("SE-1231.test").Patch(
            AccessTools.Method(typeof(MyProjectorBase), "SetNewBlueprint"),
            prefix: new HarmonyMethod(typeof(ProjectorRemapChecks), nameof(SetNewBlueprintStub))
        );

        // The game's invoke queue without the game: the prefix queues to it, Drain runs it
        var sandbox = (MySandboxGame)RuntimeHelpers.GetUninitializedObject(typeof(MySandboxGame));
        FieldInfo queueField = AccessTools.Field(typeof(MySandboxGame), "m_invokeQueue");
        object queue = Activator.CreateInstance(queueField.FieldType, 32);
        queueField.SetValue(sandbox, queue);
        MySandboxGame.Static = sandbox;

        MethodInfo prefix = AccessTools.Method(
            AccessTools.TypeByName("Shared.Patches.EntityIdProjectorPatch"),
            "RemapPrefix"
        );
        FieldInfo builders = AccessTools.Field(typeof(MyProjectorBase), "m_originalGridBuilders");
        long nextOldId = 1;
        var projectors = new MyProjectorBase[Threads * PerThread];
        var grids = new List<MyObjectBuilder_CubeGrid>[projectors.Length];
        for (int i = 0; i < projectors.Length; i++)
        {
            projectors[i] = (MyProjectorBase)
                RuntimeHelpers.GetUninitializedObject(typeof(MySpaceProjector));
            var grid = new MyObjectBuilder_CubeGrid { EntityId = nextOldId++ };
            for (int b = 0; b < BlocksPerGrid; b++)
                grid.CubeBlocks.Add(new MyObjectBuilder_CubeBlock { EntityId = nextOldId++ });
            grids[i] = [grid];
            builders.SetValue(projectors[i], grids[i]);
        }

        // A call on the game thread tracks at once, as before
        prefix.Invoke(null, [projectors[0]]);
        bool inline = tracked.Count == BlocksPerGrid + 1 && blueprintsSet == 1;

        int failures = 0;
        string firstFailure = null;
        // Own threads, not Parallel.For: that runs part of the work on the calling (game) thread
        using var start = new Barrier(Threads);
        Thread[] workers = Enumerable
            .Range(0, Threads)
            .Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                for (int i = Math.Max(1, t * PerThread); i < (t + 1) * PerThread; i++)
                {
                    try
                    {
                        prefix.Invoke(null, [projectors[i]]);
                    }
                    catch (TargetInvocationException e)
                    {
                        if (Interlocked.Increment(ref failures) == 1)
                            firstFailure =
                                e.InnerException?.GetType().Name + ": " + e.InnerException?.Message;
                    }
                }
            }))
            .ToArray();
        foreach (Thread worker in workers)
            worker.Start();
        foreach (Thread worker in workers)
            worker.Join();
        int queued = Drain(queue, ref failures, ref firstFailure);

        int expected = projectors.Length * (BlocksPerGrid + 1);
        int right = 0;
        bool corrupted = false;
        try
        {
            for (int i = 0; i < projectors.Length; i++)
                foreach (MyObjectBuilder_CubeGrid grid in grids[i])
                    right += grid
                        .CubeBlocks.Select(b => b.EntityId)
                        .Append(grid.EntityId)
                        .Count(id =>
                            tracked.TryGetValue(id, out MyProjectorBase p) && p == projectors[i]
                        );
        }
        catch (InvalidOperationException)
        {
            // Racing writers can leave a cycle in the Dictionary's buckets, which even reads detect
            corrupted = true;
        }

        bool ok =
            inline
            && !corrupted
            && failures == 0
            && right == expected
            && tracked.Count == expected
            && blueprintsSet == projectors.Length
            && events == projectors.Length
            && offThreadBlueprints == 0
            && offThreadEvents == 0;
        Console.WriteLine(
            $"{(ok ? "PASS" : "FAIL")}: {Threads} threads x {PerThread} projectors, {failures} exceptions, "
                + $"{(corrupted ? "tracker corrupted, " : "")}{right}/{expected} ids tracked for their projector, tracker Count {tracked.Count}, "
                + $"{queued} queued to the game thread, SetNewBlueprint {blueprintsSet} ({offThreadBlueprints} off the game thread), "
                + $"OnTrackedIdChanged {events} ({offThreadEvents} off the game thread), game-thread call inline: {inline}"
        );
        if (firstFailure != null)
            Console.WriteLine($"  first exception: {firstFailure}");
        return ok ? 0 : 1;
    }

    /// <summary>MySandboxGame.ProcessInvoke without its session and frame checks.</summary>
    static int Drain(object queue, ref int failures, ref string firstFailure)
    {
        MethodInfo tryDequeue = queue.GetType().GetMethod("TryDequeue");
        var args = new object[1];
        int count = 0;
        while ((bool)tryDequeue.Invoke(queue, args))
        {
            count++;
            var action = (Action)AccessTools.Field(args[0].GetType(), "Action").GetValue(args[0]);
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (++failures == 1)
                    firstFailure = e.GetType().Name + ": " + e.Message;
            }
        }
        return count;
    }
}
