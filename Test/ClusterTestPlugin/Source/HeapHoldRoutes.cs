using System;
using System.Collections.Generic;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1615: managed heap held on purpose, so a bench can push the World Authority past wa.heapLimitMb and watch it
/// exit 6. The arrays are never written, so the host's RAM moves far less than the heap.
/// </summary>
internal static class HeapHoldRoutes
{
    private const int ChunkMb = 256;
    private static readonly List<byte[]> Held = new();

    public static void Register()
    {
        // POST /test-heap-hold?mb= - hold `mb` more MB of managed heap; answers the heap size after it.
        TestRoutes.Add(
            "POST",
            "/test-heap-hold",
            context =>
            {
                long mb = TestRoutes.Long(context, "mb");
                if (mb is <= 0 or > 65536)
                    throw new ArgumentOutOfRangeException("mb");
                lock (Held)
                    for (long left = mb; left > 0; left -= ChunkMb)
                        Held.Add(new byte[(int)Math.Min(left, ChunkMb) << 20]);
                long heapMb = GC.GetTotalMemory(false) >> 20;
                Plugin.Log($"heap-hold +{mb} MB, managed heap {heapMb} MB (SE-1615)");
                TestRoutes.Json(context, 200, new { heldMb = mb, heapMb });
            }
        );
    }
}
