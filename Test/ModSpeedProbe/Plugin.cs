using System;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.World;
using VRage.Plugins;
using VRage.Utils;

namespace ModSpeedProbe;

public sealed class Plugin : IPlugin
{
    private bool recorded;
    private Harmony staging;

    public void Init(object gameInstance)
    {
        if (Environment.GetEnvironmentVariable("MOD_SPEED_STAGE_STOCK") == "1")
        {
            staging = new Harmony("cometworks.mod-speed-test.staging");
            staging.CreateClassProcessor(typeof(LocalWorldModsPatch)).Patch();
        }
    }

    public void Update()
    {
        if (recorded || MySession.Static?.Ready != true)
            return;
        var definition = MyDefinitionManager.Static.EnvironmentDefinition;
        MyLog.Default.WriteLineAndConsole(
            FormattableString.Invariant(
                $"mod_speed_probe large={definition.LargeShipMaxSpeed:R} small={definition.SmallShipMaxSpeed:R}"
            )
        );
        recorded = true;
    }

    public void Dispose()
    {
        if (staging != null)
            staging.UnpatchAll(staging.Id);
    }
}
