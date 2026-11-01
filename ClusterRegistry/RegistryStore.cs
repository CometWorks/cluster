using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClusterRegistry;

// T-0227: the Registry's durable store. The in-memory RegistryState stays what it was - plain
// dictionaries and lists, optimised for lookup and change - and the disk side becomes a SQLite
// database in WAL mode, optimised for sequential appends: a flush is ONE transaction carrying only
// the rows that changed since the previous flush (typically a few hundred, ~10 ms with fsync), where
// the old store re-serialised and re-compressed the whole 374 MB state under the global lock (3 s).
//
// Change tracking: the big collections are Tracked* types that report every key they add, replace
// or remove; rows mutated IN PLACE (PartitionOwnership.Node = ..., save.Locations.Add(...)) are
// reported by the registry calling Touch. Everything else in RegistryState is small and lives in
// the `state` table, one row per property, written only when its serialised bytes change.

internal interface IRegistryChangeSink
{
    void Changed(string table, object key);
    void Reset(string table);
}

/// <summary>A RegistryState collection persisted row by row (see <see cref="RegistryStore"/>).</summary>
internal interface ITrackedCollection
{
    void Attach(IRegistryChangeSink? sink, string table);
    Type RowType { get; }
    /// <summary>Whether the collection holds <paramref name="key"/>, and its row (null for a set).</summary>
    bool TryGetRow(object key, out object? row);
    IEnumerable<KeyValuePair<object, object?>> Rows();
    void LoadRow(object key, object? row);
    object ParseKey(string key);
}

internal sealed class TrackedDictionary<TKey, TValue> : Dictionary<TKey, TValue>, ITrackedCollection where TKey : notnull
{
    private IRegistryChangeSink? _sink;
    private string _table = "";

    public TrackedDictionary() { }
    public TrackedDictionary(IEqualityComparer<TKey>? comparer) : base(comparer) { }

    public new TValue this[TKey key]
    {
        get => base[key];
        set { base[key] = value; _sink?.Changed(_table, key); }
    }

    public new void Add(TKey key, TValue value) { base.Add(key, value); _sink?.Changed(_table, key); }

    public new bool TryAdd(TKey key, TValue value)
    {
        if (!base.TryAdd(key, value)) return false;
        _sink?.Changed(_table, key);
        return true;
    }

    public new bool Remove(TKey key)
    {
        if (!base.Remove(key)) return false;
        _sink?.Changed(_table, key);
        return true;
    }

    public new bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (!base.Remove(key, out value)) return false;
        _sink?.Changed(_table, key);
        return true;
    }

    public new void Clear()
    {
        if (Count == 0) return;
        base.Clear();
        _sink?.Reset(_table);
    }

    /// <summary>The row under <paramref name="key"/> was changed in place.</summary>
    public void Touch(TKey key) => _sink?.Changed(_table, key);

    void ITrackedCollection.Attach(IRegistryChangeSink? sink, string table) { _sink = sink; _table = table; }
    Type ITrackedCollection.RowType => typeof(TValue);
    bool ITrackedCollection.TryGetRow(object key, out object? row)
    {
        bool found = base.TryGetValue((TKey)key, out TValue? value);
        row = value;
        return found;
    }
    IEnumerable<KeyValuePair<object, object?>> ITrackedCollection.Rows() => this.Select(x => new KeyValuePair<object, object?>(x.Key, x.Value));
    void ITrackedCollection.LoadRow(object key, object? row) => base[(TKey)key] = (TValue)row!;
    object ITrackedCollection.ParseKey(string key) => RegistryStore.ParseKey<TKey>(key);
}

internal sealed class TrackedSet<T> : HashSet<T>, ITrackedCollection where T : notnull
{
    private IRegistryChangeSink? _sink;
    private string _table = "";

    public new bool Add(T item)
    {
        if (!base.Add(item)) return false;
        _sink?.Changed(_table, item);
        return true;
    }

    public new bool Remove(T item)
    {
        if (!base.Remove(item)) return false;
        _sink?.Changed(_table, item);
        return true;
    }

    public new void Clear()
    {
        if (Count == 0) return;
        base.Clear();
        _sink?.Reset(_table);
    }

    void ITrackedCollection.Attach(IRegistryChangeSink? sink, string table) { _sink = sink; _table = table; }
    Type ITrackedCollection.RowType => typeof(bool);
    bool ITrackedCollection.TryGetRow(object key, out object? row) { row = null; return base.Contains((T)key); }
    IEnumerable<KeyValuePair<object, object?>> ITrackedCollection.Rows() => this.Select(x => new KeyValuePair<object, object?>(x, null));
    void ITrackedCollection.LoadRow(object key, object? row) => base.Add((T)key);
    object ITrackedCollection.ParseKey(string key) => RegistryStore.ParseKey<T>(key);
}

/// <summary>
/// An append-mostly list persisted by index. Appends and index replacements are row writes; anything
/// that shifts indices (insert, remove, sort) rewrites the table, which for the Lineage is expensive
/// and not expected in normal operation.
/// </summary>
internal sealed class TrackedList<T> : List<T>, ITrackedCollection
{
    private IRegistryChangeSink? _sink;
    private string _table = "";

    public new T this[int index]
    {
        get => base[index];
        set { base[index] = value; _sink?.Changed(_table, (long)index); }
    }

