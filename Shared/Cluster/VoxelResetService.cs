using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Voxels;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// SE-1595, World Authority: voxel reset of mined voxels. A planet's 64-voxel cube, or a whole asteroid, that nobody
/// has edited for <c>voxelReset.ageSeconds</c> goes back to its generated shape and ore, unless a partition member
/// (grid or character, on any node or in an offline partition) is within <c>voxelReset.gridMeters</c> or an online
/// player within <c>voxelReset.playerMeters</c>. The World Authority writes the pristine content into its own
/// storage and journals it, so every node converges on the same voxels.
/// <para>
/// Vanilla's voxel revert stays off in a cluster (MySessionComponentTrashPatch): it runs per node on that node's
/// own entities and clock, so it can't see grids on other nodes or in offline partitions.
/// </para>
/// </summary>
internal static class VoxelResetService
{
    private const string LedgerFile = "ledger.json";
    private static readonly HttpClient Http = ClusterHttp.Client(TimeSpan.FromSeconds(10));
    private static readonly object Sync = new();

    private static Occupancy m_occupancy;
    private static double m_nextScan;
    private static double m_nextSave;
    private static bool m_fetching;
    private static bool m_authorized;
    private static string m_lastSkip;
    private static string m_skipNow;
    private static double m_nextStatus;
    private static int m_due;
    private static int m_blocked;
    private static string m_nearest;
    private static int m_resets;
    private static bool m_publishDue = true;
    private static readonly Queue<(string Storage, long Cube)> m_queue = new();
    private static readonly HashSet<(string Storage, long Cube)> m_queued = new();

    private sealed record Occupancy(
        double ReceivedMs,
        bool Complete,
        string Reason,
        double AgeSeconds,
        double[][] Spheres,
        int Remembered,
        int Memberless
    );

    private static string ResetRoot(string journalRoot) => Path.Combine(journalRoot, "reset");

    internal static VoxelEditLedger LoadLedger(string journalRoot)
    {
        string path = Path.Combine(ResetRoot(journalRoot), LedgerFile);
        try
        {
            return File.Exists(path)
                ? VoxelEditLedger.Deserialize(File.ReadAllText(path))
                : new VoxelEditLedger();
        }
        catch (Exception exception)
        {
            // The retained journal files seed it again; a reset can then only come later than it would have.
            Console.WriteLine(
                "[ClusterPlugin] voxel_reset ledger_unreadable=true error=" + exception.Message
            );
            return new VoxelEditLedger();
        }
    }

    private static bool Enabled =>
        ClusterRuntime.IsWorldAuthority && ClusterConfig.GetBool("voxelReset.enabled");

    /// <summary>Game thread, from <see cref="VoxelJournalService.Update"/> on the World Authority.</summary>
    public static void Update(VoxelJournalService journal, string journalRoot)
    {
        if (journal?.Ledger == null || MySession.Static?.Ready != true)
            return;
        double now = ClusterRuntime.LocalMilliseconds;
        if (now >= m_nextSave)
        {
            m_nextSave = now + 30_000;
            SaveLedger(journal.Ledger, journalRoot);
        }
        if (!Enabled)
        {
            m_queue.Clear();
            m_queued.Clear();
            return;
        }
        long scanMs = 1000L * Math.Max(1, ClusterConfig.GetInt("voxelReset.scanSeconds"));
        if (now >= m_nextScan)
        {
            m_nextScan = now + scanMs;
            FetchOccupancy();
            Plan(journal.Ledger, scanMs);
            if (m_publishDue)
            {
                m_publishDue = false;
                PublishResets(journal.Ledger.LastResets());
            }
        }
        Execute(journal, ClusterConfig.GetInt("voxelReset.cubesPerUpdate"), scanMs);
        if (now >= m_nextStatus)
        {
            m_nextStatus =
                now + 1000L * Math.Max(1, ClusterConfig.GetInt("voxelReset.statusSeconds"));
            Status(journal.Ledger);
            // Also refills a Gateway that restarted since.
            m_publishDue = true;
        }
    }

