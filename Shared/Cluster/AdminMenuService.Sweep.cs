using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Inventory;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// SE-1687: the Trash Removal "Other" tab's buttons, Remove floating objects, Remove cargo bags and Stop all
/// entities, for the whole cluster. Vanilla runs them over the admin's node's entities only. The admin's node keeps
/// vanilla for its own entities and asks the other nodes to run the same loop over the entities they own; the admin
/// gets the other nodes' counts in chat.
/// </summary>
internal static partial class AdminMenuService
{
    private const string SweepTopic = "admin-sweep";

    internal enum Sweep : byte
    {
        Floating = 0,
        CargoBags = 1,
        Stop = 2,
    }

    /// <summary>Game thread: vanilla's loop for the sweep over the entities <paramref name="include"/> lets through.
    /// Returns how many it closed or stopped.</summary>
    internal static int SweepHere(Sweep sweep, Func<MyEntity, bool> include)
    {
        int count = 0;
        foreach (MyEntity entity in MyEntities.GetEntities().ToArray())
        {
            bool hit = sweep switch
            {
                Sweep.Floating => entity is MyFloatingObject or MyInventoryBagEntity,
                Sweep.CargoBags => entity is MyCargoContainerInventoryBagEntity,
                _ => entity.Physics != null
                    && !entity.Closed
                    && entity is not MyCharacter
                    && MySession.Static.Players.GetEntityController(entity) == null,
            };
            if (!hit || !include(entity))
                continue;
            if (sweep == Sweep.Stop)
                entity.Physics.ClearSpeed();
            else
                entity.Close();
            count++;
        }
        return count;
    }

    // Game thread, on every other node: the sweep over the entities this node owns
    private static Task<byte[]> OnSweep(PluginMessage message)
    {
        if (message.Source == Node || message.Payload is not { Length: 1 })
            return Task.FromResult(Array.Empty<byte>());
        var sweep = (Sweep)message.Payload[0];
        int count = SweepHere(sweep, entity => OwnedHere(entity.EntityId));
        if (count > 0)
            Console.WriteLine(
                $"[ClusterPlugin] admin sweep {sweep} asked by {message.Source}: {count} (SE-1687)"
            );
        return Task.FromResult(BitConverter.GetBytes(count));
    }

    /// <summary>Any thread: the sweep on every other node, and the chat line for the admin.</summary>
    internal static async Task<string> SweepAsync(Sweep sweep)
    {
        PluginBroadcastResult result = await BroadcastAsync(SweepTopic, new[] { (byte)sweep })
            .ConfigureAwait(false);
        var done = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var missing = new List<string>();
        if (result.Nodes == null)
            missing.Add(AllNodes);
        foreach (var answer in result.Nodes ?? new Dictionary<string, PluginResult>())
        {
            if (answer.Key == Node)
                continue;
            if (
                answer.Value.Code != PluginResultCode.Success
                || answer.Value.Payload is not { Length: 4 }
            )
                missing.Add(answer.Key);
            else
                done[answer.Key] = BitConverter.ToInt32(answer.Value.Payload, 0);
        }
        return SweepLine(sweep, done, missing);
    }

    internal static string SweepLine(
        Sweep sweep,
        IReadOnlyDictionary<string, int> done,
        IReadOnlyCollection<string> missing
    )
    {
        string what = sweep switch
        {
            Sweep.Floating => "Remove floating objects",
            Sweep.CargoBags => "Remove cargo bags",
            _ => "Stop entities",
        };
        string text =
            "Admin "
            + what
            + " on the other servers: "
            + (
                done.Count == 0
                    ? (missing.Count == 0 ? "there are none." : "none answered.")
                    : string.Join(", ", done.Select(entry => $"{entry.Value} on {entry.Key}")) + "."
            );
        if (missing.Contains(AllNodes))
            text += " The other servers could not be asked.";
        else if (missing.Count > 0)
            text += " No answer from " + string.Join(", ", missing) + ".";
        return text;
    }
}
