using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game.Components;
using VRage.Game.Entity;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1133 bench: a block's maintenance panels (LargeCrate). <c>/panel-test?block=</c> reports each panel's live status,
/// whether its saved state is still pending (Deserialize kept the builder, the next-frame update has not applied it) and
/// the Open its component serializes; <c>&amp;open=SUBTYPE</c> (POST) first opens that panel the way a player's toggle
/// does on the server.
/// </summary>
internal static class PanelTestRoutes
{
    public static void Register()
    {
        TestRoutes.Add("*", "/panel-test", context =>
        {
            long block = TestRoutes.Long(context, "block");
            string open = context.Request.QueryString["open"];
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Panels(block, open)));
        });
    }

    private static object Panels(long blockId, string open)
    {
        if (!MyEntities.TryGetEntityById(blockId, out MyEntity entity) || entity.MarkedForClose)
            return new { found = false, panels = Array.Empty<object>() };
        var panels = new List<MyMaintenancePanelComponent>();
        foreach (MyComponentBase component in (MyComponentContainer)entity.Components)
            if (component is MyMaintenancePanelComponent panel) panels.Add(panel);
        if (!string.IsNullOrEmpty(open))
        {
            MyMaintenancePanelComponent panel = panels.FirstOrDefault(candidate => candidate.SubType.String == open)
                ?? throw new ArgumentException("No maintenance panel " + open + " on block " + blockId);
            panel.OpenCloseToggle();
        }
        return new
        {
            found = true,
            panels = panels.Select(panel => new
            {
                subtype = panel.SubType.String,
                status = panel.Status.ToString(),
                pending = panel.m_builderToInitFrom != null,
                serializedOpen = ((MyObjectBuilder_MaintenancePanelComponent)panel.Serialize()).Open,
            }).ToArray(),
        };
    }
}
