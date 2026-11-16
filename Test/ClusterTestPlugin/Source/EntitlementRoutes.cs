using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using ClusterPlugin;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Network;
using VRage.Utils;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>SE-1158: what the game's own DLC and skin checks answer on this node for a player.</summary>
internal static class EntitlementRoutes
{
    public static void Register()
    {
        // GET /test-entitlements?steamId= - HasDLC per DLC, HasDefinitionDLC of the first block of each DLC (the gate of
        // MyCubeGrid.BuildBlockRequestInternal and projector builds), the armor skins HasItem accepts, and
        // what ValidateItem (the gate of placing and painting with a skin) leaves of each skin in validate=a,b.
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
                    validated = TestRoutes.Query(context, "validate", "").Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                        .ToDictionary(skin => skin, skin => inventory.ValidateItem(MyStringHash.GetOrCompute(skin), steamId).String),
                };
            }));
        });

        // POST /test-skin-request?steamId=&text= - the client's inventory request as if steamId sent it (SE-1589):
        // the game's own RequestUpdateClientGameInventory handler with the text's bytes as the blob, so whatever
        // the cluster patched into it runs. A node forwards the blob to the Gateway, which logs skins_check.
        TestRoutes.Add("POST", "/test-skin-request", context =>
        {
            ulong steamId = ulong.Parse(TestRoutes.Query(context, "steamId"), CultureInfo.InvariantCulture);
            byte[] data = Encoding.UTF8.GetBytes(TestRoutes.Query(context, "text"));
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                using (MyEventContext.Set(new EndpointId(steamId), null, false))
                    MySessionComponentGameInventory.RequestUpdateClientGameInventory(data);
                return new { steamId, bytes = data.Length };
            }));
        });

        // POST /test-skin-grant?steamId=&skins=a,b - stands in for the Gateway's Steam answer: the skins this node
        // holds for an attached player, as a confirmed skins_check leaves them.
        TestRoutes.Add("POST", "/test-skin-grant", context =>
        {
            ulong steamId = ulong.Parse(TestRoutes.Query(context, "steamId"), CultureInfo.InvariantCulture);
            string[] names = TestRoutes.Query(context, "skins", "").Split(',', System.StringSplitOptions.RemoveEmptyEntries);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
                var attached = (IDictionary)typeof(PlayerEntitlements).GetField("Attached", any | BindingFlags.Static).GetValue(null);
                object entry = attached[steamId];
                if (entry == null) return (object)new { steamId, attached = false };
                var skins = new HashSet<MyStringHash>(names.Select(MyStringHash.GetOrCompute));
                // A field since SE-1589's main, an init-only record property before it (0.3.17).
                FieldInfo field = entry.GetType().GetField("Skins", any | BindingFlags.Instance);
                if (field != null) field.SetValue(entry, skins);
                else entry.GetType().GetProperty("Skins", any | BindingFlags.Instance).SetValue(entry, skins);
                return new { steamId, attached = true, skins = names };
            }));
        });

        // GET /test-block-skin?gridId=&x=&y=&z= - the skin and colour of a block as this node holds it.
        TestRoutes.Add("GET", "/test-block-skin", context =>
        {
            long gridId = TestRoutes.Long(context, "gridId");
            var position = new Vector3I(int.Parse(TestRoutes.Query(context, "x"), CultureInfo.InvariantCulture),
                int.Parse(TestRoutes.Query(context, "y"), CultureInfo.InvariantCulture),
                int.Parse(TestRoutes.Query(context, "z"), CultureInfo.InvariantCulture));
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var block = (MyEntities.GetEntityById(gridId) as MyCubeGrid)?.GetCubeBlock(position);
                return new { gridId, found = block != null, skin = block?.SkinSubtypeId.String, hsv = block == null ? null : new[] { block.ColorMaskHSV.X, block.ColorMaskHSV.Y, block.ColorMaskHSV.Z } };
            }));
        });
    }
}
