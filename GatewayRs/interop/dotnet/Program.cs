// Interop peer for the Rust `lnl` crate: a real LiteNetLib 1.3.1 NetManager configured like the
// gateway (ChannelsCount=16), acting as client or server and running the shared message script.
// The same script is implemented in GatewayRs/interop/src/script.rs; keep both in step.
//
// The NetManager runs in manual mode: receive (PollEvents), send and resend (ManualUpdate) and every
// event happen on this thread, as in the single-threaded Rust peer. In threaded mode two LiteNetLib
// 1.3.1 races made the suite flaky on loaded CI runners, both outside the wire protocol under test:
//   * NetManager.Connect sends the ConnectRequest before AddPeer, so a fast ConnectAccept can reach
//     the receive thread first and is dropped; the client connects only on its 500 ms retry, and
//     NetPeer.ProcessPacket discards everything the server sent meanwhile while the peer is still
//     Outgoing (PR #24: "unreliable 0 != 40", 405 of 813 messages = only the reliable resends).
//   * BaseChannel.SendAndCheckQueue clears the queued flag after SendNextPackets found the queue
//     empty; a Send from this thread in between sees the flag still set and does not requeue the
//     channel, so its packets stay queued until the next Send on that channel (PR #24: one
//     channel's ReliableSequenced messages never sent, "RSeq 180/192").
using System.Diagnostics;
using System.Net;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            return new Session(Options.Parse(args)).Run();
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL cs: " + e.Message);
            return 1;
        }
    }
}

sealed class Options
{
    public string Role = "";
    public IPEndPoint Endpoint = new(IPAddress.Loopback, 0);
    public string End = "close";
    public int TimeoutMs = 4000;
    public bool MtuDiscovery;
    public int ExpectMtu;
    public int MinRtt = -1;
    public int MaxRtt = -1;
    public int HoldMs;
    public bool Reject;
    public bool ExpectReject;
    public int Per = 12;
    public bool Big = true;
    public int DeadlineMs = 60000;
    public int GraceMs = 1500;
    public int ExpectUnreliable = -1;
    public bool AllowAddressChange;
    public bool ExpectAddressChange;

    public static Options Parse(string[] a)
    {
        var o = new Options { Role = a[0] };
        if (o.Role != "server" && o.Role != "client") throw new ArgumentException("role must be server or client");
        for (int i = 1; i < a.Length; i++)
        {
            string Next() => a[++i];
            switch (a[i])
            {
                case "--bind": case "--connect": o.Endpoint = IPEndPoint.Parse(Next()); break;
                case "--end": o.End = Next(); break;
                case "--timeout-ms": o.TimeoutMs = int.Parse(Next()); break;
                case "--mtu-discovery": o.MtuDiscovery = true; break;
                case "--expect-mtu": o.ExpectMtu = int.Parse(Next()); break;
                case "--min-rtt": o.MinRtt = int.Parse(Next()); break;
                case "--max-rtt": o.MaxRtt = int.Parse(Next()); break;
                case "--hold-ms": o.HoldMs = int.Parse(Next()); break;
                case "--reject": o.Reject = true; break;
                case "--expect-reject": o.ExpectReject = true; break;
                case "--per": o.Per = int.Parse(Next()); break;
                case "--no-big": o.Big = false; break;
                case "--deadline-ms": o.DeadlineMs = int.Parse(Next()); break;
                case "--grace-ms": o.GraceMs = int.Parse(Next()); break;
                case "--expect-unreliable": o.ExpectUnreliable = int.Parse(Next()); break;
                case "--allow-address-change": o.AllowAddressChange = true; break;
                case "--expect-address-change": o.ExpectAddressChange = true; break;
                default: throw new ArgumentException("unknown option " + a[i]);
            }
        }
        return o;
    }
}

static class Script
{
    public const string Key = "SE-DirectTransport-v1";
    public const ulong ClientId = 4242;
    public const byte Verified = 0xFF;
    public const byte VerifiedAck = 0xFE;
    public const byte Channels = 16;

    public static readonly DeliveryMethod[] Channeled =
        [DeliveryMethod.ReliableUnordered, DeliveryMethod.Sequenced, DeliveryMethod.ReliableOrdered, DeliveryMethod.ReliableSequenced];

    public static List<(DeliveryMethod M, byte C, int Size)> Plan(int per, bool big)
    {
        var v = new List<(DeliveryMethod, byte, int)>();
        for (byte c = 0; c < Channels; c++)
            foreach (var m in Channeled)
                for (int s = 0; s < per; s++)
                    v.Add((m, c, 10 + (s * 97 + c * 13 + (int)m * 7) % 990));
        for (int s = 0; s < 40; s++)
            v.Add((DeliveryMethod.Unreliable, 0, 10 + s * 53 % 1000));
        if (big)
        {
            v.Add((DeliveryMethod.ReliableOrdered, 1, 65536));
            v.Add((DeliveryMethod.ReliableOrdered, 1, 3000));
            v.Add((DeliveryMethod.ReliableOrdered, 1, 65536));
            v.Add((DeliveryMethod.ReliableUnordered, 2, 65536));
            v.Add((DeliveryMethod.ReliableUnordered, 2, 5000));
        }
        return v;
    }

