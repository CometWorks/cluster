using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Import warm-up. The first staged import onto a freshly started node pays the JIT for the whole
/// path - save codec, object-builder deserialisation, MyCubeGrid/MyCubeBlock.Init, the round-trip
/// XML comparison: 390-860 ms wall against 65-100 ms steady state (w22, 2026-08-21), and with a
/// rotation every few minutes one transfer in every fifteen is a cold one. The warm-up runs that
/// exact path once, off the game thread, on a canned five-block small grid that is never added to
/// the scene: serialise -> envelope -> parse -> stage on a worker -> compare -> discard. Ids come
/// from the node's own range (five of them); nothing is registered in the world's id table because
/// OnEntityInitialized is never called for the dormant copy. Failure is logged and ignored - the
/// warm-up is an optimisation, never a gate.
/// </summary>
internal sealed partial class ClusterControlServer
{
	private static int m_warmedUp;

	/// <summary>Any thread. Runs once per process; returns immediately on later calls.</summary>
	internal static void WarmUpImportPath()
	{
		if (Interlocked.Exchange(ref m_warmedUp, 1) != 0) return;
		if (!ClusterConfig.GetBool("import.warmup")) { Log("import warm-up disabled (import.warmup)"); return; }
		new Thread(WarmUpThread) { IsBackground = true, Name = "ClusterImportWarmUp" }.Start();
	}

	private static void WarmUpThread()
	{
		Stopwatch wall = Stopwatch.StartNew();
		StagePlan plan = new StagePlan { Workers = 1 };
		try
		{
			MyObjectBuilder_CubeGrid grid = WarmUpGrid();
			MyObjectBuilder_EntityBase[] source = { grid };
			byte[] body = PartitionSaveFile.SerializeEntities(source);
			byte[] file = PartitionSaveFile.Write(ulong.MaxValue, 1, 1, 1, new BoundingSphereD((Vector3D)grid.PositionAndOrientation.Value.Position, 10), false, source.Length, body, 0);
			PartitionSaveFile.Parsed save = PartitionSaveFile.Read(file);
			PartitionSaveFile.ValidateEnvelope(save, ulong.MaxValue, 1);
			MyObjectBuilder_EntityBase[] entities = PartitionSaveFile.DeserializeEntities(save.Body, save.EntityCount);
			double deserializeMs = wall.Elapsed.TotalMilliseconds;
			ImportRoundTrip.PreparedSource prepared = ImportRoundTrip.PrepareSource(entities);

			StagedEntity slot = new StagedEntity { Builder = entities[0] };
			plan.Staged.Add(slot);
			plan.Pending = 1;
			Invoke(() => { ForceSerialUpdate(plan); return 0; });
			StartBuilders(plan.Staged, plan);
			if (!plan.Done.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("warm-up worker did not finish within 60 s");
			if (slot.Error != null) throw slot.Error;
			double stageMs = slot.InitMs;

			// Game thread, one short frame: the builder of the dormant copy (what CollectImportedState
			// does for a real import), then the copy is discarded without ever being registered.
			MyObjectBuilder_EntityBase imported = Invoke(() =>
			{
				try
				{
					MyEntity entity = slot.Entity;
					return entity == null ? null : (MyObjectBuilder_EntityBase)entity.GetObjectBuilder(false);
				}
				finally
				{
					DiscardDormant(slot);
					ReleaseSerialUpdate(plan);
				}
			});
			Stopwatch compare = Stopwatch.StartNew();
			List<string> residuals = imported == null ? new List<string> { "no dormant entity" } : ImportRoundTrip.ResidualsAgainstPrepared(prepared, new[] { imported });
			Log("import warm-up done deserializeMs=" + F(deserializeMs) + " stageInitMs=" + F(stageMs)
				+ " compareMs=" + F(compare.Elapsed.TotalMilliseconds) + " residuals=" + residuals.Count
				+ " totalWallMs=" + F(wall.Elapsed.TotalMilliseconds));
			// CH 2026-10-08 10:08: residuals=N alone hides WHICH fields differ; the monitor compares this shape per boot
			// with the recorded one (cluster-regression regression/warmup_shape.py).
			List<string> shape = ImportRoundTrip.ResidualShape(residuals, out string shapeHash);
			Log("import warm-up residual shape=" + shapeHash + " fields=" + shape.Count + " [" + string.Join(" | ", shape) + "]");
		}
		catch (Exception e)
		{
			try { Invoke(() => { foreach (StagedEntity slot in plan.Staged) DiscardDormant(slot); ReleaseSerialUpdate(plan); return 0; }); }
			catch (Exception cleanup) { Log("import warm-up cleanup failed: " + cleanup.Message); }
			Log("import warm-up failed after " + F(wall.Elapsed.TotalMilliseconds) + " ms: " + e.Message);
		}
	}

	private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

	/// <summary>
	/// A small grid shaped like the regression Visitors' essentials: cockpit, gyro, battery, thruster,
	/// armour. Parked far outside every planet's gravity well so Init asks nothing of the voxel world.
	/// </summary>
	private static MyObjectBuilder_CubeGrid WarmUpGrid()
	{
		long gridId = MyEntityIdentifier.AllocateId();
		MyObjectBuilder_CubeGrid grid = new MyObjectBuilder_CubeGrid
		{
			EntityId = gridId,
			GridSizeEnum = MyCubeSize.Small,
			IsStatic = false,
			DisplayName = "Cluster import warm-up",
			PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows,
			PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(9.0e6, 9.0e6, 9.0e6), Vector3.Forward, Vector3.Up),
			LinearVelocity = Vector3.Zero,
			AngularVelocity = Vector3.Zero,
			CubeBlocks = new List<MyObjectBuilder_CubeBlock>(),
		};
		grid.CubeBlocks.Add(Block<MyObjectBuilder_Cockpit>("SmallBlockCockpit", 0));
		grid.CubeBlocks.Add(Block<MyObjectBuilder_Gyro>("SmallBlockGyro", 1));
		grid.CubeBlocks.Add(Block<MyObjectBuilder_BatteryBlock>("SmallBlockBatteryBlock", 2));
		grid.CubeBlocks.Add(Block<MyObjectBuilder_Thrust>("SmallBlockSmallThrust", 3));
		MyObjectBuilder_CubeBlock armour = Block<MyObjectBuilder_CubeBlock>("SmallBlockArmorBlock", 4);
		armour.EntityId = 0;
		grid.CubeBlocks.Add(armour);
		return grid;
	}

	private static T Block<T>(string subtype, int x) where T : MyObjectBuilder_CubeBlock, new()
	{
		T block = new T();
		block.SubtypeName = subtype;
		block.EntityId = MyEntityIdentifier.AllocateId();
		block.Min = new SerializableVector3I(x, 0, 0);
		block.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
		block.ColorMaskHSV = new SerializableVector3(0, -1, 0);
		block.BuildPercent = 1f;
		block.IntegrityPercent = 1f;
		return block;
	}
}
