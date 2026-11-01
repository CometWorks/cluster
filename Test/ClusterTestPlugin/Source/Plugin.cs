using System;
using System.Reflection;
using ClusterPlugin;
using VRage.Plugins;
using VRage.Utils;

namespace ClusterTestPlugin;

/// <summary>
/// Test utility plugin: registers the test-only control routes on the cluster plugin it was built
/// against (ClusterNode or WorldAuthority). It declares that plugin as a dependency, so the
/// control server type is loaded when <see cref="Init"/> runs; the server only starts serving
/// later, when the session is up.
/// </summary>
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public static readonly string Name = Assembly.GetExecutingAssembly().GetName().Name;

    public void Init(object gameInstance)
    {
        TestRoutes.RegisterAll();
        ClusterControlServer.AddRouteExtension(TestRoutes.Dispatch);
        // The mod-side harness has to register where a mod would: at session init, not here - it needs
        // MyAPIUtilities.Static, which does not exist yet. ClusterRuntime raises this seam at the end
        // of its service installs and never sets it itself.
        ClusterSeams.OnServicesInstalled(ModChannelTestHarness.Install);
        Log($"registered {TestRoutes.Count} test routes on the {typeof(ClusterControlServer).Assembly.GetName().Name} control server");
    }

    public void Update()
    {
        FastTestHarness.Update();   // T-0292 speed drives
    }

    public void Dispose()
    {
    }

    internal static void Log(string message)
    {
        MyLog.Default?.WriteLineAndConsole($"[{Name}] {message}");
    }
}
