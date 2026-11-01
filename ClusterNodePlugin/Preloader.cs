// IMPORTANT: no namespace. Pulsar/Magnetar looks this class up by its bare name ("Preloader")
// in every plugin assembly and calls the public static hooks it finds.

// ReSharper disable once CheckNamespace
// ReSharper disable once UnusedType.Global
public static class Preloader
{
    // Post-hook: runs in the launcher after the game assemblies are resolvable and every Cecil
    // preloader patch (linux-compat, Direct Transport) is applied, but BEFORE the dedicated
    // server's Main. IPlugin.Init would come after the session has loaded, which is too late for
    // a cluster node: the runtime must be up before the dedicated config, the world and the
    // network are.
    // ReSharper disable once UnusedMember.Global
    public static void Finish()
    {
        ClusterPlugin.ClusterPluginHost.Preload(ClusterNodePlugin.Plugin.Role, ClusterNodePlugin.Plugin.Name, ClusterNodePlugin.Plugin.Logger);
    }
}
