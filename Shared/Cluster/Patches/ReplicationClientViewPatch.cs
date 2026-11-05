using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Replication;
using Sandbox.Game.Replication.StateGroups;
using VRage.Network;
using VRage.Replication;
using VRage.Utils;

namespace ClusterPlugin;

public static class ReplicationClientViewPatch
{
	private static readonly ConditionalWeakTable<MyReplicationServer, Dictionary<Endpoint, List<KeyValuePair<IMyReplicable, byte>>>> Suspended = new();

	// T-0368: the per-client stream data of asteroid replicas that hold less than this node's storage. Keyed by the
	// stream data object, which lives exactly as long as the client holds the replicable: a replica that is
	// destroyed and streamed again (or adopted) gets a new one and is current again.
	private static readonly ConditionalWeakTable<object, object> StaleVoxelStreams = new();
	private static readonly object Stale = new object();

	/// <summary>Type name of a network object and, when it is an entity replicable, its entity id -
	/// the VRage-internals half of pending-replicable diagnostics.</summary>
	public static string DescribeNetworkObject(MyReplicationServer server, uint networkId, out long entityId)
	{
		entityId = 0;
		if (!server.m_networkIDToObject.TryGetValue(new NetworkId(networkId), out IMyNetObject value))
			return "<unknown network id>";
		if (value is IMyEntityReplicable replicable) entityId = replicable.EntityId;
		return value.GetType().Name;
	}

	public static ClientHandoverState Export(MyReplicationServer server, Endpoint endpoint, HashSet<long> entityIds)
	{
		if (!server.m_clientStates.TryGetValue(endpoint, out MyClient client)) throw new InvalidOperationException("Source replication client is absent");
		List<IMyReplicable> exported = new List<IMyReplicable>();
		// T-0368: a modified asteroid the client holds is no partition member, so the prune destroyed it at every
		// handover: the client closed the voxel and the target streamed the whole storage again, and the player
		// watched the asteroid vanish and come back. It goes with the view instead, when the client's copy is
		// known to be current (VoxelCarryPolicy); the target adopts it or tells the client to destroy it.
		List<MyVoxelReplicable> voxels = new List<MyVoxelReplicable>();
		server.m_replicables.IterateRoots(replicable =>
		{
			if (replicable is MyVoxelReplicable voxel && !entityIds.Contains(voxel.EntityId) && CarriesVoxel(server, client, voxel))
			{
				exported.Add(voxel);
				voxels.Add(voxel);
			}
			Collect(replicable, false);
		});
		ClientHandoverState state = ExportClientState(client, exported, replicable => server.GetNetworkIdByObject(replicable).Value, replicable => GroupsOf(server, replicable));
		foreach (MyVoxelReplicable voxel in voxels)
		{
			uint networkId = server.GetNetworkIdByObject(voxel).Value;
			ClientReplicableState carried = state.Replicables.Find(entry => entry.NetworkId == networkId);
			carried.VoxelEntityId = voxel.EntityId;
			carried.VoxelApplied = VoxelJournalService.Instance.CarryState(voxel.Instance.StorageName).Applied;
			carried.VoxelGroupIds.AddRange(GroupsOf(server, voxel).Select(group => server.GetNetworkIdByObject(group).Value));
		}
		// Two membership tests against a list that holds the client's whole exported view; a set makes them
		// constant instead of two full scans on the handover's game-thread path.
		HashSet<IMyReplicable> exportedSet = exported.ToHashSet();
		if ((state.ControlledReplicableId != 0 && !exportedSet.Contains(client.State.ControlledReplicable)) || (state.CharacterReplicableId != 0 && !exportedSet.Contains(client.State.CharacterReplicable)))
			throw new InvalidOperationException("Controlled or character replicable is outside transfer roots");
		return state;

		void Collect(IMyReplicable replicable, bool included)
		{
			if (replicable is IMyEntityReplicable entity) included |= entityIds.Contains(entity.EntityId);
			if (included) exported.Add(replicable);
			foreach (IMyReplicable child in server.m_replicables.GetChildren(replicable)) Collect(child, included);
		}
	}

	/// <summary>T-0368: the source's half of VoxelCarryPolicy for one voxel replicable in the client's view.</summary>
	private static bool CarriesVoxel(MyReplicationServer server, MyClient client, MyVoxelReplicable replicable)
	{
		if (!client.Replicables.TryGetValue(replicable, out MyReplicableClientData data) || replicable.Instance is not MyVoxelBase voxel
			|| VoxelJournalService.Instance is not VoxelJournalService journal) return false;
		// An unregistered object exports as network id 0 (T-0029), which the target could not address either.
		bool delivered = !data.IsPending && !data.IsStreaming && !server.GetNetworkIdByObject(replicable).IsInvalid
			&& GroupsOf(server, replicable).All(group => !server.GetNetworkIdByObject(group).IsInvalid);
		VoxelCarry verdict = VoxelCarryPolicy.Source(voxel is MyPlanet, delivered, IsVoxelStale(replicable, client.State.EndpointId),
			journal.CarryState(voxel.StorageName).Unsettled);
		// Planets are not carried and say nothing, as before; the rest is pruned with a client_view_prune_extra line.
		if (verdict != VoxelCarry.Carry && verdict != VoxelCarry.Planet)
			MyLog.Default?.WriteLine($"client_view_carry_voxel_skip client={client.State.EndpointId.Id.Value} entity={replicable.EntityId} reason={verdict}");
		return verdict == VoxelCarry.Carry;
	}

	private static bool IsVoxelStale(MyVoxelReplicable replicable, Endpoint endpoint) =>
		replicable.GetStreamingStateGroup() is not MyStreamingEntityStateGroup<MyVoxelReplicable> { m_clientStreamData: { } streams }
		|| !streams.TryGetValue(endpoint, out var stream) || StaleVoxelStreams.TryGetValue(stream, out _);

	/// <summary>
	/// T-0368, game thread: a journal entry was written into this asteroid's storage. Storage writes are not
	/// replicated - a client learns of an edit only from the vanilla events of the node it was made on - so every
	/// client that holds the replica now holds less than this node, and only a new stream brings it up to date.
	/// </summary>
	internal static void MarkVoxelStale(MyVoxelBase voxel)
	{
		if (voxel is MyPlanet || MyExternalReplicable.FindByObject(voxel) is not MyVoxelReplicable replicable
			|| replicable.GetStreamingStateGroup() is not MyStreamingEntityStateGroup<MyVoxelReplicable> { m_clientStreamData: { } streams }) return;
		foreach (object stream in streams.Values) StaleVoxelStreams.AddOrUpdate(stream, Stale);
	}

