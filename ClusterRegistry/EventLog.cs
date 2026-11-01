using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ClusterRegistry;

/// <summary>
/// Ordered cluster event/audit tail (Orchestration.md §12): every admin mutation, phase
/// change, node lifecycle transition, and alarm is one appended record with a monotonic
/// sequence number. Recent events are served from a ring buffer; when a registry
/// directory is configured they are also appended to <c>events.jsonl</c> so the tail and
/// the sequence survive a Gateway restart. Events are observability, not correctness -
/// appends enter a bounded background queue; persistence batches flush but never fsync.
/// Retention and the pending queue are bounded, so readers must treat cursor gaps as truncation.
/// </summary>
internal sealed class EventLog : IDisposable
{
    internal sealed record Entry(long Seq, DateTimeOffset At, string Type, string? Actor,
        string? RequestId, string? Target, IReadOnlyDictionary<string, string>? Data);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _lock = new();
    private readonly object _diskLock = new();
    private sealed record Pending(Entry? Entry, TaskCompletionSource? Completion = null, long Through = 0);
    private readonly Channel<Pending>? _pending;
    private readonly Task? _persistence;
    private bool _disposed;
    private Exception? _writeFailure;
    private long _writtenSeq, _writtenBytes;
    private readonly Queue<Entry> _ring = new();
    private readonly int _capacity;
    private readonly long _rollBytes;
    private readonly string? _path;
    private readonly Func<DateTimeOffset> _clock;
    private StreamWriter? _writer;
    private long _seq;
    private long _earliestRetained = 1;

