using System.Globalization;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Utils;

namespace ClusterTestPlugin;

/// <summary>SE-1158: what the game's own DLC and skin checks answer on this node for a player.</summary>
internal static class EntitlementRoutes
{
    public static void Register()
    {
        // GET /test-entitlements?steamId= - HasDLC per DLC, HasDefinitionDLC of the first block of each DLC (the gate of
        // MyCubeGrid.BuildBlockRequestInternal and projector builds), and the armor skins HasItem accepts.
        TestRoutes.Add("GET", "/test-entitlements", context =>
        {
            ulong steamId = ulong.Parse(TestRoutes.Query(context, "steamId"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var dlcs = MySession.Static.GetComponent<MySessionComponentDLC>();
                var inventory = MySession.Static.GetComponent<MySessionComponentGameInventory>();
                var blocks = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyCubeBlockDefinition>()
                    .Where(block => block.DLCs is { Length: 1 }).GroupBy(block => block.DLCs[0])
                    .ToDictionary(group => group.Key, group => group.OrderBy(block => block.Id.SubtypeName).First());
                var skins = MyDefinitionManager.Static.GetAssetModifierDefinitions()
                    .Select(skin => skin.Id.SubtypeId).Where(skin => inventory?.HasItem(skin, steamId) == true)
                    .Select(skin => skin.String).OrderBy(skin => skin).ToArray();
                return new
                {
                    steamId,
                    hasDlc = MyDLCs.DLCs.Values.ToDictionary(dlc => dlc.Name, dlc => dlcs.HasDLC(dlc.Name, steamId)),
                    blocks = blocks.ToDictionary(pair => pair.Key, pair => new
                    {
                        block = pair.Value.Id.ToString(),
                        canBuild = dlcs.HasDefinitionDLC(pair.Value, steamId),
                    }),
                    skins,
                };
            }));
        });
    }
}