    public new void Add(T item) { base.Add(item); _sink?.Changed(_table, (long)(Count - 1)); }

    public new void AddRange(IEnumerable<T> items)
    {
        int from = Count;
        base.AddRange(items);
        for (int index = from; index < Count; index++) _sink?.Changed(_table, (long)index);
    }

    public new void Insert(int index, T item) { base.Insert(index, item); _sink?.Reset(_table); }
    public new bool Remove(T item) { if (!base.Remove(item)) return false; _sink?.Reset(_table); return true; }
    public new void RemoveAt(int index) { base.RemoveAt(index); _sink?.Reset(_table); }
    public new void RemoveRange(int index, int count) { base.RemoveRange(index, count); if (count > 0) _sink?.Reset(_table); }
    public new int RemoveAll(Predicate<T> match) { int removed = base.RemoveAll(match); if (removed > 0) _sink?.Reset(_table); return removed; }
    public new void Clear() { if (Count == 0) return; base.Clear(); _sink?.Reset(_table); }
    public new void Sort() { base.Sort(); _sink?.Reset(_table); }
    public new void Sort(Comparison<T> comparison) { base.Sort(comparison); _sink?.Reset(_table); }
    public new void Reverse() { base.Reverse(); _sink?.Reset(_table); }

    public void Touch(int index) => _sink?.Changed(_table, (long)index);

    void ITrackedCollection.Attach(IRegistryChangeSink? sink, string table) { _sink = sink; _table = table; }
    Type ITrackedCollection.RowType => typeof(T);
    bool ITrackedCollection.TryGetRow(object key, out object? row)
    {
        long index = (long)key;
        if (index < 0 || index >= Count) { row = null; return false; }
        row = base[(int)index];
        return true;
    }
    IEnumerable<KeyValuePair<object, object?>> ITrackedCollection.Rows()
    {
        for (int index = 0; index < Count; index++) yield return new KeyValuePair<object, object?>((long)index, base[index]);
    }
    void ITrackedCollection.LoadRow(object key, object? row)
    {
        if ((long)key != Count) throw new InvalidDataException($"Registry table {_table} is missing row {Count}");
        base.Add((T)row!);
    }
    object ITrackedCollection.ParseKey(string key) => long.Parse(key, CultureInfo.InvariantCulture);
}

/// <summary>
/// SQLite persistence for <see cref="GatewayRegistry"/>'s state. Single writer: every call happens
/// under the registry lock. Statements are prepared once per table at open and only their parameter
/// values change per row; table names come from RegistryState's own property names, never from data.
/// </summary>
internal sealed class RegistryStore : IRegistryChangeSink, IDisposable
{
    public const string FileName = "registry.db";
    private const string StateTable = "state";

    private readonly SqliteConnection _db;
    private readonly Dictionary<string, PropertyInfo> _tracked;
    private readonly PropertyInfo[] _small;
    private readonly Dictionary<string, Statements> _statements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<object>> _dirty = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reset = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _writtenSmall = new(StringComparer.Ordinal);
    private readonly string _path;

    public long Commits { get; private set; }
    public long RowsWritten { get; private set; }

    /// <summary>
    /// Saves moved out of memory (see GatewayRegistry's archive): same row bytes as the Saves table,
    /// plus the partition, the generation and whether the save had a location, so the registry can
    /// answer "does this partition have a durable save" from a small in-memory summary.
    /// </summary>
    public const string ArchiveTable = "ArchivedSaves";
    private SqliteCommand _archiveInsert = null!, _archiveDelete = null!, _archiveSelect = null!;
    /// <summary>Archive changes for the next <see cref="Write"/>, in order: (partition, key, row) inserts one
    /// save, (partition, null, null) removes every archived save of that partition.</summary>
    private readonly List<(ulong Partition, string? Key, ArchivedRow? Row)> _archiveOps = [];
    public long ArchivedRows { get; private set; }

    internal sealed record ArchivedRow(long Generation, bool Located, byte[] Value);

    /// <summary>
    /// T-0400: when each archived partition was archived (unix ms), and whether archive retention has swept its files
    /// (Swept: no store file of it is left to delete; Legacy: how many locations it still names that a stopped node's
    /// dir holds, which retention does not hand out). One row per archived partition, exactly: the row is written with
    /// the archive move (<see cref="Stamp"/>, same transaction), and removed with the partition's rows when it is
    /// rehydrated (<see cref="Unarchive"/>). Its own table, not a column of ArchivedSaves: the stamp is per partition,
    /// and the first-sight stamp of an existing archive is one small row per partition rather than a rewrite of every
    /// archived save row.
    /// </summary>
    public const string StampTable = "ArchiveRetention";
    internal sealed record ArchiveStamp(long ArchivedMs, bool Swept, int Legacy);
    /// <summary>Stamp changes for the next <see cref="Write"/>, in order (null: the stamp is removed).</summary>
    private readonly List<(ulong Partition, ArchiveStamp? Stamp)> _stampOps = [];
    private SqliteCommand _stampUpsert = null!, _stampDelete = null!, _stampDue = null!, _stampRead = null!;

    private sealed class Statements(SqliteCommand upsert, SqliteCommand delete, SqliteCommand clear, SqliteCommand select)
    {
        public readonly SqliteCommand Upsert = upsert;
        public readonly SqliteCommand Delete = delete;
        public readonly SqliteCommand Clear = clear;
        public readonly SqliteCommand Select = select;
    }

