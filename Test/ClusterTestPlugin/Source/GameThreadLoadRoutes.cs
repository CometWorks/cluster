using System;
using System.Threading;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1134 bench routes: real frame time on a node, so its simulation ratio and its measured partition loads move the
/// way a busy or hitching node's do. Everything runs in <see cref="Plugin.Update"/>, inside the frame. A main-thread
/// sleep slows the frame (the simulation ratio); work on the game's worker pool is what the node's measured load
/// charges by default (balance.unattributedLoad thread: Sync.ServerThreadLoadSmooth, spread over its partitions by
/// member count).
/// </summary>
internal static class GameThreadLoadRoutes
{
    private static int m_perFrameMs;
    private static int m_hitchMs;
    private static int m_workerMs;
    private static int m_workerItems;

    public static void Register()
    {
        // POST /test-game-thread-load?ms= - sleep `ms` in every frame from now on; 0 stops it.
        TestRoutes.Add(
            "POST",
            "/test-game-thread-load",
            context =>
            {
                int ms = int.Parse(TestRoutes.Query(context, "ms"), System.Globalization.CultureInfo.InvariantCulture);
                if (ms is < 0 or > 1000)
                    throw new ArgumentOutOfRangeException("ms");
                Interlocked.Exchange(ref m_perFrameMs, ms);
                Plugin.Log($"game-thread-load ms={ms} per frame (SE-1134)");
                TestRoutes.Json(context, 200, new { perFrameMs = ms });
            }
        );
        // POST /test-worker-load?ms=&items= - every frame runs `items` work items of `ms` each on the worker pool and
        // waits for them; items 0 stops it.
        TestRoutes.Add(
            "POST",
            "/test-worker-load",
            context =>
            {
                int ms = int.Parse(TestRoutes.Query(context, "ms"), System.Globalization.CultureInfo.InvariantCulture);
                int items = int.Parse(TestRoutes.Query(context, "items"), System.Globalization.CultureInfo.InvariantCulture);
                if (ms is < 0 or > 1000 || items is < 0 or > 64)
                    throw new ArgumentOutOfRangeException(ms is < 0 or > 1000 ? "ms" : "items");
                Interlocked.Exchange(ref m_workerMs, ms);
                Interlocked.Exchange(ref m_workerItems, items);
                Plugin.Log($"worker-load ms={ms} items={items} per frame (SE-1134)");
                TestRoutes.Json(context, 200, new { workerMs = ms, items });
            }
        );
        // POST /test-game-thread-hitch?ms= - one frame sleeps `ms`: a single hitch.
        TestRoutes.Add(
            "POST",
            "/test-game-thread-hitch",
            context =>
            {
                long ms = TestRoutes.Long(context, "ms");
                if (ms is <= 0 or > 10000)
                    throw new ArgumentOutOfRangeException("ms");
                Interlocked.Exchange(ref m_hitchMs, (int)ms);
                Plugin.Log($"game-thread-hitch armed ms={ms} (SE-1134)");
                TestRoutes.Json(context, 200, new { hitchMs = ms });
            }
        );
    }

    public static void Update()
    {
        int workerMs = Volatile.Read(ref m_workerMs), items = Volatile.Read(ref m_workerItems);
        if (workerMs > 0 && items > 0)
            ParallelTasks.Parallel.For(0, items, _ => Thread.Sleep(workerMs));
        int ms = Volatile.Read(ref m_perFrameMs) + Interlocked.Exchange(ref m_hitchMs, 0);
        if (ms > 0)
            Thread.Sleep(ms);
    }
}
