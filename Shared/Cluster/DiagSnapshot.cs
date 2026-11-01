using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

namespace ClusterPlugin;

/// <summary>
/// Orchestration.md 11.1: this node's section of the cluster-wide <c>diag</c> snapshot - the partitions it
/// owns, what is in them, the clients attached to it, and the per-character liveness flags the standing
/// invariants (Partitioning.md 10, invariants 7-8) are checked from.
/// </summary>
/// <remarks>
/// Captured on the game thread at a frame boundary by the caller (<see cref="ClusterControlServer"/>'s
/// <c>Invoke</c>), so one section is a consistent per-node cut. Cross-node consistency is stamped, not
/// promised: <c>capturedAt</c> plus <c>simFrameCounter</c> let the bench tell a real disagreement from
/// capture skew.
///
/// ponytail: composed from the reports that already exist - the partition manager's evaluation and
/// <see cref="PlayerStateService.Characters"/> - rather than a second traversal of the entity list. The
/// only genuinely new content is the per-entity rows at <c>depth=entities</c>.
/// </remarks>
internal static class DiagSnapshot
{
	internal const string DepthCluster = "cluster";
	internal const string DepthNodes = "nodes";
	internal const string DepthPartitions = "partitions";
	internal const string DepthEntities = "entities";

	internal static object Capture(PartitionManager partitions, PartitionPersistence persistence,
		string depth, ulong? partitionFilter, long? playerFilter, long? entityFilter, int limit, int cursor)
	{
		if (limit <= 0 || limit > 10000) limit = 10000;
		if (cursor < 0) cursor = 0;
		bool withEntities = string.Equals(depth, DepthEntities, StringComparison.OrdinalIgnoreCase);
		MySession session = MySession.Static;
		var evaluated = partitions.Evaluate()
			.Where(partition => partitionFilter == null || partition.Id == partitionFilter.Value)
			.OrderBy(partition => partition.Id).ToArray();

		Dictionary<long, MyEntity> entities = MyEntities.GetEntities().ToDictionary(entity => entity.EntityId, entity => entity);
		PartitionManager.ClientPartition[] routes = partitions.ClientPartitions(evaluated);
		// Two lookups built once per capture instead of a scan per row. The identity map replaces a
		// GetAllIdentities().FirstOrDefault(...) per CHARACTER row, which is O(entities x identities) at
		// depth=entities and grows with every NPC identity a mature world accumulates; the route map replaces
		// a routes scan per partition. Both ran inside Invoke(), i.e. on the game thread.
		Dictionary<ulong, ulong[]> clientsByPartition = routes.GroupBy(route => route.Partition)
			.ToDictionary(group => group.Key, group => group.Select(route => route.Client).OrderBy(client => client).ToArray());
		Dictionary<long, long> identityByCharacter = new Dictionary<long, long>();
		if (session != null)
			foreach (MyIdentity identity in session.Players.GetAllIdentities())
				if (identity.Character != null) identityByCharacter[identity.Character.EntityId] = identity.IdentityId;

		// One flat cursor over the node's entity rows, so a page boundary can fall inside a partition and
		// the union of pages is still exactly the unfiltered set (P1-INF-01's pagination assertion).
		int seen = 0, emitted = 0;
		var partitionRows = new List<object>();
		foreach (PartitionManager.Partition partition in evaluated)
		{
			List<object> rows = null;
			if (withEntities)
			{
				rows = new List<object>();
				foreach (long member in partition.Members.OrderBy(member => member))
				{
					if (entityFilter != null && member != entityFilter.Value) continue;
					if (seen++ < cursor) continue;
					if (emitted >= limit) continue;
					emitted++;
					rows.Add(EntityRow(member, entities.GetValueOrDefault(member), session, identityByCharacter));
				}
			}
			partitionRows.Add(new
			{
				partitionId = partition.Id,
				generation = persistence?.OwnedGeneration(partition.Id) ?? 0,
				sphere = new { x = partition.Sphere.Center.X, y = partition.Sphere.Center.Y, z = partition.Sphere.Center.Z, radius = partition.Sphere.Radius },
				maxMemberSpeed = partition.MaxMemberSpeed,
				anchored = partition.Anchored,
				memberCount = partition.Members.Count,
				// CLUSTER-SIM-COST: measured us of sim work per wall second (EMA), what the
				// balancer now weighs; 0 until the first balancer poll samples it.
				simLoadUs = partitions.SimLoadUs(partition.Id),
				// A partition whose members are mid-transfer is skipped by the membership pass; the bench
				// needs to see that from the snapshot rather than infer it (P1-INF-02 locked-skip leg).
				locked = partition.Members.Any(ClusterRuntime.IsFrozen),
				attachedClients = clientsByPartition.TryGetValue(partition.Id, out ulong[] attached) ? attached : Array.Empty<ulong>(),
				entities = rows?.ToArray()
			});
		}

		object[] clients = session == null ? Array.Empty<object>() : session.Players.GetOnlinePlayers()
			.Where(player => player.IsRealPlayer && player.Identity != null)
			.Where(player => playerFilter == null || player.Identity.IdentityId == playerFilter.Value)
			.OrderBy(player => player.Id.SteamId)
			.Select(player => (object)new
			{
				clientId = player.Id.SteamId,
				identityId = player.Identity.IdentityId,
				playerName = player.DisplayName,
				controlledEntityId = player.Controller?.ControlledEntity?.Entity?.EntityId ?? 0,
				characterEntityId = player.Character?.EntityId ?? 0,
				// Partitioning.md 10 invariant 8: an attached client whose controlled entity has no bound
				// controller here cannot move it, and nothing else reports that.
				controllerBound = player.Controller?.ControlledEntity?.Entity is MyEntity held
					&& session.Players.GetControllingPlayer(held) != null
			}).ToArray();

		return new
		{
			nodeId = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty,
			role = ClusterRuntime.IsWorldAuthority ? "WA" : "regular",
			epoch = ClusterRuntime.RegistryNodeEpoch,
			pid = Environment.ProcessId,
			// The registry-issued EntityId range this node draws from (Docs/EntityIdRanges.md): index,
			// bounds, consumption and prefetch state. Null until the allocator is configured.
			entityIdRange = EntityIdAllocator.Snapshot(),
			// T-0321 P6: per-channel (CHANNELS.md id) path counters, local vs lan, for the calls this node made and served.
			host = ClusterHttp.NodeHost,
			transport = ClusterProtocol.TransportCounters.Snapshot(),
			depth = withEntities ? DepthEntities : DepthPartitions,
			capturedAt = ClusterRuntime.ClusterMilliseconds(),
			simFrameCounter = session?.GameplayFrameCounter ?? 0,
			health = new
			{
				// A dormant spare deliberately ticks at dormant.hz, so the raw ratio reads ~0.07x
				// and would poison every sim metric for a node that has nothing to simulate and is
				// at full health; report 1.0 and carry the truth in the dormant flag.
				simSpeed = ClusterRuntime.IsDormant ? 1f : Sync.ServerSimulationRatio,
				dormant = ClusterRuntime.IsDormant,
				// Worker-pool time summed over the whole pool against ONE frame budget, so it runs
				// 0..workers*100 and is NOT a percentage and NOT main-thread cost
				// (findings/threadload-is-aggregate-worker-time-not-a-percentage.md).
				threadLoad = Sync.ServerThreadLoadSmooth,
				// T-0120: was MyFpsManager.FrameTimeAvg, which is "update + RENDER time" fed from
				// the render loop - a loop a dedicated server never runs, so this field read 0.0 on
				// every node for as long as it has existed. Game.CPUTimeSmooth is the smoothed wall
				// time of one frame measured in RunSingleFrame around UpdateInternal, and crucially
				// BEFORE Lock() sleeps off the remainder of the frame budget, so it is work rather
				// than wall clock and an idle node reads low.
				//
				// DELIBERATELY NOT ZEROED FOR A DORMANT SPARE, unlike simSpeed above and unlike
				// mainThreadMsPerSimSecond below. Two reasons. (1) This field is the honest
				// measurement - a spare ticking at dormant.hz really does spend this long on a
				// frame, and that is worth seeing. (2) It spent its entire existence stuck at 0.0
				// (see above); reintroducing a conditional 0.0 would make the value mean two
				// different things again - "dead field" and "dormant node" - which is precisely
				// the ambiguity that let the dead field hide for months.
				//
				// So DO NOT convert this to a per-second load by hand. `x 60` is wrong for a
				// dormant node twice over: it is not simulating, and it is not running 60 frames
				// a second either. node-2 as the spare reads ema 6.39, i.e. a plausible-looking
				// 383 ms/s for a node doing nothing (T-0120, caught by the CM). Use
				// mainThreadMsPerSimSecond, which is spare-safe, or branch on the `dormant` flag
				// carried in this same payload - and keep spares out of cross-node comparisons
				// regardless.
				frameTimeMsEma = MySandboxGame.Static?.CPUTimeSmooth ?? 0f,
				// The figure the per-partition loads are made to sum to (PartitionManager
				// .MainThreadUsPerSimSecond). Per SIMULATED second, not per wall second: a node
				// that cannot keep up stretches wall time, so a per-wall-second figure saturates
				// at 1000 and hides the overload, while this one rises without bound. Reported so
				// the balancer's input can be checked against sim speed from outside: at sim S the
				// two should agree near 1000/S ms/s.
				mainThreadMsPerSimSecond = ClusterRuntime.IsDormant
					? 0f
					: (MySandboxGame.Static?.CPUTimeSmooth ?? 0f) * 60f,
				rss = Process.GetCurrentProcess().WorkingSet64
			},
			load = new
			{
				partitionCount = evaluated.Length,
				entityCount = evaluated.Sum(partition => partition.Members.Count),
				clientCount = clients.Length,
				// T-0103: what actually drives MyDataReceiver.UpdateBroadcastersInRange, which is ~19% of a
				// loaded node's game thread and 72-78% of that is zeroing hash sets. The method is NOT driven
				// by an entity loop: it is called from UpdateAfterSimulation10 on MyRadioAntenna and
				// MyLaserAntenna blocks, on MyCharacter, and on MyProxyAntenna ONLY when that proxy carries a
				// receiver - so the call count is a property of WHICH grids a node holds, not how many
				// entities. Five snapshot variables were tried and refuted against it (entities, antenna
				// proxies, clients, node age, per-call capacity: a 71-entity node cost 785 ms and an
				// 88-entity node 11237 ms, and the hot spare held the MOST proxies and cost the least).
				// Nothing exposed this population, so it is exposed here.
				receiverSources = ReceiverCensus(entities.Values),
				// SE1-0084: the members whose positions the fast tick detector reads every frame (static grids
				// are left out), how many members are in the fast state, and the membership passes a fast
				// transition ran at once (at most one per 250 ms), and the members reported as flipping (fast_flapping).
				fastTickMembers = partitions.TickMemberCount,
				fastMembers = partitions.Fast.FastCount,
				fastImmediatePasses = partitions.ImmediatePasses,
				fastFlapReports = partitions.FastFlapReports
			},
			partitions = partitionRows.ToArray(),
			clients,
			// The character census is the invariant-7 evidence: every character body on this node with the
			// identity that owns it, whether or not a player is currently wearing it.
			characters = PlayerStateService.Characters(),
			// Non-null only when the caller asked for entities and the page did not reach the end.
			nextCursor = withEntities && seen > cursor + emitted ? cursor + emitted : (int?)null
		};
	}


