using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace ClusterPlugin;

// Cluster owns admission/scheduling, authority time, Final persistence and apply
// rights. Concealment owns the only profile/model/capture implementation. The
// versioned seam also supports Magnetar's source-compiled assembly names.
internal static class OfflineCompensation
{
    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private static Type api;
    internal static object Call(string method, params object[] args)
    {
        if (api == null)
        {
            var candidates = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Concealment.Accounting.AccountingApi", false)).Where(t => t != null).ToArray();
            if (candidates.Length != 1 || (int)candidates[0].GetField("Contract").GetRawConstantValue() != 3)
                throw new InvalidOperationException("One compatible Concealment accounting plugin is required (contract 3).");
            var selected = candidates[0];
            var callbacks = new Dictionary<string, object>
            {
                ["clockFrameSeconds"] = 1.0 / 60,
                ["clock"] = (Func<double>)(() => AccountingClock.NativeMilliseconds),
                ["nativeFrame"] = (Func<long>)(() => AccountingClock.NativeFrame),
                ["nativeEpoch"] = (Func<string>)(() => AccountingClock.NativeEpoch),
                ["authorityClock"] = (Func<(string Epoch, long Frame, long Generation, long SunTicks)>)AuthorityClock,
                ["sunPath"] = (Func<string, string, long, long, byte[]>)AccountingClock.SunPath,
                ["sunClock"] = (Func<long>)(() => AccountingClock.SunTicks(MySession.Static)),
                ["setting"] = (Func<string, string>)ClusterConfig.Get,
                ["admit"] = (Func<ulong, uint, bool>)((scope, generation) => !ClusterRuntime.IsClustered || AccountingClock.Ready && ClusterRuntime.CanProfile(scope, generation)),
                ["generation"] = (Func<ulong, uint>)(scope => ClusterRuntime.PluginOwnershipSnapshot().TryGetValue(scope, out long g) ? checked((uint)g) : 0),
                ["observe"] = (Func<bool>)(() => ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority && ClusterRuntime.IsServing),
                ["frozen"] = (Func<long, bool>)ClusterRuntime.IsFrozen,
                ["measure"] = (Func<MyCubeGrid[], bool>)(grids => !RemoteUse.BlocksProfiling(grids.Select(g => g.EntityId))
                    && !PositionRegistryService.Players.Any(p => grids.Any(g => { var v = g.PositionComp.GetPosition(); double d = ClusterConfig.GetInt("offline.quietMeters"); return p.IsWithinDistance(v.X, v.Y, v.Z, d*d); }))),
                ["zonePending"] = (Func<MySafeZoneBlock, bool>)BlockSafeZones.AwaitingPower,
            };
            Invoke(selected, "Configure", "Cluster", callbacks);
            api = selected;
        }
        return Invoke(api, method, args);
    }
    private static object Invoke(Type type, string method, params object[] args)
    {
        try { return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    internal static bool ContentReady => (bool)Call("ContentReady");
    internal static string Readiness(MyCubeGrid grid) => (string)Call("Readiness", grid);
    private static MyCubeGrid[] Occluders(PartitionManager.Partition partition) => partition.Members.Select(id => MyEntities.GetEntityById(id)).OfType<MyCubeGrid>().OrderBy(g => g.EntityId).ToArray();
    private static MyCubeGrid[][] Groups(PartitionManager.Partition partition)
    {
        var remaining = Occluders(partition).ToDictionary(g => g.EntityId);
        var result = new List<MyCubeGrid[]>();
        foreach (var grid in remaining.Values.ToArray())
        {
            if (!remaining.ContainsKey(grid.EntityId)) continue;
            var logical = MyCubeGridGroups.Static.Logical.GetGroup(grid);
            var group = logical == null ? new[] { grid } : logical.Nodes.Select(n => n.NodeData).OrderBy(g => g.EntityId).ToArray();
            if (group.Any(g => !partition.Members.Contains(g.EntityId))) throw new InvalidOperationException("Logical group crosses partition boundary");
            foreach (var member in group) remaining.Remove(member.EntityId);
            result.Add(group);
        }
        return result.ToArray();
    }
    internal static bool Prepare(PartitionManager.Partition partition, bool quick = false) => !ClusterConfig.GetBool("offline.compensate") || (bool)Call("Prepare", partition.Id, Groups(partition), Occluders(partition), quick);
    internal static byte[] CaptureFor(PartitionManager.Partition partition, bool quick = false) => !ClusterConfig.GetBool("offline.compensate") ? null : (byte[])Call("Capture", partition.Id, Groups(partition), Occluders(partition), quick);
    internal static byte[] CaptureForDrain(PartitionManager.Partition partition) => DrainCapture(partition.Id, () => CaptureFor(partition, true));

    // SE-1091: the drain runs on the game thread inside ClusterRuntime.Update, where nothing catches, and its Final is the
    // partition's last save. A capture that threw there (the World Authority's answer to the fresh clock read never came)
    // ended the node with its Finals unwritten, and the stop ended dirty. So the drain falls back to the durable clock the
    // last heartbeat brought, at most one heartbeat older than a fresh read, and with no clock or any other capture
    // failure it saves the Final without offline profiles: the partition then only misses compensation. Game thread only.
    internal static Func<(string Epoch, long Frame, long Generation, long SunTicks)> FreshClock = AccountingClock.ReadFresh, LastKnownClock = AccountingClock.Read;
    private static bool s_drainCapture;
    // Once lost, lost for the rest of this node's drains: every later capture would hold the game thread for the full
    // command timeout again. A drained node exits.
    internal static bool DrainClockLost;
    internal static (string Epoch, long Frame, long Generation, long SunTicks) AuthorityClock()
    {
        if (!s_drainCapture) return FreshClock();
        if (!DrainClockLost)
        {
            try { return FreshClock(); }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException)
            {
                DrainClockLost = true;
                MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain offline capture uses the last durable clock, the fresh read failed: " + e.Message);
            }
        }
        return LastKnownClock();
    }
    internal static byte[] DrainCapture(ulong partition, Func<byte[]> capture)
    {
        s_drainCapture = true;
        try { return capture(); }
        catch (Exception e)
        {
            MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] drain offline capture of partition " + partition
                + " failed, its Final is saved without offline profiles: " + SecretRedactor.Redact(e.Message));
            return null;
        }
        finally { s_drainCapture = false; }
    }
    internal static void Apply(IDictionary<long, MyObjectBuilder_EntityBase> entities, IEnumerable<(byte[] Profiles, HashSet<long> Members)> sections)
    {
        if (!ClusterConfig.GetBool("offline.compensate")) return;
        var entries = sections.ToArray();
        Call("Apply", entities, entries.Select(e => e.Profiles).ToArray(), entries.Select(e => e.Members.ToArray()).ToArray());
    }
    internal static object DryRun(PartitionSaveFile.Parsed save, double hours) => Call("DryRun", save.Profiles, hours);
    internal static long? FrozenFrame(byte[] profiles) => profiles == null ? null : (long?)Call("FrozenFrame", profiles);
    internal static object Summary(byte[] profiles) => profiles == null ? null : Call("Summary", profiles);
    internal static object Status() => Call("Status");
    internal static object Profiles(ulong partition, PartitionManager manager) => Call("Profiles", (object)manager.Peek(partition).Members.ToArray());
    internal static object AdminRead(ulong partition, PartitionManager manager, byte[] offline = null) => Call("AdminRead", offline == null ? manager.Peek(partition).Members.ToArray() : Array.Empty<long>(), offline);
    internal static void UpdateObservations() => Call("UpdateObservations");
    internal static void CancelScope(ulong partition) { if (api != null) Call("CancelScope", partition); }
    internal static object BeginMeasurement(MyProductionBlock block, uint frames) => Call("BeginMeasurement", block, frames);
    internal static void FinishMeasurement(object state, bool succeeded) => Call("FinishMeasurement", state, succeeded);
    internal static Action ObserveReactorFuel(MyReactor reactor, float milliseconds) => (Action)Call("ObserveReactorFuel", reactor, milliseconds);
    internal static Vector3D SolarPivot(Vector3 pivot, MatrixD matrix) => (Vector3D)Call("SolarPivot", pivot, matrix);
    internal static bool CanMeasureAssembler(bool repeat, bool disassemble, double progress) => (bool)Call("CanMeasureAssembler", repeat, disassemble, progress);
    internal static long ObservationClockFrames(int seconds) => (long)Call("ObservationClockFrames", seconds);
}
