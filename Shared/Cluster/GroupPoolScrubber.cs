using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage.Groups;

namespace ClusterPlugin;

/// <summary>
/// T-0147: lets the GC have the grids a release closed.
///
/// A node heap dump (node-5, 2026-09-14 17:42, 1226 s old, 7.7 GB RSS) held 2375 CLOSED MyCubeGrid
/// objects of 2594. The retention paths end in two vanilla structures that nothing ever empties:
///
/// 1. MyGroups.m_groupPool. Closing a grid marks its blocks MarkedForClose BEFORE the grid leaves its
///    logical group, and both removal paths that group data relies on bail out on that flag:
///    MyGridTerminalSystem.Remove returns early, and MyResourceDistributorComponent.RemoveSink(...,
///    markedForClose: true) returns early. When the last grid leaves, the group is pushed onto the pool
///    with its MyGridTerminalSystem and MyGridResourceDistributorSystem still holding every dead block,
///    sink and source. MyGridLogicalGroupData.OnRelease clears only per-type distributor data. A vanilla
///    server closes few grids; a cluster node releases whole partitions every few minutes, so the pool
///    fills with dead grids and only a restart frees them.
/// 2. MyFixedGrids.m_nodes. Connector links recorded there are not broken when a grid closes, so the
///    node, keyed by the closed grid, keeps that grid alive.
///
/// The fix drops pooled groups (the next AcquireGroup allocates a fresh one, which is what vanilla does
/// whenever the pool is empty) and breaks the fixed-grid links of closed grids. Both run on the game
/// thread from ClusterRuntime.Update, the thread every MyGroups mutation happens on. Closed nodes left in
/// the cube-grid groups were counted, not touched, until section 9 (2026-09-15): they turned out to be
/// the anchor of the closed grids every earlier section left reachable.
/// </summary>
internal static class GroupPoolScrubber
{
	/// <summary>Seconds between scrubs. 0 disables.</summary>
	private static readonly double IntervalSeconds = ReadDouble("CLUSTER_GROUP_SCRUB_SECONDS", 60.0);

	private static readonly FieldInfo FixedGridsStatic = AccessTools.Field(typeof(MyFixedGrids), "m_static");

	/// <summary>
	/// 3. MyBlueprintIdTracker.TrackedIdToProjector (static). A projector's reserved ids leave it only when
	///    the PROJECTION is removed; closing a projector that is still projecting never calls OnRemove, so
	///    the dictionary pins the projector and, through it, its whole grid. On the dump this one holder
	///    alone kept 968 closed grids / 598 MB reachable - five times the pools and fixed grids together.
	/// </summary>
	private static readonly FieldInfo TrackedIdToProjector = AccessTools.Field(typeof(MyBlueprintIdTracker), "TrackedIdToProjector");

	private static double m_nextScrub;
	private static long m_droppedTotal;
	private static long m_fixedReleasedTotal;
	private static long m_updatingRemovedTotal;
	private static long m_scrubs;
	/// <summary>Coverage for the heartbeat: sizes of the two vanilla tables sections 7 and 8 prune; -1 = not found.</summary>
	private static int m_trackedEntities = -1, m_hazardReceivers = -1, m_identities = -1;

	private static double ReadDouble(string name, double fallback)
	{
		string raw = Environment.GetEnvironmentVariable(name);
		return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			? value
			: fallback;
	}

