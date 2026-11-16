using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using VRage.Game;
using VRage.Utils;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// <summary>
    /// SE-1606: registering a string whose hash another string already has must throw, not deadlock. On .NET 10 vanilla
    /// GetOrCompute waits forever on its own lock there; on octahedron-test a script's MyDefinitionId.TryParse did that
    /// on the game thread and got three nodes fenced. DotNetCompat's MyStringHashPatch fixes it. Without that patch the
    /// section fails at once instead of trying, since the first attempt would never return and would keep the lock.
    /// </summary>
    private static void StringHashCollision()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("string-hash-collision: " + message);
        }
        Check(
            Harmony
                .GetPatchInfo(
                    AccessTools.Method(typeof(MyStringHash), nameof(MyStringHash.GetOrCompute))
                )
                ?.Prefixes.Any(patch =>
                    patch.PatchMethod.DeclaringType?.Name == "MyStringHashPatch"
                ) == true,
            "DotNetCompat with the MyStringHash fix not loaded"
        );
        // Found by a brute-force search over MyUtils.GetHash.
        const string Known = "SE1606Collision112400",
            Colliding = "SE1606Collision1029080";
        Check(
            MyUtils.GetHash(Known, 0) == MyUtils.GetHash(Colliding, 0),
            "the two strings no longer share a hash"
        );
        MyStringHash known = MyStringHash.GetOrCompute(Known);

        Exception Attempt(Action action)
        {
            Task<Exception> task = Task.Run(() =>
            {
                try
                {
                    action();
                    return null;
                }
                catch (Exception e)
                {
                    return e;
                }
            });
            Check(
                task.Wait(5000),
                "registering the colliding string did not return in 5 s (the MyStringHash deadlock)"
            );
            return task.Result;
        }

        Exception direct = Attempt(() => MyStringHash.GetOrCompute(Colliding));
        Check(
            direct is ArgumentException && direct.Message.Contains(Known),
            "GetOrCompute did not throw the collision: " + direct
        );
        // The live path: a script parsing a definition id whose subtype collides.
        Exception parsed = Attempt(() =>
            MyDefinitionId.TryParse("MyObjectBuilder_Ore/" + Colliding, out _)
        );
        Check(parsed is ArgumentException, "TryParse did not throw the collision: " + parsed);
        Check(
            Attempt(() => MyStringHash.GetOrCompute("SE1606AfterCollision")) == null,
            "the next new string could not be registered"
        );
        Check(
            known.String == Known && !MyStringHash.TryGet(Colliding, out _),
            "the tables changed"
        );
    }
}