	/// <summary>
	/// T-0368: the target's half of VoxelCarryPolicy for a carried asteroid. Network ids are minted per node, so
	/// the target's replicable and its groups take the source's ids when nothing else on this node knows them by
	/// their own. False: the replica cannot be adopted, and the caller tells the client to destroy it.
	/// </summary>
	private static bool ResolveCarriedVoxel(MyReplicationServer server, Endpoint endpoint, ClientReplicableState exported, bool mayWait, out bool wait,
		long waitedMs = -1)
	{
		VoxelCarry verdict = CheckCarriedVoxel(server, exported, out MyVoxelReplicable replicable, out List<IMyNetObject> objects, out List<uint> ids,
			out long applied, out string missing);
		string waited = waitedMs < 0 ? "" : $" waitedMs={waitedMs}";
		wait = mayWait && VoxelCarryPolicy.Waits(verdict, exported.VoxelApplied, applied, expired: false);
		if (wait)
		{
			if (waitedMs < 0)
				MyLog.Default?.WriteLine($"client_view_carry_voxel_wait client={endpoint.Id.Value} entity={exported.VoxelEntityId} networkId={exported.NetworkId} reason={verdict} missing={missing} sourceApplied={exported.VoxelApplied} applied={applied}");
			return false;
		}
		if (verdict == VoxelCarry.Remap)
			for (int i = 0; i < ids.Count; i++) ReplicationNetworkIdPatch.Reassign(server, objects[i], ids[i]);
		if (VoxelCarryPolicy.Carries(verdict))
			MyLog.Default?.WriteLine($"client_view_carry_voxel client={endpoint.Id.Value} entity={exported.VoxelEntityId} networkId={exported.NetworkId} remapped={verdict == VoxelCarry.Remap} applied={applied}{waited}");
		else
			MyLog.Default?.WriteLine($"client_view_carry_voxel_fallback client={endpoint.Id.Value} entity={exported.VoxelEntityId} networkId={exported.NetworkId} reason={verdict} missing={missing} sourceApplied={exported.VoxelApplied} applied={applied}{waited}");
		return VoxelCarryPolicy.Carries(verdict);
	}

	/// <summary>T-0368: the verdict alone, no side effect. <paramref name="missing"/> names the first test a Missing verdict failed:
	/// "no-replicable" (not generated here), "pristine" (generated, the journal has not written into it yet) and the rest.</summary>
	private static VoxelCarry CheckCarriedVoxel(MyReplicationServer server, ClientReplicableState exported, out MyVoxelReplicable replicable,
		out List<IMyNetObject> objects, out List<uint> ids, out long applied, out string missing)
	{
		replicable = server.m_callback.GetReplicableByEntityId(exported.VoxelEntityId) as MyVoxelReplicable;
		missing = exported.IsPending ? "pending"
			: replicable == null ? "no-replicable"
			: !replicable.IsValid ? "invalid"
			: !replicable.IsReadyForReplication ? "not-ready"
			: replicable.Instance is not MyVoxelBase || replicable.Instance is MyPlanet ? "not-asteroid"
			: !replicable.ShouldReplicate(default) ? "pristine"
			: VoxelJournalService.Instance == null ? "no-journal"
			: server.GetNetworkIdByObject(replicable).IsInvalid ? "no-network-id"
			: "-";
		bool present = missing == "-";
		objects = new List<IMyNetObject>();
		if (present)
		{
			objects.Add(replicable);
			objects.AddRange(GroupsOf(server, replicable));
		}
		ids = new List<uint> { exported.NetworkId };
		ids.AddRange(exported.VoxelGroupIds);
		bool groupsMatch = objects.Count == ids.Count, sameIds = groupsMatch, idsFree = groupsMatch;
		for (int i = 0; groupsMatch && i < ids.Count; i++)
		{
			sameIds &= server.GetNetworkIdByObject(objects[i]).Value == ids[i];
			idsFree &= ids[i] != 0 && (!server.m_networkIDToObject.TryGetValue(new NetworkId(ids[i]), out IMyNetObject holder) || holder == objects[i]);
		}
		bool unsettled, ownEdits;
		(applied, unsettled, ownEdits) = present ? VoxelJournalService.Instance.CarryState(replicable.Instance.StorageName) : default;
		MyVoxelReplicable target = replicable;
		return VoxelCarryPolicy.Target(present, groupsMatch, unsettled, ownEdits, exported.VoxelApplied, applied, sameIds, idsFree,
			viewed: present && server.m_clientStates.Values.Any(other => other.Replicables.ContainsKey(target)));
	}

	/// <summary>T-0368: carried asteroids a target is waiting for (VoxelCarryPolicy.Waits), per client. The client still
	/// holds each one under the source's network id; until it is adopted or destroyed, vanilla must not add this node's
	/// own replica (a second network id for the same entity on the client).</summary>
	private sealed class WaitingVoxel
	{
		public ClientReplicableState Exported;
		public long Since;
		public long Deadline;
	}

	private static readonly Dictionary<Endpoint, List<WaitingVoxel>> Waiting = new();

	/// <summary>True while a carried asteroid is waited for on behalf of this client: vanilla's add would mint a second id.</summary>
	internal static bool IsWaitedFor(IMyReplicable replicable, MyClient client)
	{
		if (Waiting.Count == 0 || replicable is not MyVoxelReplicable voxel || client?.State == null
			|| !Waiting.TryGetValue(client.State.EndpointId, out List<WaitingVoxel> list)) return false;
		long entityId = voxel.EntityId;
		return list.Exists(entry => entry.Exported.VoxelEntityId == entityId);
	}

