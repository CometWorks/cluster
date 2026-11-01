using ClusterPlugin;

namespace ClusterRegistry;

/// <summary>T-0396 (Shared/Cluster/RedeliverPolicy.cs): what a client-view adopt destroys before it re-delivers a
/// replicable the client had not finished receiving. Earned red: the adopt before this ticket destroyed nothing
/// but the redelivered replicable itself and re-delivered every pending entry - the seated-character case below
/// then destroys nothing and re-delivers the proxy on its own.</summary>
internal static partial class SelfTest
{
    private static void TestRedeliverPolicy()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("redeliver-policy: " + what); }
        // grid 1 > cockpit-seated character 2 > { proxy 3 (pending), inventory 4 }; block 5 under the grid (not in view);
        // a second grid 10 > the client's OWN character 11 > proxy 12; 20 a lone pending proxy whose parent 21 is delivered.
        Dictionary<int, int[]> tree = new()
        {
            [1] = new[] { 2, 5 }, [2] = new[] { 3, 4 }, [10] = new[] { 11 }, [11] = new[] { 12 }, [21] = new[] { 20 },
        };
        HashSet<int> view = new() { 1, 2, 3, 4, 10, 11, 12, 20, 21 };
        IEnumerable<int> Children(int id) => tree.TryGetValue(id, out int[] found) ? found : Array.Empty<int>();
        RedeliverPolicy.Plan<int> Decide(params int[] pending) => RedeliverPolicy.Decide(pending, Children, view.Contains, id => id == 11);

        // client-52 / 'Visitor Ion': the grid and a proxy under its seated character were pending, the character ready.
        RedeliverPolicy.Plan<int> plan = Decide(1, 3);
        Check(plan.Redeliver.SequenceEqual(new[] { 1 }), "a pending replicable below a re-delivered one comes back with it, not on its own");
        Check(plan.Destroy.SequenceEqual(new[] { 2, 3, 4 }), "every descendant the client holds is destroyed first, parents before children");
        Check(plan.Destroy.Count == plan.Destroy.Distinct().Count(), "each descendant is destroyed once");
        Check(!plan.Destroy.Contains(5), "a descendant the client does not hold is not destroyed");

        // The client's own character below a pending grid: kept, with its subtree.
        plan = Decide(10);
        Check(plan.Redeliver.SequenceEqual(new[] { 10 }) && plan.Destroy.Count == 0 && plan.Kept.SequenceEqual(new[] { 11 }),
            "the client's own character and what is below it are never destroyed");

        // Falsifiers: the field case of the original redeliver (a lone pending proxy under a delivered character)
        // and a pending grid with nothing below it in view change nothing.
        plan = Decide(20);
        Check(plan.Redeliver.SequenceEqual(new[] { 20 }) && plan.Destroy.Count == 0, "a pending leaf is re-delivered alone, nothing destroyed");
        view.Remove(2); view.Remove(3); view.Remove(4);
        plan = Decide(1);
        Check(plan.Redeliver.SequenceEqual(new[] { 1 }) && plan.Destroy.Count == 0, "a pending grid with no child in view destroys only itself");
        plan = Decide(1, 1);
        Check(plan.Redeliver.Count == 1, "a replicable listed twice is re-delivered once");
    }
}
