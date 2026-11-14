using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1316 bench: identities that stand for the sebot traders (a Steam ID of <c>cleanup.botSteamIds</c>, no client), and a
/// pilot seated in a ship.
/// <c>POST /bot-ship-test-identity?name=&amp;steamId=[&amp;identityId=]</c> creates an identity the player collection maps to
/// that Steam ID, as it maps a player who has joined once; with <c>identityId</c> it maps that identity (created if this
/// node lacks it), so a second node knows the owner of a ship that arrives from the first. <c>POST /bot-ship-test-pilot?grid=&amp;owner=</c> seats a new character of
/// that identity in the grid's first free cockpit. <c>POST /bot-ship-test-owner?grid=&amp;owner=</c> gives every block of
/// the grid to that identity (SE-1419: a bot ship sold to a player is no bot ship any more).
/// <c>POST /bot-ship-test-person?key=&amp;steamId=[&amp;x=&amp;y=&amp;z=]</c> has someone with that Steam ID stand at x, y, z for
/// the cleanup's use tracking, with no client (SE-1528: a bot beside a ship); without x, y, z it takes them away.
/// </summary>
internal static class BotShipTestRoutes
{
    public static void Register()
    {
        TestRoutes.Add("POST", "/bot-ship-test-identity", context =>
        {
            string name = TestRoutes.Query(context, "name");
            ulong steamId = ulong.Parse(TestRoutes.Query(context, "steamId"), System.Globalization.CultureInfo.InvariantCulture);
            string id = context.Request.QueryString["identityId"];
            long identityId = id == null ? 0 : long.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Identity(name, steamId, identityId)));
        });
        TestRoutes.Add("POST", "/bot-ship-test-pilot", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            long owner = TestRoutes.Long(context, "owner");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Pilot(grid, owner)));
        });
        TestRoutes.Add("POST", "/bot-ship-test-person", context =>
        {
            string key = TestRoutes.Query(context, "key");
            ulong steamId = ulong.Parse(TestRoutes.Query(context, "steamId"), System.Globalization.CultureInfo.InvariantCulture);
            VRageMath.Vector3D? at = context.Request.QueryString["x"] == null ? null
                : new VRageMath.Vector3D(TestRoutes.Double(context, "x"), TestRoutes.Double(context, "y"), TestRoutes.Double(context, "z"));
            var seam = HarmonyLib.AccessTools.Method("ClusterPlugin.BotShipCleaner:SetTestPerson")
                ?? throw new MissingMethodException("ClusterPlugin.BotShipCleaner", "SetTestPerson");
            seam.Invoke(null, new object[] { key, steamId, at });
            TestRoutes.Json(context, 200, new { key, steamId, placed = at.HasValue });
        });
        TestRoutes.Add("POST", "/bot-ship-test-owner", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            long owner = TestRoutes.Long(context, "owner");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Owner(grid, owner)));
        });
    }

    private static object Owner(long gridId, long owner)
    {
        if (!MyEntities.TryGetEntityById(gridId, out MyEntity entity) || entity is not MyCubeGrid grid)
            throw new InvalidOperationException($"Grid {gridId} is not on this node");
        int changed = 0;
        foreach (MyCubeBlock block in grid.GetFatBlocks())
        {
            block.ChangeOwner(owner, VRage.Game.MyOwnershipShareModeEnum.None);
            changed++;
        }
        return new { grid = gridId, blocks = changed };
    }

    private static object Identity(string name, ulong steamId, long identityId)
    {
        var players = MySession.Static.Players;
        MyIdentity identity = identityId == 0 ? players.CreateNewIdentity(name, initialPlayer: true)
            : players.TryGetIdentity(identityId) ?? players.CreateNewIdentity(name, identityId, null, null);
        var playerId = new MyPlayer.PlayerId(steamId, 0);
        // A Dictionary or a ConcurrentDictionary, by server build: both are an IDictionary.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var byIdentity = (System.Collections.IDictionary)typeof(Sandbox.Game.Multiplayer.MyPlayerCollection).GetField("m_identityPlayerIds", flags).GetValue(players);
        var byPlayer = (System.Collections.IDictionary)typeof(Sandbox.Game.Multiplayer.MyPlayerCollection).GetField("m_playerIdentityIds", flags).GetValue(players);
        byIdentity[identity.IdentityId] = playerId;
        byPlayer[playerId] = identity.IdentityId;
        return new { identityId = identity.IdentityId, steamId = players.TryGetSteamId(identity.IdentityId), npc = players.IdentityIsNpc(identity.IdentityId) };
    }

    private static object Pilot(long gridId, long owner)
    {
        if (!MyEntities.TryGetEntityById(gridId, out MyEntity entity) || entity is not MyCubeGrid grid)
            throw new InvalidOperationException($"Grid {gridId} is not on this node");
        MyCockpit cockpit = grid.GetFatBlocks<MyCockpit>().FirstOrDefault(seat => seat.Pilot == null)
            ?? throw new InvalidOperationException($"Grid {gridId} has no free cockpit");
        MyCharacter character = MyCharacter.CreateCharacter(cockpit.WorldMatrix, VRageMath.Vector3.Zero, "se1316-pilot", "Default_Astronaut",
            null, null, findNearPos: false, identityId: owner, addDefaultItems: false);
        cockpit.AttachPilot(character, 0, storeOriginalPilotWorld: false);
        return new { grid = gridId, cockpit = cockpit.EntityId, pilot = cockpit.Pilot?.EntityId ?? 0 };
    }
}
