using System;
using HarmonyLib;
using VRage.Plugins;
using VRage.Utils;

#if !LOCAL_BUILD
using System.Reflection;
[assembly: AssemblyVersion("0.2.2.0")]
[assembly: AssemblyFileVersion("0.2.2.0")]
#endif

namespace ClusterClientDiagnostics;

public sealed class Plugin : IPlugin
{
    public static Plugin Instance { get; private set; }
    internal DiagnosticsClient Diagnostics { get; } = new DiagnosticsClient();
    private bool failed;

    public void Init(object gameInstance)
    {
        Instance = this;
        try { new Harmony("CometWorks.ClusterDiagnostics").PatchAll(); }
        catch (Exception error)
        {
            failed = true;
            MyLog.Default?.WriteLine("[ClusterDiagnostics] Could not install F11 hooks: " + error);
        }
    }

    public void Update()
    {
        if (failed) return;
        try { Diagnostics.Update(); }
        catch (Exception error)
        {
            failed = true;
            Diagnostics.Dispose();
            MyLog.Default?.WriteLine("[ClusterDiagnostics] Update failed: " + error);
        }
    }

    public void OpenConfigDialog() => DiagnosticsMenuPatch.Open();

    public void Dispose()
    {
        Diagnostics.Dispose();
        Instance = null;
    }
}