	/// <summary>
	/// T-0103: the in-scene population that calls <c>MyDataReceiver.UpdateBroadcastersInRange</c>, split by
	/// source so a change can be attributed rather than merely noticed.
	/// </summary>
	/// <remarks>
	/// Counted over the entities ALREADY materialised for the capture, and antenna blocks come from
	/// <see cref="MyCubeGrid.BlocksCounters"/> - a dictionary the grid maintains as blocks are added and
	/// removed - so this is a dictionary lookup per grid, not a block traversal. DiagSnapshot.Capture runs
	/// on the game thread (2.4% of it on a loaded node), so a per-block scan here would be self-defeating.
	///
	/// <c>proxiesWithReceiver</c> is ~100% of <c>antennaProxies</c> on every node, and an earlier version of
	/// this remark predicted ZERO. That prediction was wrong and the reasoning behind it is worth keeping,
	/// because it was wrong in a specific way: AntennaGraphService never sets <c>HasReceiver</c> ITSELF, which
	/// is true, but the flag does not have to be set here to arrive set. <c>CaptureLocal</c> (:129) calls the
	/// game's own <c>MyDataBroadcaster.InitProxyObjectBuilder</c>, whose first line is
	/// <c>ob.HasReceiver = Receiver != null</c>; a real radio antenna always has a receiver, so the flag
	/// travels in the serialized builder, <c>MyProxyAntenna.Init</c> builds a <c>MyRadioReceiver</c> from it,
	/// and <c>MyProxyAntenna.UpdateBeforeSimulation</c> then calls <c>Receiver.UpdateBroadcastersInRange()</c>
	/// on it. So EVERY antenna proxy on EVERY node is a caller of that method - measured 2026-09-09: 530 of
	/// 534 on a node holding zero entities.
	///
	/// That is the architectural cost the old remark said to watch for, and it has been paid all along. It is
	/// also REQUIRED: a proxy without a receiver could not relay, which is what cross-node antenna reachability
	/// depends on. So this row is not an alarm to be silenced - it is the caller population, and
	/// <c>total</c> below is the number this node updates per pass.
	/// </remarks>
	private static object ReceiverCensus(IEnumerable<MyEntity> scene)
	{
		MyObjectBuilderType radio = typeof(MyObjectBuilder_RadioAntenna);
		MyObjectBuilderType laser = typeof(MyObjectBuilder_LaserAntenna);
		int radioBlocks = 0, laserBlocks = 0, characters = 0, proxies = 0, proxiesWithReceiver = 0;
		foreach (MyEntity entity in scene)
		{
			switch (entity)
			{
				case MyCharacter:
					characters++;
					break;
				case MyProxyAntenna proxy:
					proxies++;
					if (proxy.Receiver != null) proxiesWithReceiver++;
					break;
				case MyCubeGrid grid:
					if (grid.BlocksCounters.TryGetValue(radio, out int r)) radioBlocks += r;
					if (grid.BlocksCounters.TryGetValue(laser, out int l)) laserBlocks += l;
					break;
			}
		}
		return new
		{
			radioAntennaBlocks = radioBlocks,
			laserAntennaBlocks = laserBlocks,
			characters,
			antennaProxies = proxies,
			proxiesWithReceiver,
			// The population that actually calls the method, once per ten simulation frames each. Frame rate
			// is the other half of the cost and is already reported as health.frameTimeMsEma.
			total = radioBlocks + laserBlocks + characters + proxiesWithReceiver
		};
	}

