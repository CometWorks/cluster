using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using ClusterPlugin;
using Sandbox;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1870 bench routes, World Authority only: a live-sized player profile record (227 players, 2.4 KB
/// toolbars, cameras) that churns the way the live one does, and fake cameras for a real player. Both feed
/// <c>PlayerProfileService.ReceiveReport</c> through reflection, as a node's report would arrive, so the same
/// routes drive the code from before and after SE-1870.
/// </summary>
internal static class PlayerProfileTestRoutes
{
    private const string LoadOrigin = "se-1870-synthetic";
    private const string CameraOrigin = "se-1870-cameras";
    private const ulong SyntheticSteamBase = 76561199870000000;

    private static readonly MethodInfo ReceiveReport = typeof(PlayerProfileService).GetMethod(
        "ReceiveReport",
        BindingFlags.NonPublic | BindingFlags.Static
    );
    private static readonly FieldInfo Record = typeof(PlayerProfileService).GetField(
        "Record",
        BindingFlags.NonPublic | BindingFlags.Static
    );

    private static Timer m_churn;
    private static PlayerProfile[] m_load = Array.Empty<PlayerProfile>();
    private static int m_churned;
    private static DateTime m_until;

    public static void Register()
    {
        // POST /player-profile-test-load?count=&cameras=&heavy=&heavyCameras=&slots=&churnSeconds=&seconds=
        // `count` synthetic players (`heavy` of them with `heavyCameras` cameras, the rest with `cameras`), each with
        // a `slots`-slot toolbar. Every churnSeconds one of them changes a camera distance, as a player zooming does;
        // reported for `seconds` (0 stops; the record keeps them, it is sticky).
        TestRoutes.Add(
            "POST",
            "/player-profile-test-load",
            context =>
            {
                int count = (int)TestRoutes.Long(context, "count");
                int cameras = (int)TestRoutes.Long(context, "cameras");
                int heavy = (int)TestRoutes.Long(context, "heavy");
                int heavyCameras = (int)TestRoutes.Long(context, "heavyCameras");
                int slots = (int)TestRoutes.Long(context, "slots");
                double churn = TestRoutes.Double(context, "churnSeconds");
                double seconds = TestRoutes.Double(context, "seconds");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                        StartLoad(count, cameras, heavy, heavyCameras, slots, churn, seconds)
                    )
                );
            }
        );
        // POST /player-profile-test-cameras?steamId=&serialId=&count= - the player's record gains `count` cameras of
        // entities that exist nowhere, reported once, the way a respawned character's old body leaves one behind.
        TestRoutes.Add(
            "POST",
            "/player-profile-test-cameras",
            context =>
            {
                ulong steam = (ulong)TestRoutes.Long(context, "steamId");
                int serial = int.Parse(TestRoutes.Query(context, "serialId", "0")); // Long() refuses 0
                int count = (int)TestRoutes.Long(context, "count");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => AddCameras(steam, serial, count))
                );
            }
        );
    }

    private static object StartLoad(
        int count,
        int cameras,
        int heavy,
        int heavyCameras,
        int slots,
        double churnSeconds,
        double seconds
    )
    {
        RequireAuthority();
        m_churn?.Dispose();
        m_churn = null;
        if (seconds <= 0)
            return new { stopped = true };
        string toolbar = Toolbar(slots);
        float[] colors = Enumerable
            .Range(0, PlayerProfileCodec.ColorSlotCount * 3)
            .Select(i => (i % 7) / 7f)
            .ToArray();
        m_load = Enumerable
            .Range(0, count)
            .Select(i => new PlayerProfile(
                SyntheticSteamBase + (ulong)i,
                0,
                9_000_000_000_000_000 + i,
                toolbar,
                colors,
                i % 14,
                "",
                false,
                0,
                Enumerable
                    .Range(0, i < heavy ? heavyCameras : cameras)
                    .Select(c => new PlayerCameraSetting(
                        8_000_000_000_000_000 + i * 1000L + c,
                        2.5 + c % 5,
                        0.1f * (c % 9),
                        0.2f * (c % 5),
                        c % 3 == 0
                    ))
                    .ToArray()
            ))
            .ToArray();
        m_until = DateTime.UtcNow.AddSeconds(seconds);
        Report(LoadOrigin, m_load);
        if (churnSeconds > 0)
            m_churn = new Timer(
                _ => MySandboxGame.Static?.Invoke(Churn, "se-1870-profile-churn"),
                null,
                TimeSpan.FromSeconds(churnSeconds),
                TimeSpan.FromSeconds(churnSeconds)
            );
        return new
        {
            players = m_load.Length,
            toolbarBytes = toolbar.Length,
            cameras = m_load.Sum(profile => profile.Cameras.Length),
            until = m_until,
        };
    }

    private static void Churn()
    {
        if (DateTime.UtcNow > m_until || m_load.Length == 0)
        {
            m_churn?.Dispose();
            m_churn = null;
            return;
        }
        int index = m_churned++ % m_load.Length;
        PlayerProfile profile = m_load[index];
        if (profile.Cameras.Length > 0)
        {
            PlayerCameraSetting[] cameras = profile.Cameras.ToArray();
            cameras[0] = cameras[0] with { Distance = 2 + m_churned % 10 };
            m_load[index] = profile with { Cameras = cameras };
        }
        Report(LoadOrigin, m_load);
    }

    private static object AddCameras(ulong steam, int serial, int count)
    {
        RequireAuthority();
        var record =
            (Dictionary<(ulong SteamId, int SerialId), PlayerProfile>)Record.GetValue(null);
        PlayerProfile stored =
            record.GetValueOrDefault((steam, serial))
            ?? throw new InvalidOperationException($"No profile for {steam}:{serial}");
        PlayerProfile reported = stored with
        {
            Cameras = (stored.Cameras ?? Array.Empty<PlayerCameraSetting>())
                .Concat(
                    Enumerable
                        .Range(0, count)
                        .Select(c => new PlayerCameraSetting(
                            7_000_000_000_000_000 + c,
                            4,
                            0,
                            0,
                            false
                        ))
                )
                .ToArray(),
        };
        Report(CameraOrigin, [reported]);
        return new
        {
            player = steam + ":" + serial,
            before = stored.Cameras?.Length ?? 0,
            after = record[(steam, serial)].Cameras?.Length ?? 0,
        };
    }

    private static void Report(string origin, PlayerProfile[] profiles) =>
        ReceiveReport.Invoke(
            null,
            [origin, PlayerProfileCodec.WriteReport(new PlayerProfileReport(origin, profiles))]
        );

    private static void RequireAuthority()
    {
        if (!ClusterRuntime.IsWorldAuthority)
            throw new InvalidOperationException("World Authority only");
    }

    /// <summary>A toolbar the vanilla serializer wrote, so every reader parses it like a real one.</summary>
    private static string Toolbar(int slots)
    {
        var toolbar = new MyObjectBuilder_Toolbar
        {
            Slots = new List<MyObjectBuilder_Toolbar.Slot>(),
        };
        string[] items =
        [
            "LargeBlockArmorBlock",
            "LargeHeavyBlockArmorBlock",
            "LargeBlockSmallContainer",
            "LargeBlockLargeContainer",
            "SmallLight",
            "LargeBlockConveyor",
            "LargeBlockBatteryBlock",
            "LargeBlockSmallGenerator",
            "LargeBlockAssembler",
        ];
        for (int slot = 0; slot < slots; slot++)
        {
            var item =
                MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ToolbarItemCubeBlock>();
            item.DefinitionId = new SerializableDefinitionId(
                new MyObjectBuilderType(typeof(MyObjectBuilder_CubeBlock)),
                items[slot % items.Length]
            );
            toolbar.Slots.Add(
                new MyObjectBuilder_Toolbar.Slot
                {
                    Index = slot,
                    Item = "slot" + slot,
                    Data = item,
                }
            );
        }
        using var stream = new MemoryStream();
        MyObjectBuilderSerializerKeen.SerializeXML(stream, toolbar);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
