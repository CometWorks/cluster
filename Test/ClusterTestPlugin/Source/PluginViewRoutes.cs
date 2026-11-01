using System;
using System.Linq;
using PluginSdk.Clustering;

namespace ClusterTestPlugin;

/// <summary>T-0256: PluginSdk's cluster-wide views as a plugin on a node or the WA reads them.</summary>
internal static class PluginViewRoutes
{
    public static void Register()
    {
        // GET /plugin-views[?entity=ID][&resolve=PARTITION] - the online players, player positions and (with
        // entity) that entity's placement, read on the game thread; resolve names a partition's owner through
        // ResolveOwnerAsync, the SDK's existing half of "where is it".
        TestRoutes.Add("GET", "/plugin-views", context =>
        {
            string id = PluginStorageRoutes.OwnId();
            PluginClusterClient client;
            try { client = PluginCluster.ForPlugin(id); }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                TestRoutes.Json(context, 200, new { error = error.GetType().Name, message = error.Message, pluginId = id });
                return;
            }
            long entity = long.TryParse(context.Request.QueryString["entity"], out long parsed) ? parsed : 0;
            var views = TestRoutes.OnGameThread(() => new
            {
                players = client.OnlinePlayers()?.Select(p => new { p.Node, steamId = p.SteamId.ToString(), p.SerialId, p.IdentityId, p.Name, p.IsAdmin }).ToArray(),
                positions = client.PlayerPositions()?.Select(p => new { p.IdentityId, steamId = p.SteamId.ToString(), p.EntityId,
                    position = new[] { p.Position.X, p.Position.Y, p.Position.Z } }).ToArray(),
                placement = entity == 0 ? null : client.LocateEntity(entity) is { } placed
                    ? new { placed.EntityId, residence = placed.Residence.ToString(), partition = placed.Partition.ToString(), placed.Node } : null,
            });
            object owner = null;
            if (ulong.TryParse(context.Request.QueryString["resolve"], out ulong partition) && partition != 0)
            {
                var result = client.ResolveOwnerAsync(new PluginTarget { Kind = PluginTargetKind.Partition, Partition = partition })
                    .GetAwaiter().GetResult();
                owner = new { code = result.Code.ToString(), node = result.Owner?.Node, generation = result.Owner?.Generation };
            }
            TestRoutes.Json(context, 200, new
            {
                node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                pluginId = id,
                views.players,
                views.positions,
                views.placement,
                owner,
            });
        });
    }
}
