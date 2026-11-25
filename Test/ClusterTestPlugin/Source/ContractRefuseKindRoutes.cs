using System;
using HarmonyLib;

namespace ClusterTestPlugin;

/// <summary>Makes this World Authority answer one contract ledger kind as a version without it would.</summary>
internal static class ContractRefuseKindRoutes
{
    private static volatile string m_kind;
    private static bool m_patched;

    public static void Register()
    {
        // POST /contract-test-refuse-kind?kind= - refuse `kind` like an older World Authority; an empty kind clears it.
        TestRoutes.Add(
            "POST",
            "/contract-test-refuse-kind",
            context =>
            {
                string kind = TestRoutes.Query(context, "kind", "");
                lock (typeof(ContractRefuseKindRoutes))
                    if (!m_patched)
                    {
                        var ledger = AccessTools.TypeByName("ClusterPlugin.ContractLedger");
                        var execute =
                            AccessTools.Method(ledger, "Execute")
                            ?? throw new MissingMethodException("ContractLedger.Execute");
                        new Harmony("ClusterTestPlugin.ContractRefuseKind").Patch(
                            execute,
                            prefix: new HarmonyMethod(
                                typeof(ContractRefuseKindRoutes),
                                nameof(BeforeExecute)
                            )
                        );
                        m_patched = true;
                    }
                m_kind = kind.Length == 0 ? null : kind;
                TestRoutes.Json(context, 200, new { kind = m_kind });
            }
        );
    }

    private static void BeforeExecute(object request)
    {
        string kind = Traverse.Create(request).Property("Kind").GetValue<string>();
        if (kind != null && kind == m_kind)
            // The exact refusal of a 0.3.34 World Authority's request switch.
            throw new InvalidOperationException("Invalid contract ledger request " + kind);
    }
}
