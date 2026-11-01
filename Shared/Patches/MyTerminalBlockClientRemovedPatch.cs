using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using VRage.Game;

namespace Shared.Patches;

/// <summary>
/// One shared subscription to <c>Sync.Clients.ClientRemoved</c> for every terminal block, instead of one each.
///
/// Vanilla MyTerminalBlock.Init does <c>Sync.Clients.ClientRemoved += ClientRemoved</c> on the server and
/// Closing does <c>-= ClientRemoved</c>. That is a multicast delegate with one entry per terminal block on
/// the node, so every add and every remove copies the whole invocation list: O(N) time and an N-sized array
/// (N ~ 10^4 on a cluster node: a large-object-heap allocation per block). A partition going offline or
/// released closes hundreds of blocks in one frame. On w32 node-1 (CPU trace 2026-09-25 18:31-18:41) the
/// removal (MulticastDelegate.RemoveImpl under MyThrust/MyTerminalBlock.Closing) was ~16% of the game
/// thread inside the 300-700 ms offline hitches.
///
/// The transpiler swaps those two accessor calls for <see cref="Subscribe"/> and <see cref="Unsubscribe"/>,
/// which keep the handlers in a set keyed by block (O(1) both ways) behind ONE handler on the collection.
/// Behaviour is unchanged: on a client leaving, every registered block's own ClientRemoved still runs (it
/// only closes a text box that client had open). Stock 1.210.014 IL: exactly one <c>add_ClientRemoved</c>
/// call in Init and one <c>remove_ClientRemoved</c> call in Closing; a game update that moves them fails the
/// Harmony pass loudly (GateTranspilerHelpers) instead of half-applying.
/// </summary>
[HarmonyPatch]
public static class MyTerminalBlockClientRemovedPatch
{
    private static readonly object Gate = new();
    private static readonly Dictionary<object, Action<ulong>> Handlers = new(ReferenceEqualityComparer.Instance);
    private static MyClientCollection s_subscribedTo;

    /// <summary>Registered block handlers, for diagnostics and the self-check.</summary>
    public static int Count
    {
        get { lock (Gate) return Handlers.Count; }
    }

    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MyTerminalBlock), "Init", new[] { typeof(MyObjectBuilder_CubeBlock), typeof(Sandbox.Game.Entities.MyCubeGrid) });
        yield return AccessTools.Method(typeof(MyTerminalBlock), "Closing");
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = new List<CodeInstruction>(instructions);
        bool init = original.Name == "Init";
        string accessor = init ? "add_ClientRemoved" : "remove_ClientRemoved";
        int at = il.IndexOfSingle(original, accessor, ci => ci.CallsNamed(accessor, typeof(MyClientCollection)));
        il[at].opcode = OpCodes.Call;
        il[at].operand = AccessTools.Method(typeof(MyTerminalBlockClientRemovedPatch), init ? nameof(Subscribe) : nameof(Unsubscribe));
        return il;
    }

    /// <summary>Transpiler call site for <c>clients.ClientRemoved += handler</c> (same stack shape).</summary>
    public static void Subscribe(MyClientCollection clients, Action<ulong> handler)
    {
        if (clients == null || handler == null)
            return;
        lock (Gate)
        {
            if (!ReferenceEquals(s_subscribedTo, clients))
            {
                // A new session builds a new collection; blocks of the old one closed with it.
                if (s_subscribedTo != null)
                    s_subscribedTo.ClientRemoved -= Raise;
                clients.ClientRemoved += Raise;
                s_subscribedTo = clients;
            }
            Handlers[handler.Target ?? handler] = handler;
        }
    }

    /// <summary>Transpiler call site for <c>clients.ClientRemoved -= handler</c> (same stack shape).</summary>
    public static void Unsubscribe(MyClientCollection clients, Action<ulong> handler)
    {
        if (handler == null)
            return;
        // T-0404 release look: release sub-timer 4 (relUnsubscribeMs/N), inert unless a release close is timed.
        ClusterPlugin.ImportAddDiag.RelSubEnter(4);
        try
        {
            lock (Gate)
                Handlers.Remove(handler.Target ?? handler);
        }
        finally
        {
            ClusterPlugin.ImportAddDiag.RelSubExit(4);
        }
    }

    private static void Raise(ulong steamId)
    {
        Action<ulong>[] handlers;
        int count;
        lock (Gate)
        {
            // Copied out so a handler may close a block (and unsubscribe) without breaking the walk; pooled, so
            // a client leaving costs no allocation of the size the multicast list used to.
            count = Handlers.Count;
            handlers = ArrayPool<Action<ulong>>.Shared.Rent(Math.Max(1, count));
            Handlers.Values.CopyTo(handlers, 0);
        }
        try
        {
            for (int i = 0; i < count; i++)
                handlers[i](steamId);
        }
        finally
        {
            Array.Clear(handlers, 0, count);
            ArrayPool<Action<ulong>>.Shared.Return(handlers);
        }
    }
}
