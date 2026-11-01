using System;
using System.Threading;
using HarmonyLib;

namespace ClusterTestPlugin;

/// <summary>
/// SE-897 bench route: stall the game thread right after <c>PartitionPersistence.ReleaseEmptied</c>, before the next
/// frame publishes its member counts. The registry thread keeps running, so the empty Final is catalogued and heartbeats
/// run on the counts from before the delete. Live, a slow frame opened the same window in about 2 of 15 deletes.
/// A Harmony postfix installed on first arm, on this test plugin only; inert while nothing is armed.
/// </summary>
internal static class EmptyReleaseStallRoutes
{
    private static int m_milliseconds;
    private static bool m_patched;

    public static void Register()
    {
        // POST /test-empty-release-stall?ms= - arm: the next ReleaseEmptied stalls the game thread for `ms` after it returns.
        TestRoutes.Add(
            "POST",
            "/test-empty-release-stall",
            context =>
            {
                long ms = TestRoutes.Long(context, "ms");
                if (ms is <= 0 or > 10000)
                    throw new ArgumentOutOfRangeException("ms");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        EnsurePatched();
                        Interlocked.Exchange(ref m_milliseconds, (int)ms);
                        Plugin.Log($"empty-release-stall armed ms={ms} (SE-897)");
                        return (object)new { armed = true, ms };
                    })
                );
            }
        );
    }

    private static void EnsurePatched()
    {
        if (m_patched)
            return;
        var type =
            AccessTools.TypeByName("ClusterPlugin.PartitionPersistence")
            ?? throw new TypeLoadException("ClusterPlugin.PartitionPersistence");
        var original =
            AccessTools.Method(type, "ReleaseEmptied")
            ?? throw new MissingMethodException("PartitionPersistence.ReleaseEmptied");
        new Harmony("ClusterTestPlugin.EmptyReleaseStall").Patch(
            original,
            postfix: new HarmonyMethod(
                AccessTools.Method(typeof(EmptyReleaseStallRoutes), nameof(ReleaseEmptiedPostfix))
            )
        );
        m_patched = true;
    }

    private static void ReleaseEmptiedPostfix(ulong partition)
    {
        int ms = Interlocked.Exchange(ref m_milliseconds, 0);
        if (ms == 0)
            return;
        Plugin.Log($"empty-release-stall partition {partition} ms={ms} (SE-897)");
        Thread.Sleep(ms);
    }
}
