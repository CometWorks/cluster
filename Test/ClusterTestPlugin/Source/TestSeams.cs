using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using ClusterPlugin;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Network;

namespace ClusterTestPlugin;

/// <summary>
/// Test-only operations that used to live on product services (PromotionService, GridTransferService,
/// PartitionPersistence). They reach product state through the publicized cluster assembly.
/// </summary>
internal static class TestSeams
{
    public static object SetPromoteLevel(ulong userId, int level)
    {
        MySession session = MySession.Static ?? throw new InvalidOperationException("No session");
        if (level < 0 || level > (int)MyPromoteLevel.Admin) throw new ArgumentOutOfRangeException(nameof(level));
        session.SetUserPromoteLevel(userId, (MyPromoteLevel)level);
        return new { node = PromotionService.Node, userId, requested = level, level = (int)session.GetUserPromoteLevel(userId) };
    }

    /// <summary>
    /// Initiates the vanilla transfer request server-side under the old owner's event context - the
    /// integration scenario cannot drive the client Info-tab UI that normally raises it. Everything
    /// downstream (validation, transaction bookkeeping, cross-node offer routing) is the production path.
    /// </summary>
    public static object OfferGridTransfer(long gridEntityId, long oldOwner, long newOwner)
    {
        MyIdentity oldIdentity = MySession.Static.Players.TryGetIdentity(oldOwner)
            ?? throw new InvalidOperationException("Old owner identity is unknown here");
        if (!oldIdentity.BlockLimits.BlocksBuiltByGrid.TryGetValue(gridEntityId, out MyBlockLimits.MyGridLimitData gridData))
            throw new InvalidOperationException("Old owner built nothing on this grid");
        ulong oldOwnerSteamId = MySession.Static.Players.TryGetSteamId(oldOwner);
        if (oldOwnerSteamId == 0) throw new InvalidOperationException("Old owner has no cluster binding");
        using (MyEventContext.Set(new EndpointId(oldOwnerSteamId), null, isInvokedLocally: false))
            MyBlockLimits.SendTransferRequestMessage(gridData, newOwner);
        return new { requested = true, blocks = gridData.BlocksBuilt, pcu = gridData.PCUBuilt };
    }

    /// <summary>The grid's per-identity authorship counts, for asserting who owns what after a transfer.</summary>
    public static object GridTransferState(long gridEntityId)
    {
        if (!MyEntities.TryGetEntityById(gridEntityId, out MyEntity entity) || entity is not MyCubeGrid grid)
            return new { exists = false };
        return new { exists = true, authorship = grid.IdentityAuthorshipBlockCount };
    }

    /// <summary>Keep this node from recovering unowned partitions (recovery-ordering scenarios).</summary>
    public static void SuppressRecovery(PartitionPersistence persistence)
    {
        persistence.m_recoveryAfter = DateTime.MaxValue;
    }

    // ---- State behind ClusterSeams (T-0171). These used to be fields of the cluster plugins; the
    // product now only reads the seam, and a deployment without this plugin leaves it null. ----

    /// <summary>The field /debug/corrupt-import has armed, or null. The route reports it back.</summary>
    internal static volatile string m_corruptImportField;

    /// <summary>
    /// Partitioning.md 6.10 / P3-HND-03. Arms or clears the perturbation of the import round-trip
    /// gate's target document. Setting the seam is also what makes the gate take the slow,
    /// document-walking path; clearing it puts both of its shortcuts back.
    /// </summary>
    public static void SetCorruptImportField(string field)
    {
        m_corruptImportField = field;
        ClusterSeams.CorruptImportedDocument = field == null ? null : Perturb;
    }

    /// <summary>Runs inside the gate, on the already-normalised target document.</summary>
    private static void Perturb(XDocument target, List<string> residuals)
    {
        string field = m_corruptImportField;
        if (field == null) return;
        XElement victim = target.Root?.Descendants(field).FirstOrDefault(element => !element.HasElements);
        if (victim == null) residuals.Add("corrupt-import: field is absent: " + field);
        else victim.Value += "~corrupt";
        HoldImport();
    }

    /// <summary>Milliseconds /debug/hold-import keeps a corrupted import's entities before its discard, 0 = none.</summary>
    internal static volatile int m_holdImportMs;

    /// <summary>How many imports the hold has held, for the route's answer.</summary>
    internal static int m_heldImports;

    /// <summary>
    /// SE-1167: a failed import's entities exist on the target only between its activate frame and the
    /// discard, about 0.1 s on a bench, so a plugin that reacts to new grids (Impostors reports them) rarely
    /// gets to act. Sleeping here, inside the gate that is about to fail, keeps them in the world for the
    /// armed time, as a big partition's slow import does live (37 s, SE-1133). The staged import runs the
    /// gate on the control server's handler thread while the game goes on; the legacy import runs it on the
    /// game thread, where a sleep would only freeze the world, so it is not held there.
    /// </summary>
    private static void HoldImport()
    {
        int ms = m_holdImportMs;
        if (ms <= 0) return;
        if (MySandboxGame.Static?.UpdateThread == Thread.CurrentThread)
        {
            Plugin.Log("hold-import: not held, the gate runs on the game thread (import.mode=legacy)");
            return;
        }
        Interlocked.Increment(ref m_heldImports);
        Plugin.Log("hold-import: holding the failing import for " + ms + " ms before its discard");
        Thread.Sleep(ms);
        Plugin.Log("hold-import: released the failing import to its discard");
    }

    private static ulong m_breakClient;
    private static double m_breakUntil;

    /// <summary>
    /// P1-CHR-03: suppress controller rebinding for one client, so the 6.6 fallback can be proven to
    /// convert a would-be stuck character into a forced reconnect rather than a dead session.
    /// </summary>
    public static object BreakControlRebind(ulong client, double seconds)
    {
        m_breakClient = client;
        m_breakUntil = ClusterRuntime.LocalMilliseconds + seconds * 1000;
        ClusterSeams.SuppressControlRebind = IsRebindBroken;
        if (MySession.Static?.Players != null
            && MySession.Static.Players.TryGetPlayerBySteamId(client, out MyPlayer player)
            && player.Controller?.ControlledEntity?.Entity is MyEntity held)
            MySession.Static.Players.ClusterForgetControlledEntity(held);
        return new { client, brokenForSeconds = seconds };
    }

    private static bool IsRebindBroken(ulong client)
        => client == m_breakClient && ClusterRuntime.LocalMilliseconds < m_breakUntil;
}
