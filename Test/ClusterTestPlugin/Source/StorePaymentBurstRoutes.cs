using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1883 bench routes: store payments the way a trader's dump runs them on a node's game thread. For `ms`, every frame
/// spends up to <see cref="FrameMs"/> on payments of 1 SC from `payer` to `payee`, each a full store payment scope: the
/// journaled reserve, the debit and credit the vanilla trade makes, the commit and its background credit. The route
/// records each payment's game-thread time and how much of it went to the payment journal's appends.
/// </summary>
internal static class StorePaymentBurstRoutes
{
    private const long FrameMs = 250;
    private static readonly Stopwatch Watch = new();
    private static readonly List<double> Payments = new();
    private static long m_payer,
        m_payee,
        m_burstMs,
        m_limit,
        m_journalCount,
        m_journalTicks;
    private static int m_refused,
        m_failed;
    private static object m_result;

    public static void Register()
    {
        // POST /test-store-payment-burst?payerId=&payeeId=&ms= - from the next frame on, store payments for `ms`.
        TestRoutes.Add(
            "POST",
            "/test-store-payment-burst",
            context =>
            {
                long payer = TestRoutes.Long(context, "payerId");
                long payee = TestRoutes.Long(context, "payeeId");
                long ms = TestRoutes.Long(context, "ms");
                // SE-1525: a cap, since a trade that no longer waits on the WA fits hundreds in a frame.
                long count = long.TryParse(context.Request.QueryString["count"], out long parsed)
                    ? parsed
                    : long.MaxValue;
                if (payer <= 0 || payee <= 0 || ms is <= 0 or > 60000)
                    throw new ArgumentOutOfRangeException("payerId, payeeId or ms");
                Volatile.Write(ref m_result, null);
                Interlocked.Exchange(ref m_payer, payer);
                Interlocked.Exchange(ref m_payee, payee);
                Interlocked.Exchange(ref m_limit, count);
                Interlocked.Exchange(ref m_burstMs, ms);
                Plugin.Log(
                    $"store-payment-burst armed payer={payer} payee={payee} ms={ms} (SE-1883)"
                );
                TestRoutes.Json(
                    context,
                    200,
                    new
                    {
                        payer,
                        payee,
                        ms,
                    }
                );
            }
        );
        // POST /test-store-settle-hold?ms=&afterReserveMs= - SE-1525: the settle worker waits `ms` before its next
        // payment and `afterReserveMs` between a reserve and its credit, so a lane can race or kill around them.
        TestRoutes.Add(
            "POST",
            "/test-store-settle-hold",
            context =>
            {
                long ms = long.TryParse(context.Request.QueryString["ms"], out long hold)
                    ? hold
                    : 0;
                int after = int.TryParse(
                    context.Request.QueryString["afterReserveMs"],
                    out int pause
                )
                    ? pause
                    : 0;
                Interlocked.Exchange(
                    ref StorePaymentJournal.TestHoldUntil,
                    ms > 0 ? Environment.TickCount64 + ms : 0
                );
                StorePaymentJournal.TestHoldAfterReserveMs = Math.Clamp(after, 0, 120000);
                Plugin.Log($"store-settle-hold ms={ms} afterReserveMs={after} (SE-1525)");
                TestRoutes.Json(context, 200, new { ms, afterReserveMs = after });
            }
        );
        // GET /test-store-holds?payerId= - SE-1525: what this node holds of the payer's balance for unsettled trades.
        TestRoutes.Add(
            "GET",
            "/test-store-holds",
            context =>
            {
                long payer = TestRoutes.Long(context, "payerId");
                TestRoutes.Json(
                    context,
                    200,
                    new
                    {
                        payer,
                        held = InvokeOnGame(() => StorePaymentHolds.HeldOf(payer)),
                        appliedVersion = MyBankingSystemPatch.AppliedVersion,
                    }
                );
            }
        );
        // GET /test-store-debt?ownerId= - SE-1525, on the WA: what the owner owes for clawed-back store payments.
        TestRoutes.Add(
            "GET",
            "/test-store-debt",
            context =>
            {
                long owner = TestRoutes.Long(context, "ownerId");
                TestRoutes.Json(
                    context,
                    200,
                    new { owner, debt = InvokeOnGame(() => StoreDebt.Owed(owner)) }
                );
            }
        );
        // GET /test-store-payment-burst - the last burst's outcome; done=false while it runs.
        TestRoutes.Add(
            "GET",
            "/test-store-payment-burst",
            context =>
                TestRoutes.Json(context, 200, Volatile.Read(ref m_result) ?? new { done = false })
        );
    }

