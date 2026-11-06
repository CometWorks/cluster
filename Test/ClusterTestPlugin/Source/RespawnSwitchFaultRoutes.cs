using System;
using System.Collections.Generic;
using System.Threading;
using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1189 bench routes on the node a dead client previews a spawn point from: fault the next spawn preview switch it
/// asks the Gateway for. "hold" keeps the request on this node until released (or the hold times out), so the bench can
/// split the medbay's partition away in between, the order of events on octahedron-test (2026-11-06 19:07:20-21Z: a
/// trader's fast split retired Alpha's partition 300 ms before Viktor picked Alpha). "refuse" throws where the Gateway's
/// answer would come, a refusal before the Gateway moved the client's backend.
/// </summary>
internal static class RespawnSwitchFaultRoutes
{
    private static readonly object Sync = new object();
    private static string m_mode;
    private static int m_remaining;
    private static double m_seconds;
    private static ManualResetEventSlim m_release = new ManualResetEventSlim(false);
    private static readonly List<object> Faulted = new List<object>();
    private static Guid m_held;

    public static void Register()
    {
        // POST /respawn-switch-fault?mode=hold|refuse|off&count=N[&seconds=S] - fault the next N preview switches this
        // node asks for; a hold lasts until /respawn-switch-fault-release or S seconds (default 60). {mode, remaining,
        // held, faulted}.
        TestRoutes.Add("POST", "/respawn-switch-fault", context =>
        {
            string mode = TestRoutes.Query(context, "mode");
            if (mode is not ("hold" or "refuse" or "off")) throw new ArgumentException("mode must be hold, refuse or off");
            lock (Sync)
            {
                m_mode = mode == "off" ? null : mode;
                m_remaining = mode == "off" ? 0 : (int)TestRoutes.Long(context, "count");
                m_seconds = double.Parse(TestRoutes.Query(context, "seconds", "60"), System.Globalization.CultureInfo.InvariantCulture);
                m_release.Set();
                m_release = new ManualResetEventSlim(false);
                Faulted.Clear();
            }
            ClusterSeams.BeforeSwitchPost = mode == "off" ? null : Fault;
            Plugin.Log($"respawn-switch-fault mode={mode} (SE-1189)");
            TestRoutes.Json(context, 200, Status());
        });
        TestRoutes.Add("POST", "/respawn-switch-fault-release", context =>
        {
            lock (Sync) m_release.Set();
            TestRoutes.Json(context, 200, Status());
        });
        TestRoutes.Add("GET", "/respawn-switch-fault", context => TestRoutes.Json(context, 200, Status()));
    }

    private static object Status()
    {
        lock (Sync) return new { mode = m_mode, remaining = m_remaining, held = m_held, faulted = Faulted.ToArray() };
    }

    private static void Fault(ClusterRespawnExecution request)
    {
        string mode;
        ManualResetEventSlim release;
        lock (Sync)
        {
            if (m_remaining <= 0) return;
            m_remaining--;
            mode = m_mode;
            release = m_release;
            Faulted.Add(new { mode, operation = request.OperationId, partition = request.PartitionId, generation = request.Generation });
        }
        if (mode == "refuse")
        {
            Plugin.Log($"spawn preview switch operation={request.OperationId:D} refused by the bench (SE-1189)");
            throw new InvalidOperationException("Gateway respawn switch refused by the SE-1189 bench");
        }
        lock (Sync) m_held = request.OperationId;
        Plugin.Log($"spawn preview switch operation={request.OperationId:D} held by the bench partition={request.PartitionId} (SE-1189)");
        bool released = release.Wait(TimeSpan.FromSeconds(m_seconds));
        lock (Sync) m_held = Guid.Empty;
        Plugin.Log($"spawn preview switch operation={request.OperationId:D} {(released ? "released" : "hold timed out")} by the bench (SE-1189)");
    }
}
