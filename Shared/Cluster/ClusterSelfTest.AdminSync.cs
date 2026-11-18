using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VRage.Game;
using VRageMath;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// <summary>
    /// SE-1596: the trash settings the World Authority publishes survive the session-settings and JSON round trips
    /// unchanged, and applying them resets boulders exactly where vanilla's own upload does.
    /// </summary>
    private static void TrashSettingsRules()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("trash-settings: " + message);
        }
        var settings = new TrashSettingsService.Settings(
            (int)(MyTrashRemovalFlags.Fixed | MyTrashRemovalFlags.RevertBoulders),
            false,
            7,
            1234.5f,
            11,
            3.25f,
            9,
            4,
            48,
            true,
            2500f,
            1500f,
            12,
            45,
            60
        );
        var session = new MyObjectBuilder_SessionSettings();
        TrashSettingsService.Write(session, settings);
        Check(
            TrashSettingsService.Read(session) == settings,
            "a set read back from the session settings differs"
        );
        var set = new TrashSettingsService.Set(3, 5, settings);
        Check(
            JsonSerializer.Deserialize<TrashSettingsService.Set>(JsonSerializer.Serialize(set))
                == set,
            "a published set does not round-trip"
        );
        var plain = settings with { Flags = (int)MyTrashRemovalFlags.Fixed };
        Check(
            TrashSettingsService.RevertsBouldersAnew(plain, settings),
            "turning boulder reverting on must reset boulders"
        );
        Check(
            TrashSettingsService.RevertsBouldersAnew(settings, settings with { VoxelAge = 13 }),
            "a new voxel age while reverting boulders must reset boulders"
        );
        Check(
            !TrashSettingsService.RevertsBouldersAnew(settings, settings with { BlockCount = 8 }),
            "an unrelated change must not reset boulders"
        );
        Check(
            !TrashSettingsService.RevertsBouldersAnew(settings, plain with { VoxelAge = 13 }),
            "no reset while boulders are not reverted"
        );
    }

    /// <summary>
    /// SE-1596: a node tells an admin's weather change from the World Authority's state by what an admin sets, not by
    /// what runs on by itself, and the World Authority takes the node's weathers over while keeping its running copies.
    /// </summary>
    private static void WeatherChangeRules()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("weather-change: " + message);
        }
        WeatherAuthorityService.WeatherEffectState Effect(string weather, double x, int life) =>
            new(weather, x, 0, 0, 0, 0, 0, 5000, life, 3600, 1, x, 0, 0);
        WeatherAuthorityService.PlanetWeatherState Planet(
            long id,
            params WeatherAuthorityService.WeatherEffectState[] weathers
        ) => new(id, 100, weathers.ToList());

        var authority = new List<WeatherAuthorityService.PlanetWeatherState>
        {
            Planet(1, Effect("Rain", 10, 100)),
            Planet(2, Effect("Snow", 20, 100)),
        };
        // The same weathers, run on by a few seconds: no change.
        var ranOn = new List<WeatherAuthorityService.PlanetWeatherState>
        {
            Planet(1, Effect("Rain", 10, 220)),
            Planet(2, Effect("Snow", 20, 220)),
            Planet(3),
        };
        Check(
            WeatherAuthorityService.ChangedPlanets(authority, ranOn).Count == 0,
            "weathers that only ran on, or a new empty planet entry, are no change"
        );
        // `/weather clear` on planet 1, `/weather Fog` on planet 2.
        var admin = new List<WeatherAuthorityService.PlanetWeatherState>
        {
            Planet(1),
            Planet(2, Effect("Snow", 20, 220), Effect("Fog", 30, 0)),
        };
        List<WeatherAuthorityService.PlanetWeatherState> changed =
            WeatherAuthorityService.ChangedPlanets(authority, admin);
        Check(
            changed.Select(planet => planet.PlanetId).SequenceEqual(new long[] { 1, 2 }),
            "a cleared and an added weather are changes: "
                + string.Join(",", changed.Select(planet => planet.PlanetId))
        );

        MyObjectBuilder_WeatherEffect Running(string weather, double x, int life) =>
            new()
            {
                Weather = weather,
                StartPoint = new Vector3D(x, 0, 0),
                Position = new Vector3D(x, 0, 0),
                Radius = 5000,
                MaxLife = 3600,
                Life = life,
            };
        MyObjectBuilder_WeatherEffect snow = Running("Snow", 20, 300);
        MyObjectBuilder_WeatherEffect fog = Running("Fog", 30, 0);
        List<MyObjectBuilder_WeatherEffect> taken = WeatherAuthorityService.TakeOver(
            new List<MyObjectBuilder_WeatherEffect> { snow, Running("Rain", 40, 50) },
            new List<MyObjectBuilder_WeatherEffect> { Running("Snow", 20, 220), fog }
        );
        Check(
            taken.Count == 2 && ReferenceEquals(taken[0], snow) && ReferenceEquals(taken[1], fog),
            "the node's weathers win, with the World Authority's running copy of one both have"
        );
        Check(
            WeatherAuthorityService
                .TakeOver(
                    new List<MyObjectBuilder_WeatherEffect> { snow },
                    new List<MyObjectBuilder_WeatherEffect>()
                )
                .Count == 0,
            "a cleared planet stays clear"
        );
    }
}
