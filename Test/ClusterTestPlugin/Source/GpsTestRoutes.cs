using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// #1405: GPS requests through the vanilla Send*Request calls (a regular node forwards them to the World Authority,
/// the WA applies them), and this process's GPS table, so a scenario can compare every node's table with the WA's.
/// </summary>
internal static class GpsTestRoutes
{
    public static void Register()
    {
        // POST /gps-test-op?op=add|modify|delete&identity=N&name=S[&newName=S][&x=D][&description=S]
        // The entry is found by name in this process's table (modify, delete); returns its hash before and after.
        TestRoutes.Add("POST", "/gps-test-op", context =>
        {
            var query = context.Request.QueryString;
            string op = query["op"] ?? throw new ArgumentException("Missing op");
            long identityId = long.Parse(query["identity"] ?? throw new ArgumentException("Missing identity"), CultureInfo.InvariantCulture);
            string name = query["name"] ?? throw new ArgumentException("Missing name");
            double x = double.Parse(query["x"] ?? "0", CultureInfo.InvariantCulture);
            string description = query["description"] ?? string.Empty;
            var result = TestRoutes.OnGameThread<object>(() =>
            {
                var gpss = MySession.Static.Gpss;
                MyGps existing = Find(identityId, name);
                switch (op)
                {
                    case "add":
                    {
                        var gps = new MyGps { Name = name, Description = description, Coords = new Vector3D(x, 2000, 3000), ShowOnHud = false, DiscardAt = null };
                        gps.UpdateHash();
                        gpss.SendAddGpsRequest(identityId, ref gps, 0, playSoundOnCreation: false);
                        return new { op, identityId, name, hash = gps.Hash };
                    }
                    case "modify":
                    {
                        if (existing == null) throw new InvalidOperationException($"no GPS '{name}' for identity {identityId}");
                        // A copy keyed by the old hash, as the vanilla GPS screen sends it: the request must not change
                        // this process's own table, which is the WA's replica on a regular node.
                        var gps = new MyGps { Name = existing.Name, Description = existing.Description, Coords = existing.Coords, ShowOnHud = existing.ShowOnHud,
                            GPSColor = existing.GPSColor, DiscardAt = null };
                        gps.UpdateHash();
                        gps.Name = query["newName"] ?? existing.Name;
                        gps.Description = query["description"] ?? existing.Description;
                        gps.Coords = query["x"] != null ? new Vector3D(x, existing.Coords.Y, existing.Coords.Z) : existing.Coords;
                        gpss.SendModifyGpsRequest(identityId, gps);
                        return new { op, identityId, name, hash = existing.Hash };
                    }
                    case "delete":
                        if (existing == null) throw new InvalidOperationException($"no GPS '{name}' for identity {identityId}");
                        gpss.SendDeleteGpsRequest(identityId, existing.Hash);
                        return new { op, identityId, name, hash = existing.Hash };
                    default: throw new ArgumentException("op must be add, modify or delete");
                }
            });
            TestRoutes.Json(context, 200, result);
        });

        // GET /gps-test-table[?prefix=S] - this process's non-local GPS entries whose name starts with prefix, sorted,
        // a SHA-256 of all of them (prefix or not), and the first identities (for a scenario to put entries on).
        TestRoutes.Add("GET", "/gps-test-table", context =>
        {
            string prefix = context.Request.QueryString["prefix"];
            var result = TestRoutes.OnGameThread(() =>
            {
                var all = new List<(long Identity, int Hash, string Name, string Description, Vector3D Coords, uint Color, bool ShowOnHud, bool Final)>();
                foreach (long identityId in MySession.Static.Players.GetAllIdentities().Select(x => x.IdentityId).Append(0))
                {
                    var list = new List<IMyGps>();
                    MySession.Static.Gpss.GetGpsList(identityId, list);
                    foreach (MyGps gps in list.Cast<MyGps>().Where(g => !g.IsLocal))
                        all.Add((identityId, gps.Hash, gps.Name, gps.Description, gps.Coords, gps.GPSColor.PackedValue, gps.ShowOnHud, !gps.DiscardAt.HasValue));
                }
                all = all.OrderBy(e => e.Identity).ThenBy(e => e.Hash).ToList();
                string canonical = string.Join("\n", all.Select(e => string.Join("|", e.Identity, e.Hash, e.Name, e.Description,
                    e.Coords.X.ToString("R", CultureInfo.InvariantCulture), e.Coords.Y.ToString("R", CultureInfo.InvariantCulture),
                    e.Coords.Z.ToString("R", CultureInfo.InvariantCulture), e.Color, e.ShowOnHud, e.Final)));
                return new
                {
                    node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                    count = all.Count,
                    sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
                    identities = MySession.Static.Players.GetAllIdentities().Select(x => x.IdentityId).OrderBy(x => x).Take(8).ToArray(),
                    entries = all.Where(e => prefix != null && e.Name != null && e.Name.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(e => new { identity = e.Identity, hash = e.Hash, name = e.Name, description = e.Description, x = e.Coords.X }).ToArray(),
                };
            });
            TestRoutes.Json(context, 200, result);
        });
    }

    private static MyGps Find(long identityId, string name)
    {
        var list = new List<IMyGps>();
        MySession.Static.Gpss.GetGpsList(identityId, list);
        return list.Cast<MyGps>().FirstOrDefault(g => !g.IsLocal && g.Name == name);
    }
}
