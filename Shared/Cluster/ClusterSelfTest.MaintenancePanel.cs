using System;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;
using VRageMath;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1133: a LargeCrate with its right maintenance panel open, created the way the handover import creates it and
	/// serialized in the same frame, before the panel's next-frame update ran. The live panel is still Closed there (the
	/// known positive: vanilla's Serialize reports it closed, which failed every handover of Alpha Station), yet the
	/// builder must carry Open for the right panel and keep the left one closed. Runs on the game thread; the grid is
	/// never added to the scene and is closed again.
	/// </summary>
	private static void MaintenancePanelPending()
	{
		Exception failure = null;
		using var done = new System.Threading.ManualResetEventSlim();
		Sandbox.MySandboxGame.Static.Invoke(() =>
		{
			try { MaintenancePanelPendingOnGameThread(); }
			catch (Exception error) { failure = error; }
			finally { done.Set(); }
		}, "self-test maintenance-panel-pending");
		Assert(done.Wait(TimeSpan.FromSeconds(30)), "the game thread did not run the section within 30 s");
		if (failure != null) throw failure;
	}

	private static void MaintenancePanelPendingOnGameThread()
	{
		var crateId = new MyDefinitionId(typeof(MyObjectBuilder_TerminalBlock), "LargeCrate");
		Assert(MyDefinitionManager.Static.TryGetCubeBlockDefinition(crateId, out _), "no " + crateId + " definition (the fixture is wrong)");
		var crate = (MyObjectBuilder_TerminalBlock)MyObjectBuilderSerializerKeen.CreateNewObject(crateId);
		crate.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
		crate.ComponentContainer = new MyObjectBuilder_ComponentContainer();
		foreach ((string subtype, bool open) in new[] { ("LargeCrate01_left", false), ("LargeCrate01_right", true) })
			crate.ComponentContainer.Components.Add(new MyObjectBuilder_ComponentContainer.ComponentData
			{
				TypeId = nameof(MyMaintenancePanelComponent),
				// As a save writes it: the builder's default (true) would let the second panel replace the first.
				Component = new MyObjectBuilder_MaintenancePanelComponent { SubtypeName = subtype, Open = open, RemoveExistingComponentOnNewInsert = false },
			});
		var grid = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
		grid.GridSizeEnum = MyCubeSize.Large;
		grid.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(0, 0, -1e7), Vector3.Forward, Vector3.Up);
		grid.CubeBlocks.Add(crate);

		var built = (MyCubeGrid)MyEntities.CreateFromObjectBuilder(grid, fadeIn: false);
		Assert(built != null, "the crate grid was not created");
		try
		{
			MyCubeBlock block = built.GetFatBlocks().Single();
			((MyComponentContainer)block.Components).TryGet(typeof(MyMaintenancePanelComponent), MyStringHash.GetOrCompute("LargeCrate01_right"), out MyComponentBase found);
			var right = found as MyMaintenancePanelComponent;
			Assert(right != null, "the crate has no LargeCrate01_right maintenance panel");
			Assert(right.Status == DoorStatus.Closed && right.m_builderToInitFrom != null,
				"the right panel already applied its saved state (" + right.Status + "), so this section tests nothing");

			var saved = (MyObjectBuilder_CubeGrid)built.GetObjectBuilder();
			MyObjectBuilder_MaintenancePanelComponent[] states = saved.CubeBlocks.Single().ComponentContainer.Components
				.Select(entry => entry.Component).OfType<MyObjectBuilder_MaintenancePanelComponent>().ToArray();
			bool? Open(string subtype) => states.SingleOrDefault(state => state.SubtypeName == subtype)?.Open;
			string report = string.Join(", ", states.Select(state => state.SubtypeName + "=" + state.Open));
			Assert(states.Length == 2, "the crate serialized " + states.Length + " maintenance panels, expected 2: " + report);
			Assert(Open("LargeCrate01_right") == true, "the open right panel was serialized closed before its next-frame update: " + report);
			Assert(Open("LargeCrate01_left") == false, "the closed left panel was serialized open: " + report);
		}
		finally
		{
			built.Close();
		}
	}
}
