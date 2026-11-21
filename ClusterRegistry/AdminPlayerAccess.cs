using System.Net;
using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal sealed partial class DirectGateway
{
    private readonly object _playerAccessMutation = new();

    private void AdminSetPlayerAccess(HttpListenerContext context, AdminActor actor)
    {
        if (!RequireManage(context, actor, "player-access"))
            return;
        var update =
            ReadAdminBody<Admin.PlayerAccessPolicy>(context)
            ?? throw new ArgumentException("Player access policy is required.");
        string target = "cluster:" + _registry!.AdminStatus(_clients.Count).ClusterId;
        lock (_playerAccessMutation)
        {
            // Absolute replacement with a monotonic policy revision/time makes duplicate refreshes idempotent.
            try
            {
                _admission.PlayerAccess.Apply(update, target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"Player access policy could not be persisted: {error.Message}"
                );
                WriteAdminError(
                    context,
                    503,
                    "player_access_persistence_failed",
                    "Gateway could not persist the player access policy."
                );
                return;
            }
            foreach (var session in _clients.Values)
                if (
                    _admission.PlayerAccess.ConfirmedDenial(session.ClientId, DateTimeOffset.UtcNow)
                )
                    ForceReconnect(session, "player-access-revoked");
            var promotions = new NodeAccessPromotionPolicy(
                update.Enabled ? update.ExpiresAt : DateTimeOffset.UtcNow,
                update.Enabled
                    ? update
                        .Players.Where(p => p.Allowed && p.Confirmed && p.Promotion > 0)
                        .Select(p => new NodeAccessPromotion(p.SteamId, p.Promotion, p.ExpiresAt))
                        .ToArray()
                    : []
            );
            try
            {
                PlayerPromotionCommand(
                    "promotion-access",
                    JsonSerializer.Serialize(promotions),
                    Guid.NewGuid()
                );
            }
            catch (Exception error)
                when (error is InvalidOperationException or IOException or TimeoutException)
            {
                // Admission already has its durable policy. Quasar retries the promotion publication on its next refresh.
                WriteAdminError(
                    context,
                    503,
                    "promotion_not_ready",
                    "Player access admission is applied; World Authority promotion publication is not ready."
                );
                return;
            }
            WriteAdminEnvelope(context, _admission.PlayerAccess.Status);
        }
    }

    private sealed record NodeAccessPromotionPolicy(
        DateTimeOffset ExpiresAt,
        NodeAccessPromotion[] Players
    );

    private sealed record NodeAccessPromotion(ulong SteamId, int Level, DateTimeOffset ExpiresAt);
}
