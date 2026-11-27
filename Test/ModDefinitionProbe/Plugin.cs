using System;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Plugins;
using VRage.Utils;

namespace ModDefinitionProbe;

public sealed class Plugin : IPlugin
{
    private bool recorded;

    public void Init(object gameInstance) { }

    public void Update()
    {
        if (recorded || MySession.Static?.Ready != true)
            return;
        var definitions = MyDefinitionManager.Static;
        FormattableString sample;
        if (Environment.GetEnvironmentVariable("MOD_DEFINITION_CASE") == "sound")
        {
            float Volume(string subtype)
            {
                var sound = definitions.GetSoundDefinition(MyStringHash.TryGet(subtype));
                return (sound?.GetObjectBuilder() as MyObjectBuilder_AudioDefinition)?.Volume
                    ?? float.NaN;
            }

            var rain = definitions
                .GetWeatherDefinitions()
                .FirstOrDefault(w => w.Id.SubtypeName == "RainHeavy");
            sample =
                $"welder={Volume("ArcToolPlayWeldIdle"):R} drill={Volume("ArcToolPlayDrillMetal"):R} rain={rain?.AmbientVolume:R}";
        }
        else
        {
            var env = definitions.EnvironmentDefinition;
            sample = $"large={env.LargeShipMaxSpeed:R} small={env.SmallShipMaxSpeed:R}";
        }
        MyLog.Default.WriteLineAndConsole(
            "mod_definition_probe " + FormattableString.Invariant(sample)
        );
        recorded = true;
    }

    public void Dispose() { }
}
