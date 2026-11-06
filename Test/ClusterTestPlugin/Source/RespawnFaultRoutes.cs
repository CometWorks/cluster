using System;
using System.Collections.Generic;
using System.Threading;
using ClusterPlugin;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1124 bench routes on a switch target: fault the next respawn or spawn preview it is asked to execute. "split" moves
/// a member of the medbay's partition away and runs the partition pass, so the partition the Gateway just resolved is
/// split on this node right before the target checks it holds the medbay (a trader shuttle leaving Alpha on
/// octahedron-test, 2026-11-05). "refuse" makes the target answer the switch with an error.
/// </summary>
internal static class RespawnFaultRoutes
{
    private static readonly object Sync = new object();
    private static string m_mode;
    private static long m_mover;
    private static double m_distance;
    private static int m_remaining;
    private static readonly HashSet<Guid> Seen = new HashSet<Guid>();
    private static readonly List<object> Faulted = new List<object>();

    public static void Register()
    {
        // POST /respawn-execute-fault?mode=split|refuse|off&count=N[&mover=ENTITY&distance=M] - fault the next N
        // operations this node executes. {mode, remaining, faulted}.
        TestRoutes.Add("POST", "/respawn-execute-fault", context =>
        {
            string mode = TestRoutes.Query(context, "mode");
            if (mode is not ("split" or "refuse" or "off")) throw new ArgumentException("mode must be split, refuse or off");
            lock (Sync)
            {
                m_mode = mode == "off" ? null : mode;
                m_remaining = mode == "off" ? 0 : (int)TestRoutes.Long(context, "count");
                m_mover = mode == "split" ? TestRoutes.Long(context, "mover") : 0;
                m_distance = double.Parse(TestRoutes.Query(context, "distance", "30000"), System.Globalization.CultureInfo.InvariantCulture);
                Faulted.Clear();
            }
            ClusterSeams.BeforeRespawnExecute = mode == "off" ? null : Fault;
            Plugin.Log($"respawn-execute-fault mode={mode} (SE-1124)");
            TestRoutes.Json(context, 200, Status());
        });
        TestRoutes.Add("GET", "/respawn-execute-fault", context => TestRoutes.Json(context, 200, Status()));
    }

    private static object Status()
    {
        lock (Sync) return new { mode = m_mode, remaining = m_remaining, faulted = Faulted.ToArray() };
    }

    private static void Fault(ClusterRespawnExecution request)
    {
        string mode;
        lock (Sync)
        {
            // The Gateway asks again while the target answers not-ready; only an operation's first ask is faulted.
            if (m_remaining <= 0 || !Seen.Add(request.OperationId)) return;
            m_remaining--;
            mode = m_mode;
        }
        if (mode == "refuse")
        {
            lock (Sync) Faulted.Add(new { mode, operation = request.OperationId, preview = request.Preview });
            Plugin.Log($"respawn execute operation={request.OperationId:D} refused by the bench (SE-1124)");
            throw new InvalidOperationException("Respawn execution refused by the SE-1124 bench");
        }
        object split = TestRoutes.OnGameThread(() =>
        {
            if (!MyEntities.TryGetEntityById(m_mover, out MyEntity mover) || !MyEntities.TryGetEntityById(request.GridEntityId, out MyEntity grid))
                throw new InvalidOperationException("Unknown mover or medbay grid");
            mover.PositionComp.SetPosition(grid.PositionComp.GetPosition() + new Vector3D(m_distance, 0, 0));
            if (mover.Physics != null)
            {
                mover.Physics.LinearVelocity = Vector3.Zero;
                mover.Physics.AngularVelocity = Vector3.Zero;
            }
            ClusterControlServer.m_partitions.Evaluate();
            RespawnRegistryService.m_partitions.EntityPartitions().TryGetValue(request.GridEntityId, out ulong partition);
            return new
            {
                mode, operation = request.OperationId, preview = request.Preview, mover = m_mover,
                requested = request.PartitionId, requestedGeneration = request.Generation,
                partition, generation = RespawnRegistryService.m_persistence.OwnedGeneration(partition),
            };
        });
        lock (Sync) Faulted.Add(split);
        Plugin.Log($"respawn execute operation={request.OperationId:D} split by the bench: {System.Text.Json.JsonSerializer.Serialize(split)} (SE-1124)");
    }
}