	/// <summary>
	/// T-0368, game thread, every update: adopts each waited-for asteroid once the target holds it as current as the
	/// client's copy, under the source's ids and as already streamed - the client keeps its entity and is sent
	/// nothing - or, at the deadline, sends the destroy the immediate fallback used to send, and the layer refresh
	/// streams this node's own replica.
	/// </summary>
	internal static void UpdateWaitingVoxels()
	{
		if (Waiting.Count == 0) return;
		if (MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer server) { Waiting.Clear(); return; }
		long now = Stopwatch.GetTimestamp();
		foreach (Endpoint endpoint in Waiting.Keys.ToArray())
		{
			List<WaitingVoxel> list = Waiting[endpoint];
			if (!server.m_clientStates.TryGetValue(endpoint, out MyClient client))
			{
				// The client left this node: it either disconnected or took a new view elsewhere (Prune flushed it).
				foreach (WaitingVoxel entry in list)
					MyLog.Default?.WriteLine($"client_view_carry_voxel_dropped client={endpoint.Id.Value} entity={entry.Exported.VoxelEntityId} networkId={entry.Exported.NetworkId} reason=client-left waitedMs={ElapsedMs(entry.Since, now)}");
				Waiting.Remove(endpoint);
				continue;
			}
			if (!client.IsReady) continue;
			for (int i = list.Count - 1; i >= 0; i--)
			{
				WaitingVoxel entry = list[i];
				bool expired = now >= entry.Deadline;
				try
				{
					if (ResolveCarriedVoxel(server, endpoint, entry.Exported, mayWait: !expired, out bool wait, ElapsedMs(entry.Since, now)))
					{
						list.RemoveAt(i);
						AdoptWaitedVoxel(server, client, entry.Exported);
					}
					else if (!wait)
					{
						list.RemoveAt(i);
						SendDestroy(server, endpoint, entry.Exported.NetworkId);
					}
				}
				catch (Exception exception)
				{
					// Never left half-adopted for the client: it is told to destroy its copy, and the layer refresh re-adds.
					list.Remove(entry);
					MyLog.Default?.WriteLine($"client_view_carry_voxel_fallback client={endpoint.Id.Value} entity={entry.Exported.VoxelEntityId} networkId={entry.Exported.NetworkId} reason=Error error={exception.GetType().Name}: {exception.Message}");
					IMyReplicable added = server.m_callback.GetReplicableByEntityId(entry.Exported.VoxelEntityId);
					bool held = added != null && client.HasReplicable(added);
					bool sameId = held && server.GetNetworkIdByObject(added).Value == entry.Exported.NetworkId;
					if (held) server.RemoveForClient(added, client, true);
					if (!sameId) SendDestroy(server, endpoint, entry.Exported.NetworkId);
				}
			}
			if (list.Count == 0) Waiting.Remove(endpoint);
		}
	}

	/// <summary>Adopt's own steps for one streamed replicable the client holds, after the adopt: delivered, streamed, the source's layer.</summary>
	private static void AdoptWaitedVoxel(MyReplicationServer server, MyClient client, ClientReplicableState exported)
	{
		IMyReplicable replicable = server.m_callback.GetReplicableByEntityId(exported.VoxelEntityId);
		if (client.HasReplicable(replicable)) return;
		server.AddClientReplicable(replicable, client, false);
		bool kept = KeepStreamed(server, replicable, client);
		MyReplicableClientData data = client.Replicables[replicable];
		data.IsPending = false;
		data.IsStreaming = false;
		client.PendingReplicables--;
		if (exported.Layer >= 0 && exported.Layer < client.UpdateLayers.Length)
		{
			client.ReplicableToLayer[replicable] = client.UpdateLayers[exported.Layer];
			client.UpdateLayers[exported.Layer].Replicables.Add(replicable);
		}
		if (!kept) MyLog.Default?.WriteLine($"client_view_adopt_streamed client={client.State.EndpointId.Id.Value} kept=0 resent=1");
	}

	/// <summary>
	/// T-0368, source side of the next handover: a client leaving with asteroids still waited for. They are in no
	/// view this node could export, so the client is told to destroy them now (the old fallback) instead of keeping
	/// an id no node knows.
	/// </summary>
	private static void FlushWaitingVoxels(MyReplicationServer server, Endpoint endpoint)
	{
		if (!Waiting.Remove(endpoint, out List<WaitingVoxel> list)) return;
		long now = Stopwatch.GetTimestamp();
		foreach (WaitingVoxel entry in list)
		{
			MyLog.Default?.WriteLine($"client_view_carry_voxel_fallback client={endpoint.Id.Value} entity={entry.Exported.VoxelEntityId} networkId={entry.Exported.NetworkId} reason=HandoverWhileWaiting waitedMs={ElapsedMs(entry.Since, now)}");
			SendDestroy(server, endpoint, entry.Exported.NetworkId);
		}
	}

	private static long ElapsedMs(long since, long now) => (now - since) * 1000 / Stopwatch.Frequency;

	/// <summary>The id is the source's and need not resolve on this node, so the packet is written by hand; it is what RemoveForClient sends.</summary>
	private static void SendDestroy(MyReplicationServer server, Endpoint endpoint, uint networkId)
	{
		var packet = server.m_callback.GetBitStreamPacketData();
		packet.Stream.WriteNetworkId(new NetworkId(networkId));
		server.m_callback.SendReplicationDestroy(packet, new List<EndpointId> { endpoint.Id });
	}

	/// <summary>
	/// Exports the client's transport-level view with an empty replicable set and no bindings, for a cross-node
	/// respawn where the client leaves every replicable behind and the destination node rebuilds its view.
	/// </summary>
	public static ClientHandoverState ExportDetached(MyReplicationServer server, Endpoint endpoint)
	{
		if (!server.m_clientStates.TryGetValue(endpoint, out MyClient client)) throw new InvalidOperationException("Source replication client is absent");
		ClientHandoverState state = ExportClientState(client, Array.Empty<IMyReplicable>(), _ => 0u);
		state.ControlledReplicableId = 0;
		state.CharacterReplicableId = 0;
		state.ControlledEntityId = 0;
		state.CharacterEntityId = 0;
		return state;
	}

