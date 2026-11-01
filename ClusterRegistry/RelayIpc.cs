using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;

namespace ClusterRegistry;

/// <summary>Message types of the relay IPC (Docs/Gateway/RelayIpc.md). 1-99 carry JSON, 100+ binary.</summary>
internal static class RelayIpcType
{
    public const ushort Hello = 1, Heartbeat = 2, Config = 3, Paused = 5;
    public const ushort JoinRequest = 10, JoinAnswer = 11, ClientMessage = 12, ClientEvent = 15, Disconnect = 16,
        ForceReconnect = 17, SessionUpdate = 18, ReplayConnect = 19;
    public const ushort SteamAuth = 20, SteamEvent = 21, SteamPublish = 22;
    public const ushort Status = 30;
    public const ushort EnsureLink = 40, DropLink = 41, LinkState = 42, NodeControl = 43, NodeFrame = 44;
    public const ushort NodeGlobal = 100, NodePlugin = 101, ClientSend = 102;
    /// <summary><c>[u32 id][u32 total][u32 offset][chunk]</c>: part of one frame larger than <see cref="RelayIpcServer.FragmentAbove"/>.</summary>
    public const ushort Fragment = 199;

    public static bool IsJson(ushort type) => type < 100;
}

/// <summary>One received IPC message. <paramref name="Connection"/> is the relay connection it arrived on
/// (<see cref="RelayIpcServer"/> counts them); correlations are only meaningful inside that connection.</summary>
internal readonly record struct RelayIpcMessage(ushort Type, uint Correlation, ReadOnlyMemory<byte> Body, int Connection = 0)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
}

/// <summary>
/// T-0229: ClusterRegistry's end of the relay IPC with gateway-rs (Docs/Gateway/RelayIpc.md).
/// A Unix SOCK_SEQPACKET listener - one message per send, boundaries kept - that accepts one relay
/// at a time. Owns framing, the Hello exchange, heartbeats both ways, request/answer correlation and
/// peer-loss detection; what the messages MEAN is the caller's (the external-relay adapter).
///
/// Threads: one accept/receive thread; <see cref="Send"/> is safe from any thread. Handlers run on
/// the receive thread and must not block on a <see cref="RequestAsync"/> answer (it would arrive on
/// the very thread that waits); hand such work to the thread pool.
/// </summary>
internal sealed class RelayIpcServer : IDisposable
{
    /// <summary>2: <see cref="RelayIpcType.Fragment"/> (2026-09-24). Version 1 dropped every frame above 256 KiB,
    /// which is where a live world's full global-state snapshots are (0.5-4 MB).</summary>
    public const int Version = 2;
    public const int MaximumMessage = 256 * 1024;
    /// <summary>The largest frame either side sends or reassembles.</summary>
    public const int MaximumReassembled = 64 * 1024 * 1024;
    private const int HeaderSize = 6;
    private const int FragmentHeader = 12;
    /// <summary>Frames above this travel as fragments of at most this size. A SEQPACKET message must fit the
    /// socket's send buffer (net.core.wmem_default, 208 KiB here), so <see cref="MaximumMessage"/> itself
    /// fails with EMSGSIZE; 64 KiB stays well inside any default.</summary>
    public const int FragmentAbove = 64 * 1024;
    private const int FragmentChunk = FragmentAbove - HeaderSize - FragmentHeader;
    /// <summary>.NET names no constant for it; SOCK_SEQPACKET is 5 on Linux.</summary>
    private const SocketType SeqPacket = (SocketType)5;

    private readonly string _path;
    private readonly TimeSpan _heartbeat;
    private readonly int _missedLimit;
    private readonly object _sendLock = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<RelayIpcMessage>> _pending = new();
    private Socket? _listener;
    private volatile Socket? _peer;
    /// <summary>Counts accepted relay connections; stamped on every received message (T-0296 r3).</summary>
    private int _connection;
    private Thread? _acceptThread;
    private Timer? _heartbeatTimer;
    private volatile bool _running;
    private uint _nextCorrelation;
    private uint _nextFragmented;
    /// <summary>One message or fragment at a time is assembled here, under <see cref="_sendLock"/>: a
    /// multi-MB global never needs a whole-frame copy (they were LOH allocations, ~10 per message).</summary>
    private readonly byte[] _sendBuffer = new byte[FragmentAbove];
    private long _heartbeatSeq;
    private long _lastReceivedTicks;

