using System;
using System.Text.Json;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1081: presence lines are for Steam players only. A Direct Transport entry (a bot) is not announced unless
	/// CLUSTER_PRESENCE_DIRECT asks for it, a second character never is, and the flag survives the report's JSON
	/// round trip while a report from a node without it reads as not Direct Transport.
	/// </summary>
	private static void PresenceTransport()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("presence-transport: " + message); }
		var steam = new OnlinePlayersService.OnlinePlayerEntry("node-1", 76561198000000001, 0, 1, "Human", "Steam");
		var bot = new OnlinePlayersService.OnlinePlayerEntry("node-1", 76561202000000001, 0, 2, "Bot", "Steam", DirectTransport: true);
		Check(ClusterPresenceAnnouncer.Announces(steam, announceDirect: false), "a Steam player is announced");
		Check(!ClusterPresenceAnnouncer.Announces(bot, announceDirect: false), "a Direct Transport player is not announced");
		Check(ClusterPresenceAnnouncer.Announces(bot, announceDirect: true), "CLUSTER_PRESENCE_DIRECT=1 announces a Direct Transport player");
		Check(!ClusterPresenceAnnouncer.Announces(steam with { SerialId = 1 }, announceDirect: true), "a second character is never announced");

		var report = new OnlinePlayersService.OnlineReport("node-1", new[] { steam, bot });
		var read = JsonSerializer.Deserialize<OnlinePlayersService.OnlineReport>(JsonSerializer.Serialize(report));
		Check(read.Players is [{ DirectTransport: false }, { DirectTransport: true }], "the flag survives the report's round trip");
		var old = JsonSerializer.Deserialize<OnlinePlayersService.OnlineReport>(
			"{\"Origin\":\"node-1\",\"Players\":[{\"Node\":\"node-1\",\"SteamId\":1,\"SerialId\":0,\"IdentityId\":1,\"Name\":\"Old\"}]}");
		Check(old.Players is [{ DirectTransport: false, ServiceName: "" }], "a report without the flag reads as not Direct Transport");
	}
}
