using System;
using System.Collections.Generic;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// #1280 mod channel v1, the pure parts: which roster changes reset the mods (WA replaced, a node gone, restarted
	/// under a new epoch or joined; this node itself never), and the codec's to-owner rule (an entity id on to-owner
	/// and on nothing else; a delivery without the field, as v0 wrote it, still reads).
	/// </summary>
	private static void ModChannelV1Rules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("mod-channel-v1: " + message); }
		ModRoster Roster(string authority, params (string Node, long Epoch)[] nodes)
		{
			var epochs = new Dictionary<string, long>(StringComparer.Ordinal);
			foreach ((string node, long epoch) in nodes) epochs[node] = epoch;
			return new ModRoster(authority, Array.Empty<string>(), Array.Empty<string>(), epochs);
		}
		ModRoster before = Roster("wa-1", ("wa-1", 1), ("self", 1), ("node-a", 1), ("node-b", 1));
		Check(ClusterModChannelService.RosterChanges(before, before, "self").Count == 0, "KNOWN NEGATIVE: an unchanged roster resets nothing");
		string changes = string.Join(" ", ClusterModChannelService.RosterChanges(before,
			Roster("wa-2", ("wa-2", 1), ("self", 2), ("node-a", 3), ("node-c", 1)), "self"));
		Check(changes == "wa-changed:wa-1->wa-2 node-restarted:node-a node-gone:node-b node-gone:wa-1 node-joined:node-c node-joined:wa-2",
			"WA replaced, node-b and wa-1 gone, node-a restarted, node-c and wa-2 joined, self left out: " + changes);
		Check(string.Join(" ", ClusterModChannelService.RosterChanges(before, Roster(null, ("self", 1), ("node-a", 1), ("node-b", 1)), "self"))
			== "wa-changed:wa-1->none node-gone:wa-1", "no WA any more");

		var owner = new ClusterModDelivery(Guid.NewGuid(), "node-a", "to-owner", "chan", "act", 0, 1, "node-b", Guid.NewGuid(), 72057594037927936);
		ClusterModDelivery read = ClusterModChannelCodec.Read(ClusterModChannelCodec.Write(owner));
		Check(read == owner, "a to-owner delivery round-trips with its entity id");
		bool Refused(ClusterModDelivery delivery)
		{
			try { ClusterModChannelCodec.Validate(delivery); return false; }
			catch (InvalidOperationException) { return true; }
		}
		Check(Refused(owner with { EntityId = 0 }), "to-owner without an entity id is refused");
		Check(Refused(owner with { Kind = "broadcast" }), "an entity id on another kind is refused");
		Check(!Refused(owner with { Kind = "to-node", EntityId = 0 }), "to-node is a kind");
		string v0 = "{\"OperationId\":\"" + Guid.NewGuid() + "\",\"Origin\":\"node-a\",\"Kind\":\"broadcast\",\"Channel\":\"chan\","
			+ "\"Payload\":\"p\",\"CorrelationId\":0,\"Sequence\":1,\"TargetNode\":\"node-b\",\"Incarnation\":\"" + Guid.NewGuid() + "\"}";
		Check(ClusterModChannelCodec.Read(v0).EntityId == 0, "a v0 delivery without EntityId reads");
	}
}
