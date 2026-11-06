using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// The test-only route table. A route matches like the product's: the HTTP method, and the trimmed
/// request path ending with the route's suffix. Handlers write their own response; an exception
/// propagates to the control server, which answers it the same way it answers a product route
/// (401, 404 for a not-found exception, otherwise 500 with the exception text).
/// </summary>
internal static class TestRoutes
{
    private static readonly List<(string Method, string Suffix, Action<HttpListenerContext, string> Handler)> Routes = new();

    public static int Count => Routes.Count;

    public static void Add(string method, string suffix, Action<HttpListenerContext> handler)
    {
        Routes.Add((method, suffix, (context, _) => handler(context)));
    }

    /// <summary>A handler that also takes the trimmed request path (the moved product arms use it).</summary>
    public static void Add(string method, string suffix, Action<HttpListenerContext, string> handler)
    {
        Routes.Add((method, suffix, handler));
    }

    /// <summary>Every route group of this plugin. A lane's routes are added here as it is ported.</summary>
    public static void RegisterAll()
    {
        Routes.Clear();
        Add("GET", "/test-plugin", context => Json(context, 200, new
        {
            plugin = Plugin.Name,
            clusterAssembly = typeof(ClusterControlServer).Assembly.GetName().Name,
            routes = Routes.Select(route => route.Method + " " + route.Suffix).ToArray(),
        }));
        TrashRoutes.Register();
        ShipAntennaTestRoutes.Register();
        MovedRoutes.Register();
        PbTestHarness.Register();
        GpsTestRoutes.Register();   // #1405 GPS requests and table digests per node
        DuplicateTestHarness.Register();
        PluginStorageRoutes.Register();
        PluginBroadcastRoutes.Register();
        PluginViewRoutes.Register();
        PluginHandoverRoutes.Register();
        PluginOfflineRoutes.Register();
        PluginClockRoutes.Register();
        PluginEventRoutes.Register();
        PluginGlobalRoutes.Register();
        FastTestHarness.Register();   // T-0292
        WeakRefRoutes.Register();     // T-0274 closed-entity reachability oracle
        PropertySyncRoutes.Register();  // #1376 null emotion controller Syncs in state sync
        VoxelCarryRoutes.Register();  // T-0368 asteroid carried across a handover
        StreamHoldRoutes.Register();  // T-0396 a grid held mid-stream to one client
        BroadcastChatRoutes.Register(); // SE-943 broadcast controller chat and the chat a node relays
        PanelTestRoutes.Register();   // SE-1133 maintenance panels open across a handover
        DriftTestRoutes.Register();   // SE-1152..1155 benign drift the round-trip gate rejected
        BlueprintTestRoutes.Register(); // T-0401 parked ship pastes (Visitor Ion) for the Add bench
        BlockCoverageHarness.Register(); // SE1-0163 one fixture per vanilla functional block type
        ContractTestRoutes.Register(); // SE1-0232 contract offers across handover and restart
        GridDeleteFaultRoutes.Register(); // #1295 a grid whose BeforeDelete throws in a cluster close
        EmptyReleaseStallRoutes.Register(); // SE-897 a slow frame right after an admin delete empties a partition
        CommandResponseDropRoutes.Register(); // #1271 World Authority answers lost on the way back
        EarlyMirrorPassHarness.Register(); // SE-732 a member message sent before the world request
        GlobalSaveHoldRoutes.Register(); // #1270 a global save held before its registration
        NpcCensusRoutes.Register();   // #1354 the NPC census slot as mod-mes calls it, and a stand-in gap band
        MeteorRoutes.Register();      // #1381 a meteor probe and its carried lifetime
        OfflineTestRoutes.Register(); // SE-347 shared LoadRecovery apply/crash boundary and real production
        RespawnFaultRoutes.Register(); // SE-1124 a split or a refusal at a respawn switch target
        StaleMemberRoutes.Register(); // SE-1156 a member with no session, injected or through the leave-before-login race
        AccountingInterlockObserver.Register();
        GameThreadLoadRoutes.Register(); // SE-1134 real game-thread time: a busy node, a single hitch
        EntitlementRoutes.Register(); // SE-1158 DLC and skin ownership as the game checks it
    }

    public static bool Dispatch(HttpListenerContext context, string path)
    {
        string method = context.Request.HttpMethod;
        foreach ((string Method, string Suffix, Action<HttpListenerContext, string> Handler) route in Routes)
        {
            if ((route.Method == "*" || route.Method == method) && path.EndsWith(route.Suffix, StringComparison.OrdinalIgnoreCase))
            {
                route.Handler(context, path);
                return true;
            }
        }
        return false;
    }

    // -- response and request helpers ---------------------------------------------------------

    public static void Json(HttpListenerContext context, int status, object value)
    {
        Write(context, status, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
    }

    public static void Text(HttpListenerContext context, int status, string text)
    {
        Write(context, status, "text/plain", Encoding.UTF8.GetBytes(text));
    }

    public static void Write(HttpListenerContext context, int status, string contentType, byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.Close();
    }

    public static string Body(HttpListenerContext context)
    {
        using StreamReader reader = new StreamReader(context.Request.InputStream);
        return reader.ReadToEnd();
    }

    public static string Query(HttpListenerContext context, string name, string fallback = null)
    {
        return context.Request.QueryString[name] ?? fallback ?? throw new ArgumentException("Missing " + name + " query parameter");
    }

    public static double Double(HttpListenerContext context, string name)
    {
        return double.Parse(Query(context, name), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A non-zero integer query parameter, like the product's QueryLong.</summary>
    public static long Long(HttpListenerContext context, string name)
    {
        if (!long.TryParse(context.Request.QueryString[name], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long value) || value == 0)
            throw new ArgumentException("Invalid " + name + " query parameter");
        return value;
    }

    /// <summary>Run on the game thread through the control server's own dispatcher (30 s limit).</summary>
    public static T OnGameThread<T>(Func<T> action) => ClusterControlServer.Invoke(action);
}
