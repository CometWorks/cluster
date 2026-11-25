using System;
using System.Threading;
using HarmonyLib;

namespace ClusterTestPlugin;

/// <summary>Hold one outbound confirmation until released or the origin process exits.</summary>
internal static class ContractConfirmHoldRoutes
{
    private static long m_contract;
    private static int m_held;
    private static bool m_patched;

    public static void Register()
    {
        TestRoutes.Add(
            "POST",
            "/contract-test-hold-confirm",
            context =>
            {
                long contract = TestRoutes.Long(context, "contract");
                if (contract < 0)
                    throw new ArgumentOutOfRangeException(nameof(contract));
                if (!m_patched)
                {
                    var ledger = AccessTools.TypeByName("ClusterPlugin.ContractLedger");
                    var send =
                        AccessTools.Method(ledger, "Send")
                        ?? throw new MissingMethodException("ContractLedger.Send");
                    new Harmony("ClusterTestPlugin.ContractConfirmHold").Patch(
                        send,
                        prefix: new HarmonyMethod(
                            typeof(ContractConfirmHoldRoutes),
                            nameof(BeforeSend)
                        )
                    );
                    m_patched = true;
                }
                Interlocked.Exchange(ref m_held, 0);
                Interlocked.Exchange(ref m_contract, contract);
                TestRoutes.Json(context, 200, new { contract });
            }
        );
        TestRoutes.Add(
            "GET",
            "/contract-test-hold-confirm",
            context => TestRoutes.Json(context, 200, new { held = Volatile.Read(ref m_held) })
        );
    }

    private static bool BeforeSend(object request)
    {
        var fields = Traverse.Create(request);
        if (
            fields.Property("Kind").GetValue<string>() != "Confirm"
            || fields.Property("ContractId").GetValue<long>() != Interlocked.Read(ref m_contract)
        )
            return true;
        // Send returns null for a missing answer. The normal worker retries.
        Interlocked.Increment(ref m_held);
        return false;
    }
}
