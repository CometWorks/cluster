using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal sealed partial class DirectGateway
{
    private readonly ConcurrentDictionary<string, byte> _promotionInFlight = new();
    private readonly object _kickCooldownMutationLock = new();

    private string PlayerPromotionCommand(string subsystem, string payload, Guid? operationId = null)
    {
        WorldAuthority authority = _registry!.WorldAuthority();
        if (authority.Node == null || authority.Generation <= 0)
            throw new InvalidOperationException("World Authority is unavailable.");
        string reply = SendGatewayCommand(authority, subsystem, payload, CancellationToken.None, operationId).GetAwaiter().GetResult();
        WorldAuthority current = _registry.WorldAuthority();
        if (current.Node != authority.Node || current.Epoch != authority.Epoch || current.Generation != authority.Generation)
            throw new InvalidOperationException("World Authority changed during the player operation.");
        var result = JsonSerializer.Deserialize<GlobalCommandResult>(reply)
            ?? throw new InvalidDataException("Empty World Authority player response.");
        if (result.Payload == null || result.Error == null || result.Success == (result.Error.Length != 0))
            throw new InvalidDataException("Invalid World Authority player response.");
        if (!result.Success && result.Error == "promotion_revision_mismatch")
            throw new PromotionRejectedException("revision_mismatch", "Player role changed. Refresh the player state before submitting a new action.");
        if (!result.Success && result.Error == "promotion_not_ready")
            throw new PromotionRejectedException("promotion_not_ready", "World Authority promotions are still loading. Refresh before submitting a new action.");
        if (!result.Success) throw new InvalidOperationException(result.Error);
        return result.Payload;
    }

    private void AdminPlayerModeration(HttpListenerContext context)
    {
        Admin.PlayerPromotion[]? promotions = null;
        string? error = null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NodePromotionSnapshot>(PlayerPromotionCommand("promotion-query", "{}"));
            if (snapshot?.Entries == null || snapshot.Entries.Length > 4096
                || snapshot.Entries.Any(entry => entry == null || entry.SteamId == 0 || entry.Level is < 0 or > 4 || entry.Revision < 0)
                || snapshot.Entries.Select(entry => entry.SteamId).Distinct().Count() != snapshot.Entries.Length)
                throw new InvalidDataException("Invalid World Authority promotion snapshot.");
            promotions = snapshot.Entries.Select(entry => new Admin.PlayerPromotion(entry.SteamId, (Admin.PlayerPromotionLevel)entry.Level, entry.Revision)).ToArray();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException or TimeoutException or JsonException)
        {
            error = "World Authority promotion state is unavailable. " + e.Message;
        }
        WriteAdminEnvelope(context, new Admin.PlayerModerationStatus(promotions, _admission.KickCooldowns(), error));
    }

    private void AdminSetPlayerPromotion(HttpListenerContext context, AdminActor actor, ulong userId)
    {
        if (!RequireManage(context, actor, "player-promotion")) return;
        Admin.SetPlayerPromotionRequest? request;
        try { request = ReadAdminBody<Admin.SetPlayerPromotionRequest>(context); }
        catch (ArgumentException e)
        {
            WriteAdminError(context, 400, "invalid_arguments", e.Message);
            return;
        }
        if (userId == 0 || request == null || !Enum.IsDefined(request.Level) || request.ExpectedRevision < 0 || request.ExpectedRevision == long.MaxValue)
        {
            WriteAdminError(context, 400, "invalid_arguments", "A non-zero userId and a role from None through Admin are required.");
            return;
        }
        var record = BeginMutation(context, actor, "player-promotion", userId + "|" + Fingerprint(request), userId.ToString(), out bool replayed);
        if (record == null) return;
        if ((record.State == AdminOperationPhase.Running || record.ErrorCode == "promotion_uncertain")
            && _promotionInFlight.TryAdd(record.OperationId, 0))
        {
            try
            {
                if (record.State == AdminOperationPhase.Failed && !_registry!.ResumePlayerPromotion(record.OperationId))
                {
                    WriteOperation(context, record);
                    return;
                }
                var entry = new NodePromotionChange(userId, (int)request.Level, request.ExpectedRevision);
                var applied = JsonSerializer.Deserialize<NodePromotionEntry>(PlayerPromotionCommand("promotion-set", JsonSerializer.Serialize(entry), Guid.Parse(record.OperationId)));
                if (applied == null || applied.SteamId != userId || applied.Level != (int)request.Level
                    || applied.Revision < request.ExpectedRevision || applied.Revision > request.ExpectedRevision + 1)
                    throw new InvalidDataException("World Authority promotion acknowledgement does not match the player, role and revision.");
                _registry!.CompleteAdminOperation(record.OperationId, new Dictionary<string, string>
                {
                    ["userId"] = userId.ToString(), ["level"] = request.Level.ToString(), ["revision"] = applied.Revision.ToString(),
                });
            }
            catch (PromotionRejectedException e)
            {
                _registry!.FailAdminOperation(record.OperationId, e.Code, e.Message);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException or TimeoutException or JsonException)
            {
                // A lost acknowledgement is not proof of no change. The same key reconciles
                // the same WA ledger GUID, including after a Gateway or WA restart.
                _registry!.FailAdminOperation(record.OperationId, "promotion_uncertain",
                    "Promotion was not confirmed; it may have applied. Retry the same Idempotency-Key to reconcile. " + e.Message);
            }
            finally { _promotionInFlight.TryRemove(record.OperationId, out _); }
        }
        WriteOperation(context, record);
    }

    private void AdminClearKickCooldown(HttpListenerContext context, AdminActor actor, ulong userId)
    {
        if (!RequireManage(context, actor, "kick-cooldown-clear")) return;
        if (userId == 0)
        {
            WriteAdminError(context, 400, "invalid_arguments", "A non-zero userId is required.");
            return;
        }
        lock (_kickCooldownMutationLock)
        {
            var record = BeginMutation(context, actor, "kick-cooldown-clear", userId.ToString(), userId.ToString(), out bool replayed);
            if (record == null) return;
            if (!replayed)
            {
                _admission.ClearKickCooldown(userId);
                _registry!.CompleteAdminOperation(record.OperationId, new Dictionary<string, string> { ["userId"] = userId.ToString() });
            }
            else if (record.State == AdminOperationPhase.Running)
                _registry!.FailAdminOperation(record.OperationId, "clear_interrupted", "Gateway restarted before this clear was confirmed. Refresh the cooldown before submitting a new action.");
            WriteOperation(context, record);
        }
    }

    private sealed class PromotionRejectedException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
    private sealed record NodePromotionChange(ulong SteamId, int Level, long ExpectedRevision);
    private sealed record NodePromotionEntry(ulong SteamId, int Level, long Revision = 0);
    private sealed record NodePromotionSnapshot(NodePromotionEntry[] Entries);
}
