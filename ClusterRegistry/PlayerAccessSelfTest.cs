using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    private static void TestPlayerAccess()
    {
        var directory = Directory.CreateTempSubdirectory("player-access-").FullName;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var policy = new Admin.PlayerAccessPolicy(
                1,
                "cluster:test",
                1,
                now,
                now.AddMinutes(2),
                true,
                true,
                [
                    new(7, true, true, true, 4, now.AddMinutes(1)),
                    new(8, false, true, false, 0, now.AddMinutes(1)),
                    new(9, false, false, false, 0, now.AddMinutes(1)),
                ]
            );
            var book = new AdmissionBook(
                new AdmissionSettings(MemberLimit: 2, Reserved: [10], Banned: [11]),
                directory
            );
            book.PlayerAccess.Apply(policy, "cluster:test");
            Assert(book.Reject(7, 7, 2, false, false) == null); // Role priority bypasses the full-server cap.
            Assert(book.Reject(8, 8, 0, false, false) == JoinResult.NotInGroup);
            Assert(book.Reject(9, 9, 0, false, false) == JoinResult.NotInGroup);
            Assert(book.Reject(10, 10, 0, false, false) == JoinResult.NotInGroup); // Native reserved is not whitelist membership.
            Assert(book.Reject(11, 11, 0, false, false) == JoinResult.BannedByAdmins);
            book.Ban(7, true);
            Assert(book.Reject(7, 7, 0, false, false) == JoinResult.BannedByAdmins);
            book.Ban(7, false);
            Assert(
                !book.PlayerAccess.CheckAdmission(7, now.AddMinutes(1), out bool expiredPriority)
                    && !expiredPriority
            );
            Assert(book.PlayerAccess.ConfirmedDenial(8, now));
            Assert(!book.PlayerAccess.ConfirmedDenial(9, now)); // An outage cannot eject existing sessions.
            Assert(!book.PlayerAccess.ConfirmedDenial(8, now.AddMinutes(2)));
            var restored = new PlayerAccessBook(directory);
            Assert(restored.Allows(7, now) && !restored.Allows(10, now));
            Assert(!restored.Allows(7, now.AddMinutes(2))); // An expired policy never reopens the whitelist after restart.
            File.Delete(Path.Combine(directory, "player-access.json"));
            var missing = new PlayerAccessBook(directory, "cluster:test");
            Assert(!missing.Allows(7, now)); // Missing decisions cannot erase whitelist intent.
            AssertArgument(() =>
                missing.Apply(policy with { Revision = 0, Enabled = false }, "cluster:test")
            );
            AssertThrows(() => new PlayerAccessBook(directory, "cluster:other"));
            AssertArgument(() =>
                book.PlayerAccess.Apply(policy with { Target = "cluster:other" }, "cluster:test")
            );
            AssertArgument(() =>
                book.PlayerAccess.Apply(policy with { Revision = 0 }, "cluster:test")
            );
            AssertArgument(() =>
                book.PlayerAccess.Apply(
                    policy with
                    {
                        Players = [new(7, true, true, true, 5, now)],
                    },
                    "cluster:test"
                )
            );
            book.PlayerAccess.Apply(policy with { Revision = 2, Enabled = false }, "cluster:test");
            Assert(
                book.PlayerAccess.CheckAdmission(7, now.AddDays(1), out bool disabledPriority)
                    && !disabledPriority
            );
            Assert(book.Reject(7, 7, 2, false, false) == JoinResult.ServerFull);
            Assert(book.Reject(10, 10, 2, false, false) == null); // Manual reserved membership is preserved.

            // A fresh/restored Registry must close before its first control-plane heartbeat arrives.
            Directory.Delete(directory, true);
            var cold = new PlayerAccessBook(directory, "cluster:test", new(20, true));
            Assert(!cold.Allows(7, now));
            Assert(!new PlayerAccessBook(directory, "cluster:test").Allows(7, now));
            AssertArgument(() =>
                cold.Apply(policy with { Revision = 19, Enabled = false }, "cluster:test")
            );
            cold.Apply(policy with { Revision = 20 }, "cluster:test");
            Assert(cold.Allows(7, now));
            var restoredOlder = new PlayerAccessBook(directory, "cluster:test", new(21, true));
            Assert(!restoredOlder.Allows(7, now));
            var disableLaunch = new PlayerAccessBook(directory, "cluster:test", new(22, false));
            Assert(!disableLaunch.Allows(7, now)); // Launch intent alone never removes persisted restrictions.
            disableLaunch.Apply(
                policy with
                {
                    Revision = 22,
                    Enabled = false,
                    Whitelist = false,
                },
                "cluster:test"
            );
            Assert(disableLaunch.Allows(7, now));
            var staleLaunch = new PlayerAccessBook(directory, "cluster:test", new(21, true));
            Assert(staleLaunch.Allows(7, now)); // Old launch metadata cannot undo a fresh explicit disable.

            // If the whitelist marker commits but the policy write fails, the live Gateway must close too.
            var decisionPath = Path.Combine(directory, "player-access.json");
            File.Delete(decisionPath);
            Directory.CreateDirectory(decisionPath);
            try
            {
                staleLaunch.Apply(policy with { Revision = 23 }, "cluster:test");
                throw new InvalidOperationException(
                    "self-test failed: expected policy write failure"
                );
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Assert(!staleLaunch.Allows(7, now));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