    public static byte Pattern(byte m, byte c, uint seq, int i) => unchecked((byte)(seq * 31u + (uint)(i * 7 + m * 13 + c)));

    public static byte[] Payload(DeliveryMethod m, byte c, uint seq, int len)
    {
        var b = new byte[len];
        b[0] = (byte)m;
        b[1] = c;
        BitConverter.TryWriteBytes(b.AsSpan(2), seq);
        BitConverter.TryWriteBytes(b.AsSpan(6), (uint)len);
        for (int i = 10; i < len; i++) b[i] = Pattern((byte)m, c, seq, i);
        return b;
    }
}

sealed class Verifier
{
    private readonly Dictionary<(byte, byte), uint> _expected = new();
    private readonly Dictionary<(byte, byte), uint> _orderedNext = new();
    private readonly Dictionary<(byte, byte), bool[]> _seen = new();
    private readonly Dictionary<(byte, byte), uint> _last = new();
    public long Received;
    public long Unreliable;

    public Verifier(List<(DeliveryMethod M, byte C, int Size)> plan)
    {
        foreach (var (m, c, _) in plan)
            _expected[((byte)m, c)] = _expected.GetValueOrDefault(((byte)m, c)) + 1;
    }

    public void OnReceive(byte channel, DeliveryMethod method, byte[] data)
    {
        if (data.Length < 10) throw new Exception($"short payload {data.Length}");
        byte m = data[0], c = data[1];
        uint seq = BitConverter.ToUInt32(data, 2);
        uint len = BitConverter.ToUInt32(data, 6);
        if (len != data.Length) throw new Exception($"length {data.Length} != header {len}");
        for (int i = 10; i < data.Length; i++)
            if (data[i] != Script.Pattern(m, c, seq, i)) throw new Exception($"corrupt byte {i} in m{m} c{c} seq{seq}");
        if (m != (byte)method) throw new Exception($"method mismatch {m} vs {method}");
        if (method == DeliveryMethod.Unreliable ? channel != 0 : channel != c) throw new Exception($"channel mismatch {c} vs {channel}");
        var key = (m, c);
        if (!_expected.TryGetValue(key, out uint n) || seq >= n) throw new Exception($"seq {seq} out of plan for m{m} c{c}");
        Received++;
        switch (method)
        {
            case DeliveryMethod.ReliableOrdered:
                uint next = _orderedNext.GetValueOrDefault(key);
                if (seq != next) throw new Exception($"ReliableOrdered out of order on channel {c}: {seq} expected {next}");
                _orderedNext[key] = next + 1;
                break;
            case DeliveryMethod.ReliableUnordered:
                if (!_seen.TryGetValue(key, out var seen)) _seen[key] = seen = new bool[n];
                if (seen[seq]) throw new Exception($"ReliableUnordered duplicate {seq} on channel {c}");
                seen[seq] = true;
                break;
            case DeliveryMethod.Sequenced:
            case DeliveryMethod.ReliableSequenced:
                if (_last.TryGetValue(key, out uint l) && seq <= l) throw new Exception($"sequenced not increasing: {seq} after {l} (m{m} c{c})");
                _last[key] = seq;
                break;
            default:
                Unreliable++;
                break;
        }
    }

    public bool Complete => _expected.All(kv => (DeliveryMethod)kv.Key.Item1 switch
    {
        DeliveryMethod.ReliableOrdered => _orderedNext.GetValueOrDefault(kv.Key) == kv.Value,
        DeliveryMethod.ReliableUnordered => _seen.TryGetValue(kv.Key, out var s) && s.All(x => x),
        DeliveryMethod.ReliableSequenced => _last.TryGetValue(kv.Key, out var l) && l == kv.Value - 1,
        _ => true,
    });
}

