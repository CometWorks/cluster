using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>Runtime-issued Registry incarnation bound to the executor's exact launch attempt.</summary>
internal static class NodeProcessIdentity
{
    private static bool registered;
    private static volatile bool ready;
    private static long nextCheck;
    private static volatile string failure;

    internal static string Revision => Environment.GetEnvironmentVariable("CLUSTER_DEPLOYMENT_REVISION");
    internal static bool Verified => ready;
    internal static string Failure => failure;

    internal static void Registered()
    {
        Publish(false);
        registered = true;
    }

    internal static void Ready()
    {
        if (!registered || Stopwatch.GetTimestamp() < nextCheck) return;
        nextCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        if (string.IsNullOrEmpty(Revision)) return;
        bool valid = PluginServices.Instance != null
            && ReferenceEquals(PluginSdk.Clustering.PluginCluster.Current, PluginServices.Instance)
            && PluginSdk.Config.ManagedPluginConfiguration.Required
            && PluginSdk.Config.ManagedPluginConfiguration.Revision == Environment.GetEnvironmentVariable("CLUSTER_DEPLOYMENT_REVISION")
            && PluginSdk.Config.ManagedPluginConfiguration.Observe();
        string observedFailure = valid ? null : "configuration_drift:" +
            (PluginSdk.Config.ManagedPluginConfiguration.Failure ?? "canonical initialization proof is unavailable");
        if (ready == valid && failure == observedFailure) return;
        ready = valid;
        failure = observedFailure;
        Publish(valid);
    }

    /// <summary>
    /// Records the exit code in the receipt right before the plugin ends the process, so the Host can
    /// tell a finished drain (0) from a crash. Best effort: nothing may stop the exit.
    /// </summary>
    internal static void Exiting(int code)
    {
        if (!registered) return;
        try { Publish(ready, code); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private static void Publish(bool gameReady, int? exitCode = null)
    {
        string path = Environment.GetEnvironmentVariable("CLUSTER_PROCESS_IDENTITY_PATH");
        if (string.IsNullOrEmpty(path)) return; // Standalone packaged launcher needs no Host receipt.
        string Required(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(name + " is required for managed admission");
            return value;
        }
        if (!Path.IsPathFullyQualified(path) || ClusterRuntime.RegistryNodeEpoch <= 0)
            throw new InvalidOperationException("Managed identity path or Registry incarnation is invalid");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            clusterId = Required("CLUSTER_ID"), slotKey = Required("CLUSTER_SLOT_ID"),
            attemptKey = Required("CLUSTER_LAUNCH_ATTEMPT"), nodeId = Required("CLUSTER_NODE_ID"),
            epoch = ClusterRuntime.RegistryNodeEpoch, endpoint = Required("CLUSTER_NODE_ENDPOINT"),
            processId = Process.GetCurrentProcess().Id,
            deploymentRevision = Required("CLUSTER_DEPLOYMENT_REVISION"), ready = gameReady, failure, exitCode,
        });
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