	public static int Prune(MyReplicationServer server, Endpoint endpoint, ClientHandoverState state)
	{
		if (!server.m_clientStates.TryGetValue(endpoint, out MyClient client)) throw new InvalidOperationException("Source replication client is absent");
		Dictionary<Endpoint, List<KeyValuePair<IMyReplicable, byte>>> suspended = Suspended.GetOrCreateValue(server);
		if (suspended.ContainsKey(endpoint)) throw new InvalidOperationException("Source replication client is already suspended");
		FlushWaitingVoxels(server, endpoint);
		// A view entry that no longer checks out - the id does not resolve, the client no longer
		// holds the replicable, or its parent is gone - used to fail the whole capture. That wedged
		// the partition: every capture retry saw the same stale view, the drain could never move it,
		// and the only exit was the client dying (partition 1714, world-7 2026-08-16, "Retained
		// replicable is missing its parent" until the wedged client was killed by hand). The client
		// is the one part of the transfer that can always be repaired by re-replication, so drop the
		// bad entries from the retained set instead: they are pruned from the source view with the
		// other extras, and the target re-sends whatever still matters through the ordinary add path.
		HashSet<IMyReplicable> retained = new HashSet<IMyReplicable>();
		int invalid = 0;
		foreach (ClientReplicableState exported in state.Replicables)
		{
			if (!server.m_networkIDToObject.TryGetValue(new NetworkId(exported.NetworkId), out IMyNetObject value) || value is not IMyReplicable replicable || !client.Replicables.ContainsKey(replicable)
				// T-0142: a detached event-proxy replicable throws from GetParent() below, and Export skips it.
				|| ReplicationNetworkIdPatch.IsDetached(server, replicable) || !retained.Add(replicable))
			{
				invalid++;
				MyLog.Default?.WriteLine($"client_view_prune_dropped client={endpoint.Id.Value} networkId={exported.NetworkId} reason=unresolved");
			}
		}
		// Drop every retained replicable whose parent chain leaves the retained set. This used to iterate to a
		// fixed point: each round COPIED the whole retained set and rescanned it, and one round was needed per
		// level of the parent chain that fell away - O(R x depth) with an R-sized allocation per round, on the
		// game thread, once per client per handover, where R is the client's whole replicable set (routinely
		// tens of thousands). One pass finds the roots that must go; a stack then cascades to their children,
		// which is what the repeated rounds were discovering one level at a time. Same set removed, same log
		// line per removal - only the order of the lines can differ.
		int orphaned = 0;
		Dictionary<IMyReplicable, List<IMyReplicable>> childrenOf = new Dictionary<IMyReplicable, List<IMyReplicable>>();
		foreach (IMyReplicable replicable in retained)
		{
			if (replicable.GetParent() is not IMyReplicable parent) continue;
			if (!childrenOf.TryGetValue(parent, out List<IMyReplicable> children)) childrenOf[parent] = children = new List<IMyReplicable>();
			children.Add(replicable);
		}
		Stack<IMyReplicable> doomed = new Stack<IMyReplicable>();
		foreach (IMyReplicable replicable in retained)
			if (replicable.GetParent() is IMyReplicable parent && !retained.Contains(parent)) doomed.Push(replicable);
		while (doomed.Count != 0)
		{
			IMyReplicable replicable = doomed.Pop();
			if (!retained.Remove(replicable)) continue;
			orphaned++;
			MyLog.Default?.WriteLine($"client_view_prune_dropped client={endpoint.Id.Value} {Describe(replicable)} reason=parent-not-retained");
			if (childrenOf.TryGetValue(replicable, out List<IMyReplicable> children))
				foreach (IMyReplicable child in children) doomed.Push(child);
		}
		if (invalid != 0 || orphaned != 0)
			Console.WriteLine($"[ClusterPlugin] client-view prune dropped {invalid} unresolved and {orphaned} orphaned view entries, client={endpoint.Id.Value}");

		IMyReplicable[] extras = client.Replicables.Keys.Where(replicable => !retained.Contains(replicable)).ToArray();
		HashSet<IMyReplicable> extraSet = extras.ToHashSet();
		suspended.Add(endpoint, client.PermanentReplicables.Where(pair => extraSet.Contains(pair.Key)).ToList());
		try
		{
			client.IsReady = false;
			foreach (IMyReplicable extra in extras)
			{
				client.PermanentReplicables.Remove(extra);
				client.CrucialReplicables.Remove(extra);
				client.ReplicableToLayer.Remove(extra);
				foreach (MyClient.UpdateLayer layer in client.UpdateLayers) if (layer.Replicables.Remove(extra)) layer.LayerPCU -= extra.PCU ?? 0;
			}
			int destroyedRoots = 0;
			foreach (IMyReplicable extra in extras)
			{
				if (ReplicationNetworkIdPatch.ParentOf(server, extra) is IMyReplicable parent && extraSet.Contains(parent)) continue;
				server.RemoveForClient(extra, client, true);
				destroyedRoots++;
			}
			// Skipping a child because its parent's removal will cascade to it assumes GetParent() and the
			// server's replicable tree agree. They need not: MyReplicablesBase.Add files anything with
			// HasToBeChild == false as a tree root whatever parent it names, which is exactly what a planet's
			// MyLogicalEnvironmentSectorReplicable does against its MyVoxelReplicable. RemoveForClient walks
			// the tree, so those were skipped by the pass above and never reached by the voxel's cascade, and
			// they held every client-bearing handover open. Whatever is still in the view is still an extra.
			foreach (IMyReplicable extra in extras)
			{
				if (!client.Replicables.ContainsKey(extra)) continue;
				server.RemoveForClient(extra, client, true);
				destroyedRoots++;
			}
			// Which half failed decides the fix: a surviving extra means the removal cascade - which walks the
			// server's replicable tree, not the client's view - missed it, a lost retained one means that
			// cascade took it along. Naming them is the difference between a diagnosis and a guess.
			IMyReplicable[] stale = client.Replicables.Keys.Where(replicable => !retained.Contains(replicable)).ToArray();
			IMyReplicable[] lost = retained.Where(replicable => !client.Replicables.ContainsKey(replicable)).ToArray();
			if (stale.Length != 0 || lost.Length != 0)
			{
				MyLog.Default?.WriteLine($"client_view_prune_failed client={endpoint.Id.Value} stale=[{string.Join(", ", stale.Select(Describe))}] lost=[{string.Join(", ", lost.Select(Describe))}]");
				throw new InvalidOperationException($"Pruned replication view does not equal the transfer view; {stale.Length} stale [{string.Join(", ", stale.Take(6).Select(Describe))}], {lost.Length} lost [{string.Join(", ", lost.Take(6).Select(Describe))}]");
			}
			MyLog.Default?.WriteLine($"client_view_prune client={endpoint.Id.Value} before={retained.Count + extras.Length} retained={retained.Count} destroyedRoots={destroyedRoots} after={client.Replicables.Count}");
			return extras.Length;
		}
		catch
		{
			Resume(server, endpoint);
			throw;
		}
	}

	private static string Describe(IMyReplicable replicable) =>
		replicable.GetType().Name + (replicable is IMyEntityReplicable entity ? "/" + entity.EntityId : "")
		+ (ReplicationNetworkIdPatch.IsDetachedProxy(replicable) ? " detached"
			: replicable.GetParent() is IMyReplicable parent ? " under " + parent.GetType().Name : " root");

