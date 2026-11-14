using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1552 bench route: a member that is gone when its handover commits on the target. Live, an NPC grid was removed
/// between the import and the commit, so <c>PartitionManager.Adopt</c> pinned it as a pending member for 180 s.
/// A Harmony prefix installed on first arm, on this test plugin only; inert while nothing is armed.
/// </summary>
internal static class PinVanishRoutes
{
    private static long m_entity;
    private static bool m_patched;

    public static void Register()
    {
        // POST /pin-test-vanish?entity= - arm: the next mature Adopt naming `entity` closes it first.
        TestRoutes.Add(
            "POST",
            "/pin-test-vanish",
            context =>
            {
                long entity = TestRoutes.Long(context, "entity");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        EnsurePatched();
                        Interlocked.Exchange(ref m_entity, entity);
                        Plugin.Log($"pin-vanish armed entity={entity} (SE-1552)");
                        return (object)new { armed = true, entity };
                    })
                );
            }
        );
    }

    private static void EnsurePatched()
    {
        if (m_patched)
            return;
        var type =
            AccessTools.TypeByName("ClusterPlugin.PartitionManager")
            ?? throw new TypeLoadException("ClusterPlugin.PartitionManager");
        var original =
            AccessTools.Method(
                type,
                "Adopt",
                new[] { typeof(ulong), typeof(IEnumerable<long>), typeof(bool) }
            ) ?? throw new MissingMethodException("PartitionManager.Adopt");
        new Harmony("ClusterTestPlugin.PinVanish").Patch(
            original,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(PinVanishRoutes), nameof(AdoptPrefix))
            )
        );
        m_patched = true;
    }

    private static void AdoptPrefix(ulong id, IEnumerable<long> members, bool mature)
    {
        long entity = Interlocked.Read(ref m_entity);
        if (
            entity == 0
            || !mature
            || !members.Contains(entity)
            || !MyEntities.TryGetEntityById(entity, out MyEntity found)
        )
            return;
        Interlocked.Exchange(ref m_entity, 0);
        found.Close();
        Plugin.Log(
            $"pin-vanish closed entity={entity} before the adoption of partition {id} (SE-1552)"
        );
    }
}
