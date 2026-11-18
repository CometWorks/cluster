using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>
/// SE-1595: the rules of the World Authority's voxel reset, kept free of game types so the Gateway self-test
/// runs them without a game. A unit of edit tracking is a 64-voxel cube in storage coordinates: a planet
/// resets cube by cube, an asteroid resets all of its edited cubes at once.
/// </summary>
internal static class VoxelResetPolicy
{
    public const int CubeSize = 64;
    private const int AxisBits = 21;
    private const long AxisMask = (1L << AxisBits) - 1;

    public static long CubeKey(int x, int y, int z) =>
        ((long)x & AxisMask)
        | (((long)y & AxisMask) << AxisBits)
        | (((long)z & AxisMask) << (2 * AxisBits));

    public static (int X, int Y, int Z) CubeOf(long key) =>
        (
            (int)(key & AxisMask),
            (int)((key >> AxisBits) & AxisMask),
            (int)((key >> (2 * AxisBits)) & AxisMask)
        );

    /// <summary>Every cube an inclusive voxel box touches; negative coordinates are clamped to 0.</summary>
    public static IEnumerable<long> CubesInBox(
        int minX,
        int minY,
        int minZ,
        int maxX,
        int maxY,
        int maxZ
    )
    {
        int ax = Math.Max(0, minX) / CubeSize,
            ay = Math.Max(0, minY) / CubeSize,
            az = Math.Max(0, minZ) / CubeSize;
        int bx = Math.Max(0, maxX) / CubeSize,
            by = Math.Max(0, maxY) / CubeSize,
            bz = Math.Max(0, maxZ) / CubeSize;
        for (int x = ax; x <= bx; x++)
        for (int y = ay; y <= by; y++)
        for (int z = az; z <= bz; z++)
            yield return CubeKey(x, y, z);
    }

    public static bool Due(long lastEditUnix, long nowUnix, long ageSeconds) =>
        nowUnix - lastEditUnix >= ageSeconds;

    private static double DistanceToBox(double x, double y, double z, double[] min, double[] max)
    {
        double dx = Math.Max(0, Math.Max(min[0] - x, x - max[0]));
        double dy = Math.Max(0, Math.Max(min[1] - y, y - max[1]));
        double dz = Math.Max(0, Math.Max(min[2] - z, z - max[2]));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Why a unit with this world box must not reset now, or null when it may. <paramref name="spheres"/> are
    /// [x,y,z,r] rows of every partition member (grids and characters, online or offline); a member closer than
    /// <paramref name="gridMeters"/> blocks. An online player closer than <paramref name="playerMeters"/> blocks.
    /// </summary>
    public static string Blocked(
        double[] boxMin,
        double[] boxMax,
        IEnumerable<double[]> spheres,
        IEnumerable<(double X, double Y, double Z)> players,
        double gridMeters,
        double playerMeters
    )
    {
        foreach (double[] s in spheres)
        {
            if (s == null || s.Length < 4)
                continue;
            double gap = DistanceToBox(s[0], s[1], s[2], boxMin, boxMax) - s[3];
            if (gap < gridMeters)
                return "member " + gap.ToString("0", CultureInfo.InvariantCulture) + " m";
        }
        foreach ((double x, double y, double z) in players)
        {
            double gap = DistanceToBox(x, y, z, boxMin, boxMax);
            if (gap < playerMeters)
                return "player " + gap.ToString("0", CultureInfo.InvariantCulture) + " m";
        }
        return null;
    }
}

/// <summary>
/// SE-1595: when each 64-voxel cube of each storage was last edited (unix seconds), as the World Authority saw
/// the journal entries arrive. A reset removes the cube and remembers when, so seeding from the retained
/// journal files after a restart does not count the reset's own entry, or older ones, as a new edit.
/// </summary>
internal sealed class VoxelEditLedger
{
    private readonly object m_lock = new();
    private readonly Dictionary<string, Dictionary<long, long>> m_edits = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, Dictionary<long, long>> m_resets = new(
        StringComparer.Ordinal
    );
    private bool m_dirty;

    public bool Dirty
    {
        get
        {
            lock (m_lock)
                return m_dirty;
        }
    }

    private static Dictionary<long, long> Of(
        Dictionary<string, Dictionary<long, long>> map,
        string storage
    )
    {
        if (!map.TryGetValue(storage, out Dictionary<long, long> cubes))
            map[storage] = cubes = new Dictionary<long, long>();
        return cubes;
    }

