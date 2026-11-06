using System;
using System.Text;
using System.Text.Json;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1158: a node answers the game's DLC checks from what the Gateway's Steam frontend found. An attached Steam
	/// player owns all but their missing DLCs, a player without Steam answers (Direct Transport) is left to the game,
	/// a player on another node is answered from the online list, and one online nowhere owns none. The fields survive
	/// the attach binding's and the online report's JSON round trips, and a binding or report without them reads as
	/// "the Gateway didn't say".
	/// </summary>
	private static void Entitlements()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("entitlements: " + message); }
		const uint heavyIndustry = 1676100, stylePack = 1084680;
		const ulong human = 76561198000000001, bot = 76561202000000001, remote = 76561198000000002, gone = 76561198000000003;
		uint[] missing = { heavyIndustry };
		var none = Array.Empty<OnlinePlayersService.OnlinePlayerEntry>();
		Check(PlayerEntitlements.HasDlc(heavyIndustry, human, true, missing, none) == false, "an attached player doesn't own a missing DLC");
		Check(PlayerEntitlements.HasDlc(stylePack, human, true, missing, none) == true, "an attached player owns the others");
		Check(PlayerEntitlements.HasDlc(heavyIndustry, bot, true, null, none) == null, "a player without Steam answers is left to the game");

		var cluster = new[]
		{
			new OnlinePlayersService.OnlinePlayerEntry("node-2", remote, 0, 2, "Remote", "Steam", MissingDlcs: missing),
			new OnlinePlayersService.OnlinePlayerEntry("node-2", bot, 0, 3, "Bot", "Steam", DirectTransport: true),
		};
		Check(PlayerEntitlements.HasDlc(heavyIndustry, remote, false, null, cluster) == false, "a player on another node doesn't own a missing DLC");
		Check(PlayerEntitlements.HasDlc(stylePack, remote, false, null, cluster) == true, "a player on another node owns the others");
		Check(PlayerEntitlements.HasDlc(heavyIndustry, bot, false, null, cluster) == null, "a bot on another node is left to the game");
		Check(PlayerEntitlements.HasDlc(stylePack, gone, false, null, cluster) == false, "a player online nowhere owns no DLC");
		Check(PlayerEntitlements.HasDlc(stylePack, gone, false, null, none) == null, "before the first online list the game decides");

		var report = JsonSerializer.Deserialize<OnlinePlayersService.OnlineReport>(
			JsonSerializer.Serialize(new OnlinePlayersService.OnlineReport("node-2", cluster)));
		Check(report.Players[0].MissingDlcs is [heavyIndustry] && report.Players[1].MissingDlcs == null, "MissingDlcs survives the report's round trip");

		var identity = new IdentityAuthorityEntry(5, "Human", "Default_Astronaut", false, 0, 0, 0, false);
		var ready = new PlayerReady(7, "Steam", human, human, 0, false, identity, missing, new[] { "Battered_Armor" });
		var binding = IdentityAuthorityCodec.ReadReady(Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(ready)));
		Check(binding.MissingDlcs is [heavyIndustry] && binding.Skins is ["Battered_Armor"], "the attach binding carries DLCs and skins");
		var old = IdentityAuthorityCodec.ReadReady(JsonSerializer.Serialize(ready with { MissingDlcs = null, Skins = null })
			.Replace(",\"MissingDlcs\":null,\"Skins\":null", ""));
		Check(old.MissingDlcs == null && old.Skins == null, "a binding without them reads as not said");
	}
}
