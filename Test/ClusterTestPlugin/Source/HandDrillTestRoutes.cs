using System;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Library.Utils;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1940 bench routes: a character holding a hand drill, the state node-0 crashed importing on
/// 2026-11-25. The import builds the character's drill and Adds it to the scene, but the drill gets its
/// owner only when the character's first UpdateOnceBeforeFrame equips it.
/// </summary>
internal static class HandDrillTestRoutes
{
    private static readonly MyDefinitionId DrillItem = new(
        typeof(MyObjectBuilder_PhysicalGunObject),
        "HandDrillItem"
    );

    public static void Register()
    {
        // POST /hand-drill-test-survival - survival game mode on this node. The bench world is creative, where
        // MyHandDrill.UpdateBeforeSimulation100 never reaches its Owner dereference.
        TestRoutes.Add(
            "POST",
            "/hand-drill-test-survival",
            context =>
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        MySession.Static.Settings.GameMode = MyGameModeEnum.Survival;
                        return new { survival = !MySession.Static.CreativeMode };
                    })
                )
        );
        // POST /hand-drill-test-spawn?x=&y=&z=&owner= - a character of that identity at rest there, a hand
        // drill in its inventory and in its hand
        TestRoutes.Add(
            "POST",
            "/hand-drill-test-spawn",
            context =>
            {
                Vector3D position = new(
                    TestRoutes.Double(context, "x"),
                    TestRoutes.Double(context, "y"),
                    TestRoutes.Double(context, "z")
                );
                long owner = TestRoutes.Long(context, "owner");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => Spawn(position, owner))
                );
            }
        );
        // GET /hand-drill-test-state?id= - the character on this node and the tool in its hand
        TestRoutes.Add(
            "GET",
            "/hand-drill-test-state",
            context =>
            {
                long id = TestRoutes.Long(context, "id");
                TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => State(id)));
            }
        );
    }

    private static object Spawn(Vector3D position, long owner)
    {
        MatrixD matrix = MatrixD.CreateTranslation(position);
        MyCharacter character = MyCharacter.CreateCharacter(
            matrix,
            Vector3.Zero,
            "se1940-driller",
            "Default_Astronaut",
            null,
            null,
            findNearPos: false,
            identityId: owner,
            addDefaultItems: false
        );
        character
            .GetInventory()
            .AddItems(
                1,
                (MyObjectBuilder_PhysicalGunObject)
                    MyObjectBuilderSerializerKeen.CreateNewObject(DrillItem)
            );
        MyHandItemDefinition hand =
            MyDefinitionManager.Static.TryGetHandItemForPhysicalItem(DrillItem)
            ?? throw new InvalidOperationException("No hand item for " + DrillItem);
        character.SwitchToWeapon(hand.Id, sync: false);
        return State(character.EntityId);
    }

    private static object State(long id)
    {
        if (
            !MyEntities.TryGetEntityById(id, out MyEntity entity)
            || entity is not MyCharacter character
            || character.MarkedForClose
        )
            return new { present = false };
        MyEntity tool = character.CurrentWeapon as MyEntity;
        return new
        {
            present = true,
            id = character.EntityId,
            survival = !MySession.Static.CreativeMode,
            tool = tool?.GetType().Name,
            toolId = tool?.EntityId ?? 0,
            toolInScene = tool?.InScene ?? false,
            toolOwner = (tool as MyHandDrill)?.Owner?.EntityId ?? 0,
        };
    }
}