    private readonly Dictionary<string, int> _auditCursor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object[]> _auditKeys = new(StringComparer.Ordinal);
    public long AuditedRows { get; private set; }
    public long AuditRepairs { get; private set; }

    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, FileName));

    public RegistryStore(string directory, Type stateType)
    {
        _path = Path.Combine(directory, FileName);
        _tracked = stateType.GetProperties()
            .Where(p => typeof(ITrackedCollection).IsAssignableFrom(p.PropertyType))
            .ToDictionary(p => p.Name, StringComparer.Ordinal);
        _small = stateType.GetProperties().Where(p => !_tracked.ContainsKey(p.Name) && IsPersisted(p)).ToArray();
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        try
        {
            _db.Open();
            Open(stateType);
        }
        catch (SqliteException error)
        {
            _db.Dispose();
            throw new InvalidDataException($"Registry store {_path} is unreadable: {error.Message}", error);
        }
    }

    private void Open(Type stateType)
    {
        Execute("PRAGMA page_size=8192");
        Execute("PRAGMA journal_mode=WAL");
        // FULL: a commit is on disk (the WAL fsynced) before Commit() returns, which is what the
        // registry's durable-sequence contract promises its callers.
        Execute("PRAGMA synchronous=FULL");
        foreach (string table in _tracked.Keys.Append(StateTable))
        {
            Execute($"CREATE TABLE IF NOT EXISTS \"{table}\"(k TEXT NOT NULL PRIMARY KEY, v BLOB)");
            _statements[table] = new Statements(
                Prepare($"INSERT INTO \"{table}\"(k, v) VALUES($k, $v) ON CONFLICT(k) DO UPDATE SET v = excluded.v", "$k", "$v"),
                Prepare($"DELETE FROM \"{table}\" WHERE k = $k", "$k"),
                Prepare($"DELETE FROM \"{table}\""),
                Prepare($"SELECT v FROM \"{table}\" WHERE k = $k", "$k"));
        }
        Execute($"CREATE TABLE IF NOT EXISTS \"{ArchiveTable}\"(k TEXT NOT NULL PRIMARY KEY, p INTEGER NOT NULL, g INTEGER NOT NULL, l INTEGER NOT NULL, v BLOB)");
        Execute($"CREATE INDEX IF NOT EXISTS \"{ArchiveTable}_p\" ON \"{ArchiveTable}\"(p)");
        _archiveInsert = PrepareTyped($"INSERT INTO \"{ArchiveTable}\"(k, p, g, l, v) VALUES($k, $p, $g, $l, $v) ON CONFLICT(k) DO UPDATE SET p = excluded.p, g = excluded.g, l = excluded.l, v = excluded.v",
            ("$k", SqliteType.Text), ("$p", SqliteType.Integer), ("$g", SqliteType.Integer), ("$l", SqliteType.Integer), ("$v", SqliteType.Blob));
        _archiveDelete = PrepareTyped($"DELETE FROM \"{ArchiveTable}\" WHERE p = $p", ("$p", SqliteType.Integer));
        _archiveSelect = PrepareTyped($"SELECT k, g, l, v FROM \"{ArchiveTable}\" WHERE p = $p ORDER BY rowid", ("$p", SqliteType.Integer));
        using SqliteCommand count = _db.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM \"{ArchiveTable}\"";
        ArchivedRows = (long)count.ExecuteScalar()!;
        // T-0400. The partial index holds only partitions still to sweep, in archive order: the pass reads the due
        // ones from it in bounded slices, and the swept ones (most of the table, eventually) cost it nothing.
        Execute($"CREATE TABLE IF NOT EXISTS \"{StampTable}\"(p INTEGER NOT NULL PRIMARY KEY, a INTEGER NOT NULL, s INTEGER NOT NULL, n INTEGER NOT NULL)");
        Execute($"CREATE INDEX IF NOT EXISTS \"{StampTable}_due\" ON \"{StampTable}\"(a, p) WHERE s = 0");
        _stampUpsert = PrepareTyped($"INSERT INTO \"{StampTable}\"(p, a, s, n) VALUES($p, $a, $s, $n) ON CONFLICT(p) DO UPDATE SET a = excluded.a, s = excluded.s, n = excluded.n",
            ("$p", SqliteType.Integer), ("$a", SqliteType.Integer), ("$s", SqliteType.Integer), ("$n", SqliteType.Integer));
        _stampDelete = PrepareTyped($"DELETE FROM \"{StampTable}\" WHERE p = $p", ("$p", SqliteType.Integer));
        _stampDue = PrepareTyped($"SELECT p, a FROM \"{StampTable}\" WHERE s = 0 AND a <= $cutoff AND (a, p) > ($a, $p) ORDER BY a, p LIMIT $limit",
            ("$cutoff", SqliteType.Integer), ("$a", SqliteType.Integer), ("$p", SqliteType.Integer), ("$limit", SqliteType.Integer));
        _stampRead = PrepareTyped($"SELECT a, s, n FROM \"{StampTable}\" WHERE p = $p", ("$p", SqliteType.Integer));
    }

    /// <summary>T-0400: stamps the partition archived at <paramref name="archivedMs"/> (not swept) with the next write.</summary>
    public void Stamp(ulong partition, long archivedMs) => _stampOps.Add((partition, new ArchiveStamp(archivedMs, false, 0)));

    /// <summary>T-0400: records with the next write that retention swept the partition's files.</summary>
    public void MarkSwept(ulong partition, long archivedMs, int legacy) => _stampOps.Add((partition, new ArchiveStamp(archivedMs, true, legacy)));

    /// <summary>T-0400: the partition's stamp as the next write will leave it (committed, then queued), or null.</summary>
    public ArchiveStamp? ReadStamp(ulong partition)
    {
        ArchiveStamp? stamp = null;
        _stampRead.Transaction = null;
        _stampRead.Parameters[0].Value = (long)partition;
        using (SqliteDataReader reader = _stampRead.ExecuteReader())
            if (reader.Read()) stamp = new ArchiveStamp(reader.GetInt64(0), reader.GetInt64(1) != 0, (int)reader.GetInt64(2));
        foreach ((ulong opPartition, ArchiveStamp? queued) in _stampOps)
            if (opPartition == partition) stamp = queued;
        return stamp;
    }

    /// <summary>T-0400: the stamp changes not yet written, last one per partition (null: removed). The pass overlays these
    /// on what <see cref="DueStamps"/> reads, which sees committed rows only.</summary>
    public Dictionary<ulong, ArchiveStamp?> QueuedStamps()
    {
        var result = new Dictionary<ulong, ArchiveStamp?>();
        foreach ((ulong partition, ArchiveStamp? stamp) in _stampOps) result[partition] = stamp;
        return result;
    }

    /// <summary>T-0400: up to <paramref name="limit"/> committed unswept stamps archived at or before
    /// <paramref name="cutoffMs"/>, in (archived, partition) order after <paramref name="after"/>. An index range read.</summary>
    public List<(ulong Partition, long ArchivedMs)> DueStamps(long cutoffMs, (long ArchivedMs, ulong Partition) after, int limit)
    {
        var result = new List<(ulong, long)>();
        _stampDue.Transaction = null;
        _stampDue.Parameters[0].Value = cutoffMs;
        _stampDue.Parameters[1].Value = after.ArchivedMs;
        _stampDue.Parameters[2].Value = (long)after.Partition;
        _stampDue.Parameters[3].Value = (long)limit;
        using SqliteDataReader reader = _stampDue.ExecuteReader();
        while (reader.Read()) result.Add(((ulong)reader.GetInt64(0), reader.GetInt64(1)));
        return result;
    }

    /// <summary>T-0400: how many committed unswept stamps are due (archived at or before <paramref name="cutoffMs"/>).
    /// Its own read-only connection (WAL: no writer is blocked), so the caller does NOT hold the registry lock.</summary>
    public long CountDueReadOnly(long cutoffMs)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{StampTable}\" WHERE s = 0 AND a <= $cutoff";
        command.Parameters.AddWithValue("$cutoff", cutoffMs);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>T-0400: the swept partitions' legacy locations, summed (the status counter's start value).</summary>
    public long SweptLegacyLocations()
    {
        using SqliteCommand command = _db.CreateCommand();
        command.CommandText = $"SELECT COALESCE(SUM(n), 0) FROM \"{StampTable}\" WHERE s <> 0";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>T-0400: every archived save row of one partition as the next write will leave it, WITHOUT taking the
    /// partition out of the archive (retention reads; <see cref="Unarchive"/> brings back). Caller holds the registry lock.</summary>
    public List<(string Key, ArchivedRow Row)> ReadArchivedPartition(ulong partition)
    {
        var rows = new Dictionary<string, ArchivedRow>(StringComparer.Ordinal);
        _archiveSelect.Transaction = null;
        _archiveSelect.Parameters[0].Value = (long)partition;
        using (SqliteDataReader reader = _archiveSelect.ExecuteReader())
            while (reader.Read())
                rows[reader.GetString(0)] = new ArchivedRow(reader.GetInt64(1), reader.GetInt64(2) != 0, (byte[])reader.GetValue(3));
        foreach ((ulong opPartition, string? key, ArchivedRow? row) in _archiveOps)
        {
            if (opPartition != partition) continue;
            if (key == null) rows.Clear();
            else rows[key] = row!;
        }
        return rows.Select(x => (x.Key, x.Value)).ToList();
    }

    /// <summary>
    /// T-0400: the archive retention state at load. Partitions archived before this build have no stamp: each gets one
    /// now, at <paramref name="nowMs"/> (first sight - their files go a full retention period after this build first
    /// loads them, never earlier), and a stamp whose partition is no longer archived (an older build rehydrated it and
    /// knows nothing of this table) is dropped. Written at once, in its own transaction, before the registry serves.
    /// Returns the partitions stamped and, read-only for the log, per newly stamped partition its newest save's
    /// Created and ClusterTime (the age proxy; never used to delete).
    /// </summary>
    public (int Stamped, int Dropped, List<(ulong Partition, DateTimeOffset? Created, long? ClusterTime)> Legacy) StampUnstamped(long nowMs)
    {
        var legacy = new List<(ulong, DateTimeOffset?, long?)>();
        using SqliteTransaction transaction = _db.BeginTransaction();
        using (SqliteCommand command = _db.CreateCommand())
        {
            command.Transaction = transaction;
            // SQLite returns the bare columns of the row holding the MAX (its ClusterTime with its Created). The row
            // JSON is a BLOB: CAST makes json_extract read it as text, not as JSONB.
            command.CommandText = $"SELECT p, MAX(json_extract(CAST(v AS TEXT), '$.Created')), json_extract(CAST(v AS TEXT), '$.ClusterTime') "
                + $"FROM \"{ArchiveTable}\" WHERE p NOT IN (SELECT p FROM \"{StampTable}\") GROUP BY p";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                DateTimeOffset? created = !reader.IsDBNull(1) && DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) ? parsed : null;
                long? clusterTime = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                legacy.Add(((ulong)reader.GetInt64(0), created, clusterTime));
            }
        }
        int dropped;
        using (SqliteCommand command = _db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM \"{StampTable}\" WHERE p NOT IN (SELECT p FROM \"{ArchiveTable}\")";
            dropped = command.ExecuteNonQuery();
        }
        _stampUpsert.Transaction = transaction;
        foreach ((ulong partition, _, _) in legacy)
        {
            _stampUpsert.Parameters[0].Value = (long)partition;
            _stampUpsert.Parameters[1].Value = nowMs;
            _stampUpsert.Parameters[2].Value = 0L;
            _stampUpsert.Parameters[3].Value = 0L;
            _stampUpsert.ExecuteNonQuery();
        }
        transaction.Commit();
        _stampUpsert.Transaction = null;
        return (legacy.Count, dropped, legacy);
    }

    /// <summary>A second read of the stamps (the self-test's check): partition -> stamp.</summary>
    public Dictionary<ulong, ArchiveStamp> StampsFresh()
    {
        var result = new Dictionary<ulong, ArchiveStamp>();
        using SqliteCommand command = _db.CreateCommand();
        command.CommandText = $"SELECT p, a, s, n FROM \"{StampTable}\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            result[(ulong)reader.GetInt64(0)] = new ArchiveStamp(reader.GetInt64(1), reader.GetInt64(2) != 0, (int)reader.GetInt64(3));
        return result;
    }

    private SqliteCommand PrepareTyped(string sql, params (string Name, SqliteType Type)[] parameters)
    {
        SqliteCommand command = _db.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, SqliteType type) in parameters)
            command.Parameters.Add(name, type);
        command.Prepare();
        return command;
    }

    /// <summary>Per archived partition: the newest generation among its saves that had a location (-1: none).</summary>
    public Dictionary<ulong, long> ArchivedPartitions()
    {
        var result = new Dictionary<ulong, long>();
        using SqliteCommand command = _db.CreateCommand();
        command.CommandText = $"SELECT p, MAX(CASE WHEN l <> 0 THEN g ELSE -1 END) FROM \"{ArchiveTable}\" GROUP BY p";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            result[(ulong)reader.GetInt64(0)] = reader.GetInt64(1);
        return result;
    }

    /// <summary>Queues one save row into the archive; it lands in the same transaction as the rest of the next write.</summary>
    public void Archive(ulong partition, string key, long generation, bool located, object row, Type rowType)
    {
        _archiveOps.Add((partition, key, new ArchivedRow(generation, located, JsonSerializer.SerializeToUtf8Bytes(row, rowType))));
        ArchivedRows++;
    }

    /// <summary>
    /// Takes every archived save of <paramref name="partition"/> back out: the rows stored plus any
    /// still queued, and queues their removal from the archive for the next write.
    /// </summary>
    public List<(string Key, byte[] Value)> Unarchive(ulong partition)
    {
        var rows = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        _archiveSelect.Transaction = null;
        _archiveSelect.Parameters[0].Value = (long)partition;
        using (SqliteDataReader reader = _archiveSelect.ExecuteReader())
            while (reader.Read())
                rows[reader.GetString(0)] = (byte[])reader.GetValue(3);
        // Only ops queued after the partition's last queued removal are still in the archive.
        for (int index = 0; index < _archiveOps.Count; index++)
        {
            (ulong opPartition, string? key, ArchivedRow? row) = _archiveOps[index];
            if (opPartition != partition) continue;
            if (key == null) rows.Clear();
            else rows[key] = row!.Value;
        }
        _archiveOps.Add((partition, null, null));
        // T-0400: back in memory, no longer archived: the stamp goes too (a re-archive stamps it afresh).
        _stampOps.Add((partition, null));
        ArchivedRows -= rows.Count;
        return rows.Select(x => (x.Key, x.Value)).ToList();
    }

    /// <summary>
    /// T-0340 step 7: one archived save row as the next write will leave it - the committed row, then the archive
    /// writes still queued for its partition, in order - or null when the archive does not hold it. Caller holds the
    /// registry lock.
    /// </summary>
    public ArchivedRow? ReadArchived(ulong partition, string key)
    {
        ArchivedRow? row = null;
        using (SqliteCommand command = _db.CreateCommand())
        {
            command.CommandText = $"SELECT g, l, v FROM \"{ArchiveTable}\" WHERE k = $k AND p = $p";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$p", (long)partition);
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read()) row = new ArchivedRow(reader.GetInt64(0), reader.GetInt64(1) != 0, (byte[])reader.GetValue(2));
        }
        foreach ((ulong opPartition, string? opKey, ArchivedRow? queued) in _archiveOps)
        {
            if (opPartition != partition) continue;
            if (opKey == null) row = null;
            else if (opKey == key) row = queued;
        }
        return row;
    }

    /// <summary>T-0340 step 7: rewrites one archived save row in place with the next write (the upsert of
    /// <see cref="Archive"/>, without counting a new row). Caller holds the registry lock.</summary>
    public void Rearchive(ulong partition, string key, long generation, bool located, object row, Type rowType) =>
        _archiveOps.Add((partition, key, new ArchivedRow(generation, located, JsonSerializer.SerializeToUtf8Bytes(row, rowType))));

    /// <summary>
    /// T-0323: which of <paramref name="ids"/> any COMMITTED archived save row names, as a quoted JSON string anywhere in
    /// the row (a holder, the writer - which a rehydrate turns into the holder of a location without one - or anything
    /// else: a false positive only keeps a row). Its own read-only connection (WAL: no writer is blocked), so the caller
    /// does NOT hold the registry lock. One scan per id, stopping at the first match: about 0.3 s for an id no row names
    /// on w33's 260k archived saves. Archive writes still queued are <see cref="QueuedArchiveMentions"/>.
    /// </summary>
    public HashSet<string> ArchiveMentionsReadOnly(IReadOnlyCollection<string> ids)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM \"{ArchiveTable}\" WHERE instr(v, $n) > 0 LIMIT 1";
        SqliteParameter needle = command.Parameters.Add("$n", SqliteType.Blob);
        foreach (string id in ids)
        {
            needle.Value = Quoted(id);
            if (command.ExecuteScalar() != null) found.Add(id);
        }
        return found;
    }

    /// <summary>T-0323: which of <paramref name="ids"/> an archive insert still queued for the next write names. Caller holds the registry lock.</summary>
    public HashSet<string> QueuedArchiveMentions(IReadOnlyCollection<string> ids)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            byte[] quoted = Quoted(id);
            if (_archiveOps.Any(op => op.Row != null && op.Row.Value.AsSpan().IndexOf(quoted) >= 0)) found.Add(id);
        }
        return found;
    }

    /// <summary>T-0296: a partition's archived rows are queued for removal (rehydrated) but still committed, so a read-only scan
    /// can still see them. Caller holds the registry lock.</summary>
    public bool ArchiveRemovalsQueued => _archiveOps.Any(op => op.Key == null);

    private static byte[] Quoted(string id) => System.Text.Encoding.UTF8.GetBytes("\"" + id + "\"");

    /// <summary>A second read of the archive (the self-test's check): key -> row bytes.</summary>
    public Dictionary<string, byte[]> ArchivedRowsFresh()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using SqliteCommand command = _db.CreateCommand();
        command.CommandText = $"SELECT k, v FROM \"{ArchiveTable}\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = (byte[])reader.GetValue(1);
        return result;
    }

    /// <summary>What a whole-state JSON document carries: writable, not indexed, not [JsonIgnore].</summary>
    internal static bool IsPersisted(PropertyInfo property) => property.CanWrite && property.GetIndexParameters().Length == 0
        && property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() == null;

    private void Execute(string sql)
    {
        using SqliteCommand command = _db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        SqliteCommand command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (string name in parameters)
            command.Parameters.Add(name, name == "$v" ? SqliteType.Blob : SqliteType.Text);
        command.Prepare();
        return command;
    }

    /// <summary>True when the database holds a registry (its state row for StateSequence exists).</summary>
    public bool HasState()
    {
        using SqliteCommand command = _db.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{StateTable}\"";
        return (long)command.ExecuteScalar()! > 0;
    }

    /// <summary>Reads the whole state. Tracking is NOT attached; the caller does that once normalised.</summary>
    public T Load<T>() where T : class, new() => Load<T>(remember: true);

    /// <summary>A second, independent read of the stored state (the self-test's completeness check).</summary>
    public T LoadFresh<T>() where T : class, new() => Load<T>(remember: false);

    /// <summary>One small-state property as stored, without opening the registry (boot lineage peeks).</summary>
    public static byte[]? PeekState(string directory, string property)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = $"SELECT v FROM \"{StateTable}\" WHERE k = $k";
        command.Parameters.AddWithValue("$k", property);
        return command.ExecuteScalar() as byte[];
    }

    private T Load<T>(bool remember) where T : class, new()
    {
        try { return LoadRows<T>(remember); }
        catch (SqliteException error) { throw new InvalidDataException($"Registry store {_path} is unreadable: {error.Message}", error); }
    }

    private T LoadRows<T>(bool remember) where T : class, new()
    {
        // The small properties are rebuilt as one JSON object so the serializer applies exactly the
        // types, converters and defaults it applies to a whole-state document.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            using SqliteCommand command = _db.CreateCommand();
            command.CommandText = $"SELECT k, v FROM \"{StateTable}\"";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                byte[] value = (byte[])reader.GetValue(1);
                if (_small.All(p => p.Name != name)) continue;   // a property this build no longer has
                writer.WritePropertyName(name);
                writer.WriteRawValue(value, skipInputValidation: true);
                if (remember) _writtenSmall[name] = value;
            }
            writer.WriteEndObject();
        }
        T state = JsonSerializer.Deserialize<T>(buffer.ToArray()) ?? throw new InvalidDataException("Empty registry state");
        foreach ((string table, PropertyInfo property) in _tracked)
        {
            var collection = (ITrackedCollection)Activator.CreateInstance(property.PropertyType)!;
            using SqliteCommand command = _db.CreateCommand();
            // rowid order is insertion order: dictionaries come back in the order they were built,
            // the Lineage list is keyed by index and checked contiguous by LoadRow.
            bool list = property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(TrackedList<>);
            command.CommandText = list
                ? $"SELECT k, v FROM \"{table}\" ORDER BY CAST(k AS INTEGER)"
                : $"SELECT k, v FROM \"{table}\" ORDER BY rowid";
            using SqliteDataReader reader = command.ExecuteReader();
            Type rowType = collection.RowType;
            bool set = property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(TrackedSet<>);
            while (reader.Read())
            {
                object key = collection.ParseKey(reader.GetString(0));
                object? row = set ? null : JsonSerializer.Deserialize((byte[])reader.GetValue(1), rowType);
                collection.LoadRow(key, row);
            }
            property.SetValue(state, collection);
        }
        return state;
    }

    /// <summary>Starts recording changes of <paramref name="state"/>'s tracked collections.</summary>
    public void Attach(object state)
    {
        foreach ((string table, PropertyInfo property) in _tracked)
            ((ITrackedCollection?)property.GetValue(state))?.Attach(this, table);
    }

    public void Changed(string table, object key)
    {
        if (_reset.Contains(table)) return;
        if (!_dirty.TryGetValue(table, out HashSet<object>? keys)) _dirty[table] = keys = [];
        keys.Add(key);
    }

    public void Reset(string table)
    {
        _reset.Add(table);
        _dirty.Remove(table);
    }

    /// <summary>Marks everything changed: the next <see cref="Write"/> rewrites the whole state.</summary>
    public void ResetAll()
    {
        foreach (string table in _tracked.Keys) Reset(table);
        _writtenSmall.Clear();
    }

    public int PendingRows => _dirty.Values.Sum(x => x.Count) + _archiveOps.Count + _stampOps.Count;

    /// <summary>
    /// One transaction: the tracked rows that changed (upserted if present, deleted if not), every
    /// reset table rewritten, and each small property whose serialised bytes differ from the last
    /// ones written. Durable when it returns (WAL + synchronous=FULL).
    /// </summary>
    public void Write(object state)
    {
        using SqliteTransaction transaction = _db.BeginTransaction();
        long rows = 0;
        foreach ((ulong partition, string? key, ArchivedRow? archived) in _archiveOps)
        {
            SqliteCommand command = archived == null ? _archiveDelete : _archiveInsert;
            command.Transaction = transaction;
            if (archived == null)
                command.Parameters[0].Value = (long)partition;
            else
            {
                command.Parameters[0].Value = key;
                command.Parameters[1].Value = (long)partition;
                command.Parameters[2].Value = archived.Generation;
                command.Parameters[3].Value = archived.Located ? 1L : 0L;
                command.Parameters[4].Value = archived.Value;
            }
            command.ExecuteNonQuery();
            rows++;
        }
        foreach ((ulong partition, ArchiveStamp? stamp) in _stampOps)
        {
            SqliteCommand command = stamp == null ? _stampDelete : _stampUpsert;
            command.Transaction = transaction;
            command.Parameters[0].Value = (long)partition;
            if (stamp != null)
            {
                command.Parameters[1].Value = stamp.ArchivedMs;
                command.Parameters[2].Value = stamp.Swept ? 1L : 0L;
                command.Parameters[3].Value = (long)stamp.Legacy;
            }
            command.ExecuteNonQuery();
            rows++;
        }
        foreach (string table in _reset)
        {
            var collection = (ITrackedCollection)_tracked[table].GetValue(state)!;
            Statements statements = _statements[table];
            statements.Clear.Transaction = transaction;
            statements.Clear.ExecuteNonQuery();
            foreach (KeyValuePair<object, object?> row in collection.Rows())
            {
                Upsert(statements, transaction, row.Key, row.Value, collection.RowType);
                rows++;
            }
        }
        foreach ((string table, HashSet<object> keys) in _dirty)
        {
            var collection = (ITrackedCollection)_tracked[table].GetValue(state)!;
            Statements statements = _statements[table];
            foreach (object key in keys)
            {
                if (collection.TryGetRow(key, out object? row))
                    Upsert(statements, transaction, key, row, collection.RowType);
                else
                {
                    statements.Delete.Transaction = transaction;
                    statements.Delete.Parameters[0].Value = FormatKey(key);
                    statements.Delete.ExecuteNonQuery();
                }
                rows++;
            }
        }
        Statements small = _statements[StateTable];
        var written = new List<(string Name, byte[] Bytes)>();
        foreach (PropertyInfo property in _small)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(property.GetValue(state), property.PropertyType);
            if (_writtenSmall.TryGetValue(property.Name, out byte[]? previous) && previous.AsSpan().SequenceEqual(bytes)) continue;
            small.Upsert.Transaction = transaction;
            small.Upsert.Parameters[0].Value = property.Name;
            small.Upsert.Parameters[1].Value = bytes;
            small.Upsert.ExecuteNonQuery();
            written.Add((property.Name, bytes));
            rows++;
        }
        transaction.Commit();
        foreach ((string name, byte[] bytes) in written) _writtenSmall[name] = bytes;
        _dirty.Clear();
        _reset.Clear();
        _archiveOps.Clear();
        _stampOps.Clear();
        _stampUpsert.Transaction = _stampDelete.Transaction = null;
        Commits++;
        RowsWritten += rows;
    }

    private static void Upsert(Statements statements, SqliteTransaction transaction, object key, object? row, Type rowType)
    {
        statements.Upsert.Transaction = transaction;
        statements.Upsert.Parameters[0].Value = FormatKey(key);
        statements.Upsert.Parameters[1].Value = rowType == typeof(bool) ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(row, rowType);
        statements.Upsert.ExecuteNonQuery();
    }

    /// <summary>
    /// The live safety net for change tracking (the self-test's full verifier is too expensive to run
    /// in production): compares the stored bytes of <paramref name="hot"/> rows and of the next
    /// <paramref name="budget"/> rows of every tracked table (a rotating cursor, so every row is
    /// re-checked over time) with their live serialisation. A row that differs was changed without
    /// being reported: it is logged and marked, and the next <see cref="Write"/> repairs it.
    /// Returns the rows repaired.
    /// </summary>
    public int Audit(object state, int budget, IEnumerable<(string Table, object Key)> hot)
    {
        int repaired = 0;
        void Check(string table, ITrackedCollection collection, object key, object? row)
        {
            if (collection.RowType == typeof(bool)) return;   // a set's rows carry no value
            if (_reset.Contains(table) || _dirty.TryGetValue(table, out HashSet<object>? pending) && pending.Contains(key)) return;
            Statements statements = _statements[table];
            statements.Select.Transaction = null;
            statements.Select.Parameters[0].Value = FormatKey(key);
            byte[]? stored = statements.Select.ExecuteScalar() as byte[];
            byte[] live = JsonSerializer.SerializeToUtf8Bytes(row, collection.RowType);
            AuditedRows++;
            if (stored != null && stored.AsSpan().SequenceEqual(live)) return;
            Console.WriteLine($"WARNING: registry store audit: {table}[{FormatKey(key)}] changed without being recorded"
                + (stored == null ? " (row missing from the store)" : "") + "; repairing on the next write");
            Changed(table, key);
            repaired++;
        }
        foreach ((string table, object key) in hot)
        {
            var collection = (ITrackedCollection)_tracked[table].GetValue(state)!;
            if (collection.TryGetRow(key, out object? row)) Check(table, collection, key, row);
        }
        foreach ((string table, PropertyInfo property) in _tracked)
        {
            var collection = (ITrackedCollection)property.GetValue(state)!;
            if (collection.RowType == typeof(bool)) continue;
            // The keys are snapshotted once per pass over the table, so each audit costs its budget,
            // not a walk from the start of a 200k-row dictionary to the cursor.
            int cursor = _auditCursor.GetValueOrDefault(table);
            if (cursor == 0 || !_auditKeys.TryGetValue(table, out object[]? keys))
                _auditKeys[table] = keys = collection.Rows().Select(row => row.Key).ToArray();
            int end = Math.Min(keys.Length, cursor + budget);
            for (int index = cursor; index < end; index++)
                if (collection.TryGetRow(keys[index], out object? row)) Check(table, collection, keys[index], row);
            _auditCursor[table] = end >= keys.Length ? 0 : end;
        }
        AuditRepairs += repaired;
        return repaired;
    }

    /// <summary>Folds the WAL back into the database file and truncates it (an operator checkpoint).</summary>
    public void Checkpoint() => Execute("PRAGMA wal_checkpoint(TRUNCATE)");

    /// <summary>Rewrites the database compactly (after a migration's bulk load).</summary>
    public void Compact() => Execute("VACUUM");

    /// <summary>
    /// Writes a consistent copy of the committed database to <paramref name="destination"/> (an online backup).
    /// Its own read-only connection, so it holds one WAL read transaction and never blocks the writer.
    /// </summary>
    public void CopyTo(string destination)
    {
        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        copy.Open();
        using SqliteCommand command = copy.CreateCommand();
        command.CommandText = "VACUUM INTO $destination";
        command.Parameters.AddWithValue("$destination", destination);
        command.ExecuteNonQuery();
    }

    public long DatabaseBytes => File.Exists(_path) ? new FileInfo(_path).Length : 0;
    public long WalBytes => File.Exists(_path + "-wal") ? new FileInfo(_path + "-wal").Length : 0;

    internal static string FormatKey(object key) => key switch
    {
        string text => text,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => key.ToString()!,
    };

    internal static object ParseKey<TKey>(string key)
    {
        if (typeof(TKey) == typeof(string)) return key;
        if (typeof(TKey) == typeof(ulong)) return ulong.Parse(key, CultureInfo.InvariantCulture);
        if (typeof(TKey) == typeof(long)) return long.Parse(key, CultureInfo.InvariantCulture);
        if (typeof(TKey) == typeof(int)) return int.Parse(key, CultureInfo.InvariantCulture);
        if (typeof(TKey) == typeof(Guid)) return Guid.Parse(key);
        throw new NotSupportedException($"Registry table key type {typeof(TKey)}");
    }

    public void Dispose()
    {
        foreach (Statements statements in _statements.Values)
        {
            statements.Upsert.Dispose();
            statements.Delete.Dispose();
            statements.Clear.Dispose();
            statements.Select.Dispose();
        }
        _archiveInsert?.Dispose();
        _archiveDelete?.Dispose();
        _archiveSelect?.Dispose();
        _stampUpsert?.Dispose();
        _stampDelete?.Dispose();
        _stampDue?.Dispose();
        _stampRead?.Dispose();
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