    public EventLog(string? directory, Func<DateTimeOffset>? clock = null, int capacity = 4096,
        long rollBytes = 64 << 20)
    {
        _capacity = capacity;
        _rollBytes = rollBytes;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (directory == null)
            return;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "events.jsonl");
        Recover();
        _writer = OpenWriter();
        _writtenSeq = _seq;
        _writtenBytes = _writer.BaseStream.Length;
        _pending = Channel.CreateBounded<Pending>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _persistence = Task.Run(PersistAsync);
    }

    /// <summary>Appends one event and returns its sequence number. Callers may hold their own locks.</summary>
    public long Append(string type, string? actor = null, string? requestId = null, string? target = null,
        IReadOnlyDictionary<string, string>? data = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = new Entry(++_seq, _clock(), type, actor, requestId, target,
                data is null ? null : new Dictionary<string, string>(data));
            _ring.Enqueue(entry);
            while (_ring.Count > _capacity)
                _ring.Dequeue();
            // Dropping persistence on overload must never delay an orchestration caller.
            _pending?.Writer.TryWrite(new Pending(entry));
            return entry.Seq;
        }
    }

    /// <summary>
    /// Events with <c>Seq &gt; afterSeq</c> in order, at most <paramref name="limit"/>. Serves the ring
    /// when it reaches back far enough, otherwise re-reads the persisted tail. Also reports the next
    /// cursor and the earliest sequence still retained anywhere, so callers can detect truncation.
    /// </summary>
    public (IReadOnlyList<Entry> Events, long NextCursor, long EarliestRetained) Read(long afterSeq, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        Entry[] recent;
        long ringStart, sequence;
        lock (_lock)
        {
            ringStart = _ring.Count == 0 ? long.MaxValue : _ring.Peek().Seq;
            recent = _ring.Where(x => x.Seq > afterSeq).Take(limit).ToArray();
            sequence = _seq;
        }
        var result = recent.ToList();
        if (afterSeq + 1 < ringStart && _path != null)
        {
            // Historical I/O never holds the producer lock. Include the live tail if disk lags.
            lock (_diskLock)
                result = ReadPersisted(afterSeq, limit).Concat(recent).DistinctBy(e => e.Seq)
                    .OrderBy(e => e.Seq).Take(limit).ToList();
        }
        long next = result.Count == 0 ? Math.Max(afterSeq, sequence) : result[^1].Seq;
        return (result, next, Math.Min(Interlocked.Read(ref _earliestRetained),
            ringStart == long.MaxValue ? Interlocked.Read(ref _earliestRetained) : ringStart));
    }

    public long LastSeq { get { lock (_lock) return _seq; } }

    // Explicit barriers are for shutdown/tests; Append and recent-tail reads never wait for storage.
    internal void Flush()
    {
        if (_pending == null) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long through;
        lock (_lock) { ObjectDisposedException.ThrowIf(_disposed, this); through = _seq; }
        _pending.Writer.WriteAsync(new Pending(null, completion, through)).AsTask().GetAwaiter().GetResult();
        completion.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Writer.TryComplete();
        }
        // The background writer owns closing its stream, including when shutdown times out.
        _persistence?.Wait(TimeSpan.FromSeconds(5));
    }

    private async Task PersistAsync()
    {
        try
        {
            while (await _pending!.Reader.WaitToReadAsync())
            {
                if (_pending.Reader.Count < 128) await Task.Delay(100);
                lock (_diskLock)
                {
                    int count = 0;
                    while (count++ < 128 && _pending.Reader.TryRead(out var pending))
                    {
                        try
                        {
                            if (_writeFailure != null) throw _writeFailure;
                            if (pending.Entry is { } entry) WriteEntry(entry);
                            else { PersistGap(pending.Through); _writer!.Flush(); }
                            pending.Completion?.TrySetResult();
                        }
                        catch (Exception error)
                        {
                            _writeFailure ??= error;
                            pending.Completion?.TrySetException(error);
                        }
                    }
                    try { if (_writeFailure == null) _writer!.Flush(); }
                    catch (Exception error) { _writeFailure ??= error; }
                }
            }
            lock (_diskLock)
            {
                try { if (_writeFailure == null) PersistGap(Interlocked.Read(ref _seq)); }
                catch (Exception error) { _writeFailure ??= error; }
            }
        }
        finally
        {
            lock (_diskLock)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }

    private void PersistGap(long through)
    {
        // Preserve the sequence high-water mark after dropped final events on a graceful stop.
        if (through > _writtenSeq) WriteEntry(new Entry(through, _clock(), "events.gap", null, null, null, null));
    }

    private StreamWriter OpenWriter() => new(new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.Read),
        new UTF8Encoding(false));

    private void WriteEntry(Entry entry)
    {
        string line = JsonSerializer.Serialize(entry, Json);
        _writer!.WriteLine(line);
        _writtenBytes += Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        _writtenSeq = entry.Seq;
        RollIfNeeded();
    }

    private List<Entry> ReadPersisted(long afterSeq, int limit)
    {
        var result = new List<Entry>();
        foreach (string file in PersistedFiles())
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            while (result.Count < limit && reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                Entry? entry = TryParse(line);
                if (entry != null && entry.Seq > afterSeq)
                    result.Add(entry);
            }
            if (result.Count >= limit)
                break;
        }
        return result;
    }

    private IEnumerable<string> PersistedFiles()
    {
        string rolled = _path + ".1";
        if (File.Exists(rolled)) yield return rolled;
        if (File.Exists(_path!)) yield return _path!;
    }

    private void Recover()
    {
        _earliestRetained = long.MaxValue;
        foreach (string file in PersistedFiles())
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                Entry? entry = TryParse(line);
                if (entry == null) continue;
                _seq = Math.Max(_seq, entry.Seq);
                _earliestRetained = Math.Min(_earliestRetained, entry.Seq);
            }
        }
        if (_earliestRetained == long.MaxValue)
            _earliestRetained = _seq + 1;
    }

    private void RollIfNeeded()
    {
        if (_path == null || _writer == null || _writtenBytes < _rollBytes)
            return;
        _writer.Dispose();
        string rolled = _path + ".1";
        File.Delete(rolled);
        File.Move(_path, rolled);
        _writer = OpenWriter();
        _writtenBytes = 0;
        Interlocked.Exchange(ref _earliestRetained, ReadEarliest(rolled));
    }

    private long ReadEarliest(string file)
    {
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (reader.ReadLine() is { } line)
        {
            Entry? entry = TryParse(line);
            if (entry != null) return entry.Seq;
        }
        return _seq + 1;
    }

    private static Entry? TryParse(string line)
    {
        try { return JsonSerializer.Deserialize<Entry>(line, Json); }
        catch (JsonException) { return null; }
    }
}
