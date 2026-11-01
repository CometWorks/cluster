using System;
using System.Collections;
using System.IO;
using System.Reflection;
using PluginSdk.Clustering;
using PluginSdk.Storage;

namespace ClusterTestPlugin;

/// <summary>T-0256: PluginSdk's shared directory as a plugin on a node or the WA sees it.</summary>
internal static class PluginStorageRoutes
{
    public static void Register()
    {
        // GET /plugin-storage[?plugin=ID][&write=FILE&text=TEXT][&read=FILE] - the plugin's shared directory on
        // this process, optionally writing a file there (temp + rename) and reading one back. A scenario
        // writes from one node and reads from another and from the WA. The id defaults to the one the loader
        // bound this assembly to; any other id must be refused (the loader identity rule).
        TestRoutes.Add("GET", "/plugin-storage", context =>
        {
            string plugin = context.Request.QueryString["plugin"] ?? OwnId();
            string write = context.Request.QueryString["write"];
            string read = context.Request.QueryString["read"];
            string directory;
            try
            {
                directory = PluginStorage.GetSharedDirectory(plugin);
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                TestRoutes.Json(context, 200, new { error = error.GetType().Name, message = error.Message, plugin, ownId = OwnId(),
                    root = PluginStorage.Root, isClusterShared = PluginStorage.IsClusterShared });
                return;
            }
            if (!string.IsNullOrEmpty(write))
            {
                string target = Path.Combine(directory, Path.GetFileName(write));
                File.WriteAllText(target + ".tmp", context.Request.QueryString["text"] ?? "");
                File.Move(target + ".tmp", target, overwrite: true);
            }
            string content = null;
            if (!string.IsNullOrEmpty(read))
            {
                string source = Path.Combine(directory, Path.GetFileName(read));
                content = File.Exists(source) ? File.ReadAllText(source) : null;
            }
            TestRoutes.Json(context, 200, new
            {
                node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                plugin,
                ownId = OwnId(),
                root = PluginStorage.Root,
                isClusterShared = PluginStorage.IsClusterShared,
                directory,
                read = content,
            });
        });
        }

    /// <summary>The id the loader bound this assembly to (PluginCluster's owner table), or null when unbound.</summary>
    public static string OwnId()
    {
        var owners = typeof(PluginCluster).GetField("Owners", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary;
        if (owners == null) return null;
        lock (owners) return owners[typeof(PluginStorageRoutes).Assembly] as string;
    }
}