	public static void Resume(MyReplicationServer server, Endpoint endpoint)
	{
		if (!Suspended.GetOrCreateValue(server).Remove(endpoint, out List<KeyValuePair<IMyReplicable, byte>> permanent)) return;
		if (!server.m_clientStates.TryGetValue(endpoint, out MyClient client)) return;
		foreach ((IMyReplicable replicable, byte layer) in permanent) if (replicable.IsValid) client.PermanentReplicables[replicable] = layer;
		foreach (MyClient.UpdateLayer layer in client.UpdateLayers) layer.UpdateTimer = 0;
		client.IsReady = true;
	}

	public static void Finish(MyReplicationServer server, Endpoint endpoint) => Suspended.GetOrCreateValue(server).Remove(endpoint);

	public static int Adopt(MyReplicationServer server, Endpoint endpoint, MyClientStateBase clientState, ClientHandoverState state)
	{
		if (server.m_clientStates.TryGetValue(endpoint, out MyClient existing) && existing.Replicables.Count != 0) throw new InvalidOperationException("Target replication client view is already populated");
		ReplicationNetworkIdPatch.RequireComplete(server);
		ValidateBindings(server, clientState, state);
		Dictionary<ClientReplicableState, IMyReplicable> resolved = new Dictionary<ClientReplicableState, IMyReplicable>();
		HashSet<uint> adopted = new HashSet<uint>();
		List<uint> destroyVoxels = new List<uint>();
		List<ClientReplicableState> waitVoxels = new List<ClientReplicableState>();
		int waitSeconds = ClusterConfig.GetInt("handover.voxelCarryWaitSeconds");
		int abandoned = 0;
		foreach (ClientReplicableState exported in state.Replicables)
		{
			// T-0368: a carried asteroid this node cannot adopt. The source kept it in the client's view, so the
			// client still has it and nothing here would ever name it again: leave the entry out and send the
			// destroy the source no longer sent (below); the layer refresh then adds this node's own replica.
			// One this node is still generating or catching up is waited for instead (UpdateWaitingVoxels).
			if (exported.VoxelEntityId != 0 && !ResolveCarriedVoxel(server, endpoint, exported, waitSeconds > 0, out bool wait))
			{
				abandoned++;
				if (wait) waitVoxels.Add(exported);
				else destroyVoxels.Add(exported.NetworkId);
				continue;
			}
			if (exported.IsStreaming && !exported.IsPending)
			{
				// An inconsistent flag pair on one view entry is not worth the player's connection;
				// leave the entry out and let the target re-send the replicable if it still matters.
				abandoned++;
				MyLog.Default?.WriteLine($"client_view_adopt_dropped client={endpoint.Id.Value} networkId={exported.NetworkId} reason=streaming-not-pending");
				continue;
			}
			if (!adopted.Add(exported.NetworkId) || !server.m_networkIDToObject.TryGetValue(new NetworkId(exported.NetworkId), out IMyNetObject value) || value is not IMyReplicable replicable || !replicable.IsValid || !replicable.IsReadyForReplication)
			{
				// The entity import just told us this replicable does not exist here: a block the
				// client could see was destroyed between the export and the import, which in a
				// survival world is ordinary (a Visitor clips a hillside and loses three thrusters).
				// The view the client carried over still names it. Refusing the adopt over that
				// detaches the player - the gateway drops the connection and the pilot is thrown
				// out of the game - for a block that is rubble on both sides. Leave the entry out
				// of the new view instead; the client removes it the moment it is told the
				// replicable is gone, exactly as it would have without a handover.
				//
				// The same holds for every other way an entry can fail to check out here - an id
				// the drop ledger never saw (the source pruned it as unresolved, or it belonged to
				// an entity that moved partitions between export and import), a duplicate entry, a
				// replicable not yet ready. Throwing turned each of those into a detached player
				// (world-6 NetworkId 22027136, world-7 NetworkId 5247492 - both cost the client its
				// session); a skipped entry costs at most one re-replication from this node. The
				// only entries worth failing the adopt for are the player's own bindings, checked
				// right below - a client without its character or controlled entity is broken in a
				// way re-replication cannot repair, and the gateway's abort-and-rollback is the
				// safe outcome for that.
				if (exported.NetworkId != 0 && (exported.NetworkId == state.ControlledReplicableId || exported.NetworkId == state.CharacterReplicableId)
						&& !ReplicationNetworkIdPatch.WasDropped(server, exported.NetworkId))
					throw new InvalidOperationException("Controlled or character replicable did not resolve on the target: NetworkId " + exported.NetworkId);
				abandoned++;
				if (!ReplicationNetworkIdPatch.WasDropped(server, exported.NetworkId))
					MyLog.Default?.WriteLine($"client_view_adopt_dropped client={endpoint.Id.Value} networkId={exported.NetworkId} reason=unresolved");
				continue;
			}
			if (exported.Layer >= MyLayers.UpdateLayerDescriptors.Count || exported.Layer < -1 || (exported.PermanentLayer.HasValue && exported.PermanentLayer.Value >= MyLayers.UpdateLayerDescriptors.Count))
				throw new InvalidOperationException("Invalid update layer for " + exported.NetworkId);
			resolved.Add(exported, replicable);
		}
		if (abandoned != 0) Console.WriteLine("[ClusterPlugin] client-view adopt dropped " + abandoned + " replicables destroyed before the import, client=" + endpoint.Id.Value);
		server.AddClient(endpoint, clientState);
		if (!server.m_clientStates.TryGetValue(endpoint, out MyClient client)) throw new InvalidOperationException("Failed to create target replication client view");
		ImportClientState(client, state);
		List<IMyReplicable> redeliver = new List<IMyReplicable>();
		int streamedKept = 0, streamedResent = 0, inventoryMismatch = 0, inventoryCarried = 0, inventorySkipped = 0;
		uint inventoryMaxId = 0;
		foreach ((ClientReplicableState exported, IMyReplicable replicable) in resolved)
		{
			// T-0261. A streamed replicable (grid, voxel) the client already holds must not be
			// streamed to it again. AddClientReplicable gives the target fresh per-client stream
			// data that says "never sent", and force=true runs ForceSend on it: the whole entity is
			// serialized and compressed on this game thread at once. The client has finished that
			// stream (m_streamed) and reads 35 bits of the ~10 KB it gets, so every backend switch
			// cost one full re-serialization per streamed replicable in view, plus an 'Invalid
			// stream terminator' per packet on the client (Remote state_sync_diag, 2026-09-27:
			// 232 of 232 were single MyStreamingEntityStateGroup packets). Adopt it as streamed,
			// the state the source reached, and force-send only its other groups. A pending one
			// is removed and re-added below anyway, so it keeps the old path.
			bool keepStream = !exported.IsPending && replicable is IMyStreamableReplicable { NeedsToBeStreamed: true };
			server.AddClientReplicable(replicable, client, !keepStream);
			if (exported.InventoryMessageIds.Count != 0 && !ClusterConfig.GetBool("handover.inventoryCounters"))
				inventorySkipped++;
			else if (exported.InventoryMessageIds.Count != 0)
			{
				if (ImportInventoryMessageIds(GroupsOf(server, replicable), endpoint, exported.InventoryMessageIds))
				{
					inventoryCarried++;
					inventoryMaxId = Math.Max(inventoryMaxId, exported.InventoryMessageIds.Max());
				}
				else inventoryMismatch++;
			}
			if (keepStream)
			{
				if (KeepStreamed(server, replicable, client)) streamedKept++;
				else streamedResent++;
			}
			MyReplicableClientData data = client.Replicables[replicable];
			// A replicable that was still pending at export must not be adopted as pending. The counter
			// that flag feeds is decremented by one thing only - the client acknowledging that it created
			// the replicable - and the client sends that ack to whichever node it was talking to when the
			// data arrived. Inheriting the flag here waits for an acknowledgement addressed to the node
			// that is no longer serving this client, so PendingReplicables never returns to zero,
			// SendPendingReplicablesDone is never sent, and the client sits behind a streaming indicator
			// that never clears - unable to drive its own character while the server happily pushes that
			// character around with external physics.
			//
			// Adopt it as delivered and hand it back to the ordinary add path below, which sends it again
			// from this node and takes the ack this node will actually receive.
			data.IsPending = false;
			data.IsStreaming = false;
			client.PendingReplicables--;
			if (exported.IsPending) redeliver.Add(replicable);
			if (exported.Layer >= 0)
			{
				client.ReplicableToLayer.Add(replicable, client.UpdateLayers[exported.Layer]);
				client.UpdateLayers[exported.Layer].Replicables.Add(replicable);
			}
			if (exported.PermanentLayer.HasValue) client.PermanentReplicables.Add(replicable, exported.PermanentLayer.Value);
		}
		// The adopted view is the exported one MINUS whatever the entity import had to let go of:
		// counting those in would fail the invariant for exactly the case the skip above exists for.
		if (client.PendingReplicables != 0 || client.Replicables.Count != state.Replicables.Count - abandoned) throw new InvalidOperationException("Adopted replication view invariant failed: " + client.Replicables.Count + " adopted, " + state.Replicables.Count + " exported, " + abandoned + " abandoned, " + client.PendingReplicables + " pending");
		ValidateBindings(server, client.State, state);
		client.IsReady = true;
		foreach (MyClient.UpdateLayer layer in client.UpdateLayers) layer.UpdateTimer = 0;
		server.SendServerData(endpoint);
		// T-0368: only now, past everything that can refuse the adopt - a refused client goes back to the source,
		// whose view still holds the replica. The id is the source's and resolves to nothing on this node.
		foreach (uint networkId in destroyVoxels) SendDestroy(server, endpoint, networkId);
		Waiting.Remove(endpoint);
		if (waitVoxels.Count != 0)
		{
			long now = Stopwatch.GetTimestamp();
			Waiting[endpoint] = waitVoxels.Select(exported => new WaitingVoxel
				{ Exported = exported, Since = now, Deadline = now + waitSeconds * Stopwatch.Frequency }).ToList();
		}
		// Tell the client its batch is complete before re-sending anything, and never on condition of an
		// acknowledgement. The adopted view IS complete - the invariant above proves the counter is zero
		// and every exported replicable resolved - so what the client is waiting on has already happened.
		//
		// Making this conditional on "nothing was re-delivered" left the confirmation hostage to an ack
		// that does not always come. A pending replicable is usually a child, and the one seen in the
		// field was MyEventProxyEntityComponentReplicable under MyCharacterReplicable: re-sent to a
		// client that already holds the parent, it need not be created again, and no creation means no
		// ack, means no confirmation, means a streaming indicator up for the rest of the session on a
		// player whose view is in fact complete.
		if (client.WantsBatchCompleteConfirmation)
		{
			server.m_callback.SendPendingReplicablesDone(endpoint);
			client.WantsBatchCompleteConfirmation = false;
			MyLog.Default?.WriteLine($"client_view_batch_complete client={endpoint.Id.Value} redeliver={redeliver.Count}");
		}
		// Now re-send what the client had not finished receiving, as ordinary streaming from this node.
		// Late arrival of one replicable is what streaming does anyway; a permanent loading indicator is
		// not.
		//
		// T-0396: the client takes the destroy of a replicable it still holds pending as "drop the pending
		// entries" only, so a child it already holds ready - a character seated in a grid that was still
		// streaming to it - stays registered there while this node re-sends it: 'Replicated object already
		// exists' at every re-entry into view. Destroy what the client holds below it first (RedeliverPolicy).
		if (redeliver.Count != 0 && ClusterConfig.GetBool("handover.redeliverSubtree"))
		{
			RedeliverPolicy.Plan<IMyReplicable> plan = RedeliverPolicy.Decide<IMyReplicable>(redeliver,
				parent => server.m_replicables.GetChildren(parent).ToList(), client.Replicables.ContainsKey,
				replicable => replicable == client.State.CharacterReplicable || replicable == client.State.ControlledReplicable);
			foreach (IMyReplicable child in plan.Destroy)
			{
				NetworkId id = server.GetNetworkIdByObject(child);
				if (!id.IsInvalid) SendDestroy(server, endpoint, id.Value);
			}
			int nested = redeliver.Count - plan.Redeliver.Count;
			if (plan.Destroy.Count != 0 || plan.Kept.Count != 0)
				MyLog.Default?.WriteLine($"client_view_redeliver_subtree client={endpoint.Id.Value} destroyed={plan.Destroy.Count} "
					+ $"kept={plan.Kept.Count} nested={nested} [{string.Join(", ", plan.Destroy.Concat(plan.Kept).Take(12).Select(Describe))}] (T-0396)");
			redeliver = plan.Redeliver;
		}
		foreach (IMyReplicable replicable in redeliver)
		{
			server.RemoveForClient(replicable, client, true);
			server.AddForClient(replicable, endpoint, client, true);
		}
		if (inventorySkipped != 0)
			MyLog.Default?.WriteLine($"client_view_inventory_counters_skipped client={endpoint.Id.Value} groups={inventorySkipped} (handover.inventoryCounters=false)");
		if (inventoryCarried != 0)
			MyLog.Default?.WriteLine($"client_view_inventory_counters client={endpoint.Id.Value} carried={inventoryCarried} maxMessageId={inventoryMaxId}");
		if (inventoryMismatch != 0)
			MyLog.Default?.WriteLine($"client_view_inventory_counters_mismatch client={endpoint.Id.Value} replicables={inventoryMismatch} (their inventories resync only on the next change past the source's counter)");
		if (streamedKept != 0 || streamedResent != 0)
			MyLog.Default?.WriteLine($"client_view_adopt_streamed client={endpoint.Id.Value} kept={streamedKept} resent={streamedResent}");
		if (redeliver.Count != 0)
			MyLog.Default?.WriteLine($"client_view_redeliver client={endpoint.Id.Value} count={redeliver.Count} "
				+ $"pending={client.PendingReplicables} [{string.Join(", ", redeliver.Select(Describe))}]");
		return resolved.Count;
	}

