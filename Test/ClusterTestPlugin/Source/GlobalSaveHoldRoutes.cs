using System;
using System.Threading;
using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// #1270 bench routes on the World Authority: hold its global saves written but not registered, so the bench can kill
/// it inside the window between a command's effect and the save that would make it durable. The seam blocks the
/// global-save thread only; the game thread keeps serving, and a held save registers as soon as the hold ends.
/// </summary>
internal static class GlobalSaveHoldRoutes
{
    private static long m_until;      // DateTime.UtcNow ticks; 0 = no hold
    private static long m_heldSaves;

    public static void Register()
    {
        // POST /global-save-register-hold?ms=N - every global save is held before its registration for N ms from now
        // (0 ends the hold). {until, heldSaves, channel} - channel is the global-state status with the held answers.
        TestRoutes.Add("POST", "/global-save-register-hold", context =>
        {
            long ms = TestRoutes.Long(context, "ms");
            if (ms is < 0 or > 600000) throw new ArgumentOutOfRangeException("ms");
            Interlocked.Exchange(ref m_until, ms == 0 ? 0 : DateTime.UtcNow.AddMilliseconds(ms).Ticks);
            ClusterSeams.BeforeGlobalSaveRegister = ms == 0 ? null : Hold;
            Plugin.Log($"global-save-register-hold ms={ms} (#1270)");
            TestRoutes.Json(context, 200, Status());
        });
        TestRoutes.Add("GET", "/global-save-register-hold", context => TestRoutes.Json(context, 200, Status()));
    }

    private static object Status() => new
    {
        until = Interlocked.Read(ref m_until),
        heldSaves = Interlocked.Read(ref m_heldSaves),
        channel = GlobalStateChannel.Status(),
    };

    private static void Hold(uint generation, uint sequence)
    {
        if (DateTime.UtcNow.Ticks >= Interlocked.Read(ref m_until)) return;
        Interlocked.Increment(ref m_heldSaves);
        Plugin.Log($"global save {generation}/{sequence} held before its registration (#1270)");
        while (DateTime.UtcNow.Ticks < Interlocked.Read(ref m_until)) Thread.Sleep(50);
        Plugin.Log($"global save {generation}/{sequence} released to its registration (#1270)");
    }
}
