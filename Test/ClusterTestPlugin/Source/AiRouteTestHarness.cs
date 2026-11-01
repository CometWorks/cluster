using System;
using System.Linq;
using System.Text.Json;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// T-0232 test seam: an AI-flown Visitor Ion already on its leg, with no client anywhere near it. The
/// ship grid comes out of the regression suite's own platform blueprint (the one ship the live cluster
/// flies), gets the anchoring player's identity as owner, and is put on its route here on the server:
/// the waypoints through the Basic block's own synced AddWaypoints event (what a client's terminal action
/// lands in), the speed cap on the Flight block, and both AI components activated. That is the state an
/// endurance AI leg is in once its pilot has hopped 2.5 km away - which is why a route change can then
/// only come from the owning node.
/// </summary>
public static class AiRouteTestHarness
{
	private const string ShipName = "cluster-ai-route-ship";

	/// <summary>Body: {"owner", "blueprint", "position": [x,y,z], "forward": [x,y,z], "waypoints": [[x,y,z],...], "names": [...], "speedLimit"}.</summary>
	public static object Spawn(string body)
	{
		using JsonDocument document = JsonDocument.Parse(body);
		JsonElement root = document.RootElement;
		long owner = root.GetProperty("owner").GetInt64();
		if (owner == 0) throw new ArgumentOutOfRangeException(nameof(owner), "AI blocks owned by identity 0 fail every ownership check");
		string blueprint = root.GetProperty("blueprint").GetString();
		Vector3D position = Vector(root.GetProperty("position"));
		Vector3D forward = Vector3D.Normalize(Vector(root.GetProperty("forward")));
		Vector3D[] waypoints = root.GetProperty("waypoints").EnumerateArray().Select(Vector).ToArray();
		string[] names = root.GetProperty("names").EnumerateArray().Select(name => name.GetString() ?? "").ToArray();
		float speedLimit = root.TryGetProperty("speedLimit", out JsonElement speed) ? speed.GetSingle() : 40f;
		if (waypoints.Length < 2 || names.Length != waypoints.Length)
			// One waypoint is the list the game refuses to engage on (MyAutopilotComponent.CanEnableAutoPilot).
			throw new ArgumentException("at least two waypoints, one name each");

		if (!MyObjectBuilderSerializerKeen.DeserializeXML(blueprint, out MyObjectBuilder_Definitions definitions))
			throw new InvalidOperationException("Could not read blueprint " + blueprint);
		MyObjectBuilder_CubeGrid ship = definitions.ShipBlueprints.SelectMany(print => print.CubeGrids)
			.FirstOrDefault(grid => grid.CubeBlocks.Any(block => block.GetType().Name == "MyObjectBuilder_BasicMissionBlock"))
			?? throw new InvalidOperationException("Blueprint " + blueprint + " carries no grid with an AI Basic block");
		MyEntities.RemapObjectBuilder(ship);
		ship.IsStatic = false;
		ship.CreatePhysics = true;
		ship.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
		ship.DisplayName = ShipName;
		ship.LinearVelocity = new SerializableVector3(0, 0, 0);
		ship.AngularVelocity = new SerializableVector3(0, 0, 0);
		Vector3D up = Vector3D.CalculatePerpendicularVector(forward);
		ship.PositionAndOrientation = new MyPositionAndOrientation(position, forward, up);
		foreach (MyObjectBuilder_CubeBlock block in ship.CubeBlocks)
		{
			block.Owner = owner;
			block.BuiltBy = owner;
			block.ShareMode = MyOwnershipShareModeEnum.All;
		}
		MyCubeGrid grid = MyEntities.CreateFromObjectBuilderAndAdd(ship, fadeIn: false) as MyCubeGrid
			?? throw new InvalidOperationException("Could not spawn the AI route ship");

		MyFlightMovementBlock flight = grid.GetFatBlocks<MyFlightMovementBlock>().Single();
		MyBasicMissionBlock basicBlock = grid.GetFatBlocks<MyBasicMissionBlock>().Single();
		MyBasicMissionAutopilot basic = basicBlock.Components.Get<MyBasicMissionAutopilot>()
			?? throw new InvalidOperationException("The AI Basic block has no mission component");
		flight.SpeedLimit = speedLimit;
		MyMultiplayer.RaiseEvent(basic, x => x.AddWaypoints, waypoints, names);
		// Flight first, then the mission, as the terminal switches are thrown in game.
		Activate(flight);
		Activate(basicBlock);
		return new
		{
			grid = grid.EntityId,
			flightBlock = flight.EntityId,
			basicBlock = basicBlock.EntityId,
			waypoints = basic.m_waypoints.Select(waypoint => waypoint.Name).ToArray(),
			flightActive = Activated(flight),
			basicActive = Activated(basicBlock),
		};
	}

	private static void Activate(MyEntity block)
	{
		// The concrete component: the ModAPI interface only reads IsActivated. Setting the synced value on
		// the server runs IsActivated_ValueChanged, which registers the block with the grid's AI system.
		MyAiBlockComponent component = (block.Components.TryGet(out IMyAiBlockComponent found) ? found : null) as MyAiBlockComponent
			?? throw new InvalidOperationException(block.GetType().Name + " has no AI block component");
		component.IsActivated = true;
	}

	private static bool Activated(MyEntity block) =>
		block.Components.TryGet(out IMyAiBlockComponent component) && component.IsActivated;

	private static Vector3D Vector(JsonElement element) =>
		new(element[0].GetDouble(), element[1].GetDouble(), element[2].GetDouble());
}