	/// <summary>
	/// Marks an adopted streamed replicable's stream as complete for this client, and force-sends its
	/// other state groups as AddClientReplicable(force: true) would. False: a streaming group of a type
	/// this does not know, which was force-sent the old way (a full re-stream).
	/// </summary>
	private static bool KeepStreamed(MyReplicationServer server, IMyReplicable replicable, MyClient client)
	{
		bool kept = true;
		if (!server.m_replicableGroups.TryGetValue(replicable, out List<IMyStateGroup> groups)) return true;
		foreach (IMyStateGroup group in groups)
		{
			if (!client.StateGroups.ContainsKey(group)) continue;
			if (group.IsStreaming && MarkStreamed(group, client.State.EndpointId)) continue;
			if (group.IsStreaming) kept = false;
			group.ForceSend(client.State);
		}
		return kept;
	}

	private static bool MarkStreamed(IMyStateGroup group, Endpoint endpoint) => group switch
	{
		MyStreamingEntityStateGroup<MyCubeGridReplicable> grid => MarkStreamed(grid, endpoint),
		MyStreamingEntityStateGroup<MyVoxelReplicable> voxel => MarkStreamed(voxel, endpoint),
		_ => false
	};

	// The per-client state vanilla reaches once every part is acknowledged: nothing left to send, not
	// dirty, and ObjectData present, so IsProcessingForClient says Finished and the one sync already
	// scheduled writes the 35-bit "complete" marker (ProcessWrite: !Incomplete) instead of a
	// SaveReplicable. The marker is exactly what a client with m_streamed reads.
	private static bool MarkStreamed<T>(MyStreamingEntityStateGroup<T> group, Endpoint endpoint) where T : IMyStreamableReplicable
	{
		if (group.m_clientStreamData == null || !group.m_clientStreamData.TryGetValue(endpoint, out var data) || data.CreatingData) return false;
		data.ObjectData = Array.Empty<byte>();
		data.RemainingBits = 0;
		data.Incomplete = false;
		data.Dirty = false;
		data.ForceSend = false;
		data.LastSent = null;
		return true;
	}

