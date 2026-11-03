using System.Text.Json;
using ClusterPlugin;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Refused(Action action)
{
    try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException) { return; }
    throw new Exception("Expected refusal");
}
static PromotionService.PromotionEntry[] Entries() => JsonSerializer.Deserialize<PromotionService.PromotionSnapshot>(GlobalStateChannel.Query("$gateway", "{}"))!.Entries;
static string Set(ulong user, int level, long revision) => GlobalStateChannel.Set("$gateway", JsonSerializer.Serialize(new PromotionService.PromotionChange(user, level, revision)));
PromotionService.Install();
Check(GlobalStateChannel.Journaled, "Admin changes must use the journaled command lane");
// MySession.Ready can precede checkpoint promotion loading. Admin commands must
// preserve the normal seed grace instead of establishing an incomplete map.
Refused(() => Set(9, 2, 0));
Check(!PromotionService.Ready && MySessionSeams.Broadcasts == 0,
    "An early role command cannot bypass checkpoint promotion seeding");
MySession.Static.PromotedUsers[7] = MyPromoteLevel.Admin;
PromotionService.Update();
Check(Entries().Single() is { SteamId: 7, Level: 4, Revision: 1 }, "Seed existing checkpoint roles");
Refused(() => GlobalStateChannel.Set("node-a", "{\"SteamId\":7,\"Level\":4}"));
Refused(() => GlobalStateChannel.Query("node-a", "{}"));
foreach (int level in new[] { -1, 5, 99 }) Refused(() => Set(7, level, 1));
Refused(() => Set(0, 4, 1));
Refused(() => Set(7, 4, -1));
foreach (int level in Enumerable.Range(0, 5))
{
    long revision = Entries().Single().Revision;
    var applied = JsonSerializer.Deserialize<PromotionService.PromotionEntry>(Set(7, level, revision))!;
    Check(applied.Level == level && applied.Revision == revision + 1, "Exact role and revision acknowledged");
    Check((int)MySession.Static.PromotedUsers.GetValueOrDefault(7UL) == level, "Game-thread effect before acknowledgement");
}
long current = Entries().Single().Revision;
Refused(() => Set(7, 0, current - 1));
Check((int)MySession.Static.PromotedUsers[7] == 4, "Stale request cannot override a later promotion");
Set(7, 0, current);
long demotionRevision = Entries().Single().Revision;
GlobalStateChannel.Report("old-node", JsonSerializer.Serialize(new PromotionService.PromotionReport("old-node", [new(7, 4)], Seed: true)));
PromotionService.Update();
Check(Entries().Single().Level == 0 && !MySession.Static.PromotedUsers.ContainsKey(7), "Stale node seed cannot resurrect a demotion");
MySession.Static.PromotedUsers[7] = MyPromoteLevel.Admin; // Vanilla reconnect/transfer checkpoint.
PromotionService.Update();
Check(!MySession.Static.PromotedUsers.ContainsKey(7), "Reconnect is reconciled even when the snapshot is unchanged");
int broadcasts = MySessionSeams.Broadcasts;
PromotionService.Update();
Check(MySessionSeams.Broadcasts == broadcasts, "Unchanged roles cause no broadcast or feedback loop");
string saved = PromotionService.PersistedRecord();
byte[] file = GlobalSaveFile.Write(new(), 1, 2, 0, 0, "{\"Entries\":[]}", promotionRecord: saved);
var bundle = GlobalSaveFile.ReadBundle(file);
Check(bundle.PromotionRecord == saved && bundle.OfflineRatesRecord == null, "New section round-trip including empty preceding section");
Check(GlobalSaveFile.ReadBundle(GlobalSaveFile.Write(new(), 1, 1, 0, 0, "{\"Entries\":[]}")).PromotionRecord == null, "Older saves remain readable");
Set(7, 4, demotionRevision);
PromotionService.RestoreRecord(bundle.PromotionRecord);
PromotionService.Update();
Check(Entries().Single() is { Level: 0 } && Entries().Single().Revision == demotionRevision, "Saved None tombstone and revision restored");
GlobalStateChannel.Report("stale-node", JsonSerializer.Serialize(new PromotionService.PromotionReport("stale-node", [new(7, 4)], Seed: true)));
Check(Entries().Single().Level == 0, "Restored demotion survives reconnect seeds");
GlobalCommandLedger.Restore(null);
Guid id = Guid.NewGuid();
string payload = JsonSerializer.Serialize(new PromotionService.PromotionChange(7, 3, demotionRevision));
GlobalCommandResult Run() => new(true, GlobalStateChannel.Set("$gateway", payload), "");
var result = GlobalCommandLedger.Execute("promotion-set", id, payload, Run, out long order);
Check(result.Success && Entries().Single().Level == 3, "Ledger executes actual owner");
Set(7, 0, Entries().Single().Revision);
Check(GlobalCommandLedger.Execute("promotion-set", id, payload, () => throw new Exception("duplicate effect")) == result, "Applied retry replays without undoing later demotion");
Check(!GlobalCommandLedger.Execute("promotion-set", id, payload + " ", Run).Success, "Changed payload conflicts");
// A WA replacement restores the earlier save, then replays the command journal after it.
PromotionService.RestoreRecord(saved);
var replay = Run();
GlobalCommandLedger.Restore(null);
GlobalCommandLedger.Replay("promotion-set", id, payload, order, replay);
Check(Entries().Single().Level == 3 && GlobalCommandLedger.Execute("promotion-set", id, payload, () => throw new Exception("replayed twice")) == replay,
    "Journal replay restores role and ledger on a replacement WA");
Refused(() => PromotionService.RestoreRecord("{\"Entries\":[{\"SteamId\":7,\"Level\":5}]}"));
Refused(() => PromotionService.RestoreRecord("{\"Entries\":[{\"SteamId\":7,\"Level\":0},{\"SteamId\":7,\"Level\":4}]}"));
ClusterRuntime.IsAuthorityFenced = true;
Refused(() => Set(7, 4, Entries().Single().Revision));
Console.WriteLine("Promotion owner, game application, revisions, reconnect, snapshot codec and ledger replay checks passed.");
