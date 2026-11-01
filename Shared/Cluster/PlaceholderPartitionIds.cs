#nullable enable
using System;

namespace ClusterProtocol;

/// <summary>
/// T-0374: the id range of the local placeholder layouts a fenced or Closed node makes around strays without asking the
/// registry. The registry never issues an id in it, and its id counter never moves into it. A Closed node may claim a
/// row-less id from it so that the drain catch-up can hand the partition (and the client in it) away instead of
/// leaving it for the force drain.
/// </summary>
public static class PlaceholderPartitionIds
{
    /// <summary>The lowest placeholder id (2^62): far above any id the registry's counter reaches.</summary>
    public const ulong Floor = 1UL << 62;

    /// <summary>How many ids from <see cref="Floor"/> up a node process may start its placeholders at (2^61).</summary>
    public const ulong StartSpan = 1UL << 61;

    public static bool Contains(ulong partition) => partition >= Floor;

    /// <summary>
    /// The id before a process's first placeholder, at random inside the range: every node process used to start at the
    /// same id, so two Closed nodes minted the same partition id. Leaves room for 2^61 placeholders above it.
    /// </summary>
    public static long RandomStart() => (long)(Floor + (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) & (StartSpan - 1)));
}
