using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using VRage.Game;
using VRage.Network;
using VRage.ObjectBuilders;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// In-process checks for the parts of the node that are pure functions of their input: the
/// membership algorithm and the partition save container. TestPlan.md calls these Tier 1/2 and class
/// E - "membership is plain data" - so they belong here rather than in a scenario that boots a
/// cluster and infers the algorithm from where entities ended up.
///
/// Addressable by section (<c>GET /cluster/self-test?section=membership-conga</c>) so each scenario
/// cites exactly the section it owns.
/// </summary>
internal static partial class ClusterSelfTest
{
	private const double Distance = 3000;
	private const double Margin = 300;

	public static readonly (string Name, Action Body)[] Sections = new (string, Action)[]
	{
		("membership-determinism", MembershipDeterminism),
		("offline-save-sections", OfflineSaveSections),
		("membership-hysteresis", MembershipHysteresis),
		("membership-atomicity", MembershipAtomicity),
		("membership-conga", MembershipConga),
		("membership-pinning", MembershipPinning),
		("membership-revert", MembershipRevert),
		("membership-held", MembershipHeld),
		("membership-registry-hold", MembershipRegistryHold),
		("save-retry-policy", SaveRetryPolicyBounds),
		("empty-recovery-release", EmptyRecoveryRelease),
		("registry-hold-policy", RegistryHoldPolicyRules),
		("registry-apply-async", () => RegistryApplyScenarios.Run()),
		("voxel-replay-policy", VoxelReplayPolicyRules),
		("voxel-carry-policy", VoxelCarryPolicyRules),
		("membership-unowned", MembershipUnowned),
		("membership-discard", MembershipDiscard),
		("membership-fast", MembershipFast),
		("membership-flapping", MembershipFlappingScenarios),
		("fast-motion-tracker", FastMotionTrackerRules),
		("config-normalize", ConfigNormalize),
		("fast-tick-detector", FastTickDetectorRules),
		("fast-flap-monitor", FastFlapMonitorRules),
		("startup-conflict", StartupConflict),
		("double-captured-pilot", DoubleCapturedPilot),
		("nested-pilot-live-elsewhere", NestedPilotLiveElsewhere),
		("census-seated-pilots", CensusSeatedPilots),
		("census-same-lineage", CensusSameLineage),
		("census-stale-copy", CensusStaleCopy),
		("trash-grace-handover", TrashGraceHandover),
		("trash-remote-players", TrashRemotePlayers),
		("character-reap-execution", CharacterReapExecution),
		("ship-antenna-rule", ShipAntennaRule),
		("wa-readiness", WorldAuthorityReadiness),
		("gps-snapshot-parts", GpsSnapshotParts),
		("save-container", SaveContainer),
		("host-save-store", HostSaveStoreWriters),
		("game-thread-stacks", GameThreadStacksRender),
		("global-scrub", GlobalScrub),
		("time-of-day", TimeOfDay),
		("round-trip-normalize", RoundTripNormalize),
		("havok-disposed-world", HavokDisposedWorld),
		("antenna-delta", AntennaDelta),
		("antenna-parse-alloc", AntennaParseAllocation),
		("antenna-fastpath", AntennaFastPath),
		("stale-home-copy", StaleHomeCopy),
		("close-attached-children", CloseAttachedChildren),
		("network-id-prefetch", NetworkIdPrefetch),
		("network-id-lockfree", NetworkIdLockFree),
		("gateway-pass-coalesce", GatewayPassCoalesce),
		("inventory-message-ids", InventoryMessageIds),
		("activate-slices", ActivateSlices),
		("import-activation-guard", ImportActivationGuardRules),
		("activate-pacing", ActivatePacing),
		("import-add-diag", ImportAddDiagRules),
		("residual-shape", ResidualShapeRules),
		("prepare-scope", PrepareScopeRules),
		("admin-safezone-retry", AdminSafeZoneRetry),
		("ship-storage-takes", ShipStorageTakes),
		("ship-storage-retry", ShipStorageRetry),
		("mod-channel-v1", ModChannelV1Rules),
		("npc-census", NpcCensusRules),
		("wreck-cleanup", WreckCleanupRules),
		("meteor-lifetime", MeteorLifetimeRules),
		("drop-cap", DropCapRules),
		("mod-message-to-server", ModMessageToServer),
		("ledger-replay", LedgerReplay),
		("handover-diff-budget", HandoverDiffBudget),
	};

	/// <summary>
	/// #1405: the WA builds its GPS snapshot from per-identity parts; the text must equal the whole-table serializer's
	/// (escaped strings, nulls, several identities, an identity without entries), and the replicas' reader accepts it.
	/// </summary>
	private static void GpsSnapshotParts()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("gps-snapshot-parts: " + message); }
		GpsAuthorityEntry Entry(long identity, int hash, string name, string description) => new(identity, hash, name, null, description,
			1.5, -2.25, 1e9, true, true, false, 0xFF00FF00u, 0, false, false, 0, false);
		var entries = new[]
		{
			Entry(7, -5, "quote \" slash \\ <tag> ünï", "line\nbreak"), Entry(7, 3, null, ""), Entry(42, int.MinValue, "a", null),
			Entry(42, int.MaxValue, "b", new string('x', 300)),
		};
		string whole = GpsAuthorityCodec.WriteSnapshot(entries);
		string parts = GpsAuthorityCodec.WriteSnapshotFromParts(entries.GroupBy(x => x.IdentityId).OrderBy(x => x.Key)
			.Select(x => GpsAuthorityCodec.WriteEntries(x.OrderBy(e => e.Hash))).Append(string.Empty));
		Check(whole == parts, "parts differ from the whole table: " + parts);
		Check(GpsAuthorityCodec.ReadSnapshot(parts).Entries.SequenceEqual(entries.OrderBy(x => x.IdentityId).ThenBy(x => x.Hash)), "the reader round-trips the parts");
		Check(GpsAuthorityCodec.WriteSnapshotFromParts(Array.Empty<string>()) == GpsAuthorityCodec.WriteSnapshot(Array.Empty<GpsAuthorityEntry>()),
			"an empty table matches");
	}

	/// <summary>
	/// #1318: a journaled command replayed on a replacement World Authority answers a retry of its operation id with
	/// the recorded result (the handler does not run again), a reused id with another payload is still a conflict, and
	/// the next command is numbered after the replayed order. On this node's own ledger, under a subsystem of its own.
	/// </summary>
	private static void LedgerReplay()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("ledger-replay: " + message); }
		const string Subsystem = "self-test-ledger-replay";
		Guid operationId = Guid.NewGuid();
		long replayed = GlobalCommandLedger.Order + 7;
		GlobalCommandLedger.Replay(Subsystem, operationId, "{\"amount\":5}", replayed, new GlobalCommandResult(true, "recorded", string.Empty));
		GlobalCommandResult retry = GlobalCommandLedger.Execute(Subsystem, operationId, "{\"amount\":5}",
			() => throw new InvalidOperationException("the handler ran again"), out long order);
		Check(retry.Success && retry.Payload == "recorded" && order == replayed, "a retry gets the recorded result at the replayed order");
		Check(!GlobalCommandLedger.Execute(Subsystem, operationId, "{\"amount\":6}", () => new GlobalCommandResult(true, "other", string.Empty)).Success,
			"a reused operation id with another payload is a conflict");
		GlobalCommandLedger.Execute(Subsystem, Guid.NewGuid(), "{}", () => new GlobalCommandResult(true, string.Empty, string.Empty), out long next);
		Check(next > replayed, "the next command is numbered after the replayed one");
	}

	/// <summary>
	/// DF-671: a mod message a client sends to the server reaches the node's mods whatever server id the client
	/// addressed (a Steam client through gateway-rs knows the gateway's GameServer id, not the node's), reliable and
	/// unreliable; vanilla's own recipient check still drops it, so ModMessageToServerPatch is what delivers.
	/// </summary>
	private static void ModMessageToServer()
	{
		Exception failure = null;
		using var done = new System.Threading.ManualResetEventSlim();
		Sandbox.MySandboxGame.Static.Invoke(() =>
		{
			try { ModMessageToServerOnGameThread(); }
			catch (Exception error) { failure = error; }
			finally { done.Set(); }
		}, "self-test mod-message-to-server");
		Assert(done.Wait(TimeSpan.FromSeconds(30)), "the game thread did not run the section within 30 s");
		if (failure != null) throw failure;
	}

	private static void ModMessageToServerOnGameThread()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("mod-message-to-server: " + message); }
		const ushort Channel = 65021;
		int received = 0;
		Action<byte[]> handler = _ => received++;
		ulong own = Sandbox.Game.Multiplayer.Sync.MyId, gameServer = own + 1;
#pragma warning disable CS0618 // the plain handler: a secure one also needs a remote sender, which a direct call has not
		Sandbox.ModAPI.MyAPIGateway.Multiplayer.RegisterMessageHandler(Channel, handler);
		try
		{
			Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.HandleMessageClient(Channel, new byte[1], gameServer);
			Check(received == 0, "vanilla's recipient check let another id through (the fixture is wrong)");
			Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.ModMessageServerReliable(Channel, new byte[1], gameServer);
			Check(received == 1, "a reliable message addressed to the gateway's id was dropped");
			Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.ModMessageServerUnreliable(Channel, new byte[1], gameServer);
			Check(received == 2, "an unreliable message addressed to the gateway's id was dropped");
			Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.ModMessageServerReliable(Channel, new byte[1], own);
			Check(received == 3, "a message addressed to the node's own id was dropped");
		}
		finally
		{
			Sandbox.ModAPI.MyAPIGateway.Multiplayer.UnregisterMessageHandler(Channel, handler);
		}
