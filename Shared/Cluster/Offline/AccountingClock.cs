using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Sandbox.Game.World;

namespace ClusterPlugin;

// Resource work is not replication time. The Gateway durably acknowledges actual
// WA native progress; neither a missed sample nor wall time extrapolates work.
internal static class AccountingClock
{
    internal sealed record Sample([property: JsonRequired] string World, [property: JsonRequired] string Epoch,
        [property: JsonRequired] long Frame, [property: JsonRequired] long Generation, [property: JsonRequired] long SunTicks,
        [property: JsonRequired] long HistoryFrom, [property: JsonRequired] Dictionary<long, long> SunRuns, [property: JsonRequired] int Version = 1);
    private static readonly object Sync = new();
    private static Sample committed;
    private static bool gatewaySeen;
    private static MySession session;
    private static int lastNative;
    private static long frame;
    private static long frameOrigin;
    private static string epoch;
    private static string nativeEpoch;
    private static int lastLocalNative;
    private static long localFrame;
    private static long localHistoryFrom;
    private static long phaseTicks;
    private static readonly SortedDictionary<long, long> pendingSun = new();
    private static readonly SortedDictionary<long, long> localSun = new();
    private static long localRunFrame, localRunTicks, authorityRunFrame, authorityRunTicks;
    private static bool haveLocalRun, haveAuthorityRun;
    private static string fault;
    private const string Subsystem = "offline-accounting-clock";
    private static readonly HttpClient Client = ClusterHttp.Client(TimeSpan.FromSeconds(10));

    internal static void Install()
    {
        ClusterRuntime.AuthorizeRegistryClient(Client);
        GlobalStateChannel.RegisterQueryHandler(Subsystem, (_, _) =>
        {
            var done = new TaskCompletionSource<Sample>(TaskCreationOptions.RunContinuationsAsynchronously);
            Sandbox.MySandboxGame.Static.Invoke(() =>
            {
                try { done.TrySetResult(Commit()); }
                catch (Exception e) { done.TrySetException(e); }
            }, "Cluster native accounting snapshot");
            return JsonSerializer.Serialize(done.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
        });
    }

    private static Sample Commit()
    {
        Update();
        var sample = Proposal() ?? throw new InvalidOperationException("WA simulation clock has not initialized");
        var body = new { Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"), Epoch = ClusterRuntime.RegistryNodeEpoch, Sample = sample };
        using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
        using var response = Client.PostAsync(gateway + "/registry/offline-accounting-clock", content).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<Sample>(response.Content.ReadAsStream());
        Receive(result, ClusterRuntime.AuthorityGeneration);
        return result;
    }

    // Freeze and wake linearize at an actual WA native-work sample, not at a
    // possibly old heartbeat. Replies are published only after the durable commit.
    internal static (string Epoch, long Frame, long Generation, long SunTicks) ReadFresh()
    {
        var sample = ClusterRuntime.IsWorldAuthority ? Commit()
            : JsonSerializer.Deserialize<Sample>(GlobalStateChannel.SendCommand(Subsystem, ""));
        Receive(sample, ClusterRuntime.AuthorityGeneration);
        return Read();
    }

    internal static byte[] SunPath(string domain, string requestedEpoch, long from, long through)
    {
        if (domain == "ClusterLocal")
        {
            lock (Sync)
            {
                if (requestedEpoch != nativeEpoch || from < localHistoryFrom || through > localFrame || through < from)
                    throw new InvalidDataException("Local sun/work interval is unavailable");
                if (through > from && !localSun.Any(p => p.Key <= from)) throw new InvalidDataException("Local sun/work origin is missing");
                var prior = localSun.LastOrDefault(p => p.Key <= from);
                var runs = localSun.Where(p => p.Key > from && p.Key < through).Prepend(prior).Select(p => new[] { p.Key, p.Value }).ToArray();
                return JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Epoch = nativeEpoch, From = from, Through = through, Runs = runs });
            }
        }
        string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
        return Client.GetByteArrayAsync(gateway + "/registry/offline-accounting-sun?epoch=" + Uri.EscapeDataString(requestedEpoch)
            + "&from=" + from + "&through=" + through).GetAwaiter().GetResult();
    }

