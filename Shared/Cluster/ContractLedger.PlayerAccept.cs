using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage.Network;

namespace ClusterPlugin;

internal static partial class ContractLedger
{
    private sealed class PlayerAccept(
        MyContract offer,
        long player,
        MyContractBlock block,
        EndpointId sender
    )
    {
        public readonly MyContract Offer = offer;
        public readonly long Player = player;

        public bool IsSameClick(MyContractBlock source, long identity, EndpointId endpoint) =>
            block == source && Player == identity && sender == endpoint;

        public readonly Guid Operation = Guid.NewGuid();
        public bool AllowGrant;
        public bool Committed;
    }

    // Local game-thread state. The authority reserves an offer without activating it.
    private static readonly Dictionary<long, PlayerAccept> PlayerAccepts = new();

    private sealed record Intent(Request Offer, Guid Decision);

    private static volatile string m_intents;

    internal static bool AllowActivation(MyContract offer, long player, out Guid approved)
    {
        approved = Guid.Empty;
        if (!PlayerAccepts.TryGetValue(offer.Id, out var pending))
            return true;
        if (pending.Offer != offer || pending.Player != player || !pending.AllowGrant)
            return false;
        pending.AllowGrant = false;
        approved = pending.Operation;
        return true;
    }

    // Called once by the payment journal's startup worker. Publish the directory only
    // after taking its inventory so recovery cannot cancel a new player's reservation.
    internal static void RecoverPlayerAccepts(string data, IEnumerable<Guid> commitOperations)
    {
        string folder = Path.Combine(data, "contract-accepts");
        try
        {
            Directory.CreateDirectory(folder);
            string[] files = ContractAcceptIntentFile.Pending(folder);
            if (files.Length != 0)
            {
                var commits = new HashSet<Guid>(commitOperations);
                foreach (string path in files)
                    _ = Task.Run(() => RecoverIntent(path, commits));
            }
            m_intents = folder;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[ClusterPlugin] contract reservations unavailable: {error}");
        }
    }

    private static async Task RecoverIntent(string path, HashSet<Guid> committed)
    {
        try
        {
            var intent =
                JsonSerializer.Deserialize<Intent>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Empty contract acceptance intent");
            bool confirm =
                !ContractAcceptIntentFile.IsReleasing(path)
                && committed.Contains(intent.Offer.AcceptanceOperation);
            await ResolveIntent(intent, path, confirm);
        }
        catch (Exception error)
        {
            Console.WriteLine(
                $"[ClusterPlugin] leaving unresolved contract intent {path}: {error}"
            );
        }
    }

    internal static void PlayerDepositCommitted(long contract, Guid operation)
    {
        if (PlayerAccepts.TryGetValue(contract, out var pending) && pending.Operation == operation)
            pending.Committed = true;
    }

    private static async Task<Result> AwaitReservation(Intent intent)
    {
        while (true)
        {
            var reply = Send(intent.Offer, intent.Offer.AcceptanceOperation);
            if (reply != null)
                return reply;
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }

    private static async Task ResolveIntent(Intent intent, string path, bool confirm)
    {
        if (!confirm)
        {
            string release = await ContractAcceptIntentFile.PrepareRelease(
                path,
                async () => (await AwaitReservation(intent)).Success
            );
            if (release == null)
            {
                ForgetIntent(path);
                return;
            }
            path = release;
        }
        var decision = new Request(
            confirm ? "Confirm" : "Release",
            intent.Offer.ContractId,
            AcceptanceOperation: intent.Offer.AcceptanceOperation
        );
        Result answer;
        while ((answer = Send(decision, intent.Decision)) == null)
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!answer.Success)
            throw new InvalidDataException(
                $"Authority refused {decision.Kind} for contract {decision.ContractId}: {answer.Reason}"
            );
        ForgetIntent(path);
    }

