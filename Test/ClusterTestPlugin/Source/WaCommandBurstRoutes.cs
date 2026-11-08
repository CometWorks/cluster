using System;
using System.Diagnostics;
using System.Threading;
using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1228 bench routes: a node's game thread waiting on World Authority commands, as it does for a trader's store
/// payment legs. For `ms`, every frame spends up to <see cref="FrameMs"/> sending balance changes to the WA back to back,
/// each waiting for its answer on the game thread with the production 5 s timeout, and the route records how long the
/// answers took. The frames in between keep the node's position reports going (the WA forgets a node's players after
/// 5 s without one). +1 then -1, so the balance ends where it began. Runs in <see cref="Plugin.Update"/>, so the arming
/// request returns at once and does not hold the control listener.
/// </summary>
internal static class WaCommandBurstRoutes
{
    private const long FrameMs = 250;
    private static readonly Stopwatch Watch = new();
    private static long m_owner;
    private static long m_burstMs;
    private static int m_sent, m_timedOut, m_failed;
    private static long m_maxMs;
    private static object m_result;

    public static void Register()
    {
        // POST /test-wa-command-burst?ownerId=&ms= - from the next frame on, balance changes for `ms`.
        TestRoutes.Add(
            "POST",
            "/test-wa-command-burst",
            context =>
            {
                long owner = TestRoutes.Long(context, "ownerId");
                long ms = TestRoutes.Long(context, "ms");
                if (owner <= 0 || ms is <= 0 or > 60000)
                    throw new ArgumentOutOfRangeException(owner <= 0 ? "ownerId" : "ms");
                Volatile.Write(ref m_result, null);
                Interlocked.Exchange(ref m_owner, owner);
                Interlocked.Exchange(ref m_burstMs, ms);
                Plugin.Log($"wa-command-burst armed owner={owner} ms={ms} (SE-1228)");
                TestRoutes.Json(context, 200, new { owner, ms });
            }
        );
        // GET /test-wa-command-burst - the last burst's outcome; done=false while it runs.
        TestRoutes.Add(
            "GET",
            "/test-wa-command-burst",
            context => TestRoutes.Json(context, 200, Volatile.Read(ref m_result) ?? new { done = false })
        );
    }

    public static void Update()
    {
        long burstMs = Interlocked.Read(ref m_burstMs);
        if (burstMs <= 0)
            return;
        if (!Watch.IsRunning)
        {
            m_sent = m_timedOut = m_failed = 0;
            m_maxMs = 0;
            Watch.Restart();
        }
        long owner = Interlocked.Read(ref m_owner);
        long frameEnd = Math.Min(Watch.ElapsedMilliseconds + FrameMs, burstMs);
        while (Watch.ElapsedMilliseconds < frameEnd)
        {
            long started = Watch.ElapsedMilliseconds;
            try
            {
                MyBankingSystemPatch.ChangeBalanceWithOperation(owner, m_sent % 2 == 0 ? 1 : -1, Guid.NewGuid());
            }
            catch (TimeoutException)
            {
                m_timedOut++;
            }
            catch (Exception)
            {
                m_failed++;
            }
            m_maxMs = Math.Max(m_maxMs, Watch.ElapsedMilliseconds - started);
            m_sent++;
        }
        if (Watch.ElapsedMilliseconds < burstMs)
            return;
        Watch.Reset();
        Interlocked.Exchange(ref m_burstMs, 0);
        Volatile.Write(ref m_result, new { done = true, sent = m_sent, timedOut = m_timedOut, failed = m_failed, maxMs = m_maxMs });
        Plugin.Log($"wa-command-burst done sent={m_sent} timedOut={m_timedOut} failed={m_failed} maxMs={m_maxMs} (SE-1228)");
    }
}