	/// <summary>
	/// Streams the committed members that are in the client's range, each with its layer, as the
	/// layer refresh would on its next pass.
	/// </summary>
	/// <remarks>
	/// T-0261: this used to AddForClient every member. A partition reaches ~8 km radius, so members
	/// outside every layer were streamed with no ReplicableToLayer entry, and the target's next
	/// layer refresh destroyed them 0.3-7 s later. The client saw a grid appear and vanish: 30 grids
	/// and 3 characters, 3-12 km away, in 32 min of -99 (2026-09-28 02:22-02:55). RefreshReplicable
	/// is vanilla's own decision: a layer when in range, layer 0 for the controlled, character,
	/// crucial and permanent replicables, nothing otherwise.
	/// </remarks>
	public static int AddEntities(MyReplicationServer server, Endpoint endpoint, IEnumerable<long> entityIds)
	{
		if (!server.m_clientStates.TryGetValue(endpoint, out MyClient client)) throw new InvalidOperationException("Target replication client is not connected");
		int count = 0;
		foreach (long entityId in entityIds.Distinct())
		{
			IMyReplicable replicable = server.m_callback.GetReplicableByEntityId(entityId);
			if (replicable == null || client.HasReplicable(replicable)) continue;
			server.RefreshReplicable(replicable, endpoint, client, true);
			if (client.HasReplicable(replicable)) count++;
		}
		return count;
	}

	public static void ValidateBindings(MyReplicationServer server, MyClientStateBase clientState, ClientHandoverState state)
	{
		uint controlled = clientState.ControlledReplicable == null ? 0 : server.GetNetworkIdByObject(clientState.ControlledReplicable).Value;
		uint character = clientState.CharacterReplicable == null ? 0 : server.GetNetworkIdByObject(clientState.CharacterReplicable).Value;
		long controlledEntity = (clientState.ControlledReplicable as IMyEntityReplicable)?.EntityId ?? 0;
		long characterEntity = (clientState.CharacterReplicable as IMyEntityReplicable)?.EntityId ?? 0;
		if (controlled != state.ControlledReplicableId || character != state.CharacterReplicableId || controlledEntity != state.ControlledEntityId || characterEntity != state.CharacterEntityId)
			throw new InvalidOperationException("Target controlled or character binding does not match source: controlled " + controlled + "/" + controlledEntity
				+ " vs " + state.ControlledReplicableId + "/" + state.ControlledEntityId + ", character " + character + "/" + characterEntity
				+ " vs " + state.CharacterReplicableId + "/" + state.CharacterEntityId);
	}

