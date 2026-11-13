using System;
using System.IO;
using HarmonyLib;
using ClusterPlugin;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;

// Define assembly version when compiled by Magnetar
#if !LOCAL_BUILD
using System.Reflection;

[assembly: AssemblyVersion("0.3.12.0")]
[assembly: AssemblyFileVersion("0.3.12.0")]
#endif

namespace ClusterNodePlugin;

/// <summary>
/// Magnetar plugin turning this dedicated server into a regular cluster node: it hosts partitions
/// handed to it by the Gateway, serves the loopback control endpoint and follows the World Authority.
/// </summary>
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, ICommonPlugin
{
    public const string Name = "ClusterNode";
    public const ClusterRole Role = ClusterRole.Regular;
    public static Plugin Instance { get; private set; }

    public long Tick { get; private set; }
    private static bool failed;

    public IPluginLogger Log => Logger;
    internal static readonly IPluginLogger Logger = new PluginLogger(Name);

    public IPluginConfig Config => config?.Data;
    private PersistentConfig<PluginConfig> config;
    private static readonly string ConfigFileName = $"{Name}.cfg";

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        Instance = this;

        Log.Info("Loading");

        var configPath = Path.Combine(MyFileSystem.UserDataPath, ConfigFileName);
        config = PersistentConfig<PluginConfig>.Load(Log, configPath);

        var gameVersion = MyFinalBuildConstants.APP_VERSION_STRING.ToString();
        Common.SetPlugin(this, Name, gameVersion, MyFileSystem.UserDataPath);

        // Both cluster plugins may be enabled in one Magnetar profile; the process role selected
        // by CLUSTER_NODE_ROLE decides which one drives this process. The other one stays inert.
        if (!ClusterPluginHost.Claim(Role, Log))
            return;

        if (ClusterPluginHost.Preloaded)
        {
            // Preloader.Finish already patched the game and armed the runtime start on
            // DedicatedServer.Run, ahead of the dedicated config, the world and the network.
            Log.Info("Successfully loaded (preloaded)");
            return;
        }

        // No preloader hook ran (a launcher without Pulsar's preloader): patch and start now. This is
        // after the session loaded, so the patches on startup paths did not take effect this run.
        Log.Warning("Late start: the preloader hook did not run; startup-path patches are inactive");
        var harmony = new Harmony(Name);
        if (!PatchHelpers.HarmonyPatchAll(Log, harmony))
        {
            failed = true;
            ClusterPluginHost.Fail("Harmony patching failed");
            return;
        }

        try
        {
            ClusterPluginHost.Start(Role, Log, harmony);
        }
        catch (Exception e)
        {
            failed = true;
            Log.Critical(e, "Cluster startup failed");
            ClusterPluginHost.Fail(e.Message);
            return;
        }

        Log.Info("Successfully loaded");
    }

    public void Dispose()
    {
        try
        {
            ClusterPluginHost.Dispose(Role);
        }
        catch (Exception ex)
        {
            Log.Critical(ex, "Dispose failed");
            ClusterPluginHost.Fail("dispose_failed: " + ex.Message);
        }

        Instance = null;
    }

    public void Update()
    {
        if (failed || !ClusterPluginHost.Owns(Role))
            return;

        try
        {
            ClusterPluginHost.Update();
            PatchHelpers.PatchUpdates();
            Tick++;
        }
        catch (Exception e)
        {
            Log.Critical(e, "Update failed");
            ClusterPluginHost.Fail("update_failed: " + e.Message);
            failed = true;
        }
    }
}
