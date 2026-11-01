using System.Collections.Generic;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Multiplayer;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// Request prefixes preserve the original sender and
/// local-invocation flag, execute the complete vanilla handler on the WA and skip node-local mutation;
/// accepted postfixes publish the exact vanilla success arguments. Everything routes through the boxed
/// seams with one operation string per request; a null seam runs vanilla.
/// The AcceptHumans setter prefix lives in MyFactionPatch (target type MyFaction).
/// </summary>
[HarmonyPatch(typeof(MyFactionCollection))]
public static class MyFactionCollectionPatch
{
	private static bool Request(string operation, params object[] arguments)
	{
		var seam = MyFactionCollectionSeams.ClusterFactionRequestPrefix;
		return seam == null || seam(operation, arguments);
	}

	private static bool Void(string operation, params object[] arguments)
	{
		var seam = MyFactionCollectionSeams.ClusterFactionVoidPrefix;
		return seam == null || seam(operation, arguments);
	}

	private static void Accepted(string operation, params object[] arguments)
		=> MyFactionCollectionSeams.ClusterFactionAcceptedPostfix?.Invoke(operation, arguments);

	// ClusterPlugin patch point: MyFactionCollectionPatch.CreateRequestPrefix
	[HarmonyPrefix]
	[HarmonyPatch("CreateFactionRequest", new[] { typeof(MyFactionCollection.AddFactionMsg) })]
	private static bool CreateRequestPrefix(MyFactionCollection.AddFactionMsg msg) => Request("Create", msg);

