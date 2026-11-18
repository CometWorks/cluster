using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClusterPlugin;
using Sandbox.Game.Entities;
using VRage.Library.Collections;
using VRage.Serialization;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1596: the cluster-wide Alt-F10 Entity List and PCU tab as the admin's node runs them, without a client: the same
/// service calls the patched event handlers make, with what the client would receive and what it cost.
/// </summary>
internal static class AdminMenuRoutes
{
    public static void Register()
    {
        // POST /admin-menu-spawn?count=N&x=&y=&z=[&spacing=m][&blocks=n][&npc=true] - N static probes in a cube lattice
        TestRoutes.Add(
            "POST",
            "/admin-menu-spawn",
            context =>
            {
                int count = int.Parse(
                    TestRoutes.Query(context, "count"),
                    CultureInfo.InvariantCulture
                );
                double x = TestRoutes.Double(context, "x");
                double y = TestRoutes.Double(context, "y");
                double z = TestRoutes.Double(context, "z");
                double spacing = double.TryParse(
                    context.Request.QueryString["spacing"],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double given
                )
                    ? given
                    : 12;
                bool npc = context.Request.QueryString["npc"] == "true";
                int blocks = int.TryParse(
                    context.Request.QueryString["blocks"],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int givenBlocks
                )
                    ? givenBlocks
                    : 1;
                int edge = (int)Math.Ceiling(Math.Pow(count, 1.0 / 3.0));
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        var grids = new List<object>();
                        for (int i = 0; i < count; i++)
                            grids.Add(
                                TrashTestHarness.Spawn(
                                    x + i % edge * spacing,
                                    y + i / edge % edge * spacing,
                                    z + i / (edge * edge) * spacing,
                                    isStatic: true,
                                    blocks: blocks,
                                    npc: npc
                                )
                            );
                        return new { grids };
                    })
                );
            }
        );
        // GET /admin-menu-list?type=Grids[&match=prefix] - the merged list the admin would get, its stats, and the size
        // of the EntityListResponse argument as VRage serializes it. Rows only those whose name starts with match.
        TestRoutes.Add(
            "GET",
            "/admin-menu-list",
            context =>
            {
                var type = (MyEntityList.MyEntityTypeEnum)
                    Enum.Parse(
                        typeof(MyEntityList.MyEntityTypeEnum),
                        context.Request.QueryString["type"] ?? "Grids"
                    );
                string match = context.Request.QueryString["match"] ?? "";
                var watch = System.Diagnostics.Stopwatch.StartNew();
                List<MyEntityList.MyEntityListInfoItem> local = TestRoutes.OnGameThread(() =>
                    MyEntityList.GetEntityList(type)
                );
                long localMs = watch.ElapsedMilliseconds;
                AdminMenuService.ListResult result = AdminMenuService
                    .CollectListAsync(type, local)
                    .GetAwaiter()
                    .GetResult();
                List<MyEntityList.MyEntityListInfoItem> rows = result.Rows;
                using var stream = new BitStream();
                stream.ResetWrite();
                MySerializer.Write(stream, ref rows);
                TestRoutes.Json(
                    context,
                    200,
                    new
                    {
                        stats = result.Stats,
                        localMs,
                        clientBytes = stream.BytePosition,
                        incomplete = AdminMenuService.Incomplete(
                            result.Stats.Missing,
                            result.Stats.Truncated
                        ),
                        rows = rows.Where(row =>
                                (row.DisplayName ?? "").StartsWith(match, StringComparison.Ordinal)
                            )
                            .Select(row => new
                            {
                                id = row.EntityId,
                                name = row.DisplayName,
                                x = row.Position.X,
                                y = row.Position.Y,
                                z = row.Position.Z,
                            })
                            .ToArray(),
                    }
                );
            }
        );
        // POST /admin-menu-action?ids=1,2&action=Remove - the split a row action makes here and the forwarded outcome
        TestRoutes.Add(
            "POST",
            "/admin-menu-action",
            context =>
            {
                var ids = TestRoutes
                    .Query(context, "ids")
                    .Split(',')
                    .Select(id => long.Parse(id, CultureInfo.InvariantCulture))
                    .ToList();
                var action = (MyEntityList.EntityListAction)
                    Enum.Parse(
                        typeof(MyEntityList.EntityListAction),
                        context.Request.QueryString["action"] ?? "Remove"
                    );
                List<long> remote = TestRoutes.OnGameThread(() => AdminMenuService.TakeRemote(ids));
                string text =
                    remote.Count == 0
                        ? null
                        : AdminMenuService.ForwardAsync(remote, action).GetAwaiter().GetResult();
                TestRoutes.Json(
                    context,
                    200,
                    new
                    {
                        local = ids,
                        remote,
                        text,
                    }
                );
            }
        );
        // GET /admin-menu-grids?identity= - the PCU tab's list of an identity's grids
        TestRoutes.Add(
            "GET",
            "/admin-menu-grids",
            context =>
            {
                long identity = TestRoutes.Long(context, "identity");
                List<AdminMenuService.GridPcu> local = TestRoutes.OnGameThread(() =>
                    AdminMenuService.PlayerGrids(identity, ownedOnly: true)
                );
                AdminMenuService.GridsResult result = AdminMenuService
                    .CollectPlayerGridsAsync(identity, local)
                    .GetAwaiter()
                    .GetResult();
                TestRoutes.Json(context, 200, result);
            }
        );
        // GET /admin-menu-pcu - the PCU tab's totals: used, pirate used/available, global encounter used/available
        TestRoutes.Add(
            "GET",
            "/admin-menu-pcu",
            context =>
            {
                int[] local = TestRoutes.OnGameThread(AdminMenuService.PcuHere);
                var (sums, missing) = AdminMenuService
                    .CollectPcuAsync(local)
                    .GetAwaiter()
                    .GetResult();
                TestRoutes.Json(
                    context,
                    200,
                    new
                    {
                        local,
                        sums,
                        missing,
                    }
                );
            }
        );
        // GET /admin-menu-known?id= - where the last list put an entity this node doesn't have (the camera jump)
        TestRoutes.Add(
            "GET",
            "/admin-menu-known",
            context =>
            {
                long id = TestRoutes.Long(context, "id");
                bool found = AdminMenuService.TryKnown(id, out var position, out string node);
                TestRoutes.Json(
                    context,
                    200,
                    new
                    {
                        found,
                        node,
                        x = position.X,
                        y = position.Y,
                        z = position.Z,
                    }
                );
            }
        );
    }
}
