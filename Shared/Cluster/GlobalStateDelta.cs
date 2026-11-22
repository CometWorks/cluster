using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>
/// One global-state broadcast of a keyed record that carries only what changed (SE-1870): the entries whose
/// JSON differs from what was last broadcast, and the keys that left the record.
/// </summary>
public sealed record GlobalStateDeltaPayload<TKey, TValue>(TValue[] Changed, TKey[] Removed);

/// <summary>
/// World Authority side of delta publishing for a keyed record, for any subsystem. Republishing a whole
/// record on every change sent the 227 player profiles (1.36 MB) about every 6 s for one player's edit.
/// </summary>
/// <remarks>
/// How a subsystem uses it, with no change to <see cref="GlobalStateChannel"/>:
///
///   * WA: after changing the record, <see cref="Take"/> the keys that may have changed and
///     <see cref="GlobalStateChannel.Publish"/> the payload when it is not null.
///   * <see cref="GlobalStateChannel.RegisterSnapshot"/> with a provider that serializes the WHOLE record and a
///     handler that replaces the replica's copy; <see cref="GlobalStateChannel.RegisterReplicaHandler"/> with a
///     handler that <see cref="Read"/>s and <see cref="Apply"/>s a delta.
///
/// The channel already makes this safe: broadcasts carry sequence numbers, a replica that sees a gap (or a new
/// link, or a new WA generation) asks for a resync, and the resync answers the full record. A delta therefore
/// only ever lands on a copy that holds everything before it. Keys must serialize to JSON (records, not tuples:
/// System.Text.Json skips a ValueTuple's fields) and be usable as dictionary keys.
///
/// Compatibility: a payload is <c>{"Changed":[...],"Removed":[...]}</c>. A replica from before the delta format
/// that expects <c>{"Profiles":[...]}</c>-style snapshots should reject it rather than read it as an empty record;
/// check that for the subsystem's old reader.
/// </remarks>
public sealed class GlobalStateDelta<TKey, TValue>
    where TValue : class
{
    private readonly Dictionary<TKey, string> m_published = new();

    /// <summary>Entries broadcast so far (for status).</summary>
    public int Published => m_published.Count;

    /// <summary>
    /// The delta for <paramref name="touched"/>: the entries whose JSON differs from the last broadcast, and the
    /// touched keys <paramref name="current"/> no longer has (null). Null when nothing changed. Marks the
    /// result as broadcast, so the caller must publish what it gets.
    /// </summary>
    public string Take(IEnumerable<TKey> touched, Func<TKey, TValue> current)
    {
        var changed = new List<TValue>();
        var removed = new List<TKey>();
        foreach (TKey key in touched.Distinct())
        {
            TValue value = current(key);
            if (value == null)
            {
                if (m_published.Remove(key))
                    removed.Add(key);
                continue;
            }
            string json = JsonSerializer.Serialize(value);
            if (m_published.TryGetValue(key, out string last) && last == json)
                continue;
            m_published[key] = json;
            changed.Add(value);
        }
        return changed.Count == 0 && removed.Count == 0
            ? null
            : JsonSerializer.Serialize(
                new GlobalStateDeltaPayload<TKey, TValue>(changed.ToArray(), removed.ToArray())
            );
    }

    /// <summary>Forget what was broadcast, e.g. when this node becomes the World Authority again.</summary>
    public void Reset() => m_published.Clear();

    /// <summary>
    /// Parse a delta. Throws on anything that is not one, which the channel answers with a resync.
    /// </summary>
    public static GlobalStateDeltaPayload<TKey, TValue> Read(string payload, int maxEntries)
    {
        GlobalStateDeltaPayload<TKey, TValue> delta =
            JsonSerializer.Deserialize<GlobalStateDeltaPayload<TKey, TValue>>(
                payload ?? throw new InvalidOperationException("Empty global-state delta")
            ) ?? throw new InvalidOperationException("Invalid global-state delta");
        if (
            delta.Changed == null
            || delta.Changed.Length > maxEntries
            || delta.Removed?.Length > maxEntries
        )
            throw new InvalidOperationException("Invalid global-state delta");
        return delta with
        {
            Changed = delta.Changed.Where(value => value != null).ToArray(),
            Removed = delta.Removed?.Where(key => key != null).ToArray() ?? Array.Empty<TKey>(),
        };
    }

    /// <summary>Patch a replica's copy of the record with a delta.</summary>
    public static void Apply(
        IDictionary<TKey, TValue> record,
        GlobalStateDeltaPayload<TKey, TValue> delta,
        Func<TValue, TKey> keyOf
    )
    {
        foreach (TValue value in delta.Changed)
            record[keyOf(value)] = value;
        foreach (TKey key in delta.Removed ?? Array.Empty<TKey>())
            record.Remove(key);
    }
}