	private static object EntityRow(long entityId, MyEntity entity, MySession session,
		Dictionary<long, long> identityByCharacter)
	{
		MyCharacter character = entity as MyCharacter;
		MyCubeGrid grid = entity as MyCubeGrid;
		return new
		{
			entityId,
			kind = entity == null ? "missing" : character != null ? "character" : grid != null ? "grid" : entity.GetType().Name,
			displayName = entity?.DisplayName ?? entity?.Name ?? string.Empty,
			position = entity == null ? null : new[] { entity.PositionComp.GetPosition().X, entity.PositionComp.GetPosition().Y, entity.PositionComp.GetPosition().Z },
			velocity = entity?.Physics == null ? null : new[] { (double)entity.Physics.LinearVelocity.X, entity.Physics.LinearVelocity.Y, entity.Physics.LinearVelocity.Z },
			blockCount = grid?.BlocksCount ?? 0,
			pcu = grid?.BlocksPCU ?? 0,
			ownerIdentityId = grid?.BigOwners?.FirstOrDefault() ?? 0,
			playerIdentityId = character == null ? 0
				: identityByCharacter.TryGetValue(entityId, out long identityId) ? identityId : 0,
			controlledByClientId = character == null ? 0
				: session?.Players.GetControllingPlayer(character)?.Client?.SteamUserId ?? 0,
			frozen = ClusterRuntime.IsFrozen(entityId),
			physicsActive = entity?.Physics?.IsActive ?? false,
			controllerBound = character != null && session?.Players.GetControllingPlayer(character) != null
		};
	}
}