	private static ClientHandoverState ExportClientState(MyClient client, IEnumerable<IMyReplicable> replicables, Func<IMyReplicable, uint> getId,
		Func<IMyReplicable, IEnumerable<IMyStateGroup>> inventoryGroups = null)
	{
		if (!client.IsReady) throw new InvalidOperationException("Source replication client is not ready");
		ClientHandoverState state = new ClientHandoverState
		{
			StateSyncPacketId = client.m_stateSyncPacketId, StreamingPacketId = client.m_streamingPacketId, LastReceivedAckId = client.m_lastReceivedAckId,
			LastClientStreamingAck = client.m_lastClientStreamingAck, LastProcessedClientPacketId = client.m_lastProcessedClientPacketId,
			ClientTimestampMilliseconds = client.State.ClientTimeStamp.Milliseconds, PriorityMultiplier = client.PriorityMultiplier, PCULimit = client.PCULimit,
			WantsBatchCompleteConfirmation = client.WantsBatchCompleteConfirmation, ForcePlayoutDelayBuffer = client.ForcePlayoutDelayBuffer,
			UsePlayoutDelayBufferForCharacter = client.UsePlayoutDelayBufferForCharacter, UsePlayoutDelayBufferForJetpack = client.UsePlayoutDelayBufferForJetpack,
			UsePlayoutDelayBufferForGrids = client.UsePlayoutDelayBufferForGrids,
			ControlledReplicableId = client.State.ControlledReplicable == null ? 0 : getId(client.State.ControlledReplicable),
			CharacterReplicableId = client.State.CharacterReplicable == null ? 0 : getId(client.State.CharacterReplicable),
			ControlledEntityId = (client.State.ControlledReplicable as IMyEntityReplicable)?.EntityId ?? 0,
			CharacterEntityId = (client.State.CharacterReplicable as IMyEntityReplicable)?.EntityId ?? 0
		};
		foreach (IMyReplicable replicable in replicables)
		{
			if (!client.Replicables.TryGetValue(replicable, out MyReplicableClientData data)) continue;
			if (data.IsStreaming && !data.IsPending) throw new InvalidOperationException("Streaming replicable is not pending " + getId(replicable));
			ClientReplicableState exported = new ClientReplicableState { NetworkId = getId(replicable), IsPending = data.IsPending, IsStreaming = data.IsStreaming,
				Layer = client.ReplicableToLayer.TryGetValue(replicable, out MyClient.UpdateLayer layer) ? layer.Index : -1,
				PermanentLayer = client.PermanentReplicables.TryGetValue(replicable, out byte permanent) ? permanent : null };
			if (inventoryGroups != null) ExportInventoryMessageIds(inventoryGroups(replicable), client.State.EndpointId, exported.InventoryMessageIds);
			state.Replicables.Add(exported);
		}
		state.CachedVoxelNames.AddRange(client.m_clientCachedData);
		return state;
	}

	private static IEnumerable<IMyStateGroup> GroupsOf(MyReplicationServer server, IMyReplicable replicable) =>
		server.m_replicableGroups.TryGetValue(replicable, out List<IMyStateGroup> groups) ? groups : Array.Empty<IMyStateGroup>();

	/// <summary>
	/// T-0265: a client applies an inventory delta only when its MessageId is the next one it expects
	/// (MyEntityInventoryStateGroup.ReadInventory; a lower id is read and discarded). The counter is per client
	/// and per inventory group, and the target's AddClientReplicable starts it at 0 (CreateClientData), so after
	/// a backend switch every delta was dropped until the target had sent as many as the source had: the
	/// player's own character inventory froze, then drifted. The source's counters go with the view.
	/// </summary>
	internal static void ExportInventoryMessageIds(IEnumerable<IMyStateGroup> groups, Endpoint endpoint, List<uint> into)
	{
		foreach (IMyStateGroup group in groups)
			if (group is MyEntityInventoryStateGroup inventory)
				into.Add(inventory.m_clientInventoryUpdate != null && inventory.m_clientInventoryUpdate.TryGetValue(endpoint, out MyEntityInventoryStateGroup.InventoryClientData data)
					? data.CurrentMessageId : 0u);
		if (into.TrueForAll(id => id == 0)) into.Clear();
	}

	/// <returns>False when the target's inventory groups do not line up with the exported ones (nothing is set).</returns>
	internal static bool ImportInventoryMessageIds(IEnumerable<IMyStateGroup> groups, Endpoint endpoint, List<uint> ids)
	{
		List<MyEntityInventoryStateGroup> inventories = groups.OfType<MyEntityInventoryStateGroup>().ToList();
		if (inventories.Count != ids.Count) return false;
		for (int i = 0; i < inventories.Count; i++)
		{
			if (inventories[i].m_clientInventoryUpdate == null || !inventories[i].m_clientInventoryUpdate.TryGetValue(endpoint, out MyEntityInventoryStateGroup.InventoryClientData data))
				return false;
			data.CurrentMessageId = Math.Max(data.CurrentMessageId, ids[i]);
		}
		return true;
	}

	private static void ImportClientState(MyClient client, ClientHandoverState state)
	{
		client.m_stateSyncPacketId = state.StateSyncPacketId;
		client.m_streamingPacketId = state.StreamingPacketId;
		client.m_lastReceivedAckId = state.LastReceivedAckId;
		client.m_lastClientStreamingAck = state.LastClientStreamingAck;
		client.m_lastProcessedClientPacketId = state.LastProcessedClientPacketId;
		client.State.ClientTimeStamp = VRage.Library.Utils.MyTimeSpan.FromMilliseconds(state.ClientTimestampMilliseconds);
		client.PriorityMultiplier = state.PriorityMultiplier;
		client.PCULimit = state.PCULimit;
		client.WantsBatchCompleteConfirmation = state.WantsBatchCompleteConfirmation;
		client.ForcePlayoutDelayBuffer = state.ForcePlayoutDelayBuffer;
		client.UsePlayoutDelayBufferForCharacter = state.UsePlayoutDelayBufferForCharacter;
		client.UsePlayoutDelayBufferForJetpack = state.UsePlayoutDelayBufferForJetpack;
		client.UsePlayoutDelayBufferForGrids = state.UsePlayoutDelayBufferForGrids;
		client.m_clientCachedData.Clear();
		client.m_clientCachedData.UnionWith(state.CachedVoxelNames);
		foreach (List<IMyStateGroup> pending in client.m_pendingStateSyncAcks) pending.Clear();
	}
}

/// <summary>T-0368: vanilla's layer refresh must not add this node's own replica of a carried asteroid the node is
/// still waiting for (ReplicationClientViewPatch.UpdateWaitingVoxels) - the client holds it under the source's id.</summary>
[HarmonyPatch(typeof(MyReplicationServer), "AddForClient", new[] { typeof(IMyReplicable), typeof(Endpoint), typeof(MyClient), typeof(bool), typeof(bool) })]
public static class WaitingVoxelAddPatch
{
	[HarmonyPrefix]
	private static bool Prefix(IMyReplicable replicable, MyClient client) => !ReplicationClientViewPatch.IsWaitedFor(replicable, client);
}