    private static void ForgetIntent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A later restart can replay the same decision.
            Console.WriteLine(
                $"[ClusterPlugin] settled contract intent retained at {path}: {error}"
            );
        }
    }

    internal static bool TryStartPlayerAccept(MyContractBlock block, long player, long contract)
    {
        var system = System;
        if (
            !RegularNode
            || m_intents == null
            || contract <= 0
            || system == null
            || !block.HasAccess()
        )
            return false;
        var sender = MyEventContext.Current.Sender;
        void Reply(MyContractResults result) =>
            MyMultiplayer.RaiseEvent(block, x => x.ReceiveAcceptContract, result, sender);
        if (sender.Value != 0 && MySession.Static.Players.TryGetIdentityId(sender.Value) != player)
        {
            Reply(MyContractResults.Fail_CannotAccess);
            return true;
        }
        // The client keeps a single callback on this block. An early duplicate reply
        // would consume it even after local activation, while confirmation is pending.
        PlayerAccepts.TryGetValue(contract, out var existing);
        if (existing?.IsSameClick(block, player, sender) == true)
            return true;
        if (!system.m_inactiveContracts.TryGetValue(contract, out var offer))
            return false;
        long Station() =>
            MySession.Static.Factions.GetStationByGridId(block.CubeGrid.EntityId)?.Id ?? 0;
        if (offer.StartStation != Station() && offer.StartBlock != block.EntityId)
            return false;
        if (existing != null)
        {
            Reply(MyContractResults.Fail_NotPossible);
            return true;
        }

        var pending = new PlayerAccept(offer, player, block, sender);
        PlayerAccepts.Add(contract, pending);
        var session = MySession.Static;
        var game = MySandboxGame.Static;
        try
        {
            long owner = (MyEntities.GetEntityById(offer.StartBlock) as MyCubeBlock)?.OwnerId ?? 0;
            var request = AcceptRequest(offer, player, owner, pending.Operation) with
            {
                Kind = "Reserve",
            };
            var intent = new Intent(request, Guid.NewGuid());
            string prefix = Path.Combine(m_intents, pending.Operation.ToString("N"));
            string path = prefix + ".intent";
            _ = Task.Run(async () =>
            {
                Result answer = null;
                try
                {
                    // Write before sending so a restarted origin can release an unclaimed reservation.
                    using (
                        var stream = new FileStream(
                            prefix + ".tmp",
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None
                        )
                    )
                    {
                        JsonSerializer.Serialize(stream, intent);
                        stream.Flush(true);
                    }
                    File.Move(prefix + ".tmp", path);
                    answer = await AwaitReservation(intent);
                }
                catch (Exception error)
                {
                    Console.WriteLine($"[ClusterPlugin] contract reservation failed: {error}");
                }
                game.Invoke(() => Complete(intent, path, answer), "contract reservation");
            });
        }
        catch
        {
            PlayerAccepts.Remove(contract);
            throw;
        }
        return true;

        void Complete(Intent intent, string path, Result answer)
        {
            var result = MyContractResults.Fail_NotPossible;
            bool uncertain = false;
            bool activating = false;
            try
            {
                if (
                    answer?.Success == true
                    && session == MySession.Static
                    && !session.IsUnloading
                    && !block.Closed
                    && !block.MarkedForClose
                    && !IsSettled(contract)
                    && system.m_inactiveContracts.TryGetValue(contract, out var current)
                    && current == offer
                    && (
                        sender.Value == 0
                        || session
                            .Players.GetPlayerById(new MyPlayer.PlayerId(sender.Value, 0))
                            ?.Identity.IdentityId == player
                    )
                    && (block.AnyoneCanUse || block.HasPlayerAccess(player))
                )
                {
                    activating = true;
                    pending.AllowGrant = true;
                    result = system.ActivateContract(player, contract, Station(), block.EntityId);
                    if (result == MyContractResults.Success)
                    {
                        lock (Sync)
                            AcceptedHere[contract] = ClusterRuntime.LocalMilliseconds;
                    }
                }
                else if (answer?.Reason == "contract limit")
                    result =
                        MyContractResults.Fail_ActivationConditionsNotMet_ContractLimitReachedHard;
                else if (answer?.Reason == AlreadySettled)
                {
                    lock (Sync)
                        Remember(contract);
                    system.RemoveInactiveContract(contract);
                }
            }
            catch (Exception error)
            {
                // A mod callback or a journal write may have failed during vanilla activation.
                // Recovery reads the durable payment decision before releasing the reservation.
                uncertain = activating && !pending.Committed;
                Console.WriteLine(
                    $"[ClusterPlugin] player activation failed for {contract}: {error}"
                );
            }
            finally
            {
                pending.AllowGrant = false;
                bool confirm = pending.Committed;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!uncertain && File.Exists(path))
                            await ResolveIntent(intent, path, confirm);
                    }
                    catch (Exception error)
                    {
                        result = MyContractResults.Fail_NotPossible;
                        Console.WriteLine(
                            $"[ClusterPlugin] leaving unresolved contract intent {path}: {error}"
                        );
                    }
                    game.Invoke(
                        () =>
                        {
                            PlayerAccepts.Remove(contract);
                            if (
                                session == MySession.Static
                                && !session.IsUnloading
                                && !block.Closed
                            )
                                Reply(result);
                        },
                        "contract acceptance result"
                    );
                });
            }
        }
    }
}
