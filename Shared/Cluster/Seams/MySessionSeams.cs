using System;
using System.Collections.Generic;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MySession"/>: the session clock, strict promotion, PlayerProfileService and
/// block-limit seams. Shared/Patches/MySessionPatch.cs invokes them.
/// </summary>
public static class MySessionSeams
{
	// SessionClockPatch.Postfix, invoked right after the vanilla ElapsedGameTime increment (postfix on Update).
	public static Action<MySession> ClusterAdjustElapsedGameTime;

	// ClusterPlugin seam adapter for PlayerProfileService.AugmentSavedPlayers, invoked between
	// SavePlayers and SaveToolbars. A cluster player's settings of record live at the World Authority
	// and the player is attached to whichever node hosts them, so this node's own player list cannot
	// carry them into the checkpoint it saves - or into the one it hands a joining client.
	public static Action<MyObjectBuilder_Checkpoint> ClusterAfterSavePlayers;

	/// <summary>
	/// Cluster seam: the offline everyone-is-Owner short-circuit hides every AdminOnly rejection, so an
	/// offline-mode cluster test bench can never exercise real promotion levels (P3-SUN-01's non-admin
	/// leg). When strict promotion is enabled the raw PromotedUsers entry decides for everyone. Installed
	/// only by ClusterRuntime, from an opt-in environment flag.
	/// </summary>
	public static Func<bool> ClusterStrictPromotion;

	/// <summary>
	/// Cluster seam: raised after a promote-level change executed on this server. Vanilla's
	/// OnPromoteLevelSet is [ServerInvoked][Broadcast] - it updates PromotedUsers HERE and broadcasts to
	/// the clients attached HERE, so without replication every other node keeps serving the old level.
	/// </summary>
	public static Action<ulong, MyPromoteLevel> ClusterPromotionChanged;

	/// <summary>Runs the vanilla promote-level event locally (and to locally attached clients).</summary>
	public static void ClusterRaisePromoteLevel(ulong steamId, MyPromoteLevel level)
	{
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MySession.OnPromoteLevelSet, steamId, level);
	}

	// ClusterPlugin seams: block/PCU usage committed on other cluster nodes, joined into every limit verdict
	// of CheckLimits. Without them each node enforces only against its own partitions and the world-wide caps
	// can be exceeded once per node.
	public static Func<long, (int PcuBuilt, int BlocksBuilt)> ClusterRemoteBlockUsage;
	public static Func<long, string, int> ClusterRemoteTypeBuilt;

	/// <summary>
	/// The cluster <c>MySession.CheckLimits</c> body: vanilla 1.210.014 plus the remote-usage terms at three
	/// places. Shared/Patches/MySessionPatch.cs substitutes it for the
	/// original only while at least one of the two seams above is installed, so an uninstalled server runs
	/// the stock method untouched.
	/// </summary>
	public static MySession.LimitResult CheckLimitsWithRemoteUsage(MySession self, Dictionary<string, int> failedBlockTypesResult,
		long ownerID, string blockName, int pcuToBuild, int blocksToBuild, int blocksCount, Dictionary<string, int> blocksPerType)
	{
		if (self.BlockLimitsEnabled == MyBlockLimitsEnabledEnum.NONE)
		{
			return MySession.LimitResult.Passed;
		}
		ulong num = self.Players.TryGetSteamId(ownerID);
		if (num == 0L)
		{
			num = Sync.MyId;
		}
		if (num != 0L && MySession.Static.IsUserAdmin(num))
		{
			AdminSettingsEnum adminSettingsEnum = AdminSettingsEnum.None;
			if (num == Sync.MyId)
			{
				adminSettingsEnum = MySession.Static.AdminSettings;
			}
			else if (MySession.Static.RemoteAdminSettings.ContainsKey(num))
			{
				adminSettingsEnum = MySession.Static.RemoteAdminSettings[num];
			}
			if ((adminSettingsEnum & AdminSettingsEnum.IgnorePcu) != AdminSettingsEnum.None)
			{
				return MySession.LimitResult.Passed;
			}
		}
		MyIdentity myIdentity = self.Players.TryGetIdentity(ownerID);
		if (self.MaxGridSize != 0 && blocksCount + blocksToBuild > self.MaxGridSize)
		{
			return MySession.LimitResult.MaxGridSize;
		}
		if (myIdentity != null)
		{
			MyBlockLimits blockLimits = myIdentity.BlockLimits;
			if (self.BlockLimitsEnabled == MyBlockLimitsEnabledEnum.PER_FACTION && self.Factions.GetPlayerFaction(myIdentity.IdentityId) == null)
			{
				return MySession.LimitResult.NoFaction;
			}
			if (blockLimits != null)
			{
				(int PcuBuilt, int BlocksBuilt) remoteUsage = ClusterRemoteBlockUsage?.Invoke(ownerID) ?? default;
				if (self.MaxBlocksPerPlayer != 0 && blockLimits.BlocksBuilt + remoteUsage.BlocksBuilt + blocksToBuild > blockLimits.MaxBlocks)
				{
					return MySession.LimitResult.MaxBlocksPerPlayer;
				}
				if (self.TotalPCU != 0 && pcuToBuild > blockLimits.PCU - remoteUsage.PcuBuilt)
				{
					return MySession.LimitResult.PCU;
				}
				if (blocksPerType != null)
				{
					foreach (KeyValuePair<string, short> blockTypeLimit2 in self.BlockTypeLimits)
					{
						if (!blocksPerType.ContainsKey(blockTypeLimit2.Key))
						{
							continue;
						}
						int num2 = blocksPerType[blockTypeLimit2.Key];
						if (blockLimits.BlockTypeBuilt.TryGetValue(blockTypeLimit2.Key, out var value))
						{
							num2 += value.BlocksBuilt;
						}
						num2 += ClusterRemoteTypeBuilt?.Invoke(ownerID, blockTypeLimit2.Key) ?? 0;
						if (num2 > self.GetBlockTypeLimit(blockTypeLimit2.Key))
						{
							if (failedBlockTypesResult != null)
							{
								failedBlockTypesResult[blockTypeLimit2.Key] = num2 - self.GetBlockTypeLimit(blockTypeLimit2.Key);
							}
							return MySession.LimitResult.BlockTypeLimit;
						}
					}
				}
				else if (blockName != null)
				{
					short blockTypeLimit = self.GetBlockTypeLimit(blockName);
					if (blockTypeLimit >= 0)
					{
						if (blockLimits.BlockTypeBuilt.TryGetValue(blockName, out var value2))
						{
							blocksToBuild += value2.BlocksBuilt;
						}
						blocksToBuild += ClusterRemoteTypeBuilt?.Invoke(ownerID, blockName) ?? 0;
						if (blocksToBuild > blockTypeLimit)
						{
							return MySession.LimitResult.BlockTypeLimit;
						}
					}
				}
			}
		}
		return MySession.LimitResult.Passed;
	}
}
