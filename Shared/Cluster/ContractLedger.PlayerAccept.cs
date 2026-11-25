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

    // Decides an intent from the durable payment journal: a recorded grant is confirmed, anything else released.
    // A failure other than an unreadable intent tries again a minute later, so it does not wait for a restart.
    private static async Task RecoverIntent(string path, HashSet<Guid> committed)
    {
        while (true)
        {
            // The release phase renames the intent; an earlier attempt may have got that far.
            string release = Path.ChangeExtension(path, ".release");
            if (!File.Exists(path) && File.Exists(release))
                path = release;
            if (!File.Exists(path))
                return;
            try
            {
                var intent =
                    JsonSerializer.Deserialize<Intent>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Empty contract acceptance intent");
                bool confirm =
                    !ContractAcceptIntentFile.IsReleasing(path)
                    && committed.Contains(intent.Offer.AcceptanceOperation);
                await ResolveIntent(intent, path, confirm);
                return;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                Console.WriteLine(
                    $"[ClusterPlugin] leaving unreadable contract intent {path}: {error}"
                );
                return;
            }
            catch (Exception error)
            {
                Console.WriteLine(
                    $"[ClusterPlugin] contract intent {path} failed, retrying in a minute: {error}"
                );
            }
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            committed = StorePaymentJournal.ContractOperations();
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
        // The decision carries the offer, so a confirmation that arrives after the reservation expired can still
        // install the grant when nobody else took the offer.
        var decision = intent.Offer with
        {
            Kind = confirm ? "Confirm" : "Release",
        };
        Result answer;
        while ((answer = Send(decision, intent.Decision)) == null)
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!answer.Success)
        {
            // Only a late confirmation can be refused: another player took the offer after this reservation
            // expired. The intent stays for an operator and for the next start.
            Console.WriteLine(
                $"[ClusterPlugin] contract_grant_refused id={decision.ContractId} kind={decision.Kind}"
                    + $" reason={answer.Reason} intent={path}"
            );
            return;
        }
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
                        // An activation that failed before its commit decides from the journal, like a restart.
                        if (uncertain)
                            await RecoverIntent(path, StorePaymentJournal.ContractOperations());
                        else if (File.Exists(path))
                            await ResolveIntent(intent, path, confirm);
                    }
                    catch (Exception error)
                    {
                        // The player's result stands: a granted contract is theirs, a refused one stays refused.
                        Console.WriteLine(
                            $"[ClusterPlugin] contract intent {path} failed, retrying: {error}"
                        );
                        _ = RecoverIntent(path, StorePaymentJournal.ContractOperations());
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
