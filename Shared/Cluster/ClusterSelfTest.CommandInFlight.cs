using System;
using System.Threading;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// <summary>
    /// SE-1561: a World Authority command whose handler waits for the game thread runs on the node-link thread while a
    /// queued copy of the same operation (the node's earlier try) is handled as the queue drain does. It must not wait for the running
    /// handler: on the live cluster the game thread did, the handler waited for the game thread, and the World Authority
    /// hung for ten minutes. The copy stays queued and the ledger answers it once the handler is done, which runs once.
    /// </summary>
    private static void CommandInFlight()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("command-in-flight: " + message);
        }
        if (!ClusterRuntime.IsWorldAuthority)
            return;
        const string Subsystem = "self-test-command-in-flight";
        int runs = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        GlobalStateChannel.RegisterCommandHandler(
            Subsystem,
            (_, _) =>
            {
                Interlocked.Increment(ref runs);
                entered.Set();
                // Stands for the game thread running the handler's action: three seconds here, forever on the live cluster.
                if (!release.Wait(3000))
                    throw new TimeoutException("the handler waited for its thread in vain");
                return "done";
            }
        );
        (long waitedMs, bool handledWhileRunning, bool handledAfter) =
            GlobalStateChannel.SelfTestQueuedCopyOfRunningCommand(Subsystem, entered, release);
        Check(
            waitedMs < 1000,
            "the queued copy waited " + waitedMs + " ms for the command running on another thread"
        );
        Check(!handledWhileRunning, "the copy of a running command stays queued");
        Check(handledAfter, "the copy is answered from the ledger once the command is done");
        Check(Volatile.Read(ref runs) == 1, "the handler ran " + runs + " times");
    }
}
