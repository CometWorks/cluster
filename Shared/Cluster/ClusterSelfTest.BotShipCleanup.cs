using System;
using VRage.Game;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1316 bot ship cleanup, the pure parts: the abandonment rule, the Steam ID list knob and the stamp entry on a
	/// grid builder.
	/// </summary>
	private static void BotShipCleanupRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("bot-ship-cleanup: " + message); }
		Check(ClusterConfig.DefaultOf("cleanup.botShips") == "off" && ClusterConfig.DefaultOf("cleanup.botSteamIds") == "",
			"off by default, with no bots");
		const long hour = 3600, use = 1_762_700_000;
		Check(BotShipCleaner.Abandoned(use, use + 3 * hour, 3 * hour), "unused for the idle time at the backup: abandoned");
		Check(!BotShipCleaner.Abandoned(use, use + 3 * hour - 1, 3 * hour), "a second short of it: kept");
		Check(!BotShipCleaner.Abandoned(use, use - hour, 3 * hour), "used after the backup: kept");
		Check(!BotShipCleaner.Abandoned(use, 0, 3 * hour) && !BotShipCleaner.Abandoned(0, use, 3 * hour),
			"no backup, or no stamp: kept");
		Check(!BotShipCleaner.Abandoned(use, use + 9 * hour, 0), "idle minutes 0: never");
		Check(SteamIdRanges.Normalize(" 76561198000000001 , 76561198000000010-76561198000000020,") == "76561198000000001,76561198000000010-76561198000000020",
			"the list normalizes");
		Check(SteamIdRanges.Normalize("abc") == null && SteamIdRanges.Normalize("5-3") == null && SteamIdRanges.Normalize("0") == null
			&& SteamIdRanges.Normalize("1-2-3") == null, "bad items are refused");
		SteamIdRanges ranges = SteamIdRanges.Parse("100,200-300");
		Check(ranges.Contains(100) && ranges.Contains(250) && ranges.Contains(300) && !ranges.Contains(101) && !ranges.Contains(0),
			"ids and ranges match, 0 (an NPC) never");
		Check(SteamIdRanges.Parse("").IsEmpty, "an empty list has no bots");
		bool refused = false;
		try { ClusterConfig.Normalize("cleanup.botSteamIds", "x"); } catch (ArgumentException) { refused = true; }
		Check(refused && ClusterConfig.Normalize("cleanup.botShips", "DryRun") == "dryrun", "the knobs validate");
		var builder = new MyObjectBuilder_CubeGrid();
		BlockBuilderEntry.Write(builder, BotShipCleaner.Key, "1762700000");
		Check(BlockBuilderEntry.Read(builder, BotShipCleaner.Key) == "1762700000" && BlockBuilderEntry.Read(new MyObjectBuilder_CubeGrid(), BotShipCleaner.Key) == null,
			"the stamp rides the grid's builder");
	}
}