    public static void Update()
    {
        long burstMs = Interlocked.Read(ref m_burstMs);
        if (burstMs <= 0)
            return;
        if (!Watch.IsRunning)
        {
            Payments.Clear();
            m_refused = m_failed = 0;
            m_journalCount = Interlocked.Read(ref StorePaymentJournal.AppendCount);
            m_journalTicks = Interlocked.Read(ref StorePaymentJournal.AppendTicks);
            Interlocked.Exchange(ref StorePaymentJournal.AppendMaxTicks, 0);
            Watch.Restart();
        }
        long payer = Interlocked.Read(ref m_payer);
        long payee = Interlocked.Read(ref m_payee);
        long frameEnd = Math.Min(Watch.ElapsedMilliseconds + FrameMs, burstMs);
        while (
            Watch.ElapsedMilliseconds < frameEnd && Payments.Count < Interlocked.Read(ref m_limit)
        )
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                using MyStoreBlockPatch.StorePaymentScope payment = MyStoreBlockPatch.BeginPayment(
                    payer,
                    1
                );
                if (!payment.Approved)
                    m_refused++;
                else
                {
                    bool observed = false;
                    MyStoreBlockPatch.TryObserveBalanceChange(payer, -1, ref observed);
                    MyStoreBlockPatch.TryObserveBalanceChange(payee, 1, ref observed);
                    payment.Commit();
                }
            }
            catch (Exception)
            {
                m_failed++;
            }
            Payments.Add(Ms(Stopwatch.GetTimestamp() - started));
        }
        if (Watch.ElapsedMilliseconds < burstMs && Payments.Count < Interlocked.Read(ref m_limit))
            return;
        Watch.Reset();
        Interlocked.Exchange(ref m_burstMs, 0);
        long journalCount = Interlocked.Read(ref StorePaymentJournal.AppendCount) - m_journalCount;
        double journalMs = Ms(
            Interlocked.Read(ref StorePaymentJournal.AppendTicks) - m_journalTicks
        );
        double[] sorted = Payments.OrderBy(x => x).ToArray();
        double Percentile(double p) =>
            sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
        var result = new
        {
            done = true,
            payments = sorted.Length,
            refused = m_refused,
            failed = m_failed,
            paymentP50Ms = Math.Round(Percentile(0.5), 2),
            paymentP90Ms = Math.Round(Percentile(0.9), 2),
            paymentMaxMs = Math.Round(sorted.LastOrDefault(), 2),
            paymentTotalMs = Math.Round(sorted.Sum(), 1),
            journalAppends = journalCount,
            journalTotalMs = Math.Round(journalMs, 1),
            journalMaxMs = Math.Round(
                Ms(Interlocked.Read(ref StorePaymentJournal.AppendMaxTicks)),
                2
            ),
        };
        Volatile.Write(ref m_result, result);
        Plugin.Log(
            $"store-payment-burst done {System.Text.Json.JsonSerializer.Serialize(result)} (SE-1883)"
        );
    }

    private static T InvokeOnGame<T>(Func<T> read)
    {
        var done = new System.Threading.Tasks.TaskCompletionSource<T>();
        Sandbox.MySandboxGame.Static.Invoke(
            () =>
            {
                try
                {
                    done.SetResult(read());
                }
                catch (Exception exception)
                {
                    done.SetException(exception);
                }
            },
            "SE-1525 test read"
        );
        return done.Task.Wait(10000)
            ? done.Task.Result
            : throw new TimeoutException("the game thread did not answer");
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
