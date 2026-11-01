using System;
using System.Text;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// #1354 NPC census and sweep, the pure parts: the registry's census answer parses (the 64-bit ids exactly), Count
	/// counts within the radius and answers -1 without a snapshot or past the 60 s age limit (mod-mes then refuses
	/// the spawn), the online-only count (DF-712), the removal limits (the gap factor shortens, never lengthens), and the npc.* knobs.
	/// </summary>
	private static void NpcCensusRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("npc-census: " + message); }
		// The registry's answer (ClusterRegistry/NpcCensus.cs, System.Text.Json of NpcCensus).
		byte[] answer = Encoding.UTF8.GetBytes("{\"ageSeconds\":2.5,\"online\":2,\"offline\":1,\"grids\":["
			+ "{\"id\":144115188075855873,\"x\":0,\"y\":0,\"z\":0,\"u\":10},"
			+ "{\"id\":2,\"x\":3000,\"y\":0,\"z\":0,\"u\":20},"
			+ "{\"id\":3,\"x\":0,\"y\":-9000,\"z\":0,\"u\":1800,\"k\":\"keep\",\"o\":true},"
			+ "{\"id\":4,\"x\":0,\"y\":0,\"z\":0,\"u\":250,\"k\":\"unowned\"}]}");
		NpcCensusService.Snapshot snapshot = NpcCensusService.Parse(answer, answeredAt: 1000);
		Check(snapshot.Online == 2 && snapshot.Offline == 1 && snapshot.X.Length == 3, "online, offline and rows read");
		Check(snapshot.Unvisited[144115188075855873] == 10 && snapshot.Unvisited[3] == 1800, "unvisited seconds by exact 64-bit id");
		Check(snapshot.Unvisited[4] == 250, "#1379: an unowned row carries its unvisited seconds");
		Check(NpcCensusService.Count(snapshot, 1000, 0, 0, 0, 3000) == 2, "radius 3000 m holds the grid at the centre and the one at 3000 m");
		Check(NpcCensusService.Count(snapshot, 1000, 0, 0, 0, 2999) == 1, "radius 2999 m holds only the centre one, not the unowned row there");
		Check(NpcCensusService.Count(snapshot, 1000, 0, -9000, 0, 1) == 1, "the offline partition's grid counts");
		Check(NpcCensusService.Count(snapshot, 1000, 1e6, 0, 0, 1000) == 0, "an empty cell counts 0, not unknown");
		// DF-712: the online count leaves the offline partition's grid out, and keeps the -1 rule
		Check(NpcCensusService.Count(snapshot, 1000, 0, 0, 0, double.PositiveInfinity, onlineOnly: true) == 2
			&& NpcCensusService.Count(snapshot, 1000, 0, 0, 0, double.PositiveInfinity) == 3, "online only: 2 of the 3 NPC grids");
		Check(NpcCensusService.Count(snapshot, 1000, 0, -9000, 0, 1, onlineOnly: true) == 0, "online only: the offline grid does not count");
		Check(NpcCensusService.Count(null, 1000, 0, 0, 0, 3000, onlineOnly: true) == -1, "online only: no snapshot yet is unknown (-1)");
		Check(Math.Abs(snapshot.Age(1000 + 4000) - 6.5) < 1e-9, "age = the registry's 2.5 s + 4 s since the answer");
		Check(NpcCensusService.Count(snapshot, 1000 + 57_500, 0, 0, 0, 3000) == 2, "57.5 s since the answer = 60 s old: still known");
		Check(NpcCensusService.Count(snapshot, 1000 + 57_600, 0, 0, 0, 3000) == -1, "older than 60 s: unknown (-1), mod-mes fails closed");
		Check(NpcCensusService.Count(null, 1000, 0, 0, 0, 3000) == -1, "no snapshot yet: unknown (-1)");

		Check(UnvisitedNpcSweeper.RemovalLimitSeconds(30, 4, inGap: false) == 1800, "30 min outside the gap bands");
		Check(UnvisitedNpcSweeper.RemovalLimitSeconds(30, 4, inGap: true) == 450, "30 min / 4 inside a gap band");
		Check(UnvisitedNpcSweeper.RemovalLimitSeconds(30, 0.5, inGap: true) == 1800, "a factor below 1 never lengthens the gap limit");
		// #1379: which rule takes which kind (npc 30 min, unowned 5 min, a static unowned grid only when asked, kept never).
		Check(UnvisitedNpcSweeper.RemovalMinutes(NpcCensusService.GridKind.Npc, isStatic: true, 30, 5, false) == 30, "an NPC grid, static or not: npc.*");
		Check(UnvisitedNpcSweeper.RemovalMinutes(NpcCensusService.GridKind.Unowned, isStatic: false, 30, 5, false) == 5, "a dynamic unowned grid: cleanup.unownedMinutes");
		Check(UnvisitedNpcSweeper.RemovalMinutes(NpcCensusService.GridKind.Unowned, isStatic: true, 30, 5, false) == 0
			&& UnvisitedNpcSweeper.RemovalMinutes(NpcCensusService.GridKind.Unowned, isStatic: true, 30, 5, true) == 5, "a static unowned grid only with unownedStatic");
		Check(UnvisitedNpcSweeper.RemovalMinutes(NpcCensusService.GridKind.Keep, isStatic: false, 30, 5, true) == 0
			&& UnvisitedNpcSweeper.RemovalMinutes(NpcCensusService.GridKind.Player, isStatic: false, 30, 5, true) == 0, "kept and player grids never");
		Check(ClusterConfig.DefaultOf("npc.sweepKeepFactions") == "OCT" && ClusterConfig.DefaultOf("cleanup.keepTradeStations") == "true"
			&& ClusterConfig.DefaultOf("cleanup.unownedMinutes") == "5" && ClusterConfig.DefaultOf("cleanup.unownedVisitRadius") == "200"
			&& ClusterConfig.DefaultOf("cleanup.unownedStatic") == "false" && ClusterConfig.DefaultOf("npc.sweepMaxRemovals") == "50", "cleanup.* defaults");
		Check(ClusterConfig.Normalize("npc.sweepKeepFactions", " OCT , SPRT,,") == "OCT,SPRT" && ClusterConfig.Normalize("npc.sweepKeepFactions", "") == "",
			"npc.sweepKeepFactions is a tag list, empty allowed");
		bool badTag = false;
		try { ClusterConfig.Normalize("npc.sweepKeepFactions", "OCT;SPRT"); } catch (ArgumentException) { badTag = true; }
		Check(badTag, "a faction tag list refuses separators other than commas");

		Check(ClusterConfig.DefaultOf("npc.sweepSeconds") == "60" && ClusterConfig.DefaultOf("npc.unvisitedMinutes") == "30"
			&& ClusterConfig.DefaultOf("npc.visitRadius") == "auto" && ClusterConfig.DefaultOf("npc.gapRemovalFactor") == "4", "npc.* defaults");
		Check(ClusterConfig.Normalize("npc.visitRadius", "AUTO") == "auto" && ClusterConfig.Normalize("npc.visitRadius", "2500.5") == "2500.5",
			"npc.visitRadius takes auto or metres");
		Check(ClusterConfig.Normalize("npc.gapRemovalFactor", "2.5") == "2.5", "npc.gapRemovalFactor takes a decimal");
		bool refused = false;
		try { ClusterConfig.Normalize("npc.unvisitedMinutes", "0.5"); } catch (ArgumentException) { refused = true; }
		Check(refused, "npc.unvisitedMinutes is whole minutes");
		if (ClusterRuntime.IsClustered)
			Check(AppDomain.CurrentDomain.GetData(NpcCensusService.CountSlot) is Func<double, double, double, double, int>
				&& AppDomain.CurrentDomain.GetData(NpcCensusService.AgeSlot) is Func<double>
				&& AppDomain.CurrentDomain.GetData(NpcCensusService.OnlineCountSlot) is Func<double, double, double, double, int>,
				"a cluster node publishes the census slots");
	}
}
