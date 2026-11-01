using System;
using System.Collections.Generic;
using Sandbox.Game.GameSystems.IntergridCommunication;
using Sandbox.Game.SessionComponents;
using Sandbox.ModAPI.Ingame;
using VRage.Collections;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for the cluster members of <c>MyIGCSystemSessionComponent</c>
/// (MyIntergridCommunicationContextPatch: DeliverMessage and
/// BroadcastTags). Both instance members are call-ins, not interceptions: nothing invokes them
/// outside a cluster, so standalone runs stay byte-for-byte vanilla. They read the private
/// <c>m_activeBroadcastListeners</c> map directly through the publicized assembly.
/// </summary>
public static class MyIGCSystemSessionComponentSeams
{
	// ClusterPlugin seam: returns the frozen root of a programmable block's grid, or 0 when it is not frozen.
	// A broadcast must not be enqueued on a listener whose grid is frozen for a partition handover - the copy
	// was serialized at capture and dies at release. Skipped listeners are reported through frozenListeners
	// (block id -> frozen root) so the caller can hold a scoped copy across the handover window.
	// listenerScope restricts delivery to the listeners of a single block: a held copy forwarded after the
	// handover must reach only the moved listener, never re-fan to listeners that already received the original.
	public static Func<long, long> ClusterFrozenRootOf;

	/// <summary>Unscoped delivery: every active listener of the tag, no frozen-listener report.</summary>
	public static bool ClusterDeliverMessage(this MyIGCSystemSessionComponent self, long addressee, string tag,
		object data, long sourceAddress, int transmissionDistance)
		=> self.ClusterDeliverMessage(addressee, tag, data, sourceAddress, transmissionDistance, 0L, null);

	// ClusterPlugin patch point: MyIntergridCommunicationContextPatch.DeliverMessage
	// A message that crossed a node boundary was already checked for reachability on the sending node, where the
	// source block, its grid and the proxy antennas of remote grids all exist. This node cannot repeat that check
	// - the sender's block is not loaded here - so it delivers what it was given.
	public static bool ClusterDeliverMessage(this MyIGCSystemSessionComponent self, long addressee, string tag,
		object data, long sourceAddress, int transmissionDistance, long listenerScope, IDictionary<long, long> frozenListeners)
	{
		MyIGCMessage message = new MyIGCMessage(data, tag, sourceAddress);
		if (addressee != 0L)
		{
			MyIntergridCommunicationContext contextForPB = self.GetContextForPB(addressee);
			if (contextForPB == null || !contextForPB.IsActive)
			{
				return false;
			}
			contextForPB.UnicastListener.EnqueueMessage(message);
			return true;
		}
		if (!self.m_activeBroadcastListeners.TryGetValue(tag, out CachingHashSet<BroadcastListener> value))
		{
			return false;
		}
		value.ApplyChanges();
		bool delivered = false;
		foreach (BroadcastListener item in value)
		{
			long block = item.Context?.ProgrammableBlock?.EntityId ?? 0L;
			if (listenerScope != 0L && block != listenerScope)
			{
				continue;
			}
			if (listenerScope == 0L && block != 0L)
			{
				long frozenRoot = ClusterFrozenRootOf?.Invoke(block) ?? 0L;
				if (frozenRoot != 0L)
				{
					if (frozenListeners != null)
					{
						frozenListeners[block] = frozenRoot;
					}
					continue;
				}
			}
			item.EnqueueMessage(message);
			delivered = true;
		}
		return delivered;
	}

	// ClusterPlugin patch point: MyIntergridCommunicationContextPatch.BroadcastTags
	// Names the tags this node currently listens for, so a cross-node broadcast is only sent to nodes that can
	// consume it. Vanilla drops a broadcast with no matching listener, and turning every script heartbeat into
	// cluster-wide traffic would be a much larger change in behavior than the delivery itself.
	public static string[] ClusterBroadcastTags(this MyIGCSystemSessionComponent self)
	{
		List<string> tags = new List<string>(self.m_activeBroadcastListeners.Count);
		foreach (KeyValuePair<string, CachingHashSet<BroadcastListener>> listeners in self.m_activeBroadcastListeners)
		{
			listeners.Value.ApplyChanges();
			if (listeners.Value.Count > 0)
			{
				tags.Add(listeners.Key);
			}
		}
		return tags.ToArray();
	}
}
