using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage;
using VRage.Game;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE1-0231: the safe zone of a safe zone block travels inside the block's object builder.
/// <para>
/// The zone is a separate <see cref="MySafeZone"/> entity, not a partition member, so neither a handover
/// nor a partition save carried it. The source's close of the grid then removed it (vanilla
/// <c>MySafeZoneBlock.OnRemovedFromScene</c>), and the block arrived pointing at a zone id no node had.
/// Vanilla only creates a zone while that id is 0, so the base stayed unprotected until a player switched
/// the block off and on.
/// </para>
/// <para>
/// The zone's builder is written into the block's builder as a <see cref="BlockBuilderEntry"/> (<see cref="Key"/>).
/// Handovers, partition saves, offline partitions and recovery all move the block's builder, so they all
/// carry the zone. A node that adds the block to its scene and has no entity under the block's zone id
/// creates the zone from the entry, with the same id and settings. The zone chips and the upkeep clock
/// are the block's own state and travel anyway. A node that closes the block still removes its zone, as
/// vanilla does: the copy left on the source of a handover is the one that has to go.
/// </para>
/// </summary>
internal static partial class BlockSafeZones
{
	/// <summary>The entry the block's builders carry: the zone's builder as XML, null when it has no zone.</summary>
	private sealed class State
	{
		public string Value;
	}

	private static readonly ConditionalWeakTable<MySafeZoneBlock, State> States = new ConditionalWeakTable<MySafeZoneBlock, State>();

	private static bool Active => ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority && Sync.IsServer;

	private static bool OnGameThread => MySandboxGame.Static != null && Thread.CurrentThread == MySandboxGame.Static.UpdateThread;

	/// <summary>MySafeZoneBlock.Init postfix, any thread: take the entry off the arriving builder.</summary>
	internal static void Initialized(MySafeZoneBlock block, MyObjectBuilder_CubeBlock builder)
	{
		if (!Active) return;
		States.AddOrUpdate(block, new State { Value = BlockBuilderEntry.Read(builder, Key) });
		BlockBuilderEntry.Forget(block, Key);
	}

	/// <summary>MySafeZoneBlock.GetObjectBuilderCubeBlock postfix: write the entry into the builder.</summary>
	internal static void Saving(MySafeZoneBlock block, MyObjectBuilder_CubeBlock builder, bool copy)
	{
		// A copy (blueprint, clipboard) does not carry the zone id either: a pasted block makes its own zone.
		if (!Active || copy || builder == null) return;
		State state = States.GetValue(block, _ => new State());
		// Off the game thread the last value stands. While the zone waits to be restored, the arrived value does.
		if (OnGameThread)
		{
			long zoneId = block.m_safeZoneManager?.SafeZoneEntityId ?? 0;
			if (zoneId == 0) state.Value = null;
			else if (Zone(block, zoneId) is MySafeZone zone)
			{
				try { state.Value = Serialize((MyObjectBuilder_SafeZone)zone.GetObjectBuilder()); }
				catch (Exception error)
				{
					// The previous entry stands; a save must not fail over the zone.
					MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] safe zone not serialized block=" + block.EntityId + ": " + error.Message);
				}
			}
		}
		if (state.Value != null) BlockBuilderEntry.Write(builder, Key, state.Value);
	}

	/// <summary>MySafeZoneBlock.OnAddedToScene postfix: restore the zone on the next frame, once the add is done.</summary>
	internal static void AddedToScene(MySafeZoneBlock block)
	{
		if (!Active || !States.TryGetValue(block, out State state) || state.Value == null) return;
		MySandboxGame.Static?.Invoke(() => Restore(block), "SE1-0231 safe zone");
	}

	private static void Restore(MySafeZoneBlock block)
	{
		if (MySession.Static?.Ready != true)
		{
			MySandboxGame.Static?.Invoke(() => Restore(block), "SE1-0231 safe zone");
			return;
		}
		if (block.MarkedForClose || block.Closed || !States.TryGetValue(block, out State state) || state.Value == null) return;
		long zoneId = block.m_safeZoneManager?.SafeZoneEntityId ?? 0;
		// Switched off since, or already here (a world file that holds the zone, a second add to the scene).
		if (zoneId == 0 || Zone(block, zoneId) != null) return;
		try
		{
			if (MyEntities.EntityExists(zoneId))
				throw new InvalidOperationException("entity " + zoneId + " exists and is not this block's zone");
			if (!block.CubeGrid.IsStatic)
				throw new InvalidOperationException("the grid is not static");
			MyObjectBuilder_SafeZone zone = Parse(state.Value);
			if (zone.EntityId != zoneId || zone.SafeZoneBlockId != block.EntityId)
				throw new InvalidOperationException("the entry is zone " + zone.EntityId + " of block " + zone.SafeZoneBlockId);
			// Where vanilla puts a block's zone: on the block.
			zone.PositionAndOrientation = new MyPositionAndOrientation(block.WorldMatrix);
			MyEntities.CreateFromObjectBuilderAndAdd(zone, fadeIn: false);
			block.OnSafeZoneChanged();
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] safe zone restored block=" + block.EntityId + " zone=" + zoneId
				+ " enabled=" + zone.Enabled);
		}
		catch (Exception error)
		{
			// The block keeps its entry and its zone id, so a later save still carries the zone.
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] safe zone not restored block=" + block.EntityId + " zone=" + zoneId
				+ ": " + error.GetType().Name + ": " + error.Message);
		}
	}

	private static MySafeZone Zone(MySafeZoneBlock block, long zoneId) =>
		MyEntities.TryGetEntityById(zoneId, out MySafeZone zone) && !zone.MarkedForClose && zone.SafeZoneBlockId == block.EntityId ? zone : null;
}
