using HarmonyLib;
using VRage.Plugins;

namespace ModStaging;

/// <summary>
/// Loads a standalone test server's world mods from its pre-staged Mods folder, the way every
/// cluster process does, by applying the cluster's own LocalWorldModsPatch. Nothing else.
/// </summary>
public sealed class Plugin : IPlugin
{
    private readonly Harmony harmony = new("cometworks.cluster-test.mod-staging");

    public void Init(object gameInstance) =>
        harmony.CreateClassProcessor(typeof(ClusterPlugin.LocalWorldModsPatch)).Patch();

    public void Update() { }

    public void Dispose() => harmony.UnpatchAll(harmony.Id);
}