    public string? PeerBuild { get; private set; }
    public int? PeerPid { get; private set; }
    public bool Connected => _peer != null && PeerBuild != null;
    public DateTimeOffset? LastReceived => Interlocked.Read(ref _lastReceivedTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;
    public long MessagesReceived, MessagesSent, PeerLosses, FragmentedReceived, FragmentedSent, FragmentFailures;

    /// <summary>Raised on the receive thread for every message except Hello and Heartbeat, and answers to pending requests.</summary>
    public event Action<RelayIpcMessage>? Message;
    /// <summary>Raised after a relay completed its Hello (the adapter re-sends Config, Nodes, WaGeneration).</summary>
    public event Action? PeerReady;
    /// <summary>Raised when the relay disconnects or misses its heartbeats.</summary>
    public event Action<string>? PeerLost;

    public string Build { get; init; } = typeof(RelayIpcServer).Assembly.GetName().Version?.ToString() ?? "unknown";

    public RelayIpcServer(string path, TimeSpan? heartbeat = null, int missedLimit = 3)
    {
        _path = path;
        _heartbeat = heartbeat ?? TimeSpan.FromSeconds(1);
        _missedLimit = missedLimit;
    }

    public void Start()
    {
        if (File.Exists(_path)) File.Delete(_path);   // a stale socket from a previous gateway
        _listener = new Socket(AddressFamily.Unix, SeqPacket, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(1);
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Gateway-Relay-IPC" };
        _acceptThread.Start();
        _heartbeatTimer = new Timer(_ => Beat(), null, _heartbeat, _heartbeat);
    }

    private void AcceptLoop()
    {
        byte[] buffer = new byte[MaximumMessage];
        while (_running)
        {
            Socket peer;
            try { peer = _listener!.Accept(); }
            catch (Exception) when (!_running) { return; }
            catch (SocketException error) { Console.WriteLine("WARNING: relay IPC accept failed: " + error.Message); Thread.Sleep(250); continue; }
            int connection = Interlocked.Increment(ref _connection);
            _peer = peer;
            PeerBuild = null;
            Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
            string reason = "relay disconnected";
            var reassembler = new RelayIpcReassembler(this);
            try
            {
                Send(RelayIpcType.Hello, 0, new { version = Version, role = "registry", pid = Environment.ProcessId, build = Build });
                while (_running)
                {
                    int read = peer.Receive(buffer);
                    if (read == 0) break;
                    if (read < HeaderSize) { reason = $"short message ({read} bytes)"; break; }
                    Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
                    ReadOnlySpan<byte> frame = buffer.AsSpan(0, read);
                    ReadOnlyMemory<byte> body;
                    if (BinaryPrimitives.ReadUInt16LittleEndian(frame) == RelayIpcType.Fragment)
                    {
                        byte[]? complete = reassembler.Push(frame[HeaderSize..]);
                        if (complete == null) continue;
                        if (complete.Length < HeaderSize) { reason = $"short reassembled message ({complete.Length} bytes)"; break; }
                        Interlocked.Increment(ref FragmentedReceived);
                        frame = complete;
                        body = complete.AsMemory(HeaderSize);   // the reassembled array is ours: no second copy
                    }
                    else body = frame[HeaderSize..].ToArray();
                    Interlocked.Increment(ref MessagesReceived);
                    ushort type = BinaryPrimitives.ReadUInt16LittleEndian(frame);
                    uint correlation = BinaryPrimitives.ReadUInt32LittleEndian(frame[2..]);
                    var message = new RelayIpcMessage(type, correlation, body, connection);
                    Dispatch(message);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or JsonException or InvalidDataException)
            {
                reason = error.Message;
            }
            DropPeer(peer, reason);
        }
    }

    private void Dispatch(RelayIpcMessage message)
    {
        switch (message.Type)
        {
            case RelayIpcType.Hello:
                JsonElement hello = message.Json;
                int version = hello.GetProperty("version").GetInt32();
                if (version != Version)
                    throw new InvalidDataException($"relay speaks IPC version {version}, this gateway {Version}");
                PeerPid = hello.TryGetProperty("pid", out JsonElement pid) ? pid.GetInt32() : null;
                PeerBuild = hello.TryGetProperty("build", out JsonElement build) ? build.GetString() ?? "" : "";
                Console.WriteLine($"relay IPC: gateway-rs connected (pid {PeerPid}, build {PeerBuild})");
                PeerReady?.Invoke();
                return;
            case RelayIpcType.Heartbeat:
                return;
        }
        if (message.Correlation != 0 && _pending.TryRemove(message.Correlation, out TaskCompletionSource<RelayIpcMessage>? waiter))
        {
            waiter.TrySetResult(message);
            return;
        }
        try { Message?.Invoke(message); }
        catch (Exception error) { Console.WriteLine($"WARNING: relay IPC handler for message {message.Type} failed: {error.Message}"); }
    }

    private void DropPeer(Socket peer, string reason)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _peer, null, peer), peer)) return;
        try { peer.Dispose(); } catch (Exception) { }
        bool wasReady = PeerBuild != null;
        PeerBuild = null;
        Interlocked.Increment(ref PeerLosses);
        foreach (uint key in _pending.Keys)
            if (_pending.TryRemove(key, out TaskCompletionSource<RelayIpcMessage>? waiter))
                waiter.TrySetException(new IOException("relay IPC lost: " + reason));
        if (_running) Console.WriteLine($"WARNING: relay IPC: gateway-rs lost ({reason})");
        if (wasReady && _running) PeerLost?.Invoke(reason);
    }

    private void Beat()
    {
        Socket? peer = _peer;
        if (peer == null) return;
        long last = Interlocked.Read(ref _lastReceivedTicks);
        if (DateTimeOffset.UtcNow.UtcTicks - last > _heartbeat.Ticks * _missedLimit)
        {
            DropPeer(peer, $"no message for {_missedLimit} heartbeats");
            return;
        }
        try { Send(RelayIpcType.Heartbeat, 0, new { seq = Interlocked.Increment(ref _heartbeatSeq) }); }
        catch (Exception) { }
    }

    /// <summary>Sends one message; JSON types take any serialisable body, binary types a byte array.</summary>
    public void Send(ushort type, uint correlation, object body)
    {
        byte[] payload = body switch
        {
            byte[] bytes when !RelayIpcType.IsJson(type) => bytes,
            _ when RelayIpcType.IsJson(type) => JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions),
            _ => throw new ArgumentException($"message type {type} needs a byte[] body"),
        };
        SendBinary(type, correlation, default, payload);
    }

    /// <summary>
    /// Sends the frame <c>[type][correlation][prefix][payload]</c> without ever building it whole:
    /// each message or fragment is gathered into <see cref="_sendBuffer"/>. The fragments of one frame
    /// go out back to back under the send lock, so a later message from any thread cannot overtake it.
    /// </summary>
    public void SendBinary(ushort type, uint correlation, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> payload)
    {
        int total = HeaderSize + prefix.Length + payload.Length;
        if (total > MaximumReassembled)
            throw new ArgumentException($"relay IPC message of {prefix.Length + payload.Length} bytes exceeds {MaximumReassembled}");
        Socket peer = _peer ?? throw new IOException("relay IPC: no gateway-rs connected");
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(header, type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..], correlation);
        lock (_sendLock)
        try
        {
            Span<byte> buffer = _sendBuffer;
            if (total <= FragmentAbove)
            {
                Gather(header, prefix, payload, 0, buffer[..total]);
                peer.Send(buffer[..total]);
            }
            else
            {
                uint id = _nextFragmented++;
                for (int offset = 0; offset < total; offset += FragmentChunk)
                {
                    int length = Math.Min(FragmentChunk, total - offset);
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer, RelayIpcType.Fragment);
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer[2..], 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer[HeaderSize..], id);
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer[(HeaderSize + 4)..], (uint)total);
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer[(HeaderSize + 8)..], (uint)offset);
                    Gather(header, prefix, payload, offset, buffer.Slice(HeaderSize + FragmentHeader, length));
                    peer.Send(buffer[..(HeaderSize + FragmentHeader + length)]);
                }
                Interlocked.Increment(ref FragmentedSent);
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            // #1138: the peer can be dropped (relay lost, or this server disposed) after it was read above.
            // Callers take IOException as "no relay connected"; anything else escaped a thread-pool callback
            // (OnRelayLinkState -> OnLinkConnected) and killed the gateway.
            throw new IOException("relay IPC: gateway-rs connection lost while sending: " + error.Message, error);
        }
        Interlocked.Increment(ref MessagesSent);
    }

    /// <summary>Copies bytes <c>offset..offset+destination.Length</c> of <c>a ++ b ++ c</c> into <paramref name="destination"/>.</summary>
    private static void Gather(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, int offset, Span<byte> destination)
    {
        foreach (int part in new[] { 0, 1, 2 })
        {
            ReadOnlySpan<byte> source = part switch { 0 => a, 1 => b, _ => c };
            if (offset >= source.Length) { offset -= source.Length; continue; }
            int length = Math.Min(source.Length - offset, destination.Length);
            source.Slice(offset, length).CopyTo(destination);
            destination = destination[length..];
            offset = 0;
            if (destination.IsEmpty) return;
        }
    }

    /// <summary>Splits a frame into <see cref="RelayIpcType.Fragment"/> messages of at most <see cref="FragmentAbove"/> bytes.</summary>
    internal static IEnumerable<byte[]> Fragments(uint id, byte[] frame)
    {
        for (int offset = 0; offset < frame.Length; offset += FragmentChunk)
        {
            int length = Math.Min(FragmentChunk, frame.Length - offset);
            byte[] message = new byte[HeaderSize + FragmentHeader + length];
            BinaryPrimitives.WriteUInt16LittleEndian(message, RelayIpcType.Fragment);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(HeaderSize), id);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(HeaderSize + 4), (uint)frame.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(HeaderSize + 8), (uint)offset);
            frame.AsSpan(offset, length).CopyTo(message.AsSpan(HeaderSize + FragmentHeader));
            yield return message;
        }
    }

    internal void NoteFragmentFailure(string why)
    {
        Interlocked.Increment(ref FragmentFailures);
        Console.WriteLine("WARNING: relay IPC: fragmented frame dropped: " + why);
    }

    /// <summary>Sends a request and awaits the relay's answer (same correlation).</summary>
    public async Task<RelayIpcMessage> RequestAsync(ushort type, object body, TimeSpan timeout, CancellationToken cancel = default)
    {
        uint correlation;
        do correlation = Interlocked.Increment(ref _nextCorrelation); while (correlation == 0);
        var waiter = new TaskCompletionSource<RelayIpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlation] = waiter;
        try
        {
            Send(type, correlation, body);
            return await waiter.Task.WaitAsync(timeout, cancel);
        }
        finally { _pending.TryRemove(correlation, out _); }
    }

    /// <summary>
    /// Answers a relay request (JoinRequest and the like). T-0296 r3: only on the connection that asked. Answers are
    /// produced off the receive thread, so the relay can be lost and reconnected in between; the new connection has
    /// no such request, and an accept it silently drops leaves a session here that the relay does not have. Checked
    /// again after the send, so a reconnect during it is reported as well.
    /// </summary>
    public void Answer(RelayIpcMessage request, ushort type, object body)
    {
        if (request.Connection != Volatile.Read(ref _connection))
            throw new IOException("relay IPC: the request's gateway-rs connection is gone");
        Send(type, request.Correlation, body);
        if (request.Connection != Volatile.Read(ref _connection))
            throw new IOException("relay IPC: the request's gateway-rs connection was lost while answering");
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose()
    {
        _running = false;
        _heartbeatTimer?.Dispose();
        try { _listener?.Dispose(); } catch (Exception) { }
        if (_peer is Socket peer) DropPeer(peer, "gateway stopping");
        try { if (File.Exists(_path)) File.Delete(_path); } catch (Exception) { }
    }
}

