using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Sandbox.Engine.Voxels;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-1952: builds voxel storages off the game thread, one per name, for a caller that adds the entity in the frame.
/// Loading or generating an asteroid storage takes seconds on a fresh node; the game thread only polls here.
/// Not thread-safe: call it from the game thread only.
/// </summary>
internal sealed class StorageBuilds
{
    private const long RetryMilliseconds = 10_000;
    private readonly string m_what;
    private readonly Dictionary<string, Task<MyStorageBase>> m_builds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> m_retryAt = new(StringComparer.Ordinal);

    public StorageBuilds(string what) => m_what = what;

    public bool IsBuilding(string name) => m_builds.ContainsKey(name);

    /// <summary>
    /// The built storage of <paramref name="name"/>, once, or null while it builds. The first call starts the build.
    /// A build that fails or returns null is tried again after 10 s. The caller owns the storage it gets.
    /// </summary>
    public MyStorageBase Poll(string name, Func<MyStorageBase> build)
    {
        if (!m_builds.TryGetValue(name, out Task<MyStorageBase> task))
        {
            if (m_retryAt.TryGetValue(name, out long retry) && Environment.TickCount64 < retry)
                return null;
            m_retryAt.Remove(name);
            m_builds[name] = Task.Run(() => Timed(name, build));
            return null;
        }
        if (!task.IsCompleted)
            return null;
        m_builds.Remove(name);
        if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
            return task.Result;
        m_retryAt[name] = Environment.TickCount64 + RetryMilliseconds;
        if (task.Exception != null)
            MyLog.Default?.WriteLineAndConsole(
                "[ClusterPlugin] "
                    + m_what
                    + " storage build failed name="
                    + name
                    + ": "
                    + task.Exception.GetBaseException()
            );
        return null;
    }

    private MyStorageBase Timed(string name, Func<MyStorageBase> build)
    {
        long start = Stopwatch.GetTimestamp();
        MyStorageBase storage = build();
        Console.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "[ClusterPlugin] {0} storage built off the game thread name={1} ms={2:F1}",
                m_what,
                name,
                (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency
            )
        );
        return storage;
    }
}
