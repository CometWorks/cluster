using System.Net;
using ClusterProtocol;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>
/// SE-1197: F3 Kick and Ban on a node reach the cluster-wide ban and kick books here.
///
/// Vanilla's KickUser/BanUser act on the node the admin is attached to, and the kick list and the ban config are
/// node-local, so a kicked or banned player came back through another node. The node now forwards the action, after
/// vanilla's admin check, to the same admin contract routes Quasar uses (POST /admission/bans and
/// /clients/{id}/kick), presenting the cluster join token it already holds for the registry. The join token alone
/// is not enough: the request must name, in <see cref="ClusterNet.ModeratorHeader"/>, an admin from the Gateway's
/// own administrator list who is connected right now and attached to the calling node
/// (<see cref="ClusterNet.NodeHeader"/>). Any other admin route stays closed to the join token. A request that
/// names a moderator is held to these rules on a loopback Gateway too, where the admin contract is otherwise open.
/// </summary>
internal sealed partial class DirectGateway
{
    /// <summary>
    /// The actor for a node forwarding an admin's kick or ban, or null. <paramref name="refusal"/> is set when the
    /// join token was presented (or <paramref name="loopback"/> needs none) on one of the two routes but the named
    /// admin does not qualify (403, not 401: the credential is fine, the act is not).
    /// </summary>
    private AdminActor? NodeModerator(HttpListenerContext context, string path, bool loopback, out string? refusal)
    {
        refusal = null;
        if (!IsNodeModerationRoute(context.Request.HttpMethod, path) || !loopback && !JoinTokenPresented(context))
            return null;
        string? node = context.Request.Headers[ClusterNet.NodeHeader];
        if (!ulong.TryParse(context.Request.Headers[ClusterNet.ModeratorHeader], out ulong admin) || admin == 0)
            refusal = $"A node's kick or ban must name the admin it acts for in {ClusterNet.ModeratorHeader}.";
        else if (!_admission.IsAdmin(admin))
            refusal = $"{admin} is not on the cluster's administrator list.";
        else if (!_clients.TryGetValue(admin, out ClientSession? session) || session.RegistryRoute?.Node is not { } attached)
            refusal = $"Admin {admin} is not connected to the cluster.";
        else if (!string.Equals(attached, node, StringComparison.Ordinal))
            refusal = $"Admin {admin} is attached to {attached}, not to {node ?? "(no node)"}.";
        if (refusal != null)
        {
            _events.Append("admin.denied", "node:" + (node ?? "?"), target: path);
            return null;
        }
        return new AdminActor($"node:{node}/admin:{admin}", Admin.AdminScope.Manage);
    }

    private static bool IsNodeModerationRoute(string method, string path)
    {
        if (method != "POST") return false;
        string prefix = Admin.AdminProtocol.RoutePrefix;
        if (path.Equals(prefix + "/admission/bans", StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(prefix + "/clients/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/kick", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(path[(prefix + "/clients/").Length..^"/kick".Length], out _);
    }
}