	// ClusterPlugin patch point: MyFactionCollectionPatch.AcceptedPostfix (FactionCreationFinished)
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyFactionCollection.FactionCreationFinished))]
	private static void CreateAcceptedPostfix(long factionId, long founderId, string factionTag, string factionName,
		string description, string privateInfo, bool createFromDef, string type, SerializableDefinitionId? factionIconGroupId,
		int factionIconId, Vector3 factionColor, Vector3 factionIconColor, int score)
	{
		// The same message vanilla just raised CreateFactionSuccess with.
		MyFactionCollection.AddFactionMsg message = new MyFactionCollection.AddFactionMsg
		{
			FactionId = factionId,
			FounderId = founderId,
			FactionTag = factionTag,
			FactionName = factionName,
			FactionDescription = description,
			FactionPrivateInfo = privateInfo,
			CreateFromDefinition = createFromDef,
			FactionColor = factionColor,
			FactionIconColor = factionIconColor,
			Type = type,
			FactionIconGroupId = factionIconGroupId,
			FactionIconId = factionIconId,
			FactionScore = score
		};
		Accepted("CreateSuccess", message);
	}

	// ClusterPlugin patch point: MyFactionCollectionPatch.DefaultFactionsPrefix (#1365: a node takes the WA's factions first)
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyFactionCollection.CreateDefaultFactions))]
	private static void DefaultFactionsPrefix(MyFactionCollection __instance)
		=> MyFactionCollectionSeams.ClusterDefaultFactionsPrefix?.Invoke(__instance);

	// ClusterPlugin patch point: MyFactionCollectionPatch.DefaultFactionsPostfix (#1365: reports what vanilla created locally)
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyFactionCollection.CreateDefaultFactions))]
	private static void DefaultFactionsPostfix(MyFactionCollection __instance)
		=> MyFactionCollectionSeams.ClusterDefaultFactionsPostfix?.Invoke(__instance);

	// ClusterPlugin patch point: MyFactionCollectionPatch.EditRequestPrefix
	[HarmonyPrefix]
	[HarmonyPatch("EditFactionRequest", new[] { typeof(MyFactionCollection.AddFactionMsg) })]
	private static bool EditRequestPrefix(MyFactionCollection.AddFactionMsg msgEdit) => Request("Edit", msgEdit);

	// ClusterPlugin patch point: MyFactionCollectionPatch.AcceptedPostfix (EditFactionSuccess)
	[HarmonyPostfix]
	[HarmonyPatch("EditFactionSuccess", new[] { typeof(MyFactionCollection.AddFactionMsg), typeof(long) })]
	private static void EditAcceptedPostfix(MyFactionCollection.AddFactionMsg msgEdit, long senderId) => Accepted("EditSuccess", msgEdit, senderId);

	// ClusterPlugin patch point: MyFactionCollectionPatch.AcceptHumansAcceptedPostfix
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyFactionCollection.EditFactionSuccess_AcceptHumans))]
	private static void AcceptHumansAcceptedPostfix(long factionId, bool acceptHumans) => Accepted("AcceptHumansSuccess", factionId, acceptHumans);

	// ClusterPlugin patch point: MyFactionCollectionPatch.AutoAcceptRequestPrefix
	[HarmonyPrefix]
	[HarmonyPatch("ChangeAutoAcceptRequest", new[] { typeof(long), typeof(bool), typeof(bool) })]
	private static bool AutoAcceptRequestPrefix(long factionId, bool autoAcceptMember, bool autoAcceptPeace)
		=> Request("AutoAccept", factionId, autoAcceptMember, autoAcceptPeace);

	// ClusterPlugin patch point: MyFactionCollectionPatch.AcceptedPostfix (ChangeAutoAcceptSuccess)
	[HarmonyPostfix]
	[HarmonyPatch("ChangeAutoAcceptSuccess", new[] { typeof(long), typeof(bool), typeof(bool) })]
	private static void AutoAcceptAcceptedPostfix(long factionId, bool autoAcceptMember, bool autoAcceptPeace)
		=> Accepted("AutoAcceptSuccess", factionId, autoAcceptMember, autoAcceptPeace);

	// ClusterPlugin patch point: MyFactionCollectionPatch.StateChangeRequestPrefix
	[HarmonyPrefix]
	[HarmonyPatch("FactionStateChangeRequest", new[] { typeof(MyFactionStateChange), typeof(long), typeof(long), typeof(long) })]
	private static bool StateChangeRequestPrefix(MyFactionStateChange action, long fromFactionId, long toFactionId, long playerId)
		=> Request("StateChange", action, fromFactionId, toFactionId, playerId);

	// ClusterPlugin patch point: MyFactionCollectionPatch.AcceptedPostfix (FactionStateChangeSuccess)
	[HarmonyPostfix]
	[HarmonyPatch("FactionStateChangeSuccess", new[] { typeof(MyFactionStateChange), typeof(long), typeof(long), typeof(long), typeof(long) })]
	private static void StateChangeAcceptedPostfix(MyFactionStateChange action, long fromFactionId, long toFactionId, long playerId, long senderId)
		=> Accepted("StateChangeSuccess", action, fromFactionId, toFactionId, playerId, senderId);

	// ClusterPlugin patch point: MyFactionCollectionPatch.AddReputationPrefix
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyFactionCollection.AddFactionPlayerReputation))]
	private static bool AddReputationPrefix(long playerIdentityId, long factionId, int delta, ReputationChangeReason reason,
		bool propagate, bool adminChange, ref bool __result)
	{
		var seam = MyFactionCollectionSeams.ClusterFactionBoolPrefix;
		if (seam == null) return true;
		bool clusterResult = false;
		if (seam("AddReputation", new object[] { playerIdentityId, factionId, delta, reason, propagate, adminChange }, ref clusterResult))
			return true;
		__result = clusterResult;
		return false;
	}

	// ClusterPlugin patch point: MyFactionCollectionPatch.AcceptedPostfix (AddFactionPlayerReputationSuccess)
	[HarmonyPostfix]
	[HarmonyPatch("AddFactionPlayerReputationSuccess", new[] { typeof(long), typeof(List<MyFactionCollection.MyReputationChangeWrapper>), typeof(ReputationChangeReason) })]
	private static void ReputationAcceptedPostfix(long playerId, List<MyFactionCollection.MyReputationChangeWrapper> changes, ReputationChangeReason reason)
		=> Accepted("ReputationSuccess", playerId, changes, reason);

	// ClusterPlugin patch point: MyFactionCollectionPatch.DamageReputationPrefix
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyFactionCollection.DamageFactionPlayerReputation))]
	private static bool DamageReputationPrefix(long playerIdentityId, long attackedIdentityId, MyReputationDamageType repDamageType, float damageAmount)
		=> Void("DamageReputation", playerIdentityId, attackedIdentityId, repDamageType, damageAmount);

	// ClusterPlugin patch point: MyFactionCollectionPatch.SetFactionReputationPrefix
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyFactionCollection.SetReputationBetweenFactions))]
	private static bool SetFactionReputationPrefix(long fromFactionId, long toFactionId, int reputation)
		=> Void("SetFactionReputation", fromFactionId, toFactionId, reputation);

	// ClusterPlugin patch point: MyFactionCollectionPatch.SetPlayerReputationPrefix
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyFactionCollection.SetReputationBetweenPlayerAndFaction))]
	private static bool SetPlayerReputationPrefix(long identityId, long factionId, int reputation, ReputationChangeReason reason)
		=> Void("SetPlayerReputation", identityId, factionId, reputation, reason);
}
