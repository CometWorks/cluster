using ClusterPlugin;
using Sandbox.Game.World;
using System.Text.Json;

int checks = 0;
void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
void Refuses(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { checks++; return; } throw new Exception("Invalid accounting domain was accepted"); }
var session = MySession.Static = new() { Ready = true, WorldId = Guid.NewGuid(), GameplayFrameCounter = 1000, ElapsedGameTime = TimeSpan.FromSeconds(100) };
AccountingClock.Update();
Check(AccountingClock.NativeMilliseconds == 0, "uninitialized local clock starts at a native origin, not host uptime or session display time");
Refuses(() => AccountingClock.Read());
string epoch = Guid.NewGuid().ToString("D");
AccountingClock.Receive(new(session.WorldId.ToString(), epoch, 100, 1, session.ElapsedGameTime.Ticks, 100, []), 1);
AccountingClock.Update();
void Native(int count)
{
    for (int i = 0; i < count; i++)
    {
        int before = session.GameplayFrameCounter;
        long sun = session.ElapsedGameTime.Ticks - TimeOfDayService.OffsetMilliseconds * 10000;
        session.GameplayFrameCounter = unchecked(before + 1);
        session.ElapsedGameTime += TimeSpan.FromMilliseconds(16);
        AccountingClock.CompletedNativeUpdate(session, before, sun);
        AccountingClock.Update();
    }
}
Native(5);
Check(AccountingClock.Proposal().Frame == 105 && AccountingClock.NativeMilliseconds == 80, "ordinary native updates, not display milliseconds, own work");
session.ElapsedGameTime += TimeSpan.FromSeconds(40); AccountingClock.Update();
Check(AccountingClock.Proposal().Frame == 105 && AccountingClock.NativeMilliseconds == 80, "stalled native progress and sun/time-sync passage grant no work");
TimeOfDayService.OffsetMilliseconds = 1800000; session.ElapsedGameTime += TimeSpan.FromMilliseconds(1800000);
Native(2);
Check(AccountingClock.Proposal().Frame == 107 && AccountingClock.NativeMilliseconds == 112, "admin offset is separate from work");
var proposal = AccountingClock.Proposal();
Check(proposal.SunRuns.Count == 2 && proposal.SunRuns[105] == TimeSpan.FromSeconds(140.080).Ticks, "ordered history records non-admin jump and excludes manual offset");
AccountingClock.Receive(proposal with { HistoryFrom = proposal.Frame, SunRuns = [] }, 1);
Check(AccountingClock.Read().Frame == 107, "only acknowledged durable progress settles");
ClusterRuntime.AuthorityGeneration = 2;
Check(!AccountingClock.Ready, "generation change invalidates old ready sample"); Refuses(() => AccountingClock.Read());
AccountingClock.Receive(proposal with { Generation = 2, HistoryFrom = proposal.Frame, SunRuns = [] }, 2);
Check(AccountingClock.Ready, "explicit same-epoch next-generation acknowledgement restores readiness");
Refuses(() => AccountingClock.Receive(proposal with { Epoch = Guid.NewGuid().ToString("D") }, 2));
AccountingClock.Receive(proposal with { HistoryFrom = proposal.Frame, SunRuns = [] }, 2);
Check(AccountingClock.Ready, "late previous-generation reply cannot revoke a current clock");
Refuses(() => AccountingClock.Receive(proposal with { Frame = 106, Generation = 2 }, 2));
string oldNativeEpoch = AccountingClock.NativeEpoch;
session = MySession.Static = new() { Ready = true, WorldId = session.WorldId, GameplayFrameCounter = int.MaxValue - 1, ElapsedGameTime = session.ElapsedGameTime };
AccountingClock.Update();
Check(AccountingClock.NativeMilliseconds == 0 && AccountingClock.NativeEpoch != oldNativeEpoch && AccountingClock.Proposal().Frame == 107, "restart rebases at durable work and gives local observations a new identity");
Native(4);
Check(AccountingClock.NativeMilliseconds == 64 && AccountingClock.Proposal().Frame == 111, "native int rollover is not a backwards clock or a huge duration");
ClusterRuntime.IsWorldAuthority = false;
session.ElapsedGameTime = TimeSpan.FromSeconds(50); SessionClockPatch.AppliedOffsetMilliseconds = 0;
Check(AccountingClock.SunTicks(session) == TimeSpan.FromSeconds(50).Ticks, "received-but-unapplied offset is not subtracted from a regular node's old clock");
session.ElapsedGameTime += TimeSpan.FromMilliseconds(TimeOfDayService.OffsetMilliseconds);
SessionClockPatch.AppliedOffsetMilliseconds = TimeOfDayService.OffsetMilliseconds;
Check(AccountingClock.SunTicks(session) == TimeSpan.FromSeconds(50).Ticks, "actual applied sun step leaves non-admin native history unchanged");
if (args.Length == 1)
{
    long beforeMemory = GC.GetTotalMemory(true), beforeAllocated = GC.GetTotalAllocatedBytes(true);
    for (int i = 0; i < 60000; i++)
    {
        // Worst-case live cache: each observed native update starts a new run.
        session.ElapsedGameTime += TimeSpan.FromTicks(1);
        Native(1);
    }
    long afterMemory = GC.GetTotalMemory(true);
    var local = typeof(AccountingClock).GetField("localSun", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    int count = (int)local.GetType().GetProperty("Count")!.GetValue(local)!;
    Refuses(() => AccountingClock.SunPath("ClusterLocal", AccountingClock.NativeEpoch, 0, 1));
    Check(count <= 11350, "live cache retains its observation window plus bounded housekeeping lag, not unlimited past work");
    File.WriteAllText(args[0], JsonSerializer.Serialize(new { scope = "actual production clock source with inert native boundary, worst-case live run density; not a native or exclusive performance claim",
        updates = 60000, retainedRuns = count, observationFrames = 11250, housekeepingPeriodFrames = 100, liveManagedDeltaBytes = afterMemory - beforeMemory,
        allocatedBytes = GC.GetTotalAllocatedBytes(true) - beforeAllocated }, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"PASS {checks} exact-production clock cold checks; fake native boundary, no native-server claim");
