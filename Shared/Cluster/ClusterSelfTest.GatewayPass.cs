using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// F40 (#1285): <see cref="CoalescedRequest"/>, the Gateway pass request's one-worker coalescing. (1) An ask between
	/// the worker leaving its send loop and resetting <c>posting</c> (the <see cref="CoalescedRequest.AfterDrain"/> hook)
	/// is sent: two asks, two sends; before the fix one. (2) Asks during a send coalesce into one more send. (3) An ask
	/// during a failed send is still sent, and a later ask starts a new worker. (4) 8 threads x 20k asks on the thread
	/// pool: never two sends at once, and every ask is followed by a send that started after it.
	/// </summary>
	private static void GatewayPassCoalesce()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("gateway-pass-coalesce: " + message); }
		void Inline(Action work) => work();

		// (1) the F40 interleaving, forced.
		int sends = 0;
		CoalescedRequest request = new CoalescedRequest(() => sends++, exception => throw exception, Inline);
		bool hooked = false, startedInWindow = true;
		request.AfterDrain = () =>
		{
			if (hooked) return;
			hooked = true;
			startedInWindow = request.Request();
		};
		Check(request.Request(), "the first ask starts the worker");
		Check(hooked && !startedInWindow, "the ask in the window finds the worker still posting");
		Check(sends == 2, $"an ask between the send loop's exit and the posting reset is sent ({sends} sends for 2 asks)");
		Check(request.Request() && sends == 3, "the worker is free again afterwards");

		// (2) asks during a send coalesce into one more send.
		sends = 0;
		request = new CoalescedRequest(() =>
		{
			if (++sends == 1) Check(!request.Request() && !request.Request(), "asks during a send do not start a second worker");
		}, exception => throw exception, Inline);
		request.Request();
		Check(sends == 2, $"two asks during a send coalesce into one more send ({sends} sends)");

		// (3) a failed send: the ask made during it is still sent, then a new ask starts a new worker.
		sends = 0;
		int failures = 0;
		request = new CoalescedRequest(() =>
		{
			if (++sends == 1)
			{
				request.Request();
				throw new InvalidOperationException("refused");
			}
		}, exception => failures++, Inline);
		Check(request.Request(), "the first ask starts the worker");
		Check(failures == 1 && sends == 2, $"an ask during a failed send is sent ({sends} sends, {failures} failures)");
		Check(request.Request() && sends == 3, "a new ask after a failure starts a new worker");

		// (4) many threads, the thread pool.
		const int Threads = 8, PerThread = 20_000;
		long asked = 0, served = 0;
		int inSend = 0, overlaps = 0;
		request = new CoalescedRequest(() =>
		{
			if (Interlocked.Increment(ref inSend) > 1) Interlocked.Increment(ref overlaps);
			long seen = Interlocked.Read(ref asked);
			if (seen > Interlocked.Read(ref served)) Interlocked.Exchange(ref served, seen);
			Interlocked.Decrement(ref inSend);
		}, exception => Interlocked.Increment(ref failures));
		failures = 0;
		using (Barrier start = new Barrier(Threads))
		{
			Thread[] threads = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
			{
				start.SignalAndWait();
				for (int i = 0; i < PerThread; i++)
				{
					Interlocked.Increment(ref asked);
					request.Request();
				}
			}) { IsBackground = true }).ToArray();
			foreach (Thread thread in threads) thread.Start();
			foreach (Thread thread in threads) thread.Join();
		}
		Stopwatch waited = Stopwatch.StartNew();
		while (Interlocked.Read(ref served) < Threads * PerThread && waited.ElapsedMilliseconds < 10_000) Thread.Sleep(5);
		Check(overlaps == 0, overlaps + " sends overlapped another");
		Check(Volatile.Read(ref failures) == 0, "no send failed");
		Check(Interlocked.Read(ref served) == Threads * PerThread,
			$"the last ask was sent within 10 s (a send saw {Interlocked.Read(ref served)} of {Threads * PerThread} asks)");
	}
}
