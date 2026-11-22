// SE-1870: GlobalStateDelta, the generic "broadcast only what changed" helper for keyed global state.
using ClusterPlugin;

int failures = 0;
var authority = new Dictionary<Key, Entry>();
var replica = new Dictionary<Key, Entry>();
var delta = new GlobalStateDelta<Key, Entry>();

string Take(params Key[] touched) => delta.Take(touched, key => authority.GetValueOrDefault(key));

void Deliver(string payload) =>
    GlobalStateDelta<Key, Entry>.Apply(
        replica,
        GlobalStateDelta<Key, Entry>.Read(payload, 4096),
        entry => entry.Key
    );

for (int i = 0; i < 100; i++)
    authority[new Key(i, "a")] = new Entry(new Key(i, "a"), new string('x', 1000), [i]);

// The replica starts from a resync: the full record.
foreach ((Key key, Entry entry) in authority)
    replica[key] = entry;
Check(
    Take(authority.Keys.ToArray()) != null,
    "the first Take broadcasts every entry it has not broadcast yet"
);

Check(Take(new Key(5, "a")) == null, "an unchanged entry broadcasts nothing");

authority[new Key(5, "a")] = authority[new Key(5, "a")] with { Values = [5, 6] };
string one = Take(new Key(5, "a"), new Key(6, "a"));
Check(
    one != null && GlobalStateDelta<Key, Entry>.Read(one, 4096).Changed.Length == 1,
    "only the changed entry is in the delta"
);
Check(one.Length < 1200, $"a one-entry delta is about one entry big ({one?.Length} B)");
Deliver(one);
Check(replica[new Key(5, "a")].Values.SequenceEqual([5, 6]), "the replica applies the change");

authority.Remove(new Key(7, "a"));
string gone = Take(new Key(7, "a"));
Deliver(gone);
Check(
    !replica.ContainsKey(new Key(7, "a")) && replica.Count == 99,
    "a removed key leaves the replica, the rest stay"
);
Check(Take(new Key(7, "a")) == null, "a removal is broadcast once");

authority[new Key(200, "b")] = new Entry(new Key(200, "b"), "new", []);
Deliver(Take(new Key(200, "b")));
Check(replica.ContainsKey(new Key(200, "b")), "an added key reaches the replica");
Check(
    replica.Count == authority.Count
        && replica.All(p => authority[p.Key] == p.Value || authority[p.Key].Text == p.Value.Text),
    "the replica matches the authority"
);

delta.Reset();
Check(Take(new Key(1, "a")) != null, "after Reset everything counts as unbroadcast");

foreach (string bad in new[] { "{}", "{\"Profiles\":[]}", "null" })
{
    bool threw = false;
    try
    {
        GlobalStateDelta<Key, Entry>.Read(bad, 4096);
    }
    catch (Exception)
    {
        threw = true;
    }
    Check(threw, $"Read rejects {bad} (the channel then resyncs)");
}

Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
return failures == 0 ? 0 : 1;

void Check(bool condition, string what)
{
    Console.WriteLine((condition ? "ok   " : "FAIL ") + what);
    if (!condition)
        failures++;
}

sealed record Key(int Id, string Kind);

sealed record Entry(Key Key, string Text, int[] Values);
