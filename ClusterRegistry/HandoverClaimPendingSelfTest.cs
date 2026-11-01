namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// T-0354b, P3-HND-02 on the T-0354 build (2026-10-03 15:13:47): the scenario spawns a cell and moves it at once. Since
    /// T-0354 the node claims a new layout's id a frame or two after making it, and the capture asked inside that window
    /// was refused ("unanswered registry apply") with a 500, which POST /handover returned to the caller. The node now
    /// answers that refusal 409 before anything is frozen, and a requested move (WhenRegistryApplied) asks again:
    /// (1) clientless, the capture refused twice: the move commits on the third attempt, each refused attempt thawed and
    ///     aborted; control: the plain handover fails with the refusal and leaves the partition on its source.
    /// (2) refused for longer than the waits: the move fails as RegistryBusyException (503) after 6 captures.
    /// (3) a capture refused with a 500 is not asked again.
    /// (4) client-bearing, the id allocated to the source and not claimed yet: the gateway must not claim it for the
    ///     node (the node's own claim, expecting generation 0, would then be refused - which it reads as being closed);
    ///     the move waits until the node's claim lands, then commits. Control: the row the gateway used to make.
    /// </summary>
    private static void TestHandoverClaimPendingRetry()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("handover-claim-pending-retry: " + what); }
        static Task Fast(TimeSpan _) => Task.CompletedTask;
        static string Report(ulong id) => $$"""{"syncDistance":3000,"margin":100,"partitions":[{"id":{{id}},"memberCount":1,"anchored":false,"sphere":{"x":0,"y":0,"z":0,"radius":10},"maxMemberSpeed":0,"clients":[]}],"evaluatedAt":0}""";
        ulong Claimed(ScriptedHandover scripted)
        {
            NodeRegistration source = scripted.Registry.Nodes().Single(x => x.Node == "source");
            ulong id = scripted.Registry.AllocatePartitionIds(source.Node, source.Epoch, 1)[0];
            scripted.Registry.ClaimPartition(id, source.Node, source.Epoch, 0);
            scripted.SourcePartitions = Report(id);
            return id;
        }
        string Owner(ScriptedHandover scripted, ulong id) => scripted.Registry.GetPartitionOwner(id).Node ?? "none";

        // (1) and its control.
        foreach (bool control in new[] { false, true })
        {
            using var scripted = new ScriptedHandover((call, nth) => call == "source capture" && nth <= 2 ? 409 : 200);
            ulong id = Claimed(scripted);
            Exception? failure = scripted.Clientless(id, control ? null : Fast);
            Console.WriteLine($"handover-claim-pending-retry: clientless{(control ? " control (no retry)" : "")}: failure={failure?.GetType().Name ?? "none"}"
                + $" owner={Owner(scripted, id)} calls=[{string.Join(", ", scripted.Calls)}]");
            if (control)
            {
                Check(failure is RegistryApplyPendingRefusal && Owner(scripted, id) == "source" && scripted.Count("source capture") == 1,
                    "control: a capture refused 409 must surface as the refusal and leave the partition on its source: " + failure?.Message);
                Check(scripted.Registry.Incomplete().Length == 0, "control: the refused handover left a record");
                continue;
            }
            Check(failure == null, "the requested move did not ride out two refused captures: " + failure?.Message);
            Check(Owner(scripted, id) == "target" && scripted.Count("source capture") == 3 && scripted.Count("target commit") == 1,
                "the move must commit on the third capture");
            Check(scripted.Count("source thaw") == 2 && scripted.Registry.Incomplete().Length == 0, "each refused attempt must be thawed and aborted");
        }

        // (2) refused for good, (3) a 500.
        foreach (int status in new[] { 409, 500 })
        {
            using var scripted = new ScriptedHandover((call, _) => call == "source capture" ? status : 200);
            ulong id = Claimed(scripted);
            Exception? failure = scripted.Clientless(id, Fast);
            Console.WriteLine($"handover-claim-pending-retry: capture always {status}: failure={failure?.GetType().Name ?? "none"} captures={scripted.Count("source capture")}");
            if (status == 409)
                Check(failure is RegistryBusyException && scripted.Count("source capture") == 1 + DirectGateway.RegistryApplyRetryDelays.Length,
                    "a refusal that does not resolve must end as busy (503) after the bounded retries: " + failure?.GetType().Name);
            else
                Check(failure is HttpRequestException && scripted.Count("source capture") == 1, "a 500 capture must not be asked again: " + failure?.GetType().Name);
            Check(Owner(scripted, id) == "source" && scripted.Registry.Incomplete().Length == 0, "the failed move must leave the partition on its source, no record");
        }

        // (4) client-bearing, the claim not at the registry yet.
        using (var scripted = new ScriptedHandover((_, _) => 200))
        {
            NodeRegistration source = scripted.Registry.Nodes().Single(x => x.Node == "source");
            ulong id = scripted.Registry.AllocatePartitionIds(source.Node, source.Epoch, 1)[0];
            int waits = 0;
            bool rowBeforeClaim = true;
            Exception? failure = scripted.Handover(id, _ =>
            {
                // The first wait is where the node's claim lands; until then the gateway must not have made the row.
                if (++waits == 1)
                {
                    rowBeforeClaim = !scripted.Registry.IsAllocatedUnclaimed(id);
                    scripted.Registry.ClaimPartition(id, source.Node, source.Epoch, 0);
                }
                return Task.CompletedTask;
            });
            Console.WriteLine($"handover-claim-pending-retry: client-bearing, claim under way: failure={failure?.GetType().Name ?? "none"} waits={waits}"
                + $" gatewayMadeRow={rowBeforeClaim} owner={Owner(scripted, id)}");
            Check(!rowBeforeClaim, "the gateway claimed the node's new id for it (the node's own claim would be refused)");
            Check(failure == null && waits == 1 && Owner(scripted, id) == "target", "the move must commit once the node's claim landed: " + failure?.Message);
            // Control: the row the gateway used to make for the source turns the node's claim (expecting generation 0) away.
            ulong other = scripted.Registry.AllocatePartitionIds(source.Node, source.Epoch, 1)[0];
            scripted.Registry.ClaimPartition(other, source.Node, source.Epoch, 0);
            bool refused = false;
            try { scripted.Registry.ClaimPartition(other, source.Node, source.Epoch, 0); }
            catch (RegistryConflictException) { refused = true; }
            Check(refused, "control: a claim expecting generation 0 over a row already made must be refused");
        }
    }
}