#pragma warning restore CS0618
	}

	/// <summary>#1397: the handover-diff budget evicts the oldest dumps first and never the pair just written.</summary>
	private static void HandoverDiffBudget()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("handover-diff-budget: " + message); }
		string directory = System.IO.Directory.CreateTempSubdirectory("handover-diff-selftest-").FullName;
		try
		{
			void Write(string name, int bytes, int minutesAgo)
			{
				string path = System.IO.Path.Combine(directory, name);
				System.IO.File.WriteAllBytes(path, new byte[bytes]);
				System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
			}
			Write("7-a-source.xml", 400, 30); Write("7-a-target.xml", 400, 30);
			Write("8-b-source.xml.gz", 100, 20); Write("8-b-target.xml.gz", 100, 20);
			Write("9-c-source.xml.gz", 900, 0); Write("9-c-target.xml.gz", 900, 0);
			Check(ClusterControlServer.TrimHandoverDiffs(directory, "9-c", 10_000) == 0, "a directory inside the budget keeps everything");
			int deleted = ClusterControlServer.TrimHandoverDiffs(directory, "9-c", 2_000);
			string[] left = System.IO.Directory.GetFiles(directory).Select(System.IO.Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
			Check(deleted == 2 && left.SequenceEqual(new[] { "8-b-source.xml.gz", "8-b-target.xml.gz", "9-c-source.xml.gz", "9-c-target.xml.gz" }),
				"the oldest pair goes first: " + string.Join(",", left));
			ClusterControlServer.TrimHandoverDiffs(directory, "9-c", 0);
			left = System.IO.Directory.GetFiles(directory).Select(System.IO.Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
			Check(left.SequenceEqual(new[] { "9-c-source.xml.gz", "9-c-target.xml.gz" }), "the pair just written survives any budget: " + string.Join(",", left));
		}
		finally { System.IO.Directory.Delete(directory, true); }
	}

	public static object Run(string section)
	{
		if (section != null && !Sections.Any(entry => string.Equals(entry.Name, section, StringComparison.OrdinalIgnoreCase)))
			throw new ArgumentException($"Unknown self-test section '{section}'; known: {string.Join(", ", Sections.Select(entry => entry.Name))}");
		List<string> ran = new List<string>();
		foreach ((string name, Action body) in Sections)
		{
			if (section != null && !string.Equals(name, section, StringComparison.OrdinalIgnoreCase)) continue;
			body();
			ran.Add(name);
		}
		return new { ok = true, ran, sections = Sections.Select(entry => entry.Name).ToArray() };
	}


	/// <summary>
	/// T-0392 import.activatePerFrame: the staged activate's slice plan. Whole physical groups per
	/// slice (rotor/piston/hinge base-top from either side, connector, landing gear), at most N
	/// entities unless one group is larger, groups in first-member order, members in input order,
	/// every entity exactly once. Pure: builders only.
	/// </summary>
	private static void ActivateSlices()
	{
		MyObjectBuilder_CubeGrid Grid(long id, params MyObjectBuilder_CubeBlock[] blocks) =>
			new MyObjectBuilder_CubeGrid { EntityId = id, CubeBlocks = new List<MyObjectBuilder_CubeBlock>(blocks) };
		string Shape(List<List<int>> slices) => string.Join("|", slices.Select(slice => string.Join(",", slice)));
		string Plan(int perFrame, params MyObjectBuilder_EntityBase[] builders)
		{
			List<List<int>> slices = ImportActivateSlices.Plan(builders, perFrame);
			List<int> all = slices.SelectMany(x => x).OrderBy(x => x).ToList();
			Assert(all.SequenceEqual(Enumerable.Range(0, builders.Length)), "every entity is in exactly one slice: " + Shape(slices));
			return Shape(slices);
		}

		Assert(Plan(2) == "", "no entities, no slices");
		Assert(Plan(2, Grid(100), Grid(200), Grid(300), Grid(400), Grid(500)) == "0,1|2,3|4", "loose grids fill slices of N in order");
		Assert(Plan(10, Grid(100), Grid(200), Grid(300)) == "0,1,2", "N at least the partition: one slice");
		Assert(Plan(1, new MyObjectBuilder_Character { EntityId = 100 }, Grid(200)) == "0|1", "a character is a group of its own");
		// Rotor: base on grid 100 names its head 21 on grid 300; grid 200 between them in the input.
		Assert(Plan(2, Grid(100, new MyObjectBuilder_MotorStator { EntityId = 10, TopBlockId = 21 }), Grid(200), Grid(300, new MyObjectBuilder_MotorRotor { EntityId = 21 }))
			== "0,2|1", "a rotor base and its head stay in one slice");
		// The same link seen only from the head (ParentEntityId), and a piston.
		Assert(Plan(1, Grid(100, new MyObjectBuilder_PistonBase { EntityId = 10 }), Grid(200), Grid(300, new MyObjectBuilder_PistonTop { EntityId = 21, ParentEntityId = 10 }))
			== "0,2|1", "a top that names its base joins the base's slice");
		// Connector pair: a group larger than N is one oversized slice, never split.
		Assert(Plan(1, Grid(100), Grid(200, new MyObjectBuilder_ShipConnector { EntityId = 20, ConnectedEntityId = 40 }), Grid(300),
				Grid(400, new MyObjectBuilder_ShipConnector { EntityId = 40, ConnectedEntityId = 20 }))
			== "0|1,3|2", "connected connectors are one slice even when the group exceeds N");
		// Landing gear locked to a grid by its entity id; a chain gear -> grid -> rotor -> grid.
		Assert(Plan(3, Grid(100, new MyObjectBuilder_LandingGear { EntityId = 11, AttachedEntityId = 300 }), Grid(200),
				Grid(300, new MyObjectBuilder_MotorStator { EntityId = 31, TopBlockId = 51 }), Grid(400), Grid(500, new MyObjectBuilder_MotorRotor { EntityId = 51 }))
			== "0,2,4|1,3", "a landing gear and a rotor chain form one transitive group");
		// Links to ids outside the import (a grid already on the node, a voxel) join nothing.
		Assert(Plan(1, Grid(100, new MyObjectBuilder_LandingGear { EntityId = 11, AttachedEntityId = 999 }),
				Grid(200, new MyObjectBuilder_MotorStator { EntityId = 21, TopBlockId = 998 }), Grid(300, new MyObjectBuilder_ShipConnector { EntityId = 31 }))
			== "0|1|2", "an unresolved or empty link joins nothing");
		// Groups go in first-member order and fill greedily with whole groups.
		Assert(Plan(3, Grid(100, new MyObjectBuilder_ShipConnector { EntityId = 11, ConnectedEntityId = 41 }), Grid(200), Grid(300),
				Grid(400, new MyObjectBuilder_ShipConnector { EntityId = 41, ConnectedEntityId = 11 }), Grid(500))
			== "0,1,3|2,4", "whole groups fill a slice; the next group that does not fit opens the next slice");
		AssertThrows(() => ImportActivateSlices.Plan(new MyObjectBuilder_EntityBase[0], 0), "perFrame 0 is refused (0 = one-frame activate, never planned)");

		// import.activateBudgetMs: budget 0, or no estimate yet, is the count-cap plan exactly.
		string Budgeted(int perFrame, double budgetMs, double msPerEntity, out int over, params MyObjectBuilder_EntityBase[] builders)
		{
			List<List<int>> slices = ImportActivateSlices.Plan(builders, perFrame, budgetMs, msPerEntity, out over);
			List<int> all = slices.SelectMany(x => x).OrderBy(x => x).ToList();
			Assert(all.SequenceEqual(Enumerable.Range(0, builders.Length)), "budgeted: every entity is in exactly one slice: " + Shape(slices));
			return Shape(slices);
		}
		MyObjectBuilder_EntityBase[] Loose(int n) => Enumerable.Range(1, n).Select(i => (MyObjectBuilder_EntityBase)Grid(i * 100)).ToArray();
		// 0,2 connected; 1; 3,5,7 a gear -> rotor chain; 4; 6; 8.
		MyObjectBuilder_EntityBase[] Mixed() => new MyObjectBuilder_EntityBase[]
		{
			Grid(100, new MyObjectBuilder_ShipConnector { EntityId = 11, ConnectedEntityId = 31 }), Grid(200),
			Grid(300, new MyObjectBuilder_ShipConnector { EntityId = 31, ConnectedEntityId = 11 }),
			Grid(400, new MyObjectBuilder_LandingGear { EntityId = 41, AttachedEntityId = 600 }), Grid(500),
			Grid(600, new MyObjectBuilder_MotorStator { EntityId = 61, TopBlockId = 81 }), Grid(700), Grid(800, new MyObjectBuilder_MotorRotor { EntityId = 81 }), Grid(900),
		};
		foreach (int perFrame in new[] { 1, 2, 3, 4, 20 })
		{
			foreach (MyObjectBuilder_EntityBase[] builders in new[] { Loose(7), Mixed() })
			{
				string capped = Plan(perFrame, builders);
				Assert(Budgeted(perFrame, 0, 50, out int over0, builders) == capped && over0 == 0, "budget 0 is the count-cap plan (perFrame " + perFrame + ")");
				Assert(Budgeted(perFrame, 100, 0, out int overNone, builders) == capped && overNone == 0, "no estimate yet is the count-cap plan (perFrame " + perFrame + ")");
				Assert(Budgeted(perFrame, 1000, 0.5, out int overCheap, builders) == capped && overCheap == 0, "a budget the estimate never reaches is the count-cap plan (perFrame " + perFrame + ")");
			}
		}
		Assert(Plan(20, Mixed()) == "0,1,2,3,4,5,6,7,8", "Mixed at the default cap is one slice");
		Assert(Budgeted(20, 100, 30, out int overLoose, Loose(10)) == "0,1,2|3,4,5|6,7,8|9" && overLoose == 0,
			"a high per-entity cost makes slices of budget/cost entities");
		Assert(Budgeted(2, 100, 10, out int overCap, Loose(5)) == "0,1|2,3|4" && overCap == 0, "the budget never makes a slice larger than the count cap");
		// Mixed at 40 ms/entity, 100 ms: groups {0,2} {1} {3,5,7} {4} {6} {8}; {0,2}+{1} = 120 ms > 100.
		Assert(Budgeted(20, 100, 40, out int overMixed, Mixed()) == "0,2|1|3,5,7|4,6|8" && overMixed == 1,
			"whole groups only; the 3-entity chain (120 ms > 100) is one over-budget slice of its own");
		Assert(Budgeted(20, 100, 500, out int overAll, Loose(3)) == "0|1|2" && overAll == 3, "at least one group per slice, each one flagged over budget");
		Assert(ImportActivateSlices.Take(new[] { 2, 1, 3 }, 0, 20, 100, 60, out bool takeOver) == 1 && takeOver, "Take: a 2-entity group at 60 ms is over a 100 ms budget, alone");
		Assert(ImportActivateSlices.Take(new[] { 2, 1, 3 }, 1, 20, 100, 25, out takeOver) == 2 && !takeOver, "Take: 1 + 3 entities at 25 ms fit 100 ms");
		Assert(ImportActivateSlices.Take(new[] { 2, 1, 3 }, 3, 20, 100, 25, out takeOver) == 0 && !takeOver, "Take: nothing left, nothing taken");
		Assert(ImportActivateSlices.Take(new[] { 25 }, 0, 20, 0, 0, out takeOver) == 1 && !takeOver, "Take: a group larger than the cap is a slice of its own, not over budget without a budget");
	}

	/// <summary>
	/// T-0387: every default must survive its own Normalize (a read-back-and-PUT of a node's config is
	/// a no-op, not a 400), and the decimal knobs take decimals. Pure: never touches the live config.
	/// </summary>
	private static void ConfigNormalize()
	{
		foreach (string key in ClusterConfig.Keys)
		{
			string value = ClusterConfig.DefaultOf(key);
			string normalized;
			try { normalized = ClusterConfig.Normalize(key, value); }
			catch (ArgumentException error) { throw new InvalidOperationException("default " + key + "=" + value + " is refused: " + error.Message); }
			Assert(normalized == value, "default " + key + "=" + value + " normalizes to " + normalized);
		}
		Assert(ClusterConfig.Normalize("fast.exitFraction", " 0.75 ") == "0.75", "decimal knob refused 0.75");
		Assert(ClusterConfig.Normalize("offline.wakeOverloadRatio", "2") == "2", "decimal knob refused an integer");
		Assert(ClusterConfig.Normalize("fast.enterMps", "AUTO") == "auto", "fast.enterMps refused auto");
		Assert(ClusterConfig.Normalize("fast.enterMps", "650.5") == "650.5", "fast.enterMps refused a number");
		// T-0339 (merge hold-off): off by default, an integer number of seconds like every other integer knob.
		Assert(ClusterConfig.DefaultOf("partition.mergeHoldoffSeconds") == "0", "partition.mergeHoldoffSeconds must default to 0 (off)");
		Assert(ClusterConfig.Normalize("partition.mergeHoldoffSeconds", " 30 ") == "30", "partition.mergeHoldoffSeconds refused 30");
		Assert(ClusterConfig.Normalize("partition.mergeHoldoffSeconds", "0") == "0", "partition.mergeHoldoffSeconds refused 0");
		// T-0339 (split hysteresis): off by default, an integer number of seconds.
		Assert(ClusterConfig.DefaultOf("partition.splitHysteresisSeconds") == "0", "partition.splitHysteresisSeconds must default to 0 (off)");
		Assert(ClusterConfig.Normalize("partition.splitHysteresisSeconds", " 60 ") == "60", "partition.splitHysteresisSeconds refused 60");
		Assert(ClusterConfig.Normalize("partition.splitHysteresisSeconds", "0") == "0", "partition.splitHysteresisSeconds refused 0");
		// #1300: the oversize split, a member count; 0 = off.
		Assert(ClusterConfig.DefaultOf("partition.oversizeMembers") == "150", "partition.oversizeMembers defaults to 150");
		Assert(ClusterConfig.Normalize("partition.oversizeMembers", " 0 ") == "0", "partition.oversizeMembers refused 0 (off)");
		Assert(ClusterConfig.DefaultOf("import.activateBudgetMs") == "0", "import.activateBudgetMs defaults to 0 (off: the count cap alone)");
		Assert(ClusterConfig.Normalize("import.activateBudgetMs", " 150 ") == "150", "import.activateBudgetMs refused 150");
		Assert(ClusterConfig.DefaultOf("import.activateMsPerSecond") == "0", "import.activateMsPerSecond defaults to 0 (off: no frame waits)");
		Assert(ClusterConfig.Normalize("import.activateMsPerSecond", " 70 ") == "70", "import.activateMsPerSecond refused 70");
		// T-0400: read by the gateway only; the nodes must accept the fan-out with the same integer rule.
		Assert(ClusterConfig.DefaultOf("retention.archiveFileDays") == "7", "retention.archiveFileDays defaults to 7");
		Assert(ClusterConfig.Normalize("retention.archiveFileDays", " 0 ") == "0", "retention.archiveFileDays refused 0 (off)");
		// T-0401: log-only diagnostics, booleans.
		Assert(ClusterConfig.DefaultOf("diag.importAddTimers") == "true", "diag.importAddTimers defaults to true");
		Assert(ClusterConfig.DefaultOf("diag.gridAddTraceImports") == "false", "diag.gridAddTraceImports defaults to false (import adds not traced)");
		Assert(ClusterConfig.Normalize("diag.gridAddTraceImports", "True") == "true", "diag.gridAddTraceImports refused True");
		foreach ((string key, string value) in new[] { ("import.activateBudgetMs", "-5"), ("import.activateBudgetMs", "1.5"), ("import.activateBudgetMs", "auto"),
			("import.activateMsPerSecond", "-70"), ("import.activateMsPerSecond", "70.5"), ("import.activateMsPerSecond", "auto"),
			("fast.exitFraction", "-0.1"), ("fast.exitFraction", "NaN"),
			("fast.exitFraction", "Infinity"), ("fast.exitFraction", "auto"), ("offline.wakeOverloadRatio", "1,5"),
			("hitch.thresholdMs", "1.5"), ("hitch.thresholdMs", "auto"), ("partition.mergeHoldoffSeconds", "-5"),
			("partition.mergeHoldoffSeconds", "1.5"), ("partition.mergeHoldoffSeconds", "true"),
			("partition.splitHysteresisSeconds", "-1"), ("partition.splitHysteresisSeconds", "2.5"), ("partition.splitHysteresisSeconds", "auto"), ("retention.archiveFileDays", "-1"),
			("partition.oversizeMembers", "-1"), ("partition.oversizeMembers", "auto"),
			("retention.archiveFileDays", "7.5"), ("retention.archiveFileDays", "off"),
			("diag.importAddTimers", "1"), ("diag.importAddTimers", "on"), ("diag.gridAddTraceImports", "yes") })
		{
			bool refused = false;
			try { ClusterConfig.Normalize(key, value); } catch (ArgumentException) { refused = true; }
			Assert(refused, key + "=" + value + " must be refused");
		}
	}


	private static void CharacterReapExecution()
	{
		var permit = new ClusterProtocol.CharacterReapPermit(Guid.NewGuid(), 900, 7, "source", 2, 3, 4, 5);
		int closes = 0;
		void Close() => closes++;
		if (CharacterHomeService.ExecuteAuthorizedReap(permit, "target", 2, 3, 4, true, Close)
			|| CharacterHomeService.ExecuteAuthorizedReap(permit, "source", 3, 3, 4, true, Close)
			|| CharacterHomeService.ExecuteAuthorizedReap(permit, "source", 2, 9, 4, true, Close)
			|| CharacterHomeService.ExecuteAuthorizedReap(permit, "source", 2, 3, 6, true, Close)
			|| CharacterHomeService.ExecuteAuthorizedReap(permit, "source", 2, 3, 4, false, Close)
			|| closes != 0) throw new InvalidOperationException("character reap closed a refused candidate");
		if (!CharacterHomeService.ExecuteAuthorizedReap(permit, "source", 2, 3, 4, true, Close) || closes != 1)
			throw new InvalidOperationException("character reap did not close its authorized offline candidate");
		var queue = new ClusterProtocol.CharacterReapQueue();
		if (!queue.Enqueue(42, permit) || !queue.TryTake(out var work)) throw new InvalidOperationException("reap queue");
		work.FinishExecution();
		queue.Resolved(work);
		if (queue.Enqueue(42, permit)) throw new InvalidOperationException("resolved reap response replayed");
	}


	private static void TrashRemotePlayers()
	{
		// #1307: on a node without its own controlling player, only a registry player who controls something
		// makes a far grid trash - as vanilla counts only players with a controlled entity.
		var camera = new ClusterPlayerPosition(7, 0, 7, 0, 0, 0);
		var respawnScreen = new ClusterPlayerPosition(8, 76561198000000001, 0, 0, 0, 0);
		var flying = new ClusterPlayerPosition(9, 76561198000000002, 12345, 0, 0, 0);
		Assert(!MySessionComponentTrashPatch.HasControllingPlayer(Array.Empty<ClusterPlayerPosition>()),
			"an empty registry counted as a controlling player");
		Assert(!MySessionComponentTrashPatch.HasControllingPlayer(new[] { camera }), "a viewed camera counted as a controlling player");
		Assert(!MySessionComponentTrashPatch.HasControllingPlayer(new[] { respawnScreen }),
			"a player on the respawn screen counted as a controlling player");
		Assert(MySessionComponentTrashPatch.HasControllingPlayer(new[] { camera, respawnScreen, flying }),
			"a controlling player was not counted");
	}

	private static void TrashGraceHandover()
	{
		// Synthetic ids: exercise the registered capture hook and target hook without changing grids.
		long source = long.MinValue + 303, target = source + 1, next = source + 2, expired = source + 3;
		var hook = MySessionComponentTrashPatch.GraceHandover;
		try
		{
			MySessionComponentTrashPatch.ProtectArrivals(new[] { source }, 240);
			byte[] original = hook.Leaving(source);
			string wire = PluginHandoverService.Append("network-ids", PluginHandoverService.Leaving(new HashSet<long> { source }));
			Assert(PluginHandoverService.Split(wire, out string states) == "network-ids" && states != null,
				"registered trash grace hook did not carry the recovery expiry");
			var decoded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, byte[]>>>(states);
			byte[] carried = decoded[source.ToString()]["cluster.trash-grace"];
			Assert(original.SequenceEqual(carried), "capture changed the recovery expiry");
			MySessionComponentTrashPatch.ProtectArrival(target);
			hook.Arrived(target, carried);
			Assert(carried.SequenceEqual(hook.Leaving(target)), "arrival lost the longer recovery grace");
			hook.Stayed(target);
			hook.Arrived(next, hook.Leaving(target));
			Assert(carried.SequenceEqual(hook.Leaving(next)), "another handover restarted the grace interval");
			hook.Arrived(target, BitConverter.GetBytes(DateTime.UtcNow.AddSeconds(60).Ticks));
			Assert(carried.SequenceEqual(hook.Leaving(target)), "a shorter arrival cut the recovery grace");
			hook.Arrived(expired, BitConverter.GetBytes(DateTime.UtcNow.AddSeconds(-1).Ticks));
			hook.Arrived(expired, null);
			hook.Arrived(expired, new byte[] { 1 });
			Assert(hook.Leaving(expired) == null, "expired or missing state renewed protection");
			hook.Left(source);
			Assert(hook.Leaving(source) == null, "committed departure retained source grace state");
		}
		finally
		{
			foreach (long id in new[] { source, target, next, expired }) hook.Left(id);
		}
	}

	private static void WorldAuthorityReadiness()
	{
		foreach (string state in new[] { "Starting", "Registering", "Registered", "Serving", "Failed", "Empty" })
		foreach (bool sessionReady in new[] { false, true })
		foreach (bool restored in new[] { false, true })
		foreach (bool fenced in new[] { false, true })
		{
			bool ready = ClusterRuntime.WorldAuthorityReadyToServe(state, sessionReady, restored, fenced);
			Assert(ready == (state == "Registered" && sessionReady && restored && !fenced),
				$"WA serving gate: state={state} sessionReady={sessionReady} restored={restored} fenced={fenced}");
		}
	}


	// -- membership --------------------------------------------------------

	private static PartitionMembership Fresh()
	{
		ulong next = 1;
		return new PartitionMembership(Distance, Margin, count => Enumerable.Range(0, count).Select(_ => next++).ToArray());
	}

	private static PartitionMembership.Member At(long id, double x, long group = 0) =>
		new PartitionMembership.Member(id, x, 0, 0, 0, 0, group);

	private static long[][] Partitions(PartitionMembership membership) =>
		membership.Layouts.Select(layout => layout.Members.OrderBy(id => id).ToArray())
			.OrderBy(members => members[0]).ToArray();

	private static void SameSets(long[][] actual, long[][] expected, string what)
	{
		string left = string.Join(" | ", actual.Select(set => string.Join(",", set)));
		string right = string.Join(" | ", expected.Select(set => string.Join(",", set)));
		Assert(left == right, $"{what}: expected [{right}], got [{left}]");
	}

	/// P1-MEM-01: flood fill on a recorded layout gives the expected sets, and gives them again from a
	/// shuffled input order - membership is a function of positions, not of enumeration order.
	private static void MembershipDeterminism()
	{
		// Three clusters on a line: {1,2,3} tight, {4,5} tight, {6} alone. Gaps between clusters are
		// wider than D + m (3300), gaps inside them are narrower.
		PartitionMembership.Member[] layout = new[]
		{
			At(1, 0), At(2, 1000), At(3, 2000),
			At(4, 20000), At(5, 21000),
			At(6, 50000),
		};
		long[][] expected = new[] { new long[] { 1, 2, 3 }, new long[] { 4, 5 }, new long[] { 6 } };

		PartitionMembership first = Fresh();
		first.Evaluate(layout, 0);
		SameSets(Partitions(first), expected, "flood fill");

		PartitionMembership shuffled = Fresh();
		shuffled.Evaluate(layout.Reverse(), 0);
		SameSets(Partitions(shuffled), expected, "flood fill from a reversed input");

		// Idempotent: re-evaluating an unchanged world changes nothing.
		PartitionMembership.Change[] again = first.Evaluate(layout, 100);
		Assert(again.Length == 0, $"a second pass over an unchanged world reported {again.Length} changes");
		SameSets(Partitions(first), expected, "flood fill after a second pass");
	}

	/// P1-MEM-02: the split threshold is D + 2m and the merge threshold is D, so a pair oscillating
	/// inside the band must not flap (Partitioning §3.3). The merge threshold moved from D + m to D on
	/// 2026-08-24 (Viktor, "don't merge until the partitions actually reach the sync distance"); this
	/// section still asserted D + m and had been failing on every build since - which also made the
	/// whole-endpoint GET /cluster/self-test throw before it reached the later sections (T-0101).
	private static void MembershipHysteresis()
	{
		PartitionMembership membership = Fresh();
		membership.Evaluate(new[] { At(1, 0), At(2, 100) }, 0);
		Assert(membership.Layouts.Count == 1, "a tight pair should start as one partition");

		// Inside the split threshold (D + 2m = 3600): still one partition, however long we wait.
		for (int step = 0; step < 5; step++)
		{
			PartitionMembership.Change[] changes = membership.Evaluate(new[] { At(1, 0), At(2, 3500) }, 100 + step * 10);
			Assert(changes.Length == 0, $"a gap of 3500 m (< D + 2m) split at step {step}");
		}
		Assert(membership.Layouts.Count == 1, "a gap of 3500 m must stay one partition");

		// Past D + 2m: exactly one split.
		PartitionMembership.Change[] split = membership.Evaluate(new[] { At(1, 0), At(2, 3700) }, 200);
		Assert(split.Length == 1 && split[0].Split, "a gap of 3700 m (> D + 2m) must split once");
		Assert(membership.Layouts.Count == 2, "the split must leave two partitions");

		// Back inside the band [D, D + 2m] = [3000, 3600]: no merge, because merging needs a gap under D.
		foreach (double gap in new[] { 3400.0, 3100.0, 3000.0 })
			for (int step = 0; step < 5; step++)
			{
				PartitionMembership.Change[] changes = membership.Evaluate(new[] { At(1, 0), At(2, gap) }, 300 + step * 10);
				Assert(changes.Length == 0, $"a gap of {gap} m (>= D) merged at step {step}");
			}
		Assert(membership.Layouts.Count == 2, "a gap inside the hysteresis band must stay two partitions");

		// Under D, and past the maturity delay: exactly one merge.
		PartitionMembership.Change[] merge = membership.Evaluate(new[] { At(1, 0), At(2, 2900) }, 400);
		Assert(merge.Length == 1 && !merge[0].Split, "a gap of 2900 m (< D) must merge once");
		Assert(membership.Layouts.Count == 1, "the merge must leave one partition");
	}

	/// T-0319: travellers, shuttles and bridges must not make the topology flap; the touching and docking
	/// exceptions still merge. The scenarios live in MembershipFlapping so Tools/MembershipEquivalence
	/// runs the same ones offline ("flap").
	private static void MembershipFlappingScenarios()
	{
		string[] failures = MembershipFlapping.Failures();
		Assert(failures.Length == 0, string.Join(" || ", failures));
	}

	/// P1-MEM-03: a grid group (rotor/piston/connector) is one atomic unit, so it is never split by
	/// distance and drags its partition together however far apart its members are.
	private static void MembershipAtomicity()
	{
		PartitionMembership membership = Fresh();
		// Two members 40 km apart - far beyond every threshold - but in the same atomic group.
		membership.Evaluate(new[] { At(1, 0, group: 77), At(2, 40000, group: 77) }, 0);
		SameSets(Partitions(membership), new[] { new long[] { 1, 2 } }, "an atomic group must be one partition");

		for (int step = 0; step < 5; step++)
		{
			PartitionMembership.Change[] changes = membership.Evaluate(
				new[] { At(1, 0, group: 77), At(2, 40000 + step * 1000, group: 77) }, 100 + step * 10);
			Assert(changes.Length == 0, $"an atomic group split at step {step}");
		}
		SameSets(Partitions(membership), new[] { new long[] { 1, 2 } }, "an atomic group must stay one partition");

		// A third, unrelated entity beyond the threshold stays out of it.
		membership.Evaluate(new[] { At(1, 0, group: 77), At(2, 40000, group: 77), At(3, 80000) }, 200);
		SameSets(Partitions(membership), new[] { new long[] { 1, 2 }, new long[] { 3 } }, "an unrelated entity must not join the group");
	}

	/// P1-MEM-04: 20 entities chained at 0.9 D are one partition and stay one - the F-D2 limitation
	/// demo (TestPlan §4.1): the chain is transitively connected even though its ends are 48 km apart.
	private static void MembershipConga()
	{
		double step = Distance * 0.9;
		PartitionMembership.Member[] chain = Enumerable.Range(0, 20)
			.Select(index => At(index + 1, index * step)).ToArray();
		PartitionMembership membership = Fresh();
		membership.Evaluate(chain, 0);
		SameSets(Partitions(membership), new[] { Enumerable.Range(1, 20).Select(id => (long)id).ToArray() },
			"a 20-entity conga line at 0.9 D");
		Assert(chain[^1].X - chain[0].X > 48000, "the conga line should span more than 48 km end to end");

		for (int pass = 0; pass < 5; pass++)
		{
			PartitionMembership.Change[] changes = membership.Evaluate(chain, 100 + pass * 10);
			Assert(changes.Length == 0, $"the conga line changed on pass {pass}");
		}
		Assert(membership.Layouts.Count == 1, "the conga line must remain a single stable partition");
	}

	/// The 330/333 duplicate-incarnation regression of 2026-08-16: a member adopted from a save
	/// whose entity has not reached the census yet (parallel entity init) must stay PINNED in its
	/// layout - not dissolve and then get a brand-new registered partition minted around it when
	/// the entity finally surfaces.
	private static void MembershipPinning()
	{
		PartitionMembership membership = Fresh();
		membership.Evaluate(new[] { At(1, 0) }, 0);
		Assert(membership.Layouts.Count == 1, "bootstrap should give one layout");

		// Adopt partition 50 with live member 2 and member 3 still initializing (absent).
		membership.Adopt(50, new long[] { 2 }, 10, mature: false, pending: new long[] { 3 });
		PartitionMembership.Change[] changes = membership.Evaluate(new[] { At(1, 0), At(2, 50000) }, 11);
		Assert(changes.Length == 0, "a pinned layout must not split, merge or be reshaped");
		PartitionMembership.Layout pinned = membership.Layouts.Single(layout => layout.Id == 50);
		Assert(pinned.Members.Contains(3), "the absent member must stay pinned in its layout");
		Assert(pinned.Pending.Length == 1, "the absent member must be marked pending");

		// The entity surfaces: it unpins in place - NO new layout is minted for it.
		int layoutsBefore = membership.Layouts.Count;
		changes = membership.Evaluate(new[] { At(1, 0), At(2, 50000), At(3, 50100) }, 12);
		Assert(changes.Length == 0, "unpinning must not report a topology change");
		Assert(membership.Layouts.Count == layoutsBefore,
			"the surfaced member must not get a new layout minted around it");
		pinned = membership.Layouts.Single(layout => layout.Id == 50);
		Assert(pinned.Members.Contains(3) && pinned.Pending.Length == 0,
			"the surfaced member must be an ordinary member of its own layout");

		// A member that never surfaces is given up on at the pin deadline and the layout moves on.
		membership.Adopt(60, new long[] { 4 }, 100, mature: false, pending: new long[] { 5 });
		membership.Evaluate(new[] { At(1, 0), At(2, 50000), At(3, 50100), At(4, 100000) },
			100 + PartitionMembership.PinSeconds + 1);
		PartitionMembership.Layout expired = membership.Layouts.Single(layout => layout.Id == 60);
		Assert(!expired.Members.Contains(5) && expired.Pending.Length == 0,
			"a member that never materializes must unpin at the deadline");
	}

	// -- save container ----------------------------------------------------

	/// T-0155: the startup claim conflict classifier names stale copies as expected and keeps the
	/// "investigate" wording for a registry behind the local save or a refusal of another kind.
	private static void StartupConflict()
	{
		string Describe(uint local, string message) => PartitionPersistence.DescribeStartupConflict(local, message);
		const string prefix = "registry answered 409 Conflict: RegistryConflictException: ";
		Assert(Describe(0, prefix + "Partition 3 is generation 15, not -1").Contains("template seed"), "generation 0 is a template seed");
		Assert(Describe(1, prefix + "Partition 3 is generation 15, not 0").Contains("stale local copy"), "generation 1 behind 15 is stale");
		Assert(Describe(7, prefix + "Partition 3 is generation 7, not 6").Contains("stale local copy"), "registry at the local generation is stale");
		Assert(Describe(9, prefix + "Partition 3 is generation 4, not 8").Contains("investigate"), "a registry behind the save is a real conflict");
		Assert(Describe(2, prefix + "Partition 3 is owned by live node 'node-2'").Contains("investigate"), "another refusal is flagged");
		Assert(!Describe(1, prefix + "Partition 3 is generation 15, not 0").Contains("investigate"), "a stale copy is not flagged");
	}

	/// T-0186: the ship-antenna predicate. The ticket is about which ships keep their antenna, so the
	/// rule itself is the deliverable - and until now it was reachable only through a live MyCubeGrid
	/// and had no test at all. `assigned` is the rule Viktor chose: the UNION of occupied, a bounded
	/// grace after the pilot leaves, and under way. The two things it must NOT become are "occupied
	/// only" (which switches the antenna off exactly when a ship becomes a runaway, the failure this
	/// ticket is about) and "always on" (which gives back the receiver-set saving).
	private static void ShipAntennaRule()
	{
		const double Grace = 120.0, Speed = 1.0, Now = 1_000_000.0;
		bool W(string rule, bool occupied, double speed, double? lastOccupied) =>
			ShipAntennaService.WantedBy(rule, occupied, speed, lastOccupied, Now, Grace, Speed);
		double AgoSeconds(double s) => Now - s * 1000.0;

		// Occupied always wins, under every rule - a pilot is aboard.
		foreach (string rule in new[] { "occupied", "grace", "moving", "assigned", "off" })
			Assert(W(rule, true, 0.0, null), $"{rule}: an occupied ship keeps its antenna");

		// "occupied" is the literal rule and has the defect the ticket describes: a runaway goes dark.
		Assert(!W("occupied", false, 39.0, AgoSeconds(1)), "occupied: a moving empty ship goes dark - the T-0186 defect");

		// "assigned": under way OR recently left.
		Assert(W("assigned", false, 39.0, null), "assigned: under way with nobody aboard stays on");
		Assert(W("assigned", false, 0.0, AgoSeconds(60)), "assigned: 60 s after the pilot left, still on");
		Assert(!W("assigned", false, 0.0, AgoSeconds(180)), "assigned: 180 s parked and empty goes quiet");
		Assert(!W("assigned", false, 0.0, null), "assigned: a never-occupied parked hull is off");
		// The boundary is the one place an off-by-one would hide, and it is exclusive.
		Assert(!W("assigned", false, 0.0, AgoSeconds(Grace)), "assigned: exactly at the grace bound is off");
		Assert(W("assigned", false, 0.0, AgoSeconds(Grace - 0.001)), "assigned: just inside the grace bound is on");
		// Speed is a strict threshold, so drift below it does not hold an idle hull on.
		Assert(!W("assigned", false, Speed, null), "assigned: exactly at the speed threshold is off");
		Assert(W("assigned", false, Speed + 0.001, null), "assigned: just over the speed threshold is on");

		// assigned is a strict superset of grace and of moving, and is NOT always-on.
		foreach (double speed in new[] { 0.0, 39.0 })
			foreach (double? last in new double?[] { null, AgoSeconds(60), AgoSeconds(180) })
			{
				if (W("grace", false, speed, last) || W("moving", false, speed, last))
					Assert(W("assigned", false, speed, last), "assigned covers grace and moving");
			}
		Assert(!W("assigned", false, 0.0, AgoSeconds(3600)), "assigned is not always-on");

		// An unknown rule must be the conservative answer, not a crash and not always-on.
		Assert(!W("nonsense", false, 39.0, AgoSeconds(1)), "an unknown rule falls back to occupied-only");
	}

	/// T-0213: a character captured BOTH at top level and as a cockpit's Pilot is the state that makes
	/// the handover round-trip gate abort - the import collapses the two into one and the re-export is
	/// one entity short. The detector must fire on that and stay silent on an ordinary seated pilot,
	/// which is NOT a top-level member of its grid's partition.
	/// T-0224: recovery empties a cockpit whose pilot is already live on another node, and only that one.
	/// <summary>
	/// T-0237: a recovered stamped save WITHOUT a stamp for the home's own body. w33 2026-09-27: Pilot-028's standing
	/// body split into 73626 (save 1/1 captured at cluster time 107139813, no stamp - the home was not yet re-reported
	/// there), then boarded a ship whose partition 75485 went offline with a Final stamping the body (110255631); the
	/// start recovered the old standing copy beside the seated one. The copy is stale when the home partition's
	/// stamped copy was captured after every covered save, and kept when it is the newer one or nothing orders it.
	/// </summary>
	// T-0264: with 64k registry blocks the 75% prefetch runs every block instead of never. A failed prefetch
	// must leave the generator issuing from the quarter still left; only running out of ids may throw.
	private static void NetworkIdPrefetch()
	{
		int calls = 0;
		NetworkIdRangeGenerator failing = new NetworkIdRangeGenerator(new NetworkIdBlock(1, 64), () =>
			++calls == 1 ? throw new InvalidOperationException("registry unavailable") : new NetworkIdBlock(101, 164));
		List<uint> issued = Enumerable.Range(0, 65).Select(_ => failing.NextId()).ToList();
		Assert(issued.Take(64).SequenceEqual(Enumerable.Range(1, 64).Select(x => (uint)x)), "a failed prefetch keeps issuing the current block");
		Assert(issued[64] == 101 && calls == 2, "running out allocates the next block: last=" + issued[64] + " calls=" + calls);

		int prefetches = 0;
		uint next = 1;
		NetworkIdRangeGenerator healthy = new NetworkIdRangeGenerator(new NetworkIdBlock(next, next + 63), () =>
		{
			prefetches++;
			next += 100;
			return new NetworkIdBlock(next, next + 63);
		});
		List<uint> ids = Enumerable.Range(0, 64 * 3).Select(_ => healthy.NextId()).ToList();
		Assert(prefetches == 3 && ids.Distinct().Count() == ids.Count && ids[64] == 101 && ids[128] == 201,
			"prefetch hands over block to block: prefetches=" + prefetches + " ids[64]=" + ids[64] + " ids[128]=" + ids[128]);

		// T-0268: the forced prefetch draws one block now, a second call draws nothing, and the ids run on
		// into the forced block without another allocation at 75%.
		int forcedCalls = 0;
		NetworkIdRangeGenerator forced = new NetworkIdRangeGenerator(new NetworkIdBlock(1, 64), () =>
		{
			forcedCalls++;
			return new NetworkIdBlock((uint)(100 * forcedCalls + 1), (uint)(100 * forcedCalls + 64));
		});
		forced.NextId();
		NetworkIdBlock? first = forced.PrefetchNow();
		NetworkIdBlock? second = forced.PrefetchNow();
		List<uint> after = Enumerable.Range(0, 64).Select(_ => forced.NextId()).ToList();
		Assert(first is { Start: 101 } && second == null && forcedCalls == 1 && after[63] == 101,
			"forced prefetch draws once and is used next: first=" + first?.Start + " second=" + second?.Start + " calls=" + forcedCalls + " next=" + after[63]);
	}

	/// <summary>
	/// T-0265: the per-client inventory delta counters travel with the client's view. The codec carries them
	/// (and still reads a state written without them), and the target's fresh counter is raised to the source's.
	/// </summary>
	private static void InventoryMessageIds()
	{
		ClientHandoverState state = new ClientHandoverState();
		state.Replicables.Add(new ClientReplicableState { NetworkId = 11 });
		ClientReplicableState character = new ClientReplicableState { NetworkId = 12 };
		character.InventoryMessageIds.AddRange(new uint[] { 7, 0 });
		state.Replicables.Add(character);
		ClientHandoverState read = ClientHandoverCodec.Read(ClientHandoverCodec.Write(state));
		Assert(read.Replicables[0].InventoryMessageIds.Count == 0 && read.Replicables[1].InventoryMessageIds.SequenceEqual(new uint[] { 7, 0 }),
			"inventory counters survive the codec");
		ClientHandoverState legacy = new ClientHandoverState();
		legacy.Replicables.Add(new ClientReplicableState { NetworkId = 12 });
		Assert(ClientHandoverCodec.Read(ClientHandoverCodec.Write(legacy)).Replicables.Single().InventoryMessageIds.Count == 0,
			"a state without counters still reads");

		var endpoint = new VRage.Network.Endpoint(new VRage.Network.EndpointId(76561198223054696UL), 0);
		var source = new Sandbox.Game.Replication.StateGroups.MyEntityInventoryStateGroup(new Sandbox.Game.MyInventory(), false, null);
		source.CreateClientData(endpoint);
		source.m_clientInventoryUpdate[endpoint].CurrentMessageId = 7;
		var exported = new List<uint>();
		ReplicationClientViewPatch.ExportInventoryMessageIds(new VRage.Network.IMyStateGroup[] { source }, endpoint, exported);
		Assert(exported.SequenceEqual(new uint[] { 7 }), "the source's counter is exported: " + string.Join(",", exported));
		var target = new Sandbox.Game.Replication.StateGroups.MyEntityInventoryStateGroup(new Sandbox.Game.MyInventory(), false, null);
		target.CreateClientData(endpoint);
		Assert(target.m_clientInventoryUpdate[endpoint].CurrentMessageId == 0, "a fresh target counter starts at 0 (the defect)");
		Assert(ReplicationClientViewPatch.ImportInventoryMessageIds(new VRage.Network.IMyStateGroup[] { target }, endpoint, exported)
			&& target.m_clientInventoryUpdate[endpoint].CurrentMessageId == 7, "the target continues from the source's counter");
		Assert(!ReplicationClientViewPatch.ImportInventoryMessageIds(new VRage.Network.IMyStateGroup[] { target }, endpoint, new List<uint> { 1, 2 }),
			"groups that do not line up are left alone");
		var untouched = new List<uint>();
		var fresh = new Sandbox.Game.Replication.StateGroups.MyEntityInventoryStateGroup(new Sandbox.Game.MyInventory(), false, null);
		fresh.CreateClientData(endpoint);
		ReplicationClientViewPatch.ExportInventoryMessageIds(new VRage.Network.IMyStateGroup[] { fresh }, endpoint, untouched);
		Assert(untouched.Count == 0, "an inventory that never sent a delta exports nothing");
	}

	private static void StaleHomeCopy()
	{
		const long Identity = 144115188092633232, Body = 72057594088302385;
		PartitionPersistence.RecoveryClaim Claim(long saveCaptured, long homeCaptured, Dictionary<long, long> stamps = null) =>
			new PartitionPersistence.RecoveryClaim(73626UL, 2, Array.Empty<PartitionPersistence.RecoveryFile>(), Array.Empty<PartitionPersistence.RecoveryFile>())
			{
				HomedElsewhere = new HashSet<long>(),
				HomesByIdentity = new Dictionary<long, PartitionPersistence.HomeElsewhere>
				{
					[Identity] = new PartitionPersistence.HomeElsewhere(Body, 75485UL, 14960, DateTimeOffset.UnixEpoch, homeCaptured),
				},
				SaveHomeStamps = stamps ?? new Dictionary<long, long>(),
				SaveStamped = true,
				SaveCreated = DateTimeOffset.UnixEpoch,
				SaveCaptured = saveCaptured,
			};
		MyObjectBuilder_Character standing = new MyObjectBuilder_Character { EntityId = Body, OwningPlayerIdentityId = Identity };
		string Why(PartitionPersistence.RecoveryClaim claim) => PartitionPersistence.StaleByIdentity(claim, standing);
		Assert(Why(Claim(107139813, 110255631)) is { } why && why.Contains("107139813") && why.Contains("110255631"),
			"an unstamped copy captured before the home's stamped copy is stale (Pilot-028, 73626 vs 75485)");
		Assert(Why(Claim(110255631, 107139813)) == null, "an unstamped copy captured AFTER the home's stamped copy is kept");
		Assert(Why(Claim(110255631, 110255631)) == null, "the same capture time orders nothing: kept");
		Assert(Why(Claim(107139813, 0)) == null, "no stamped copy in the home partition (an older registry) keeps the copy");
		Assert(Why(Claim(0, 110255631)) == null, "no covered capture time (an older registry) keeps the copy");
		Assert(Why(Claim(107139813, 110255631, new Dictionary<long, long> { [Body] = 14960 })) == null,
			"a stamp for the body decides by version, never by capture time");
		Assert(Why(Claim(107139813, 110255631, new Dictionary<long, long> { [Body] = 14000 })) is { } older && older.Contains("copy version"),
			"a stamp below the home's version is still stale by version");
	}

	private static void NestedPilotLiveElsewhere()
	{
		MyObjectBuilder_CubeGrid Grid(long id, params (long Cockpit, long? Pilot)[] seats)
		{
			MyObjectBuilder_CubeGrid grid = new MyObjectBuilder_CubeGrid { EntityId = id, CubeBlocks = new List<MyObjectBuilder_CubeBlock>() };
			foreach ((long cockpitId, long? pilotId) in seats)
			{
				MyObjectBuilder_Cockpit cockpit = new MyObjectBuilder_Cockpit { EntityId = cockpitId };
				if (pilotId is { } seated)
				{
					cockpit.Pilot = new MyObjectBuilder_Character { EntityId = seated };
					// The game serialises the seat a second time as a hierarchy child of the cockpit.
					cockpit.ComponentContainer = new VRage.Game.ObjectBuilders.ComponentSystem.MyObjectBuilder_ComponentContainer();
					cockpit.ComponentContainer.Components.Add(new VRage.Game.ObjectBuilders.ComponentSystem.MyObjectBuilder_ComponentContainer.ComponentData
					{
						TypeId = "MyHierarchyComponentBase",
						Component = new VRage.Game.ObjectBuilders.Components.MyObjectBuilder_HierarchyComponentBase
						{
							Children = new List<MyObjectBuilder_EntityBase> { new MyObjectBuilder_Character { EntityId = seated } },
						},
					});
				}
				grid.CubeBlocks.Add(cockpit);
			}
			return grid;
		}
		HashSet<long> live = new HashSet<long> { 200 };
		MyObjectBuilder_CubeGrid ship = Grid(100, (101, 200), (102, 201), (103, null));
		int stripped = PartitionPersistence.StripPilotsLiveElsewhere(7UL, new MyObjectBuilder_EntityBase[] { ship, new MyObjectBuilder_Character { EntityId = 300 } }, live.Contains);
		Assert(stripped == 1, "exactly the pilot that is live elsewhere is removed");
		Assert(((MyObjectBuilder_Cockpit)ship.CubeBlocks[0]).Pilot == null, "the cockpit whose pilot is live elsewhere is emptied");
		int HierarchyChildren(int block) => ship.CubeBlocks[block].ComponentContainer.Components
			.Sum(data => (data.Component as VRage.Game.ObjectBuilders.Components.MyObjectBuilder_HierarchyComponentBase)?.Children.Count ?? 0);
		Assert(HierarchyChildren(0) == 0, "the hierarchy copy of the pilot goes too, or the game re-creates it in the emptied seat (T-0226)");
		Assert(((MyObjectBuilder_Cockpit)ship.CubeBlocks[1]).Pilot?.EntityId == 201, "a pilot that is not live elsewhere stays seated");
		Assert(HierarchyChildren(1) == 1, "a pilot that stays seated keeps its hierarchy copy");
		// T-0226: the state a Pilot-only strip left behind - an empty seat whose hierarchy still holds the body.
		MyObjectBuilder_CubeGrid orphaned = Grid(110, (111, 210), (112, 211));
		((MyObjectBuilder_Cockpit)orphaned.CubeBlocks[0]).Pilot = null;
		Assert(PartitionPersistence.StripOrphanSeatCharacters(7UL, new MyObjectBuilder_EntityBase[] { orphaned }) == 1,
			"exactly the character of the empty seat is removed");
		int OrphanedChildren(int block) => orphaned.CubeBlocks[block].ComponentContainer.Components
			.Sum(data => (data.Component as VRage.Game.ObjectBuilders.Components.MyObjectBuilder_HierarchyComponentBase)?.Children.Count ?? 0);
		Assert(OrphanedChildren(0) == 0 && OrphanedChildren(1) == 1, "the seated pilot's hierarchy copy is kept");
		Assert(PartitionPersistence.StripPilotsLiveElsewhere(7UL, new MyObjectBuilder_EntityBase[0], live.Contains) == 0, "an empty save strips nothing");
	}

	/// T-0226: the recovery census counts a peer's seated pilots as live, so a stale standalone copy of a
	/// pilot sitting in a ship on another node is skipped instead of recovered next to it. A peer that
	/// predates the "seated" field still screens its members.
	private static void CensusSeatedPilots()
	{
		HashSet<long> live = new HashSet<long>();
		using (System.Text.Json.JsonDocument current = System.Text.Json.JsonDocument.Parse(
			"{\"id\":7,\"members\":[{\"id\":100},{\"id\":300}],\"seated\":[200,201]}"))
			PartitionPersistence.AddCensusIds(current.RootElement, live);
		Assert(live.SetEquals(new long[] { 100, 300, 200, 201 }), "members and seated pilots are both live elsewhere");
		live.Clear();
		using (System.Text.Json.JsonDocument older = System.Text.Json.JsonDocument.Parse("{\"id\":7,\"members\":[{\"id\":100}]}"))
			PartitionPersistence.AddCensusIds(older.RootElement, live);
		Assert(live.SetEquals(new long[] { 100 }), "a peer without the seated field still screens its members");
	}

	/// <summary>T-0295: a recovery of partition P is not vetoed by a peer's (stale) copy of P itself, and still
	/// is by any other partition's members; the plain HashSet census (the old behaviour) cannot tell them apart.</summary>
	private static void CensusSameLineage()
	{
		PartitionPersistence.LiveCensus census = new PartitionPersistence.LiveCensus();
		foreach (string json in new[]
		{
			"{\"id\":227734,\"members\":[{\"id\":72057596621638718}],\"seated\":[72057594088301255]}",
			"{\"id\":5,\"members\":[{\"id\":500}],\"seated\":[501]}",
		})
		{
			using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
			PartitionPersistence.AddCensusIds(doc.RootElement, census);
		}
		Assert(census.Count == 4, $"the census holds every peer member and seated pilot, got {census.Count}");
		HashSet<long> screened = census.ExcludingLineage(227734UL, out int stale);
		Assert(stale == 2 && !screened.Contains(72057596621638718L) && !screened.Contains(72057594088301255L),
			$"the recovered partition's own members held by a peer's stale copy are not screened (stale {stale})");
		Assert(screened.SetEquals(new long[] { 500, 501 }), "another partition's members and pilots are still screened");
		HashSet<long> other = census.ExcludingLineage(9UL, out int none);
		Assert(none == 0 && other.Count == 4, "a lineage no peer holds excludes nothing");
		// The red: the pre-T-0295 census, a plain set, screens the recovered partition's own members.
		HashSet<long> plain = new HashSet<long>();
		using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
			"{\"id\":227734,\"members\":[{\"id\":72057596621638718}],\"seated\":[72057594088301255]}"))
			PartitionPersistence.AddCensusIds(doc.RootElement, plain);
		Assert(plain.Contains(72057596621638718L) && plain.Contains(72057594088301255L),
			"the plain census vetoes the recovered partition's own members (the 227734 loss)");
	}

	/// <summary>#1286 (F41): a peer copy screens recovered content only while the registry assigns it to its node. A copy
	/// of another partition Q whose node is Dead, or whose Q row names another owner or none, is about to be discarded
	/// (or, booting, confirmed): the old census skipped an entity adopted into Q after Q's last save, and Q's next claimant
	/// lost it. A recovery defers on both kinds; the boot path (no deferral) still screens against an uncertain copy.</summary>
	private static void CensusStaleCopy()
	{
		PartitionPersistence.LiveCensus Census(params (string Node, string Json)[] copies)
		{
			PartitionPersistence.LiveCensus census = new PartitionPersistence.LiveCensus();
			foreach ((string node, string json) in copies)
			{
				using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
				PartitionPersistence.AddCensusIds(doc.RootElement, census, node);
			}
			census.Nodes["node-2"] = 0;
			census.Nodes["node-3"] = 0;
			census.Nodes["node-5"] = 0;
			census.Nodes["node-6"] = 2;
			census.Rows[50] = (true, "node-3");
			return census;
		}
		const string q = "{\"id\":40,\"members\":[{\"id\":400}],\"seated\":[401]}";
		const string s = "{\"id\":50,\"members\":[{\"id\":500}]}";
		HashSet<long> cover = new HashSet<long> { 400, 401, 500, 900 };
		string Ids(IEnumerable<long> ids) => "[" + string.Join(",", ids.OrderBy(x => x)) + "]";

		PartitionPersistence.LiveCensus current = Census(("node-2", q), ("node-3", s));
		current.Rows[40] = (true, "node-2");
		HashSet<long> screened = current.ScreenedForLoad(30UL, true, false, cover, out HashSet<long> none);
		Assert(screened.SetEquals(new long[] { 400, 401, 500 }) && none.Count == 0, "copies the registry assigns to their node screen");

		foreach ((string why, (bool, string) row, bool stale) in new (string, (bool, string), bool)[]
		{
			("Q owned by another live node", (true, "node-5"), true),
			("Q's claim lapsed (no owner)", (true, null), false),
			("Q owned by a Dead node", (true, "node-6"), false),
			("Q owned by an unregistered node", (true, "node-77"), false),
		})
		{
			PartitionPersistence.LiveCensus census = Census(("node-2", q), ("node-3", s));
			census.Rows[40] = row;
			HashSet<long> kept = census.ScreenedForLoad(30UL, true, false, cover, out HashSet<long> staleOnly);
			Assert(kept.SetEquals(new long[] { 500 }) && staleOnly.SetEquals(new long[] { 400, 401 }),
				$"{why}: a recovery is not screened by Q's member or seated pilot, they are stale-only (kept {Ids(kept)}, stale {Ids(staleOnly)})");
			HashSet<long> boot = census.ScreenedForLoad(30UL, false, true, cover, out HashSet<long> bootStale);
			Assert(boot.Contains(400) == !stale && bootStale.Contains(400) == stale,
				$"{why}: the boot path {(stale ? "does not screen against the stale copy" : "still screens against the uncertain copy")}");
			// The red: the pre-#1286 census screens them anyway.
			Assert(census.ExcludingLineage(30UL, out _).Contains(400), $"{why}: the unchecked census screens Q's member (the F41 loss)");
		}

		PartitionPersistence.LiveCensus dead = Census(("node-6", q), ("node-3", s));
		dead.Rows[40] = (true, "node-6");
		Assert(dead.ScreenedForLoad(30UL, true, false, cover, out HashSet<long> deadOnly).SetEquals(new long[] { 500 })
			&& deadOnly.SetEquals(new long[] { 400, 401 }), "a Dead node's copies do not screen, whatever its rows say (its process may still answer)");
		Assert(!dead.ScreenedForLoad(30UL, false, true, cover, out _).Contains(400), "nor at boot");

		PartitionPersistence.LiveCensus unknown = Census(("node-2", q), (null, s), ("node-9", "{\"id\":70,\"members\":[{\"id\":700}]}"));
		unknown.Rows[70] = (true, "node-5");
		Assert(unknown.ScreenedForLoad(30UL, true, false, cover, out HashSet<long> unknownStale).SetEquals(new long[] { 400, 401, 500, 700 })
			&& unknownStale.Count == 0, "a copy with no row read, no node, or an unregistered node screens as before (the duplicate-safe side)");
		unknown.Rows[40] = (false, null);
		Assert(unknown.ScreenedForLoad(30UL, true, false, cover, out _).Contains(400), "a partition the registry has no row for yet (a fresh split) screens");

		PartitionPersistence.LiveCensus both = Census(("node-2", q), ("node-3", "{\"id\":50,\"members\":[{\"id\":500},{\"id\":400}]}"));
		both.Rows[40] = (true, "node-5");
		Assert(both.ScreenedForLoad(30UL, true, false, cover, out HashSet<long> bothStale).Contains(400) && !bothStale.Contains(400),
			"an entity a current copy also holds still screens");

		PartitionPersistence.LiveCensus lineage = Census(("node-2", "{\"id\":30,\"members\":[{\"id\":400}]}"));
		lineage.Rows[30] = (true, "node-5");
		Assert(!lineage.ScreenedForLoad(30UL, true, false, cover, out HashSet<long> lineageStale).Contains(400) && lineageStale.Count == 0,
			"a peer copy of the recovered partition itself is cut by the lineage rule (T-0295), not deferred for");
		Assert(lineage.ScreenedForLoad(30UL, false, true, cover, out HashSet<long> bootLineage).Count == 0 && bootLineage.SetEquals(new long[] { 400 }),
			"at boot (no lineage cut) a copy of the same partition the registry assigns to another live node does not screen");
		lineage.Rows[30] = (true, "node-2");
		Assert(lineage.ScreenedForLoad(30UL, false, true, cover, out _).Contains(400),
			"at boot a peer's current copy of the same partition still screens (this node's copy is the one to be fenced)");
	}

	private static void DoubleCapturedPilot()
	{
		MyObjectBuilder_EntityBase Character(long id) =>
			new MyObjectBuilder_Character { EntityId = id };
		MyObjectBuilder_CubeGrid Grid(long id, long cockpitId, long? pilotId)
		{
			MyObjectBuilder_Cockpit cockpit = new MyObjectBuilder_Cockpit { EntityId = cockpitId };
			if (pilotId is { } seated) cockpit.Pilot = new MyObjectBuilder_Character { EntityId = seated };
			return new MyObjectBuilder_CubeGrid
			{
				EntityId = id,
				CubeBlocks = new List<MyObjectBuilder_CubeBlock> { cockpit },
			};
		}
		int Check(params MyObjectBuilder_EntityBase[] entities) =>
			PartitionPersistence.WarnOnDoubleCapturedPilots(7UL, "SelfTest", entities);

		// The healthy shape: the pilot exists ONLY inside the cockpit.
		Assert(Check(Grid(100, 101, 200)) == 0, "a seated pilot that is not a captured member is not a duplicate");
		// An empty seat, and a free character with no seat at all: neither is a duplicate.
		Assert(Check(Grid(100, 101, null), Character(200)) == 0, "an empty cockpit beside a loose character is not a duplicate");
		// The defect: the same id at top level AND in the cockpit.
		Assert(Check(Grid(100, 101, 200), Character(200)) == 1, "a pilot captured at top level AND in its cockpit is a duplicate");
		// A different character at top level must not be mistaken for the seated one.
		Assert(Check(Grid(100, 101, 200), Character(201)) == 0, "a different top-level character is not the seated pilot");
		// Two forked pilots on one grid are two findings, not one.
		MyObjectBuilder_CubeGrid two = Grid(100, 101, 200);
		MyObjectBuilder_Cockpit second = new MyObjectBuilder_Cockpit { EntityId = 102, Pilot = new MyObjectBuilder_Character { EntityId = 201 } };
		two.CubeBlocks.Add(second);
		Assert(Check(two, Character(200), Character(201)) == 2, "each double-captured pilot counts once");
		// Degenerate inputs must not throw.
		Assert(Check() == 0, "an empty capture has no duplicates");
		Assert(PartitionPersistence.WarnOnDoubleCapturedPilots(7UL, "SelfTest", null) == 0, "a null capture has no duplicates");
	}

	/// T-0349/T-0350: which saves the write worker keeps retrying. An unbounded retry holds the save queue.
	private static void SaveRetryPolicyBounds()
	{
		Assert(SaveRetryPolicy.ShouldRetry("Periodic", 1) && !SaveRetryPolicy.ShouldRetry("Periodic", 2), "a Periodic save is retried once");
		Assert(SaveRetryPolicy.ShouldRetry("HandoverTee", 2, unregistered: true) && !SaveRetryPolicy.ShouldRetry("HandoverTee", 3, unregistered: true),
			"a HandoverTee the registry could not be reached for is given up after three attempts");
		Assert(SaveRetryPolicy.ShouldRetry("HandoverTee", 50, unregistered: false), "a HandoverTee that failed for another reason keeps retrying");
		Assert(SaveRetryPolicy.ShouldRetry("Final", 50, unregistered: true), "a Final keeps retrying: nothing supersedes it");
		Assert(SaveRetryPolicy.RegistryUnreachable(new System.Text.Json.JsonException("empty")) && SaveRetryPolicy.RegistryUnreachable(new System.IO.IOException("reset"))
			&& !SaveRetryPolicy.RegistryUnreachable(new System.Net.Http.HttpRequestException("conflict", null, System.Net.HttpStatusCode.Conflict)),
			"an empty answer or a transport error is unreachable; a 409 is a rejection");
	}

	/// <summary>
	/// T-0388: a recovery whose saves held content but loaded none of it (321894 on node-282 2026-10-03 01:11:04: its only
	/// entity live on another node) is released once its empty Final is catalogued at the recovered generation - never
	/// before (the release would hand recovery the older save that still holds the content) - and a save that was
	/// already empty is never released again (a pinned row then costs one lap, not a loop).
	/// </summary>
	private static void EmptyRecoveryRelease()
	{
		Assert(PartitionPersistence.MarkEmptyRecovery(enabled: true, savedEntities: 1, loadedMembers: 0),
			"a recovery that screened out its only saved entity is marked for release (the defect: held until the drain)");
		Assert(!PartitionPersistence.MarkEmptyRecovery(enabled: false, savedEntities: 1, loadedMembers: 0),
			"recovery.releaseEmpty=false holds it as before (the red)");
		Assert(!PartitionPersistence.MarkEmptyRecovery(true, 0, 0), "an empty save is not released again (no release/recover loop on a pinned row)");
		Assert(!PartitionPersistence.MarkEmptyRecovery(true, 3, 1), "a recovery that loaded a member is an ordinary partition");

		PartitionPersistence.EmptyRecoveryStep Next(uint? owned = 3, int members = 0, bool claimPending = false, bool savePending = false,
			uint? catalogued = 3, bool renewed = true) =>
			PartitionPersistence.EmptyRecoveryNext(3, owned, members, claimPending, savePending, catalogued, renewed);
		Assert(Next() == PartitionPersistence.EmptyRecoveryStep.Release, "owned, renewed, empty Final catalogued at the generation: released");
		Assert(Next(catalogued: null) == PartitionPersistence.EmptyRecoveryStep.Wait,
			"no catalogued Final yet: waits (a release now would recover the older save that holds the content)");
		Assert(Next(catalogued: 2) == PartitionPersistence.EmptyRecoveryStep.Wait, "a save of another generation is not the empty Final");
		Assert(Next(savePending: true) == PartitionPersistence.EmptyRecoveryStep.Wait, "a save still in flight waits");
		Assert(Next(claimPending: true) == PartitionPersistence.EmptyRecoveryStep.Wait, "a claim not yet confirmed waits");
		Assert(Next(renewed: false) == PartitionPersistence.EmptyRecoveryStep.Wait, "waits for a heartbeat that renewed it at this generation");
		Assert(Next(owned: null) == PartitionPersistence.EmptyRecoveryStep.Forget && Next(owned: 4) == PartitionPersistence.EmptyRecoveryStep.Forget,
			"no longer this node's at the generation: left alone");
		Assert(Next(members: 1) == PartitionPersistence.EmptyRecoveryStep.Forget, "a partition with members again is not released");
	}

	/// T-0350: what sets the registry hold from a thread other than the heartbeat, what a cached registry lookup does
	/// while it is set, the retry-line throttle, and the NetworkId generator not asking a registry that is away.
	/// <summary>
	/// T-0368: a modified asteroid the client holds is carried across a handover only when its copy is current and
	/// the target can address it; the codec carries what the target needs (and still reads a state without it).
	/// </summary>
	private static void VoxelCarryPolicyRules()
	{
		Assert(VoxelCarryPolicy.Source(planet: false, delivered: true, stale: false, unsettled: false) == VoxelCarry.Carry, "a delivered, current asteroid replica is exported (the defect: it was destroyed)");
		Assert(VoxelCarryPolicy.Source(true, true, false, false) == VoxelCarry.Planet, "planets stay as they were");
		Assert(VoxelCarryPolicy.Source(false, false, false, false) == VoxelCarry.NotDelivered, "a replica still streaming is not carried");
		Assert(VoxelCarryPolicy.Source(false, true, true, false) == VoxelCarry.Stale, "a replica a journal write passed by is not carried");
		Assert(VoxelCarryPolicy.Source(false, true, false, true) == VoxelCarry.Unsettled, "a source whose watermark overstates its storage does not carry");

		VoxelCarry Target(bool present = true, bool groupsMatch = true, bool unsettled = false, bool ownEdits = false, long source = 7, long target = 7,
			bool sameIds = false, bool idsFree = true, bool viewed = false) =>
			VoxelCarryPolicy.Target(present, groupsMatch, unsettled, ownEdits, source, target, sameIds, idsFree, viewed);
		Assert(Target() == VoxelCarry.Remap, "an unviewed target replicable takes the source's ids");
		Assert(Target(sameIds: true, viewed: true, idsFree: false) == VoxelCarry.Carry, "ids that already agree carry, whoever else views it");
		Assert(Target(target: 6) == VoxelCarry.Remap, "a target behind the source catches up from the journal");
		Assert(Target(present: false) == VoxelCarry.Missing, "a voxel the target does not replicate falls back");
		Assert(Target(groupsMatch: false) == VoxelCarry.Groups, "state groups that do not line up fall back");
		Assert(Target(unsettled: true) == VoxelCarry.Unsettled, "a target replaying the storage falls back");
		Assert(Target(ownEdits: true, sameIds: true) == VoxelCarry.OwnEdits, "a target edit the journal has not sequenced falls back");
		Assert(Target(target: 8, sameIds: true) == VoxelCarry.Newer, "a target ahead of the client's copy falls back");
		Assert(Target(viewed: true) == VoxelCarry.Viewed, "ids another client of the target knows are not changed");
		Assert(Target(idsFree: false) == VoxelCarry.IdTaken, "an id held by another object is not taken");
		Assert(VoxelCarryPolicy.Carries(VoxelCarry.Carry) && VoxelCarryPolicy.Carries(VoxelCarry.Remap)
			&& Enum.GetValues(typeof(VoxelCarry)).Cast<VoxelCarry>().Count(VoxelCarryPolicy.Carries) == 2, "every other verdict is a fallback");
		Assert(VoxelCarryPolicy.Waits(VoxelCarry.Missing, 15, 0, expired: false), "a voxel the target has not generated yet is waited for (r42: destroyed ~1 s too early)");
		Assert(VoxelCarryPolicy.Waits(VoxelCarry.Remap, 15, 3, expired: false) && !VoxelCarryPolicy.Waits(VoxelCarry.Remap, 15, 15, expired: false),
			"a target behind the client's copy is waited for, a caught-up one carries at once");
		Assert(!VoxelCarryPolicy.Waits(VoxelCarry.Missing, 15, 0, expired: true) && !VoxelCarryPolicy.Waits(VoxelCarry.Newer, 15, 16, expired: false),
			"the wait ends at the deadline, and a target ahead falls back at once");

		ClientHandoverState state = new ClientHandoverState();
		state.Replicables.Add(new ClientReplicableState { NetworkId = 11 });
		ClientReplicableState voxel = new ClientReplicableState { NetworkId = 12, VoxelEntityId = 488017231796082567, VoxelApplied = 7 };
		voxel.VoxelGroupIds.Add(13);
		state.Replicables.Add(voxel);
		ClientHandoverState read = ClientHandoverCodec.Read(ClientHandoverCodec.Write(state));
		Assert(read.Replicables[0].VoxelEntityId == 0 && read.Replicables[1].VoxelEntityId == 488017231796082567 && read.Replicables[1].VoxelApplied == 7
			&& read.Replicables[1].VoxelGroupIds.SequenceEqual(new uint[] { 13 }) && read.Replicables[1].InventoryMessageIds.Count == 0,
			"a carried voxel survives the codec without inventory counters");
		state.Replicables[0].InventoryMessageIds.Add(5);
		read = ClientHandoverCodec.Read(ClientHandoverCodec.Write(state));
		Assert(read.Replicables[0].InventoryMessageIds.SequenceEqual(new uint[] { 5 }) && read.Replicables[1].VoxelGroupIds.SequenceEqual(new uint[] { 13 }),
			"and beside them");
	}

	/// <summary>T-0364: a storage generated after its entries were skipped, or regenerated, replays its journal.</summary>
	private static void VoxelReplayPolicyRules()
	{
		Assert(VoxelReplayPolicy.NeedsReplay(skipped: true, sameInstance: null), "a storage that appears after its entries were skipped replays");
		Assert(VoxelReplayPolicy.NeedsReplay(skipped: false, sameInstance: false), "a regenerated storage (another voxel instance, same entity id) replays");
		Assert(!VoxelReplayPolicy.NeedsReplay(skipped: false, sameInstance: true), "the instance the watermark was applied to does not replay");
		Assert(!VoxelReplayPolicy.NeedsReplay(skipped: false, sameInstance: null), "a storage without a watermark has nothing to replay");
		Assert(VoxelReplayPolicy.ReplayFrom(null) == 0 && VoxelReplayPolicy.ReplayFrom(12) == 12, "a replay starts at the catalogued base tail, else at the first entry");
	}

	private static void RegistryHoldPolicyRules()
	{
		var refused = new System.Net.Http.HttpRequestException("connection refused");
		var unavailable = new System.Net.Http.HttpRequestException("503", null, System.Net.HttpStatusCode.ServiceUnavailable);
		Assert(SaveRetryPolicy.RegistryNoAnswer(refused) && SaveRetryPolicy.RegistryNoAnswer(new System.Threading.Tasks.TaskCanceledException("timeout"))
			&& SaveRetryPolicy.RegistryNoAnswer(new RegistryUnreachableException("registry unreachable", refused)),
			"a refused connection or a timeout, wrapped by the save worker or not, is no answer");
		Assert(!SaveRetryPolicy.RegistryNoAnswer(unavailable) && !SaveRetryPolicy.RegistryNoAnswer(new System.Text.Json.JsonException("empty"))
			&& !SaveRetryPolicy.RegistryNoAnswer(new RegistryUnreachableException("registry unreachable", unavailable))
			&& !SaveRetryPolicy.RegistryNoAnswer(new InvalidOperationException("Registry unreachable: no node roster")),
			"an answer, however bad, is left to the heartbeat; so is a site's own fail-fast exception");

		Assert(RegistryHoldPolicy.CachedLookup(false, true, 100, 200) == RegistryHoldPolicy.Lookup.Cached
			&& RegistryHoldPolicy.CachedLookup(false, true, 200, 200) == RegistryHoldPolicy.Lookup.Fetch
			&& RegistryHoldPolicy.CachedLookup(false, false, 0, 0) == RegistryHoldPolicy.Lookup.Fetch,
			"with the registry reachable a lookup is cached until it expires and fetched after, as before");
		Assert(RegistryHoldPolicy.CachedLookup(true, true, 100, 200) == RegistryHoldPolicy.Lookup.Cached
			&& RegistryHoldPolicy.CachedLookup(true, true, 900000, 200) == RegistryHoldPolicy.Lookup.Cached
			&& RegistryHoldPolicy.CachedLookup(true, false, 0, 0) == RegistryHoldPolicy.Lookup.Fail,
			"with the registry unreachable the last answer serves at any age, and none fails at once");

		Assert(RegistryHoldPolicy.HeartbeatAnswering(10000, 9000) && !RegistryHoldPolicy.HeartbeatAnswering(10000, 8500),
			"a failure beside an answered heartbeat sets no hold; one 1.5 s after the last answer does");

		long last = 0;
		Assert(RegistryHoldPolicy.LogDue(ref last, 1000, 10) && !RegistryHoldPolicy.LogDue(ref last, 1009, 10) && last == 1000
			&& RegistryHoldPolicy.LogDue(ref last, 1010, 10) && last == 1010, "a throttled line is due first, and again one interval later");

		// 75% of 1..64 is id 49. Unreachable: no attempt there, and running out throws without one.
		bool unreachable = true;
		long clock = 0;
		int calls = 0;
		bool fail = false;
		NetworkIdRangeGenerator generator = new NetworkIdRangeGenerator(new NetworkIdBlock(1, 64), () =>
		{
			calls++;
			return fail ? throw new InvalidOperationException("registry unavailable") : new NetworkIdBlock((uint)(100 * calls + 1), (uint)(100 * calls + 64));
		}, () => unreachable, () => clock);
		for (int i = 0; i < 64; i++) generator.NextId();
		Assert(calls == 0, "an unreachable registry was asked for a prefetch " + calls + " time(s)");
		bool threw = false;
		try { generator.NextId(); }
		catch (System.Net.Http.HttpRequestException) { threw = true; }
		Assert(threw && calls == 0, "running out with the registry unreachable must throw at once: threw=" + threw + " calls=" + calls);
		unreachable = false;
		Assert(generator.NextId() == 101 && calls == 1, "the registry back, the same call allocates and issues the next block");
		// A failed prefetch is retried after 5 s, not on every id past 75%.
		fail = true;
		calls = 0;
		for (int i = 0; i < 55; i++) generator.NextId();
		Assert(calls == 1, "a failed prefetch was retried inside its back-off: " + calls + " attempt(s)");
		clock = 5000;
		fail = false;
		generator.NextId();
		Assert(calls == 2, "the prefetch must be retried once the back-off has passed: " + calls + " attempt(s)");
	}

	/// T-0350: while the registry is unreachable every layout is locked and new layouts are deferred, so one pass
	/// asks the allocator (a registry call on the game thread) for nothing; the first pass after it does the work.
	private static void MembershipRegistryHold()
	{
		bool hold = false;
		int calls = 0;
		ulong next = 1;
		PartitionMembership membership = new PartitionMembership(Distance, Margin,
			count => { calls++; return Enumerable.Range(0, count).Select(_ => next++).ToArray(); }, _ => hold);
		membership.Evaluate(new[] { At(1, 0), At(2, 100) }, 0);
		Assert(membership.Layouts.Count == 1 && membership.Layouts[0].Members.Length == 2, "two near members must start as one partition");
		calls = 0;
		hold = true;
		membership.DeferNewLayouts = true;
		// Member 2 drifts out of split distance, stray 3 appears far away, stray 4 appears beside member 1.
		PartitionMembership.Member[] apart = { At(1, 0), At(2, 50000), At(3, 200000), At(4, 50) };
		for (int pass = 1; pass <= 3; pass++)
		{
			PartitionMembership.Change[] held = membership.Evaluate(apart, pass * 100);
			Assert(held.Length == 0, "a held pass changed the topology");
			Assert(calls == 0, $"a held pass asked the allocator {calls} time(s)");
			Assert(membership.Layouts.Count == 1 && membership.Layouts[0].Members.SequenceEqual(new long[] { 1, 2 }),
				"a held pass rewrote the layout");
		}
		// T-0353: with the hold's adoption rule a near stray (a pilot who left the seat) joins the layout - no allocator
		// call, no topology change - so the body is in its saves and its drain.
		membership.HoldAdoptionLocked = _ => false;
		PartitionMembership.Change[] adopting = membership.Evaluate(apart, 400);
		Assert(adopting.Length == 0 && calls == 0, "adoption under a hold changed the topology or asked the allocator");
		Assert(membership.Layouts.Count == 1 && membership.Layouts[0].Members.SequenceEqual(new long[] { 1, 2, 4 }),
			"under a hold a stray within the split distance must join the nearest layout, a far one must stay unassigned");
		membership.HoldAdoptionLocked = _ => true;
		Assert(membership.Evaluate(new[] { At(1, 0), At(2, 50000), At(3, 200000), At(4, 50), At(5, 60) }, 500).Length == 0
			&& membership.Layouts[0].Members.Length == 3, "a layout locked for another reason (frozen, unowned) adopted a stray");
		membership.HoldAdoptionLocked = null;
		hold = false;
		membership.DeferNewLayouts = false;
		PartitionMembership.Change[] released = membership.Evaluate(apart, 1000);
		Assert(calls != 0 && released.Any(change => change.Split), "the released pass must split the drifted layout");
		Assert(membership.Layouts.SelectMany(layout => layout.Members).Order().SequenceEqual(new long[] { 1, 2, 3, 4 }),
			"every member, the deferred strays included, must be in a layout after the release");

		// An empty membership defers too: no layout is made for the first members while the registry is away.
		calls = 0;
		PartitionMembership empty = new PartitionMembership(Distance, Margin,
			count => { calls++; return Enumerable.Range(0, count).Select(_ => next++).ToArray(); }) { DeferNewLayouts = true };
		Assert(empty.Evaluate(new[] { At(1, 0) }, 0).Length == 0 && empty.Layouts.Count == 0 && calls == 0, "an empty membership made a layout while held");
		empty.DeferNewLayouts = false;
		empty.Evaluate(new[] { At(1, 0) }, 100);
		Assert(empty.Layouts.Count == 1, "the released empty membership must make the layout");
	}

	/// T-0158: a partition held for one pass (PartitionManager.Get) is neither merged into a neighbour,
	/// nor absorbs one, nor splits; the next unheld pass does what was deferred.
	private static void MembershipHeld()
	{
		HashSet<ulong> held = new HashSet<ulong>();
		ulong next = 1;
		PartitionMembership membership = new PartitionMembership(Distance, Margin,
			count => Enumerable.Range(0, count).Select(_ => next++).ToArray(), id => held.Contains(id));
		membership.Evaluate(new[] { At(1, 0), At(2, 50000) }, 0);
		Assert(membership.Layouts.Count == 2, "two far members must start as two partitions");
		foreach (ulong id in membership.Layouts.Select(layout => layout.Id).ToArray())
		{
			held.Clear();
			held.Add(id);
			PartitionMembership.Change[] changes = membership.Evaluate(new[] { At(1, 0), At(2, 100) }, 100);
			Assert(changes.Length == 0, $"a merge touched held partition {id}");
			Assert(membership.Layouts.Any(layout => layout.Id == id), $"held partition {id} disappeared");
		}
		held.Clear();
		PartitionMembership.Change[] merge = membership.Evaluate(new[] { At(1, 0), At(2, 100) }, 200);
		Assert(merge.Length == 1 && !merge[0].Split && membership.Layouts.Count == 1, "the unheld pass must merge");

		ulong only = membership.Layouts.Single().Id;
		held.Add(only);
		Assert(membership.Evaluate(new[] { At(1, 0), At(2, 9000) }, 300).Length == 0, "a held partition split");
		held.Clear();
		PartitionMembership.Change[] split = membership.Evaluate(new[] { At(1, 0), At(2, 9000) }, 400);
		Assert(split.Length == 1 && split[0].Split, "the unheld pass must split");
	}

	/// T-0292: a fast atomic group is split out of a shared partition, is never adopted into or
	/// merged while fast, keeps its own partition as it passes other content, and merges normally
	/// again once it is no longer fast.
	private static void MembershipFast()
	{
		PartitionMembership membership = Fresh();
		PartitionMembership.Member Fast(long id, double x, long group) =>
			new PartitionMembership.Member(id, x, 0, 0, 0, 0, group, Fast: true);
		// 1, 2 (attached, group 7) and 3 share one partition.
		membership.Evaluate(new[] { At(1, 0, 7), At(2, 10, 7), At(3, 500, 3) }, 0);
		SameSets(Partitions(membership), new[] { new long[] { 1, 2, 3 } }, "start: one partition");
		// Group 7 turns fast: it leaves as ONE partition (both attached grids), 3 stays behind.
		PartitionMembership.Change[] split = membership.Evaluate(new[] { Fast(1, 0, 7), Fast(2, 10, 7), At(3, 500, 3) }, 100);
		Assert(split.Length == 1 && split[0].Split, "a fast group sharing a partition must split out");
		SameSets(Partitions(membership), new[] { new long[] { 1, 2 }, new long[] { 3 } }, "fast group alone");
		// Passing right through 3 (1 m apart) while fast: no merge, no adoption.
		PartitionMembership.Change[] pass = membership.Evaluate(new[] { Fast(1, 500, 7), Fast(2, 501, 7), At(3, 500, 3) }, 200);
		Assert(pass.Length == 0, "a fast partition merged with content it passed");
		// A new slow member appearing next to the fast group is not adopted into it.
		membership.Evaluate(new[] { Fast(1, 500, 7), Fast(2, 501, 7), At(3, 500, 3), At(4, 502, 4) }, 300);
		Assert(membership.Layouts.Single(layout => layout.Members.Contains(1L)).Members.Length == 2,
			"a slow member was adopted into a fast partition");
		// A fast member with no partition gets its own, never an adopted place.
		membership.Evaluate(new[] { Fast(1, 500, 7), Fast(2, 501, 7), At(3, 500, 3), At(4, 502, 4), Fast(5, 503, 5) }, 400);
		Assert(membership.Layouts.Single(layout => layout.Members.Contains(5L)).Members.Length == 1,
			"a fast newcomer was adopted");
		// Slow again: normal rules resume and everything within D merges into one partition.
		for (int pass2 = 0; pass2 < 6; pass2++)
			membership.Evaluate(new[] { At(1, 500, 7), At(2, 501, 7), At(3, 500, 3), At(4, 502, 4), At(5, 503, 5) }, 500 + pass2 * 20);
		Assert(membership.Layouts.Count == 1, "after slowing down the partitions must merge again, got "
			+ membership.Layouts.Count);
	}

	/// T-0292: the fast classifier measures position over time with hysteresis and dwell - one
	/// teleport spike never enters, two consecutive fast samples do, and it leaves only after the
	/// exit dwell below the lower threshold.
	private static void FastMotionTrackerRules()
	{
		// Pinned at 1000 m/s (sync distance 3000): the steps below are absolute speeds.
		FastMotionTracker tracker = new FastMotionTracker(1000);
		List<(long, bool)> transitions = new List<(long, bool)>();
		tracker.Transition += (id, fast, speed, by) => transitions.Add((id, fast));
		double t = 0, x = 0;
		void Step(double speed) { t += 1; x += speed; tracker.Sample(new[] { new KeyValuePair<long, VRageMath.Vector3D>(1, new VRageMath.Vector3D(x, 0, 0)) }, t); }
		Step(0);
		Step(50000);   // one teleport hop (admin move, respawn): a single spike
		Step(10);
		Assert(!tracker.IsFast(1) && transitions.Count == 0, "a single spike entered the fast state");
		Step(1500);
		Assert(!tracker.IsFast(1), "one fast sample must not enter (dwell 2)");
		Step(10000);
		Assert(tracker.IsFast(1) && transitions.Count == 1 && transitions[0] == (1, true), "two fast samples must enter");
		Assert(Math.Abs(tracker.SpeedOf(1) - 10000) < 1, "speed must be displacement over time, got " + tracker.SpeedOf(1));
		Step(900);     // between exit (800) and enter (1000): stays fast, resets nothing
		for (int i = 0; i < 4; i++) Step(100);
		Assert(tracker.IsFast(1), "left fast before the exit dwell (5 samples below 800)");
		Step(100);
		Assert(!tracker.IsFast(1) && transitions.Count == 2 && transitions[1] == (1, false), "must leave after 5 slow samples");
		// Samples closer together than the minimum interval are ignored (no dt blow-up).
		tracker.Sample(new[] { new KeyValuePair<long, VRageMath.Vector3D>(1, new VRageMath.Vector3D(x + 5000, 0, 0)) }, t + 0.1);
		Assert(!tracker.IsFast(1) && tracker.SpeedOf(1) < 1000, "a sample 0.1 s later was used");
		// A member no longer present is forgotten.
		tracker.Sample(Array.Empty<KeyValuePair<long, VRageMath.Vector3D>>(), t + 2);
		Assert(tracker.SpeedOf(1) == 0, "a vanished member was kept");
		// A ship held at a 1000 m/s speed cap reads 1000 +- a little: it never enters (fast.enterMarginFraction),
		// and a reading clearly above the tolerance still does.
		FastMotionTracker capped = new FastMotionTracker(1000);
		double ct = 0, cx = 0;
		void Capped(double speed) { ct += 1; cx += speed; capped.Sample(new[] { new KeyValuePair<long, VRageMath.Vector3D>(2, new VRageMath.Vector3D(cx, 0, 0)) }, ct); }
		Capped(0);
		foreach (double reading in new[] { 1000.4, 1003, 999.7, 1006, 1002, 1009, 1001 }) Capped(reading);
		Assert(!capped.IsFast(2), "a ship at a 1000 m/s cap entered the fast state (margin 1% = 10 m/s)");
		Capped(1011); Capped(1012);
		Assert(capped.IsFast(2), "two readings above enterMps + margin must enter");
		// The hysteresis scales with the threshold: at 2000 m/s (sync distance 6000) 1500 never enters,
		// 2021 does (margin 20), and it leaves only below 1600.
		FastMotionTracker wide = new FastMotionTracker(2000);
		double wt = 0, wx = 0;
		void Wide(double speed) { wt += 1; wx += speed; wide.Sample(new[] { new KeyValuePair<long, VRageMath.Vector3D>(3, new VRageMath.Vector3D(wx, 0, 0)) }, wt); }
		Wide(0);
		foreach (double reading in new[] { 1500, 1500, 1500, 2015, 2018 }) Wide(reading);
		Assert(!wide.IsFast(3), "at T=2000 a 1500 m/s member or one inside the 20 m/s margin entered");
		Wide(2021); Wide(2030);
		Assert(wide.IsFast(3), "at T=2000 two readings above 2020 must enter");
		for (int i = 0; i < 5; i++) Wide(1700);
		Assert(wide.IsFast(3), "at T=2000 1700 m/s (above 0.8 T) must not leave");
		for (int i = 0; i < 5; i++) Wide(1500);
		Assert(!wide.IsFast(3), "at T=2000 five samples below 1600 must leave");
		// A member that arrives fast (handover) is fast from its first pass here and leaves only by the exit dwell;
		// the state rides the handover's per-entity map under the cluster's own key, next to plugin states.
		FastMotionTracker arrived = new FastMotionTracker(1000);
		arrived.Seed(4, new VRageMath.Vector3D(0, 0, 0), 100, 3000);
		Assert(arrived.IsFast(4) && arrived.SpeedOf(4) == 3000, "a seeded member is not fast");
		for (int i = 1; i <= 4; i++)
			arrived.Sample(new[] { new KeyValuePair<long, VRageMath.Vector3D>(4, new VRageMath.Vector3D(i * 3000.0, 0, 0)) }, 100 + i);
		Assert(arrived.IsFast(4), "a seeded member still moving at 3000 m/s left the fast state");
		Dictionary<long, double> carried = PluginHandoverService.FastArrivals(PluginHandoverService.Leaving(new HashSet<long>(),
			new Dictionary<long, double> { [4] = 3000.5, [-9] = 1200 }));
		Assert(carried.Count == 2 && carried[4] == 3000.5 && carried[-9] == 1200, "fast state lost in the handover map: " + string.Join(",", carried));
		Assert(PluginHandoverService.FastArrivals(null).Count == 0 && PluginHandoverService.Leaving(new HashSet<long>(), new Dictionary<long, double>()) == null,
			"an empty fast map must add nothing to the network state");
		// The derived threshold is the sync distance in use / 3 (this node's world).
		(double derived, string source) = FastMotionTracker.Threshold();
		double sync = MyLayers.GetSyncDistance();
		Assert(ClusterConfig.Get("fast.enterMps") != "auto" || sync <= 0 || Math.Abs(derived - sync / 3.0) < 1e-6,
			"derived threshold " + derived + " (" + source + ") is not SyncDistance " + sync + " / 3");
	}

	/// SE1-0084: the tick detector. Each of the last K steps decides, so a single teleport, or a teleport and a
	/// correction on the next tick, never enters; a warp enters within K ticks and leaves within K ticks of
	/// slowing below the exit speed. A hop every 3rd tick only the 0.9 s samples see, and they keep it until
	/// their own exit dwell; a member the tick detector entered leaves without waiting for that dwell, and one it
	/// stops feeding (StaleTickSeconds) leaves through the samples.
	private static void FastTickDetectorRules()
	{
		int k = FastMotionTracker.TickSteps;
		Assert(k >= 3, "these cases need fast.tickSteps >= 3, got " + k);
		FastMotionTracker tracker = null;
		List<(bool Fast, string By, long Frame)> transitions = null;
		long frame = 0;
		double x = 0;
		KeyValuePair<long, VRageMath.Vector3D>[] At() => new[] { new KeyValuePair<long, VRageMath.Vector3D>(1, new VRageMath.Vector3D(x, 0, 0)) };
		void Move(double metres) { frame++; x += metres; tracker.Step(At(), frame); }
		void Tick(double speed) => Move(speed / 60);
		void Sample() => tracker.Sample(At(), frame / 60.0);
		void Reset()
		{
			tracker = new FastMotionTracker(1000);   // enter > 1010 m/s, exit < 800 m/s
			transitions = new List<(bool, string, long)>();
			tracker.Transition += (id, fast, speed, by) => transitions.Add((fast, by, frame));
			frame = 0;
			x = 0;
			Tick(0);
		}

		Reset();
		for (int i = 0; i < 5; i++) Tick(0);
		Move(10000);   // respawn, admin move, jump drive
		for (int i = 0; i < 10; i++) Tick(0);
		Assert(transitions.Count == 0 && !tracker.IsFast(1), "a single 10 km teleport entered the fast state");

		Reset();
		Move(10000);
		Move(-9990);   // the correction on the next tick
		for (int i = 0; i < 10; i++) Tick(0);
		Assert(transitions.Count == 0 && !tracker.IsFast(1), "a teleport followed by a correction entered the fast state");

		// A warp: the same step every tick, 3000 m/s.
		Reset();
		long start = frame;
		for (int i = 0; i < 10; i++) Tick(3000);
		Assert(transitions.Count == 1 && transitions[0] == (true, "tick", start + k), "every-tick steps must enter on step " + k
			+ ", got " + string.Join(",", transitions));
		Assert(Math.Abs(tracker.SpeedOf(1) - 3000) < 1, "the speed must be the step's, got " + tracker.SpeedOf(1));
		// Ramp down by 100 m/s per tick: it leaves on the k-th step below 800 m/s. Off the exact boundary: a step
		// of 800 m/s reads 799.99 from the positions.
		long firstBelow = 0;
		for (double speed = 2950; speed >= 0; speed -= 100)
		{
			Tick(speed);
			if (speed < 800 && firstBelow == 0) firstBelow = frame;
		}
		Assert(transitions.Count == 2 && transitions[1] == (false, "tick", firstBelow + k - 1),
			"a ramp down must leave on the " + k + "th step below the exit speed (frame " + (firstBelow + k - 1) + "), got " + string.Join(",", transitions));

		// Cruising at the threshold, +-0.5 % (inside the 1 % margin): never enters. Fast and then cruising at
		// it (above the exit speed): never leaves.
		Reset();
		for (int i = 0; i < 600; i++) Tick(1000 + 5 * Math.Sin(i));
		Assert(transitions.Count == 0, "a cruise at the threshold entered: " + string.Join(",", transitions));
		for (int i = 0; i < k; i++) Tick(3000);
		for (int i = 0; i < 600; i++) Tick(1000 + 5 * Math.Sin(i));
		Assert(transitions.Count == 1 && tracker.IsFast(1), "a fast ship cruising at the threshold flapped: " + string.Join(",", transitions));

		// A 100 m hop every 3rd tick (2000 m/s on average) with a sample every second, as the membership pass
		// takes them: the tick detector never sees 3 fast steps in a row, the samples enter on their 2nd reading.
		Reset();
		Sample();
		for (int i = 1; i <= 240; i++)
		{
			if (i % 3 == 0) Move(100);
			else Tick(0);
			if (i % 60 == 0) Sample();
		}
		Assert(transitions.Count == 1 && transitions[0].Fast && transitions[0].By == "samples",
			"a hop every 3rd tick must enter through the samples, got " + string.Join(",", transitions));
		// It stops: three still ticks are not enough here (the tick detector never had it), the samples' dwell is.
		for (int i = 1; i <= 60; i++) Tick(0);
		Sample();
		Assert(tracker.IsFast(1), "a hopper the tick detector never entered left before the samples' exit dwell");
		for (int i = 1; i <= 300; i++)
		{
			Tick(0);
			if (i % 60 == 0) Sample();
		}
		Assert(!tracker.IsFast(1) && transitions.Count == 2 && !transitions[1].Fast && transitions[1].By == "samples",
			"the hopper must leave after the samples' exit dwell, got " + string.Join(",", transitions));

		// A warp with both detectors fed: both have it fast. At the stop the tick detector ends the samples'
		// state too, and the next sample (whose window would span the stop) does not bring it back.
		Reset();
		Sample();
		for (int i = 1; i <= 300; i++)
		{
			Tick(3000);
			if (i % 60 == 0) Sample();
		}
		long stop = frame;
		for (int i = 0; i < k; i++) Tick(0);
		Assert(!tracker.IsFast(1) && transitions.Count == 2 && transitions[1] == (false, "tick", stop + k),
			"a stopped warp must leave on the " + k + "th still tick, got " + string.Join(",", transitions));
		for (int i = 0; i < 60; i++) Tick(0);
		Sample();
		Assert(!tracker.IsFast(1) && transitions.Count == 2, "a sample after the stop brought the fast state back: " + string.Join(",", transitions));

		// Fast by tick, then no longer fed (a grid converted to a station, the detector switched off): the next
		// sample more than StaleTickSeconds later drops the tick state, and the slow samples leave.
		Reset();
		Sample();
		for (int i = 0; i < k; i++) Tick(3000);
		Assert(tracker.IsFast(1) && transitions.Count == 1, "the warp did not enter: " + string.Join(",", transitions));
		frame += 50;
		Sample();
		Assert(tracker.IsFast(1), "dropped the tick state of a member fed 0.83 s ago (limit " + FastMotionTracker.StaleTickSeconds + " s)");
		frame += 20;
		Sample();
		Assert(!tracker.IsFast(1) && transitions.Count == 2 && transitions[1] == (false, "samples", frame),
			"a tick-fast member no longer fed must leave through the samples, got " + string.Join(",", transitions));
	}

	/// SE1-0084: a member is reported once it has FastFlapMonitor.Threshold transitions within the window, then at most
	/// once per window while it keeps flipping; slow, spread-out transitions never are.
	private static void FastFlapMonitorRules()
	{
		FastFlapMonitor monitor = new FastFlapMonitor();
		int threshold = FastFlapMonitor.Threshold;
		double window = FastFlapMonitor.WindowSeconds;
		for (int i = 1; i < threshold; i++)
			Assert(monitor.Note(1, i * 0.1) == 0, "reported before " + threshold + " transitions, at " + i);
		Assert(monitor.Note(1, threshold * 0.1) == threshold, "not reported at " + threshold + " transitions in the window");
		Assert(monitor.Note(1, threshold * 0.1 + 0.1) == 0, "reported twice in one window");
		// Still flipping a window later: reported again, with the transitions of the last window only.
		double t = threshold * 0.1 + 0.1;
		int again = 0;
		while (again == 0 && t < 2 * window + 1) again = monitor.Note(1, t += 0.5);
		Assert(again != 0 && t >= threshold * 0.1 + window && again <= window / 0.5 + 1,
			"a member still flipping a window later was not reported again (t=" + t + ", count=" + again + ")");
		Assert(monitor.Reports == 2, "reports " + monitor.Reports + ", expected 2");
		// One transition every 10 s is a ship speeding up and slowing down, not flapping.
		for (int i = 0; i < 100; i++)
			Assert(monitor.Note(2, 1000 + i * 10.0) == 0, "a transition every 10 s was reported as flapping");
	}

	/// T-0307: a layout the node does not own (a startup-fenced stale copy) is locked for the pass. The
	/// control runs the same scenario without the ownership predicate and must show the defect: the
	/// split mutates the unowned layout and allocates (the live allocator CLAIMS) the outputs.
	private static void MembershipUnowned()
	{
		(PartitionMembership.Change[] Changes, int Allocated, PartitionMembership Membership) Run(bool fenced)
		{
			HashSet<ulong> owned = new HashSet<ulong>();
			HashSet<ulong> locked = new HashSet<ulong>();
			ulong next = 1;
			int allocated = 0;
			PartitionMembership membership = new PartitionMembership(Distance, Margin, count =>
			{
				ulong[] ids = Enumerable.Range(0, count).Select(_ => next++).ToArray();
				allocated += count;
				owned.UnionWith(ids);   // the live allocator claims every id it hands out
				return ids;
			}, id => locked.Contains(id));
			membership.Evaluate(new[] { At(1, 0), At(2, 100), At(3, 50000) }, 0);
			Assert(membership.Layouts.Count == 2, "start: {1,2} and {3}");
			ulong stale = membership.Layouts.Single(layout => layout.Members.Contains(1)).Id;
			if (fenced) owned.Remove(stale);   // startup claim refused: loaded, not owned
			allocated = 0;
			locked = PartitionManager.LockedLayouts(membership.Layouts, _ => false, false, null, fenced ? owned.Contains : null);
			// 1 and 2 drift beyond D+2m (a split), and a stray 4 appears next to 1 (an adoption).
			PartitionMembership.Change[] changes = membership.Evaluate(new[] { At(1, 0), At(2, 9000), At(3, 50000), At(4, 50) }, 100);
			return (changes, allocated, membership);
		}

		var control = Run(fenced: false);
		// T-0339: the largest output of a split keeps the layout's id, so the two-way split allocates one id, not two.
		Assert(control.Changes.Any(change => change.Split) && control.Allocated >= 1,
			"control: without the ownership predicate the split must mutate and allocate (the T-0307 defect)");

		var fenced = Run(fenced: true);
		Assert(fenced.Changes.Length == 0, "an unowned layout was split or merged");
		SameSets(Partitions(fenced.Membership).Where(set => set.Contains(1)).ToArray(), new[] { new long[] { 1, 2 } },
			"the unowned layout keeps exactly its members (no split, no adoption of the stray)");
		Assert(fenced.Allocated == 1, "only the stray gets a new (owned) id; nothing is allocated for the unowned layout");

		// The live wiring: this node's lock reads its registry ownership (false for an unowned id).
		string wiring = ClusterControlServer.TopologyOwnershipWiring(out int checkedOwned);
		Assert(wiring == null, "topology lock wiring: " + wiring);
		VRage.Utils.MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] membership-unowned: live topology lock wired, "
			+ checkedOwned + " owned partition(s) checked");
	}

	/// T-0309: a partition close closes a grid's non-block hierarchy children (a seated pilot) itself, but
	/// never a block or a subpart one by one - WeaponCore throws when a turret subpart closes under a live block.
	private static void CloseAttachedChildren()
	{
		Assert(PartitionPersistence.ClosesWithItsGrid(new VRage.Game.Entity.MyEntitySubpart()), "a subpart must be left to its grid");
		Assert(PartitionPersistence.ClosesWithItsGrid(new Sandbox.Game.Entities.MyCubeBlock()), "a block must be left to its grid");
		Assert(!PartitionPersistence.ClosesWithItsGrid(new VRage.Game.Entity.MyEntity()), "any other child (a seated pilot) is closed with the sweep");
	}

	/// P1-SAV-02: container property tests - header parse, round trip, and corruption rejection.
	/// T-0150: a change the registry refuses is reverted in memory. A split reverted puts the members back
	/// into the registered input; a merge reverted restores both inputs; a pass of several changes reverted
	/// last first restores the layouts exactly as they stood before the pass.
	private static void MembershipRevert()
	{
		PartitionMembership membership = Fresh();
		membership.Evaluate(new[] { At(1, 0), At(2, 100), At(3, 200) }, 0);
		long[][] whole = Partitions(membership);
		PartitionMembership.Change[] split = membership.Evaluate(new[] { At(1, 0), At(2, 100), At(3, 9000) }, 100);
		Assert(split.Length == 1 && split[0].Split && split[0].InputLayouts.Length == 1, "a split must carry its input layout");
		membership.Revert(split[0]);
		SameSets(Partitions(membership), whole, "a reverted split");
		Assert(membership.Layouts.Single().Id == 1, "a reverted split must restore the input's id");

		membership.Evaluate(new[] { At(1, 0), At(2, 100), At(3, 9000) }, 200);
		long[][] apart = Partitions(membership);
		PartitionMembership.Change[] merge = membership.Evaluate(new[] { At(1, 0), At(2, 100), At(3, 2000) }, 300);
		Assert(merge.Length == 1 && !merge[0].Split && merge[0].InputLayouts.Length == 2, "a merge must carry both input layouts");
		membership.Revert(merge[0]);
		SameSets(Partitions(membership), apart, "a reverted merge");

		PartitionMembership chained = Fresh();
		chained.Evaluate(new[] { At(1, 0), At(2, 100), At(3, 200), At(4, 50000), At(5, 50100) }, 0);
		long[][] prePass = Partitions(chained);
		PartitionMembership.Member[] moved = new[] { At(1, 0), At(2, 100), At(3, 20000), At(4, 50000), At(5, 90000) };
		PartitionMembership.Change[] pass = chained.Evaluate(moved, 100);
		Assert(pass.Length == 2, $"both layouts should split in one pass, got {pass.Length} changes");
		for (int index = pass.Length - 1; index >= 0; index--) chained.Revert(pass[index]);
		SameSets(Partitions(chained), prePass, "a pass reverted last first");
	}

	private static void SaveContainer()
	{
		byte[] body = Enumerable.Range(0, 4096).Select(value => (byte)(value * 31)).ToArray();
		BoundingSphereD sphere = new BoundingSphereD(new Vector3D(1, -2, 3), 4242);
		byte[] file = PartitionSaveFile.Write(0xABCDEF01, 7, 3, 2, sphere, true, 9, body, 123456789);

		PartitionSaveFile.Parsed parsed = PartitionSaveFile.Read(file);
		Assert(parsed.PartitionId == 0xABCDEF01, "partition id did not survive the round trip");
		Assert(parsed.Generation == 7 && parsed.Sequence == 3 && parsed.Kind == 2, "envelope fields did not survive");
		Assert(parsed.Anchored && parsed.EntityCount == 9 && parsed.ClusterTime == 123456789, "header fields did not survive");
		Assert(parsed.Sphere.Center == sphere.Center && Math.Abs(parsed.Sphere.Radius - sphere.Radius) < 1e-9,
			"the sphere did not survive the round trip");
		Assert(parsed.Body.SequenceEqual(body), "the body did not survive the round trip");

		// T-0147: the pooled-scratch writer must round-trip every compression branch - LZ4 (kind 1),
		// zstd (any other kind) and the stored-as-is fallback an incompressible body takes - on a
		// body larger than the 85 KB large-object threshold the change is about.
		Random random = new Random(147);
		byte[] noise = new byte[200_000];
		random.NextBytes(noise);
		byte[] periodic = Enumerable.Range(0, 200_000).Select(value => (byte)(value * 31)).ToArray();
		foreach ((byte testKind, byte[] testBody, string label) in new[] { ((byte)1, periodic, "lz4"), ((byte)2, periodic, "zstd"),
			((byte)1, noise, "lz4-incompressible"), ((byte)2, noise, "zstd-incompressible") })
		{
			byte[] written = PartitionSaveFile.Write(5, 1, 1, testKind, sphere, false, 3, testBody, 1);
			PartitionSaveFile.Parsed back = PartitionSaveFile.Read(written);
			Assert(back.Body.SequenceEqual(testBody), $"{label}: the body did not survive the round trip");
			Assert(back.Kind == testKind && back.EntityCount == 3, $"{label}: envelope fields did not survive");
			Assert(label.EndsWith("incompressible") ? written.Length > testBody.Length : written.Length < testBody.Length / 4,
				$"{label}: unexpected stored size {written.Length} for a {testBody.Length} byte body");
			// ReadHeader: same envelope and checksum, no body; still rejects a corrupted stored body.
			// The trailer checksum is over the STORED bytes, so it equals BodyChecksum(body) only
			// where the body is stored as-is (the incompressible fallback) - the 2026-09-15 w32
			// self-test failure was this assertion asked of the compressed branches.
			PartitionSaveFile.Parsed head = PartitionSaveFile.ReadHeader(written);
			Assert(head.Body == null && head.BodyChecksum == back.BodyChecksum && head.Kind == testKind && head.EntityCount == 3,
				$"{label}: ReadHeader disagrees with Read");
			Assert(label.EndsWith("incompressible") == (head.BodyChecksum == PartitionSaveFile.BodyChecksum(testBody)),
				$"{label}: the trailer checksum must cover the stored bytes (equal to the body's only when stored as-is)");
			byte[] damaged = (byte[])written.Clone();
			damaged[written.Length / 2] ^= 0xFF;
			AssertThrows(() => PartitionSaveFile.ReadHeader(damaged), $"{label}: ReadHeader accepted a corrupted body");
		}

		PartitionSaveFile.ValidateEnvelope(parsed, 0xABCDEF01, 7, 3);
		AssertThrows(() => PartitionSaveFile.ValidateEnvelope(parsed, 0xABCDEF02, 7, 3), "a wrong partition id was accepted");
		AssertThrows(() => PartitionSaveFile.ValidateEnvelope(parsed, 0xABCDEF01, 8, 3), "a wrong generation was accepted");
		AssertThrows(() => PartitionSaveFile.ValidateEnvelope(parsed, 0xABCDEF01, 7, 4), "a wrong sequence was accepted");

		byte[] flipped = (byte[])file.Clone();
		flipped[^1] ^= 0xFF;
		AssertThrows(() => PartitionSaveFile.Read(flipped), "a corrupted body was accepted");

		byte[] header = (byte[])file.Clone();
		header[0] ^= 0xFF;
		AssertThrows(() => PartitionSaveFile.Read(header), "a corrupted magic/header was accepted");

		AssertThrows(() => PartitionSaveFile.Read(file.Take(file.Length / 2).ToArray()), "a truncated file was accepted");
		AssertThrows(() => PartitionSaveFile.Read(Array.Empty<byte>()), "an empty file was accepted");

		// An empty body is legal and must still round-trip.
		PartitionSaveFile.Parsed empty = PartitionSaveFile.Read(
			PartitionSaveFile.Write(1, 1, 1, 0, new BoundingSphereD(Vector3D.Zero, 1), false, 0, Array.Empty<byte>(), 0));
		Assert(empty.EntityCount == 0 && empty.Body.Length == 0, "an empty body did not round-trip");
	}

	/// P3-SUN-01's pure half (WorldAuthority.md §8.2): delta semantics never rebase and never move
	/// the clock backward, the same value is a zero delta (no loopback), and the persisted
	/// SunTimeOffset + last slider value survive the global-save section round trip.
	private static void TimeOfDay()
	{
		const double period = 120;
		Assert(TimeOfDayService.DeltaMilliseconds(0, 30, period) == 30 * 60000L, "a forward set is a plain delta");
		Assert(TimeOfDayService.DeltaMilliseconds(30, 30, period) == 0, "the same value must be a zero delta (no loopback)");
		// Backward by 30 minutes on a 120-minute sun: served 90 minutes forward - same angle, monotonic clock.
		Assert(TimeOfDayService.DeltaMilliseconds(60, 30, period) == 90 * 60000L, "a backward set is served forward");
		// Backward by exactly one rotation lands on a zero delta.
		Assert(TimeOfDayService.DeltaMilliseconds(120, 0, period) == 0, "a whole-rotation step back is a no-op");
		Assert(TimeOfDayService.DeltaMilliseconds(0, 30, period) >= 0
			&& TimeOfDayService.DeltaMilliseconds(119, 1, period) == 2 * 60000L, "delta wraps by whole rotations");

		string record = System.Text.Json.JsonSerializer.Serialize(new TimeOfDayService.TimeOfDaySnapshot(42.5f, 3_600_000));
		var parsed = System.Text.Json.JsonSerializer.Deserialize<TimeOfDayService.TimeOfDaySnapshot>(record);
		Assert(parsed.TimeOfDay == 42.5f && parsed.OffsetMilliseconds == 3_600_000, "the persisted record round-trips");
	}

	// -- assertions --------------------------------------------------------

	/// <summary>
	/// T-0153: the round-trip gate's per-node normalisations, pinned against the XML shape of the
	/// handover-diff dumps they came from. A frame stamp differing across nodes is no residual;
	/// the state next to it still is.
	/// </summary>
	/// T-0263: a disposed HkWorld forgets its released batch buffers, and a batch call on it writes nothing
	/// (the vanilla path wrote the body pointer into the freed buffer: two node SIGSEGVs).
	private static void HavokDisposedWorld()
	{
		// Havok allocates through a per-thread memory router that only the game and physics threads have:
		// the control server's HTTP thread crashed in HkWorld's constructor (bench 2026-09-29 12:32).
		Exception failure = null;
		using var done = new System.Threading.ManualResetEventSlim();
		Sandbox.MySandboxGame.Static.Invoke(() =>
		{
			try { HavokDisposedWorldOnGameThread(); }
			catch (Exception error) { failure = error; }
			finally { done.Set(); }
		}, "self-test havok-disposed-world");
		Assert(done.Wait(TimeSpan.FromSeconds(30)), "the game thread did not run the section within 30 s");
		if (failure != null) throw failure;
	}

	private static void HavokDisposedWorldOnGameThread()
	{
		Havok.HkWorld world = new Havok.HkWorld(false, 100f, 1f, false, 4);
		(IntPtr add, IntPtr remove) = HavokDisposedWorldGuard.BatchPointers(world);
		Assert(add != IntPtr.Zero && remove != IntPtr.Zero, "a new world has no batch buffers (the fixture is wrong)");
		world.Dispose();
		(add, remove) = HavokDisposedWorldGuard.BatchPointers(world);
		Assert(add == IntPtr.Zero && remove == IntPtr.Zero,
			"a disposed world kept its released batch buffers: add=0x" + add.ToString("x") + " remove=0x" + remove.ToString("x"));
		long before = HavokDisposedWorldGuard.Skipped;
		world.RemoveRigidBodyBatch(null);
		world.AddRigidBodyBatch(null);
		world.FinishBatch();
		(add, remove) = HavokDisposedWorldGuard.BatchPointers(world);
		Assert(HavokDisposedWorldGuard.Skipped == before + 2 && add == IntPtr.Zero && remove == IntPtr.Zero,
			"batch calls on a disposed world were not skipped (skipped " + (HavokDisposedWorldGuard.Skipped - before) + ")");
	}

	private static void RoundTripNormalize()
	{
		// T-0361: a Final captured after the drain freeze carries the velocity read before it.
		MyObjectBuilder_CubeGrid moving = new();
		PartitionPersistence.ApplyFrozenVelocity(moving, new VRageMath.Vector3(0, -104, 0), new VRageMath.Vector3(0, 0, 1));
		Assert(((VRageMath.Vector3)moving.LinearVelocity).Y == -104f && ((VRageMath.Vector3)moving.AngularVelocity).Z == 1f,
			"a frozen member's builder must carry the velocity it had before the freeze");
		// T-0363: the capture writes the capacity Init restores; a value inside the range is never touched.
		Assert(ImportRoundTrip.ClampCapacity(100000.27f, 100000f) == 100000f && ImportRoundTrip.ClampCapacity(-1f, 100000f) == 0f,
			"an overfull or negative fuel capacity must be captured as Init restores it");
		Assert(ImportRoundTrip.ClampCapacity(4000f, 100000f) == 4000f && ImportRoundTrip.ClampCapacity(5f, float.PositiveInfinity) == 5f,
			"a capacity inside its range, or of a block without a maximum, must be captured unchanged");
		const string shape = "<MyObjectBuilder_Sector xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><SectorObjects>"
			+ "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_OffensiveCombatBlock\"><EntityId>2</EntityId>"
			+ "<ComponentContainer><Components><ComponentData><TypeId>MyOffensiveCombatComponent</TypeId>"
			+ "<Component xsi:type=\"MyObjectBuilder_OffensiveCombatHitAndRun\"><RetreatTimeout>30</RetreatTimeout>"
			+ "<HitAndRunState><CurrentState>{0}</CurrentState><RunAwayDirection><X>0</X><Y>0</Y><Z>0</Z></RunAwayDirection>"
			+ "<LastPosition xsi:nil=\"true\" /><RunAwayStartedFrame>{1}</RunAwayStartedFrame></HitAndRunState>"
			+ "</Component></ComponentData></Components></ComponentContainer></MyObjectBuilder_CubeBlock>"
			+ "</CubeBlocks></MyObjectBuilder_EntityBase></SectorObjects></MyObjectBuilder_Sector>";
		string source = string.Format(shape, "None", "-8420675"), target = string.Format(shape, "None", "-8436099");
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(source, target);
		Assert(residuals.Count == 0, "RunAwayStartedFrame (a per-node frame stamp) was reported as a residual: " + string.Join("; ", residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(source, string.Format(shape, "RunningAway", "-8436099"));
		Assert(residuals.Count == 1 && residuals[0].Contains("HitAndRunState/CurrentState"),
			"the hit-and-run STATE must stay validated, got: " + string.Join("; ", residuals));
		// The rule is scoped to HitAndRunState: a same-named element elsewhere is still compared.
		residuals = ImportRoundTrip.ResidualsOfXml("<A><RunAwayStartedFrame>1</RunAwayStartedFrame></A>", "<A><RunAwayStartedFrame>2</RunAwayStartedFrame></A>");
		Assert(residuals.Count == 1, "RunAwayStartedFrame outside HitAndRunState was normalised away");
		// SE1-0114: a safe zone block's countdowns are relative to each node's game clock; the import
		// drift (117 ms on the bench) is tolerated, a reset countdown is not.
		const string zone = "<MyObjectBuilder_Sector xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><SectorObjects>"
			+ "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_SafeZoneBlock\"><EntityId>2</EntityId>"
			+ "<ComponentContainer><Components><ComponentData><TypeId>MySafeZoneComponent</TypeId>"
			+ "<Component xsi:type=\"MyObjectBuilder_SafeZoneComponent\"><UpkeepTime>{0}</UpkeepTime>"
			+ "<Activating>false</Activating><ActivationTime>{0}</ActivationTime>"
			+ "</Component></ComponentData></Components></ComponentContainer></MyObjectBuilder_CubeBlock>"
			+ "</CubeBlocks></MyObjectBuilder_EntityBase></SectorObjects></MyObjectBuilder_Sector>";
		residuals = ImportRoundTrip.ResidualsOfXml(string.Format(zone, "-32922997"), string.Format(zone, "-32923114"));
		Assert(residuals.Count == 0, "a safe zone countdown 117 ms apart was reported as a residual: " + string.Join("; ", residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(string.Format(zone, "3600000"), string.Format(zone, "0"));
		Assert(residuals.Count == 2, "a reset safe zone countdown must stay a residual, got: " + string.Join("; ", residuals));
		// T-0290: mod storage keyed by the base type on the source, by its own type after the import
		// (handover-diff 215809, w33 2026-09-29). The key is one spelling; the stored data stays validated.
		const string storage = "<MyObjectBuilder_Sector xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><SectorObjects>"
			+ "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><ComponentContainer><Components>"
			+ "<ComponentData><TypeId>{0}</TypeId><Component xsi:type=\"MyObjectBuilder_ModStorageComponent\"><Storage><dictionary><item>"
			+ "<Key>29ffd684-13d7-4045-bf76-cd48bf80e36a</Key><Value>{1}</Value></item></dictionary></Storage></Component></ComponentData>"
			+ "</Components></ComponentContainer></MyObjectBuilder_EntityBase></SectorObjects></MyObjectBuilder_Sector>";
		residuals = ImportRoundTrip.ResidualsOfXml(string.Format(storage, "MyModStorageComponentBase", "CAAQ"), string.Format(storage, "MyModStorageComponent", "CAAQ"));
		Assert(residuals.Count == 0, "the mod storage component's key (base vs own type) was reported: " + string.Join("; ", residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(string.Format(storage, "MyModStorageComponentBase", "CAAQ"), string.Format(storage, "MyModStorageComponent", "CAAR"));
		Assert(residuals.Count == 1 && residuals[0].Contains("Storage"), "the mod storage DATA must stay validated, got: " + string.Join("; ", residuals));
		RoundTripNormalizePilotedSmallGrid();
		RoundTripNormalizeProjectedGrids();
		RoundTripNormalizeLoadDerivedLeftovers();
	}

	/// <summary>
	/// #1253 (octahedron-1191, 2026-10-20): the residual classes left after #1216 and #1245 - a loaded meteor's
	/// basis carries its Init scale, a seated pilot's breath saws the seat's oxygen level, MES stamps its NPC record
	/// with the writing node's clock, and a zero-amount stockpile item vanishes on load. Each is checked with a
	/// control that must still fail.
	/// </summary>
	private static void RoundTripNormalizeLoadDerivedLeftovers()
	{
		const string head = "<MyObjectBuilder_Sector xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><SectorObjects>";
		const string tail = "</SectorObjects></MyObjectBuilder_Sector>";
		string Report(List<string> found) => string.Join("; ", found);

		// A meteor's Forward/Up are as long as its Init scale; the direction stays compared.
		string Body(string type, string forward, string up) => head + "<MyObjectBuilder_EntityBase xsi:type=\"" + type + "\"><EntityId>1</EntityId>"
			+ "<PositionAndOrientation><Position x=\"1\" y=\"2\" z=\"3\" /><Forward " + forward + " /><Up " + up + " />"
			+ "<Orientation><X>0</X><Y>0</Y><Z>0</Z><W>1</W></Orientation></PositionAndOrientation></MyObjectBuilder_EntityBase>" + tail;
		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Body("MyObjectBuilder_Meteor", "x=\"0\" y=\"0\" z=\"-1\"", "x=\"0\" y=\"1\" z=\"0\""),
			Body("MyObjectBuilder_Meteor", "x=\"0\" y=\"0\" z=\"-1.3051158\"", "x=\"0\" y=\"1.3051158\" z=\"0\""));
		Assert(residuals.Count == 0, "a loaded meteor's scaled basis was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Body("MyObjectBuilder_Meteor", "x=\"0\" y=\"0\" z=\"-1\"", "x=\"0\" y=\"1\" z=\"0\""),
			Body("MyObjectBuilder_Meteor", "x=\"1.3\" y=\"0\" z=\"0\"", "x=\"0\" y=\"1.3\" z=\"0\""));
		Assert(residuals.Count == 2 && residuals.TrueForAll(entry => entry.Contains("/Forward/")), "a meteor's DIRECTION must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Body("MyObjectBuilder_CubeGrid", "x=\"0\" y=\"0\" z=\"-1\"", "x=\"0\" y=\"1\" z=\"0\""),
			Body("MyObjectBuilder_CubeGrid", "x=\"0\" y=\"0\" z=\"-1.3\"", "x=\"0\" y=\"1.3\" z=\"0\""));
		Assert(residuals.Count == 2, "a GRID's basis length must stay validated, got: " + Report(residuals));

		// A seat's fill level within a breath or two; the seated pilot's reading of it is derived.
		string Seat(string level, string pilotLevel, string freeLevel) => head + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Cockpit\"><SubtypeName>SmallBlockCockpit</SubtypeName><EntityId>2</EntityId>"
			+ "<Pilot><EntityId>9</EntityId><EnvironmentOxygenLevel>" + pilotLevel + "</EnvironmentOxygenLevel></Pilot>"
			+ "<OxygenLevel>" + level + "</OxygenLevel></MyObjectBuilder_CubeBlock></CubeBlocks></MyObjectBuilder_EntityBase>"
			+ "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_Character\"><EntityId>8</EntityId><EnvironmentOxygenLevel>" + freeLevel
			+ "</EnvironmentOxygenLevel></MyObjectBuilder_EntityBase>" + tail;
		residuals = ImportRoundTrip.ResidualsOfXml(Seat("1", "1", "1"), Seat("0.99895", "0.99895", "1"));
		Assert(residuals.Count == 0, "a seat's one-breath oxygen step was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Seat("1", "1", "1"), Seat("0.5", "0.5", "1"));
		Assert(residuals.Count == 1 && residuals[0].Contains("/OxygenLevel"), "a seat's LOST oxygen must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Seat("1", "1", "1"), Seat("1", "1", "0.99895"));
		Assert(residuals.Count == 1 && residuals[0].Contains("EnvironmentOxygenLevel"), "a FREE character's oxygen reading must stay validated, got: " + Report(residuals));

		// MES's NpcData: only the LastChangeToData stamp (field 25) leaves the comparison.
		string Record(string name, byte stamp) => Convert.ToBase64String(new byte[] { 0x22, 0x01, (byte)name[0], 0xCA, 0x01, 0x04, 0x08, stamp, 0x10, 0x05 });
		string Storage(string key, string value) => head + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><ComponentContainer><Components>"
			+ "<ComponentData><TypeId>MyModStorageComponent</TypeId><Component xsi:type=\"MyObjectBuilder_ModStorageComponent\"><Storage><dictionary><item>"
			+ "<Key>" + key + "</Key><Value>" + value + "</Value></item></dictionary></Storage></Component></ComponentData>"
			+ "</Components></ComponentContainer></MyObjectBuilder_EntityBase>" + tail;
		const string npcData = "29ffd684-13d7-4045-bf76-cd48bf80e36a";
		residuals = ImportRoundTrip.ResidualsOfXml(Storage(npcData, Record("A", 2)), Storage(npcData, Record("A", 4)));
		Assert(residuals.Count == 0, "MES's LastChangeToData stamp was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Storage(npcData, Record("A", 2)), Storage(npcData, Record("B", 2)));
		Assert(residuals.Count == 1 && residuals[0].Contains("Storage"), "the rest of MES's NPC record must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Storage("ad4dbd09-359d-48f5-9f48-54d352b59171", Record("A", 2)), Storage("ad4dbd09-359d-48f5-9f48-54d352b59171", Record("A", 4)));
		Assert(residuals.Count == 1 && residuals[0].Contains("Storage"), "another storage key's data must stay validated byte for byte, got: " + Report(residuals));

		// A zero-amount stockpile item does not survive a load; a real stockpile does.
		string Item(string subtype, string amount) => "<Items xsi:type=\"MyObjectBuilder_StockpileItem\"><SubtypeName /><Amount>" + amount
			+ "</Amount><PhysicalContent xsi:type=\"MyObjectBuilder_Component\"><SubtypeName>" + subtype + "</SubtypeName></PhysicalContent></Items>";
		string Block(string stockpile) => head + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_CameraBlock\"><SubtypeName>SmallCameraBlock</SubtypeName><EntityId>2</EntityId>"
			+ (stockpile == null ? "" : "<ConstructionStockpile><SubtypeName />" + stockpile + "</ConstructionStockpile>")
			+ "<CustomName>Camera 2</CustomName></MyObjectBuilder_CubeBlock></CubeBlocks></MyObjectBuilder_EntityBase>" + tail;
		residuals = ImportRoundTrip.ResidualsOfXml(Block(Item("Scrap", "0")), Block(null));
		Assert(residuals.Count == 0, "an empty construction stockpile item was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Block(Item("Scrap", "0") + Item("SteelPlate", "3")), Block(Item("SteelPlate", "3")));
		Assert(residuals.Count == 0, "a zero-amount item next to a real one was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Block(Item("SteelPlate", "3")), Block(null));
		Assert(residuals.Exists(entry => entry.Contains("ConstructionStockpile")), "a stockpile holding components must stay validated, got: " + Report(residuals));

		// #1382: a block whose skin has a DefaultColor takes the skin's colour at its next UpdateVisual.
		string Painted(string skin, string hsv) => head + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Door\"><SubtypeName>SmallDoor</SubtypeName><EntityId>2</EntityId><Min x=\"0\" y=\"0\" z=\"0\" />"
			+ "<ColorMaskHSV " + hsv + " />" + (skin == null ? "" : "<SkinSubtypeId>" + skin + "</SkinSubtypeId>")
			+ "</MyObjectBuilder_CubeBlock></CubeBlocks></MyObjectBuilder_EntityBase>" + tail;
		const string spawned = "x=\"0.275\" y=\"-0.8\" z=\"-0.45\"", golden = "x=\"0.14583334\" y=\"0.69411767\" z=\"-0.33333334\"";
		residuals = ImportRoundTrip.ResidualsOfXml(Painted("Golden_Armor", spawned), Painted("Golden_Armor", golden));
		Assert(residuals.Count == 0, "a Golden_Armor block taking its skin's default colour was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Painted("Silver_Armor", spawned), Painted("Silver_Armor", "x=\"0.2777778\" y=\"-0.95588237\" z=\"0.06666672\""));
		Assert(residuals.Count == 0, "a Silver_Armor block taking its skin's default colour was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Painted(null, spawned), Painted(null, golden));
		Assert(residuals.Count == 3 && residuals.TrueForAll(entry => entry.Contains("ColorMaskHSV")), "an unskinned block's colour must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Painted("Rusty_Armor", spawned), Painted("Rusty_Armor", golden));
		Assert(residuals.Count == 3 && residuals.TrueForAll(entry => entry.Contains("ColorMaskHSV")), "a block with a skin without a default colour must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Painted("Golden_Armor", spawned), Painted("Rusty_Armor", spawned));
		Assert(residuals.Exists(entry => entry.Contains("SkinSubtypeId")), "a lost skin must stay validated, got: " + Report(residuals));
	}

	/// <summary>
	/// #1245 (octahedron-1191, 2026-10-20): the server projector remaps its blueprint to fresh entity ids
	/// whenever it re-registers, so an imported projector's ProjectedGrids came back as one grid missing and
	/// one extra on 194 partitions. The ids under a projection compare by position; what the blueprint
	/// holds, and which of its blocks each reference points at, stays compared.
	/// </summary>
	private static void RoundTripNormalizeProjectedGrids()
	{
		// Projector 72057594000000010 on real grid ...001 projects grid G with blocks B1 (a timer whose toolbar
		// slot names B2), B2 (a connector docked to an entity outside the blueprint) and B3; B1 and B3 form a
		// multiblock. The vanilla remap rewrites every one of those ids, the Names and the group id.
		string Projection(string g, string b1, string b2, string b3, string slot, string docked, string group,
			string subtype = "LargeBlockArmorBlock", bool third = true, string realGrid = "72057594000000001")
			=> "<MyObjectBuilder_Sector xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><SectorObjects>"
			+ "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>" + realGrid + "</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Projector\"><SubtypeName>LargeProjector</SubtypeName><EntityId>72057594000000010</EntityId>"
			+ "<ProjectedGrids><MyObjectBuilder_CubeGrid><SubtypeName /><EntityId>" + g + "</EntityId><Name>" + g + "</Name><GridSizeEnum>Large</GridSizeEnum><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_TimerBlock\"><SubtypeName>TimerBlockLarge</SubtypeName><EntityId>" + b1 + "</EntityId>"
			+ "<MultiBlockId>" + group + "</MultiBlockId><Toolbar><Slots><Slot><Index>0</Index><Data xsi:type=\"MyObjectBuilder_ToolbarItemTerminalBlock\">"
			+ "<BlockEntityId>" + slot + "</BlockEntityId><_Action>OnOff</_Action></Data></Slot></Slots></Toolbar></MyObjectBuilder_CubeBlock>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_ShipConnector\"><SubtypeName>Connector</SubtypeName><EntityId>" + b2 + "</EntityId>"
			+ "<Name>" + b2 + "</Name><ConnectedEntityId>" + docked + "</ConnectedEntityId></MyObjectBuilder_CubeBlock>"
			+ (third ? "<MyObjectBuilder_CubeBlock><SubtypeName>" + subtype + "</SubtypeName><EntityId>" + b3 + "</EntityId><MultiBlockId>" + group + "</MultiBlockId>"
				+ "<Min x=\"0\" y=\"0\" z=\"2\" /></MyObjectBuilder_CubeBlock>" : "")
			+ "</CubeBlocks></MyObjectBuilder_CubeGrid></ProjectedGrids></MyObjectBuilder_CubeBlock>"
			+ "</CubeBlocks></MyObjectBuilder_EntityBase></SectorObjects></MyObjectBuilder_Sector>";
		const string g = "72057594100000001", b1 = "72057594100000002", b2 = "72057594100000003", b3 = "72057594100000004";
		const string g2 = "72057594200000001", c1 = "72057594200000002", c2 = "72057594200000003", c3 = "72057594200000004";
		string source = Projection(g, b1, b2, b3, b2, "72057594100000099", "1849302");
		string Report(List<string> found) => string.Join("; ", found);

		List<string> residuals = ImportRoundTrip.ResidualsOfXml(source, Projection(g2, c1, c2, c3, c2, "72057594200000099", "40213"));
		Assert(residuals.Count == 0, "a projection remapped to new entity ids was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(source, Projection(g2, c1, c2, c3, c3, "72057594200000099", "40213"));
		Assert(residuals.Count == 1 && residuals[0].Contains("BlockEntityId"), "a toolbar slot that names ANOTHER block of the blueprint must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(source, Projection(g2, c1, c2, c3, c2, "72057594200000099", "40213", subtype: "LargeHeavyBlockArmorBlock"));
		Assert(residuals.Count == 1 && residuals[0].Contains("SubtypeName"), "the blueprint's blocks must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(source, Projection(g2, c1, c2, c3, c2, "72057594200000099", "40213", third: false));
		Assert(residuals.Count > 0 && residuals.Any(entry => entry.Contains("ProjectedGrids")), "a block lost from the blueprint must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(source, Projection(g2, c1, c2, c3, c2, "0", "40213"));
		Assert(residuals.Count == 1 && residuals[0].Contains("ConnectedEntityId"), "a docked blueprint connector that comes back undocked must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(source, Projection(g, b1, b2, b3, b2, "72057594100000099", "1849302", realGrid: "72057594000000002"));
		Assert(residuals.Count == 2 && residuals.All(entry => !entry.Contains("ProjectedGrids")), "the projector's own grid id must stay validated, got: " + Report(residuals));
	}

	/// <summary>
	/// #1216 (octahedron-1191, 2026-10-20): a piloted Visitor Ion's raw dump pair differs in the cockpit
	/// LCD's animation stamp, the unconnected connectors' master flag and the seated pilot's pose, and the
	/// partitions it shared with NPC ships and Unknown Signal pods were HandoverBlocked by three classes the
	/// comparison did not yet know: a mod-rewritten autopilot speed limit, a slideshow's clock-derived
	/// slide and an Emotion Controller's output image. Each erase is checked with a control that must
	/// still fail.
	/// </summary>
	private static void RoundTripNormalizePilotedSmallGrid()
	{
		const string head = "<MyObjectBuilder_Sector xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><SectorObjects>";
		const string tail = "</SectorObjects></MyObjectBuilder_Sector>";
		string Pose(string x) => "<PositionAndOrientation><Position x=\"" + x + "\" y=\"20\" z=\"30\" /><Forward x=\"0\" y=\"0\" z=\"-1\" />"
			+ "<Up x=\"0\" y=\"1\" z=\"0\" /><Orientation><X>0</X><Y>0</Y><Z>0</Z><W>1</W></Orientation></PositionAndOrientation>";
		string Character(string element, string attributes, string x) => "<" + element + attributes + "><EntityId>9</EntityId>" + Pose(x)
			+ "<CharacterModel>Default_Astronaut</CharacterModel><MovementState>Sitting</MovementState></" + element + ">";
		string Grid(string stamp, string master, string pilotX, string connected = "") => "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\">"
			+ "<EntityId>1</EntityId><GridSizeEnum>Small</GridSizeEnum><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Cockpit\"><SubtypeName>SmallBlockCockpit</SubtypeName><EntityId>2</EntityId>"
			+ "<ComponentContainer><Components><ComponentData><TypeId>MyMultiTextPanelComponent</TypeId>"
			+ "<Component xsi:type=\"MyObjectBuilder_MultiTextPanelComponent\"><TextPanelsContents><MySerializedTextPanelData>"
			+ "<ChangeInterval>0</ChangeInterval><CurrentShownTexture>0</CurrentShownTexture><UpdateStartOfFirstTexture>" + stamp + "</UpdateStartOfFirstTexture>"
			+ "</MySerializedTextPanelData></TextPanelsContents></Component></ComponentData>"
			+ "<ComponentData><TypeId>MyHierarchyComponentBase</TypeId><Component xsi:type=\"MyObjectBuilder_HierarchyComponentBase\"><Children>"
			+ Character("MyObjectBuilder_EntityBase", " xsi:type=\"MyObjectBuilder_Character\"", pilotX)
			+ "</Children></Component></ComponentData></Components></ComponentContainer>"
			+ Character("Pilot", "", pilotX) + "</MyObjectBuilder_CubeBlock>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_ShipConnector\"><SubtypeName>ConnectorMedium</SubtypeName><EntityId>3</EntityId>"
			+ connected + "<IsMaster>" + master + "</IsMaster></MyObjectBuilder_CubeBlock>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_ShipConnector\"><SubtypeName>ConnectorMedium</SubtypeName><EntityId>4</EntityId>"
			+ "<IsMaster>" + master + "</IsMaster></MyObjectBuilder_CubeBlock>"
			+ "</CubeBlocks></MyObjectBuilder_EntityBase>";
		string Report(List<string> found) => string.Join("; ", found);

		List<string> residuals = ImportRoundTrip.ResidualsOfXml(head + Grid("1130055", "true", "10.123456") + tail,
			head + Grid("1748007", "false", "10.123498") + tail);
		Assert(residuals.Count == 0, "a seated pilot's pose, an unconnected connector's IsMaster or an LCD's animation stamp was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(head + Grid("1", "true", "10", "<ConnectedEntityId>7</ConnectedEntityId>") + tail,
			head + Grid("1", "false", "10", "<ConnectedEntityId>7</ConnectedEntityId>") + tail);
		Assert(residuals.Count == 1 && residuals[0].Contains("IsMaster"), "a CONNECTED connector's master flag must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(head + Character("MyObjectBuilder_EntityBase", " xsi:type=\"MyObjectBuilder_Character\"", "10") + tail,
			head + Character("MyObjectBuilder_EntityBase", " xsi:type=\"MyObjectBuilder_Character\"", "11") + tail);
		Assert(residuals.Count == 1 && residuals[0].Contains("PositionAndOrientation/Position"), "a FREE character's pose must stay validated, got: " + Report(residuals));

		// A slideshow's slide is re-derived from this node's clock; a static panel's is what Init restores.
		string Panel(string interval, string shown, string image) => head + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_TextPanel\"><EntityId>2</EntityId><ComponentContainer><Components><ComponentData>"
			+ "<TypeId>MyLcdSurfaceComponent</TypeId><Component xsi:type=\"MyObjectBuilder_LcdSurfaceComponent\"><TextPanelContent>"
			+ "<ChangeInterval>" + interval + "</ChangeInterval><SelectedImages><string>LCD_SoF_Exploration</string><string>" + image + "</string></SelectedImages>"
			+ "<CurrentShownTexture>" + shown + "</CurrentShownTexture></TextPanelContent></Component></ComponentData></Components></ComponentContainer>"
			+ "</MyObjectBuilder_CubeBlock></CubeBlocks></MyObjectBuilder_EntityBase>" + tail;
		residuals = ImportRoundTrip.ResidualsOfXml(Panel("1", "0", "Danger"), Panel("1", "1", "Danger"));
		Assert(residuals.Count == 0, "a cycling panel's current slide was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Panel("0", "0", "Danger"), Panel("0", "1", "Danger"));
		Assert(residuals.Count == 1 && residuals[0].Contains("CurrentShownTexture"), "a STATIC panel's shown image must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Panel("1", "0", "Danger"), Panel("1", "0", "Arrow"));
		Assert(residuals.Count == 1 && residuals[0].Contains("SelectedImages"), "a cycling panel's IMAGES must stay validated, got: " + Report(residuals));

		// An Emotion Controller's panel image follows its emotion; the emotion stays compared.
		string Emotion(string emotion, string image, string other) => head + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_EmotionControllerBlock\"><EntityId>2</EntityId><ComponentContainer><Components>"
			+ "<ComponentData><TypeId>MyEmotionControllerComponent</TypeId><Component xsi:type=\"MyObjectBuilder_EmotionControllerComponent\">"
			+ "<SelectedLCDsIds><long>2</long></SelectedLCDsIds><SelectedEmotionSubtype>" + emotion + "</SelectedEmotionSubtype></Component></ComponentData>"
			+ "<ComponentData><TypeId>MyMultiTextPanelComponent</TypeId><Component xsi:type=\"MyObjectBuilder_MultiTextPanelComponent\"><TextPanelsContents>"
			+ "<MySerializedTextPanelData><SelectedImages><string>" + image + "</string></SelectedImages></MySerializedTextPanelData></TextPanelsContents>"
			+ "</Component></ComponentData></Components></ComponentContainer></MyObjectBuilder_CubeBlock>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_TextPanel\"><EntityId>3</EntityId><ComponentContainer><Components><ComponentData>"
			+ "<TypeId>MyLcdSurfaceComponent</TypeId><Component xsi:type=\"MyObjectBuilder_LcdSurfaceComponent\"><TextPanelContent>"
			+ "<SelectedImages><string>" + other + "</string></SelectedImages></TextPanelContent></Component></ComponentData></Components></ComponentContainer>"
			+ "</MyObjectBuilder_CubeBlock></CubeBlocks></MyObjectBuilder_EntityBase>" + tail;
		residuals = ImportRoundTrip.ResidualsOfXml(Emotion("LCD_Emote_Neutral", "LCD_Emote_Neutral", "Danger"), Emotion("LCD_Emote_Neutral", "Online", "Danger"));
		Assert(residuals.Count == 0, "an Emotion Controller's output image was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Emotion("LCD_Emote_Neutral", "LCD_Emote_Neutral", "Danger"), Emotion("LCD_Emote_Happy", "Online", "Danger"));
		Assert(residuals.Count == 1 && residuals[0].Contains("SelectedEmotionSubtype"), "the controller's EMOTION must stay validated, got: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Emotion("LCD_Emote_Neutral", "LCD_Emote_Neutral", "Danger"), Emotion("LCD_Emote_Neutral", "LCD_Emote_Neutral", "Arrow"));
		Assert(residuals.Count == 1 && residuals[0].Contains("SelectedImages"), "an LCD the controller does NOT drive must stay validated, got: " + Report(residuals));

		// A mod may rewrite the autopilot speed limit after Init; the rest of the autopilot stays compared.
		string Autopilot(string enabled, string limit) => head + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_RemoteControl\"><SubtypeName>RivalAIRemoteControlLarge</SubtypeName><EntityId>2</EntityId>"
			+ "<ComponentContainer><Components><ComponentData><TypeId>MyAutopilotComponent</TypeId><Component xsi:type=\"MyObjectBuilder_AutopilotComponent\">"
			+ "<AutoPilotEnabled>" + enabled + "</AutoPilotEnabled><FlightMode>2</FlightMode><AutopilotSpeedLimit>" + limit + "</AutopilotSpeedLimit>"
			+ "</Component></ComponentData></Components></ComponentContainer><IsUpdatedSave>true</IsUpdatedSave></MyObjectBuilder_CubeBlock>"
			+ "</CubeBlocks></MyObjectBuilder_EntityBase>" + tail;
		residuals = ImportRoundTrip.ResidualsOfXml(Autopilot("true", "25"), Autopilot("true", "100"));
		Assert(residuals.Count == 0, "a mod-rewritten autopilot speed limit was reported: " + Report(residuals));
		residuals = ImportRoundTrip.ResidualsOfXml(Autopilot("true", "25"), Autopilot("false", "100"));
		Assert(residuals.Count == 1 && residuals[0].Contains("AutoPilotEnabled"), "the autopilot's STATE must stay validated, got: " + Report(residuals));
	}

	// -- antenna graph delta protocol (T-0154) -----------------------------

	/// <summary>
	/// The wire protocol between the antenna reports/publishes and their receivers is pure data:
	/// a delta applied to the sender's base must reproduce the sender's set and its digest, a delta
	/// applied to any other base must be caught by the digest, and the digest must not depend on
	/// order or on Origin (the authority rewrites it). Builders are opaque strings to the codec, so
	/// no engine is needed here.
	/// </summary>
	private static void AntennaDelta()
	{
		AntennaGraphEntry Entry(long id, string builder, string origin = "node-1", long grid = 0)
			=> new AntennaGraphEntry(origin, id, builder, grid);
		AntennaGraphEntry[] before = { Entry(1, "AAA", grid: 10), Entry(2, "BBB", grid: 10), Entry(3, "CCC", grid: 30) };
		AntennaGraphEntry[] after = { Entry(1, "AAA", grid: 10), Entry(2, "BBB-moved", grid: 10), Entry(4, "DDD", grid: 40) };

		// Digest: order-independent, Origin-independent, content-sensitive.
		long digest = AntennaGraphCodec.Digest(after);
		Assert(digest == AntennaGraphCodec.Digest(after.Reverse()), "antenna digest depends on order");
		Assert(digest == AntennaGraphCodec.Digest(after.Select(x => x with { Origin = "wa-1" })), "antenna digest depends on Origin");
		Assert(digest != AntennaGraphCodec.Digest(before), "antenna digest blind to content");
		Assert(digest != AntennaGraphCodec.Digest(after.Select(x => x.AntennaEntityId == 4 ? x with { GridEntityId = 41 } : x)),
			"antenna digest blind to GridEntityId");
		Assert(AntennaGraphCodec.Digest(Array.Empty<AntennaGraphEntry>()) == 0, "empty set digest is not zero");

		// Full message round trip carries revision and digest.
		AntennaGraphMessage full = AntennaGraphCodec.ReadMessage(AntennaGraphCodec.WriteFull(after, 7));
		Assert(full.IsFull && full.Revision == 7 && full.Digest == digest && full.Antennas.Length == 3, "full antenna message round trip");
		Assert(AntennaGraphCodec.Read(AntennaGraphCodec.Write(before)).Antennas.Length == 3, "legacy full read");

		// Delta from before to after: 2 changed/new (2 moved, 4 new), 1 removed (3).
		AntennaGraphEntry[] upserts = { after[1], after[2] };
		string wire = AntennaGraphCodec.WriteDelta(upserts, new[] { 3L }, 6, 7, digest);
		AntennaGraphMessage delta = AntennaGraphCodec.ReadMessage(wire);
		Assert(!delta.IsFull && delta.Base == 6 && delta.Revision == 7 && delta.Upserts.Length == 2 && delta.Removed.Length == 1,
			"delta antenna message round trip");
		AntennaGraphEntry[] applied = AntennaGraphCodec.ApplyDelta(before, delta);
		Assert(applied.SequenceEqual(after.OrderBy(x => x.AntennaEntityId)), "delta apply does not reproduce the sender's set");
		// The authority stamps Origin while applying; the digest must still match.
		AntennaGraphEntry[] stamped = AntennaGraphCodec.ApplyDelta(before, delta, x => x with { Origin = "node-9" });
		Assert(stamped.Count(x => x.Origin == "node-9") == 2, "delta apply map not applied");
		// Removing an id the base never had is a no-op, and the digest still matches.
		string wireUnknownRemoval = AntennaGraphCodec.WriteDelta(upserts, new[] { 3L, 99L }, 6, 7, digest);
		Assert(AntennaGraphCodec.ApplyDelta(before, AntennaGraphCodec.ReadMessage(wireUnknownRemoval)).SequenceEqual(applied),
			"unknown removal is not a no-op");
		// Wrong base: the same delta on a base that already moved antenna 1 must be caught.
		AntennaGraphEntry[] drifted = { Entry(1, "AAA-elsewhere", grid: 10), before[1], before[2] };
		AssertThrows(() => AntennaGraphCodec.ApplyDelta(drifted, delta), "delta digest mismatch not detected");
		// Malformed deltas are refused at the codec.
		AssertThrows(() => AntennaGraphCodec.ReadMessage(AntennaGraphCodec.WriteDelta(upserts, new[] { 2L }, 6, 7, digest)),
			"upsert and removal of the same id accepted");
		AssertThrows(() => AntennaGraphCodec.ReadMessage(AntennaGraphCodec.WriteDelta(upserts, new long[0], 7, 7, digest)),
			"delta with revision <= base accepted");
		AssertThrows(() => AntennaGraphCodec.ApplyDelta(before, full), "full message accepted as a delta");
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}

	private static void AssertThrows(Action action, string message)
	{
		try
		{
			action();
		}
		catch (Exception)
		{
			return;
		}
		throw new InvalidOperationException(message);
	}
}
