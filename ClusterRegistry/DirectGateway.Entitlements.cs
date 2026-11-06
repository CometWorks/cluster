using System.Net;

namespace ClusterRegistry;

/// <summary>
/// SE-1158: what a Steam player owns, which a node can't ask Steam about (Direct Transport replaces its game server).
/// The DLCs come from the ticket verdict (<see cref="SteamFrontend.DlcAppIds"/>); skins from the inventory blob the
/// client sends its node, which the node forwards here. Both go to every node the player attaches to
/// (<see cref="AttachBinding"/>); the node side is PlayerEntitlements.
/// </summary>
internal sealed partial class DirectGateway
{
    /// <summary>PlayerEntitlements.SkinCheckRoute on the node side.</summary>
    private const string SkinCheckRoute = "/registry/steam/skins";

    private sealed record SkinCheckRequest(ulong UserId, string Data);

    private void HandleSkinCheck(HttpListenerContext context, string path)
    {
        if (context.Request.HttpMethod != "POST" || !NodeAuthorized(context, path))
        { WriteJson(context, 403, new { error = "node credential required" }); return; }
        if (context.Request.ContentLength64 is <= 0 or > 256 * 1024)
        { WriteJson(context, 413, new { error = "skin check exceeds limit" }); return; }
        SkinCheckRequest request = ReadJson<SkinCheckRequest>(context);
        if (_steam == null)
        { WriteJson(context, 503, new { error = "no Steam frontend in this Gateway" }); return; }
        // Only a client the Steam frontend admitted: its blob is checked against its own Steam id.
        if (!_clients.TryGetValue(request.UserId, out ClientSession? session) || session.Client?.Frontend != "steam"
            || session.Credential == null)
        { WriteJson(context, 404, new { error = "no Steam session for this player" }); return; }
        Task<string[]?> check = _steam.CheckSkins(request.UserId, Convert.FromBase64String(request.Data));
        string[]? skins = check.Wait(TimeSpan.FromSeconds(8)) ? check.Result : null;
        if (skins == null)
        {
            Console.WriteLine($"Steam refused the inventory of {request.UserId}");
            WriteJson(context, 422, new { error = "Steam refused the inventory" });
            return;
        }
        session.Skins = skins;
        Console.WriteLine($"Steam inventory of {request.UserId}: {skins.Length} skins");
        WriteJson(context, 200, new { skins }, redact: false);
    }
}