sealed class Session(Options o)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(DeliveryMethod M, byte C, int Size)> _plan = Script.Plan(o.Per, o.Big);
    private NetPeer? _peer;
    private string? _failure;
    private (DisconnectReason Reason, string Data, long At)? _disconnect;
    private ConnectionRequest? _held;
    private long _heldAt;
    private long _rejectedAt = -1;
    private bool _sentVerified, _gotVerified, _gotVerifiedAck;
    private int _latencyUpdates;
    private int _addressChanges;
    private string Other => o.Role == "server" ? "client" : "server";

    private void Log(string s) => Console.WriteLine($"[cs {o.Role} {_clock.ElapsedMilliseconds,6}ms] {s}");

    private int Pass(string s)
    {
        Console.WriteLine($"PASS cs {o.Role}: {s}");
        return 0;
    }

    private int Fail(string s)
    {
        Console.WriteLine($"FAIL cs {o.Role}: {s}");
        return 1;
    }

    private long _lastUpdateTicks;

    /// <summary>Manual mode: receive and dispatch events, then run the send/resend/timeout logic.</summary>
    private void Pump(NetManager nm)
    {
        nm.PollEvents();
        long ticks = _clock.ElapsedTicks;
        nm.ManualUpdate((float)((ticks - _lastUpdateTicks) * 1000.0 / Stopwatch.Frequency));
        _lastUpdateTicks = ticks;
    }

    private void Linger(NetManager nm, int ms)
    {
        long until = _clock.ElapsedMilliseconds + ms;
        while (_clock.ElapsedMilliseconds < until)
        {
            Pump(nm);
            Thread.Sleep(1);
        }
    }

    public int Run()
    {
        var verifier = new Verifier(_plan);
        var listener = new EventBasedNetListener();
        var nm = new NetManager(listener)
        {
            UnsyncedEvents = false,
            AutoRecycle = true,
            DisconnectTimeout = o.TimeoutMs,
            ReconnectDelay = 500,
            MaxConnectAttempts = 20,
            ChannelsCount = Script.Channels,
            MtuDiscovery = o.MtuDiscovery,
            IPv6Enabled = false,
            AllowPeerAddressChange = o.AllowAddressChange,
        };
        listener.PeerAddressChangedEvent += (p, previous) =>
        {
            Log($"peer address changed {previous} -> {p}");
            _addressChanges++;
        };
        listener.ConnectionRequestEvent += req =>
        {
            var r = req.Data;
            string key = r.GetString();
            ulong id = r.GetULong();
            string name = r.GetString();
            if (key != Script.Key || id != Script.ClientId || name != "interop")
            {
                _failure = $"bad handshake data {key}/{id}/{name}";
                req.Reject();
                return;
            }
            Log($"connection request from {req.RemoteEndPoint}");
            if (o.Reject) { req.Reject(Encoding.UTF8.GetBytes("go-away")); _rejectedAt = _clock.ElapsedMilliseconds; }
            else if (o.HoldMs > 0) { _held = req; _heldAt = _clock.ElapsedMilliseconds; }
            else req.Accept();
        };
        listener.PeerConnectedEvent += p =>
        {
            Log($"connected {p} id={p.Id} remoteId={p.RemoteId}");
            _peer = p;
            var seqs = new Dictionary<(DeliveryMethod, byte), uint>();
            foreach (var (m, c, size) in _plan)
            {
                uint s = seqs.GetValueOrDefault((m, c));
                seqs[(m, c)] = s + 1;
                p.Send(Script.Payload(m, c, s, size), c, m);
            }
        };
        listener.PeerDisconnectedEvent += (p, info) =>
        {
            string data = info.AdditionalData.IsNull ? "" : Encoding.UTF8.GetString(info.AdditionalData.GetRemainingBytes());
            Log($"disconnected reason={info.Reason} data='{data}'");
            _disconnect = (info.Reason, data, _clock.ElapsedMilliseconds);
        };
        listener.NetworkLatencyUpdateEvent += (p, latency) => _latencyUpdates++;
        listener.NetworkReceiveEvent += (p, reader, channel, method) =>
        {
            byte[] data = reader.GetRemainingBytes();
            try
            {
                if (data.Length == 1 && data[0] == Script.Verified)
                {
                    _gotVerified = true;
                    p.Send([Script.VerifiedAck], 0, DeliveryMethod.ReliableOrdered);
                }
                else if (data.Length == 1 && data[0] == Script.VerifiedAck) _gotVerifiedAck = true;
                else verifier.OnReceive(channel, method, data);
            }
            catch (Exception e)
            {
                _failure ??= e.Message;
            }
        };

        if (o.Role == "server")
        {
            if (!nm.StartInManualMode(o.Endpoint.Address, IPAddress.IPv6Any, o.Endpoint.Port)) return Fail("bind failed");
        }
        else
        {
            if (!nm.StartInManualMode(0)) return Fail("bind failed");
            var w = new NetDataWriter();
            w.Put(Script.Key);
            w.Put(Script.ClientId);
            w.Put("interop");
            nm.Connect(o.Endpoint, w);
        }
        Log($"started on port {nm.LocalPort}");

        long doneAt = -1;
        bool closed = false;
        _lastUpdateTicks = _clock.ElapsedTicks;
        while (_clock.ElapsedMilliseconds < o.DeadlineMs)
        {
            Pump(nm);
            long now = _clock.ElapsedMilliseconds;
            if (_failure != null) return Fail(_failure);
            if (_held != null && now - _heldAt >= o.HoldMs)
            {
                Log($"accepting held request after {now - _heldAt} ms");
                _held.Accept();
                _held = null;
            }
            if (o.Reject && o.Role == "server")
            {
                // The rejecting peer resends its Disconnect until the client answers ShutdownOk.
                if (_peer != null) return Fail("a rejected client connected");
                if (_rejectedAt >= 0 && now - _rejectedAt >= 500 && nm.GetPeersCount(ConnectionState.ShutdownRequested) == 0)
                {
                    nm.Stop();
                    return Pass("rejected the client; it answered ShutdownOk");
                }
                if (_rejectedAt >= 0 && now - _rejectedAt > 10000) return Fail("no ShutdownOk for the reject");
                Thread.Sleep(1);
                continue;
            }
            if (_disconnect is { } d && !closed)
            {
                if (o.ExpectReject)
                {
                    nm.Stop();
                    return d.Reason == DisconnectReason.ConnectionRejected && d.Data == "go-away"
                        ? Pass("rejected with data 'go-away'")
                        : Fail($"expected ConnectionRejected/go-away, got {d.Reason}/'{d.Data}'");
                }
                if (doneAt < 0) return Fail($"disconnected before the script completed: {d.Reason} (received {verifier.Received}, verified={_gotVerified}, ack={_gotVerifiedAck})");
                if (o.End == "await-close")
                {
                    // Keep answering the remote's resent Disconnects with ShutdownOk for a while.
                    Linger(nm, 3000);
                    nm.Stop();
                    return d.Reason == DisconnectReason.RemoteConnectionClose && d.Data == "bye-" + Other
                        ? Pass($"remote close with data '{d.Data}'")
                        : Fail($"expected RemoteConnectionClose/bye-{Other}, got {d.Reason}/'{d.Data}'");
                }
                if (o.End == "await-timeout")
                {
                    nm.Stop();
                    return d.Reason == DisconnectReason.Timeout
                        ? Pass($"timeout detected {d.At - doneAt} ms after completion")
                        : Fail($"expected Timeout, got {d.Reason}");
                }
                return Fail($"unexpected disconnect {d.Reason}");
            }
            if (_peer != null && doneAt < 0)
            {
                if (verifier.Complete && !_sentVerified)
                {
                    _sentVerified = true;
                    Log($"script complete: {verifier.Received} messages, {verifier.Unreliable} unreliable");
                    _peer.Send([Script.Verified], 0, DeliveryMethod.ReliableOrdered);
                }
                bool mtuOk = o.ExpectMtu == 0 || _peer.Mtu == o.ExpectMtu;
                bool moveOk = !o.ExpectAddressChange || _addressChanges > 0;
                if (_sentVerified && _gotVerified && _gotVerifiedAck && _latencyUpdates > 0 && mtuOk && moveOk)
                {
                    doneAt = now;
                    int rtt = _peer.RoundTripTime;
                    Log($"done: rtt={rtt} ms mtu={_peer.Mtu} unreliable={verifier.Unreliable} latencyUpdates={_latencyUpdates}");
                    if (o.MinRtt >= 0 && rtt < o.MinRtt) return Fail($"rtt {rtt} < {o.MinRtt}");
                    if (o.MaxRtt >= 0 && rtt > o.MaxRtt) return Fail($"rtt {rtt} > {o.MaxRtt}");
                    if (o.ExpectUnreliable >= 0 && verifier.Unreliable != o.ExpectUnreliable) return Fail($"unreliable {verifier.Unreliable} != {o.ExpectUnreliable}");
                }
            }
            if (doneAt >= 0 && !closed && now - doneAt >= o.GraceMs)
            {
                if (o.End == "close")
                {
                    Log("disconnecting");
                    nm.DisconnectPeer(_peer!, Encoding.UTF8.GetBytes("bye-" + o.Role));
                    closed = true;
                }
                else if (o.End == "die")
                {
                    Console.WriteLine($"PASS cs {o.Role}: script complete, exiting without disconnect");
                    Environment.Exit(0);
                }
            }
            if (closed)
            {
                if (_peer!.ConnectionState == ConnectionState.Disconnected)
                {
                    bool ownEvent = _disconnect is { Reason: DisconnectReason.DisconnectPeerCalled };
                    nm.Stop();
                    return ownEvent ? Pass("closed; remote answered ShutdownOk") : Fail($"own disconnect event missing: {_disconnect}");
                }
                if (now - doneAt > o.GraceMs + 5000) return Fail("no ShutdownOk after disconnect");
            }
            Thread.Sleep(1);
        }
        return Fail($"deadline: received {verifier.Received} complete={verifier.Complete} verified={_gotVerified} ack={_gotVerifiedAck} latency={_latencyUpdates} mtu={_peer?.Mtu} state={_peer?.ConnectionState}");
    }
}
