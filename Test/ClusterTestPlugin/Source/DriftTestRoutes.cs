using System;
using System.Linq;
using System.Text;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1152..1155 bench: puts blocks into the states the round-trip gate rejected on octahedron-test.
/// <c>/drift-test?action=</c> (POST) with <c>block=</c> or <c>grid=</c>:
/// <c>warhead-runout</c> a countdown that ran out without an explosion (CountdownMs -1, still counting down, dropped
/// from MyWarheads), <c>empty-group</c> a block group with no blocks, <c>overfill</c> a reactor holding 0.0032 kg more
/// uranium than the load restores, <c>pilot</c> (with <c>owner=</c>) a character of that identity
/// seated in the cockpit, <c>suit</c> (with <c>level=</c>) that pilot's suit oxygen. <c>/drift-test-state</c> (GET)
/// reports all of it for a list of <c>blocks=</c>.
/// </summary>
internal static class DriftTestRoutes
{
    private static readonly MyDefinitionId Uranium = new(typeof(MyObjectBuilder_Ingot), "Uranium");

    public static void Register()
    {
        TestRoutes.Add("POST", "/drift-test", context =>
        {
            string action = context.Request.QueryString["action"] ?? throw new ArgumentException("Missing action");
            long id = TestRoutes.Long(context, action == "empty-group" ? "grid" : "block");
            long owner = action == "pilot" ? TestRoutes.Long(context, "owner") : 0;
            float level = action == "suit" ? float.Parse(context.Request.QueryString["level"], System.Globalization.CultureInfo.InvariantCulture) : 0;
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Act(action, id, owner, level)));
        });
        TestRoutes.Add("GET", "/drift-test-state", context =>
        {
            long[] blocks = (context.Request.QueryString["blocks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => blocks.Select(State).ToArray()));
        });
    }

    private static T Entity<T>(long id) where T : MyEntity
        => MyEntities.TryGetEntityById(id, out MyEntity entity) && entity is T typed && !entity.MarkedForClose
            ? typed : throw new InvalidOperationException($"{typeof(T).Name} {id} is not on this node");

    private static object Act(string action, long id, long owner, float level)
    {
        switch (action)
        {
            case "warhead-runout":
            {
                var warhead = Entity<MyWarhead>(id);
                warhead.StartCountdown();
                warhead.m_countdownMs.Value = -1;
                MyWarheads.RemoveWarhead(warhead);
                return State(id);
            }
            case "empty-group":
            {
                var grid = Entity<MyCubeGrid>(id);
                grid.BlockGroups.Add(new MyBlockGroup { Name = new StringBuilder("Railguns") });
                return new { grid = id, groups = ((MyObjectBuilder_CubeGrid)grid.GetObjectBuilder()).BlockGroups.Select(group => new { name = group.Name, blocks = group.Blocks.Count }) };
            }
            case "overfill":
            {
                var reactor = Entity<MyReactor>(id);
                MyInventory inventory = reactor.GetInventory();
                inventory.Clear();
                // The load restores at most MaxVolume / volume in float arithmetic; partition 4519's reactor held 0.0032 kg
                // over that after a station refuel. AddItems would refuse it, so it goes in past the check.
                foreach (var fuel in reactor.BlockDefinition.FuelInfos)
                    inventory.AddItemsInternal((MyFixedPoint)((double)inventory.MaxVolume / (double)fuel.FuelDefinition.Volume + 0.0032), fuel.FuelItem);
                return State(id);
            }
            case "pilot":
            {
                var cockpit = Entity<MyCockpit>(id);
                MyCharacter character = MyCharacter.CreateCharacter(cockpit.WorldMatrix, Vector3.Zero, "se1154-pilot", "Default_Astronaut",
                    null, null, findNearPos: false, identityId: owner, addDefaultItems: false);
                cockpit.AttachPilot(character, 0, storeOriginalPilotWorld: false);
                return State(id);
            }
            case "suit":
            {
                MyCharacter pilot = Entity<MyCockpit>(id).Pilot ?? throw new InvalidOperationException($"Cockpit {id} has no pilot");
                MyDefinitionId oxygen = MyCharacterOxygenComponent.OxygenId;
                pilot.OxygenComponent.UpdateStoredGasLevel(ref oxygen, level);
                return State(id);
            }
            default:
                throw new ArgumentException($"Unknown drift action '{action}'");
        }
    }

    private static object State(long id)
    {
        if (!MyEntities.TryGetEntityById(id, out MyEntity entity) || entity.MarkedForClose) return new { id, found = false };
        return entity switch
        {
            MyWarhead warhead => new { id, found = true, countdownMs = (int)warhead.m_countdownMs, isCountingDown = warhead.IsCountingDown },
            MyReactor reactor => (object)new { id, found = true, uranium = reactor.GetInventory().GetItemAmount(Uranium).ToString() },
            MyCockpit cockpit => new
            {
                id, found = true, pilot = cockpit.Pilot?.EntityId ?? 0, suit = cockpit.Pilot?.OxygenComponent?.SuitOxygenLevel,
                groups = cockpit.CubeGrid.BlockGroups.Count,
            },
            MyCubeBlock block => new { id, found = true, groups = block.CubeGrid.BlockGroups.Count },
            _ => new { id, found = true },
        };
    }
}
