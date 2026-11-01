using System.Collections.Generic;
using System.Linq;
using Sandbox.Engine.Multiplayer;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// Read-only: what this node believes one client holds of one grid's replicable subtree (control route
/// <c>/cluster/client-view</c>, T-0225).
///
/// A pilot whose client lost its ship from view and re-acquired it can see the grid and still reach none
/// of its blocks: pilot-43's client logged "Raising event on object which is not recognized by
/// replication: MyBasicMissionAutopilot" 54 times from 16:51 (2026-09-23), a SpeedLimit it set never
/// reached the owning node while the same probe on two healthy ships arrived in 4 s, and its AI leg missed
/// 13 times. The block-level replicables - one MyTerminalReplicable per terminal block, the component
/// replicables - are children of the grid's, sent with it. This says, per child type, how many the node has
/// and how many it believes the client has, and whether the client holds the grid itself: "server has 40,
/// client 0" wants the children sent; "client 40" while the client cannot see them is a stale belief.
/// </summary>
public static class ClientViewService
{
	public static object Status(ulong client, long gridEntityId)
	{
		if (MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer server)
			return new { found = false, reason = "not a replication server" };
		Endpoint endpoint = new Endpoint(new EndpointId(client), 0);
		bool clientKnown = server.m_clientStates.TryGetValue(endpoint, out MyClient state);
		IMyReplicable grid = server.m_callback.GetReplicableByEntityId(gridEntityId);
		if (grid == null)
			return new { found = false, client, gridEntityId, clientKnown, reason = "no replicable for this entity on this node" };
		var children = new List<IMyReplicable>();
		Collect(grid);
		var byType = children
			.GroupBy(child => child.GetType().Name)
			.Select(group => new
			{
				type = group.Key,
				server = group.Count(),
				client = clientKnown ? group.Count(child => state.Replicables.ContainsKey(child)) : 0,
			})
			.OrderByDescending(entry => entry.server)
			.ToArray();
		return new
		{
			found = true,
			client,
			gridEntityId,
			clientKnown,
			gridReplicable = grid.GetType().Name,
			gridOnClient = clientKnown && state.Replicables.ContainsKey(grid),
			children = children.Count,
			childrenOnClient = clientKnown ? children.Count(child => state.Replicables.ContainsKey(child)) : 0,
			byType,
			// Which children the node believes the client lacks, what they are, and whether the node would
			// send them at all (IsReadyForReplication false = vanilla holds them back).
			missing = clientKnown
				? children.Where(child => !state.Replicables.ContainsKey(child)).Take(20).Select(child => new
				{
					type = child.GetType().Name,
					networkId = server.GetNetworkIdByObject(child).Value,
					what = Describe(child),
					ready = child.IsReadyForReplication,
					valid = child.IsValid,
					parentOnClient = child.GetParent() is IMyReplicable parent && state.Replicables.ContainsKey(parent),
				}).ToArray()
				: null,
		};

		static string Describe(IMyReplicable replicable)
		{
			object instance = (replicable as Sandbox.Game.Replication.MyExternalReplicable)?.GetInstance();
			return instance switch
			{
				Sandbox.Game.Entities.MyCubeBlock block => block.BlockDefinition?.Id.SubtypeName + " block " + block.EntityId,
				VRage.Game.Components.MyEntityComponentBase component => component.GetType().Name + " on "
					+ ((component.Entity as Sandbox.Game.Entities.MyCubeBlock)?.BlockDefinition?.Id.SubtypeName ?? component.Entity?.GetType().Name)
					+ " " + (component.Entity?.EntityId ?? 0),
				VRage.Game.Entity.MyEntity entity => entity.GetType().Name + " " + entity.EntityId,
				null => "<no instance>",
				_ => instance.GetType().Name,
			};
		}

		void Collect(IMyReplicable parent)
		{
			foreach (IMyReplicable child in server.m_replicables.GetChildren(parent))
			{
				children.Add(child);
				Collect(child);
			}
		}
	}
}
