using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.Game.WorldEnvironment;
using VRage.Game;
using VRage.Network;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1596 bench routes: an admin's Alt-F10 Trash Removal upload and weather changes, made on this node the way the
/// game makes them, and the trash settings this node's session runs with. Vanilla members only, so the routes build
/// against a package from before the fix too.
/// </summary>
internal static class AdminSyncRoutes
{
    public static void Register()
    {
        // GET /admin-sync-trash - this session's trash settings.
        TestRoutes.Add(
            "GET",
            "/admin-sync-trash",
            context => TestRoutes.Json(context, 200, TestRoutes.OnGameThread(Trash))
        );

        // POST /admin-sync-trash-upload?blockCount=&voxelAge=&enable= - the server side of the admin menu's upload
        // (MyGuiScreenAdminMenu.UploadSettingsToServer) with this session's settings and the given changes.
        TestRoutes.Add(
            "POST",
            "/admin-sync-trash-upload",
            context =>
            {
                int blockCount = int.Parse(
                    TestRoutes.Query(context, "blockCount"),
                    CultureInfo.InvariantCulture
                );
                int voxelAge = int.Parse(
                    TestRoutes.Query(context, "voxelAge"),
                    CultureInfo.InvariantCulture
                );
                bool enable = bool.Parse(TestRoutes.Query(context, "enable"));
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        MyObjectBuilder_SessionSettings s = MySession.Static.Settings;
                        var settings = new MyGuiScreenAdminMenu.AdminSettings
                        {
                            Flags = s.TrashFlags,
                            Enable = enable,
                            BlockCount = blockCount,
                            PlayerDistance = s.PlayerDistanceThreshold,
                            GridCount = s.OptimalGridCount,
                            PlayerInactivity = s.PlayerInactivityThreshold,
                            CharacterRemovalThreshold = s.PlayerCharacterRemovalThreshold,
                            StopGridsPeriod = s.StopGridsPeriodMin,
                            RemoveOldIdentities = s.RemoveOldIdentitiesH,
                            VoxelDistanceFromPlayer = s.VoxelPlayerDistanceThreshold,
                            VoxelDistanceFromGrid = s.VoxelGridDistanceThreshold,
                            VoxelAge = voxelAge,
                            VoxelEnable = s.VoxelTrashRemovalEnabled,
                            TrashCleanerCargoBagsMaxLiveTime = s.TrashCleanerCargoBagsMaxLiveTime,
                            MaxCargoBags = s.MaxCargoBags,
                        };
                        MyGuiScreenAdminMenu.UploadSettingsToServer(settings);
                        return Trash();
                    })
                );
            }
        );

        // POST /admin-sync-weather-set?weather=&length= - a weather set on this node through the weather API, as a
        // server plugin or mod would make it, over the first planet.
        TestRoutes.Add(
            "POST",
            "/admin-sync-weather-set",
            context =>
            {
                string weather = TestRoutes.Query(context, "weather");
                int length = int.Parse(
                    TestRoutes.Query(context, "length", "600"),
                    CultureInfo.InvariantCulture
                );
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        MySectorWeatherComponent component = Weather();
                        MyPlanet planet = Planet();
                        if (
                            !component.m_weatherPlanetData.Any(entry =>
                                entry != null && entry.PlanetId == planet.EntityId
                            )
                        )
                            component.m_weatherPlanetData.Add(
                                new MyObjectBuilder_WeatherPlanetData { PlanetId = planet.EntityId }
                            );
                        Vector3D center = planet.PositionComp.GetPosition();
                        bool applied = component.SetWeather(
                            weather,
                            planet.AtmosphereRadius,
                            center,
                            null,
                            Vector3.Zero,
                            length
                        );
                        return (object)
                            new
                            {
                                planetId = planet.EntityId,
                                weather,
                                applied,
                            };
                    })
                );
            }
        );

        // POST /admin-sync-weather-client-clear - what an admin's client sends up after `/weather clear` (or the
        // Alt-F10 Remove button) over the first planet: this node's weather list with that planet's weathers removed,
        // through the [Server] event UpdateWeathersOnClients as a remote client raises it.
        TestRoutes.Add(
            "POST",
            "/admin-sync-weather-client-clear",
            context =>
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        MyPlanet planet = Planet();
                        MyObjectBuilder_WeatherPlanetData[] data = Weather()
                            .m_weatherPlanetData.Where(entry => entry != null)
                            .Select(entry => new MyObjectBuilder_WeatherPlanetData
                            {
                                PlanetId = entry.PlanetId,
                                NextWeather = entry.NextWeather,
                                Weathers =
                                    entry.PlanetId == planet.EntityId
                                        ? new List<MyObjectBuilder_WeatherEffect>()
                                        : entry.Weathers.ToList(),
                            })
                            .ToArray();
                        using (MyEventContext.Set(new EndpointId(1234567890UL), null, false))
                            MySectorWeatherComponent.UpdateWeathersOnClients(data);
                        return (object)new { planetId = planet.EntityId, planets = data.Length };
                    })
                )
        );

        // POST /admin-sync-time-burst?from=&to=&count= - what a node does with a dragged Alt-F10 time-of-day slider
        // whose values arrive several per frame: the vanilla [Server] handler MyTimeOfDayHelper.UpdateTimeOfDayServer
        // once per value, from..to, back to back on the game thread.
        TestRoutes.Add(
            "POST",
            "/admin-sync-time-burst",
            context =>
            {
                float from = float.Parse(
                    TestRoutes.Query(context, "from"),
                    CultureInfo.InvariantCulture
                );
                float to = float.Parse(
                    TestRoutes.Query(context, "to"),
                    CultureInfo.InvariantCulture
                );
                int count = int.Parse(
                    TestRoutes.Query(context, "count", "60"),
                    CultureInfo.InvariantCulture
                );
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        for (int i = 0; i < count; i++)
                            MyTimeOfDayHelper.UpdateTimeOfDayServer(
                                count == 1 ? to : from + (to - from) * i / (count - 1)
                            );
                        return (object)
                            new
                            {
                                from,
                                to,
                                count,
                            };
                    })
                );
            }
        );
    }

    private static object Trash()
    {
        MyObjectBuilder_SessionSettings s = MySession.Static.Settings;
        return new
        {
            s.TrashRemovalEnabled,
            s.BlockCountThreshold,
            s.VoxelAgeThreshold,
            TrashFlags = (int)s.TrashFlags,
            s.PlayerDistanceThreshold,
            s.RemoveOldIdentitiesH,
        };
    }

    private static MySectorWeatherComponent Weather() =>
        MySession.Static?.GetComponent<MySectorWeatherComponent>()
        ?? throw new InvalidOperationException("Weather component is not loaded");

    private static MyPlanet Planet() =>
        MySession
            .Static.VoxelMaps.Instances.OfType<MyPlanet>()
            .OrderBy(candidate => candidate.EntityId)
            .FirstOrDefault()
        ?? throw new InvalidOperationException("No planet is loaded");
}
