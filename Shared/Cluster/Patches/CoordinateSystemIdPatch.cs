using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.GameSystems.CoordinateSystem;

namespace ClusterPlugin;

/// <summary>
/// Cluster-wide allocation of local coordinate-system ids (T-0046).
///
/// THE DEFECT. <c>MyCoordinateSystem</c> allocates ids from a per-process counter -
/// <c>do { num = m_lastCoordSysId++; } while (m_localCoordSystems.ContainsKey(num));</c> - seeded
/// from the world's <c>LastCoordSysId</c>. Within ONE server process that is sound: the counter is
/// monotonic, so a client can never be sent the same id twice. A cluster is many server processes
/// sharing one client, and every node loads the same base world, so every node's counter starts at
/// the same value. Measured on 2026-09-05: all six nodes sat at <c>LastCoordSysId=2</c>, so the
/// first static-grid split, paste or build on any node issues id 2.
///
/// Creation is broadcast to clients with <c>[Event][Reliable][BroadcastExcept] CoordSysCreated_Client</c>,
/// whose handler does <c>m_localCoordSystems.Add(coordSysId, ...)</c>. A handover never reloads the
/// client session, so a client keeps the ids of every node it has been attached to and the second
/// node's id 2 throws:
///
///     System.ArgumentException: An item with the same key has already been added. Key: 2
///        at MyCoordinateSystem.CreateCoordSys_ClientInternal(MyTransformD&amp;, Int64)
///        at MyCoordinateSystem.CoordSysCreated_Client(MyCreateCoordSysBuffer)
///        at MyReceiveQueue.Process(NetworkMessageDelegate)
///
/// <c>MyReceiveQueue.Process</c> rethrows on clients, so this is a client PROCESS CRASH, not a
/// disconnect. It killed Viktor's own client on 2026-09-04 20:44:14 and a batch of bots on
/// 2026-08-25. EntityIds and NetworkIds are coordinated cluster-wide; these were not.
///
/// THE FIX. Reuse the range the registry has already issued this node. EntityIds are
/// <c>rangeIndex(32) &lt;&lt; 24 | offset(24)</c> with every index issued exactly once and never 0
/// (<c>EntityIdAllocator</c>), so <c>rangeIndex &lt;&lt; 24</c> is a value no other node can reach.
/// Seeding the counter there gives each node 2^24 coordinate ids that are globally unique BY
/// CONSTRUCTION - no registry call, no gateway change, no collision check.
///
/// Coordinate systems are created only by large static grids, so 16.7 M per node is far beyond
/// any run: w29 consumed one per node in 26 hours.
/// </summary>
public static class CoordinateSystemIdPatch
{
	/// <summary>Set by ClusterRuntime; null or false outside a cluster, where the vanilla counter is correct.</summary>
	public static Func<bool> IsClustered;

	private static readonly FieldInfo LastCoordSysIdField =
		AccessTools.Field(typeof(MyCoordinateSystem), "m_lastCoordSysId");

	/// <summary>
	/// Reseed the counter after the session component has loaded it from the world. Runs once per
	/// session load, before any coordinate system can be created: registration draws the node's
	/// EntityId range during ClusterRuntime start, which completes before the session is loaded.
	/// </summary>
	public static void Reseed(MyCoordinateSystem instance)
	{
		if (instance == null || IsClustered?.Invoke() != true) return;
		if (!EntityIdAllocator.Enabled) return;                       // standalone, or range not yet issued
		uint index = EntityIdAllocator.CurrentRangeIndex;
		if (index == 0) return;                                        // the registry never issues 0
		if (LastCoordSysIdField == null) return;                       // field renamed by a game update

		long baseId = (long)index << EntityIdAllocator.RangeBits;
		long current = (long)LastCoordSysIdField.GetValue(instance);
		// Never move the counter DOWN. A world saved by this node already carries ids at or above
		// its own base, and reusing them would collide with its own live systems.
		if (current >= baseId)
		{
			Log($"[ClusterPlugin] coord_sys_id already node-scoped: counter={current} base={baseId} index={index}");
			return;
		}
		LastCoordSysIdField.SetValue(instance, baseId);
		Log($"[ClusterPlugin] coord_sys_id range allocated index={index} base={baseId} was={current}");
	}

	private static void Log(string message)
	{
		try { Console.WriteLine(message); } catch { /* logging must never break session load */ }
	}
}
