using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using VRage.ModAPI;
using VRageMath;

namespace ClusterPlugin;

internal static partial class OfflineCompensation
{
	internal static Vector3D SolarPivot(Vector3 pivot, MatrixD matrix) => matrix.Translation
		+ matrix.Right * pivot.X + matrix.Up * pivot.Y + matrix.Forward * pivot.Z;
	private static bool IsEnvironmental(MyCubeBlock block) => block is MySolarPanel or MyOxygenFarm;

	/// <summary>Snapshot geometry on the game thread, ray-test off-thread, as the engine's own solar
	/// component does. Neither the world's sun nor a live block's measured output is changed.</summary>
	private static Task<double[]> SampleSun(MyCubeBlock block)
	{
		MatrixD matrix = block.WorldMatrix;
		MyCubeGrid grid = block.CubeGrid;
		bool functional = block.IsFunctional && grid.Physics != null;
		Vector3 normal;
		bool twoSided;
		Vector3D[] pivots, alternate = null;
		bool legacy;
		if (block is MySolarPanel solar && solar.SolarComponent is MyNewSolarGameLogicComponent modern)
		{
			normal = Vector3.Transform(modern.PanelOrientation, matrix.GetOrientation());
			twoSided = modern.m_isTwoSided;
			legacy = false;
			pivots = Enumerable.Range(0, modern.PivotAmount).Select(i => SolarPivot(modern.m_pivots[i * (twoSided ? 2 : 1)], matrix)).ToArray();
			if (twoSided) alternate = Enumerable.Range(0, modern.PivotAmount).Select(i => SolarPivot(modern.m_pivots[i * 2 + 1], matrix)).ToArray();
		}
		else
		{
			MySolarGameLogicComponent component = block is MyOxygenFarm farm ? farm.SolarComponent : ((MySolarPanel)block).SolarComponent as MySolarGameLogicComponent;
			if (component == null) return Task.FromException<double[]>(new InvalidOperationException("Unknown solar component"));
			normal = Vector3.Transform(component.PanelOrientation, matrix.GetOrientation());
			twoSided = component.m_isTwoSided;
			legacy = true;
			double direction = Vector3D.Dot(matrix.Forward, normal);
			double size = grid.GridSize;
			pivots = Enumerable.Range(0, 8).Select(i => matrix.Translation
				+ ((i % 4) - 1.5) * size * direction * (block.BlockDefinition.Size.X / 4.0) * matrix.Left
				+ ((i / 4) - 0.5) * size * direction * (block.BlockDefinition.Size.Y / 2.0) * matrix.Up
				+ size * direction * (block.BlockDefinition.Size.Z / 2.0) * (Vector3D)normal * component.PanelOffset).ToArray();
		}
		var planets = MyPlanets.GetPlanets().Select(p => (Center: p.PositionComp.GetPosition(), Radius: (double)p.Provider.Radius, Maximum: (double)p.MaximumRadius)).ToArray();
		var weather = MySession.Static.GetComponent<MySectorWeatherComponent>();
		Vector3 baseSun = weather?.m_baseSunDirection ?? MySector.SunProperties.BaseSunDirectionNormalized;
		Vector3 axis = weather?.m_sunRotationAxis ?? MySector.SunProperties.SunRotationAxis;
		Vector3 currentSun = MySector.DirectionToSunNormalized;
		bool rotating = MySession.Static.Settings.EnableSunRotation;
		int steps = rotating ? Math.Clamp(ClusterConfig.GetInt("offline.solarSteps"), 4, 720) : 1;
		Vector3D gridPosition = grid.PositionComp.GetPosition();
		return Task.Run(() =>
		{
			var curve = new double[steps];
			if (!functional) return curve;
			var hits = new List<MyPhysics.HitInfo>();
			for (int step = 0; step < steps; step++)
			{
				if (grid.MarkedForClose || block.MarkedForClose) throw new InvalidOperationException("Solar geometry closed while sampling");
				Vector3 sun = rotating ? Vector3.Normalize(Vector3.Transform(baseSun, Matrix.CreateFromAxisAngle(axis, (float)(step * 2 * Math.PI / steps)))) : currentSun;
				double angle = Vector3.Dot(normal, sun);
				if (angle < 0 && !twoSided) continue;
				var nearest = planets.OrderBy(p => Vector3D.DistanceSquared(p.Center, matrix.Translation)).FirstOrDefault();
				if (nearest.Maximum > 0 && Vector3D.Distance(matrix.Translation, nearest.Center) <= nearest.Maximum * 1.1
					&& Vector3D.Dot(sun, Vector3D.Normalize(matrix.Translation - nearest.Center)) < -0.1) continue;
				bool shadow = planets.Any(p =>
				{
					Vector3D offset = gridPosition - p.Center;
					double distance = Vector3D.Dot(offset, sun);
					double radius = p.Radius > 500 ? p.Radius - 500 : p.Radius * 0.5;
					return distance <= 0 && -distance <= 20 * p.Radius && (offset - (Vector3D)sun * distance).LengthSquared() < radius * radius;
				});
				if (shadow) continue;
				int lit = 0;
				for (int i = 0; i < pivots.Length; i++)
				{
					Vector3D pivot = pivots[i];
					if (alternate != null && Vector3D.DistanceSquared(alternate[i], matrix.Translation + sun * 100) < Vector3D.DistanceSquared(pivot, matrix.Translation + sun * 100)) pivot = alternate[i];
					Vector3D from = pivot + sun * 100, to = legacy ? pivot + sun * grid.GridSize / 4 : pivot;
					hits.Clear(); MyPhysics.CastRay(to, from, hits, 15);
					bool visible = true;
					foreach (var hit in hits)
					{
						IMyEntity entity = hit.HkHitInfo.GetHitEntity();
						if (entity != grid) { visible = false; break; }
						Vector3I? cell = grid.RayCastBlocks(from, to);
						if (cell.HasValue && grid.GetCubeBlock(cell.Value) != block.SlimBlock) { visible = false; break; }
					}
					if (visible) lit++;
				}
				curve[step] = Math.Clamp(Math.Abs(angle) * lit / pivots.Length, 0, 1); // weather expectation = 1
			}
			return curve;
		});
	}
}