    /// <summary>A live edit: every cube of the inclusive box was edited at <paramref name="unix"/>.</summary>
    public void Touch(
        string storage,
        int minX,
        int minY,
        int minZ,
        int maxX,
        int maxY,
        int maxZ,
        long unix
    ) => Record(storage, minX, minY, minZ, maxX, maxY, maxZ, unix, seeding: false);

    /// <summary>A retained journal entry written at <paramref name="unix"/>: counts only where no reset came later.</summary>
    public void Seed(
        string storage,
        int minX,
        int minY,
        int minZ,
        int maxX,
        int maxY,
        int maxZ,
        long unix
    ) => Record(storage, minX, minY, minZ, maxX, maxY, maxZ, unix, seeding: true);

    private void Record(
        string storage,
        int minX,
        int minY,
        int minZ,
        int maxX,
        int maxY,
        int maxZ,
        long unix,
        bool seeding
    )
    {
        lock (m_lock)
        {
            Dictionary<long, long> edits = Of(m_edits, storage);
            m_resets.TryGetValue(storage, out Dictionary<long, long> resets);
            foreach (long cube in VoxelResetPolicy.CubesInBox(minX, minY, minZ, maxX, maxY, maxZ))
            {
                if (
                    seeding
                    && resets != null
                    && resets.TryGetValue(cube, out long resetAt)
                    && resetAt >= unix
                )
                    continue;
                if (!edits.TryGetValue(cube, out long last) || last < unix)
                {
                    edits[cube] = unix;
                    m_dirty = true;
                }
            }
        }
    }

    public void MarkReset(string storage, long cube, long unix)
    {
        lock (m_lock)
        {
            if (m_edits.TryGetValue(storage, out Dictionary<long, long> edits))
                edits.Remove(cube);
            Of(m_resets, storage)[cube] = unix;
            m_dirty = true;
        }
    }

    /// <summary>The edited cubes per storage as (cube, last edit) pairs, a copy.</summary>
    public Dictionary<string, KeyValuePair<long, long>[]> Edits()
    {
        lock (m_lock)
            return m_edits
                .Where(x => x.Value.Count > 0)
                .ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>The cube's last edit, or -1 when it has none since its last reset.</summary>
    public long LastEdit(string storage, long cube)
    {
        lock (m_lock)
            return
                m_edits.TryGetValue(storage, out Dictionary<long, long> edits)
                && edits.TryGetValue(cube, out long last)
                ? last
                : -1;
    }

    /// <summary>The newest edit of any cube of the storage, or -1 when none.</summary>
    public long NewestEdit(string storage)
    {
        lock (m_lock)
            return m_edits.TryGetValue(storage, out Dictionary<long, long> edits) && edits.Count > 0
                ? edits.Values.Max()
                : -1;
    }

    public int EditedCubes()
    {
        lock (m_lock)
            return m_edits.Values.Sum(x => x.Count);
    }

    public string Serialize()
    {
        lock (m_lock)
        {
            static Dictionary<string, Dictionary<string, long>> Plain(
                Dictionary<string, Dictionary<long, long>> map
            ) =>
                map.Where(x => x.Value.Count > 0)
                    .ToDictionary(
                        x => x.Key,
                        x =>
                            x.Value.ToDictionary(
                                c => c.Key.ToString(CultureInfo.InvariantCulture),
                                c => c.Value
                            )
                    );
            m_dirty = false;
            return JsonSerializer.Serialize(
                new
                {
                    v = 1,
                    edits = Plain(m_edits),
                    resets = Plain(m_resets),
                }
            );
        }
    }

    public static VoxelEditLedger Deserialize(string json)
    {
        var ledger = new VoxelEditLedger();
        using JsonDocument document = JsonDocument.Parse(json);
        void Read(string name, Dictionary<string, Dictionary<long, long>> into)
        {
            if (!document.RootElement.TryGetProperty(name, out JsonElement map))
                return;
            foreach (JsonProperty storage in map.EnumerateObject())
            {
                Dictionary<long, long> cubes = Of(into, storage.Name);
                foreach (JsonProperty cube in storage.Value.EnumerateObject())
                    cubes[long.Parse(cube.Name, CultureInfo.InvariantCulture)] =
                        cube.Value.GetInt64();
            }
        }
        Read("edits", ledger.m_edits);
        Read("resets", ledger.m_resets);
        return ledger;
    }
}
