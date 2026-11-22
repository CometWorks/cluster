// SE-1870: the World Authority broadcasts only the player profiles that changed, and cameras of entities
// nobody reports any more expire instead of piling up to the cap.
using System.Text.Json;
using ClusterPlugin;

const long Week = 604800;
const int MaxCameras = 32;
int failures = 0;
long now = 1_800_000_000;

PlayerCameraSetting Camera(long entity, long seen = 0) => new(entity, 3, 0, 0, false, seen);

PlayerProfile Profile(ulong steam, int toolbarBytes = 2400, params PlayerCameraSetting[] cameras) =>
    new(
        steam,
        0,
        (long)steam,
        "<Toolbar>" + new string('x', toolbarBytes) + "</Toolbar>",
        Enumerable.Range(0, PlayerProfileCodec.ColorSlotCount * 3).Select(i => i / 42f).ToArray(),
        0,
        "",
        false,
        0,
        cameras
    );

var record = new Dictionary<(ulong SteamId, int SerialId), PlayerProfile>();
var published = new GlobalStateDelta<PlayerProfileKey, PlayerProfile>();
var node = new Dictionary<(ulong SteamId, int SerialId), PlayerProfile>();

string Fold(params PlayerProfile[][] reports) =>
    PlayerProfileCodec.TakeDelta(
        record,
        PlayerProfileCodec.Merge(record, reports, now, Week, MaxCameras),
        published
    );

// A live-sized record: 227 players, 2.4 KB toolbars, 19 cameras each.
var everyone = Enumerable
    .Range(1, 227)
    .Select(i =>
        Profile(
            (ulong)i,
            cameras: Enumerable.Range(1, 19).Select(c => Camera(i * 1000 + c)).ToArray()
        )
    )
    .ToArray();
string first = Fold(everyone);
PlayerProfileCodec.Apply(
    node,
    PlayerProfileCodec.ReadSnapshot(PlayerProfileCodec.WriteSnapshot(record.Values))
);
string full = PlayerProfileCodec.WriteSnapshot(record.Values);
Check(first != null, "the first report of every player is a change");

// One player of 227 edits a toolbar slot.
now += 5;
PlayerProfile edited = everyone[100] with { ToolbarXml = "<Toolbar>edited</Toolbar>" };
string delta = Fold([edited]);
Check(delta != null, "a toolbar edit is broadcast");
PlayerProfileSnapshot parsed = PlayerProfileCodec.ReadSnapshot(delta);
Check(
    !parsed.IsFull && parsed.Changed.Length == 1,
    "the broadcast carries only the changed profile"
);
Console.WriteLine($"full snapshot {full.Length} B, one-player delta {delta.Length} B");
Check(delta.Length * 50 < full.Length, "the delta is a small fraction of the full record");
PlayerProfileCodec.Apply(node, parsed);
Check(SameAs(node, record), "a node that applies the delta holds the full record");
Check(node.Count == 227, "the other 226 profiles stay on the node");

// The same report again, a second later: nothing changed, nothing is broadcast.
now += 1;
Check(Fold([edited]) == null, "an unchanged report broadcasts nothing");

// ...also later, while the camera stamps are within their refresh interval.
now += 600;
Check(Fold([edited]) == null, "re-seen cameras inside the refresh interval broadcast nothing");

// A node that runs the code from before SE-1870 rejects a delta and keeps its record.
var old = JsonSerializer.Deserialize<OldSnapshot>(delta);
Check(
    old.Profiles == null,
    "the pre-delta reader sees no profiles in a delta (it throws and keeps its record)"
);

// A World Authority from before SE-1870 broadcasts full snapshots; a new node takes them whole.
PlayerProfileSnapshot fromOld = PlayerProfileCodec.ReadSnapshot(
    JsonSerializer.Serialize(new OldSnapshot(everyone))
);
Check(
    fromOld.IsFull && fromOld.Profiles.Length == 227,
    "a full snapshot from an old World Authority is accepted"
);

// Cameras: a player respawns 300 times; every respawn is a new character entity, the old one is gone.
var record2 = new Dictionary<(ulong SteamId, int SerialId), PlayerProfile>();
long t = now;
for (int life = 1; life <= 300; life++)
{
    t += 600;
    PlayerProfileCodec.Merge(
        record2,
        [
            [Profile(7, cameras: [Camera(5000 + life)])],
        ],
        t,
        Week,
        MaxCameras
    );
}
PlayerCameraSetting[] kept = record2[(7, 0)].Cameras;
Check(kept.Length == MaxCameras, $"cameras are capped at {MaxCameras} (got {kept.Length})");
Check(
    kept.Any(c => c.EntityId == 5300),
    "the newest camera survives the cap (the old cap kept the lowest ids)"
);
Check(
    kept.All(c => c.EntityId > 5300 - MaxCameras),
    "the cap keeps the most recently seen cameras"
);

// Age: an offline player's cameras expire once no node reported them for the retention period.
var record3 = new Dictionary<(ulong SteamId, int SerialId), PlayerProfile>();
var published3 = new GlobalStateDelta<PlayerProfileKey, PlayerProfile>();
PlayerProfileCodec.TakeDelta(
    record3,
    PlayerProfileCodec.Merge(
        record3,
        [
            [Profile(8, cameras: [Camera(1), Camera(2)])],
        ],
        now,
        60,
        MaxCameras
    ),
    published3
);

// Entity 1 still exists on a node and is reported; entity 2 is gone.
PlayerProfileCodec.TakeDelta(
    record3,
    PlayerProfileCodec.Merge(
        record3,
        [
            [Profile(8, cameras: [Camera(1)])],
        ],
        now + 50,
        60,
        MaxCameras
    ),
    published3
);
string expiry = PlayerProfileCodec.TakeDelta(
    record3,
    PlayerProfileCodec.Merge(record3, [], now + 70, 60, MaxCameras),
    published3
);
Check(expiry != null, "an expiry is broadcast even with nobody reporting");
Check(
    record3[(8, 0)].Cameras.Select(c => c.EntityId).SequenceEqual([1L]),
    "the unreported camera expired, the reported one stays"
);
Check(
    PlayerProfileCodec.TakeDelta(
        record3,
        PlayerProfileCodec.Merge(record3, [], now + 71, 60, MaxCameras),
        published3
    ) == null,
    "an expiry is broadcast once"
);

// Unknown age (0) never expires by age.
var record4 = new Dictionary<(ulong SteamId, int SerialId), PlayerProfile>
{
    [(9, 0)] = Profile(9, cameras: [Camera(3, 0)]),
};
PlayerProfileCodec.Merge(record4, [], now + 10 * Week, Week, MaxCameras);
Check(record4[(9, 0)].Cameras.Length == 1, "a camera of unknown age is kept");

Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
return failures == 0 ? 0 : 1;

bool SameAs(
    Dictionary<(ulong SteamId, int SerialId), PlayerProfile> a,
    Dictionary<(ulong SteamId, int SerialId), PlayerProfile> b
) =>
    a.Count == b.Count
    && a.All(entry =>
        b.TryGetValue(entry.Key, out PlayerProfile other)
        && JsonSerializer.Serialize(entry.Value) == JsonSerializer.Serialize(other)
    );

void Check(bool condition, string what)
{
    Console.WriteLine((condition ? "ok   " : "FAIL ") + what);
    if (!condition)
        failures++;
}

/// <summary>The snapshot shape every node read before SE-1870.</summary>
sealed record OldSnapshot(PlayerProfile[] Profiles);
