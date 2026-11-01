using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using ClusterProtocol;
using HarmonyLib;
using Shared.Logging;
using Shared.Patches;
using VRage.Dedicated;
using VRage.FileSystem;

namespace ClusterPlugin;

public enum ClusterRole
{
    Regular,
    WorldAuthority,
}

/// <summary>
/// Process-wide rendezvous of the two cluster plugins. Exactly one of them drives the process:
/// the one whose role matches CLUSTER_NODE_ROLE (Regular when unset). The other one logs that it
/// is idle and does nothing, so both may be enabled in the same Magnetar profile.
///
/// Startup order under Magnetar: the plugin's <c>Preloader.Finish</c> hook calls <see cref="Preload"/>
/// before the dedicated server's Main runs. That applies every Harmony patch and arms a prefix on
/// <see cref="DedicatedServer.Run"/>, which starts the cluster runtime after MyVRageWindows.Init and
/// InitMultithreading, before the dedicated config, the world and the network. <c>IPlugin.Init</c> comes only after the session loaded and
/// is a fallback for launchers without a preloader.
/// </summary>
public static class ClusterPluginHost
{
    private static readonly object Sync = new object();
    private static ClusterRole? owner;
    private static bool started;
    private static ClusterRole preloadedRole;
    private static IPluginLogger preloadedLog;
    private static Harmony preloadedHarmony;

    /// <summary>True once <see cref="Preload"/> patched the game and armed the runtime start.</summary>
    public static bool Preloaded { get; private set; }

    public static bool Owns(ClusterRole role) => owner == role;

    public static bool Claim(ClusterRole role, IPluginLogger log)
    {
        // Reflection here avoids JIT references to SDK types absent from older launchers.
        var sdk = Assembly.Load("PluginSdk");
        string contract = null;
        foreach (var metadata in sdk.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (metadata.Key == PluginSdkContract.Key) contract = metadata.Value;
        if (contract != null && !PluginSdkContract.IsSatisfiedBy(contract))
            throw new InvalidOperationException($"Cluster needs PluginSdk contract {PluginSdkContract.Required}, but Magnetar has {contract}. Use a Magnetar and a cluster release with compatible PluginSdk contracts.");
        // Magnetar releases before the contract: the version and the types tell.
        if ((contract == null && sdk.GetName().Version < new Version(2, 4, 2, 1))
            || sdk.GetType("PluginSdk.Config.ManagedPluginConfiguration") == null
            || sdk.GetType("PluginSdk.Clustering.PluginCluster") == null)
            throw new InvalidOperationException("Cluster 1.1.0 requires Magnetar 2.4.2.1 or newer. Upgrade Magnetar before loading cluster plugins.");
        ClusterRole processRole = ClusterNodeRole.IsWorldAuthority(Environment.GetEnvironmentVariable("CLUSTER_NODE_ROLE"))
            ? ClusterRole.WorldAuthority
            : ClusterRole.Regular;
        lock (Sync)
        {
            if (processRole != role)
            {
                if (owner != role)
                    log.Info($"This process runs as {processRole} (CLUSTER_NODE_ROLE); the {role} plugin stays idle");
                return false;
            }
            if (owner != null && owner != role)
                throw new InvalidOperationException($"Cluster role {owner} already claimed this process");
            owner = role;
            return true;
        }
    }

    /// <summary>Preloader post-hook entry: claim the role, patch the game, arm the start.</summary>
    public static void Preload(ClusterRole role, string pluginName, IPluginLogger log)
    {
        if (!Claim(role, log))
            return;
        lock (Sync)
        {
            if (Preloaded)
                return;
        }
        var harmony = new Harmony(pluginName);
        if (!PatchHelpers.HarmonyPatchAll(log, harmony))
        {
            Fail("Harmony patching failed");
            throw new InvalidOperationException("Harmony patching failed");
        }
        MethodInfo run = AccessTools.Method(typeof(DedicatedServer), nameof(DedicatedServer.Run), new[] { typeof(string[]), typeof(Action<bool>) });
        if (run == null)
            throw new MissingMethodException("VRage.Dedicated.DedicatedServer.Run(string[], Action<bool>) not found");
        harmony.Patch(run, prefix: new HarmonyMethod(typeof(ClusterPluginHost), nameof(RunPrefix)));
        lock (Sync)
        {
            preloadedRole = role;
            preloadedLog = log;
            preloadedHarmony = harmony;
            Preloaded = true;
        }
        log.Info("Preloaded: game patched, cluster runtime starts with DedicatedServer.Run");
    }

    // Harmony prefix on DedicatedServer.Run: the cluster runtime's start point in MyProgram.Main.
    private static void RunPrefix()
    {
        try
        {
            Start(preloadedRole, preloadedLog, preloadedHarmony);
        }
        catch (Exception e)
        {
            preloadedLog.Critical(e, "Cluster startup failed");
            Fail(e.Message);
            throw;
        }
    }

    public static void Start(ClusterRole role, IPluginLogger log, Harmony harmony)
    {
        lock (Sync)
        {
            if (!Owns(role) || started) return;
            started = true;
        }
        // The Gateway node link lives on the Direct Transport plugin's UdpPeer2Peer, an assembly with a
        // random name under Magnetar: manual Harmony patches bound at runtime, before the runtime that
        // sends global frames over it starts. Absent transport: warning when standalone, exception when
        // clustered (see NodeLinkTransportBinding.Install).
        NodeLinkTransportBinding.Install(harmony, log);
        ClusterRuntime.StartFromEnvironment();
        if (!ClusterRuntime.IsClustered)
            log.Warning("CLUSTER_CONTROL_PORT or CLUSTER_GATEWAY_REGISTRY is not set; running as a standalone server");
        else
            log.Info($"Cluster runtime started as {role}");
    }

    public static void Fail(string reason)
    {
        ClusterRuntime.SetNodeState("Failed", reason);
        PluginSdk.Logging.Logger.Create("Cluster").Error("Cluster node entered Failed: " + reason,
            new { eventCode = "node_failed", reasonCode = reason, phase = "runtime" });
    }

    public static void Update() => ClusterRuntime.Tick();

    public static void Dispose(ClusterRole role)
    {
        if (!Owns(role)) return;
        PluginServices.Instance?.Dispose();
    }

    /// <summary>
    /// The game assemblies that define the replicated type table, in the order hashed by
    /// <see cref="ReplicationTableHash"/>. The cluster CLI computes the same digest over the same
    /// files of the DedicatedServer64 directory for the Gateway's CLUSTER_REPLICATION_TABLE_HASH.
    /// </summary>
    public static readonly string[] ReplicationAssemblies =
    {
        "Sandbox.Common.dll", "Sandbox.Game.dll", "SpaceEngineers.Game.dll", "SpaceEngineers.ObjectBuilders.dll",
        "VRage.dll", "VRage.Game.dll", "VRage.Library.dll",
    };

    /// <summary>
    /// Digest the Gateway compares at node admission. Stock binaries build the replication type table
    /// from the game assemblies, so their bytes stand in for it (a ReplicatedTypes.json is hashed
    /// instead when present).
    /// </summary>
    public static string ReplicationTableHash()
    {
        string json = Path.Combine(AppContext.BaseDirectory, "ReplicatedTypes.json");
        if (File.Exists(json))
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(json)));
        string directory = MyFileSystem.ExePath;
        if (string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, ReplicationAssemblies[1])))
            directory = AppContext.BaseDirectory;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string name in ReplicationAssemblies)
            sha.AppendData(File.ReadAllBytes(Path.Combine(directory, name)));
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
