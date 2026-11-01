using System.Runtime.CompilerServices;
using System.Threading;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyEntity"/>: a cluster instance field of the game type, provided as a C# 14
/// extension property so <c>entity.SimCostTicks</c> in Shared/Cluster reads unchanged.
/// </summary>
public static class MyEntitySeams
{
	private sealed class SimCost
	{
		public long Ticks;
	}

	// Keyed by the entity instance, so the counter dies with it exactly as the field did.
	private static readonly ConditionalWeakTable<MyEntity, SimCost> m_simCost = new ConditionalWeakTable<MyEntity, SimCost>();

	extension(MyEntity self)
	{
		/// <summary>
		/// Cumulative Stopwatch ticks this entity's Update*/Simulate calls have held (game thread and
		/// parallel workers alike; written with Interlocked.Add from the update orchestrator). The
		/// cluster plugin reads deltas per partition as the balancer's CPU-cost proxy. Monotonic for
		/// the entity's lifetime; never reset.
		///
		/// The writer was MyParallelEntityUpdateOrchestrator (batch E1-handover-engine): its dispatch
		/// loops charge each entity with chained timestamps. Until that writer is ported the value is 0
		/// and PartitionManager.SampleSimLoad sees only the physics share.
		/// Writers must use <see cref="AddSimCostTicks"/>: a ConditionalWeakTable cell cannot be passed
		/// by ref, so <c>Interlocked.Add(ref entity.SimCostTicks, ...)</c> has no equivalent here.
		/// </summary>
		public long SimCostTicks
		{
			get => m_simCost.TryGetValue(self, out SimCost cell) ? Volatile.Read(ref cell.Ticks) : 0L;
			set => Volatile.Write(ref m_simCost.GetOrCreateValue(self).Ticks, value);
		}

		/// <summary>Thread-safe increment; the replacement for <c>Interlocked.Add(ref entity.SimCostTicks, delta)</c>.</summary>
		public void AddSimCostTicks(long ticks)
		{
			Interlocked.Add(ref m_simCost.GetOrCreateValue(self).Ticks, ticks);
		}
	}
}