	internal static void Update()
	{
		if (IntervalSeconds <= 0) return;
		if (MySession.Static?.Ready != true) return;
		double now = MySession.Static.ElapsedGameTime.TotalSeconds;
		if (now < m_nextScrub) return;
		m_nextScrub = now + IntervalSeconds;
		try
		{
			Scrub();
		}
		catch (Exception ex)
		{
			// A game update that renamed a field must cost the scrub, not the node.
			Console.WriteLine("[ClusterPlugin] group_pool_scrub failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void Scrub()
	{
		int dropped = 0;
		int closedGroupNodes = 0, groupNodesReleased = 0, groupNodesFailed = 0;
		MyCubeGridGroups groups = MyCubeGridGroups.Static;
		if (groups != null)
		{
			foreach (object instance in new object[] { groups.Logical, groups.Physical, groups.NoContactDamage,
				groups.Mechanical, groups.Electrical, groups.PhysicalDynamic, groups.SmallToLargeBlockConnections })
			{
				dropped += DropPool(instance);
				(int closedNodes, int released, int failed) = ReleaseClosedNodes(instance);
				closedGroupNodes += closedNodes;
				groupNodesReleased += released;
				groupNodesFailed += failed;
			}
		}
		dropped += DropPool(MyGridPhysicalHierarchy.Static);
		{
			(int closedNodes, int released, int failed) = ReleaseClosedNodes(MyGridPhysicalHierarchy.Static);
			closedGroupNodes += closedNodes;
			groupNodesReleased += released;
			groupNodesFailed += failed;
		}

		int fixedClosed = 0, fixedReleased = 0;
		if (FixedGridsStatic?.GetValue(null) is MyFixedGrids fixedGrids)
		{
			dropped += DropPool(fixedGrids);
			List<MyCubeGrid> closed = ClosedGrids(fixedGrids);
			fixedClosed = closed.Count;
			foreach (MyCubeGrid grid in closed)
			{
				fixedGrids.BreakAllLinks(grid);
				if (fixedGrids.GetNode(grid) == null) fixedReleased++;
			}
		}

		int projectorsForgotten = 0, idsForgotten = 0;
		if (TrackedIdToProjector?.GetValue(null) is Dictionary<long, MyProjectorBase> tracked && tracked.Count > 0)
		{
			Dictionary<MyProjectorBase, HashSet<long>> dead = null;
			foreach (KeyValuePair<long, MyProjectorBase> entry in tracked)
			{
				if (entry.Value is not { Closed: true } projector) continue;
				dead ??= new Dictionary<MyProjectorBase, HashSet<long>>();
				if (!dead.TryGetValue(projector, out HashSet<long> ids)) dead[projector] = ids = new HashSet<long>();
				ids.Add(entry.Key);
			}
			if (dead != null)
				foreach (KeyValuePair<MyProjectorBase, HashSet<long>> entry in dead)
				{
					// The vanilla entry point, so the tracker's events fire exactly as for a removed projection.
					MyBlueprintIdTracker.OnRemove(entry.Key, entry.Value);
					projectorsForgotten++;
					idsForgotten += entry.Value.Count;
				}
		}

		int updatingRemoved = ScrubComponentUpdater();
		(int liveTerminalBlocks, int liveTerminalSystems) = ScrubLiveTerminalSystems();
		(int animationQueued, int dependencyParents, int dependencies) = ScrubCharacters();
		(int untracked, int trackedEntities) = ScrubProceduralTracking();
		(int hazardReceivers, int hazardReceiversLeft) = ScrubHazardReceivers();
		(int identityHandlers, int identities) = ScrubIdentityHandlers();
		m_trackedEntities = trackedEntities;
		m_hazardReceivers = hazardReceiversLeft;
		m_identities = identities;
		if (animationQueued > 0 || dependencyParents > 0 || dependencies > 0 || untracked > 0 || hazardReceivers > 0 || identityHandlers > 0)
			Console.WriteLine("[ClusterPlugin] group_pool_scrub closedAnimationControllers=" + animationQueued
				+ " staleDependencyParents=" + dependencyParents + " staleDependencies=" + dependencies
				+ " closedTrackedEntities=" + untracked + " closedHazardReceivers=" + hazardReceivers
				+ " staleIdentityHandlers=" + identityHandlers);
		if (liveTerminalBlocks > 0)
			Console.WriteLine("[ClusterPlugin] group_pool_scrub liveTerminalClosedBlocks=" + liveTerminalBlocks + " terminalSystems=" + liveTerminalSystems);

		m_droppedTotal += dropped;
		m_fixedReleasedTotal += fixedReleased;
		m_updatingRemovedTotal += updatingRemoved;
		m_scrubs++;
		// A heartbeat, so a scrubber that removes nothing is distinguishable from one that does not run:
		// node-2 went 2 h without a group_pool_scrub line on 2026-09-15 while 569 closed grids piled up.
		if (m_scrubs % 10 == 0)
			Console.WriteLine("[ClusterPlugin] group_pool_scrub heartbeat scrubs=" + m_scrubs + " droppedTotal=" + m_droppedTotal
				+ " fixedReleasedTotal=" + m_fixedReleasedTotal + " closedUpdatingComponentsTotal=" + m_updatingRemovedTotal
				+ " trackedEntities=" + m_trackedEntities + " hazardReceivers=" + m_hazardReceivers + " identities=" + m_identities);
		if (dropped > 0 || fixedClosed > 0 || closedGroupNodes > 0 || projectorsForgotten > 0 || updatingRemoved > 0)
			Console.WriteLine("[ClusterPlugin] group_pool_scrub dropped=" + dropped + " fixedClosed=" + fixedClosed
				+ " fixedReleased=" + fixedReleased + " closedGroupNodes=" + closedGroupNodes
				+ " groupNodesReleased=" + groupNodesReleased + " groupNodesFailed=" + groupNodesFailed
				+ " closedProjectors=" + projectorsForgotten + " trackedIds=" + idsForgotten
				+ " closedUpdatingComponents=" + updatingRemoved
				+ " droppedTotal=" + m_droppedTotal + " fixedReleasedTotal=" + m_fixedReleasedTotal);
	}

	private static readonly string[] UpdaterSets =
	{
		"m_componentsForUpdateBeforeOnce", "m_componentsForUpdateBefore", "m_componentsForParallelUpdateBefore",
		"m_componentsForParallelUpdateAfter", "m_componentsForUpdateSimulate", "m_componentsForUpdateAfter",
	};

	private static readonly string[] UpdaterDistributed =
	{
		"m_componentsForUpdateBefore100", "m_componentsForUpdateAfter10", "m_componentsForUpdateAfter100",
	};

	/// <summary>
	/// 4. MyEntityComponentUpdater update sets. OnEntityClosing queues a component for removal, but
	///    ApplyChanges removes it with the component's NeedsUpdate AT THAT TIME; a component whose flags were
	///    cleared during close (the character ladder component) is removed from no set and updates - and pins
	///    its character - forever. On the dump 66 of 70 MyCharacter objects were closed, and their replicables
	///    listed 4289 closed grids in m_dependencyParents: the ~1215 closed grids no other holder explained.
	/// </summary>
	private static int ScrubComponentUpdater()
	{
		VRage.Game.Components.Session.MyEntityComponentUpdater updater =
			MySession.Static.GetComponent<VRage.Game.Components.Session.MyEntityComponentUpdater>();
		if (updater == null) return 0;
		int removed = 0;
		foreach (string name in UpdaterSets)
		{
			if (Field(updater.GetType(), name)?.GetValue(updater) is HashSet<VRage.Game.Entity.EntityComponents.Interfaces.IMyUpdatingEntityComponent> set)
				removed += set.RemoveWhere(IsClosed);
		}
		foreach (string name in UpdaterDistributed)
		{
			if (Field(updater.GetType(), name)?.GetValue(updater) is not VRage.Collections.MyDistributedTypeUpdater<VRage.Game.Entity.EntityComponents.Interfaces.IMyUpdatingEntityComponent> distributed)
				continue;
			List<VRage.Game.Entity.EntityComponents.Interfaces.IMyUpdatingEntityComponent> dead = null;
			foreach (var component in distributed)
				if (IsClosed(component)) (dead ??= new()).Add(component);
			if (dead == null) continue;
			foreach (var component in dead) distributed.Remove(component);
			removed += dead.Count;
		}
		return removed;
	}

	/// <summary>
	/// 5. Terminal systems of LIVE logical groups. The same early return that fills the pool (Remove bails on
	///    block.MarkedForClose) also applies when a closed grid leaves a group that stays alive, so the live
	///    group's MyGridTerminalSystem keeps the dead blocks, and each block keeps its closed grid. On a node-1
	///    dump taken 21 min after e9de1bd went live, 482 closed grids were still reachable with every earlier
	///    holder empty; the direct anchors ran through live MyGridTerminalSystem block sets. Removes closed
	///    blocks from the four block collections and from every block group, without events: a closed block
	///    has no listener left that should hear about it.
	/// </summary>
	private static (int Blocks, int Systems) ScrubLiveTerminalSystems()
	{
		MyCubeGridGroups groups = MyCubeGridGroups.Static;
		if (groups == null) return (0, 0);
		int removed = 0, systems = 0;
		foreach (var group in groups.Logical.Groups)
		{
			if (Field(typeof(MyGridLogicalGroupData), "TerminalSystem")?.GetValue(group.GroupData) is not Sandbox.Game.GameSystems.MyGridTerminalSystem terminal)
				continue;
			Type type = terminal.GetType();
			int before = removed;
			if (Field(type, "m_blocks")?.GetValue(terminal) is HashSet<Sandbox.Game.Entities.Cube.MyTerminalBlock> blocks)
				removed += blocks.RemoveWhere(IsClosedBlock);
			if (Field(type, "m_blocksForHud")?.GetValue(terminal) is HashSet<Sandbox.Game.Entities.Cube.MyTerminalBlock> hud)
				hud.RemoveWhere(IsClosedBlock);
			if (Field(type, "m_blockList")?.GetValue(terminal) is List<Sandbox.Game.Entities.Cube.MyTerminalBlock> list)
				list.RemoveAll(IsClosedBlock);
			if (Field(type, "m_blockTable")?.GetValue(terminal) is Dictionary<long, Sandbox.Game.Entities.Cube.MyTerminalBlock> table)
			{
				List<long> dead = null;
				foreach (KeyValuePair<long, Sandbox.Game.Entities.Cube.MyTerminalBlock> entry in table)
					if (IsClosedBlock(entry.Value)) (dead ??= new List<long>()).Add(entry.Key);
				if (dead != null) foreach (long id in dead) table.Remove(id);
			}
			foreach (Sandbox.Game.GameSystems.MyBlockGroup blockGroup in terminal.BlockGroups)
				if (Field(typeof(Sandbox.Game.GameSystems.MyBlockGroup), "Blocks")?.GetValue(blockGroup) is HashSet<Sandbox.Game.Entities.Cube.MyTerminalBlock> members)
					members.RemoveWhere(IsClosedBlock);
			if (removed > before) systems++;
		}
		return (removed, systems);
	}

	/// <summary>
	/// 6. Characters. On the node-1 dump after 5ced558 (249 closed grids reachable), the direct anchors ran
	///    through MyCharacterReplicable: m_dependencyParents listed 155 closed grids and m_dependencies held
	///    MyFarBroadcasterReplicables of 148 closed antennas. GetDependencies rebuilds both sets only for a
	///    player-controlled character whose dependencies are dirty, so a character nobody controls keeps
	///    the last set forever. The characters themselves stay reachable from
	///    MySessionComponentAnimationSystem, which holds the controllers of closed characters.
	///    - Where exactly: NOT m_skinnedEntityComponents. MySession.LoadWorld sets the animation system to
	///      MyUpdateOrder.NoUpdate on a dedicated server, so UpdateAfterSimulation, the only code that drains
	///      m_skinnedEntityComponentsToAdd and m_skinnedEntityComponentsToRemove, never runs on a node.
	///      Every character ever spawned stays in ToAdd, every closed one also in ToRemove, and the main
	///      set stays empty. node-2 full dump on 7b9bb74 (2026-09-15 11:57, 69 min up): main set 0,
	///      ToAdd 101 (= all live controllers), ToRemove 86 (all with m_container null and a Closed
	///      m_entity), all 8 sampled root paths through ToAdd. That is why the earlier loop over the
	///      main set logged closedAnimationControllers=0 on every node: it walked an empty set.
	///      The vanilla drain is also lossy: it removes from ToAdd only when the main set held the item.
	///    - So this applies the drain itself under the system's lock: everything queued for removal
	///      leaves ToAdd and the main set, ToRemove is cleared, and any remaining controller whose owner
	///      is closed (or unset) leaves both sets too. Nothing is moved from ToAdd into the main set: on a
	///      node the system never updates, and off a node UpdateAfterSimulation still does that itself.
	///    - Every character replicable, live or closed, loses dependency parents that are closed entities
	///      and dependencies that are no longer valid external replicables. A controlled character rebuilds
	///      both on its next dirty GetDependencies, so pruning dead entries changes nothing it would send.
	/// </summary>
	private static (int AnimationQueued, int DependencyParents, int Dependencies) ScrubCharacters()
	{
		int queued = 0, parents = 0, dependencies = 0;
		var characters = new HashSet<Sandbox.Game.Entities.Character.MyCharacter>();
		foreach (VRage.Game.Entity.MyEntity entity in MyEntities.GetEntities())
			if (entity is Sandbox.Game.Entities.Character.MyCharacter character) characters.Add(character);

		VRage.Game.SessionComponents.MySessionComponentAnimationSystem animation = VRage.Game.SessionComponents.MySessionComponentAnimationSystem.Static;
		if (animation != null
			&& Field(animation.GetType(), "m_skinnedEntityComponents")?.GetValue(animation) is HashSet<VRage.Game.Components.MyAnimationControllerComponent> skinned
			&& Field(animation.GetType(), "m_skinnedEntityComponentsToAdd")?.GetValue(animation) is HashSet<VRage.Game.Components.MyAnimationControllerComponent> toAdd
			&& Field(animation.GetType(), "m_skinnedEntityComponentsToRemove")?.GetValue(animation) is HashSet<VRage.Game.Components.MyAnimationControllerComponent> toRemove
			&& Field(animation.GetType(), "m_lock")?.GetValue(animation) is VRage.FastResourceLock gate)
		{
			using (VRage.FastResourceLockExtensions.AcquireExclusiveUsing(gate))
			{
				// The vanilla drain that never runs on a dedicated server (see above), minus its lossy
				// "only if the main set had it" condition: unregistered controllers leave both sets.
				foreach (VRage.Game.Components.MyAnimationControllerComponent controller in toRemove)
				{
					if (controller == null) continue;
					bool dropped = toAdd.Remove(controller);
					dropped |= skinned.Remove(controller);
					if (dropped) queued++;
					if (AnimationOwner(controller) is Sandbox.Game.Entities.Character.MyCharacter closedCharacter) characters.Add(closedCharacter);
				}
				toRemove.Clear();

				// Entity is Container?.Entity, and MyEntity.Close nulls the container before anything here
				// runs, so a closed character's controller answers Entity == null. The component keeps its
				// own m_entity field, which still names the character.
				foreach (HashSet<VRage.Game.Components.MyAnimationControllerComponent> set in new[] { toAdd, skinned })
				{
					queued += set.RemoveWhere(controller =>
					{
						if (controller == null) return true;
						VRage.Game.Entity.MyEntity owner = controller.Entity as VRage.Game.Entity.MyEntity ?? AnimationOwner(controller);
						if (owner is { Closed: false }) return false;
						if (owner is Sandbox.Game.Entities.Character.MyCharacter closedCharacter) characters.Add(closedCharacter);
						return true;
					});
				}
			}
		}

		// Every live entity's replicable, not only characters': the node-2 dump of 5754d20 (2026-09-15
		// 15:05) rooted closed grids in LIVE grid replicables' m_dependencies (MyReplicablesAABB ->
		// MyCubeGridReplicable -> HashSet<IMyReplicable> -> a closed grid's replicable -> the grid) and in
		// closed characters' m_dependencyParents. GetDependencies rebuilds both sets only when dirty, so a
		// quiet replicable keeps closed neighbours forever. Pruning dead entries changes nothing it would
		// send: a closed entity is never replicated again.
		var owners = new HashSet<object>(characters);
		foreach (VRage.Game.Entity.MyEntity entity in MyEntities.GetEntities()) owners.Add(entity);
		foreach (object owner in owners)
		{
			if (Sandbox.Game.Replication.MyExternalReplicable.FindByObject(owner) is not { } replicable) continue;
			Type type = replicable.GetType();
			if (Field(type, "m_dependencyParents")?.GetValue(replicable) is HashSet<VRage.Game.ModAPI.Ingame.IMyEntity> parentSet)
				parents += parentSet.RemoveWhere(e => e == null || e.Closed);
			if (Field(type, "m_dependencies")?.GetValue(replicable) is HashSet<VRage.Network.IMyReplicable> dependencySet)
				dependencies += dependencySet.RemoveWhere(r => r is Sandbox.Game.Replication.MyExternalReplicable { IsValid: false });
		}
		return (queued, parents, dependencies);
	}

	/// <summary>
	/// 7. MyProceduralWorldGenerator.m_trackedEntitiesCache - a guard, not a proven holder. Every dynamic
	///    grid and every character is tracked (asteroid/encounter generation around it) and untracked by an
	///    OnClose handler. My first read of the node-2 dump of d718cef (2026-09-15 13:18) put every sampled
	///    closed grid in this dictionary; that was an alignment artifact (the address list carried
	///    dotnet-dump's preamble lines, so the traced "closed" grids were open ones, which ARE tracked).
	///    Recomputed correctly: 426 grids + 15 characters tracked, none Closed, and all root paths of the
	///    closed grids run through holder 8 below. This section stays because it costs a dictionary walk
	///    per minute and turns any future leak here into a log line; RemoveTrackedEntity is the vanilla
	///    entry point, and marking cells dirty for a closed entity is harmless. Game thread, like every
	///    generator mutation. The heartbeat's trackedEntities= shows the table was reached (-1 = not found).
	/// </summary>
	private static (int Removed, int Tracked) ScrubProceduralTracking()
	{
		Sandbox.Game.World.Generator.MyProceduralWorldGenerator generator = Sandbox.Game.World.Generator.MyProceduralWorldGenerator.Static;
		if (generator == null || Field(generator.GetType(), "m_trackedEntitiesCache")?.GetValue(generator) is not IDictionary tracked)
			return (0, -1); // -1 in the heartbeat: the generator or its field was not found (coverage, not a clean world)
		if (tracked.Count == 0) return (0, 0);
		List<VRage.Game.Entity.MyEntity> dead = null;
		foreach (DictionaryEntry entry in tracked)
			if (entry.Key is VRage.Game.Entity.MyEntity { Closed: true } entity) (dead ??= new List<VRage.Game.Entity.MyEntity>()).Add(entity);
		if (dead == null) return (0, tracked.Count);
		foreach (VRage.Game.Entity.MyEntity entity in dead) generator.RemoveTrackedEntity(entity);
		return (dead.Count, tracked.Count);
	}

	/// <summary>
	/// 8. MyHazardExposureComponent.m_updateStamps: per hazard source, a Dictionary&lt;IMyHazardReceiver, int&gt;
	///    of the last frame each receiver (a character's MyCharacterStatComponent) was processed. Entries are
	///    written on every exposure update and never removed - the component has no receiver removal path at
	///    all - so every character ever exposed stays reachable after it closes, and through its stat
	///    component -> replicable -> dependencies, its grids. Same dump: 71 of 86 live characters Closed,
	///    all sampled ones rooted here. A receiver whose entity is null (container cleared by Close) or
	///    Closed leaves every source's dictionary. A live receiver that is processed again simply gets a
	///    fresh stamp, which is what happens to a receiver the component has never seen anyway.
	/// </summary>
	private static (int Removed, int Remaining) ScrubHazardReceivers()
	{
		SpaceEngineers.Game.SessionComponents.MyHazardExposureComponent hazards = MySession.Static?.GetComponent<SpaceEngineers.Game.SessionComponents.MyHazardExposureComponent>();
		if (hazards == null || Field(hazards.GetType(), "m_updateStamps")?.GetValue(hazards) is not IDictionary stamps)
			return (0, -1); // -1 in the heartbeat: component or field not found
		int removed = 0, remaining = 0;
		List<object> dead = null;
		foreach (DictionaryEntry source in stamps)
		{
			if (source.Value is not IDictionary receivers || receivers.Count == 0) continue;
			dead?.Clear();
			foreach (DictionaryEntry entry in receivers)
			{
				if (entry.Key is not VRage.Game.Components.MyEntityComponentBase component) continue;
				if (component.Entity is { Closed: false }) continue;
				(dead ??= new List<object>()).Add(entry.Key);
			}
			if (dead != null)
			{
				foreach (object key in dead) receivers.Remove(key);
				removed += dead.Count;
			}
			remaining += receivers.Count;
		}
		return (removed, remaining);
	}

	/// <summary>
	/// 10. MyIdentity.FactionChanged handlers of closed cockpits. MyCockpit.AttachPilot subscribes
	///     OnCharacterFactionChanged on the pilot's identity and RemovePilot unsubscribes it - but only when
	///     m_pilot.GetIdentity() still resolves at that moment. On a node the pilot character is routinely gone
	///     (handed over, or closed by a release) before its cockpit closes, so the handler stays on the identity
	///     forever. node-2 full dump on d2d22e4 (18:59, ramp+100): 330 of 868 live grids Closed, and the SHORTEST
	///     root path of every sampled one was MySessionComponentTrash.m_allIdentities -> MyIdentity ->
	///     FactionChanged -> a Closed MyCockpit (m_pilot null) -> its Sync -> MyTerminalReplicable ->
	///     MyCubeGridReplicable -> the closed grid, and on through dependencies into the rest. Identities are
	///     never released, so one seated-then-released ship per pilot per hour accumulates without bound.
	///     Removes every FactionChanged delegate whose target is a closed entity or a block of a closed grid.
	/// </summary>
	private static (int Removed, int Identities) ScrubIdentityHandlers()
	{
		ICollection<MyIdentity> all = MySession.Static?.Players?.GetAllIdentities();
		FieldInfo backing = Field(typeof(MyIdentity), "FactionChanged");
		if (all == null || backing == null) return (0, -1);
		int removed = 0, identities = 0;
		foreach (MyIdentity identity in all)
		{
			identities++;
			if (backing.GetValue(identity) is not Delegate handlers) continue;
			Delegate pruned = handlers;
			foreach (Delegate handler in handlers.GetInvocationList())
			{
				bool dead = handler.Target switch
				{
					MyCubeBlock block => block.Closed || block.CubeGrid is null or { Closed: true },
					VRage.Game.Entity.MyEntity entity => entity.Closed,
					VRage.Game.Components.MyEntityComponentBase component => component.Entity is null or { Closed: true },
					_ => false,
				};
				if (!dead) continue;
				pruned = Delegate.Remove(pruned, handler);
				removed++;
			}
			if (!ReferenceEquals(pruned, handlers)) backing.SetValue(identity, pruned);
		}
		return (removed, identities);
	}

	/// <summary>The entity an animation controller was created for, from its own field; null when unset.</summary>
	private static VRage.Game.Entity.MyEntity AnimationOwner(VRage.Game.Components.MyAnimationControllerComponent controller)
		=> Field(typeof(VRage.Game.Components.MyAnimationControllerComponent), "m_entity")?.GetValue(controller) as VRage.Game.Entity.MyEntity;

	private static bool IsClosedBlock(Sandbox.Game.Entities.Cube.MyTerminalBlock block)
		=> block == null || block.Closed || block.CubeGrid is { Closed: true };

	/// <summary>
	/// A component with NO entity is dead too. MyEntity.Close runs Components.Clear, which sets every
	/// component's container to null, so a closed character's ladder component answers ParentEntity == null -
	/// and the old test (ParentEntity is { Closed: true }) kept it: on the node-2 full dumps of 2026-09-15
	/// (08:08 -> 08:34, T-0147) every one of the 121 live MyCharacterLadderComponents sat in the updater's
	/// sets with m_container null and m_needsUpdate set, each pinning its closed character, the character's
	/// replicable, its m_dependencies (MyFarBroadcasterReplicables of closed antennas) and through them 569
	/// closed grids - 402 open ones being the node's whole real content. The scrubber ran all along (168
	/// log lines in those two hours, 861 stale dependencies pruned) and every one of its 132 summary lines
	/// read closedUpdatingComponents=0: this predicate found nothing to remove.
	/// </summary>
	private static bool IsClosed(VRage.Game.Entity.EntityComponents.Interfaces.IMyUpdatingEntityComponent component)
		=> component == null || component.ParentEntity is null or { Closed: true };

	private static int DropPool(object instance)
	{
		if (instance == null) return 0;
		if (Field(instance.GetType(), "m_groupPool")?.GetValue(instance) is not ICollection pool) return 0;
		int count = pool.Count;
		if (count > 0) pool.GetType().GetMethod("Clear")!.Invoke(pool, null);
		return count;
	}

	/// <summary>
	/// 9. Closed grids that are still KEYS of a MyGroups.m_nodes table (Logical, Physical, Mechanical,
	///    Electrical, ..., MyGridPhysicalHierarchy). Until 2026-09-15 these were only counted
	///    (closedGroupNodes=, 7-18 per node all day) "because removing them fires group callbacks on a dead
	///    grid". The node-2 full dump on 994bad5 (16:46, 78 min up, 131 closed grids of 637) showed what
	///    that costs: every one of the six sampled closed grids was rooted through ONE such node in
	///    MyCubeGridGroups.Electrical (m_groups -> Group -> m_members -> Node -> closed grid), and from that
	///    grid the chain ran on through its replicable's m_dependencies, its cockpit's conveyor lines and
	///    the resource sinks into the other closed grids - a dead node in a live group anchors a whole
	///    dead partition. 16 of the 568 electrical nodes referenced closed grids.
	///    How a closed grid gets a node back: MyCubeGrid.OnRemovedFromScene removes it from all four
	///    tables, but MyGroups.CreateLink calls GetOrCreateNode on BOTH ends, so a live neighbour that
	///    re-establishes a constraint afterwards (a ship connector's OnConstraintAdded, a rotor/piston
	///    head) re-creates the node keyed by the closed grid, inside the neighbour's live group.
	///    The release is what OnRemovedFromScene does: RemoveNode (BreakAllLinks + TryReleaseNode) on the
	///    tables that support orphans, BreakAllLinks alone on the two that do not (their nodes are
	///    released with their last link). The group callbacks these fire (OnChangeComplete on the
	///    neighbours' group data) are the ones a normally closing grid fires too; a grid that throws
	///    inside them is counted in groupNodesFailed and left for the next scrub.
	/// </summary>
	private static (int Closed, int Released, int Failed) ReleaseClosedNodes(object instance)
	{
		if (instance == null) return (0, 0, 0);
		List<MyCubeGrid> closed = ClosedGrids(instance);
		if (closed.Count == 0) return (0, 0, 0);
		Type type = instance.GetType();
		bool orphans = type.GetProperty("SupportsOphrans")?.GetValue(instance) is true;
		MethodInfo breakAll = type.GetMethod("BreakAllLinks", new[] { typeof(MyCubeGrid) });
		MethodInfo getNode = type.GetMethod("GetNode", new[] { typeof(MyCubeGrid) });
		int released = 0, failed = 0;
		foreach (MyCubeGrid grid in closed)
		{
			try
			{
				if (orphans && instance is MyGroupsBase<MyCubeGrid> groups) groups.RemoveNode(grid);
				else breakAll?.Invoke(instance, new object[] { grid });
				if (getNode?.Invoke(instance, new object[] { grid }) == null) released++;
			}
			catch (Exception ex)
			{
				failed++;
				if (failed == 1)
					Console.WriteLine("[ClusterPlugin] group_pool_scrub release of closed grid " + grid.EntityId + " from "
						+ type.Name + " failed: " + (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message);
			}
		}
		return (closed.Count, released, failed);
	}

	private static List<MyCubeGrid> ClosedGrids(object instance)
	{
		var closed = new List<MyCubeGrid>();
		if (Field(instance.GetType(), "m_nodes")?.GetValue(instance) is IDictionary nodes)
			foreach (object key in nodes.Keys)
				if (key is MyCubeGrid { Closed: true } grid) closed.Add(grid);
		return closed;
	}

	private static readonly Dictionary<(Type, string), FieldInfo> m_fields = new();

	private static FieldInfo Field(Type type, string name)
	{
		if (!m_fields.TryGetValue((type, name), out FieldInfo field))
		{
			// The pools live on the generic base MyGroups<TNode, TGroupData>; MyFixedGrids derives from it.
			for (Type t = type; t != null && field == null; t = t.BaseType) field = AccessTools.DeclaredField(t, name);
			m_fields[(type, name)] = field;
		}
		return field;
	}
}
