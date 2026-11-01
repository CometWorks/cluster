using System.Diagnostics;
using Havok;
using Sandbox.Engine.Physics;
using VRage.Collections;
using VRage.Game.Entity;
using VRageMath.Spatial;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyPhysics"/>: two cluster static members of the
/// game type. Written against the publicized assemblies; no reflection.
/// </summary>
public static class MyPhysicsSeams
{
	/// <summary>
	/// CLUSTER-SIM-COST: cumulative <see cref="Stopwatch"/> ticks StepWorlds has held the game thread.
	/// The prefix/postfix pair of Shared.Patches.MyPhysicsPatch on MyPhysics.StepWorlds maintains it
	/// (SimulateInternal calls StepWorlds exactly once per physics step). Written only
	/// on the game thread, read as deltas by PartitionManager.SampleSimLoad to price the physics share of
	/// each partition's load. Monotonic; never reset.
	/// </summary>
	public static long StepWallTicks;

	/// <summary>
	/// ClusterPlugin diagnostic seam (P1/P2-CHR-02 inert character). IsInWorld, IsActive and
	/// hasProxy all read healthy on a body whose Havok world exists but whose rigid/proxy body
	/// never entered that world's simulation lists - the one state no public member exposes to a
	/// plugin without a HavokWrapper reference. Names the body's cluster and counts what the
	/// world actually holds; returned as one string so the caller needs no Havok types.
	/// </summary>
	public static string DescribeBodyWorld(MyEntity entity)
	{
		if (entity?.Physics is not MyPhysicsBody body)
		{
			return "no-physics";
		}
		HkWorld world = body.HavokWorld;
		if (world == null)
		{
			return "no-world";
		}
		MyClusterTree.MyCluster cluster = null;
		int total = 0;
		if (MyPhysics.Clusters != null)
		{
			ListReader<MyClusterTree.MyCluster> clusters = MyPhysics.Clusters.GetClusters();
			total = clusters.Count;
			foreach (MyClusterTree.MyCluster candidate in clusters)
			{
				if (ReferenceEquals(candidate.UserData, world))
				{
					cluster = candidate;
					break;
				}
			}
		}
		HkRigidBody proxyBody = body.CharacterProxy?.GetHitRigidBody();
		return "cluster=" + (cluster != null ? cluster.ClusterId.ToString() : "?")
			+ " clusterObjects=" + (cluster != null ? cluster.Objects.Count.ToString() : "?")
			+ " clustersTotal=" + total
			+ " worldCharBodies=" + world.CharacterRigidBodies.Count
			+ " rigidBodyInWorld=" + (body.RigidBody != null ? body.RigidBody.InWorld.ToString() : "none")
			+ " proxyBodyInWorld=" + (proxyBody != null ? proxyBody.InWorld.ToString() : "none");
	}
}