/// <summary>
/// Reassembles <see cref="RelayIpcType.Fragment"/> bodies (one instance per connection, receive
/// thread only). Fragments of one frame arrive in order; fragments of different frames may
/// interleave, because gateway-rs sends from several threads without a lock. Any inconsistency
/// drops that frame and says so.
/// </summary>
internal sealed class RelayIpcReassembler(RelayIpcServer? owner)
{
    private const int MaximumPartials = 64;
    private readonly Dictionary<uint, (byte[] Buffer, int Filled)> _partials = new();
    public long Failures;

    public byte[]? Push(ReadOnlySpan<byte> body)
    {
        if (body.Length < 12) return Fail(null, $"fragment of {body.Length} bytes has no header");
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
        ReadOnlySpan<byte> chunk = body[12..];
        if (offset == 0)
        {
            if (_partials.ContainsKey(id)) Fail(id, $"frame {id} restarted before it completed");
            if (total < 6 || total > RelayIpcServer.MaximumReassembled)
                return Fail(null, $"frame {id} of {total} bytes is outside 6..{RelayIpcServer.MaximumReassembled}");
            if (_partials.Count >= MaximumPartials)
                foreach (uint other in _partials.Keys.ToArray()) Fail(other, $"more than {MaximumPartials} frames in reassembly");
            _partials[id] = (new byte[total], 0);
        }
        if (!_partials.TryGetValue(id, out var partial)) return Fail(null, $"fragment at {offset} of frame {id} without its start");
        if (partial.Buffer.Length != total || partial.Filled != offset || offset + (uint)chunk.Length > total)
            return Fail(id, $"fragment {offset}+{chunk.Length} of {total} does not follow {partial.Filled}");
        chunk.CopyTo(partial.Buffer.AsSpan((int)offset));
        partial.Filled += chunk.Length;
        if (partial.Filled < total) { _partials[id] = partial; return null; }
        _partials.Remove(id);
        return partial.Buffer;
    }

    private byte[]? Fail(uint? id, string why)
    {
        if (id is uint key) _partials.Remove(key);
        Failures++;
        if (owner != null) owner.NoteFragmentFailure(why);
        else Console.WriteLine("WARNING: relay IPC: fragmented frame dropped: " + why);
        return null;
    }
}