    internal static void CompletedNativeUpdate(MySession current, int before, long sunTicks)
    {
        try { RecordNativeUpdate(current, before, sunTicks); }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            // A missing accounting observation holds settlement, not the game.
            lock (Sync) fault ??= e.Message;
        }
    }

    internal static long SunTicks(MySession current) => current.ElapsedGameTime.Ticks
        - (ClusterRuntime.IsWorldAuthority ? TimeOfDayService.OffsetMilliseconds : SessionClockPatch.AppliedOffsetMilliseconds) * 10000;

    private static void RecordNativeUpdate(MySession current, int before, long sunTicks)
    {
        if (current?.Ready != true || !ClusterRuntime.IsClustered || before == current.GameplayFrameCounter) return;
        lock (Sync)
        {
            if (!ReferenceEquals(session, current)) { ObserveLocal(); return; }
            if (unchecked((uint)(current.GameplayFrameCounter - before)) != 1 || sunTicks < 0)
                throw new InvalidDataException("Unobserved native accounting progress");
            ObserveLocal();
            long work = localFrame - 1;
            if (!haveLocalRun || sunTicks != checked(localRunTicks + (work - localRunFrame) * 160000))
            {
                localSun[work] = sunTicks;
                localRunFrame = work; localRunTicks = sunTicks; haveLocalRun = true;
            }
            if (localFrame % 100 == 0)
            {
                // LIVE profiles have already executed their native work. This
                // advisory cache is bounded by the existing observation window;
                // durable unpaid WA runs below are never evicted here.
                long window = (ClusterConfig.GetInt("offline.observeSeconds") * 1000L + 15) / 16;
                if (ClusterConfig.Get("offline.longObservation") == "InPlace")
                    window = Math.Max(window, checked((long)Math.Ceiling(current.Settings.SunRotationIntervalMinutes * 60000d / 16)));
                localHistoryFrom = Math.Max(0, localFrame - Math.Max(1, window));
                var older = localSun.Keys.TakeWhile(k => k <= localHistoryFrom).ToArray();
                foreach (long key in older.Take(Math.Max(0, older.Length - 1))) localSun.Remove(key);
            }
            if (ClusterRuntime.IsWorldAuthority && epoch != null)
            {
                if (!haveAuthorityRun || sunTicks != checked(authorityRunTicks + (frame - authorityRunFrame) * 160000))
                {
                    pendingSun[frame] = sunTicks;
                    authorityRunFrame = frame; authorityRunTicks = sunTicks; haveAuthorityRun = true;
                }
                frame = checked(frame + 1);
            }
            phaseTicks = SunTicks(current);
        }
    }

    internal static long NativeFrame
    {
        get { lock (Sync) { ObserveLocal(); return localFrame; } }
    }
    internal static double NativeMilliseconds => checked(NativeFrame * 16);
    private static void ObserveLocal()
    {
        var current = MySession.Static;
        if (current?.Ready != true) throw new InvalidOperationException("Native accounting session is unavailable");
        if (!ReferenceEquals(session, current))
        {
            session = current;
            nativeEpoch = Guid.NewGuid().ToString("D");
            lastNative = lastLocalNative = current.GameplayFrameCounter;
            localFrame = 0;
            localHistoryFrom = 0;
            epoch = null;
            localSun.Clear(); pendingSun.Clear(); haveLocalRun = haveAuthorityRun = false;
            fault = null;
        }
        localFrame = checked(localFrame + unchecked((uint)(current.GameplayFrameCounter - lastLocalNative)));
        lastLocalNative = current.GameplayFrameCounter;
    }
    internal static string NativeEpoch
    {
        get { lock (Sync) return nativeEpoch ?? throw new InvalidOperationException("Native accounting session is unavailable"); }
    }

    internal static void Update()
    {
        var current = MySession.Static;
        if (current?.Ready != true) return;
        lock (Sync)
        {
            ObserveLocal();
            if (!ClusterRuntime.IsWorldAuthority || !gatewaySeen) return;
            string world = current.WorldId.ToString();
            if (committed != null && committed.World != world) throw new InvalidDataException("Accounting clock belongs to another world");
            if (epoch == null)
            {
                epoch = committed?.Epoch ?? Guid.NewGuid().ToString("D");
                frame = committed?.Frame ?? 0;
                frameOrigin = frame;
                lastNative = current.GameplayFrameCounter;
            }
            phaseTicks = SunTicks(current);
        }
    }

    internal static Sample Proposal()
    {
        lock (Sync) return fault != null || epoch == null || session?.Ready != true || !ClusterRuntime.IsWorldAuthority ? null
            : new(session.WorldId.ToString(), epoch, frame, ClusterRuntime.AuthorityGeneration, phaseTicks,
                committed?.Frame ?? 0, pendingSun.Where(p => p.Key >= (committed?.Frame ?? 0)).ToDictionary(p => p.Key, p => p.Value));
    }

    internal static void Receive(Sample value, long generation)
    {
        lock (Sync)
        {
            gatewaySeen = true;
            if (value == null) return;
            if (value.Version != 1 || value.SunTicks < 0 || value.SunRuns == null || !Guid.TryParseExact(value.Epoch, "D", out _) || value.Frame < 0 || value.Generation <= 0
                || value.Generation > generation || string.IsNullOrEmpty(value.World))
                throw new InvalidDataException("Invalid durable accounting clock");
            if (committed != null && (value.World != committed.World || value.Epoch != committed.Epoch || value.Frame < committed.Frame))
                throw new InvalidDataException("Durable accounting clock changed epoch or moved backwards");
            if (committed != null && value.Generation < committed.Generation) return;
            committed = value;
            foreach (long key in pendingSun.Keys.TakeWhile(k => k < value.Frame).ToArray()) pendingSun.Remove(key);
        }
    }

    internal static bool Ready
    {
        get { lock (Sync) return !ClusterRuntime.IsAuthorityFenced && fault == null && committed != null && committed.Generation == ClusterRuntime.AuthorityGeneration
            && MySession.Static?.Ready == true && committed.World == MySession.Static.WorldId.ToString(); }
    }

    internal static (string Epoch, long Frame, long Generation, long SunTicks) Read()
    {
        lock (Sync)
        {
            if (!Ready) throw new InvalidOperationException("Current durable WA simulation clock is unavailable: " + fault);
            return (committed.Epoch, committed.Frame, committed.Generation, committed.SunTicks);
        }
    }
}
