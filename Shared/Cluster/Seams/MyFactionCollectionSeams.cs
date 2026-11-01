using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyFactionCollection"/>. Harmony side: Shared/Patches/MyFactionCollectionPatch.cs.
/// The private success/request handlers referenced below are reached through the publicized assembly.
/// </summary>
public static class MyFactionCollectionSeams
{
	// ClusterPlugin seam adapters for MyFactionCollectionPatch
	public delegate bool ClusterFactionBoolPrefixDelegate(string operation, object[] arguments, ref bool result);
	public static Func<string, object[], bool> ClusterFactionRequestPrefix;
	public static ClusterFactionBoolPrefixDelegate ClusterFactionBoolPrefix;
	public static Func<string, object[], bool> ClusterFactionVoidPrefix;
	public static Action<string, object[]> ClusterFactionAcceptedPostfix;
	// #1365: around vanilla CreateDefaultFactions (session load, server only), the NPC factions of the definitions
	public static Action<MyFactionCollection> ClusterDefaultFactionsPrefix;
	public static Action<MyFactionCollection> ClusterDefaultFactionsPostfix;

	// ClusterPlugin seams: client-only broadcasts for corrective-snapshot diffs. A node that
	// reconciled its faction state from a full snapshot never ran the per-operation success
	// path, so its game clients would keep a stale faction table without these. [Broadcast]
	// events raised on the server go to clients only - the node itself already applied the
	// snapshot.
	public static void ClusterBroadcastFactionAdded(MyObjectBuilder_Faction faction)
	{
		MyFactionCollection.AddFactionMsg msg = new MyFactionCollection.AddFactionMsg
		{
			FactionId = faction.FactionId,
			FounderId = faction.Members?.FirstOrDefault(member => member.IsFounder).PlayerId ?? 0L,
			FactionTag = faction.Tag,
			FactionName = faction.Name,
			FactionDescription = faction.Description,
			FactionPrivateInfo = faction.PrivateInfo,
			Type = faction.FactionTypeString ?? faction.FactionType.ToString()
		};
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.CreateFactionSuccess, msg);
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.SetDefaultFactionStates, faction.FactionId);
	}

	public static void ClusterBroadcastFactionRemoved(long factionId)
		=> MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.FactionStateChangeSuccess,
			MyFactionStateChange.RemoveFaction, factionId, factionId, 0L, 0L);

	/// <summary>
	/// Thin private-event access adapter that raises the original success events on a replica; it contains
	/// no authority decisions or state.
	/// </summary>
	public static void ClusterApplyFactionSuccess(string operation, object[] arguments)
	{
		switch (operation)
		{
		case "CreateSuccess":
			MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.CreateFactionSuccess, (MyFactionCollection.AddFactionMsg)arguments[0]);
			MyFactionCollection.CreateFactionSuccess((MyFactionCollection.AddFactionMsg)arguments[0]);
			MyFactionCollection.SetDefaultFactionStates(((MyFactionCollection.AddFactionMsg)arguments[0]).FactionId);
			MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.SetDefaultFactionStates, ((MyFactionCollection.AddFactionMsg)arguments[0]).FactionId);
			break;
		case "EditSuccess": MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.EditFactionSuccess, (MyFactionCollection.AddFactionMsg)arguments[0], (long)arguments[1]); break;
		case "AutoAcceptSuccess": MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.ChangeAutoAcceptSuccess, (long)arguments[0], (bool)arguments[1], (bool)arguments[2]); break;
		case "AcceptHumansSuccess": MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.EditFactionSuccess_AcceptHumans, (long)arguments[0], (bool)arguments[1]); break;
		case "StateChangeSuccess": MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.FactionStateChangeSuccess,
			(MyFactionStateChange)arguments[0], (long)arguments[1], (long)arguments[2], (long)arguments[3], (long)arguments[4]); break;
		case "ReputationSuccess":
			MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyFactionCollection.AddFactionPlayerReputationSuccess, (long)arguments[0],
				(List<MyFactionCollection.MyReputationChangeWrapper>)arguments[1], (ReputationChangeReason)arguments[2]);
			MyFactionCollection.AddFactionPlayerReputationSuccess((long)arguments[0], (List<MyFactionCollection.MyReputationChangeWrapper>)arguments[1], (ReputationChangeReason)arguments[2]);
			break;
		default: throw new ArgumentOutOfRangeException(nameof(operation));
		}
	}
}