    /// <summary>SE-1706: the newest reset per storage to the Gateway's GET /registry/voxel-resets, for whoever needs to
    /// know that an asteroid grew back (the NPC miners).</summary>
    private static void PublishResets(Dictionary<string, long> resets)
    {
        string gateway = Environment
            .GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")
            ?.TrimEnd('/');
        if (resets.Count == 0 || string.IsNullOrWhiteSpace(gateway))
            return;
        string body = JsonSerializer.Serialize(resets);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (!m_authorized)
                {
                    ClusterRuntime.AuthorizeRegistryClient(Http);
                    m_authorized = true;
                }
                using var content = new StringContent(
                    body,
                    System.Text.Encoding.UTF8,
                    "application/json"
                );
                using HttpResponseMessage response = Http.PostAsync(
                        gateway + "/registry/voxel-resets",
                        content
                    )
                    .GetAwaiter()
                    .GetResult();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("HTTP " + (int)response.StatusCode);
            }
            catch (Exception exception)
            {
                m_publishDue = true;
                Console.WriteLine(
                    "[ClusterPlugin] voxel_reset publish_failed=true error=" + exception.Message
                );
            }
        });
    }

    /// <summary>SE-1706: one line every voxelReset.statusSeconds, because the skip line is logged only when its reason
    /// changes and host.log rotates every ~30 min. Counts are from the last scan; resets since the last status line.</summary>
    private static void Status(VoxelEditLedger ledger)
    {
        Occupancy occupancy;
        string skip;
        lock (Sync)
        {
            occupancy = m_occupancy;
            skip = m_skipNow;
        }
        Console.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "[ClusterPlugin] voxel_reset status edited_cubes={0} due={1} blocked={2} nearest_blocker={3} resets={4} queued={5} skip={6} occupancy_age_s={7:0} remembered={8} memberless={9}",
                ledger.EditedCubes(),
                m_due,
                m_blocked,
                m_nearest == null ? "-" : "\"" + m_nearest + "\"",
                m_resets,
                m_queue.Count,
                skip == null ? "-" : "\"" + skip + "\"",
                occupancy?.AgeSeconds ?? -1,
                occupancy?.Remembered ?? 0,
                occupancy?.Memberless ?? 0
            )
        );
        m_resets = 0;
    }

    private static void SaveLedger(VoxelEditLedger ledger, string journalRoot)
    {
        if (!ledger.Dirty)
            return;
        string json = ledger.Serialize();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                string directory = ResetRoot(journalRoot);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, LedgerFile);
                lock (Sync)
                {
                    File.WriteAllText(path + ".tmp", json);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    "[ClusterPlugin] voxel_reset ledger_save_failed=true error=" + exception.Message
                );
            }
        });
    }

    /// <summary>Background: every member sphere in the cluster, from the Gateway's last complete partition report pass and its offline table.</summary>
    private static void FetchOccupancy()
    {
        lock (Sync)
        {
            if (m_fetching)
                return;
            m_fetching = true;
        }
        string gateway = Environment
            .GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")
            ?.TrimEnd('/');
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gateway))
                    return;
                if (!m_authorized)
                {
                    ClusterRuntime.AuthorizeRegistryClient(Http);
                    m_authorized = true;
                }
                using JsonDocument document = ClusterRuntime.Get(
                    Http,
                    gateway + "/registry/voxel-reset-occupancy"
                );
                JsonElement root = document.RootElement;
                double[][] spheres = root.GetProperty("spheres")
                    .EnumerateArray()
                    .Select(x => x.EnumerateArray().Select(v => v.GetDouble()).ToArray())
                    .ToArray();
                var occupancy = new Occupancy(
                    ClusterRuntime.LocalMilliseconds,
                    root.GetProperty("complete").GetBoolean(),
                    root.TryGetProperty("reason", out JsonElement reason)
                    && reason.ValueKind == JsonValueKind.String
                        ? reason.GetString()
                        : null,
                    root.GetProperty("ageSeconds").GetDouble(),
                    spheres,
                    root.TryGetProperty("remembered", out JsonElement remembered)
                        ? remembered.GetInt32()
                        : 0,
                    root.TryGetProperty("memberless", out JsonElement memberless)
                        ? memberless.GetInt32()
                        : 0
                );
                lock (Sync)
                    m_occupancy = occupancy;
            }
            catch (Exception exception)
            {
                Skip("occupancy fetch failed: " + exception.Message);
            }
            finally
            {
                lock (Sync)
                    m_fetching = false;
            }
        });
    }

    private static void Skip(string why)
    {
        lock (Sync)
        {
            m_skipNow = why;
            if (why == m_lastSkip)
                return;
            m_lastSkip = why;
        }
        Console.WriteLine("[ClusterPlugin] voxel_reset skipped=true reason=" + why);
    }

    /// <summary>The occupancy to judge with now, or null (and why) when there is none fresh and complete.</summary>
    private static Occupancy Usable(long scanMs)
    {
        Occupancy occupancy;
        lock (Sync)
            occupancy = m_occupancy;
        if (occupancy == null)
            Skip("no occupancy yet");
        else if (!occupancy.Complete)
            Skip("occupancy incomplete: " + occupancy.Reason);
        else if (ClusterRuntime.LocalMilliseconds - occupancy.ReceivedMs > 3 * scanMs + 10_000)
            Skip("occupancy stale");
        else if (occupancy.AgeSeconds > 120)
            Skip(
                "partition report pass "
                    + occupancy.AgeSeconds.ToString("0", CultureInfo.InvariantCulture)
                    + " s old"
            );
        else
        {
            lock (Sync)
                m_lastSkip = m_skipNow = null;
            return occupancy;
        }
        return null;
    }

    private static string Blocked(MyVoxelBase voxel, long cube, Occupancy occupancy)
    {
        BoundingBoxD box;
        if (voxel is MyPlanet)
        {
            (int x, int y, int z) = VoxelResetPolicy.CubeOf(cube);
            Vector3D min =
                voxel.PositionLeftBottomCorner + new Vector3D(x, y, z) * VoxelResetPolicy.CubeSize;
            box = new BoundingBoxD(min, min + VoxelResetPolicy.CubeSize);
        }
        else
            box = voxel.PositionComp.WorldAABB;
        return VoxelResetPolicy.Blocked(
            new[] { box.Min.X, box.Min.Y, box.Min.Z },
            new[] { box.Max.X, box.Max.Y, box.Max.Z },
            occupancy.Spheres,
            PositionRegistryService.Players.Select(p => (p.X, p.Y, p.Z)),
            ClusterConfig.GetInt("voxelReset.gridMeters"),
            ClusterConfig.GetInt("voxelReset.playerMeters")
        );
    }

    private static MyVoxelBase Resettable(string storage)
    {
        MyVoxelBase voxel = MySession.Static.VoxelMaps.TryGetVoxelMapByName(storage);
        // Only voxels with a generator have a shape to go back to (a prefab .vx2 has none), and a voxel map a
        // player placed is theirs.
        return
            voxel?.Storage is MyStorageBase { DataProvider: not null }
            && voxel == voxel.RootVoxel
            && !voxel.CreatedByUser
            && !voxel.Closed
            ? voxel
            : null;
    }

    private static void Plan(VoxelEditLedger ledger, long scanMs)
    {
        Occupancy occupancy = Usable(scanMs);
        if (occupancy == null)
            return;
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long age = ClusterConfig.GetInt("voxelReset.ageSeconds");
        int due = 0,
            blocked = 0;
        string nearest = null;
        double nearestMeters = double.MaxValue;
        // Counts a due unit, and keeps the nearest blocker of the blocked ones; true when it may reset.
        bool Judge(string blocker)
        {
            due++;
            if (blocker == null)
                return true;
            blocked++;
            double meters = double.Parse(
                blocker.Split(' ')[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture
            );
            if (meters < nearestMeters)
                (nearest, nearestMeters) = (blocker, meters);
            return false;
        }
        foreach (KeyValuePair<string, KeyValuePair<long, long>[]> storage in ledger.Edits())
        {
            MyVoxelBase voxel = Resettable(storage.Key);
            if (voxel == null)
                continue;
            if (voxel is MyPlanet)
            {
                foreach (KeyValuePair<long, long> cube in storage.Value)
                    if (
                        VoxelResetPolicy.Due(cube.Value, nowUnix, age)
                        && Judge(Blocked(voxel, cube.Key, occupancy))
                    )
                        Enqueue(storage.Key, cube.Key);
                continue;
            }
            // An asteroid goes back whole: once its newest edit is old enough and nothing is near any of it.
            if (!VoxelResetPolicy.Due(storage.Value.Max(x => x.Value), nowUnix, age))
                continue;
            if (!Judge(Blocked(voxel, 0, occupancy)))
                continue;
            foreach (KeyValuePair<long, long> cube in storage.Value)
                Enqueue(storage.Key, cube.Key);
        }
        (m_due, m_blocked, m_nearest) = (due, blocked, nearest);
    }

    private static void Enqueue(string storage, long cube)
    {
        if (m_queued.Add((storage, cube)))
            m_queue.Enqueue((storage, cube));
    }

    private static void Execute(VoxelJournalService journal, int perUpdate, long scanMs)
    {
        if (m_queue.Count == 0)
            return;
        Occupancy occupancy = Usable(scanMs);
        if (occupancy == null)
            return;
        long age = ClusterConfig.GetInt("voxelReset.ageSeconds");
        for (int done = 0; done < Math.Max(1, perUpdate) && m_queue.Count > 0; done++)
        {
            (string storage, long cube) = m_queue.Peek();
            MyVoxelBase voxel = Resettable(storage);
            // Checked again per cube: a player may have come close since the plan.
            if (voxel == null || Blocked(voxel, cube, occupancy) != null)
            {
                Drop();
                continue;
            }
            long lastEdit = 0;
            bool reset = journal.ResetCube(
                voxel,
                cube,
                () =>
                {
                    // An asteroid's newest edit decides for all of its cubes.
                    lastEdit = journal.Ledger.LastEdit(storage, cube);
                    if (lastEdit >= 0 && voxel is not MyPlanet)
                        lastEdit = journal.Ledger.NewestEdit(storage);
                    return lastEdit >= 0
                        && VoxelResetPolicy.Due(
                            lastEdit,
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            age
                        );
                }
            );
            if (!reset)
            {
                // Not applied up to its tail yet: try again next update. Edited since: the next plan decides.
                if (
                    lastEdit >= 0
                    && VoxelResetPolicy.Due(
                        lastEdit,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        age
                    )
                )
                    return;
                Drop();
                continue;
            }
            Drop();
            m_resets++;
            m_publishDue = true;
            (int x, int y, int z) = VoxelResetPolicy.CubeOf(cube);
            Console.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[ClusterPlugin] voxel_reset storage={0} cube={1},{2},{3} edited_hours_ago={4:0.0} remaining_edited_cubes={5}",
                    storage,
                    x,
                    y,
                    z,
                    (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastEdit) / 3600.0,
                    journal.Ledger.EditedCubes()
                )
            );
        }
    }

    private static void Drop() => m_queued.Remove(m_queue.Dequeue());
}
